param(
    [string]$GameRoot='D:\SteamLibrary\steamapps\common\Slay the Spire 2',
    [ValidateNotNullOrEmpty()][string]$Seed='FDZFLOCAL',
    [ValidateRange(1024,65535)][int]$Port=39182,
    [switch]$Smoke,
    [string]$ScreenshotDirectory
)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
if(Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue){throw '请先退出正在运行的游戏，再启动本地双窗口测试。'}
$gameExe=Join-Path $GameRoot 'SlayTheSpire2.exe'
$modDll=Join-Path $GameRoot 'mods\FendaoZhengfeng\FendaoZhengfeng.dll'
if(!(Test-Path -LiteralPath $gameExe) -or !(Test-Path -LiteralPath $modDll)){throw '没有找到游戏或本模组，请检查 GameRoot。'}
if([version][Diagnostics.FileVersionInfo]::GetVersionInfo($modDll).FileVersion -lt [version]'0.3.3'){throw '需要本地 0.3.3 或更新构建，请先运行 ./scripts/Build.ps1 -Deploy。'}
$sessionRoot=Join-Path $repoRoot ('artifacts\interactive\'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $sessionRoot -Force | Out-Null
$records=[Collections.Generic.List[object]]::new()
function Start-LocalPeer([string]$Role){
    $launch=[Diagnostics.ProcessStartInfo]::new()
    $launch.FileName=$gameExe
    $launch.WorkingDirectory=$GameRoot
    $launch.UseShellExecute=$false
    $launch.CreateNoWindow=$true
    # These are the two visible interactive windows explicitly requested by the user.
    $launch.WindowStyle=[Diagnostics.ProcessWindowStyle]::Normal
    $log=Join-Path $sessionRoot ($Role+'.log')
    foreach($argument in @('--windowed','--resolution','960x600','--audio-driver','Dummy','--log-file',$log,'--max-fps','60')){$launch.ArgumentList.Add($argument)}
    $launch.Environment['SteamAppId']='2868840'
    $launch.Environment['SteamGameId']='2868840'
    $launch.Environment['FDZF_NATIVE_VERIFY']='1'
    $launch.Environment['FDZF_VERIFY_ROLE']=$Role
    $launch.Environment['FDZF_INTERACTIVE_NETWORK']='1'
    $launch.Environment['FDZF_LOCAL_PORT']=$Port.ToString()
    $launch.Environment['FDZF_LOCAL_SEED']=$Seed
    $launch.Environment['FDZF_REPORT_DIR']=$sessionRoot
    if($Smoke){$launch.Environment['FDZF_INTERACTIVE_SMOKE']='1'}
    if($ScreenshotDirectory){$launch.Environment['FDZF_SCREENSHOT_DIR']=$ScreenshotDirectory}
    return [PSCustomObject]@{Process=[Diagnostics.Process]::Start($launch);Role=$Role;Log=$log}
}
try{
    $records.Add((Start-LocalPeer 'host'))
    $records.Add((Start-LocalPeer 'client'))
    Write-Output "正在启动两个窗口并自动连接，种子：$Seed；本机端口：$Port"
    Write-Output "日志目录：$sessionRoot"
    $deadline=[DateTime]::UtcNow.AddSeconds(120)
    while($true){
        $ready=0
        foreach($record in $records){
            $logText=if(Test-Path -LiteralPath $record.Log){Get-Content -LiteralPath $record.Log -Raw}else{''}
            if($logText -match 'ObjectDisposedException|LocException|\[FDZF NETWORK FAILED\]'){throw "启动失败，请查看 $($record.Log)"}
            if($logText -match '\[FDZF INTERACTIVE READY\]'){$ready++}
            elseif($record.Process.HasExited){throw "游戏提前退出，请查看 $($record.Log)"}
        }
        if($ready -eq 2){break}
        if([DateTime]::UtcNow -gt $deadline){throw '双窗口连接超时，请查看上述日志。'}
        Start-Sleep -Milliseconds 250
    }
    if($Smoke){
        while(@($records | Where-Object {!$_.Process.HasExited}).Count -gt 0){
            if([DateTime]::UtcNow -gt $deadline){throw '双窗口启动验证超时。'}
            Start-Sleep -Milliseconds 250
        }
        foreach($record in $records){
            $logText=Get-Content -LiteralPath $record.Log -Raw
            if($record.Process.ExitCode -ne 0 -or $logText -notmatch '\[FDZF INTERACTIVE SMOKE COMPLETE\]'){throw "双窗口启动验证失败：$($record.Log)"}
        }
        Write-Output 'PASS: 两个可视进程完成真实 ENet 握手、开局与队友资料同步。'
    }else{
        Write-Output '已连接：1号为房主，2号为客户端。两边分别操作原版开局、地图和战斗。'
        Write-Output '使用 Alt+Tab 切换，或 Win+左/右方向键并排。关闭任意一个游戏窗口，会结束本次双窗口测试。'
        Write-Output '本局使用内存测试存档；退出后不续局。此测试不覆盖 Steam 好友邀请。'
        while(@($records | Where-Object {$_.Process.HasExited}).Count -eq 0){Start-Sleep -Milliseconds 500}
    }
}finally{
    foreach($record in $records){if(!$record.Process.HasExited){$record.Process.Kill()}}
}
