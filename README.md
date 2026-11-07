# IFZ Koop – Koop-Mod für Infection Free Zone

Zwei bis acht Spieler spielen gemeinsam **eine** Stadt in Infection Free Zone (Steam-App 1465460). Ein Spieler ist der
**Host**: Bei ihm läuft das Spiel wirklich. Die Gäste bekommen seinen Spielstand und sehen, was beim Host passiert. Ihre
Befehle gehen an den Host. Dazu gibt es einen Versus-Modus.

BepInEx-5-Mod (C#) für Spielversion 0.26.10.21.

## Installieren

1. **[IfzCoop.zip herunterladen](https://github.com/xxmasyxx/ifz-koop/releases/latest/download/IfzCoop.zip)** (immer die neueste Version) und entpacken.
2. Doppelklick auf `IfzCoop-Installer.bat`, dann **1** (Installieren). Der Installer findet das Spiel über Steam,
   lädt BepInEx 5.4.23.5 (Prüfsumme wird geprüft) und legt den Mod ins Spiel.
3. Spiel ganz normal über Steam starten.

**Noch einfacher:** nur den **[Launcher (IfzCoop-Launcher.exe)](https://github.com/xxmasyxx/ifz-koop/releases/latest/download/IfzCoop-Launcher.exe)**
herunterladen und starten: „Neueste Version holen und spielen“, „Spielen ohne Update“ oder eine bestimmte Version aus
der Liste installieren (dann ist das Auto-Update aus). Fehlt BepInEx, installiert er es mit. Der Installer legt dafür
auch eine Desktop-Verknüpfung „IFZ Koop“ an. Quellcode in `launcher\`.

**Updates kommen von selbst:** Beim Spielstart schaut der Mod hier auf GitHub nach einer neueren Version und installiert
sie, bevor das Spiel lädt (Prüfsumme wird geprüft). Ohne Internet startet das Spiel mit der installierten Version.
Abschalten: in `<Spiel>\BepInEx\config\ifzcoop-update.cfg` die Zeile `AutoUpdate = false`, oder Startoption `-ifzcoop-noupdate`.

Host und Gäste brauchen dieselbe Protokollversion, sonst lehnt der Host den Gast ab. Mit dem Auto-Update haben alle
automatisch dieselbe Version.

Installer-Menü: 1 installieren/aktualisieren, 2 Spiel starten, 3 Mod ausschalten, 4 Mod einschalten, 5 entfernen
(danach Originalzustand). Kommandozeile:
`IfzCoop-Installer.bat install|uninstall|enable|disable|start|status [-Yes] [-GameDir <Pfad>] [-Force]`.

## Spielen

- **Host:** im Hauptmenü auf **„Mehrspieler“**, auf das **+** einer Kachel klicken und einen Freund einladen. Dann wie
  gewohnt „Neues Spiel“ oder „Laden“. Die Verbindung läuft über Steam, kein Port muss freigegeben werden.
- **Gast:** Spiel starten und die Steam-Einladung annehmen. Du wartest im Hauptmenü und siehst, was der Host auswählt.
- **Esc-Menü im Spiel:** Mehrspieler-Kacheln, Einstellungen und **„Statistik“** (Getötet, Schaden, Gebaut, … je Spieler).
- **F8:** Fehler melden (Log, Zustand und Bildschirmfoto landen beim Host in `BepInEx\IfzCoop-Fehlerberichte`).
- **F7:** Entwickler-Menü (Host, für Gäste per Haken freigebbar).
- **Direktverbindung** (LAN/VPN, ohne Steam): im Mehrspieler-Fenster, Port 27120.

## Bauen

```bat
dotnet build -c Release -p:GameManagedDir="<Spiel>\Infection Free Zone_Data\Managed" -p:BepInExCoreDir="<Spiel>\BepInEx\core" -p:TestGameDir=C:\IFZ-Test
```

Voraussetzung: .NET-8-SDK und BepInEx 5 im Spiel. Ergebnis: das Paket `dist\IfzCoop-<Version>\` (Mod, Updater,
Installer, Anleitung). Die Spiel-DLLs werden nur als Verweis benutzt und sind nicht Teil dieses Projekts.

Neues Release (baut, schreibt `version.txt` mit SHA-256 und lädt alles hoch):

```powershell
.\release.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Infection Free Zone" -Notes "Was ist neu"
```

## Aufbau

- `src\Plugin.cs` – Einstieg, Einstellungen. `src\Session.cs` – Verbindung, Rollen, Spielstand-Übergabe, Uhr.
- `src\Net\` – Transport (Steam, TCP) und Nachrichtenformat (`Protocol.cs`).
- `src\Patches.cs`, `src\UnitPatches.cs` – Harmony-Haken.
- `src\Sync\` – je ein Baustein pro Spielbereich (Einheiten, Kampf, Trupps, Häuser, Lager, Forschung, …).
- `src\Menu\` – Mehrspieler im Haupt- und Esc-Menü, Farbrad, Statistik, Meldungen.
- `src\Versus\` – Versus-Modus.
- `src\QaDriver.cs` – Testmodus für automatische Tests mit zwei Spielen auf einem PC.
- `updater\` – Auto-Update (BepInEx-Preloader-Patcher).
- `installer\` – Installer und Kurzanleitung.
