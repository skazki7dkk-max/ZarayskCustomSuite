param([string]$Dotnet = "dotnet")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& $Dotnet build "$root/src/ZarayskMenu/ZarayskMenu.csproj" -c Release
if ($LASTEXITCODE) { throw 'Build failed' }
& $Dotnet build "$root/src/ZarayskWarcraftMenu/ZarayskWarcraftMenu.csproj" -c Release
if ($LASTEXITCODE) { throw 'Warcraft menu build failed' }
$plugin = "$root/server/game/csgo/addons/counterstrikesharp/plugins/ZarayskMenu"
$api = "$root/server/game/csgo/addons/counterstrikesharp/shared/ZarayskMenu.Api"
New-Item -ItemType Directory -Force $plugin,$api | Out-Null
Copy-Item "$root/src/ZarayskMenu/bin/Release/net10.0/ZarayskMenu.dll" $plugin -Force
Copy-Item "$root/src/ZarayskMenu/bin/Release/net10.0/ZarayskMenu.deps.json" $plugin -Force
Copy-Item "$root/src/ZarayskMenu.Api/bin/Release/net10.0/ZarayskMenu.Api.dll" $api -Force
Copy-Item "$root/config/menu.json" $plugin -Force
$war = "$root/server/game/csgo/addons/counterstrikesharp/plugins/ZarayskWarcraftMenu"
New-Item -ItemType Directory -Force $war | Out-Null
Copy-Item "$root/src/ZarayskWarcraftMenu/bin/Release/net10.0/ZarayskWarcraftMenu.dll" $war -Force
Copy-Item "$root/src/ZarayskWarcraftMenu/bin/Release/net10.0/ZarayskWarcraftMenu.deps.json" $war -Force
Copy-Item "$root/config/warcraft.json" $war -Force
Write-Output "Server package: $root/server"
