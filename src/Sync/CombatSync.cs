using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Combat.Projectiles;
using Gameplay.Units.Equipment;
using Gameplay.Units.Interfaces;
using Gameplay.Units.Virtual;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Kampf-Optik beim Gast: Schüsse (Mündungsfeuer, Leuchtspur, Ton) und Geschosse (Granaten, Mörser) des Hosts
    /// werden nachgespielt. Schaden entsteht dabei keiner: beim Gast ist jedes ReceiveDamage gesperrt, die
    /// Lebenspunkte kommen aus dem Schnappschuss.
    ///
    /// Host: sammelt Schüsse sichtbarer Gruppen und schickt sie 5-mal pro Sekunde gebündelt; Geschosse sofort.
    /// </summary>
    internal sealed class CombatSync
    {
        private const int MaxShotsPerBatch = 400;

        private static readonly AccessTools.FieldRef<CharacterFightHandler, ICharacter> AttackerField =
            AccessTools.FieldRefAccess<CharacterFightHandler, ICharacter>("_character");

        private static readonly AccessTools.FieldRef<CharacterFightHandler, Weapon> LastWeaponField =
            AccessTools.FieldRefAccess<CharacterFightHandler, Weapon>("_lastUsedWeapon");

        private static readonly MethodInfo SelectWeaponMethod =
            AccessTools.Method(typeof(CharacterFightHandler), "SelectWeaponToUseByReach", new[] { typeof(Vector3), typeof(Weapon) });

        private struct Shot
        {
            public string AttackerGroup;
            public string AttackerId;
            public string TargetGroup;   // leer, wenn das Ziel keine Figur ist
            public string TargetId;
            public Vector3 Point;
        }

        private readonly Session _session;
        private readonly UnitSync _units;
        private readonly List<Shot> _pending = new List<Shot>();
        private float _nextFlush;
        private Dictionary<string, ProjectileDraft> _drafts;

        public int ShotsPlayed { get; private set; }
        public int ProjectilesPlayed { get; private set; }

        public CombatSync(Session session, UnitSync units)
        {
            _session = session;
            _units = units;
        }

        private bool HostWithGuests => _session.Role == Role.Host && _session.ActiveGuestCount > 0;

        public void Reset()
        {
            _pending.Clear();
        }

        // ================================================================ Host

        /// <summary>Postfix von CharacterFightHandler.Attack (Treffer-Ablauf), nur wenn angegriffen wurde.</summary>
        public void OnAttack(CharacterFightHandler handler, IAttackable opponent, Vector3 attackPoint)
        {
            if (!HostWithGuests || _pending.Count >= MaxShotsPerBatch)
                return;
            var attacker = AttackerField(handler) as Character;
            if (attacker == null || !(attacker.IGroup is Group group) || !group.IsVisible())
                return;
            // Geschoss-Angriffe kommen über OnProjectileLaunched, nicht als Schuss.
            var weapon = LastWeaponField(handler);
            if (weapon != null && weapon.UseProjectileAmmo)
                return;
            var shot = new Shot
            {
                AttackerGroup = group.Id,
                AttackerId = attacker.Id ?? string.Empty,
                TargetGroup = string.Empty,
                TargetId = string.Empty,
                Point = attackPoint,
            };
            if (opponent is Character target && target.IGroup is Group targetGroup)
            {
                shot.TargetGroup = targetGroup.Id;
                shot.TargetId = target.Id ?? string.Empty;
            }
            _pending.Add(shot);
        }

        public void HostUpdate()
        {
            if (_pending.Count == 0)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextFlush)
                return;
            _nextFlush = now + 0.2f;
            if (!HostWithGuests)
            {
                _pending.Clear();
                return;
            }
            var batch = new List<Shot>(_pending);
            _pending.Clear();
            _session.BroadcastToGuests(Protocol.Build(Msg.ShotBatch, w =>
            {
                w.Write(batch.Count);
                foreach (var s in batch)
                {
                    w.Write(s.AttackerGroup);
                    w.Write(s.AttackerId);
                    w.Write(s.TargetGroup);
                    w.Write(s.TargetId);
                    w.Write(s.Point.x);
                    w.Write(s.Point.y);
                    w.Write(s.Point.z);
                }
            }));
        }

        /// <summary>Postfix von ProjectilesController.LaunchProjectile(Draft, Start, Ziel, Applier).</summary>
        public void OnProjectileLaunched(ProjectileDraft draft, Vector3 start, Vector3 end, IAreaDamageApplier applier)
        {
            if (!HostWithGuests || draft == null)
                return;
            string group = string.Empty, id = string.Empty;
            if (applier is CharacterDamageAreaApplier cda && cda.Character is Character c && c.IGroup is Group g)
            {
                group = g.Id;
                id = c.Id ?? string.Empty;
            }
            _session.BroadcastToGuests(Protocol.Build(Msg.ProjectileLaunch, w =>
            {
                w.Write(draft.name);
                w.Write(start.x); w.Write(start.y); w.Write(start.z);
                w.Write(end.x); w.Write(end.y); w.Write(end.z);
                w.Write(group);
                w.Write(id);
            }));
        }

        // ================================================================ Gast

        public void GuestOnShots(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                return;
            for (int i = 0; i < n; i++)
            {
                string attackerGroup = r.ReadString();
                string attackerId = r.ReadString();
                string targetGroup = r.ReadString();
                string targetId = r.ReadString();
                var point = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                var attacker = _units.ResolveCharacter(attackerGroup, attackerId);
                if (attacker == null || attacker.CharacterAttackFeedback == null)
                    continue;
                IAttackable target = string.IsNullOrEmpty(targetGroup) ? null : _units.ResolveCharacter(targetGroup, targetId);
                if (target == null)
                    continue; // Feedback braucht ein Ziel; Schüsse auf Gebäude/Fahrzeuge zeigt Stufe 2 noch nicht

                try
                {
                    var weapon = SelectWeapon(attacker, point);
                    if (weapon == null)
                        continue;
                    attacker.Movement.UpdateRotationToTarget(point);
                    attacker.CharacterAttackFeedback.Play(target, point, weapon);
                    ShotsPlayed++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug("Schuss nachspielen: " + e.Message);
                }
            }
        }

        public void GuestOnProjectile(BinaryReader r)
        {
            string draftName = r.ReadString();
            var start = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var end = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            string group = r.ReadString();
            string id = r.ReadString();

            var draft = FindDraft(draftName);
            var attacker = _units.ResolveCharacter(group, id);
            var controller = GameAccess.Game<ProjectilesController>();
            if (draft == null || attacker == null || controller == null)
                return;

            UnitSync.NetApplying = true;
            try
            {
                // Ohne Verursacher scheitert der Einschlag; Schaden richtet er beim Gast nicht an (ReceiveDamage gesperrt).
                controller.LaunchProjectile(draft, start, end, new CharacterDamageAreaApplier(attacker));
                attacker.CharacterAttackFeedback?.PlayLaunchProjectileFeedback(attacker.Weapon);
                ProjectilesPlayed++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Geschoss nachspielen: " + e.Message);
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private static Weapon SelectWeapon(Character attacker, Vector3 point)
        {
            if (SelectWeaponMethod != null && attacker.FightModule != null)
            {
                try
                {
                    return SelectWeaponMethod.Invoke(attacker.FightModule, new object[] { point, null }) as Weapon;
                }
                catch (Exception)
                {
                    // fällt auf die Hauptwaffe zurück
                }
            }
            return attacker.Weapon;
        }

        private ProjectileDraft FindDraft(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            if (_drafts == null || !_drafts.ContainsKey(name))
            {
                _drafts = new Dictionary<string, ProjectileDraft>();
                foreach (var d in Resources.FindObjectsOfTypeAll<ProjectileDraft>())
                    if (d != null && !_drafts.ContainsKey(d.name)) _drafts[d.name] = d;
            }
            return _drafts.TryGetValue(name, out var draft) ? draft : null;
        }
    }
}
