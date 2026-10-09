param([Parameter(Mandatory=$true)][string]$Cs2)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $Cs2 'game/bin/win64/resourcecompiler.exe'
if (!(Test-Path $compiler)) { throw 'Install Counter-Strike 2 Workshop Tools first' }
& node "$PSScriptRoot/style.mjs"
if ($LASTEXITCODE) { throw 'Invalid layout configuration' }
$content = Join-Path $Cs2 'content/csgo_addons/zaraysk_menu'
$game = Join-Path $Cs2 'game/csgo_addons/zaraysk_menu'
New-Item -ItemType Directory -Force $content,$game | Out-Null
Copy-Item "$root/panorama" $content -Recurse -Force
foreach ($file in @('panorama/styles/custom_game/zaraysk_menu.css','panorama/layout/custom_game/zaraysk_menu.xml')) {
  & $compiler -i "$content/$file" -r -nop4
  if ($LASTEXITCODE) { throw "Compilation failed: $file" }
}
foreach ($file in @('panorama/styles/custom_game/zaraysk_menu.vcss_c','panorama/layout/custom_game/zaraysk_menu.vxml_c')) {
  if (!(Test-Path "$game/$file")) { throw "Missing compiled output: $file" }
}
Write-Output "Compiled addon: $game. Publish it with Workshop Tools."
