param(
    [Parameter(Mandatory=$true)][string]$MenuPatchedWarcraft,
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$inputDll = (Resolve-Path -LiteralPath $MenuPatchedWarcraft).Path
$build = Join-Path $root 'src/ZarayskMenu.Api/bin/Release/net10.0'
$stage = Join-Path $root 'tools/GameplayPatch/bin/patched/WarcraftPlugin.dll'
& $Dotnet build "$root/src/ZarayskMenu.Api" -c Release
if ($LASTEXITCODE) { throw 'Shared API build failed' }
& $Dotnet run --project "$root/tools/GameplayPatch" -c Release -- $inputDll "$build/ZarayskMenu.Api.dll" $stage
if ($LASTEXITCODE) { throw 'Gameplay patch verification failed' }
& $Dotnet run --project "$root/gameplay-tests" -c Release -- $stage (Split-Path -Parent $inputDll)
if ($LASTEXITCODE) { throw 'Gameplay checks failed' }
$target = Join-Path $root 'server/game/csgo/addons/counterstrikesharp/plugins/WarcraftPlugin'
Copy-Item -LiteralPath $stage -Destination "$target/WarcraftPlugin.dll" -Force
$shared = Join-Path $root 'server/game/csgo/addons/counterstrikesharp/shared/ZarayskMenu.Api'
Copy-Item -LiteralPath "$build/ZarayskMenu.Api.dll" -Destination $shared -Force
# The original dependency manifest is retained byte-for-byte. ZarayskMenu.Api is
# already a shared assembly used by the menu-patched Warcraft binary.
$originalDeps = Join-Path $root '../../warcraft-server/WarcraftPlugin/WarcraftPlugin.deps.json'
$originalDeps = (Resolve-Path -LiteralPath $originalDeps).Path
Copy-Item -LiteralPath $originalDeps -Destination "$target/WarcraftPlugin.deps.json" -Force
Write-Output 'Gameplay update staged without changing WarcraftPlugin.deps.json.'
