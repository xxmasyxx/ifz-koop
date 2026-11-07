<# : IfzCoop-Launcher.bat - Batch-Teil startet den PowerShell-Teil weiter unten
@echo off
setlocal
rem Alles in einer Zeile: der Launcher ersetzt beim Aktualisieren diese Datei, cmd darf danach nichts mehr aus ihr lesen.
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([ScriptBlock]::Create([IO.File]::ReadAllText('%~f0'))) %*" || pause & exit /b
: Ende Batch-Teil #>

# ---------------------------------------------------------------------------------------------
# IFZ Koop - Launcher: holt die neueste Version von GitHub, installiert sie und startet das Spiel.
# Doppelklick genuegt. Ohne Internet startet das Spiel mit der installierten Version.
# ---------------------------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo  = 'xxmasyxx/ifz-koop'
$AppId = '1465460'

function Start-Game {
    Write-Host ''
    Write-Host '  Starte Infection Free Zone ueber Steam ...' -ForegroundColor Cyan
    Start-Process "steam://rungameid/$AppId"
    Start-Sleep -Seconds 3
}

Write-Host ''
Write-Host '  IFZ Koop - Launcher' -ForegroundColor Cyan
Write-Host ''

if (Get-Process -Name 'Infection Free Zone' -ErrorAction SilentlyContinue) {
    Write-Host '  Das Spiel laeuft schon. Zum Aktualisieren erst das Spiel beenden.' -ForegroundColor Yellow
    Start-Sleep -Seconds 4
    exit 0
}

$dir = Join-Path ([IO.Path]::GetTempPath()) 'IfzCoop-Launcher'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
$installer = Join-Path $dir 'IfzCoop-Installer.bat'

Write-Host '  Suche die neueste Version auf GitHub ...'
try {
    Invoke-WebRequest -Uri "https://github.com/$Repo/releases/latest/download/IfzCoop-Installer.bat" -OutFile $installer -UseBasicParsing -TimeoutSec 20
} catch {
    Write-Host '  GitHub nicht erreichbar, das Spiel startet mit der installierten Version.' -ForegroundColor Yellow
    Start-Game
    exit 0
}

# Der Installer laedt IfzCoop.dll und den Updater selbst von GitHub (er liegt hier ohne DLL)
& $installer install -Yes
$rc = $LASTEXITCODE
if ($rc -eq 2) {
    $a = Read-Host '  Diese Spielversion ist nicht getestet. Trotzdem installieren? [j/N]'
    if ($a -match '^(j|ja|y|yes)$') { & $installer install -Yes -Force; $rc = $LASTEXITCODE }
}
if ($rc -ne 0) {
    Write-Host ''
    Write-Host '  Aktualisieren hat nicht geklappt (siehe oben). Das Spiel startet trotzdem.' -ForegroundColor Yellow
    Start-Sleep -Seconds 5
}
Start-Game
exit 0
