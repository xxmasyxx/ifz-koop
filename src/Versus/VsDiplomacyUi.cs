using IfzCoop.Game;
using UnityEngine;

namespace IfzCoop.Versus
{
    /// <summary>
    /// Fenster für Bündnisse: „Spieler …“ nach Klick auf die Kachel im Pausenmenü (Verbünden / Bündnis beenden) und
    /// oben in der Mitte die Anfragen an mich (Annehmen / Ablehnen), auch ohne Pausenmenü.
    /// </summary>
    internal static class VsDiplomacyUi
    {
        private const int PlayerWindowId = 0x1F2E50;
        private const int AskWindowId = 0x1F2E51;
        private static int _target = -1;
        private static Rect _playerRect = new Rect(0f, 0f, 360f, 10f);
        private static Rect _askRect = new Rect(0f, 70f, 440f, 10f);
        private static GUIStyle _text, _box;

        private static VersusSession Vs => VersusSession.Instance;

        private static bool InGame => VersusSession.Active && GameAccess.IsGameStarted && Vs.Phase != VsPhase.Lobby;

        /// <summary>Klick auf die Kachel eines anderen Spielers im Pausenmenü.</summary>
        public static void OpenFor(int slot)
        {
            if (!InGame || slot == Vs.MySlot)
                return;
            _target = slot;
            _playerRect.x = (Screen.width - _playerRect.width) / 2f;
            _playerRect.y = Screen.height * 0.30f;
        }

        public static void Draw()
        {
            if (!InGame)
            {
                _target = -1;
                return;
            }
            if (_text == null)
            {
                _text = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true, richText = true };
                _text.normal.textColor = Color.white;
                // deckend und dunkel wie das Statistik-Fenster (sonst schimmern Karte und Symbole durch)
                var bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0.07f, 0.09f, 0.10f, 1f));
                bg.Apply();
                _box = new GUIStyle(GUI.skin.window) { padding = new RectOffset(14, 14, 32, 12), fontSize = 16, fontStyle = FontStyle.Bold };
                _box.normal.background = bg;
                _box.onNormal.background = bg;
                _box.normal.textColor = new Color(0.29f, 0.99f, 0.49f);
                _box.onNormal.textColor = _box.normal.textColor;
            }

            if (_target >= 0)
            {
                if (Vs.Roster.BySlot(_target) == null) _target = -1;
                else
                {
                    _playerRect = GUILayout.Window(PlayerWindowId, _playerRect, DrawPlayer, "BÜNDNIS", _box, GUILayout.Width(360f));
                    Menu.ModUi.Report(PlayerWindowId, _playerRect);
                }
            }

            var asks = Vs.Diplomacy.IncomingAsks;
            if (asks.Count > 0)
            {
                _askRect.x = (Screen.width - _askRect.width) / 2f;
                _askRect = GUILayout.Window(AskWindowId, _askRect, DrawAsk, "BÜNDNIS-ANFRAGE", _box, GUILayout.Width(440f));
                Menu.ModUi.Report(AskWindowId, _askRect);
            }
        }

        private static string Colored(VsPlayer p)
        {
            var c = p.Color;
            return $"<color=#{c.r:X2}{c.g:X2}{c.b:X2}><b>{p.Name}</b></color>";
        }

        private static void DrawPlayer(int id)
        {
            var p = Vs.Roster.BySlot(_target);
            if (p == null) { _target = -1; return; }
            var d = Vs.Diplomacy;
            GUILayout.Label(Colored(p), _text);
            GUILayout.Label("Status: " + d.RelationLabel(p.Slot), _text);
            GUILayout.Space(6f);
            if (d.IsAlliedWithMe(p.Slot))
            {
                GUILayout.Label("Verbündete sehen die Häuser, die der andere geplündert hat.", _text);
                if (GUILayout.Button("Bündnis beenden", GUILayout.Height(30f)))
                    d.Request(p.Slot, 1);
            }
            else if (d.IncomingAsks.Contains(p.Slot))
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Annehmen", GUILayout.Height(30f))) d.Answer(p.Slot, true);
                if (GUILayout.Button("Ablehnen", GUILayout.Height(30f))) d.Answer(p.Slot, false);
                GUILayout.EndHorizontal();
            }
            else if (d.AskSentTo(p.Slot))
            {
                GUILayout.Label("Anfrage gesendet, " + p.Name + " muss noch annehmen.", _text);
            }
            else if (GUILayout.Button("Verbünden", GUILayout.Height(30f)))
            {
                d.Request(p.Slot, 0);
            }
            GUILayout.Space(4f);
            if (GUILayout.Button("Schließen"))
                _target = -1;
            GUI.DragWindow();
        }

        private static void DrawAsk(int id)
        {
            var d = Vs.Diplomacy;
            if (d.IncomingAsks.Count == 0)
                return;
            byte from = d.IncomingAsks[0];
            var p = Vs.Roster.BySlot(from);
            if (p == null)
            {
                d.IncomingAsks.RemoveAt(0);
                return;
            }
            GUILayout.Label(Colored(p) + " möchte sich mit dir verbünden.", _text);
            GUILayout.Label("Verbündete sehen die Häuser, die der andere geplündert hat.", _text);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Annehmen", GUILayout.Height(30f))) d.Answer(from, true);
            if (GUILayout.Button("Ablehnen", GUILayout.Height(30f))) d.Answer(from, false);
            GUILayout.EndHorizontal();
            if (d.IncomingAsks.Count > 1)
                GUILayout.Label($"(noch {d.IncomingAsks.Count - 1} weitere Anfrage(n))", _text);
        }
    }
}
