using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DialogueSystem.Runtime.Core;
using HarmonyLib;
using IfzCoop.Net;
using Steamworks;
using TransmissionSystem;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>Wer Funksprüche beantworten darf. Legt der Host fest.</summary>
    public enum AnswerMode
    {
        Alle = 0,
        NurHost = 1,
        NurGaeste = 2,
    }

    /// <summary>
    /// Gemeinsame Funksprüche.
    ///
    /// Host:  Funksprüche entstehen nur hier. Start und jede gespielte Zeile gehen an alle Gäste.
    ///        Antworten von Gästen kommen als Anfrage an und werden hier ausgeführt; die erste gültige gilt.
    /// Gast:  Eigene Funksprüche werden unterdrückt. Der Funkspruch des Hosts wird gestartet, seine Zeilen
    ///        werden der Reihe nach abgespielt (statt selbst zu würfeln). Antwort-Klicks gehen an den Host.
    ///
    /// Ausgehende Funksprüche des Spielers selbst („outtrans…“, über das Funkgerät-Menü) bleiben lokal.
    /// </summary>
    internal sealed class RadioSync
    {
        private const string OutgoingPrefix = "outtrans";
        private const float StaleLineSeconds = 60f;

        private static readonly AccessTools.FieldRef<TransmissionManager, DialogueManager> DialogueManagerField =
            AccessTools.FieldRefAccess<TransmissionManager, DialogueManager>("_dialogueManager");

        private static readonly AccessTools.FieldRef<DialogueManager, DialogueLine> CurrentLineField =
            AccessTools.FieldRefAccess<DialogueManager, DialogueLine>("_currentPlayingLine");

        private static readonly AccessTools.FieldRef<DialogueManager, List<DialogueLine>> PreviousAnswersField =
            AccessTools.FieldRefAccess<DialogueManager, List<DialogueLine>>("_previousAnswers");

        // TransmissionManager._isPaused: true, solange eine UI-Pause (Esc-Menü, Autospeichern, Spielende) den Funk anhält
        // (UIPauseLayer.ApplyPause → TransmissionManager.Pause).
        private static readonly AccessTools.FieldRef<TransmissionManager, bool> TmPausedField =
            AccessTools.FieldRefAccess<TransmissionManager, bool>("_isPaused");

        private static readonly MethodInfo LineSelectedMethod =
            AccessTools.Method(typeof(DialogueManager), "DialogueLineSelected", new[] { typeof(DialogueLine) });

        private sealed class PendingLine
        {
            public string DialogueId;
            public string LineId;
            public bool Selected;
            public float Received;
        }

        private readonly Session _session;
        private TransmissionManager _transmissionManager;

        // Host
        private bool _inSelection;
        private bool _selectionLineSent;
        private bool _inAutoComplete;
        private bool _applyingGuestAnswer;
        private string _answeredBy;

        // Gast
        private string _remoteTransmissionId;
        private readonly Queue<PendingLine> _pendingLines = new Queue<PendingLine>();
        private DialogueLine _expectedRemoteLine;   // genau diese Zeile darf PlayLine beim Gast spielen
        private bool _applyingHostAnswer;
        private bool _waitingForUnpause;

        /// <summary>Gast: so oft hat eine Host-Zeile gewartet, bis die eigene UI-Pause (Esc) vorbei war.</summary>
        public int LinesHeldForPause { get; private set; }

        /// <summary>Beim Gast: die vom Host gemeldete Einstellung.</summary>
        public AnswerMode GuestSideMode = AnswerMode.Alle;

        public RadioSync(Session session)
        {
            _session = session;
        }

        public AnswerMode Mode => _session.Role == Role.Guest ? GuestSideMode : Plugin.AnswerModeSetting.Value;

        public void Reset()
        {
            _remoteTransmissionId = null;
            _pendingLines.Clear();
            _inSelection = false;
            _selectionLineSent = false;
            _inAutoComplete = false;
            _applyingGuestAnswer = false;
            _answeredBy = null;
            _expectedRemoteLine = null;
            _applyingHostAnswer = false;
            _waitingForUnpause = false;
            GuestSideMode = AnswerMode.Alle;
        }

        private TransmissionManager Transmissions
        {
            get
            {
                if (_transmissionManager == null)
                    _transmissionManager = UnityEngine.Object.FindFirstObjectByType<TransmissionManager>();
                return _transmissionManager;
            }
        }

        private DialogueManager Dialogues
        {
            get
            {
                var tm = Transmissions;
                return tm == null ? null : DialogueManagerField(tm);
            }
        }

        private static bool IsOutgoing(string dialogueId) =>
            dialogueId != null && dialogueId.StartsWith(OutgoingPrefix, StringComparison.Ordinal);

        private bool GuestSynced => _session.GuestControlsLocked;

        private bool HostWithGuests => _session.Role == Role.Host && _session.ActiveGuestCount > 0;

        private bool IsRemoteDialogue(Dialogue dialogue) =>
            dialogue != null && _remoteTransmissionId != null && dialogue.ID == _remoteTransmissionId;

        // ------------------------------------------------------------- Patch-Einstiege

        /// <summary>TransmissionManager.StartTransmissionCoroutine – Prefix. false = nicht starten.</summary>
        public bool AllowTransmissionStart(string id)
        {
            if (!GuestSynced || IsOutgoing(id))
                return true;
            if (id == _remoteTransmissionId)
                return true; // vom Host gestartet (auch wenn das Spiel ihn erst aus der Warteschlange holt)
            Plugin.Log.LogInfo("Funkspruch beim Gast unterdrückt (kommt vom Host): " + id);
            return false;
        }

        /// <summary>TransmissionManager.StartTransmissionCoroutine – Postfix: Host meldet den Start.</summary>
        public void OnActiveDialogueSet(string dialogueId)
        {
            if (!HostWithGuests || dialogueId == null || IsOutgoing(dialogueId))
                return;
            _session.BroadcastToGuests(Protocol.Build(Msg.TransmissionStart, w => w.Write(dialogueId)));
            Plugin.Log.LogInfo("IFZCOOP radio start " + dialogueId);
        }

        /// <summary>
        /// Host: ein Gast hat gerade fertig geladen. Läuft schon ein Funkspruch (z. B. „Hier spricht IFZ“ direkt nach
        /// der HQ-Wahl), bekommt er ihn nachgereicht, sonst sähe er ihn nie.
        /// </summary>
        public void SendActiveTo(Peer peer)
        {
            string id = Transmissions?.ActiveDialogueId;
            if (id == null || IsOutgoing(id))
                return;
            _session.SendTo(peer.Id, Protocol.Build(Msg.TransmissionStart, w => w.Write(id)));
            // auch die Zeile, die gerade läuft (mit ihren Antworten), sonst stünde der Gast am Anfang fest
            var manager = Dialogues;
            var line = manager != null ? CurrentLineField(manager) : null;
            if (line != null && manager.CurrentDialogue != null && manager.CurrentDialogue.ID == id)
                _session.SendTo(peer.Id, Protocol.Build(Msg.DialogueLine, w =>
                {
                    w.Write(id);
                    w.Write(line.ID);
                    w.Write(false);
                }));
            Plugin.Log.LogInfo($"IFZCOOP radio start {id} an {peer.Name} nachgereicht (Zeile {line?.ID})");
        }

        // ------------------------------------------------------------- Testmodus

        /// <summary>Testmodus: Funkspruch starten (wie ein Ereignis des Spiels).</summary>
        public string QaStart(string id)
        {
            var tm = Transmissions;
            if (tm == null)
                return "kein TransmissionManager";
            tm.StartTransmissionCoroutine(id);
            return "gestartet " + id;
        }

        /// <summary>Testmodus: die n-te angebotene Antwort anklicken (beim Gast geht das als Anfrage an den Host).</summary>
        public string QaAnswer(int index)
        {
            var manager = Dialogues;
            var offered = OfferedAnswers(manager);
            if (offered.Count == 0)
                return "keine Antworten offen";
            if (index < 0 || index >= offered.Count)
                return "nur " + offered.Count + " Antworten";
            var answer = offered[index];
            LineSelectedMethod.Invoke(manager, new object[] { answer });
            return "geklickt " + answer.ID + " (" + LineText(answer) + ")";
        }

        /// <summary>
        /// Testmodus: einen eigenen ausgehenden Funkspruch absetzen wie über das Funkgerät-Menü
        /// (TransmissionManager.SetBroadcastDialogue mit dem geladenen Funkspruch aus _broadcastTransmissions).
        /// </summary>
        public string QaOutgoing(string id)
        {
            var tm = Transmissions;
            if (tm == null)
                return "kein TransmissionManager";
            if (tm.ActiveDialogueId != null)
                return "belegt: " + tm.ActiveDialogueId + " (ein Funkgerät-Klick täte nichts)";
            var list = Traverse.Create(tm).Field("_broadcastTransmissions").GetValue() as System.Collections.IList;
            if (list == null)
                return "keine Rundrufe geladen";
            foreach (var result in list)
            {
                var dialogue = Traverse.Create(result).Field("Dialogue").GetValue() as Dialogue
                               ?? Traverse.Create(result).Property("Dialogue").GetValue() as Dialogue;
                if (dialogue == null || dialogue.ID != id)
                    continue;
                AccessTools.Method(typeof(TransmissionManager), "SetBroadcastDialogue").Invoke(tm, new[] { result });
                return "gesendet " + id + " aktiv=" + (tm.ActiveDialogueId ?? "-");
            }
            return "Rundruf " + id + " nicht geladen (" + list.Count + " geladen)";
        }

        /// <summary>Für die Statusdatei: laufender Funkspruch, aktuelle Zeile, angebotene Antworten.</summary>
        public string StatusLine()
        {
            var manager = Dialogues;
            var dialogue = manager?.CurrentDialogue;
            var line = manager != null ? CurrentLineField(manager) : null;
            var answers = new List<string>();
            foreach (var a in OfferedAnswers(manager)) answers.Add(a.ID);
            var tm = Transmissions;
            bool paused = tm != null && TmPausedField(tm);
            return $"radio={tm?.ActiveDialogueId ?? "-"} dialog={dialogue?.ID ?? "-"} line={line?.ID ?? "-"} answers={string.Join(",", answers)}"
                + $" tmPaused={(paused ? 1 : 0)} starting={(tm != null && tm.IsTransmissionStarting ? 1 : 0)} queue={tm?._transmissionQueue.Count ?? -1}"
                + $" pendingLines={_pendingLines.Count} heldForPause={LinesHeldForPause}";
        }

        /// <summary>
        /// DialogueManager.PlayLine – Prefix. Der Gast spielt Zeilen des Host-Funkspruchs nur auf Ansage,
        /// und zwar genau die angesagte Zeile. Was das Spiel danach selbst nachschiebt (nächste Zeile,
        /// ausgewürfelte Antwort), wird blockiert; das kommt ebenfalls vom Host.
        /// </summary>
        public bool AllowPlayLine(DialogueManager manager, DialogueLine line)
        {
            if (!GuestSynced || !IsRemoteDialogue(manager.CurrentDialogue))
                return true;
            if (_expectedRemoteLine != null && ReferenceEquals(line, _expectedRemoteLine))
            {
                _expectedRemoteLine = null;
                return true;
            }
            return false;
        }

        /// <summary>DialogueManager.PlayLine – Prefix (nach AllowPlayLine). Der Host verteilt jede Zeile.</summary>
        public void OnLinePlaying(DialogueManager manager, DialogueLine line)
        {
            if (!HostWithGuests || line == null)
                return;
            var dialogue = manager.CurrentDialogue;
            if (dialogue == null || IsOutgoing(dialogue.ID))
                return;
            // Nur die erste Zeile innerhalb einer Auswahl ist die Antwort selbst; was das Spiel
            // danach im selben Aufruf nachschiebt, ist wieder eine normale Zeile.
            bool selected = _inSelection && !_selectionLineSent;
            if (_inSelection)
                _selectionLineSent = true;
            _session.BroadcastToGuests(Protocol.Build(Msg.DialogueLine, w =>
            {
                w.Write(dialogue.ID);
                w.Write(line.ID);
                w.Write(selected);
            }));
        }

        /// <summary>DialogueManager.DialogueLineSelected – Prefix. Entscheidet, ob eine Antwort hier gilt.</summary>
        public bool AllowAnswer(DialogueManager manager, DialogueLine line)
        {
            var dialogue = manager.CurrentDialogue;

            if (_applyingHostAnswer)
                return true; // Gast spielt die Antwort des Hosts nach

            if (dialogue == null || IsOutgoing(dialogue.ID))
                return true;

            if (_session.Role == Role.Host)
            {
                if (_applyingGuestAnswer)
                {
                    BeginSelection();
                    return true; // _answeredBy ist schon der Gast
                }
                if (_inAutoComplete)
                {
                    _answeredBy = "Automatisch";
                    BeginSelection();
                    return true;
                }
                if (HostWithGuests && Mode == AnswerMode.NurGaeste)
                {
                    _session.ShowNotice("Nur Gäste dürfen antworten (F6 zum Umstellen)");
                    return false;
                }
                _answeredBy = _session.MyName;
                BeginSelection();
                return true;
            }

            if (GuestSynced && IsRemoteDialogue(dialogue))
            {
                if (Mode == AnswerMode.NurHost)
                {
                    _session.ShowNotice("Nur der Host darf antworten");
                    return false;
                }
                _session.SendToHost(Protocol.Build(Msg.AnswerRequest, w =>
                {
                    w.Write(dialogue.ID);
                    w.Write(line.ID);
                }));
                _session.ShowNotice("Antwort an den Host geschickt ...");
                return false;
            }

            return true;
        }

        private void BeginSelection()
        {
            _inSelection = true;
            _selectionLineSent = false;
        }

        /// <summary>DialogueManager.DialogueLineSelected – Postfix.</summary>
        public void AfterAnswer(DialogueLine line)
        {
            if (!_inSelection)
                return;
            _inSelection = false;
            if (_session.Role != Role.Host || _answeredBy == null)
                return;
            string text = $"{_answeredBy} hat geantwortet: {LineText(line)}";
            _answeredBy = null;
            _session.ShowNotice(text);
            if (HostWithGuests)
                _session.BroadcastToGuests(Protocol.Build(Msg.Notice, w => w.Write(text)));
        }

        /// <summary>DialogueManager.AutoCompleteWithAnswer – Prefix. Beim Gast nie, beim Host markieren.</summary>
        public bool AllowAutoComplete(DialogueManager manager)
        {
            if (GuestSynced && IsRemoteDialogue(manager.CurrentDialogue))
                return false;
            _inAutoComplete = _session.Role == Role.Host;
            return true;
        }

        public void AfterAutoComplete()
        {
            _inAutoComplete = false;
        }

        // ------------------------------------------------------------- Netz

        public void HostOnAnswerRequest(Peer peer, BinaryReader r)
        {
            string dialogueId = r.ReadString();
            string lineId = r.ReadString();

            if (Mode == AnswerMode.NurHost)
            {
                _session.SendTo(peer.Id, Protocol.Build(Msg.Notice, w => w.Write("Nur der Host darf antworten")));
                return;
            }

            var manager = Dialogues;
            var dialogue = manager?.CurrentDialogue;
            if (dialogue == null || dialogue.ID != dialogueId)
            {
                Plugin.Log.LogInfo($"Antwort von {peer.Name} verworfen: Funkspruch {dialogueId} ist nicht mehr aktiv");
                return;
            }

            var line = OfferedAnswer(manager, lineId);
            if (line == null)
            {
                // Typischer Fall: jemand anderes war schneller.
                Plugin.Log.LogInfo($"Antwort von {peer.Name} verworfen: {lineId} steht nicht mehr zur Wahl");
                return;
            }

            _answeredBy = peer.Name;
            _applyingGuestAnswer = true;
            try
            {
                LineSelectedMethod.Invoke(manager, new object[] { line });
            }
            finally
            {
                _applyingGuestAnswer = false;
            }
        }

        public void GuestOnTransmissionStart(BinaryReader r)
        {
            string id = r.ReadString();
            _remoteTransmissionId = id;
            _pendingLines.Clear();
            var tm = Transmissions;
            if (tm == null)
            {
                Plugin.Log.LogWarning("TransmissionManager nicht gefunden, Funkspruch " + id + " entfällt");
                return;
            }
            Plugin.Log.LogInfo("IFZCOOP radio start (vom Host) " + id);
            tm.StartTransmissionCoroutine(id);
        }

        public void GuestOnDialogueLine(BinaryReader r)
        {
            _pendingLines.Enqueue(new PendingLine
            {
                DialogueId = r.ReadString(),
                LineId = r.ReadString(),
                Selected = r.ReadBoolean(),
                Received = Time.realtimeSinceStartup,
            });
            PumpLines();
        }

        public void GuestOnSettings(BinaryReader r)
        {
            GuestSideMode = (AnswerMode)r.ReadByte();
            // ab 0.21.0: darf der Gast das Entwickler-Menü benutzen
            CheatWindow.GuestAllowedByHost = r.BaseStream.Position < r.BaseStream.Length && r.ReadBoolean();
            // ab 0.22.0: Lobby-Einstellungen (Spielmodus, Spieler, Versus …)
            Menu.LobbySettings.Read(r);
        }

        public byte[] BuildSettings()
        {
            var mode = (byte)Plugin.AnswerModeSetting.Value;
            bool cheats = Plugin.GuestCheats.Value;
            return Protocol.Build(Msg.Settings, w => { w.Write(mode); w.Write(cheats); Menu.LobbySettings.Write(w); });
        }

        /// <summary>Jeden Frame beim Gast: wartende Zeilen abspielen, sobald der Funkspruch geladen ist.</summary>
        public void Update()
        {
            if (_pendingLines.Count > 0)
                PumpLines();
        }

        private void PumpLines()
        {
            // Hält beim Gast eine UI-Pause (Esc-Menü) den Funk an, wird keine Host-Zeile abgespielt, bis sie vorbei ist.
            // Grund: Im Einzelspiel läuft während dieser Pause keine Zeile weiter (TextPrinter steht). Spielte der Mod trotzdem
            // die letzte Zeile, schloss AutoHideTransmissionPanel → OnTransmissionClosed → PauseController.UnpauseGameplay das
            // Fenster mitten in der UI-Pause: UnpauseGameplay leert den Pausen-Stapel, ohne UIPauseLayer/PopupPauseLayer
            // zurückzunehmen. Danach blieb TransmissionManager._isPaused für immer true (jeder neue Funk hängt in
            // StartTransmissionAsync „while (_isPaused)“ bei gesetzter ActiveDialogueId) und die Kamera-Sperre Pause stehen.
            var tm = Transmissions;
            if (tm != null && TmPausedField(tm))
            {
                if (!_waitingForUnpause)
                {
                    _waitingForUnpause = true;
                    LinesHeldForPause++;
                    Plugin.Log.LogInfo("IFZCOOP Funkzeile des Hosts wartet: Funk beim Gast pausiert (Esc-Menü o. ä.)");
                }
                return;
            }
            _waitingForUnpause = false;
            var manager = Dialogues;
            while (_pendingLines.Count > 0)
            {
                var next = _pendingLines.Peek();
                var dialogue = manager?.CurrentDialogue;

                if (dialogue == null || dialogue.ID != next.DialogueId)
                {
                    bool stale = next.DialogueId != _remoteTransmissionId
                                 || Time.realtimeSinceStartup - next.Received > StaleLineSeconds;
                    if (!stale)
                        return; // Funkspruch lädt noch, später weiter
                    _pendingLines.Dequeue();
                    continue;
                }

                _pendingLines.Dequeue();
                var line = TryGetLine(dialogue, next.LineId);
                if (line == null)
                {
                    Plugin.Log.LogWarning($"Zeile {next.LineId} fehlt in {dialogue.ID}");
                    continue;
                }

                _expectedRemoteLine = line;
                _applyingHostAnswer = next.Selected;
                try
                {
                    if (next.Selected)
                        LineSelectedMethod.Invoke(manager, new object[] { line });
                    else
                        manager.PlayLine(line);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Zeile " + next.LineId + " abspielen: " + e);
                }
                finally
                {
                    _expectedRemoteLine = null;
                    _applyingHostAnswer = false;
                }
                manager = Dialogues; // der Funkspruch kann mit dieser Zeile geendet haben
            }
        }

        // ------------------------------------------------------------- Hilfen

        private static DialogueLine TryGetLine(Dialogue dialogue, string id)
        {
            try
            {
                return dialogue.GetLine(id);
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }

        /// <summary>
        /// Die angebotene Antwort mit dieser ID (per ID, nicht per Objekt: Zuwanderer-Funksprüche bauen neue
        /// DialogueLine-Objekte mit denselben IDs, Dialogue.GetLine liefert dann das Original), sonst null.
        /// </summary>
        private static DialogueLine OfferedAnswer(DialogueManager manager, string lineId)
        {
            var current = CurrentLineField(manager);
            if (current != null)
                foreach (var a in current.Answers)
                    if (a != null && a.ID == lineId) return a;
            var previous = PreviousAnswersField(manager);
            if (previous != null)
                foreach (var a in previous)
                    if (a != null && a.ID == lineId) return a;
            return null;
        }

        /// <summary>Angebotene Antworten: die der aktuellen Zeile, sonst die früheren, die das Spiel wieder anbietet.</summary>
        private static List<DialogueLine> OfferedAnswers(DialogueManager manager)
        {
            var result = new List<DialogueLine>();
            if (manager == null)
                return result;
            var current = CurrentLineField(manager);
            if (current != null)
                foreach (var a in current.Answers)
                    if (a != null) result.Add(a);
            if (result.Count == 0)
            {
                var previous = PreviousAnswersField(manager);
                if (previous != null)
                    foreach (var a in previous)
                        if (a != null) result.Add(a);
            }
            return result;
        }

        /// <summary>Was am Ende einer Zeile beim Gast laufen darf: nur Anzeige. Alles andere macht der Host.</summary>
        private static readonly HashSet<string> GuestActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "End", "SetGraphic", "OverrideGraphic", "ShowAnswersButtons",
        };

        /// <summary>
        /// DialogueManager.InvokeActionOnEnd – Prefix. Beim Gast liefen sonst die Aktionen am Zeilenende eines Host-Funkspruchs
        /// ein zweites Mal (StartAction, StartEvent, StartMission, Zuwanderer, Bürger …). Gefiltert wird der Ausdruck der Zeile,
        /// das Original kommt im Postfix zurück (original != null).
        /// </summary>
        public void FilterActionOnEnd(DialogueManager manager, DialogueLine line, out string original)
        {
            original = null;
            if (line == null || string.IsNullOrEmpty(line.ActionOnEnd) || !GuestSynced || !IsRemoteDialogue(manager.CurrentDialogue))
                return;
            var kept = new List<string>();
            foreach (var raw in line.ActionOnEnd.Split(';'))
            {
                var statement = raw.Trim();
                if (statement.Length == 0)
                    continue;
                int paren = statement.IndexOf('(');
                string name = (paren > 0 ? statement.Substring(0, paren) : statement).Trim();
                if (GuestActions.Contains(name))
                    kept.Add(statement);
                else
                    Plugin.Log.LogInfo("Funk-Aktion beim Gast übersprungen (macht der Host): " + statement);
            }
            original = line.ActionOnEnd;
            line.ActionOnEnd = kept.Count == 0 ? string.Empty : string.Join("; ", kept) + ";";
        }

        private static string LineText(DialogueLine line)
        {
            try
            {
                var text = line.GetDialogOptionText();
                if (!string.IsNullOrEmpty(text))
                    return text.Length > 80 ? text.Substring(0, 77) + "..." : text;
            }
            catch (Exception)
            {
                // Text hängt am Sprachsystem; dann eben die ID.
            }
            return line.ID;
        }
    }
}
