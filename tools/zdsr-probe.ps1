#Requires -Version 5.1
<#
    争渡读屏接口（ZDSRAPI）自检工具
    ------------------------------------------------
    目的：在不启动游戏的情况下，单独检查「争渡的语音接口现在到底通不通」。
    模组里的争渡后端就是用这几个函数，所以这份报告能直接定位问题出在哪一环。

    用法（在装了争渡读屏的那台电脑上）：

        powershell -ExecutionPolicy Bypass -File zdsr-probe.ps1

    建议跑两次，好对比：
        1. 先关掉争渡读屏，跑一次
        2. 再打开争渡读屏，等它完全启动，再跑一次

    想顺便听一下接口能不能真的出声，加 -Speak：

        powershell -ExecutionPolicy Bypass -File zdsr-probe.ps1 -Speak

    接口 dll 位置特殊时可以指定：

        powershell -ExecutionPolicy Bypass -File zdsr-probe.ps1 -DllPath "D:\zdsr\zdsr\ZDSRAPI_x64.dll"

    报告会同时打印到屏幕，并写到桌面的 zdsr-probe-report.txt。
#>
[CmdletBinding()]
param(
    [string]$DllPath = "",
    [switch]$Speak
)

$ErrorActionPreference = 'Continue'
$lines = New-Object System.Collections.Generic.List[string]

function Say([string]$text) {
    Write-Host $text
    $lines.Add($text)
}

Say "==== 争渡读屏接口自检 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===="
Say ""

# ---------------------------------------------------------------- 1. 找 dll
Say "---- 1. 找 ZDSRAPI_x64.dll ----"
$dllName = if ([IntPtr]::Size -eq 8) { 'ZDSRAPI_x64.dll' } else { 'ZDSRAPI.dll' }
Say "本进程位数：$([IntPtr]::Size * 8) 位，所以找 $dllName"

$candidates = New-Object System.Collections.Generic.List[string]
if ($DllPath -ne '') { $candidates.Add($DllPath) }
foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
    if ($root) {
        $candidates.Add((Join-Path $root "zdsr\zdsr\$dllName"))
        $candidates.Add((Join-Path $root "zdsr\zdsr_yth\$dllName"))
        $candidates.Add((Join-Path $root "zdsr\$dllName"))
    }
}
$found = $null
foreach ($c in $candidates) {
    if (Test-Path -LiteralPath $c) { $found = $c; break }
}
if (-not $found) {
    # 兜底：常见根目录下凡是名字里带 zdsr / 争渡 的目录都翻一遍
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA, $env:APPDATA, $env:USERPROFILE)
    foreach ($r in $roots) {
        if (-not $r -or -not (Test-Path -LiteralPath $r)) { continue }
        foreach ($sub in (Get-ChildItem -LiteralPath $r -Directory -ErrorAction SilentlyContinue)) {
            if ($sub.Name -notmatch '(?i)zdsr|争渡') { continue }
            $hit = Get-ChildItem -LiteralPath $sub.FullName -Recurse -Filter $dllName -ErrorAction SilentlyContinue |
                   Select-Object -First 1
            if ($hit) { $found = $hit.FullName; break }
        }
        if ($found) { break }
    }
}

if (-not $found) {
    Say "没找到 $dllName。"
    Say "  找过这些位置："
    foreach ($c in $candidates) { Say "    $c" }
    Say "  → 请把争渡安装目录里的 $dllName 完整路径用 -DllPath 参数传进来。"
} else {
    Say "找到：$found"
    $fi = Get-Item -LiteralPath $found
    Say ("文件：{0} 字节，修改时间 {1}" -f $fi.Length, $fi.LastWriteTime)
    Say ("版本：{0} / {1}" -f $fi.VersionInfo.FileVersion, $fi.VersionInfo.CompanyName)
    Say ("SHA256：{0}" -f (Get-FileHash -LiteralPath $found -Algorithm SHA256).Hash)
}
Say ""

# ---------------------------------------------------------------- 2. 读屏进程
Say "---- 2. 现在有哪些读屏相关的进程 ----"
$procs = Get-Process -ErrorAction SilentlyContinue |
         Where-Object { $_.ProcessName -match '(?i)zdsr|nvda|^zd|争渡' }
if ($procs) {
    foreach ($p in $procs) {
        $path = ''
        try { $path = $p.Path } catch { }
        Say ("    {0}  PID={1}  {2}" -f $p.ProcessName, $p.Id, $path)
    }
    Say "  → 这里面应该有争渡读屏本体。请把这几行一起发回来（尤其是它的名字）。"
} else {
    Say "    （没有找到名字里带 zdsr / zd / nvda 的进程）"
    Say "  → 如果这时争渡明明开着，请把它的进程名告诉我：任务管理器里看一眼。"
}
Say ""

# ---------------------------------------------------------------- 3. 调接口
if (-not $found) {
    Say "---- 3. 跳过接口调用（没找到 dll）----"
} else {
    Say "---- 3. 调用接口 ----"
    $cs = @"
using System;
using System.Runtime.InteropServices;
public static class ZdsrProbe {
    [DllImport(@"$found", CallingConvention = CallingConvention.Cdecl)]
    public static extern int InitTTS(int type, IntPtr channelName, int keyDownInterrupt);
    [DllImport(@"$found", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Speak([MarshalAs(UnmanagedType.LPWStr)] string text, int interrupt);
    [DllImport(@"$found", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetSpeakState();
    [DllImport(@"$found", CallingConvention = CallingConvention.Cdecl)]
    public static extern void StopSpeak();
}
"@
    try {
        Add-Type -TypeDefinition $cs -ErrorAction Stop

        $rcInit = [ZdsrProbe]::InitTTS(0, [IntPtr]::Zero, 0)
        Say ("InitTTS(0, NULL, 0)  = {0}   {1}" -f $rcInit, $(if ($rcInit -eq 0) { '成功' } else { '失败' }))

        $st = [ZdsrProbe]::GetSpeakState()
        $stText = switch ($st) {
            1 { '接口版本不匹配' }
            2 { '争渡读屏没有运行' }
            3 { '正在朗读' }
            4 { '空闲（争渡在运行）' }
            default { '未知' }
        }
        Say ("GetSpeakState()      = {0}   {1}" -f $st, $stText)

        if ($st -eq 3 -or $st -eq 4) {
            Say "  → 接口认为争渡正在运行，这一环是通的。"
        } elseif ($st -eq 2) {
            Say "  → 接口认为争渡没在运行。若此时争渡确实开着，那就是接口自己的识别环节没认出来，"
            Say "    请把上面第 2 节的进程清单一起发回来。"
        }

        if ($Speak) {
            $rcSpeak = [ZdsrProbe]::Speak('这是争渡读屏接口自检。', 1)
            Say ("Speak(测试句, 打断)  = {0}   {1}" -f $rcSpeak, $(if ($rcSpeak -eq 0) { '成功（应该能听到这句话）' } else { '失败' }))
            Start-Sleep -Milliseconds 300
            [ZdsrProbe]::StopSpeak()
            Say "StopSpeak()          = 已调用"
        } else {
            Say "（没加 -Speak，所以没有真的朗读。想试发声就加 -Speak 再跑一次）"
        }
    } catch {
        Say ("调用接口时出错：{0}" -f $_.Exception.Message)
    }
}

Say ""
Say "==== 报告结束 ===="
Say "把这份报告（桌面上的 zdsr-probe-report.txt）发回来即可。"

$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'zdsr-probe-report.txt'
try {
    [System.IO.File]::WriteAllLines($out, $lines, (New-Object System.Text.UTF8Encoding $true))
    Write-Host ""
    Write-Host "报告已保存到：$out" -ForegroundColor Green
} catch {
    Write-Host "报告保存失败：$($_.Exception.Message)" -ForegroundColor Yellow
}
