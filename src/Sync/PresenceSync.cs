using System.Collections.Generic;
using System.IO;
using Cameras;
using Controllers;
using Gameplay.Buildings;
using Gameplay.Rebuilding;
using Gameplay.Units;
using HarmonyLib;
using IfzCoop.Game;
using IfzCoop.Net;
using Steamworks;
using UnityEngine;

namespace IfzCoop.Sync
{
    /// <summary>
    /// Zeigt die Mitspieler: wohin ihre Kamera schaut, welche Trupps und welches Gebäude sie ausgewählt haben.
    /// Jeder schickt 4-mal pro Sekunde seinen Kamerapunkt, die IDs der ausgewählten Trupps und das angeklickte Gebäude.
    /// Trupps bekommen einen Rahmen um ihr Schild (Symbol über der Gruppe), Gebäude einen Rahmen mit Hinweis.
    /// Der Host verteilt die Daten aller an alle. Gezeichnet wird mit IMGUI über dem Spiel,
    /// ohne die Auswahl oder die Umrisse des Spiels anzufassen.
    /// </summary>
    internal sealed class PresenceSync
    {
        private const float SendInterval = 0.25f;
        private const float StaleAfter = 5f;

        private static readonly Color[] Palette =
        {
            new Color(0.25f, 0.6f, 1f),  // blau
            new Color(1f, 0.55f, 0.1f),  // orange
            new Color(0.75f, 0.4f, 1f),  // lila
            new Color(1f, 0.3f, 0.6f),   // pink
            new Color(0.5f, 1f, 0.3f),   // grün
            new Color(1f, 1f, 0.3f),     // gelb
            new Color(0.2f, 0.85f, 1f),  // hellblau
        };

        internal sealed class Remote
        {
            public ulong SteamId;
            public string Name;
            public Vector3 Camera;
            public readonly List<string> Squads = new List<string>();
            public long Building;          // angeklicktes Gebäude (Building.ID), 0 = keins
            public float Updated;
            public Color Color;
        }

        private readonly Session _session;
        private readonly Dictionary<ulong, Remote> _remotes = new Dictionary<ulong, Remote>();
        private float _nextSend;
        private GUIStyle _labelStyle;
        private InfoPanelController _infoPanel;

        private static readonly AccessTools.FieldRef<InfoPanelController, Structure> HighlightedField =
            AccessTools.FieldRefAccess<InfoPanelController, Structure>("highlightedBuilding");

        /// <summary>Testmodus: so tun, als wäre dieses Gebäude angeklickt (0 = aus).</summary>
        public static long QaSelectedBuilding;

        public PresenceSync(Session session)
        {
            _session = session;
        }

        public IEnumerable<Remote> Remotes => _remotes.Values;

        public void Reset()
        {
            _remotes.Clear();
        }

        public void Forget(ulong steamId)
        {
            _remotes.Remove(steamId);
        }

        // ------------------------------------------------------------- senden

        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSend || !GameAccess.IsGameStarted || GameCamera.Active == null)
                return;
            bool active = (_session.Role == Role.Host && _session.ActiveGuestCount > 0) || _session.GuestControlsLocked;
            if (!active)
                return;
            _nextSend = now + SendInterval;

            Vector3 cam;
            try
            {
                cam = GameCamera.Active.GetPositionOnYEqualsAndVieport(0f, new Vector3(0.5f, 0.5f, 0f));
            }
            catch
            {
                return;
            }
            var squads = SelectedSquadIds();
            long building = SelectedBuildingId();

            if (_session.Role == Role.Host)
            {
                _session.BroadcastToGuests(BuildRelay(_session.MyId.m_SteamID, _session.MyName, cam, squads, building));
            }
            else
            {
                _session.SendToHost(Protocol.Build(Msg.Presence, w =>
                {
                    WriteVector(w, cam);
                    WriteIds(w, squads);
                    w.Write(building);
                }));
            }
        }

        /// <summary>Das Gebäude, dessen Infofenster offen ist oder das umrandet ist.</summary>
        private long SelectedBuildingId()
        {
            if (QaSelectedBuilding != 0)
                return QaSelectedBuilding;
            if (_infoPanel == null)
                _infoPanel = Object.FindObjectOfType<InfoPanelController>();
            if (_infoPanel == null)
                return 0;
            Structure structure = null;
            try
            {
                structure = _infoPanel.CurrentDisplayedPanel != null ? _infoPanel.CurrentDisplayedPanel.Structure : null;
                if (structure == null)
                    structure = HighlightedField(_infoPanel);
            }
            catch
            {
                // Infofenster im Umbau (Szenenwechsel)
            }
            return structure is Building b && b != null ? b.ID : 0;
        }

        private static List<string> SelectedSquadIds()
        {
            var result = new List<string>();
            var selected = GameAccess.Game<SquadsController>()?.SelectedSquads;
            if (selected == null)
                return result;
            for (int i = 0; i < selected.Count && result.Count < 32; i++)
                if (selected[i] != null && !string.IsNullOrEmpty(selected[i].Id))
                    result.Add(selected[i].Id);
            return result;
        }

        // ------------------------------------------------------------- empfangen

        /// <summary>Host: Daten eines Gastes merken und an die anderen Gäste weitergeben.</summary>
        public void HostOnPresence(Peer from, BinaryReader r)
        {
            var cam = ReadVector(r);
            var ids = ReadIds(r);
            long building = r.ReadInt64();
            Store(from.Id.m_SteamID, from.Name, cam, ids, building);
            _session.BroadcastToGuests(BuildRelay(from.Id.m_SteamID, from.Name, cam, ids, building), except: from.Id);
        }

        /// <summary>Gast: Daten des Hosts oder eines anderen Gastes (vom Host weitergereicht).</summary>
        public void GuestOnPresence(BinaryReader r)
        {
            ulong id = r.ReadUInt64();
            string name = r.ReadString();
            var cam = ReadVector(r);
            var ids = ReadIds(r);
            long building = r.ReadInt64();
            if (id == _session.MyId.m_SteamID)
                return;
            Store(id, name, cam, ids, building);
        }

        private void Store(ulong id, string name, Vector3 cam, List<string> ids, long building)
        {
            if (!_remotes.TryGetValue(id, out var remote))
            {
                remote = new Remote { SteamId = id, Color = Palette[_remotes.Count % Palette.Length] };
                _remotes[id] = remote;
            }
            remote.Name = name;
            remote.Color = _session.PlayerColors != null ? _session.PlayerColors.ColorOf(id, remote.Color) : remote.Color;
            remote.Camera = cam;
            remote.Squads.Clear();
            remote.Squads.AddRange(ids);
            remote.Building = building;
            remote.Updated = Time.realtimeSinceStartup;
        }

        // ------------------------------------------------------------- zeichnen

        public void Draw()
        {
            if (_remotes.Count == 0 || !GameAccess.IsGameStarted || GameCamera.Active == null || Event.current.type != EventType.Repaint)
                return;
            // Technologie- und Gesetze-Fenster decken das Bild ab, Markierungen nicht darüber zeichnen.
            if (Gameplay.Research.UI.ResearchCanvas.IsVisible)
                return;
            var cam = GameCamera.Active.UnityCamera;
            if (cam == null)
                return;
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(16, Screen.height / 50), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }

            float now = Time.realtimeSinceStartup;
            var squads = GameAccess.Game<SquadsController>()?.Squads;
            var buildings = GameAccess.Game<BuildingsController>();
            var oldColor = GUI.color;

            foreach (var remote in _remotes.Values)
            {
                if (now - remote.Updated > StaleAfter)
                    continue;

                // Trupps des Mitspielers: kein Rahmen mehr, ihr Symbol ist selbst blau (SquadViewSync).

                // Angeklicktes Gebäude des Mitspielers: Umriss des Spiels in Blau (PresenceOutline), darüber nur der Name.
                // Ohne Umriss-Haken (Spiel-Update) wie früher ein eigener Rahmen.
                if (remote.Building != 0 && buildings != null)
                {
                    var b = buildings.GetBuildingById(remote.Building);
                    if (b != null && TryScreenRect(cam, b.gameObject, out var rect))
                    {
                        bool outlined = PresenceOutline.Active;
                        if (!outlined)
                            DrawFrame(Pad(rect, 6f), remote.Color, 3f);
                        float h = _labelStyle.fontSize + 12f;
                        DrawLabel(new Rect(rect.center.x - 250f, rect.yMin - h - 8f, 500f, h), outlined ? remote.Name : remote.Name + " meint dieses Haus", remote.Color);
                    }
                }

                // Blickpunkt des Mitspielers: Punkt mit Namen, am Bildrand als Pfeil.
                var p = cam.WorldToScreenPoint(remote.Camera);
                bool behind = p.z <= 0f;
                var pos = new Vector2(p.x, Screen.height - p.y);
                if (behind)
                    pos = new Vector2(Screen.width - pos.x, Screen.height - pos.y);
                bool onScreen = !behind && pos.x >= 0f && pos.x <= Screen.width && pos.y >= 0f && pos.y <= Screen.height;

                GUI.color = remote.Color;
                if (onScreen)
                {
                    GUI.DrawTexture(new Rect(pos.x - 6f, pos.y - 6f, 12f, 12f), Texture2D.whiteTexture);
                    DrawLabel(new Rect(pos.x - 150f, pos.y + 6f, 300f, _labelStyle.fontSize + 12f), remote.Name, remote.Color);
                }
                else
                {
                    float x = Mathf.Clamp(pos.x, 70f, Screen.width - 70f);
                    float y = Mathf.Clamp(pos.y, 20f, Screen.height - 40f);
                    string arrow = pos.x < 0f ? "<< " : pos.x > Screen.width ? "" : pos.y < 0f ? "^ " : "v ";
                    string suffix = pos.x > Screen.width ? " >>" : "";
                    DrawLabel(new Rect(x - 150f, y - 11f, 300f, _labelStyle.fontSize + 12f), arrow + remote.Name + suffix, remote.Color);
                }
            }
            GUI.color = oldColor;
        }

        public void JumpTo(Remote remote)
        {
            GameCamera.Active?.CenterOnPosition(remote.Camera);
        }

        private static Group Find(IReadOnlyList<Group> squads, string id)
        {
            for (int i = 0; i < squads.Count; i++)
                if (squads[i] != null && squads[i].Id == id)
                    return squads[i];
            return null;
        }

        private static readonly Vector3[] Corners = new Vector3[8];

        /// <summary>Bildschirm-Rechteck (GUI-Koordinaten) um alle sichtbaren Renderer eines Objekts.</summary>
        private static bool TryScreenRect(Camera cam, GameObject go, out Rect rect)
        {
            rect = default;
            bool any = false;
            var bounds = default(Bounds);
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(false))
            {
                // Nur das Bild selbst: ohne Partikel und ohne den Strich vom Schild zum Boden (LineRenderer).
                if (renderer == null || !renderer.enabled || renderer is LineRenderer || renderer is TrailRenderer
                    || renderer.GetType().Name == "ParticleSystemRenderer")
                    continue;
                if (any)
                    bounds.Encapsulate(renderer.bounds);
                else
                {
                    bounds = renderer.bounds;
                    any = true;
                }
            }
            if (!any)
                return false;
            var min = bounds.min;
            var max = bounds.max;
            for (int i = 0; i < 8; i++)
                Corners[i] = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = cam.WorldToScreenPoint(Corners[i]);
                if (p.z <= 0f)
                    return false;
                float y = Screen.height - p.y;
                xMin = Mathf.Min(xMin, p.x);
                xMax = Mathf.Max(xMax, p.x);
                yMin = Mathf.Min(yMin, y);
                yMax = Mathf.Max(yMax, y);
            }
            if (xMax < 0f || yMax < 0f || xMin > Screen.width || yMin > Screen.height)
                return false;
            rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return true;
        }

        /// <summary>Schrift mit dunklem Rand, damit sie auf hellen Dächern lesbar bleibt.</summary>
        private void DrawLabel(Rect r, string text, Color color)
        {
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            for (int dx = -2; dx <= 2; dx += 2)
                for (int dy = -2; dy <= 2; dy += 2)
                    if (dx != 0 || dy != 0)
                        GUI.Label(new Rect(r.x + dx, r.y + dy, r.width, r.height), text, _labelStyle);
            GUI.color = color;
            GUI.Label(r, text, _labelStyle);
            GUI.color = old;
        }

        private static Rect Pad(Rect r, float pad) => new Rect(r.x - pad, r.y - pad, r.width + 2f * pad, r.height + 2f * pad);

        private static void DrawFrame(Rect r, Color color, float thickness)
        {
            var old = GUI.color;
            GUI.color = color;
            var tex = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, r.width, thickness), tex);
            GUI.DrawTexture(new Rect(r.xMin, r.yMax - thickness, r.width, thickness), tex);
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, thickness, r.height), tex);
            GUI.DrawTexture(new Rect(r.xMax - thickness, r.yMin, thickness, r.height), tex);
            GUI.color = old;
        }

        // ------------------------------------------------------------- Format

        private static byte[] BuildRelay(ulong steamId, string name, Vector3 cam, List<string> ids, long building)
        {
            return Protocol.Build(Msg.Presence, w =>
            {
                w.Write(steamId);
                w.Write(name ?? "?");
                WriteVector(w, cam);
                WriteIds(w, ids);
                w.Write(building);
            });
        }

        private static void WriteVector(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        private static Vector3 ReadVector(BinaryReader r)
        {
            return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        private static void WriteIds(BinaryWriter w, List<string> ids)
        {
            w.Write(ids.Count);
            foreach (var id in ids)
                w.Write(id);
        }

        private static List<string> ReadIds(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 64)
                throw new InvalidDataException("zu viele Trupp-IDs");
            var ids = new List<string>(n);
            for (int i = 0; i < n; i++)
                ids.Add(r.ReadString());
            return ids;
        }
    }
}
