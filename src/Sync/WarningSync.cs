using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UI.WarningsInfo;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Gelbe Warnungen oben rechts (Fehlende Impfstoffe, volle Lager, Hunger, Stimmung, Munition …).
    /// Das Spiel rechnet sie aus eigenen Daten (Krankheit, Verbrauch, Lager), die beim Gast nicht oder anders laufen
    /// (Bericht 2026-11-08: Gast sah sie nicht). Der Host schickt seinen Stand (Art, aktiv, Stufe, Zusatztext); beim Gast
    /// wirken nur diese, seine eigenen Berechnungen erreichen die Anzeige nicht mehr.
    /// </summary>
    internal sealed class WarningSync
    {
        private const float SendInterval = 1f;
        private const float ForceInterval = 10f;

        private static WarningSync _instance;
        private static bool _applying;

        private readonly Session _session;
        private float _nextSend, _nextForce, _nextReapply;
        private string _sentSig;
        private List<(int type, bool active, int level, string text)> _last;
        private WarningsInfoCanvas _canvas;
        private float _nextFind;

        public int Sent { get; private set; }
        public int Applied { get; private set; }

        private static readonly AccessTools.FieldRef<WarningsInfoCanvas, List<WarningInfo>> WarningsF =
            AccessTools.FieldRefAccess<WarningsInfoCanvas, List<WarningInfo>>("_warnings");

        private static readonly System.Reflection.MethodInfo StateM = AccessTools.DeclaredMethod(typeof(WarningsInfoCanvas), "OnWarningStateChanged");
        private static readonly System.Reflection.MethodInfo LevelM = AccessTools.DeclaredMethod(typeof(WarningsInfoCanvas), "OnWarningLevelChanged");
        private static readonly System.Reflection.MethodInfo TextM = AccessTools.DeclaredMethod(typeof(WarningsInfoCanvas), "OnTooltipTextChanged");

        public WarningSync(Session session)
        {
            _session = session;
            _instance = this;
        }

        public void ResendAll()
        {
            _sentSig = null;
            _nextSend = 0f;
        }

        public void Reset()
        {
            ResendAll();
            _last = null;
            _canvas = null;
        }

        private WarningsInfoCanvas Canvas()
        {
            if (_canvas == null && Time.realtimeSinceStartup >= _nextFind)
            {
                _nextFind = Time.realtimeSinceStartup + 2f;
                _canvas = UnityEngine.Object.FindObjectOfType<WarningsInfoCanvas>();
            }
            return _canvas;
        }

        // ================================================================ Host

        public void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || _session.ActiveGuestCount == 0 || !GameAccess.IsGameStarted)
                return;
            _nextSend = now + SendInterval;
            var c = Canvas();
            var list = c != null ? WarningsF(c) : null;
            if (list == null)
                return;
            var state = new List<(int, bool, int, string)>();
            foreach (var w in list)
                if (w != null)
                    state.Add(((int)w.WarningInfoType, w.IsActive, w.CurrentLevel, w.AdditionalTooltipText ?? ""));
            var sb = new StringBuilder();
            foreach (var s in state) sb.Append(s.Item1).Append(s.Item2 ? '+' : '-').Append(s.Item3).Append(s.Item4).Append('|');
            string sig = sb.ToString();
            bool force = now >= _nextForce;
            if (!force && sig == _sentSig)
                return;
            if (force) _nextForce = now + ForceInterval;
            _sentSig = sig;
            _session.BroadcastToGuests(Protocol.Build(Msg.WarningState, wr =>
            {
                wr.Write((byte)state.Count);
                foreach (var s in state)
                {
                    wr.Write((byte)s.Item1);
                    wr.Write(s.Item2);
                    wr.Write((byte)Mathf.Clamp(s.Item3, 0, 255));
                    wr.Write(s.Item4);
                }
            }));
            Sent++;
        }

        // ================================================================ Gast

        public void GuestOnState(BinaryReader r)
        {
            int n = r.ReadByte();
            var list = new List<(int, bool, int, string)>(n);
            for (int i = 0; i < n; i++)
                list.Add((r.ReadByte(), r.ReadBoolean(), r.ReadByte(), r.ReadString()));
            _last = list;
            Apply();
        }

        public void GuestFrame()
        {
            if (_last == null || !_session.GuestControlsLocked)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextReapply)
                return;
            _nextReapply = now + 2f;
            Apply();
        }

        private void Apply()
        {
            var c = Canvas();
            var mine = c != null ? WarningsF(c) : null;
            if (mine == null || _last == null)
                return;
            _applying = true;
            try
            {
                foreach (var e in _last)
                {
                    WarningInfo w = null;
                    foreach (var x in mine) if (x != null && (int)x.WarningInfoType == e.Item1) { w = x; break; }
                    if (w == null)
                        continue;
                    var wt = Traverse.Create(w);
                    wt.Property("AdditionalTooltipText").SetValue(e.Item4);
                    if (w.CurrentLevel != e.Item3)
                    {
                        wt.Property("CurrentLevel").SetValue(e.Item3);
                        LevelM.Invoke(c, new object[] { w, e.Item3 });
                    }
                    wt.Property("IsActive").SetValue(e.Item2);
                    // Anzeige nachziehen (die Methode prüft selbst, ob das Symbol schon so steht)
                    StateM.Invoke(c, new object[] { w, e.Item2 });
                    if (e.Item2)
                        TextM.Invoke(c, new object[] { w, e.Item4 });
                }
                Applied++;
            }
            catch (Exception ex) { Plugin.Log.LogDebug("Warnungen anwenden: " + ex.Message); }
            finally { _applying = false; }
        }

        // ================================================================ Sperren beim Gast

        private static bool GuestBlocks => !_applying && _instance != null && _instance._session.GuestControlsLocked && _instance._last != null;

        /// <summary>Prefix auf die drei Anzeige-Handler des WarningsInfoCanvas: beim Gast nur der Stand vom Host.</summary>
        private static bool CanvasHandlerPrefix() => !GuestBlocks;

        public static void ApplyPatches(Harmony harmony)
        {
            int ok = 0;
            foreach (var m in new[] { "OnWarningStateChanged", "OnWarningLevelChanged", "OnTooltipTextChanged" })
            {
                try
                {
                    var target = AccessTools.DeclaredMethod(typeof(WarningsInfoCanvas), m);
                    if (target == null) { Plugin.Log.LogWarning("IFZCOOP warning hook fehlt: " + m); continue; }
                    harmony.Patch(target, prefix: new HarmonyMethod(typeof(WarningSync), nameof(CanvasHandlerPrefix)));
                    ok++;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"IFZCOOP warning hook {m}: {e.Message}"); }
            }
            Plugin.Log.LogInfo($"Warnungs-Abgleich: {ok} Haken gesetzt");
        }

        public string Compact() => $"warnings sent={Sent} applied={Applied} last={(_last == null ? "-" : string.Join(",", _last.ConvertAll(e => e.Item1 + (e.Item2 ? "+" : "-") + e.Item3)))}";
    }
}
