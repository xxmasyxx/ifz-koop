using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using UnityEngine;

namespace IfzCoop
{
    /// <summary>
    /// Fehlerbericht (Wunsch des Nutzers, 6.11.2026), Taste F8 (BugReportKey).
    /// Beim Drücken wird sofort festgehalten: Bildschirmfoto, die letzten Zeilen aus dem Mod-Log (BepInEx LogOutput.log)
    /// und dem Spiel-Log (Player.log), der ganze Zustand wie in der QA-Statusdatei. Dann öffnet sich ein Fenster, in das man
    /// schreibt, was passiert ist. „Speichern“ legt alles in &lt;Spiel&gt;\BepInEx\IfzCoop-Fehlerberichte ab. Beim Gast (Koop oder
    /// Versus) geht der Bericht zusätzlich in Stücken an den Host und wird dort im selben Ordner gespeichert.
    /// Die frühere F8-Kamera-Rettung ist ein Knopf in diesem Fenster.
    /// </summary>
    internal static class BugReport
    {
        private const int ModLogLines = 400;
        private const int GameLogLines = 200;
        private const int MaxReportBytes = 20 * 1024 * 1024;

        public static bool Visible { get; private set; }
        public static int Saved { get; private set; }
        public static int Received { get; private set; }
        public static string LastPath { get; private set; } = "-";

        private static bool _capturing;
        private static string _autoText;

        /// <summary>QA: wie F8, aber mit festem Text sofort speichern/senden („bugreport &lt;Text&gt;“).</summary>
        public static string QaReport(string text)
        {
            if (Visible || _capturing) return "Fenster schon offen";
            _autoText = string.IsNullOrEmpty(text) ? "QA-Test" : text;
            Toggle(Plugin.Instance);
            return "Bericht wird erstellt";
        }
        private static string _text = "";
        private static DateTime _at;
        private static string _modLog, _gameLog, _status, _situation;
        private static byte[] _jpg;
        private static Rect _rect = new Rect(0f, 0f, 560f, 330f);
        private static object _savedKeyboard;
        private static object _savedPrevious;
        private static GUIStyle _area;

        public static string Folder => Path.Combine(Paths.BepInExRootPath, "IfzCoop-Fehlerberichte");

        // ================================================================ Öffnen

        public static void Toggle(MonoBehaviour runner)
        {
            if (Visible) { Close(); return; }
            if (_capturing) return;
            _capturing = true;
            _at = DateTime.Now;
            _text = "";
            try { _modLog = Tail(Path.Combine(Paths.BepInExRootPath, "LogOutput.log"), ModLogLines); } catch (Exception e) { _modLog = "Fehler: " + e.Message; }
            try { _gameLog = Tail(Application.consoleLogPath, GameLogLines); } catch (Exception e) { _gameLog = "Fehler: " + e.Message; }
            try { _status = QaDriver.Instance?.StatusSnapshot() ?? "-"; } catch (Exception e) { _status = "Fehler: " + e.Message; }
            _situation = Situation();
            runner.StartCoroutine(Capture());
        }

        /// <summary>Bildschirmfoto am Ende des Bildes, bevor das Fenster gezeichnet wird.</summary>
        private static IEnumerator Capture()
        {
            yield return new WaitForEndOfFrame();
            _jpg = null;
            try
            {
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (tex != null)
                {
                    _jpg = tex.EncodeToJPG(75);
                    UnityEngine.Object.Destroy(tex);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Fehlerbericht Bildschirmfoto: " + e.Message);
            }
            _capturing = false;
            if (_autoText != null)
            {
                _text = _autoText;
                _autoText = null;
                Plugin.Log.LogInfo("IFZCOOP QA Fehlerbericht: " + Save());
                _jpg = null;
                yield break;
            }
            Visible = true;
            _rect.x = (Screen.width - _rect.width) / 2f;
            _rect.y = (Screen.height - _rect.height) / 3f;
            BlockGameKeyboard(true);
        }

        private static void Close()
        {
            Visible = false;
            _jpg = null;
            BlockGameKeyboard(false);
        }

        /// <summary>Beim Tippen sollen Spieltasten (Bauen, Tempo, Kamera) nicht auslösen.</summary>
        private static void BlockGameKeyboard(bool block)
        {
            try
            {
                var kb = GameAccess.Game<GameInput.KeyboardControllersManager>();
                if (kb == null) return;
                // Aktive UND vorherige Tastatur merken: SetKeyboardController überschreibt die vorherige. Ohne das
                // blieb nach F8 bei offenem Fenster (z. B. Forschung) nach dem Schließen des Fensters dessen Tastatur
                // aktiv – die Spieltasten (Shift für Wegpunkte usw.) gingen nicht mehr (Fehlerbericht 2026-11-06).
                var t = Traverse.Create(kb);
                var active = t.Field("_activeKeyboardController");
                var previous = t.Field("_previousKeyboardController");
                if (block)
                {
                    if (_savedKeyboard != null) return; // schon gesperrt
                    _savedKeyboard = active.GetValue();
                    _savedPrevious = previous.GetValue();
                    kb.SetKeyboardController<GameInput.EmptyKeyboardController>();
                }
                else if (_savedKeyboard != null)
                {
                    active.SetValue(_savedKeyboard);
                    previous.SetValue(_savedPrevious);
                    _savedKeyboard = null;
                    _savedPrevious = null;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Fehlerbericht Tastatur: " + e.Message);
            }
        }

        // ================================================================ Fenster

        public static void Draw()
        {
            if (!Visible) return;
            _rect = GUI.Window(0x1F2E3D, _rect, DrawWindow, "Fehler melden (" + Plugin.BugReportKey.Value + ")");
        }

        private static void DrawWindow(int id)
        {
            if (_area == null)
                _area = new GUIStyle(GUI.skin.textArea) { wordWrap = true, fontSize = 14 };
            GUILayout.Label("Was ist passiert? (Log, Zustand und Bildschirmfoto von " + _at.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " sind schon gemerkt)");
            GUI.SetNextControlName("ifzBugText");
            _text = GUILayout.TextArea(_text, 4000, _area, GUILayout.Height(170f));
            if (GUI.GetNameOfFocusedControl() != "ifzBugText")
                GUI.FocusControl("ifzBugText");
            string where = Plugin.Instance.Session != null && Plugin.Instance.Session.Role == Role.Guest || Versus.VersusSession.IsGuestActive
                ? "Wird bei dir gespeichert und an den Host geschickt."
                : "Wird hier gespeichert: BepInEx\\IfzCoop-Fehlerberichte";
            GUILayout.Label(where);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Speichern", GUILayout.Height(28f)))
            {
                string result = Save();
                Close();
                Notice(result);
            }
            if (GUILayout.Button("Abbrechen", GUILayout.Height(28f)))
                Close();
            if (GameAccess.IsGameStarted && GUILayout.Button("Kamera freigeben", GUILayout.Height(28f)))
                Notice(CameraRescue.Run());
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private static void Notice(string text)
        {
            if (Versus.VersusSession.Active)
                Versus.VersusSession.Instance.Notice(text, 5f);
            else
                Plugin.Instance.Session?.ShowNotice(text, 5f);
        }

        // ================================================================ Speichern und Senden

        private static string Situation()
        {
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            string name = "?";
            try { name = Plugin.Instance.Session?.MyName ?? "?"; } catch { }
            sb.AppendLine("Zeit: " + _at.ToString("yyyy-MM-dd HH:mm:ss", inv));
            sb.AppendLine("Spieler: " + name);
            sb.AppendLine("Mod: " + Plugin.Version + "  Spiel: " + Application.version);
            sb.AppendLine("Koop-Rolle: " + (Plugin.Instance.Session?.Role.ToString() ?? "-") + "  Versus: "
                          + (Versus.VersusSession.Active ? Versus.VersusSession.Instance.Role + " Phase " + Versus.VersusSession.Instance.Phase : "nein"));
            try
            {
                if (GameAccess.IsGameStarted)
                    sb.AppendLine($"Spiel: Tag {Controllers.Time.TimeController.Day} {Controllers.Time.TimeController.Hour.ToString("0.00", inv)} Uhr, Tempo {Controllers.Time.TimeController.SpeedMultiplier.ToString("0.##", inv)}");
                var cam = Cameras.GameCamera.Active;
                if (cam != null)
                    sb.AppendLine("Kamera: " + cam.transform.position.ToString("F1"));
            }
            catch { }
            return sb.ToString();
        }

        private static string Save()
        {
            var inv = CultureInfo.InvariantCulture;
            string name = "Spieler";
            try { name = Plugin.Instance.Session?.MyName ?? name; } catch { }
            string id = _at.ToString("yyyyMMdd-HHmmss", inv) + "-" + Safe(name);
            var sb = new StringBuilder();
            sb.AppendLine("IFZ Koop – Fehlerbericht");
            sb.AppendLine(_situation);
            sb.AppendLine("=== Beschreibung ===");
            sb.AppendLine(string.IsNullOrWhiteSpace(_text) ? "(keine)" : _text.Trim());
            sb.AppendLine();
            sb.AppendLine("=== Zustand (wie ifzcoop-status.txt) ===");
            sb.AppendLine(_status);
            sb.AppendLine("=== Mod-Log, letzte " + ModLogLines + " Zeilen ===");
            sb.AppendLine(_modLog);
            sb.AppendLine("=== Spiel-Log (Player.log), letzte " + GameLogLines + " Zeilen ===");
            sb.AppendLine(_gameLog);
            var files = new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>(id + ".txt", Encoding.UTF8.GetBytes(sb.ToString()))
            };
            if (_jpg != null)
                files.Add(new KeyValuePair<string, byte[]>(id + ".jpg", _jpg));
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (var f in files)
                    File.WriteAllBytes(Path.Combine(Folder, f.Key), f.Value);
                Saved++;
                LastPath = Path.Combine(Folder, id + ".txt");
                Plugin.Log.LogInfo("IFZCOOP Fehlerbericht gespeichert: " + LastPath + " – " + FirstLine(_text));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Fehlerbericht speichern: " + e.Message);
                return "Fehlerbericht konnte nicht gespeichert werden: " + e.Message;
            }
            bool sent = false;
            try { sent = SendToHost(id, files); }
            catch (Exception e) { Plugin.Log.LogWarning("Fehlerbericht senden: " + e.Message); }
            return sent ? "Fehlerbericht gespeichert und an den Host geschickt" : "Fehlerbericht gespeichert (BepInEx\\IfzCoop-Fehlerberichte)";
        }

        private static bool SendToHost(string id, List<KeyValuePair<string, byte[]>> files)
        {
            bool koop = Plugin.Instance.Session != null && Plugin.Instance.Session.Role == Role.Guest;
            bool vs = !koop && Versus.VersusSession.IsGuestActive;
            if (!koop && !vs)
                return false;
            foreach (var f in files)
            {
                byte[] data = f.Value;
                int count = Math.Max(1, (data.Length + Protocol.ChunkSize - 1) / Protocol.ChunkSize);
                for (int i = 0; i < count; i++)
                {
                    int offset = i * Protocol.ChunkSize;
                    int len = Math.Min(Protocol.ChunkSize, data.Length - offset);
                    int index = i;
                    string fileName = f.Key;
                    Action<BinaryWriter> write = w =>
                    {
                        w.Write(id);
                        w.Write(fileName);
                        w.Write(index);
                        w.Write(count);
                        w.Write(len);
                        w.Write(data, offset, len);
                    };
                    if (koop)
                        Plugin.Instance.Session.SendToHost(Protocol.Build(Msg.BugReportPart, write));
                    else
                        Versus.VersusSession.Instance.SendToHost(Versus.VsNet.Build(Msg.BugReportPart, write));
                }
            }
            Plugin.Log.LogInfo($"IFZCOOP Fehlerbericht {id} an den Host geschickt ({files.Count} Dateien)");
            return true;
        }

        // ================================================================ Host: Stücke sammeln

        private sealed class Incoming
        {
            public byte[][] Parts;
            public int Have;
            public int Bytes;
        }

        private static readonly Dictionary<string, Incoming> _incoming = new Dictionary<string, Incoming>();

        public static void HostOnPart(string fromName, BinaryReader r)
        {
            string id = Safe(r.ReadString());
            string fileName = Path.GetFileName(r.ReadString());
            int index = r.ReadInt32();
            int count = r.ReadInt32();
            int len = r.ReadInt32();
            if (count < 1 || count > 400 || index < 0 || index >= count || len < 0 || len > Protocol.ChunkSize)
                throw new InvalidDataException("Fehlerbericht: ungültiges Stück");
            byte[] data = r.ReadBytes(len);
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext != ".txt" && ext != ".jpg")
                return;
            string key = id + "/" + fileName;
            if (!_incoming.TryGetValue(key, out var inc) || inc.Parts.Length != count)
                _incoming[key] = inc = new Incoming { Parts = new byte[count][] };
            if (inc.Parts[index] == null)
            {
                inc.Parts[index] = data;
                inc.Have++;
                inc.Bytes += data.Length;
            }
            if (inc.Bytes > MaxReportBytes)
            {
                _incoming.Remove(key);
                return;
            }
            if (inc.Have < count)
                return;
            _incoming.Remove(key);
            var all = new MemoryStream(inc.Bytes);
            foreach (var p in inc.Parts) all.Write(p, 0, p.Length);
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, Safe(id) + Path.GetExtension(fileName));
            File.WriteAllBytes(path, all.ToArray());
            if (ext == ".txt")
            {
                Received++;
                LastPath = path;
                Plugin.Log.LogInfo($"IFZCOOP Fehlerbericht von {fromName} gespeichert: {path}");
                Notice($"Fehlerbericht von {fromName} gespeichert");
            }
        }

        // ================================================================ Hilfen

        private static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(keine Beschreibung)";
            s = s.Trim();
            int n = s.IndexOfAny(new[] { '\r', '\n' });
            if (n >= 0) s = s.Substring(0, n);
            return s.Length > 120 ? s.Substring(0, 120) + " …" : s;
        }

        private static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            string r = sb.ToString();
            return r.Length == 0 ? "x" : (r.Length > 80 ? r.Substring(0, 80) : r);
        }

        /// <summary>Letzte Zeilen einer Datei, die das Spiel gerade beschreibt (nur das Ende lesen).</summary>
        private static string Tail(string path, int lines)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return "(nicht gefunden: " + path + ")";
            const int maxBytes = 1024 * 1024;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - maxBytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                var all = Encoding.UTF8.GetString(buf, 0, read).Replace("\r\n", "\n").Split('\n');
                int from = Math.Max(0, all.Length - lines);
                return string.Join("\n", all, from, all.Length - from);
            }
        }
    }
}
