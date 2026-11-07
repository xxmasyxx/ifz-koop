using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace IfzCoop.Updater
{
    /// <summary>
    /// Preloader-Patcher: läuft beim Spielstart, bevor BepInEx die Mods lädt. Holt
    /// https://github.com/&lt;Repo&gt;/releases/latest/download/version.txt und, wenn dort eine neuere Version steht,
    /// IfzCoop.dll und IfzCoop.Updater.dll (SHA-256 aus version.txt geprüft). Ohne Internet oder bei Fehlern
    /// passiert nichts, das Spiel startet mit der installierten Version.
    /// Abschalten: BepInEx\config\ifzcoop-update.cfg (AutoUpdate = false) oder Startoption -ifzcoop-noupdate.
    /// </summary>
    public static class UpdaterPatcher
    {
        public const string DefaultRepo = "xxmasyxx/ifz-koop";

        // BepInEx-Patcher-Vertrag: wir patchen keine Spiel-DLL, wir brauchen nur den frühen Startzeitpunkt.
        public static IEnumerable<string> TargetDLLs { get { yield break; } }
        public static void Patch(AssemblyDefinition assembly) { }

        private static ManualLogSource _log;

        public static void Initialize()
        {
            _log = Logger.CreateLogSource("IFZ Koop Update");
            try
            {
                Run();
            }
            catch (Exception e)
            {
                _log.LogWarning("Update übersprungen: " + e.Message);
            }
        }

        private static string CacheDir => Path.Combine(Paths.BepInExRootPath, "cache");
        public static string NoteFile => Path.Combine(CacheDir, "ifzcoop-updated.txt");

        private static void Run()
        {
            string pluginDll = Path.Combine(Path.Combine(Paths.PluginPath, "IfzCoop"), "IfzCoop.dll");
            string selfDll = typeof(UpdaterPatcher).Assembly.Location;
            CleanOld(selfDll);

            var cfg = ReadConfig();
            if (!cfg.Enabled)
            {
                _log.LogInfo("Automatische Updates sind aus (ifzcoop-update.cfg).");
                return;
            }
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "-ifzcoop-noupdate", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInfo("Update aus (Startoption -ifzcoop-noupdate).");
                    return;
                }
            if (string.IsNullOrEmpty(cfg.Url) && (string.IsNullOrEmpty(cfg.Repo) || cfg.Repo.Contains("__")))
                return;

            Version local = LocalVersion(pluginDll);
            Directory.CreateDirectory(CacheDir);
            string baseUrl = !string.IsNullOrEmpty(cfg.Url)
                ? cfg.Url.TrimEnd('/') + "/" // nur zum Testen: eigener Server statt GitHub
                : "https://github.com/" + cfg.Repo + "/releases/latest/download/";
            string verFile = Path.Combine(CacheDir, "ifzcoop-version.txt");
            if (!Download(baseUrl + "version.txt", verFile, 8))
            {
                _log.LogInfo("Keine Verbindung zu GitHub, starte mit " + local + ".");
                return;
            }
            var remote = ParseKeyValues(File.ReadAllText(verFile));
            File.Delete(verFile);
            if (!remote.TryGetValue("version", out var rv) || !Version.TryParse(rv, out var remoteVersion))
            {
                _log.LogWarning("version.txt ohne gültige Version.");
                return;
            }
            if (remoteVersion <= local)
            {
                _log.LogInfo("IFZ Koop " + local + " ist aktuell (GitHub: " + remoteVersion + ").");
                return;
            }

            _log.LogMessage("Neue Version " + remoteVersion + " gefunden (installiert: " + local + "), lade ...");
            string newPlugin = Fetch(baseUrl, "IfzCoop.dll", remote);
            if (newPlugin == null)
                return;
            string newSelf = Fetch(baseUrl, "IfzCoop.Updater.dll", remote); // darf fehlen

            Directory.CreateDirectory(Path.GetDirectoryName(pluginDll));
            if (File.Exists(pluginDll))
                File.Copy(pluginDll, pluginDll + ".bak", true);
            File.Copy(newPlugin, pluginDll, true);
            File.Delete(newPlugin);

            if (newSelf != null)
            {
                try
                {
                    if (!string.Equals(Sha256(newSelf), Sha256(selfDll), StringComparison.OrdinalIgnoreCase))
                    {
                        // Die eigene DLL ist geladen: umbenennen geht unter Windows, überschreiben nicht.
                        string old = selfDll + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(selfDll, old);
                        File.Move(newSelf, selfDll);
                    }
                    else File.Delete(newSelf);
                }
                catch (Exception e)
                {
                    _log.LogWarning("Updater selbst nicht ersetzt (" + e.Message + "), nächstes Mal.");
                }
            }

            File.WriteAllText(NoteFile, local + "|" + remoteVersion);
            _log.LogMessage("IFZ Koop auf " + remoteVersion + " aktualisiert.");
        }

        private static string Fetch(string baseUrl, string name, Dictionary<string, string> remote)
        {
            if (!remote.TryGetValue(name, out var want))
            {
                if (name == "IfzCoop.dll") _log.LogWarning("version.txt nennt keine Prüfsumme für " + name + ".");
                return null;
            }
            string tmp = Path.Combine(CacheDir, name + ".download");
            if (!Download(baseUrl + name, tmp, 60))
            {
                _log.LogWarning("Download von " + name + " fehlgeschlagen.");
                return null;
            }
            string got = Sha256(tmp);
            if (!string.Equals(got, want.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(name + ": Prüfsumme falsch (" + got + "), nicht installiert.");
                File.Delete(tmp);
                return null;
            }
            return tmp;
        }

        private static void CleanOld(string selfDll)
        {
            try { if (File.Exists(selfDll + ".old")) File.Delete(selfDll + ".old"); }
            catch { /* noch gesperrt: beim nächsten Start */ }
        }

        private static Version LocalVersion(string dll)
        {
            if (!File.Exists(dll))
                return new Version(0, 0, 0, 0);
            try
            {
                return AssemblyName.GetAssemblyName(dll).Version;
            }
            catch
            {
                return new Version(0, 0, 0, 0);
            }
        }

        private struct Config
        {
            public bool Enabled;
            public string Repo;
            public string Url;
        }

        private static Config ReadConfig()
        {
            string path = Path.Combine(Paths.ConfigPath, "ifzcoop-update.cfg");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Paths.ConfigPath);
                File.WriteAllText(path,
                    "# IFZ Koop: automatisches Update beim Spielstart (neueste Version von GitHub).\r\n" +
                    "# AutoUpdate = false schaltet es aus.\r\n" +
                    "AutoUpdate = true\r\n" +
                    "Repo = " + DefaultRepo + "\r\n");
            }
            var kv = ParseKeyValues(File.ReadAllText(path));
            var c = new Config { Enabled = true, Repo = DefaultRepo };
            if (kv.TryGetValue("AutoUpdate", out var on))
                c.Enabled = !on.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) && on.Trim() != "0";
            if (kv.TryGetValue("Repo", out var repo) && !string.IsNullOrWhiteSpace(repo))
                c.Repo = repo.Trim();
            if (kv.TryGetValue("Url", out var url))
                c.Url = url.Trim();
            if (c.Repo.Contains("__"))
                c.Repo = DefaultRepo; // alte Datei ohne echten Namen
            return c;
        }

        private static Dictionary<string, string> ParseKeyValues(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            return d;
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
            {
                var sb = new StringBuilder();
                foreach (var b in sha.ComputeHash(f)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Lädt mit dem curl von Windows (TLS über Windows, unabhängig von Mono). Fallback: WebClient.
        /// </summary>
        private static bool Download(string url, string outFile, int maxSeconds)
        {
            if (File.Exists(outFile)) File.Delete(outFile);
            string root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            string curl = Path.Combine(Path.Combine(root, "System32"), "curl.exe");
            if (File.Exists(curl))
            {
                try
                {
                    var psi = new ProcessStartInfo(curl,
                        "-sSfL --connect-timeout 4 --max-time " + maxSeconds + " -A IfzCoop-Updater -o \"" + outFile + "\" \"" + url + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        p.StandardOutput.ReadToEnd();
                        p.StandardError.ReadToEnd();
                        if (!p.WaitForExit((maxSeconds + 5) * 1000))
                        {
                            try { p.Kill(); } catch { }
                            return false;
                        }
                        return p.ExitCode == 0 && File.Exists(outFile) && new FileInfo(outFile).Length > 0;
                    }
                }
                catch (Exception e)
                {
                    _log.LogInfo("curl nicht nutzbar: " + e.Message);
                }
            }
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "IfzCoop-Updater";
                    wc.DownloadFile(url, outFile);
                }
                return File.Exists(outFile) && new FileInfo(outFile).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
