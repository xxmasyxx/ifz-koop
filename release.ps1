# IFZ Koop: Release auf GitHub erstellen.
#   .\release.ps1 -GameDir 'D:\...\Infection Free Zone' [-Notes 'Was ist neu'] [-Draft]
# Baut den Mod, legt version.txt mit SHA-256 an und laedt alles als GitHub-Release hoch.
# Der Updater im Spiel liest https://github.com/<Repo>/releases/latest/download/version.txt.
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$Notes = '',
    [string]$TestGameDir = 'C:\IFZ-Test',
    [switch]$Draft
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$gh = 'gh'
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { $gh = 'C:\Program Files\GitHub CLI\gh.exe' }

[xml]$proj = Get-Content (Join-Path $root 'IfzCoop.csproj')
$ver = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Version $ver"

& dotnet build (Join-Path $root 'IfzCoop.csproj') -c Release `
    "-p:GameManagedDir=$GameDir\Infection Free Zone_Data\Managed" `
    "-p:BepInExCoreDir=$GameDir\BepInEx\core" "-p:TestGameDir=$TestGameDir"
if ($LASTEXITCODE -ne 0) { throw 'Bauen fehlgeschlagen' }

$pkg = Join-Path $root "dist\IfzCoop-$ver"
$out = Join-Path $root "dist\release-$ver"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

$files = 'IfzCoop.dll', 'IfzCoop.Updater.dll'
$lines = @("version=$ver")
foreach ($f in $files) {
    Copy-Item (Join-Path $pkg $f) $out
    $lines += "$f=" + (Get-FileHash (Join-Path $out $f) -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $out 'version.txt'), ($lines -join "`n") + "`n")

# Komplettpaket fuer neue Spieler (Installer + DLLs + Anleitung), immer unter demselben Namen
$zip = Join-Path $out 'IfzCoop.zip'
Compress-Archive -Path (Join-Path $pkg '*') -DestinationPath $zip -Force
Copy-Item $zip (Join-Path $root "dist\IfzCoop-$ver.zip") -Force

$assets = @('version.txt', 'IfzCoop.dll', 'IfzCoop.Updater.dll', 'IfzCoop.zip') | ForEach-Object { Join-Path $out $_ }
# Text ueber Datei (PowerShell 5.1 zerlegt Anfuehrungszeichen in Argumenten an Programme)
$notesFile = Join-Path $out 'notes.md'
[IO.File]::WriteAllText($notesFile, $(if ($Notes) { $Notes } else { "IFZ Koop $ver" }), (New-Object Text.UTF8Encoding $false))
$ghArgs = @('release', 'create', "v$ver") + $assets + @('--title', "IFZ Koop $ver", '--notes-file', $notesFile)
if ($Draft) { $ghArgs += '--draft' }
Push-Location $root
try { & $gh @ghArgs; if ($LASTEXITCODE -ne 0) { throw 'gh release create fehlgeschlagen' } }
finally { Pop-Location }
Write-Host "Release v$ver erstellt."
