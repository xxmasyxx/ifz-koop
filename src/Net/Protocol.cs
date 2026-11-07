using System;
using System.IO;
using System.Text;

namespace IfzCoop.Net
{
    /// <summary>Nachrichtentypen. Das erste Byte jeder Nachricht.</summary>
    internal enum Msg : byte
    {
        Hello = 1,          // Gast -> Host: int proto, string modVersion, string gameVersion, string name
        Welcome = 2,        // Host -> Gast: bool accepted, string reason
        SaveBegin = 3,      // Host -> Gast: int transferId, int totalBytes, byte[32] sha256
        SaveChunk = 4,      // Host -> Gast: int transferId, int offset, int length, bytes
        SaveEnd = 5,        // Host -> Gast: int transferId
        Ready = 6,          // Gast -> Host: int transferId (Spielstand geladen, Spiel läuft)
        Speed = 7,          // Host -> Gast: float speed
        SpeedRequest = 8,   // Gast -> Host: byte kind (0 = Tempo setzen, 1 = Pause umschalten), int timeSpeedType
        TimeState = 9,      // Host -> Gast: int day, float dayGts, float speed, int dayLength
        Ping = 10,          // beide Richtungen: double sendTime
        Pong = 11,          // beide Richtungen: double echoedSendTime
        Bye = 12,           // beide Richtungen: string reason
        ResyncRequest = 13, // Gast -> Host: (leer)

        // Funksprüche
        TransmissionStart = 14, // Host -> Gast: string transmissionId
        DialogueLine = 15,      // Host -> Gast: string dialogueId, string lineId, bool selected (Antwort)
        AnswerRequest = 16,     // Gast -> Host: string dialogueId, string lineId
        Notice = 17,            // Host -> Gast: string text (z. B. „Max hat geantwortet“)
        Settings = 18,          // Host -> Gast: byte answerMode

        // Trupp-Befehle (beide Richtungen, Host verteilt weiter)
        SquadOrder = 19,        // string groupId, string orderJson, bool clear, bool checkLoop, bool clearRelated

        // Mitspieler sichtbar
        Presence = 20,          // Gast -> Host: vec3 cam, ids, long building;  Host -> Gast: ulong steamId, string name, vec3 cam, ids, long building

        // Spielende
        GameOver = 21,          // Host -> Gast: int failReason, int fraction

        // Einheiten-Abgleich
        UnitSnapshot = 22,      // Host -> Gast: Gruppen mit Figuren (Position, Blick, Tempo, HP)
        UnitRoster = 23,        // Host -> Gast: alle Gruppen-IDs (für Verschwinden)
        VehicleSnapshot = 24,   // Host -> Gast: Fahrzeuge (ID, Position, Blick)
        GroupSpawn = 25,        // Host -> Gast: string GroupSerializedInfo-JSON
        GroupSpawnRequest = 26, // Gast -> Host: string groupId (unbekannte Gruppe anfordern)

        // Einheiten Stufe 2
        ShotBatch = 27,         // Host -> Gast: Schüsse (Angreifer, Ziel, Punkt) gebündelt
        ProjectileLaunch = 28,  // Host -> Gast: string draft, vec3 start, vec3 end, string group, string id
        SquadRequest = 29,      // Gast -> Host: Ausbildung (0) oder Auffüllen (1)
        SquadSpawn = 30,        // Host -> Gast: neuer Trupp (GroupSerializedInfo-JSON)
        CharacterDied = 31,     // Host -> Gast: string characterId (eigene Leute)

        // Hauptquartier
        HqSetupRequest = 32,    // Gast -> Host: string squadId (Konvoi: Lager aufschlagen)

        // Gebäude
        ScavengeState = 33,     // Host -> Gast: int n, n × (long buildingId, float timeTotal, float timeLeft)
        HideoutRevealed = 34,   // Host -> Gast: int n, n × string hideoutId
        StockState = 35,        // Host -> Gast: Lager (Container, VirtualResourcesContainer, Haltbarkeit Nahrung/Fleisch/Leichen)
        ContainerState = 36,    // Host -> Gast: n × (byte Art Trupp/Kofferraum/Haus, string id, int Kapazität, Rohstoffe, gesperrte)
        ItemsState = 37,        // Host -> Gast: n × (string characterId, int Waffe, int Schutz, float Schutz-Anteil, float Munition)
        GroundState = 38,       // Host -> Gast: bool voll, n × Bodenkiste (id, Art, Menge, Ort, Ablauf, sichtbar), m × id weg

        // Fahrzeuge
        VehicleState = 39,      // Host -> Gast: n × (VehicleSerialized-JSON, Insassen-Gruppe), m × id weg, [alle IDs]

        // Ereignisse, Missionen, Marker
        EventState = 40,        // Host -> Gast: Missionen (JSON), Marker (JSON, sichtbar), freigeschaltete Inhalte, erledigte/gescheiterte Missionen, Kapitel
        MissionTimes = 41,      // Host -> Gast: n × (Missions-ID, Restzeit)

        // Forschung, Gesetze, Stimmung, Wetter
        ResearchState = 42,     // Host -> Gast: ResearchControllerSerialized-JSON
        LawState = 43,          // Host -> Gast: LawsControllerSerialized-JSON
        MoodState = 44,         // Host -> Gast: MoodControllerSerialized-JSON
        WeatherState = 45,      // Host -> Gast: WeatherControllerSerialized-JSON, int Tag
        ResearchRequest = 46,   // Gast -> Host: int ResearchID, bool abbrechen
        LawRequest = 47,        // Gast -> Host: int LawID

        // Gebäude
        BuildingState = 48,     // Host -> Gast: n × (long id, int Zustand, string Umbau-Art, float Zielhöhe, bool Abriss markiert)
        BuildingRequest = 49,   // Gast -> Host: byte Aktion, long id, string Umbau-Art, float Wert

        // Bevölkerung
        PopulationState = 50,   // Host -> Gast: n × (string id, byte Art), m × CharacterSerializedInfo-JSON (neu/geändert)

        // Umgebungskarte
        DragRequest = 53,       // Gast -> Host: Tausch per Ziehen (Art, Seiten, Rohstoffe, Mengen, Flags)
        SquadState = 54,        // Host -> Gast: n × (Trupp-ID, Name, Haus-ID, Haltungen)
        SquadStateRequest = 55, // Gast -> Host: Trupp-ID, 0 = Name (string) | 1 = Haltung (int, bool)
        WorkState = 56,         // Host -> Gast: Modus, Alarm, Berufs-Wichtigkeit, n × (Arbeit-Schlüssel, Wichtigkeit, Höchstzahl, ruht, Arbeiter-IDs)
        WorkRequest = 57,       // Gast -> Host: Op (byte), Arbeit-Schlüssel, Berufsart, Zahl, Schalter
        ExpeditionState = 58,   // Host -> Gast: n × (Trupp-ID, Feld), m × Feld (Koordinaten, Durchsuchen, Kisten, Inhalt, Höhle, Kampf, Horde, Sperren)
        TraderState = 64,       // Host -> Gast: Händler (Abklingzeit, Zustand, Liefer-Listen, Vorräte, Händler-Gruppen, Tauschfenster mit Angeboten)
        TraderRequest = 65,     // Gast -> Host: Op (byte), Handelspartner, [Felder, Rohstoffe, Mengen, Flags]
        WorkerPos = 68,         // Host -> Gast: float Zeit, bool voll, n × (id, x, z, y·10, Blick, Tempo, Flags, [Haus], [Kiste], [Beruf]), m × id weg
        AreaWorkState = 60,     // Host -> Gast: n × Gebiet (Kennung, Art, erledigt, Fortschritt, Start-Rohstoffe, Unterarbeiten), fertige Unterarbeiten, verschwundene Sammelobjekte
        AreaWorkRequest = 61,   // Gast -> Host: Op (byte) 1 = anlegen (Art, n × Ziel) | 2 = löschen (Kennung)
        ProductionState = 66,   // Host -> Gast: Produktionen (Rezept, nächstes, Zeit, Rest), Werkstatt-Aufträge, Anpassungen, Sägewerk, Reparaturtrupp
        ProductionRequest = 67, // Gast -> Host: Op (byte) + Daten (Rezept wählen, Werkstatt-Auftrag, Anpassen, Schalter)
        GroupSide = 70,         // Host -> Gast: bool voll, n × (Gruppen-ID, Vorlage, Zugehörigkeiten, Einwanderer-Zustand, Zustand); Gast -> Host: Op (1 = Einladen per Funk)
        StoryCue = 71,          // Host -> Gast: Art (1 Kamera, 2 Marker folgen, 3 Ton), ID, Ort
        StructureState = 62,    // Host -> Gast: byte 0 = n × Anlage (Kennung, Art, Bauplan, Zustand, HP, Fortschritt, Lage, JSON wenn neu), m × Kennung weg | byte 1 = alle Kennungen
        StructureRequest = 63,  // Gast -> Host: Op, Kennung, Art, Bauplan, n × (Ort, Drehung, Größe)
        SquadActionState = 74,  // Host -> Gast: n × Fahrzeug (ID, Tanken an, Abgeben an, Munition nachfüllen, Reparatur läuft, Fortschritt), m × Trupp (ID, Ausrüstung gesperrt)
        SquadActionRequest = 75, // Gast -> Host: Op (byte) + Daten (Aussteigen, Reparieren, Sprit, Sperren, Teilen, Mitglieder ziehen, Tausch-Ende, Wegwerfen)
        SquadActionEvent = 76,  // Host -> Gast: Art (byte) 1 Teilen bereit, 2 Mitglied verschoben, 3 getauscht, 4 Trupp leer, 5 Erfahrung/Fähigkeiten je Figur
        ZoneState = 77,         // Host -> Gast: Siedlung (Art, aufgedeckt, übernommen, n × (Kennung, Typ, Bauplan, Zustand, gefunden, Versteck, Versteck-JSON))
        ZoneStats = 78,         // Host -> Gast: Statistik (Tote Infizierte/Banditen/eigene, Einwanderer, gesammelt, gefunden)
        ZoneMove = 79,          // Host -> Gast: byte Phase (1 Umzug angekündigt, 2 Host verlässt die Karte, 3 Host wieder im Spiel), string Text
        BuildingHealth = 72,    // Host -> Gast: n × (long id, float HP, float Max-HP, byte Flags (beansprucht, Abbau/Rückbau angehalten), int Sprengladungen)
        BuildingEvent = 73,     // Host -> Gast: byte Op: 1 eigenes Gebäude (long Zähler, Eckpunkte, Höhe, Dach, Fassade, Farben) | 2 Teilung beginnt (long id, string JSON) | 3 Teilung fertig (long id, long Zähler)
        BuildResState = 85,     // Host -> Gast: n × Baustelle (Ort, ID, Platz, Art, Fortschritt, Zeiten, Restkosten, Gesamtkosten, verbraucht, [Kisten])
        SquadView = 80,         // Host -> Gast: n × (Trupp-/Gruppen-ID, Flags, Zustand, Steuernder, Linienart, Restweg, Befehlskette)
        ResStats = 87,          // Host -> Gast: 2 × Vorrat je 6 Statistiken (Produziert/Gesammelt/Verwendet heute und Vortag), Kapazität, Kisten-Abweichung, Bevölkerungszähler
                                // 88 reserviert (ResStatsSync)
        PlayerColors = 81,      // Host -> Gast: n × (Steam-ID, Name, r, g, b, gewählt)
        PlayerRequest = 82,     // Gast -> Host: byte Op: 0 = Farbe (gewählt, r, g, b) | 1 = Trupps geändert (n × ID)
        // Versus (eigene Sitzung auf Steam-Kanal 1 / TCP 27121, VERSUS-TECHNIK 9.2). Eigenes Protokoll: Versus.VsProtocol.Version
        BugReportPart = 90,     // Gast -> Host: string Bericht-ID, string Dateiname (.txt/.jpg), int Stück, int Stücke, int Länge, bytes (Fehlerbericht F8)
        CheatRequest = 91,      // Gast -> Host: string Befehl des Entwickler-Menüs (nur wenn der Host es für Gäste erlaubt)
        LobbyInfo = 92,         // Host -> Gäste in der Lobby: n × string (was der Host im Hauptmenü gerade wählt: Stadt, Größe, Einstellungen)
        HqSuggest = 93,         // Gast -> Host: long Gebäude-ID (Vorschlag für das erste HQ, KONZEPT-MENUE M5)
        HqSuggestShow = 94,     // Host -> alle: ulong Absender, long Gebäude-ID, string Name, float r, g, b
        SquadQueue = 96,        // Host -> Gast: byte 0 = Warteschlangen der Trupp-Ausbildung (JSON wie im Spielstand) | 1 = Fortschritt n × (Gebäude, Nr., float)
        Stats = 99,             // Host -> Gast: int Entwickler-Schalter, n × (ulong Spieler, string Name, int k, k × float) – Statistik
        DemolishState = 98,     // Host -> Gast: n × (string Schlüssel, float Fortschritt, float Zeit, Startmenge, Restmenge) – Abbau/Rückbau
        SquadQueueCancel = 97,  // Gast -> Host: string Gebäude, int Nr., string Truppname (Ausbildung abbrechen)
        VsSwitch = 95,          // Host -> Gäste der Hauptmenü-Lobby: bool direkt, int Port, ulong Versus-Lobby (Wechsel in die Versus-Runde)
        VsHello = 100,          // Gast -> Host: int vsProto, string modVersion, string gameVersion, string name, bool Farbe [r,g,b], string SpielID
        VsWelcome = 101,        // Host -> Gast: bool angenommen, string Grund, byte Platz, ulong Kennung
        VsSettings = 102,       // Host -> alle: Start-Einstellungen (Versus.VsSettings)
        VsRoster = 103,         // Host -> alle: n × (Platz, Steam-ID, Name, Farbe, Team, lebt, Zustand)
        VsStartPhase = 104,     // Host -> alle: byte Phase (0 Lobby, 1 Spielstand kommt, 2 HQ wählen, 3 los), byte Startweg, string Text, [S2: int Übertragung, x, y, Zoom, Rand]
        VsHqChosen = 105,       // Gast -> Host: long Gebäude-ID, float x, float z (geheim)
        VsRelations = 107,      // Host -> alle: byte n, n × (byte Platz, byte Platz) – verbündete Paare
        VsAllyRequest = 108,    // Spieler -> Host: byte Ziel-Platz, byte 0 = Bündnis anbieten, 1 = beenden
        VsAllyAsk = 109,        // Host -> Spieler: byte Platz des Fragenden
        VsAllyAnswer = 110,     // Spieler -> Host: byte Platz des Fragenden, bool ja | Host -> Fragender: byte Platz, false (abgelehnt)
        VsScavenged = 111,      // Spieler -> Host -> alle: byte Platz, int n, n × long Haus-ID (fertig geplündert)
        VsVoteStatus = 106,     // Host -> alle: string Anzeige der Abstimmung über Pause/Vorspulen („1 von 3 wollen vorspulen“)
        VsWeather = 120,        // Host -> alle: string WeatherControllerSerialized-JSON, int Tag
        VsPopulation = 138,     // Spieler -> Host: bool Start gesetzt, int Arbeiter, Kinder, Trupps, Soldaten, string Start-Rohstoffe
        VsHqAnswer = 145,       // Host -> Gast: bool ok, long Gebäude-ID, string Grund
        Checksum = 52,          // Gast -> Host: n × (string Bereich, uint Prüfsumme)
        VirtualState = 51,      // Host -> Gast: Horden draußen (id, Ort, Größe, sichtbar), neue als JSON, Schwärme (id, Ort, sichtbar, Gruppen)
    }

    internal static class Protocol
    {
        /// <summary>Wird bei jeder inkompatiblen Änderung am Protokoll erhöht.</summary>
        public const int Version = 31;

        /// <summary>Nutzlast pro Spielstand-Stück. Steam erlaubt bis 512 KB pro Nachricht.</summary>
        public const int ChunkSize = 60000;

        public static byte[] Build(Msg type, Action<BinaryWriter> body = null)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((byte)type);
                body?.Invoke(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static BinaryReader Reader(byte[] data, out Msg type)
        {
            if (data == null || data.Length == 0)
                throw new InvalidDataException("leere Nachricht");
            type = (Msg)data[0];
            return new BinaryReader(new MemoryStream(data, 1, data.Length - 1, false), Encoding.UTF8);
        }
    }
}
