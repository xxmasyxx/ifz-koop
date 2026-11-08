using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IfzCoop.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try
            {
                string self = Assembly.GetExecutingAssembly().Location;
                if (File.Exists(self + ".old")) File.Delete(self + ".old");
            }
            catch { }
            // Testmodus ohne Fenster: --qa <Ausgabedatei> <Spielordner> [Version] [noself]
            var args = Environment.GetCommandLineArgs();
            if (args.Length >= 4 && args[1] == "--qa-remove")
            {
                string res;
                try { res = Game.RemoveMod(args[3]) + " installed=" + Game.Installed(args[3]); }
                catch (Exception e) { res = "FEHLER " + e.Message; }
                File.WriteAllText(args[2], res);
                return;
            }
            if (args.Length >= 3 && args[1] == "--shot")
            {
                // Bild des Fensters, ohne es anzuzeigen (für Tests)
                using (var f = new LauncherForm())
                {
                    f.PrepareForShot();
                    using (var bmp = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        // Kinder einzeln zeichnen (bei nie gezeigtem Fenster malt DrawToBitmap sie nicht mit)
                        var origin = f.PointToScreen(Point.Empty);
                        int bx = origin.X - f.Left, by = origin.Y - f.Top;
                        foreach (Control c in f.Controls)
                        {
                            using (var cb = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height)))
                            {
                                c.DrawToBitmap(cb, new Rectangle(0, 0, c.Width, c.Height));
                                using (var g = Graphics.FromImage(bmp)) g.DrawImage(cb, bx + c.Left, by + c.Top);
                            }
                        }
                        bmp.Save(args[2], System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                return;
            }
            if (args.Length >= 4 && args[1] == "--qa")
            {
                var log = new StringBuilder();
                try
                {
                    var rels = GitHub.Releases();
                    log.AppendLine("releases=" + string.Join(",", rels.Select(r => r.Tag)));
                    log.AppendLine("found=" + (Game.FindGameDir() ?? "-"));
                    log.AppendLine("installed=" + Game.Installed(args[3]));
                    if (args.Length >= 5)
                    {
                        var r = rels.First(x => x.Tag == "v" + args[4] || x.Tag == args[4]);
                        bool newest = r == rels[0] && !(args.Length >= 6 && args[5] == "noself");
                        log.AppendLine("result=" + LauncherForm.InstallFiles(args[3], r, newest));
                        Game.SetAutoUpdate(args[3], r == rels[0]);
                        log.AppendLine("installed=" + Game.Installed(args[3]) + " auto=" + Game.AutoUpdate(args[3]));
                    }
                }
                catch (Exception e) { log.AppendLine("FEHLER " + e); }
                File.WriteAllText(args[2], log.ToString());
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm());
        }
    }

    // ===================================================================== GitHub-Releases

    internal sealed class Release
    {
        public string Tag;
        public Version Ver;
        public string Title;
        public string Notes;
        public DateTime Date;
        public bool Newest;
        public bool Installed;

        public override string ToString()
        {
            string s = Ver + "   (" + Date.ToString("dd.MM.yyyy") + ")";
            if (Newest) s += "   neueste";
            if (Installed) s += "   installiert";
            return s;
        }
    }

    internal static class GitHub
    {
        public const string Repo = "xxmasyxx/ifz-koop";

        private static WebClient Client()
        {
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "IfzCoop-Launcher";
            return wc;
        }

        public static string GetString(string url)
        {
            using (var wc = Client()) return wc.DownloadString(url);
        }

        public static void Download(string url, string file)
        {
            using (var wc = Client()) wc.DownloadFile(url, file);
        }

        /// <summary>Tag -> (Dateiname -> API-Adresse der Datei). Kommt aus der Release-Liste.</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> _apiAssets =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Release-Datei laden: erst der normale Link, bei Fehler (GitHub gab zeitweise 504 auf releases/download)
        /// über die API-Adresse der Datei (Accept: application/octet-stream).
        /// </summary>
        public static void DownloadAsset(string tag, string name, string file)
        {
            try { Download(AssetUrl(tag, name), file); return; }
            catch (WebException) when (ApiUrl(tag, name) != null) { }
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.Accept] = "application/octet-stream";
                wc.DownloadFile(ApiUrl(tag, name), file);
            }
        }

        public static string GetAssetString(string tag, string name)
        {
            try { return GetString(AssetUrl(tag, name)); }
            catch (WebException) when (ApiUrl(tag, name) != null) { }
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.Accept] = "application/octet-stream";
                return wc.DownloadString(ApiUrl(tag, name));
            }
        }

        private static string ApiUrl(string tag, string name)
        {
            if (!_apiAssets.ContainsKey(tag))
            {
                // Release noch nicht gelesen: einzeln von der API holen
                try
                {
                    var d = new JavaScriptSerializer().DeserializeObject(GetString("https://api.github.com/repos/" + Repo + "/releases/tags/" + tag)) as Dictionary<string, object>;
                    if (d != null) RememberAssets(tag, d);
                }
                catch { _apiAssets[tag] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
            }
            return _apiAssets.TryGetValue(tag, out var m) && m.TryGetValue(name, out var u) ? u : null;
        }

        private static void RememberAssets(string tag, Dictionary<string, object> release)
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (release.TryGetValue("assets", out var assets) && assets is object[] aa)
                foreach (var a in aa)
                    if (a is Dictionary<string, object> ad && ad.TryGetValue("name", out var n) && n is string ns
                        && ad.TryGetValue("url", out var u) && u is string us)
                        m[ns] = us;
            _apiAssets[tag] = m;
        }

        /// <summary>Alle Releases mit version.txt (ab 0.23.1), neueste zuerst.</summary>
        public static List<Release> Releases()
        {
            var json = GetString("https://api.github.com/repos/" + Repo + "/releases?per_page=60");
            var arr = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as object[];
            var list = new List<Release>();
            if (arr == null) return list;
            foreach (var o in arr)
            {
                if (!(o is Dictionary<string, object> d)) continue;
                if (d.TryGetValue("draft", out var dr) && dr is bool isDraft && isDraft) continue;
                string tag = d.TryGetValue("tag_name", out var t) ? t as string : null;
                if (string.IsNullOrEmpty(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) continue;
                RememberAssets(tag, d);
                bool hasVersionTxt = false;
                if (d.TryGetValue("assets", out var assets) && assets is object[] aa)
                    foreach (var a in aa)
                        if (a is Dictionary<string, object> ad && ad.TryGetValue("name", out var n) && (n as string) == "version.txt")
                            hasVersionTxt = true;
                if (!hasVersionTxt) continue;
                DateTime date = DateTime.MinValue;
                if (d.TryGetValue("published_at", out var p) && p is string ps) DateTime.TryParse(ps, out date);
                list.Add(new Release
                {
                    Tag = tag,
                    Ver = ver,
                    Title = d.TryGetValue("name", out var nm) ? nm as string : tag,
                    Notes = d.TryGetValue("body", out var b) ? b as string : "",
                    Date = date,
                });
            }
            list.Sort((x, y) => y.Ver.CompareTo(x.Ver));
            if (list.Count > 0) list[0].Newest = true;
            return list;
        }

        public static Dictionary<string, string> VersionTxt(string tag)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in GetAssetString(tag, "version.txt").Split('\n'))
            {
                int i = raw.IndexOf('=');
                if (i > 0) d[raw.Substring(0, i).Trim()] = raw.Substring(i + 1).Trim();
            }
            return d;
        }

        public static string AssetUrl(string tag, string name) => "https://github.com/" + Repo + "/releases/download/" + tag + "/" + name;
    }

    // ===================================================================== Spiel finden, Dateien

    internal static class Game
    {
        public const string AppId = "1465460";

        public static string FindGameDir()
        {
            var libs = new List<string>();
            string steam = null;
            foreach (var (root, key, name) in new[]
            {
                (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            })
            {
                try
                {
                    using (var k = root.OpenSubKey(key))
                        if (k?.GetValue(name) is string v && Directory.Exists(v)) { steam = v.Replace('/', '\\'); break; }
                }
                catch { }
            }
            if (steam == null) return null;
            libs.Add(steam);
            string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    string p = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (!libs.Contains(p, StringComparer.OrdinalIgnoreCase)) libs.Add(p);
                }
            foreach (var lib in libs)
            {
                string acf = Path.Combine(lib, @"steamapps\appmanifest_" + AppId + ".acf");
                if (!File.Exists(acf)) continue;
                var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                if (!m.Success) continue;
                string dir = Path.Combine(lib, @"steamapps\common\" + m.Groups[1].Value);
                if (File.Exists(Path.Combine(dir, "Infection Free Zone.exe"))) return dir;
            }
            return null;
        }

        public static bool IsRunning() => Process.GetProcessesByName("Infection Free Zone").Length > 0;

        public static string PluginDll(string dir) => Path.Combine(dir, @"BepInEx\plugins\IfzCoop\IfzCoop.dll");
        public static string UpdaterDll(string dir) => Path.Combine(dir, @"BepInEx\patchers\IfzCoop\IfzCoop.Updater.dll");
        public static bool HasBepInEx(string dir) => File.Exists(Path.Combine(dir, @"BepInEx\core\BepInEx.dll")) && File.Exists(Path.Combine(dir, "winhttp.dll"));

        public static Version Installed(string dir)
        {
            try { return File.Exists(PluginDll(dir)) ? AssemblyName.GetAssemblyName(PluginDll(dir)).Version : null; }
            catch { return null; }
        }

        private static string Cfg(string dir) => Path.Combine(dir, @"BepInEx\config\ifzcoop-update.cfg");

        public static bool AutoUpdate(string dir)
        {
            try
            {
                if (!File.Exists(Cfg(dir))) return true;
                var m = Regex.Match(File.ReadAllText(Cfg(dir)), @"(?m)^\s*AutoUpdate\s*=\s*(\S+)");
                return !m.Success || !(m.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase) || m.Groups[1].Value == "0");
            }
            catch { return true; }
        }

        public static void SetAutoUpdate(string dir, bool on)
        {
            string path = Cfg(dir);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string text = File.Exists(path) ? File.ReadAllText(path)
                : "# IFZ Koop: automatisches Update beim Spielstart (neueste Version von GitHub).\r\n# AutoUpdate = false schaltet es aus.\r\nAutoUpdate = true\r\nRepo = " + GitHub.Repo + "\r\n";
            string val = on ? "true" : "false";
            if (Regex.IsMatch(text, @"(?m)^\s*AutoUpdate\s*="))
                text = Regex.Replace(text, @"(?m)^(\s*AutoUpdate\s*=\s*)\S*", "${1}" + val);
            else
                text += "\r\nAutoUpdate = " + val + "\r\n";
            File.WriteAllText(path, text);
        }

        /// <summary>Mod-Lader (Doorstop) an/aus. Aus = das Spiel startet ohne BepInEx.</summary>
        public static void SetLoader(string dir, bool on)
        {
            string ini = Path.Combine(dir, "doorstop_config.ini");
            if (!File.Exists(ini)) return;
            string text = File.ReadAllText(ini);
            File.WriteAllText(ini, Regex.Replace(text, @"(?m)^(\s*enabled\s*=\s*)(true|false)", "${1}" + (on ? "true" : "false")));
        }

        /// <summary>
        /// Mod aus dem Spielordner entfernen: IfzCoop, Updater, Update-Notizen. BepInEx bleibt, wird aber ausgeschaltet,
        /// wenn keine anderen Mods da sind (dann startet das Spiel wie ohne Mods). Spielstände bleiben.
        /// </summary>
        public static string RemoveMod(string dir)
        {
            var removed = new List<string>();
            foreach (var sub in new[] { @"BepInEx\plugins\IfzCoop", @"BepInEx\patchers\IfzCoop" })
            {
                string p = Path.Combine(dir, sub);
                if (!Directory.Exists(p)) continue;
                Exception last = null;
                for (int i = 0; i < 10; i++)
                {
                    try { Directory.Delete(p, true); last = null; break; }
                    catch (Exception e) { last = e; System.Threading.Thread.Sleep(1000); }
                }
                if (last != null) throw new Exception("Konnte " + sub + " nicht löschen: " + last.Message);
                removed.Add(sub);
            }
            foreach (var f in new[] { @"BepInEx\cache\ifzcoop-updated.txt", @"BepInEx\cache\ifzcoop-skipupdate-once", @"BepInEx\cache\ifzcoop-version.txt" })
                try { File.Delete(Path.Combine(dir, f)); } catch { }

            string plugins = Path.Combine(dir, @"BepInEx\plugins");
            bool others = Directory.Exists(plugins) && Directory.EnumerateFileSystemEntries(plugins).Any();
            string loader = "";
            if (!others && File.Exists(Path.Combine(dir, "doorstop_config.ini")))
            {
                SetLoader(dir, false);
                loader = " Mod-Lader ausgeschaltet, das Spiel startet jetzt ohne Mods.";
            }
            else if (others)
                loader = " BepInEx bleibt an (andere Mods vorhanden).";
            return (removed.Count > 0 ? "Mod entfernt." : "Die Mod war nicht installiert.") + loader;
        }

        public static void SkipUpdateOnce(string dir)
        {
            string cache = Path.Combine(dir, @"BepInEx\cache");
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "ifzcoop-skipupdate-once"), DateTime.Now.ToString("s"));
        }

        public static void Start()
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/" + AppId) { UseShellExecute = true });
        }

        public static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(file))
                return string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2")));
        }
    }

    // ===================================================================== Fenster

    internal sealed class LauncherForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(17, 20, 23);
        private static readonly Color Panel = Color.FromArgb(29, 34, 38);
        private static readonly Color Green = Color.FromArgb(74, 252, 125);
        private static readonly Color Text1 = Color.FromArgb(222, 222, 222);
        private static readonly Color Text2 = Color.FromArgb(150, 156, 160);

        private readonly Label _info = new Label();
        private readonly Label _status = new Label();
        private readonly Button _playNewest = new Button();
        private readonly Button _play = new Button();
        private readonly ComboBox _versions = new ComboBox();
        private readonly Button _install = new Button();
        private readonly TextBox _notes = new TextBox();
        private readonly CheckBox _auto = new CheckBox();
        private readonly CheckBox _removeAfter = new CheckBox();
        private readonly Button _removeNow = new Button();
        private System.Threading.CancellationTokenSource _wait;
        private bool _loadingAuto;

        private string _dir;
        private List<Release> _releases = new List<Release>();
        private bool _busy;

        public LauncherForm()
        {
            Text = "IFZ Koop";
            ClientSize = new Size(640, 650);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Text1;
            Font = new Font("Segoe UI", 10f);
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

            var title = new Label { Text = "IFZ KOOP", Font = new Font("Segoe UI", 24f, FontStyle.Bold), ForeColor = Green, AutoSize = true, Location = new Point(24, 14) };
            var sub = new Label { Text = "Koop-Mod für Infection Free Zone", ForeColor = Text2, AutoSize = true, Location = new Point(28, 60) };
            _info.SetBounds(28, 92, 590, 48);
            _info.ForeColor = Text1;

            StyleButton(_playNewest, Green, Color.Black, 13f, FontStyle.Bold);
            _playNewest.Text = "▶   NEUESTE VERSION HOLEN UND SPIELEN";
            _playNewest.SetBounds(24, 148, 592, 54);
            _playNewest.Click += async (s, e) => await PlayNewest();

            StyleButton(_play, Panel, Text1, 10.5f, FontStyle.Regular);
            _play.Text = "Spielen ohne Update (installierte Version)";
            _play.SetBounds(24, 210, 592, 38);
            _play.Click += (s, e) => PlayWithout();

            var other = new Label { Text = "ANDERE VERSION WÄHLEN", Font = new Font("Segoe UI", 9f, FontStyle.Bold), ForeColor = Text2, AutoSize = true, Location = new Point(26, 266) };
            _versions.DropDownStyle = ComboBoxStyle.DropDownList;
            _versions.FlatStyle = FlatStyle.Flat;
            _versions.BackColor = Panel;
            _versions.ForeColor = Text1;
            _versions.SetBounds(24, 290, 420, 30);
            _versions.SelectedIndexChanged += (s, e) => ShowNotes();
            _versions.DrawMode = DrawMode.OwnerDrawFixed;
            _versions.ItemHeight = 22;
            _versions.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var bg = new SolidBrush(hot ? Color.FromArgb(45, 80, 55) : Panel))
                    e.Graphics.FillRectangle(bg, e.Bounds);
                var r = _versions.Items[e.Index] as Release;
                TextRenderer.DrawText(e.Graphics, r?.ToString() ?? "", _versions.Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height),
                    r != null && r.Newest ? Green : Text1, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            StyleButton(_install, Panel, Green, 10f, FontStyle.Bold);
            _install.Text = "Installieren";
            _install.SetBounds(454, 289, 162, 32);
            _install.Click += async (s, e) => await InstallSelected();

            _notes.Multiline = true;
            _notes.ReadOnly = true;
            _notes.ScrollBars = ScrollBars.Vertical;
            _notes.BorderStyle = BorderStyle.None;
            _notes.BackColor = Panel;
            _notes.ForeColor = Text1;
            _notes.SetBounds(24, 330, 592, 150);

            _auto.Text = "Beim Spielstart automatisch die neueste Version holen";
            _auto.ForeColor = Text1;
            _auto.AutoSize = true;
            _auto.Location = new Point(26, 492);
            _auto.CheckedChanged += (s, e) =>
            {
                if (_loadingAuto || _dir == null) return;
                try { Game.SetAutoUpdate(_dir, _auto.Checked); SetStatus(_auto.Checked ? "Auto-Update ist an." : "Auto-Update ist aus: das Spiel bleibt auf der installierten Version."); }
                catch (Exception ex) { SetStatus("Einstellung nicht gespeichert: " + ex.Message, true); }
            };

            _removeAfter.Text = "Nach dem Spielen die Mod wieder entfernen (Launcher bleibt offen und wartet)";
            _removeAfter.ForeColor = Text1;
            _removeAfter.AutoSize = true;
            _removeAfter.Location = new Point(26, 518);
            _removeAfter.CheckedChanged += (s, e) =>
            {
                if (!_removeAfter.Checked && _wait != null) _wait.Cancel();
            };

            _status.SetBounds(28, 550, 590, 42);
            _status.ForeColor = Text2;

            var link = new LinkLabel { Text = "Projekt auf GitHub", AutoSize = true, Location = new Point(26, 612), LinkColor = Green, ActiveLinkColor = Color.White };
            link.LinkClicked += (s, e) => Process.Start(new ProcessStartInfo("https://github.com/" + GitHub.Repo) { UseShellExecute = true });

            StyleButton(_removeNow, Panel, Color.FromArgb(255, 140, 130), 9.5f, FontStyle.Regular);
            _removeNow.Text = "Mod jetzt entfernen";
            _removeNow.SetBounds(456, 604, 160, 30);
            _removeNow.Click += async (s, e) => await RemoveNow();

            FormClosing += (s, e) =>
            {
                if (_wait == null || _wait.IsCancellationRequested) return;
                if (MessageBox.Show(this, "Der Launcher wartet noch auf das Spielende, um die Mod zu entfernen.\r\nWenn du ihn jetzt schließt, bleibt die Mod installiert. Trotzdem schließen?",
                        "IFZ Koop", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    e.Cancel = true;
                else _wait.Cancel();
            };

            Controls.AddRange(new Control[] { title, sub, _info, _playNewest, _play, other, _versions, _install, _notes, _auto, _removeAfter, _status, link, _removeNow });
            Shown += async (s, e) => await LoadAll();
        }

        public void PrepareForShot()
        {
            var h = Handle;
            foreach (Control c in Controls) { var ch = c.Handle; }
            _dir = Game.FindGameDir();
            try { _releases = GitHub.Releases(); } catch { }
            RefreshInfo();
            SetBusy(false);
            SetStatus("Bereit.");
        }

        private static void StyleButton(Button b, Color back, Color fore, float size, FontStyle style)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(70, 80, 86);
            b.FlatAppearance.BorderSize = 1;
            b.BackColor = back;
            b.ForeColor = fore;
            b.Font = new Font("Segoe UI", size, style);
            b.Cursor = Cursors.Hand;
        }

        private void SetStatus(string text, bool error = false)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, error))); return; }
            _status.ForeColor = error ? Color.FromArgb(255, 120, 110) : Text2;
            _status.Text = text;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            bool ok = !busy && _dir != null;
            _playNewest.Enabled = ok && _releases.Count > 0;
            _play.Enabled = ok && Game.Installed(_dir) != null;
            _install.Enabled = ok && _versions.SelectedItem != null;
            _versions.Enabled = !busy;
            _auto.Enabled = ok;
            _removeNow.Enabled = ok && Game.Installed(_dir) != null;
            UseWaitCursor = busy && _wait == null;
        }

        private void RefreshInfo()
        {
            if (_dir == null)
            {
                _info.Text = "Infection Free Zone wurde in Steam nicht gefunden.";
                return;
            }
            var inst = Game.Installed(_dir);
            var newest = _releases.FirstOrDefault();
            _info.Text = "Spiel: " + _dir + "\r\n"
                + "Installiert: " + (inst != null ? Short(inst) : "noch nicht") + "      Neueste: " + (newest != null ? Short(newest.Ver) : "unbekannt");
            foreach (var r in _releases) r.Installed = inst != null && Short(inst) == Short(r.Ver);
            _loadingAuto = true;
            _auto.Checked = Game.AutoUpdate(_dir);
            _loadingAuto = false;
            object sel = _versions.SelectedItem;
            _versions.Items.Clear();
            foreach (var r in _releases) _versions.Items.Add(r);
            if (sel != null && _versions.Items.Contains(sel)) _versions.SelectedItem = sel;
            else if (_versions.Items.Count > 0) _versions.SelectedIndex = 0;
        }

        private static string Short(Version v) => v.Build >= 0 ? v.Major + "." + v.Minor + "." + v.Build : v.ToString();

        private void ShowNotes()
        {
            var r = _versions.SelectedItem as Release;
            _notes.Text = r == null ? "" : (r.Notes ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n").Replace("**", "");
            _install.Enabled = !_busy && _dir != null && r != null;
        }

        private async Task LoadAll()
        {
            SetBusy(true);
            SetStatus("Suche das Spiel und die Versionen auf GitHub ...");
            _dir = await Task.Run(() => Game.FindGameDir());
            try
            {
                _releases = await Task.Run(() => GitHub.Releases());
                SetStatus(Game.IsRunning() ? "Das Spiel läuft gerade. Zum Aktualisieren erst beenden." : "Bereit.");
            }
            catch (Exception ex)
            {
                SetStatus("GitHub nicht erreichbar (" + ex.Message + "). Spielen ohne Update geht trotzdem.", true);
            }
            RefreshInfo();
            SetBusy(false);
        }

        private bool CheckNotRunning()
        {
            if (!Game.IsRunning()) return true;
            MessageBox.Show(this, "Infection Free Zone läuft gerade.\r\nBitte erst das Spiel beenden, dann nochmal versuchen.", "IFZ Koop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        private async Task PlayNewest()
        {
            if (_dir == null || _releases.Count == 0 || !CheckNotRunning()) return;
            var newest = _releases[0];
            var inst = Game.Installed(_dir);
            bool need = inst == null || Short(inst) != Short(newest.Ver) || !File.Exists(Game.UpdaterDll(_dir));
            if (need && !await Install(newest)) return;
            try { Game.SetAutoUpdate(_dir, _auto.Checked); } catch { }
            StartAndClose("Version " + Short(newest.Ver) + " ist drauf. Starte das Spiel ...");
        }

        private void PlayWithout()
        {
            if (_dir == null) return;
            if (Game.IsRunning()) { SetStatus("Das Spiel läuft schon."); return; }
            try { if (Game.AutoUpdate(_dir)) Game.SkipUpdateOnce(_dir); } catch { }
            StartAndClose("Starte mit der installierten Version " + Short(Game.Installed(_dir)) + " ...");
        }

        private async Task InstallSelected()
        {
            var r = _versions.SelectedItem as Release;
            if (r == null || _dir == null || !CheckNotRunning()) return;
            bool newest = r == _releases.FirstOrDefault();
            if (!newest)
            {
                var ans = MessageBox.Show(this,
                    "Version " + Short(r.Ver) + " installieren?\r\n\r\nDamit das Spiel nicht gleich wieder auf die neueste Version springt, wird das automatische Update ausgeschaltet. "
                    + "Mit \"Neueste Version holen und spielen\" geht es jederzeit zurück.\r\n\r\nAlle Mitspieler brauchen eine passende Version.",
                    "IFZ Koop", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (ans != DialogResult.OK) return;
            }
            if (!await Install(r)) return;
            try { Game.SetAutoUpdate(_dir, newest); } catch { }
            RefreshInfo();
            if (MessageBox.Show(this, "Version " + Short(r.Ver) + " ist installiert. Jetzt spielen?", "IFZ Koop", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                StartAndClose("Starte das Spiel ...");
        }

        private void StartAndClose(string msg)
        {
            SetStatus(msg);
            try { Game.Start(); }
            catch (Exception ex) { SetStatus("Steam ließ sich nicht starten: " + ex.Message, true); return; }
            if (_removeAfter.Checked)
            {
                _ = WaitAndRemove();
                return;
            }
            var t = new Timer { Interval = 2500 };
            t.Tick += (s, e) => { t.Stop(); Close(); };
            t.Start();
        }

        /// <summary>Wartet, bis das Spiel gestartet und wieder beendet ist, dann Mod entfernen.</summary>
        private async Task WaitAndRemove()
        {
            _wait = new System.Threading.CancellationTokenSource();
            var ct = _wait.Token;
            SetBusy(true);
            _removeAfter.Enabled = true; // Haken weg = Warten abbrechen
            SetStatus("Warte auf den Spielstart ...");
            var t0 = DateTime.Now;
            bool seen = false;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    bool run = Game.IsRunning();
                    if (run && !seen)
                    {
                        seen = true;
                        SetStatus("Das Spiel läuft. Sobald du es beendest, entfernt der Launcher die Mod. Dieses Fenster bitte offen lassen.");
                        WindowState = FormWindowState.Minimized;
                    }
                    if (!run && seen) break;
                    if (!seen && (DateTime.Now - t0).TotalMinutes > 5)
                    {
                        SetStatus("Das Spiel ist nicht gestartet, die Mod bleibt installiert.", true);
                        return;
                    }
                    await Task.Delay(3000);
                }
                if (ct.IsCancellationRequested)
                {
                    SetStatus("Entfernen abgebrochen, die Mod bleibt installiert.");
                    return;
                }
                SetStatus("Spiel beendet, entferne die Mod ...");
                await Task.Delay(3000); // Dateien freigeben lassen
                string res = await Task.Run(() => Game.RemoveMod(_dir));
                SetStatus(res);
                WindowState = FormWindowState.Normal;
                Activate();
            }
            catch (Exception ex)
            {
                SetStatus("Entfernen hat nicht geklappt: " + ex.Message, true);
                WindowState = FormWindowState.Normal;
            }
            finally
            {
                _wait = null;
                RefreshInfo();
                SetBusy(false);
            }
        }

        private async Task RemoveNow()
        {
            if (_dir == null || !CheckNotRunning()) return;
            if (MessageBox.Show(this, "IFZ Koop aus dem Spiel entfernen?\r\n\r\nSpielstände bleiben. Mit \"Neueste Version holen und spielen\" ist die Mod jederzeit wieder da.",
                    "IFZ Koop", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            SetBusy(true);
            try { SetStatus(await Task.Run(() => Game.RemoveMod(_dir))); }
            catch (Exception ex) { SetStatus("Entfernen hat nicht geklappt: " + ex.Message, true); }
            finally { RefreshInfo(); SetBusy(false); }
        }

        /// <summary>Lädt die Dateien einer Version von GitHub, prüft SHA-256 und legt sie ins Spiel. Ohne BepInEx erst den Installer.</summary>
        private async Task<bool> Install(Release r)
        {
            SetBusy(true);
            try
            {
                if (!Game.HasBepInEx(_dir))
                {
                    SetStatus("BepInEx (Mod-Lader) fehlt noch, starte den Installer ...");
                    int rc = await Task.Run(() => RunInstaller(r, false));
                    if (rc == 2 && MessageBox.Show(this, "Diese Spielversion ist für den Mod nicht getestet. Trotzdem installieren?", "IFZ Koop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        rc = await Task.Run(() => RunInstaller(r, true));
                    if (!Game.HasBepInEx(_dir)) { SetStatus("Installation hat nicht geklappt (Installer-Fenster beachten).", true); return false; }
                }
                SetStatus("Lade Version " + Short(r.Ver) + " von GitHub ...");
                string result = await Task.Run(() => InstallFiles(r));
                SetStatus(result);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Fehler: " + ex.Message, true);
                return false;
            }
            finally
            {
                RefreshInfo();
                SetBusy(false);
            }
        }

        private int RunInstaller(Release r, bool force)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "IfzCoop-Launcher-Installer");
            Directory.CreateDirectory(tmp);
            string bat = Path.Combine(tmp, "IfzCoop-Installer.bat");
            GitHub.DownloadAsset(r.Tag, "IfzCoop-Installer.bat", bat);
            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\" install -Yes " + (force ? "-Force " : "") + "-GameDir \"" + _dir + "\"\"")
            {
                UseShellExecute = false,
                WorkingDirectory = tmp,
            };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        private string InstallFiles(Release r) => InstallFiles(_dir, r, r == _releases.FirstOrDefault());

        /// <summary>Dateien einer Version ins Spiel legen (auch für den Testmodus --qa).</summary>
        public static string InstallFiles(string _dir, Release r, bool newest)
        {
            var vt = GitHub.VersionTxt(r.Tag);
            string tmp = Path.Combine(Path.GetTempPath(), "IfzCoop-Launcher-Download");
            Directory.CreateDirectory(tmp);
            string Get(string name)
            {
                if (!vt.TryGetValue(name, out var want)) return null;
                string f = Path.Combine(tmp, name);
                if (File.Exists(f)) File.Delete(f);
                GitHub.DownloadAsset(r.Tag, name, f);
                if (!string.Equals(Game.Sha256(f), want, StringComparison.OrdinalIgnoreCase))
                    throw new Exception(name + ": Prüfsumme falsch, nichts installiert");
                return f;
            }

            string plugin = Get("IfzCoop.dll") ?? throw new Exception("Die Version enthält keine IfzCoop.dll");
            string updater = Get("IfzCoop.Updater.dll");

            string target = Game.PluginDll(_dir);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            Game.SetLoader(_dir, true); // falls die Mod vorher entfernt (Lader aus) war
            if (File.Exists(target)) File.Copy(target, target + ".bak", true);
            File.Copy(plugin, target, true);
            if (updater != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Game.UpdaterDll(_dir)));
                File.Copy(updater, Game.UpdaterDll(_dir), true);
            }

            // Launcher selbst erneuern (nur bei der neuesten Version): laufende exe umbenennen geht, überschreiben nicht
            string note = "";
            try
            {
                if (newest)
                {
                    string self = Assembly.GetExecutingAssembly().Location;
                    if (vt.TryGetValue("IfzCoop-Launcher.exe", out var want) && !string.Equals(Game.Sha256(self), want, StringComparison.OrdinalIgnoreCase))
                    {
                        string fresh = Get("IfzCoop-Launcher.exe");
                        if (File.Exists(self + ".old")) File.Delete(self + ".old");
                        File.Move(self, self + ".old");
                        File.Move(fresh, self);
                        note = " Launcher erneuert.";
                    }
                }
            }
            catch (Exception ex) { note = " (Launcher nicht erneuert: " + ex.Message + ")"; }
            return "Version " + Short(r.Ver) + " installiert (Prüfsummen stimmen)." + note;
        }
    }
}
