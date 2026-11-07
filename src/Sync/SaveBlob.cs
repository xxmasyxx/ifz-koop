using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Packt einen Spielstand-Ordner (CompressedData.zip, info.json, Vorschaubild) in einen
    /// Byte-Block und wieder zurück. Format: int Anzahl, dann je Datei string Pfad, int Länge, Bytes.
    /// </summary>
    internal static class SaveBlob
    {
        private const int MaxFiles = 10000;
        private const int MaxTotalBytes = 512 * 1024 * 1024;

        public static byte[] Pack(string directory)
        {
            var root = Path.GetFullPath(directory);
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(files.Length);
                foreach (var file in files)
                {
                    var rel = file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                    var bytes = File.ReadAllBytes(file);
                    w.Write(rel);
                    w.Write(bytes.Length);
                    w.Write(bytes);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static void Unpack(byte[] blob, string targetDirectory)
        {
            var root = Path.GetFullPath(targetDirectory);
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            Directory.CreateDirectory(root);

            using (var r = new BinaryReader(new MemoryStream(blob, false), Encoding.UTF8))
            {
                int count = r.ReadInt32();
                if (count < 0 || count > MaxFiles)
                    throw new InvalidDataException("ungültige Dateianzahl: " + count);
                long total = 0;
                for (int i = 0; i < count; i++)
                {
                    var rel = r.ReadString();
                    int length = r.ReadInt32();
                    total += length;
                    if (length < 0 || total > MaxTotalBytes)
                        throw new InvalidDataException("ungültige Dateigröße");
                    var target = SafeCombine(root, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.WriteAllBytes(target, r.ReadBytes(length));
                }
            }
        }

        public static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(data);
        }

        public static bool SameHash(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>Verhindert Pfade wie "../x" oder "C:\x" aus der Gegenstelle.</summary>
        private static string SafeCombine(string root, string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(".."))
                throw new InvalidDataException("unzulässiger Pfad im Spielstand: " + relative);
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("unzulässiger Pfad im Spielstand: " + relative);
            return full;
        }
    }
}
