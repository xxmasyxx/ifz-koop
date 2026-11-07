using System;
using System.IO;
using Controllers;
using Gameplay.Rebuilding.Towers;
using Gameplay.Units;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Das Hauptquartier wählt nur der Host.
    ///
    /// - Host: Solange er sein erstes HQ wählt (HqSetterController.IsSelecting), hält er Spielstände für Gäste
    ///   zurück und schickt sie kurz nach der Wahl (Session.SendSnapshot). Der Gast bekommt so immer einen
    ///   Spielstand mit HQ, und die Folgen der ersten Wahl (Startkisten, Fahrzeuge, Ereignisse) laufen nur beim Host.
    /// - Gast: Bestätigt er trotzdem ein HQ im Auswahlfenster, wird die Wahl verworfen.
    /// - Gast: „Lager aufschlagen“ im Konvoi (ConvoyStructureController.SetupHq) geht als Auftrag an den Host.
    ///   Danach bekommen alle Gäste einen frischen Spielstand, weil Gebäude noch nicht abgeglichen werden (DESIGN Nr. 11).
    /// </summary>
    internal sealed class HqSync
    {
        /// <summary>Wartezeit nach dem Aufschlagen eines Lagers, bis die Gäste den neuen Spielstand bekommen.</summary>
        private const float ResyncDelay = 2f;

        private readonly Session _session;
        private float _resyncAt = -1f;

        public HqSync(Session session)
        {
            _session = session;
        }

        /// <summary>Host: wählt gerade sein erstes Hauptquartier (Uhr gesperrt, Auswahlfenster offen).</summary>
        public bool HostChoosingHq
        {
            get
            {
                if (!GameAccess.IsGameStarted)
                    return false;
                var setter = GameAccess.Game<HqSetterController>();
                return setter != null && setter.IsSelecting;
            }
        }

        public void HostUpdate()
        {
            if (_resyncAt > 0f && Time.realtimeSinceStartup >= _resyncAt)
            {
                _resyncAt = -1f;
                Plugin.Log.LogInfo("IFZCOOP Lager aufgeschlagen, Gäste bekommen neuen Spielstand");
                _session.ResyncAll();
            }
        }

        // ================================================================ Gast

        /// <summary>Prefix von HqSetterController.OnSelectionConfirmed. false = Wahl nicht übernehmen.</summary>
        public bool GuestConfirmPrefix(HqSetterController setter)
        {
            if (_session.Role != Role.Guest || UnitSync.NetApplying)
                return true;
            try
            {
                Traverse.Create(setter).Method("OnSelectionDenied").GetValue();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("HQ-Auswahl verwerfen: " + e.Message);
            }
            _session.ShowNotice("Das Hauptquartier wählt der Host");
            return false;
        }

        /// <summary>Prefix von ConvoyStructureController.SetupHq beim Gast. false = nicht lokal.</summary>
        public bool ForwardSetupHq(Group squad)
        {
            if (_session.Role != Role.Guest || UnitSync.NetApplying)
                return true;
            if (squad == null || string.IsNullOrEmpty(squad.Id))
                return false;
            _session.SendToHost(Protocol.Build(Msg.HqSetupRequest, w => w.Write(squad.Id)));
            _session.ShowNotice("Lager aufschlagen beim Host beauftragt");
            return false;
        }

        // ================================================================ Host

        /// <summary>Postfix von ConvoyStructureController.SetupHq beim Host (eigener Klick oder Auftrag eines Gastes).</summary>
        public void OnSetupHq()
        {
            if (_session.Role == Role.Host && _session.ActiveGuestCount > 0)
                _resyncAt = Time.realtimeSinceStartup + ResyncDelay;
        }

        public void HostOnSetupRequest(Peer from, BinaryReader r)
        {
            string squadId = r.ReadString();
            var squad = UnitSync.FindGroupPublic(squadId);
            var convoys = GameAccess.Game<ConvoyStructureController>();
            if (squad == null || convoys == null || !squad.VehicleHandler.HaveVehicle())
            {
                _session.SendTo(from.Id, Protocol.Build(Msg.Notice, w => w.Write("Host: Dieser Trupp hat kein Fahrzeug für ein Lager")));
                return;
            }
            try
            {
                convoys.SetupHq(squad);
                Plugin.Log.LogInfo($"IFZCOOP Lager aufgeschlagen für {from.Name} mit Trupp {squadId}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("IFZCOOP Lager aufschlagen: " + e);
            }
        }
    }
}
