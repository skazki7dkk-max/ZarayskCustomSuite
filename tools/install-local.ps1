param([string]$Cs2Path = 'C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive')
$ErrorActionPreference = 'Stop'
$targetRoot = [IO.Path]::GetFullPath($Cs2Path)
if (!(Test-Path -LiteralPath (Join-Path $targetRoot 'game/bin/win64/cs2.exe'))) { throw 'CS2 not found at this path.' }
if (@(Get-Process cs2 -ErrorAction SilentlyContinue).Count) { throw 'Close CS2 before installing.' }
$sourceRoot = Join-Path $PSScriptRoot 'game'
$backupRoot = Join-Path $targetRoot ('zaraysk-backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$cssRoot = Join-Path $targetRoot 'game/csgo/addons/counterstrikesharp'
if (!(Test-Path -LiteralPath (Join-Path $cssRoot 'bin'))) { throw 'CounterStrikeSharp with runtime must already be installed.' }
function Backup-File([string]$path) {
    if (!(Test-Path -LiteralPath $path)) { return }
    $relative = [IO.Path]::GetRelativePath($targetRoot, $path)
    $saved = Join-Path $backupRoot $relative
    New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($saved)) | Out-Null
    Copy-Item -LiteralPath $path -Destination $saved
}
# Numbered chunks cannot remain in the root of this loose addon. Keep them in a backup.
$addon = Join-Path $targetRoot 'game/csgo_addons/zaraysk_menu'
foreach ($name in @('3809867620_000.vpk','3809867620_001.vpk','3809867620_002.vpk','3809867620_dir.vpk')) {
    $path = Join-Path $addon $name
    if (Test-Path -LiteralPath $path) {
        $saved = Join-Path $backupRoot $name
        New-Item -ItemType Directory -Force $backupRoot | Out-Null
        Move-Item -LiteralPath $path -Destination $saved
    }
}
$copied = 0
$preserved = 0
foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
    $destination = [IO.Path]::GetFullPath((Join-Path (Join-Path $targetRoot 'game') $relative))
    if (!$destination.StartsWith($targetRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected target path.' }
    if (Test-Path -LiteralPath $destination) {
        # Preserve database settings and player data; only update menu control choices.
        if ($relative -match '[/\\]configs[/\\]' -or ($file.Extension -eq '.json' -and $file.Name -ne 'menu.json' -and $file.Name -notlike '*.deps.json')) { $preserved++; continue }
        if ($file.Name -eq 'menu.json') {
            $old = Get-Content -LiteralPath $destination -Raw | ConvertFrom-Json
            $new = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            $newHelp = $new.Menus | Where-Object Id -eq 'help'
            $oldHelp = $old.Menus | Where-Object Id -eq 'help'
            Backup-File $destination
            if ($oldHelp) { $oldHelp.Title = $newHelp.Title; $oldHelp.Items = $newHelp.Items }
            else { $old.Menus += $newHelp }
            if (!$old.Menus.Items.Where({ $_.Submenu -eq 'help' }).Count) {
                $main = $old.Menus | Where-Object Id -eq 'main'
                $main.Items += [pscustomobject]@{Id='menu_controls';Text='Управление меню';Submenu='help'}
            }
            [IO.File]::WriteAllText($destination, ($old | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
            $copied++; continue
        }
        if ((Get-FileHash -LiteralPath $destination).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) { continue }
        Backup-File $destination
    }
    New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Copy verification failed: $relative" }
    $copied++
}
Write-Output "Installed/updated $copied files. Preserved $preserved existing settings."
Write-Output "Backups: $backupRoot"
Write-Output 'Launch with Start-Local.ps1 from this package. Open !menu in game.'
