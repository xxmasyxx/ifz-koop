using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gameplay.Buildings;
using Gameplay.GameResources;
using Gameplay.Rebuilding;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Player.Workers;
using Gameplay.Units.Skills;
using IfzCoop.Game;
using IfzCoop.Net;
using Newtonsoft.Json;
using UnityEngine;
using GameJson = Utilities.JsonSerializer;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Eigene Trupps und Leute über Rechner hinweg.
    ///
    /// - Ausbildung und Auffüllen, die der Gast anklickt, gehen als Auftrag an den Host
    ///   (SquadCreatorController.AddCreationRequestAtStructure / AddReplenishRequestAtStructure).
    /// - Stellt der Host einen Trupp auf, wandelt der Gast dieselben Arbeiter (gleiche Figuren-ID) in
    ///   Soldaten um und baut den Trupp mit der Gruppen-ID des Hosts. Fehlen ihm Arbeiter, entstehen sie neu.
    /// - Auffüllen: neue Mitglieder im Schnappschuss werden beim Gast aus Arbeitern übernommen (AdoptWorker),
    ///   wer den Trupp verlässt, wird Arbeiter (ReleaseToWorker), aufgelöste Trupps werden aufgelöst.
    /// - Stirbt beim Host einer der eigenen Leute (Soldat oder Arbeiter), stirbt er gezielt auch beim Gast.
    /// </summary>
    internal sealed class SquadSync
    {
        private readonly Session _session;
        private readonly UnitSync _units;
        private Dictionary<SkillId, StaticSkillInfo> _skills;

        public SquadSync(Session session, UnitSync units)
        {
            _session = session;
            _units = units;
        }

        private bool HostWithGuests => _session.Role == Role.Host && _session.ActiveGuestCount > 0;

        // ================================================================ Gast: Aufträge an den Host

        /// <summary>Prefix von SquadCreatorController.AddCreationRequestAtStructure beim Gast. false = nicht lokal.</summary>
        public bool ForwardCreation(List<StaticSkillInfo> skills, ResourceID weapon, ResourceID gear, Structure structure, bool duringTraining)
        {
            if (!_units.PuppetMode || UnitSync.NetApplying)
                return true;
            if (!(structure is Building building))
            {
                _session.ShowNotice("Ausbildung hier im Koop noch nicht möglich (nur in Gebäuden)");
                return false;
            }
            var ids = skills?.Where(s => s != null).Select(s => (int)s.SkillId).ToList() ?? new List<int>();
            _session.SendToHost(Protocol.Build(Msg.SquadRequest, w =>
            {
                w.Write((byte)0);
                w.Write(building.ID);
                w.Write(ids.Count);
                foreach (var id in ids) w.Write(id);
                w.Write((int)weapon);
                w.Write((int)gear);
                w.Write(duringTraining);
                w.Write(string.Empty);
            }));
            _session.ShowNotice("Ausbildung beim Host beauftragt");
            return false;
        }

        /// <summary>Prefix von SquadCreatorController.AddReplenishRequestAtStructure beim Gast.</summary>
        public bool ForwardReplenish(Group squad, Structure structure)
        {
            if (!_units.PuppetMode || UnitSync.NetApplying)
                return true;
            if (!(structure is Building building) || squad == null)
            {
                _session.ShowNotice("Auffüllen hier im Koop noch nicht möglich (nur in Gebäuden)");
                return false;
            }
            _session.SendToHost(Protocol.Build(Msg.SquadRequest, w =>
            {
                w.Write((byte)1);
                w.Write(building.ID);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(false);
                w.Write(squad.Id);
            }));
            _session.ShowNotice("Auffüllen beim Host beauftragt");
            return false;
        }

        /// <summary>Prefix von SquadsCreator.CreateSquadFromRequest beim Gast: Trupps entstehen nur beim Host.</summary>
        public bool AllowLocalSquadCreation() => !_units.PuppetMode || UnitSync.NetApplying;

        // ================================================================ Host: Aufträge ausführen

        public void HostOnRequest(Peer from, BinaryReader r)
        {
            byte kind = r.ReadByte();
            long buildingId = r.ReadInt64();
            int n = r.ReadInt32();
            if (n < 0 || n > 64)
                return;
            var skillIds = new List<int>();
            for (int i = 0; i < n; i++) skillIds.Add(r.ReadInt32());
            var weapon = (ResourceID)r.ReadInt32();
            var gear = (ResourceID)r.ReadInt32();
            bool duringTraining = r.ReadBoolean();
            string squadId = r.ReadString();

            var building = GameAccess.Game<Controllers.BuildingsController>()?.GetBuildingById(buildingId);
            var creator = GameAccess.Game<SquadCreatorController>();
            if (building == null || creator == null)
            {
                _session.SendTo(from.Id, Protocol.Build(Msg.Notice, w => w.Write("Host findet das Gebäude für den Auftrag nicht")));
                return;
            }

            try
            {
                if (kind == 0)
                {
                    var skills = skillIds.Select(id => Skill((SkillId)id)).Where(s => s != null).ToList();
                    StatsSync.As(from.Id.m_SteamID, () => creator.AddCreationRequestAtStructure(skills, weapon, gear, building, duringTraining));
                    Plugin.Log.LogInfo($"IFZCOOP Ausbildung für {from.Name} in {buildingId}");
                }
                else
                {
                    var squad = UnitSync.FindGroupPublic(squadId);
                    if (squad != null)
                        creator.AddReplenishRequestAtStructure(squad, building);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Trupp-Auftrag ausführen: " + e.Message);
            }
        }

        private StaticSkillInfo Skill(SkillId id)
        {
            if (_skills == null || !_skills.ContainsKey(id))
            {
                _skills = new Dictionary<SkillId, StaticSkillInfo>();
                foreach (var s in Resources.FindObjectsOfTypeAll<StaticSkillInfo>())
                    if (s != null && !_skills.ContainsKey(s.SkillId)) _skills[s.SkillId] = s;
            }
            return _skills.TryGetValue(id, out var skill) ? skill : null;
        }

        // ================================================================ Host: neue Trupps und Tode melden

        public void OnPlayerSquadBuilt(Group group)
        {
            if (!HostWithGuests || group == null)
                return;
            var spawn = BuildSquadSpawn(group);
            if (spawn == null)
                return;
            _session.BroadcastToGuests(spawn);
            Plugin.Log.LogInfo($"IFZCOOP neuer Trupp {group.Id} gemeldet");
        }

        /// <summary>
        /// Host: Gast kennt einen Trupp aus dem Schnappschuss nicht (entstand, während er lud, z. B. die
        /// Start-Trupps nach der ersten HQ-Wahl). Gleiche Nachricht wie bei einem neuen Trupp.
        /// </summary>
        public void SendSquadTo(Peer peer, Group group)
        {
            var spawn = BuildSquadSpawn(group);
            if (spawn == null)
                return;
            _session.SendTo(peer.Id, spawn);
            Plugin.Log.LogInfo($"IFZCOOP Trupp {group.Id} an {peer.Name} nachgereicht");
        }

        private static byte[] BuildSquadSpawn(Group group)
        {
            if (group == null)
                return null;
            try
            {
                string json = JsonConvert.SerializeObject(new GroupSerializedInfo(group), GameJson.DefaultSettings);
                return Protocol.Build(Msg.SquadSpawn, w => w.Write(json));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Trupp {group.Id} lässt sich nicht übertragen: {e.Message}");
                return null;
            }
        }

        /// <summary>Postfix von Character.DestroyCharacter beim Host: eigene Leute (Soldaten und Arbeiter).</summary>
        public void OnCharacterDestroyed(Character c, bool silent)
        {
            if (!HostWithGuests || silent || c == null || string.IsNullOrEmpty(c.Id))
                return;
            if (c.Fraction != Fraction.Player && !c.IsWorker)
                return;
            _session.BroadcastToGuests(Protocol.Build(Msg.CharacterDied, w => w.Write(c.Id)));
        }

        // ================================================================ Gast: anwenden

        public void GuestOnSquadSpawn(BinaryReader r)
        {
            string json = r.ReadString();
            GroupSerializedInfo info;
            try
            {
                info = JsonConvert.DeserializeObject<GroupSerializedInfo>(json, GameJson.DefaultSettings);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Neuer Trupp nicht lesbar: " + e.Message);
                return;
            }
            if (info == null || string.IsNullOrEmpty(info.Id) || UnitSync.FindGroupPublic(info.Id) != null)
                return;

            var builder = GameAccess.Game<GroupBuilder>();
            var converter = GameAccess.Game<CharactersConverter>();
            var workers = GameAccess.Game<WorkersController>();
            var draft = JutsuGamesConfig.Runtime.Configs.GetConfig<Gameplay.Units.Enemy.GroupsConfig>()?.GetDraftByName(info.GroupDraftName);
            if (builder == null || converter == null || workers == null || draft == null)
            {
                Plugin.Log.LogWarning("Neuer Trupp kann hier nicht gebaut werden");
                return;
            }

            var members = info.GetMembers() ?? new List<Serialization.Data.Citizens.CharacterSerializedInfo>();
            var found = new List<Character>();
            var missing = new List<Serialization.Data.Citizens.CharacterSerializedInfo>();
            foreach (var m in members)
            {
                var worker = workers.FindWorker(m.CharacterInfo?.CharacterId);
                if (worker != null && !found.Contains(worker)) found.Add(worker);
                else missing.Add(m);
            }

            UnitSync.NetApplying = true;
            try
            {
                foreach (var worker in found)
                {
                    converter.ConvertToSoldier(worker);
                    var m = members.First(x => x.CharacterInfo?.CharacterId == worker.Id);
                    ApplyItems(worker, m.CharacterInfo.ItemsInfo);
                    try { worker.Deserialize(m); } catch (Exception) { /* Fähigkeiten sind Kür */ }
                }

                builder.Clear()
                    .SetSerializedInfo(info)
                    .SetCharactersToUse(found)
                    .SetCitizenInfos(missing)
                    .SetCharactersCountToCreate(missing.Count)
                    .SetPosition(info.Position)
                    .SetName(info.GroupName)
                    .SetUniqueNamingNumber(info.UniqueNamingNumber);
                var squad = builder.Build(draft);
                Plugin.Log.LogInfo($"IFZCOOP Trupp {info.Id} übernommen: {found.Count} aus Arbeitern, {missing.Count} neu");
                if (squad != null)
                    _session.ShowNotice($"Neuer Trupp: {squad.GroupName}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Neuen Trupp bauen: " + e);
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        /// <summary>Schnappschuss zeigt ein neues Trupp-Mitglied: beim Gast denselben Arbeiter übernehmen.</summary>
        public Character AdoptWorker(Group squad, string characterId)
        {
            var workers = GameAccess.Game<WorkersController>();
            var converter = GameAccess.Game<CharactersConverter>();
            var worker = workers?.FindWorker(characterId);
            if (worker == null || converter == null)
                return null;
            UnitSync.NetApplying = true;
            try
            {
                converter.ConvertToSoldier(worker);
                worker.AssignGroup(squad, silent: false);
                return worker;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Arbeiter in Trupp übernehmen: " + e.Message);
                return null;
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        /// <summary>Mitglied hat den Trupp beim Host verlassen: beim Gast wieder Arbeiter.</summary>
        public void ReleaseToWorker(Character c)
        {
            var converter = GameAccess.Game<CharactersConverter>();
            if (converter == null || c == null)
                return;
            UnitSync.NetApplying = true;
            try
            {
                converter.ConvertToWorker(c);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Zurück zum Arbeiter: " + e.Message);
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        public void GuestOnCharacterDied(BinaryReader r)
        {
            string id = r.ReadString();
            if (string.IsNullOrEmpty(id))
                return;
            Character c = GameAccess.Game<WorkersController>()?.FindWorker(id)
                          ?? GameAccess.Game<SquadsController>()?.FindSquadMember(id);
            if (c == null || c.Health == null || c.Health.IsDead())
                return;
            _units.Forget(c);
            UnitSync.NetApplying = true;
            try
            {
                c.Kill(SubtractionHpReason.Enemy);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Tod übernehmen: " + e.Message);
            }
            finally
            {
                UnitSync.NetApplying = false;
            }
        }

        private static void ApplyItems(Character soldier, ItemsContainer.ItemsContainerInfo items)
        {
            try
            {
                if (items.Weapon != ResourceID.None)
                {
                    soldier.ItemsContainer.WeaponHandler.TryAssignWeapon(items.Weapon);
                    soldier.CharacterTextureRandomizer.SetWeaponAppearance(items.Weapon);
                    var weapon = soldier.ItemsContainer.WeaponHandler.Weapon;
                    if (weapon != null && !weapon.IsMelee && items.Ammo > 0f)
                        soldier.ItemsContainer.AmmunitionHandler.AddAmmo(items.Ammo);
                }
                if (items.Gear != ResourceID.None)
                    soldier.ItemsContainer.GearHandler.AssignGear(items.Gear, items.GearQuantity);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Ausrüstung übernehmen: " + e.Message);
            }
        }
    }
}
