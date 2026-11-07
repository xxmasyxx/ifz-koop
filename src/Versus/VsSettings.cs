using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Gameplay.GameResources;

namespace IfzCoop.Versus
{
    internal enum VsMode : byte { FreeForAll = 0, Teams = 1, AllAllied = 2 }

    internal enum VsMissions : byte { Race = 0, OwnStory = 1, Both = 2 }

    /// <summary>
    /// Start-Einstellungen des Versus-Spiels (KONZEPT-VERSUS Abschnitt 3). Legt der Host fest, die Gäste sehen sie.
    /// Einwohner, Trupps und Rohstoffe setzt jeder Rechner nach seiner HQ-Wahl selbst (VsStart.ApplyStartConditions).
    /// Sichtbarkeit durchsuchter Häuser, Missionen, Sicht im Bündnis und Sieg werden hier schon geführt und übertragen;
    /// ausgewertet werden sie in den späteren Schritten (V5, V7, V10, V11).
    /// </summary>
    internal sealed class VsSettings
    {
        private const byte FormatVersion = 1;

        /// <summary>Wählbare Start-Rohstoffe (zusätzlich zur normalen Startausstattung des Spiels im HQ).</summary>
        public static readonly (string label, ResourceID id)[] ResourceChoices =
        {
            ("Rationen", ResourceID.res_food_rations),
            ("Konserven", ResourceID.res_cans),
            ("Holz", ResourceID.res_wood),
            ("Bretter", ResourceID.res_planks),
            ("Metall", ResourceID.res_metal),
            ("Ziegel", ResourceID.res_bricks),
            ("Treibstoff", ResourceID.res_fuel),
            ("Medizin", ResourceID.res_drugs),
            ("Munition", ResourceID.res_ammo),
            ("Pistolen", ResourceID.eq_pistol),
            ("Schrotflinten", ResourceID.eq_shotgun),
            ("Sturmgewehre", ResourceID.eq_assault_rifle),
            ("Schützer", ResourceID.eq_wear_protectors),
            ("Schutzausrüstung", ResourceID.eq_wear_riot_gear),
        };

        public static readonly int[] WorkerPresets = { 10, 20, 40 };

        public VsMode Mode = VsMode.FreeForAll;

        /// <summary>Arbeiter (Erwachsene ohne Trupp) nach der HQ-Wahl. 0 = wie im Spiel (keine Änderung).</summary>
        public int Workers = 20;

        public int Squads = 1;
        public int SquadSize = 4;

        /// <summary>Zusätzliche Rohstoffe ins Lager, je ResourceID.</summary>
        public readonly Dictionary<ResourceID, int> Resources = new Dictionary<ResourceID, int>();

        /// <summary>true = alle sehen, welche Häuser schon durchsucht sind.</summary>
        public bool ScavengedVisible = true;

        public VsMissions Missions = VsMissions.Both;

        /// <summary>true = Verbündete teilen die Sicht.</summary>
        public bool SharedAllySight = true;

        /// <summary>true = wer keine Bevölkerung mehr hat, ist raus; der Letzte gewinnt. false = ohne Sieg.</summary>
        public bool Victory = true;

        public int MaxPlayers = 4;

        /// <summary>Wird bei jeder Änderung hochgezählt (Gäste erkennen neue Stände, das Fenster zeigt sie).</summary>
        public int Revision;

        public VsSettings Clone()
        {
            var copy = new VsSettings();
            copy.CopyFrom(this);
            return copy;
        }

        public void CopyFrom(VsSettings o)
        {
            Mode = o.Mode;
            Workers = o.Workers;
            Squads = o.Squads;
            SquadSize = o.SquadSize;
            Resources.Clear();
            foreach (var kv in o.Resources)
                Resources[kv.Key] = kv.Value;
            ScavengedVisible = o.ScavengedVisible;
            Missions = o.Missions;
            SharedAllySight = o.SharedAllySight;
            Victory = o.Victory;
            MaxPlayers = o.MaxPlayers;
            Revision = o.Revision;
        }

        public void Clamp()
        {
            Workers = Math.Max(0, Math.Min(200, Workers));
            Squads = Math.Max(0, Math.Min(6, Squads));
            SquadSize = Math.Max(1, Math.Min(10, SquadSize));
            MaxPlayers = Math.Max(2, Math.Min(VsProtocol.MaxPlayers, MaxPlayers));
            var keys = new List<ResourceID>(Resources.Keys);
            foreach (var k in keys)
            {
                int v = Math.Max(0, Math.Min(100000, Resources[k]));
                if (v == 0) Resources.Remove(k);
                else Resources[k] = v;
            }
        }

        public int ResourceAmount(ResourceID id) => Resources.TryGetValue(id, out var v) ? v : 0;

        public void Write(BinaryWriter w)
        {
            w.Write(FormatVersion);
            w.Write(Revision);
            w.Write((byte)Mode);
            w.Write(Workers);
            w.Write(Squads);
            w.Write(SquadSize);
            w.Write((ushort)Resources.Count);
            foreach (var kv in Resources)
            {
                w.Write((int)kv.Key);
                w.Write(kv.Value);
            }
            w.Write(ScavengedVisible);
            w.Write((byte)Missions);
            w.Write(SharedAllySight);
            w.Write(Victory);
            w.Write((byte)MaxPlayers);
        }

        public static VsSettings Read(BinaryReader r)
        {
            byte version = r.ReadByte();
            if (version != FormatVersion)
                throw new InvalidDataException("Versus-Einstellungen Format " + version);
            var s = new VsSettings();
            s.Revision = r.ReadInt32();
            s.Mode = (VsMode)r.ReadByte();
            s.Workers = r.ReadInt32();
            s.Squads = r.ReadInt32();
            s.SquadSize = r.ReadInt32();
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                var id = (ResourceID)r.ReadInt32();
                s.Resources[id] = r.ReadInt32();
            }
            s.ScavengedVisible = r.ReadBoolean();
            s.Missions = (VsMissions)r.ReadByte();
            s.SharedAllySight = r.ReadBoolean();
            s.Victory = r.ReadBoolean();
            s.MaxPlayers = r.ReadByte();
            s.Clamp();
            return s;
        }

        /// <summary>Kurzform für Statuszeilen (bei Host und Gast gleich, solange die Gäste den Stand haben).</summary>
        public string Compact()
        {
            var sb = new StringBuilder();
            sb.Append("mode=").Append(Mode)
              .Append(" workers=").Append(Workers)
              .Append(" squads=").Append(Squads).Append('x').Append(SquadSize)
              .Append(" scavVisible=").Append(ScavengedVisible ? 1 : 0)
              .Append(" missions=").Append(Missions)
              .Append(" allySight=").Append(SharedAllySight ? "shared" : "separate")
              .Append(" victory=").Append(Victory ? 1 : 0)
              .Append(" max=").Append(MaxPlayers)
              .Append(" res=");
            var keys = new List<ResourceID>(Resources.Keys);
            keys.Sort((a, b) => ((int)a).CompareTo((int)b));
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(keys[i]).Append(':').Append(Resources[keys[i]]);
            }
            if (keys.Count == 0) sb.Append('-');
            return sb.ToString();
        }

        /// <summary>
        /// Testbefehl/Text: "key=value" setzen. Schlüssel: mode=ffa|teams|allied, workers=n, squads=n, squadsize=n,
        /// scav=0|1, missions=race|story|both, sight=shared|separate, victory=0|1, max=n, &lt;ResourceID&gt;=n.
        /// </summary>
        public bool TrySet(string key, string value, out string error)
        {
            error = null;
            var inv = CultureInfo.InvariantCulture;
            key = key.Trim();
            value = value.Trim();
            switch (key.ToLowerInvariant())
            {
                case "mode":
                    switch (value.ToLowerInvariant())
                    {
                        case "ffa": case "0": Mode = VsMode.FreeForAll; return true;
                        case "teams": case "1": Mode = VsMode.Teams; return true;
                        case "allied": case "2": Mode = VsMode.AllAllied; return true;
                    }
                    break;
                case "workers":
                    if (int.TryParse(value, NumberStyles.Integer, inv, out Workers)) return true;
                    break;
                case "squads":
                    if (int.TryParse(value, NumberStyles.Integer, inv, out Squads)) return true;
                    break;
                case "squadsize":
                    if (int.TryParse(value, NumberStyles.Integer, inv, out SquadSize)) return true;
                    break;
                case "scav":
                    ScavengedVisible = value == "1" || value.ToLowerInvariant() == "ja" || value.ToLowerInvariant() == "true";
                    return true;
                case "missions":
                    switch (value.ToLowerInvariant())
                    {
                        case "race": case "0": Missions = VsMissions.Race; return true;
                        case "story": case "1": Missions = VsMissions.OwnStory; return true;
                        case "both": case "2": Missions = VsMissions.Both; return true;
                    }
                    break;
                case "sight":
                    SharedAllySight = value.ToLowerInvariant() != "separate" && value != "0";
                    return true;
                case "victory":
                    Victory = value == "1" || value.ToLowerInvariant() == "true";
                    return true;
                case "max":
                    if (int.TryParse(value, NumberStyles.Integer, inv, out MaxPlayers)) return true;
                    break;
                default:
                    if (Enum.TryParse(key, out ResourceID id) && int.TryParse(value, NumberStyles.Integer, inv, out var amount))
                    {
                        Resources[id] = amount;
                        return true;
                    }
                    break;
            }
            error = $"unbekannt: {key}={value}";
            return false;
        }
    }
}
