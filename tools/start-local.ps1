param([string]$Cs2Path = 'C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive')
$ErrorActionPreference = 'Stop'
$cs2 = $Cs2Path
$info = Join-Path $cs2 'game/csgo/gameinfo.gi'
$bin = Join-Path $cs2 'game/bin/win64/cs2.exe'
$original = [IO.File]::ReadAllBytes($info)
$text = [IO.File]::ReadAllText($info)
if ($text.Contains('csgo/addons/metamod')) { throw 'Unexpected existing Metamod search path.' }
$anchor = "`t`t`tGame`tcsgo"
if (!$text.Contains($anchor)) { throw 'Unexpected gameinfo format.' }
$written = $null
try {
    [IO.File]::WriteAllText($info, $text.Replace($anchor, "`t`t`tGame`tcsgo/addons/metamod`r`n$anchor"), [Text.UTF8Encoding]::new($false))
    $written = (Get-FileHash $info).Hash
    # This is an interactive listen-server QA client: keep its window visible so
    # menu, model and HUD behavior can be verified instead of only checking logs.
    $client = Start-Process -FilePath $bin -ArgumentList @('-steam','-addon','zaraysk_menu','-insecure','-console','-windowed','-w','1280','-h','720','-ip','127.0.0.1','+ip','127.0.0.1','-port','27017','+sv_lan','1','+rcon_password','zaraysk-local-only','+map','de_dust2','+jointeam','3','+mp_warmup_end','+slot3') -WorkingDirectory ([IO.Path]::GetDirectoryName($bin)) -PassThru
    Write-Output "Task-owned listen test PID $($client.Id), port27017"
    $logs = Join-Path $cs2 'game/csgo/addons/counterstrikesharp/logs'
    $started = Get-Date
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Seconds 2
        if (@(Get-ChildItem $logs -Filter '*ZarayskMenu*' | Where-Object LastWriteTime -gt $started).Count) { break }
    }
} finally {
    if ($written -and (Get-FileHash $info).Hash -eq $written) { [IO.File]::WriteAllBytes($info, $original); Write-Output 'Original gameinfo restored.' }
}
