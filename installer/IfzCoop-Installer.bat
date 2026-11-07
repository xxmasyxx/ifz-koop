<# : IfzCoop-Installer.bat - Batch-Teil startet den PowerShell-Teil weiter unten
@echo off
setlocal
set "IFZCOOP_HERE=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([ScriptBlock]::Create([IO.File]::ReadAllText('%~f0'))) %*"
set "RC=%errorlevel%"
if "%~1"=="" pause
exit /b %RC%
: Ende Batch-Teil #>

# ---------------------------------------------------------------------------------------------
# IFZ Koop - Installer fuer Infection Free Zone (Steam-App 1465460)
#
# Doppelklick: Menue. Kommandozeile:
#   IfzCoop-Installer.bat install | uninstall | enable | disable | start | status  [-Yes] [-GameDir <Pfad>] [-Force]
#
# Was er tut:
#   - findet das Spiel ueber Steam (Registry, libraryfolders.vdf, appmanifest_1465460.acf)
#   - prueft, ob die Spielversion die getestete ist (Ifz.dll, SHA-256)
#   - installiert BepInEx 5.4.23.5 (offizieller GitHub-Download, SHA-256 fest im Skript)
#   - legt IfzCoop.dll (liegt neben diesem Installer) nach BepInEx\plugins\IfzCoop\
#   - startet das Spiel ueber Steam (dann funktionieren Overlay und Einladungen)
#   - entfernt alles wieder (nur was er selbst installiert hat)
#
# Testen ohne echtes Spiel: Umgebungsvariablen IFZCOOP_STEAM_ROOT (Steam-Ordner) und
# IFZCOOP_BEPINEX_ZIP (lokale BepInEx-Zip statt Download).
# ---------------------------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$AppId           = '1465460'
$ModName         = 'IFZ Koop'
$BepInExVersion  = '5.4.23.5'
$BepInExUrl      = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip'
$BepInExSha256   = '82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4'
# Spielversionen (Ifz.dll), gegen die der Mod geschrieben ist. Steam-Build 26104777.
$TestedIfzHashes = @('56464f9c355c350d3eb274ca2e56455ca629a00a7dc02774f4e99a125009db8f')
$MarkerName      = 'ifzcoop-installed-bepinex.txt'
# GitHub-Projekt mit den Releases (version.txt, IfzCoop.dll, IfzCoop.Updater.dll, IfzCoop.zip)
$Repo            = 'xxmasyxx/ifz-koop'

# ---------------------------------------------------------------- Argumente
$Command = $null; $Yes = $false; $Force = $false; $GameDirArg = $null
for ($i = 0; $i -lt $args.Count; $i++) {
    $a = [string]$args[$i]
    switch -Regex ($a) {
        '^-{1,2}yes$'      { $Yes = $true; continue }
        '^-{1,2}force$'    { $Force = $true; continue }
        '^-{1,2}gamedir$'  { $i++; $GameDirArg = [string]$args[$i]; continue }
        default            { if (-not $Command) { $Command = $a.ToLowerInvariant() } }
    }
}

$Here = $env:IFZCOOP_HERE
if (-not $Here) { $Here = (Get-Location).Path }
$LogFile = Join-Path $Here 'IfzCoop-Installer.log'

function Log([string]$msg) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $msg
    try { Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8 } catch { }
}
function Say([string]$msg, [string]$color = 'Gray') { Write-Host $msg -ForegroundColor $color; Log $msg }
function Ok([string]$msg)   { Say "  OK   $msg" 'Green' }
function Warn([string]$msg) { Say "  !    $msg" 'Yellow' }

function Fail([string]$what, [string]$todo) {
    Write-Host ''
    Write-Host '  +-- Das hat nicht geklappt --------------------------------------------' -ForegroundColor Red
    Write-Host "  |  $what" -ForegroundColor Red
    Write-Host '  |' -ForegroundColor Red
    Write-Host "  |  Was tun: $todo" -ForegroundColor Red
    Write-Host "  |  Protokoll: $LogFile" -ForegroundColor Red
    Write-Host '  +---------------------------------------------------------------------' -ForegroundColor Red
    Log "FEHLER: $what | $todo"
    exit 1
}

function Confirm([string]$question) {
    if ($Yes) { return $true }
    $answer = Read-Host "$question [j/N]"
    return $answer -match '^(j|ja|y|yes)$'
}

# ---------------------------------------------------------------- Spiel finden
function Get-SteamRoot {
    if ($env:IFZCOOP_STEAM_ROOT) { return $env:IFZCOOP_STEAM_ROOT }
    foreach ($key in @(
        @{ Path = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
        @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' },
        @{ Path = 'HKLM:\SOFTWARE\Valve\Steam'; Name = 'InstallPath' })) {
        try {
            $v = (Get-ItemProperty -Path $key.Path -Name $key.Name -ErrorAction Stop).($key.Name)
            if ($v -and (Test-Path -LiteralPath $v)) { return ($v -replace '/', '\') }
        } catch { }
    }
    return $null
}

function Find-GameDir {
    if ($GameDirArg) {
        if (-not (Test-Path -LiteralPath (Join-Path $GameDirArg 'Infection Free Zone.exe'))) {
            Fail "In '$GameDirArg' liegt keine 'Infection Free Zone.exe'." 'Den Ordner angeben, in dem die exe liegt.'
        }
        return (Resolve-Path -LiteralPath $GameDirArg).Path
    }
    $steam = Get-SteamRoot
    if (-not $steam) {
        Fail 'Steam wurde nicht gefunden.' 'Steam installieren oder den Spielordner angeben: IfzCoop-Installer.bat install -GameDir "D:\...\Infection Free Zone"'
    }
    $libraries = New-Object System.Collections.Generic.List[string]
    $libraries.Add($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        foreach ($m in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) {
            $p = $m.Groups[1].Value -replace '\\\\', '\'
            if (-not $libraries.Contains($p)) { $libraries.Add($p) }
        }
    }
    foreach ($lib in $libraries) {
        $acf = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (-not (Test-Path -LiteralPath $acf)) { continue }
        $m = [regex]::Match([IO.File]::ReadAllText($acf), '"installdir"\s+"([^"]+)"')
        if (-not $m.Success) { continue }
        $dir = Join-Path $lib ("steamapps\common\" + $m.Groups[1].Value)
        if (Test-Path -LiteralPath (Join-Path $dir 'Infection Free Zone.exe')) { return $dir }
    }
    Fail 'Infection Free Zone ist in keiner Steam-Bibliothek installiert.' 'Spiel in Steam installieren, oder den Ordner angeben: IfzCoop-Installer.bat install -GameDir "D:\...\Infection Free Zone"'
}

function Test-GameRunning([string]$gameDir) {
    $exe = [IO.Path]::GetFullPath((Join-Path $gameDir 'Infection Free Zone.exe'))
    foreach ($p in @(Get-Process -Name 'Infection Free Zone' -ErrorAction SilentlyContinue)) {
        try { if ($p.Path -and [string]::Equals([IO.Path]::GetFullPath($p.Path), $exe, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
        catch { return $true } # Pfad nicht lesbar: lieber vorsichtig sein
    }
    return $false
}

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Test-GameBuild([string]$gameDir) {
    $ifz = Join-Path $gameDir 'Infection Free Zone_Data\Managed\Ifz.dll'
    if (-not (Test-Path -LiteralPath $ifz)) { return 'fehlt' }
    $h = Get-Sha256 $ifz
    if ($TestedIfzHashes -contains $h) { return 'getestet' }
    return $h
}

# ---------------------------------------------------------------- Zustand
function Get-State([string]$gameDir) {
    $core = Join-Path $gameDir 'BepInEx\core\BepInEx.dll'
    $bepVersion = $null
    if (Test-Path -LiteralPath $core) { $bepVersion = (Get-Item -LiteralPath $core).VersionInfo.FileVersion }
    $doorstop = Join-Path $gameDir 'doorstop_config.ini'
    $enabled = $null
    if (Test-Path -LiteralPath $doorstop) {
        $enabled = -not ([IO.File]::ReadAllText($doorstop) -match '(?m)^\s*enabled\s*=\s*false')
    }
    $plugin = Join-Path $gameDir 'BepInEx\plugins\IfzCoop\IfzCoop.dll'
    $pluginVersion = $null
    if (Test-Path -LiteralPath $plugin) { $pluginVersion = (Get-Item -LiteralPath $plugin).VersionInfo.FileVersion }
    [pscustomobject]@{
        BepInEx      = $bepVersion
        LoaderOn     = $enabled
        WinHttp      = Test-Path -LiteralPath (Join-Path $gameDir 'winhttp.dll')
        Plugin       = $pluginVersion
        OurBepInEx   = Test-Path -LiteralPath (Join-Path $gameDir "BepInEx\$MarkerName")
    }
}

function Show-Status([string]$gameDir) {
    $s = Get-State $gameDir
    $build = Test-GameBuild $gameDir
    Say "Spielordner:   $gameDir"
    if ($build -eq 'getestet') { Say 'Spielversion:  getestet (passt zum Mod)' 'Green' }
    else { Say "Spielversion:  NICHT getestet (Ifz.dll $build)" 'Yellow' }
    Say ("BepInEx:       " + $(if ($s.BepInEx) { "$($s.BepInEx)" + $(if ($s.OurBepInEx) { ' (von diesem Installer)' } else { ' (war schon da)' }) } else { 'nicht installiert' }))
    Say ("Mod-Lader:     " + $(if ($s.LoaderOn -eq $null) { '-' } elseif ($s.LoaderOn) { 'an' } else { 'aus' }))
    Say ("IFZ Koop:      " + $(if ($s.Plugin) { $s.Plugin } else { 'nicht installiert' }))
}

# ---------------------------------------------------------------- Installieren
function Get-BepInExZip {
    if ($env:IFZCOOP_BEPINEX_ZIP) { $zip = $env:IFZCOOP_BEPINEX_ZIP }
    else {
        $zip = Join-Path ([IO.Path]::GetTempPath()) "BepInEx_win_x64_$BepInExVersion.zip"
        if (-not (Test-Path -LiteralPath $zip) -or (Get-Sha256 $zip) -ne $BepInExSha256) {
            Say "Lade BepInEx $BepInExVersion von GitHub ..."
            try { Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing }
            catch { Fail "Download von BepInEx fehlgeschlagen: $($_.Exception.Message)" 'Internetverbindung pruefen und nochmal starten.' }
        }
    }
    $h = Get-Sha256 $zip
    if ($h -ne $BepInExSha256) {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        Fail "Die BepInEx-Datei hat eine falsche Pruefsumme ($h)." 'Nochmal starten. Bleibt der Fehler, die Datei nicht benutzen und dem Mod-Autor Bescheid geben.'
    }
    Ok "BepInEx $BepInExVersion geprueft (SHA-256 stimmt)"
    return $zip
}

function Set-HideManager([string]$gameDir) {
    $cfgDir = Join-Path $gameDir 'BepInEx\config'
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    $cfg = Join-Path $cfgDir 'BepInEx.cfg'
    if (-not (Test-Path -LiteralPath $cfg)) {
        $text = "[Chainloader]`r`n`r`n## Haelt das BepInEx-Objekt vor dem Spiel versteckt (manche Unity-Spiele loeschen es sonst).`r`nHideManagerGameObject = true`r`n"
        [IO.File]::WriteAllText($cfg, $text)
        Ok 'BepInEx-Einstellung angelegt'
        return
    }
    $text = [IO.File]::ReadAllText($cfg)
    if ($text -match '(?m)^\s*HideManagerGameObject\s*=\s*false') {
        Copy-Item -LiteralPath $cfg -Destination "$cfg.bak" -Force
        [IO.File]::WriteAllText($cfg, ($text -replace '(?m)^(\s*HideManagerGameObject\s*=\s*)false', '${1}true'))
        Ok 'BepInEx-Einstellung HideManagerGameObject = true gesetzt (Sicherung: BepInEx.cfg.bak)'
    }
}

function Set-Loader([string]$gameDir, [bool]$on) {
    $ini = Join-Path $gameDir 'doorstop_config.ini'
    if (-not (Test-Path -LiteralPath $ini)) { Fail 'BepInEx ist nicht installiert.' 'Erst "Installieren" waehlen.' }
    $text = [IO.File]::ReadAllText($ini)
    $value = if ($on) { 'true' } else { 'false' }
    $new = [regex]::Replace($text, '(?m)^(\s*enabled\s*=\s*)(true|false)', "`${1}$value")
    [IO.File]::WriteAllText($ini, $new)
}

function Get-DllVersion([string]$path) {
    try { return [Reflection.AssemblyName]::GetAssemblyName($path).Version } catch { return [version]'0.0.0.0' }
}

# Neueste Version von GitHub holen, wenn sie neuer ist als die mitgelieferte. Gibt den Ordner mit den DLLs zurueck.
function Get-NewestFiles {
    $bundled = Join-Path $Here 'IfzCoop.dll'
    $have = if (Test-Path -LiteralPath $bundled) { Get-DllVersion $bundled } else { [version]'0.0.0.0' }
    if ($env:IFZCOOP_OFFLINE -or $Repo -like '*__*') { return $Here }
    $base = "https://github.com/$Repo/releases/latest/download/"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) 'IfzCoop-neu'
    try {
        New-Item -ItemType Directory -Force -Path $tmp | Out-Null
        $text = (Invoke-WebRequest -Uri ($base + 'version.txt') -UseBasicParsing -TimeoutSec 15).Content
        if ($text -is [byte[]]) { $text = [Text.Encoding]::UTF8.GetString($text) }
        $kv = @{}
        foreach ($line in ($text -split "`n")) { if ($line -match '^\s*([^#=]+?)\s*=\s*(.+?)\s*$') { $kv[$Matches[1]] = $Matches[2] } }
        $remote = [version]$kv['version']
        if ($remote -le $have) { Ok "Mitgelieferte Version $have ist die neueste"; return $Here }
        Say "Auf GitHub gibt es die neuere Version $remote, lade ..."
        foreach ($name in @('IfzCoop.dll', 'IfzCoop.Updater.dll', 'IfzCoop-Launcher.bat')) {
            if (-not $kv[$name]) { continue }
            $out = Join-Path $tmp $name
            Invoke-WebRequest -Uri ($base + $name) -OutFile $out -UseBasicParsing -TimeoutSec 60
            if ((Get-Sha256 $out) -ne $kv[$name].ToLowerInvariant()) { throw "$name hat eine falsche Pruefsumme" }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $tmp 'IfzCoop.dll'))) { throw 'IfzCoop.dll fehlt im Release' }
        Ok "Version $remote von GitHub geladen (SHA-256 stimmt)"
        return $tmp
    } catch {
        if (Test-Path -LiteralPath $bundled) { Warn "GitHub nicht erreichbar ($($_.Exception.Message)), nehme die mitgelieferte Version $have."; return $Here }
        Fail "Download von GitHub fehlgeschlagen: $($_.Exception.Message)" 'Internetverbindung pruefen und nochmal starten.'
    }
}

function Install-All([string]$gameDir) {
    $src = Get-NewestFiles
    $plugin = Join-Path $src 'IfzCoop.dll'
    if (-not (Test-Path -LiteralPath $plugin)) {
        Fail "IfzCoop.dll liegt nicht neben dem Installer ($Here)." 'Den ganzen entpackten Ordner benutzen, nicht nur die .bat.'
    }
    $updater = Join-Path $src 'IfzCoop.Updater.dll'
    if (-not (Test-Path -LiteralPath $updater)) { $updater = Join-Path $Here 'IfzCoop.Updater.dll' }

    $build = Test-GameBuild $gameDir
    if ($build -ne 'getestet') {
        Warn "Diese Spielversion ist nicht getestet (Ifz.dll: $build)."
        Warn 'Nach einem Spiel-Update kann der Mod beim Start abschalten (steht dann im BepInEx-Log).'
        # -Yes allein reicht hier nicht: eine ungetestete Version braucht ein bewusstes Ja oder -Force.
        $go = $Force -or (-not $Yes -and (Read-Host 'Trotzdem installieren? [j/N]') -match '^(j|ja|y|yes)$')
        if (-not $go) { Say 'Abgebrochen, nichts geaendert. (Erzwingen mit -Force)'; exit 2 }
    } else { Ok 'Spielversion passt zum Mod' }

    if (Test-GameRunning $gameDir) {
        Fail 'Infection Free Zone laeuft gerade.' 'Spiel beenden und den Installer nochmal starten.'
    }

    $state = Get-State $gameDir
    try {
        if ($state.BepInEx) {
            if (-not $state.BepInEx.StartsWith('5.')) {
                Fail "Im Spielordner ist BepInEx $($state.BepInEx) installiert, der Mod braucht BepInEx 5." 'Das vorhandene BepInEx entfernen (Ordner BepInEx, winhttp.dll, doorstop_config.ini) und neu installieren.'
            }
            Ok "BepInEx $($state.BepInEx) ist schon da, wird weiterbenutzt"
            if ($state.LoaderOn -eq $false) { Set-Loader $gameDir $true; Ok 'Mod-Lader wieder eingeschaltet' }
        } else {
            $zip = Get-BepInExZip
            Expand-Archive -LiteralPath $zip -DestinationPath $gameDir -Force
            Set-Content -LiteralPath (Join-Path $gameDir "BepInEx\$MarkerName") -Value "Installiert von IfzCoop-Installer am $(Get-Date -Format s). BepInEx $BepInExVersion." -Encoding UTF8
            Ok "BepInEx $BepInExVersion installiert"
        }
        Set-HideManager $gameDir

        $targetDir = Join-Path $gameDir 'BepInEx\plugins\IfzCoop'
        New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
        $target = Join-Path $targetDir 'IfzCoop.dll'
        if (Test-Path -LiteralPath $target) {
            if ((Get-Sha256 $target) -eq (Get-Sha256 $plugin)) { Ok 'IFZ Koop ist schon in dieser Version installiert' }
            else {
                Copy-Item -LiteralPath $target -Destination "$target.bak" -Force
                Copy-Item -LiteralPath $plugin -Destination $target -Force
                Ok 'IFZ Koop aktualisiert (alte Version: IfzCoop.dll.bak)'
            }
        } else {
            Copy-Item -LiteralPath $plugin -Destination $target -Force
            Ok 'IFZ Koop installiert'
        }

        if (Test-Path -LiteralPath $updater) {
            $updDir = Join-Path $gameDir 'BepInEx\patchers\IfzCoop'
            New-Item -ItemType Directory -Force -Path $updDir | Out-Null
            Copy-Item -LiteralPath $updater -Destination (Join-Path $updDir 'IfzCoop.Updater.dll') -Force
            Ok 'Auto-Update installiert (holt beim Spielstart die neueste Version von GitHub)'
        }

        $launcher = Join-Path $src 'IfzCoop-Launcher.bat'
        if (-not (Test-Path -LiteralPath $launcher)) { $launcher = Join-Path $Here 'IfzCoop-Launcher.bat' }
        if (Test-Path -LiteralPath $launcher) {
            $launcherTarget = Join-Path $gameDir 'BepInEx\IfzCoop-Launcher.bat'
            if (-not [string]::Equals([IO.Path]::GetFullPath($launcher), [IO.Path]::GetFullPath($launcherTarget), [StringComparison]::OrdinalIgnoreCase)) {
                Copy-Item -LiteralPath $launcher -Destination $launcherTarget -Force
            }
            try {
                $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'IFZ Koop.lnk'
                $sh = New-Object -ComObject WScript.Shell
                $s = $sh.CreateShortcut($lnk)
                $s.TargetPath = $launcherTarget
                $s.WorkingDirectory = Split-Path $launcherTarget
                $s.IconLocation = (Join-Path $gameDir 'Infection Free Zone.exe') + ',0'
                $s.Description = 'IFZ Koop: aktualisieren und Infection Free Zone starten'
                $s.Save()
                Ok 'Desktop-Verknuepfung "IFZ Koop" angelegt (aktualisiert und startet das Spiel)'
            } catch { Warn "Desktop-Verknuepfung nicht angelegt: $($_.Exception.Message)" }
        }
    } catch [UnauthorizedAccessException] {
        Fail 'Keine Schreibrechte im Spielordner.' 'Den Installer mit Rechtsklick > "Als Administrator ausfuehren" starten.'
    }

    Say ''
    Say 'Fertig. So geht es weiter:' 'Cyan'
    Say '  Starten:   ueber Steam wie immer (oder hier im Menue "Spiel starten").' 'Cyan'
    Say '  Hosten:    im Hauptmenue "Mehrspieler", auf + bei einer Kachel klicken = Freund einladen.' 'Cyan'
    Say '  Beitreten: Spiel starten, dann die Steam-Einladung annehmen.' 'Cyan'
    Say '  Updates:   kommen beim Spielstart von selbst (aus: BepInEx\config\ifzcoop-update.cfg).' 'Cyan'
    Say '  Ohne Mod spielen: im Menue "Mod ausschalten" (nichts wird geloescht).' 'Cyan'
}

function Uninstall-All([string]$gameDir) {
    if (Test-GameRunning $gameDir) {
        Fail 'Infection Free Zone laeuft gerade.' 'Spiel beenden und den Installer nochmal starten.'
    }
    $state = Get-State $gameDir
    $pluginDir = Join-Path $gameDir 'BepInEx\plugins\IfzCoop'
    if (Test-Path -LiteralPath $pluginDir) { Remove-Item -LiteralPath $pluginDir -Recurse -Force; Ok 'IFZ Koop entfernt' }
    else { Say 'IFZ Koop war nicht installiert.' }
    $updDir = Join-Path $gameDir 'BepInEx\patchers\IfzCoop'
    if (Test-Path -LiteralPath $updDir) { Remove-Item -LiteralPath $updDir -Recurse -Force; Ok 'Auto-Update entfernt' }
    $updCfg = Join-Path $gameDir 'BepInEx\config\ifzcoop-update.cfg'
    if (Test-Path -LiteralPath $updCfg) { Remove-Item -LiteralPath $updCfg -Force }
    foreach ($p in @((Join-Path $gameDir 'BepInEx\IfzCoop-Launcher.bat'), (Join-Path ([Environment]::GetFolderPath('Desktop')) 'IFZ Koop.lnk'))) {
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
    }

    if ($state.OurBepInEx) {
        $others = @(Get-ChildItem -LiteralPath (Join-Path $gameDir 'BepInEx\plugins') -ErrorAction SilentlyContinue)
        if ($others.Count -gt 0) {
            Warn 'BepInEx bleibt, weil noch andere Mods im Ordner BepInEx\plugins liegen.'
        } elseif (Confirm 'BepInEx (vom Installer mitgebracht) auch entfernen?') {
            foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')) {
                $p = Join-Path $gameDir $f
                if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
            }
            Remove-Item -LiteralPath (Join-Path $gameDir 'BepInEx') -Recurse -Force
            Ok 'BepInEx entfernt. Das Spiel ist wieder im Originalzustand.'
        }
    } elseif ($state.BepInEx) {
        Say 'BepInEx war schon vor dem Installer da und bleibt.'
    }
    Say 'Spielstaende des Mods (ifzcoop-host, ifzcoop-guest) bleiben im Spielstand-Ordner und lassen sich im Spiel loeschen.'
}

function Start-Game {
    Say 'Starte Infection Free Zone ueber Steam ...'
    Start-Process "steam://rungameid/$AppId"
}

# ---------------------------------------------------------------- Ablauf
Log "---- Start: $Command $($args -join ' ')"
Write-Host ''
Write-Host "  $ModName - Installer" -ForegroundColor Cyan
Write-Host ''

$gameDir = Find-GameDir

if (-not $Command) {
    Show-Status $gameDir
    Write-Host ''
    Write-Host '  1  Installieren / Aktualisieren'
    Write-Host '  2  Spiel starten (ueber Steam)'
    Write-Host '  3  Mod ausschalten (ohne Mod spielen)'
    Write-Host '  4  Mod einschalten'
    Write-Host '  5  Entfernen'
    Write-Host '  Q  Beenden'
    Write-Host ''
    $choice = Read-Host 'Auswahl'
    switch ($choice.Trim().ToLowerInvariant()) {
        '1' { $Command = 'install' }
        '2' { $Command = 'start' }
        '3' { $Command = 'disable' }
        '4' { $Command = 'enable' }
        '5' { $Command = 'uninstall' }
        default { exit 0 }
    }
    Write-Host ''
}

switch ($Command) {
    'install'   { Install-All $gameDir; if (-not $Yes -and (Confirm 'Spiel jetzt starten?')) { Start-Game } }
    'uninstall' { Uninstall-All $gameDir }
    'disable'   { Set-Loader $gameDir $false; Ok 'Mod ausgeschaltet. Das Spiel startet jetzt ohne Mods.' }
    'enable'    { Set-Loader $gameDir $true;  Ok 'Mod eingeschaltet.' }
    'start'     { Start-Game }
    'status'    { Show-Status $gameDir }
    default     { Fail "Unbekannter Befehl '$Command'." 'Ohne Befehl starten (Doppelklick) oder: install, uninstall, enable, disable, start, status' }
}
Log '---- Ende'
exit 0
