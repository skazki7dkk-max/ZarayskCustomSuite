param([string]$Output)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (!$Output) { $Output = Join-Path $root 'ZarayskMenu-0.2.0-Warcraft-4.2.0.zip' }
$archivePath = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($archivePath)) | Out-Null
# CreateNew avoids silently overwriting an existing release.
$stream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
        if ($relative -match '(^|/)(bin|obj)/|\.pdb$|\.zip$') { continue }
        # Use the host CounterStrikeSharp API, never the older copy bundled with Warcraft.
        if ($relative -eq 'server/game/csgo/addons/counterstrikesharp/plugins/WarcraftPlugin/CounterStrikeSharp.API.dll') { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "ZarayskMenu/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose(); $stream.Dispose() }
$check = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    foreach ($required in @('server/game/csgo/addons/counterstrikesharp/plugins/ZarayskMenu/ZarayskMenu.dll',
        'server/game/csgo/addons/counterstrikesharp/plugins/ZarayskWarcraftMenu/ZarayskWarcraftMenu.dll',
        'server/game/csgo/addons/counterstrikesharp/plugins/WarcraftPlugin/WarcraftPlugin.dll',
        'server/game/csgo/addons/counterstrikesharp/shared/ZarayskMenu.Api/ZarayskMenu.Api.dll', 'INSTALL-0.2.0-RU.md')) {
        if (!$check.GetEntry("ZarayskMenu/$required")) { throw "Missing package entry: $required" }
    }
    Write-Output "Verified $($check.Entries.Count) archive entries: $archivePath"
} finally { $check.Dispose() }
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
