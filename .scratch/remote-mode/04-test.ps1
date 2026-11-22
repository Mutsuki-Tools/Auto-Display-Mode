# 票 04 手動實測用的指令碼（見 04-manual-test.md）。
# 用法：powershell -ExecutionPolicy Bypass -File .scratch\remote-mode\04-test.ps1 <動作> [-Delay 秒] [-Seconds 秒]
#   status        列出主程式與守護程式（含父程序），以及 cfg 的目前模式、復原模式、PreviousMode
#   kill          等 Delay 秒後強制結束主程式（不動守護程式）
#   killall       等 Delay 秒後強制結束主程式與守護程式
#   killguardian  等 Delay 秒後只結束守護程式
#   hang          等 Delay 秒後暫停主程式的所有執行緒 Seconds 秒（模擬卡住），再恢復
#   logoff        等 Delay 秒後登出
#   reboot        等 Delay 秒後重新開機
# 時間到要動作時會嗶一聲，方便螢幕全暗時知道進度。

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('status', 'kill', 'killall', 'killguardian', 'hang', 'logoff', 'reboot')]
    [string]$Action,
    [int]$Delay = 20,
    [int]$Seconds = 30
)

$cfg = Join-Path $PSScriptRoot '..\..\release\AutoDisplayMode.cfg'

function Get-AdmProcesses {
    Get-CimInstance Win32_Process -Filter "Name='AutoDisplayMode.exe'" | ForEach-Object {
        $role = if ($_.CommandLine -match '--guardian-launch') { '中繼程序' }
                elseif ($_.CommandLine -match '--guardian ') { '守護程式' }
                elseif ($_.CommandLine -match '--(un)?install-driver') { '驅動安裝' }
                else { '主程式' }
        [pscustomobject]@{ 角色 = $role; PID = $_.ProcessId; 父程序 = $_.ParentProcessId; 開始 = $_.CreationDate }
    }
}

function Show-Status {
    $p = @(Get-AdmProcesses)
    if ($p.Count -eq 0) { Write-Host '沒有 AutoDisplayMode.exe 在執行' }
    else { $p | Format-Table -AutoSize | Out-String | Write-Host }
    if (Test-Path $cfg) {
        $lines = Get-Content $cfg -Encoding UTF8
        $modes = @{}
        $id = $null
        foreach ($l in $lines) {
            if ($l -match '^\[Mode\.(\d+)\]$') { $id = $Matches[1] }
            elseif ($id -and $l -match '^Name=(.*)$') { $modes[$id] = $Matches[1]; $id = $null }
        }
        foreach ($key in 'CurrentMode', 'RestoreMode', 'PreviousMode') {
            $v = ($lines | Where-Object { $_ -match "^$key=" } | Select-Object -First 1) -replace "^$key=", ''
            Write-Host ("{0,-13} {1} {2}" -f $key, $v, $modes[$v])
        }
        Write-Host ('cfg 修改時間  ' + (Get-Item $cfg).LastWriteTime.ToString('HH:mm:ss'))
    }
}

function Wait-Countdown([int]$s) {
    for ($i = $s; $i -gt 0; $i--) {
        Write-Host -NoNewline "`r$i 秒後執行「$Action」…  "
        Start-Sleep 1
    }
    Write-Host ''
    [console]::beep(1000, 300)
}

function Get-Main { Get-AdmProcesses | Where-Object { $_.角色 -eq '主程式' } }

if ($Action -eq 'status') { Show-Status; return }

Write-Host '現在的狀態：'
Show-Status
Wait-Countdown $Delay
Write-Host ((Get-Date).ToString('HH:mm:ss') + ' 執行')

switch ($Action) {
    'kill' {
        Get-Main | ForEach-Object { Stop-Process -Id $_.PID -Force; Write-Host "已結束主程式 $($_.PID)" }
    }
    'killall' {
        Get-AdmProcesses | Sort-Object { $_.角色 -ne '守護程式' } | ForEach-Object {
            Stop-Process -Id $_.PID -Force -ErrorAction SilentlyContinue; Write-Host "已結束$($_.角色) $($_.PID)"
        }
    }
    'killguardian' {
        Get-AdmProcesses | Where-Object { $_.角色 -eq '守護程式' } | ForEach-Object { Stop-Process -Id $_.PID -Force; Write-Host "已結束守護程式 $($_.PID)" }
    }
    'hang' {
        Add-Type -Namespace Adm -Name Nt -MemberDefinition @'
[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
[DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
'@
        $main = Get-Main | Select-Object -First 1
        $proc = Get-Process -Id $main.PID
        [Adm.Nt]::NtSuspendProcess($proc.Handle) | Out-Null
        Write-Host "已暫停主程式 $($main.PID)，$Seconds 秒後恢復"
        Start-Sleep $Seconds
        [Adm.Nt]::NtResumeProcess($proc.Handle) | Out-Null
        [console]::beep(600, 300)
        Write-Host ((Get-Date).ToString('HH:mm:ss') + ' 已恢復主程式')
    }
    'logoff' { shutdown /l }
    'reboot' { shutdown /r /t 0 }
}

if ($Action -in 'kill', 'killall', 'killguardian', 'hang') {
    Write-Host '15 秒後再看一次狀態…'
    Start-Sleep 15
    Show-Status
}
