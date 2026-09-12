#Requires -Version 5.1
<#
    争渡读屏接口（ZDSRAPI）自检工具
    ------------------------------------------------
    目的：不启动游戏，单独检查「争渡的语音接口现在到底通不通」。
    模组里的争渡后端用的就是这几个函数，所以这份报告能直接定位问题在哪一环。

    用法（在装了争渡读屏的那台电脑上，把本脚本和它的报告放在一起）：

        powershell -ExecutionPolicy Bypass -File zdsr-probe.ps1

    报告写在**本脚本同一个目录**里（zdsr-probe-report.txt），屏幕上也会打印一份。

    想顺便听一下接口能不能真的出声，加 -Speak：

        powershell -ExecutionPolicy Bypass -File zdsr-probe.ps1 -Speak

    接口 dll 位置特殊时可以指定：

        powershell -ExecutionPolicy Bypass -File zdsr-probe.ps1 -DllPath "D:\zdsr\zdsr\ZDSRAPI_x64.dll"

    建议跑两次对比：先关掉争渡读屏跑一次，再打开争渡读屏跑一次。
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
    $lines.Add([string]$text)
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

$installDir = $null
if (-not $found) {
    Say "没找到 $dllName。"
    Say "  找过这些位置："
    foreach ($c in $candidates) { Say "    $c" }
    Say "  → 请把争渡安装目录里的 $dllName 完整路径用 -DllPath 参数传进来。"
} else {
    Say "找到：$found"
    $fi = Get-Item -LiteralPath $found
    $installDir = $fi.DirectoryName
    Say ("文件：{0} 字节，修改时间 {1}" -f $fi.Length, $fi.LastWriteTime)
    Say ("版本：{0} / {1}" -f $fi.VersionInfo.FileVersion, $fi.VersionInfo.CompanyName)
    Say ("SHA256：{0}" -f (Get-FileHash -LiteralPath $found -Algorithm SHA256).Hash)
}
Say ""

# ---------------------------------------------------------------- 2. 读屏进程
Say "---- 2. 争渡目录里有哪些进程 ----"
Say "（不看进程名，只看可执行文件在不在争渡目录里 —— 各版本的进程名不一定一样）"
$procs = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -match '(?i)zdsr|nvda|^zd|争渡' -or
    ($installDir -and $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase))
}
$mainNames = @()
$zdsrCount = 0
if ($procs) {
    foreach ($p in $procs) {
        $path = ''
        try { $path = $p.Path } catch { }
        $title = ''
        try { $title = $p.MainWindowTitle } catch { }
        Say ("    {0}  PID={1}{2}{3}" -f $p.ProcessName, $p.Id,
             $(if ($title) { "  窗口「$title」" } else { '' }),
             $(if ($path) { "  $path" } else { '' }))

        # NVDA 是另一家的读屏，跟争渡的接口没关系，不参与下面的判断
        $isNvda = $p.ProcessName -match '(?i)nvda'
        $isZdsr = (-not $isNvda) -and (
            $p.ProcessName -match '(?i)zdsr|^zd|争渡' -or
            ($installDir -and $path -and $path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase)))
        if (-not $isZdsr) { continue }
        $zdsrCount++

        if ($p.ProcessName -notmatch '(?i)daemon|cloud|updat|helper' -and
            ($p.ProcessName -match '(?i)main' -or $p.ProcessName -match '(?i)zdsr|争渡')) {
            $mainNames += $p.ProcessName
        }
    }

    if ($zdsrCount -eq 0) {
        Say "  → 上面那些都不是争渡的进程 —— **争渡读屏没在运行**。"
        Say "    请从开始菜单/桌面快捷方式启动争渡读屏，确认它真的在给你读屏，再跑一次本脚本。"
    } elseif ($mainNames.Count -eq 0) {
        Say "  → **只看到守护进程，没看到读屏本体**（一般是 ZDSRMain_x64.exe）。"
        Say "    争渡的「读屏通道」要求读屏本体在运行 —— 请从开始菜单/桌面快捷方式"
        Say "    启动争渡读屏，确认它真的在给你读屏，再跑一次本脚本。"
    } else {
        Say ("  → 读屏本体在运行：" + ($mainNames -join '、'))
        Say "    如果这样接口还是报 2，那就只剩「没有授权」这一种解释（见下面第 4 节）。"
    }
} else {
    Say "    争渡目录里一个进程都没有 —— 争渡没在运行。"
}
Say ""

# ---------------------------------------------------------------- 3. ini
Say "---- 3. 接口目录里的 ZDSRAPI.ini ----"
if ($installDir) {
    $ini = Join-Path $installDir 'ZDSRAPI.ini'
    if (Test-Path -LiteralPath $ini) {
        Say "路径：$ini"
        Get-Content -LiteralPath $ini -Encoding Unicode -ErrorAction SilentlyContinue |
            Where-Object { $_ -and $_ -notmatch '^\s*[;\[]' } |
            ForEach-Object { Say ("    " + $_.Trim()) }
        Say "  （这个文件里的设置会**覆盖**程序调用 InitTTS 时传的参数）"
    } else {
        Say "    没有这个文件（等于全用默认值）。"
    }
} else {
    Say "    跳过（没找到 dll，不知道接口目录在哪）。"
}
Say ""

# ---------------------------------------------------------------- 4. 调接口
if (-not $found) {
    Say "---- 4. 跳过接口调用（没找到 dll）----"
} else {
    Say "---- 4. 调用接口 ----"
    Say "先解释一下返回码，免得看串："
    Say "    1 = 接口版本不匹配"
    Say "    2 = 争渡读屏**没有运行或没有授权**"
    Say "    3 = 正在朗读； 4 = 空闲（这两个才说明争渡在，接口能用）"
    Say ""

    $cs = @"
using System;
using System.Runtime.InteropServices;
public static class ZdsrProbe {
    [DllImport(@"$found", CallingConvention = CallingConvention.Cdecl)]
    public static extern int InitTTS(int type, IntPtr channelName, int keyDownInterrupt);
    [DllImport(@"$found", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "InitTTS")]
    public static extern int InitTTSName(int type, string channelName, int keyDownInterrupt);
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

        function StateText($st) {
            switch ($st) {
                1 { '接口版本不匹配' }
                2 { '争渡读屏没有运行或没有授权' }
                3 { '正在朗读' }
                4 { '空闲（争渡在运行）' }
                default { "未知（$st）" }
            }
        }

        # ---- 读屏通道 type=0
        Say "[读屏通道 type=0]（走争渡自己的语音与设置，最理想）"
        $rc = [ZdsrProbe]::InitTTS(0, [IntPtr]::Zero, 0)
        Say ("    InitTTS(0, NULL, 0) = {0}  {1}" -f $rc, $(if ($rc -eq 0) { '成功' } else { '失败' }))
        $st0 = [ZdsrProbe]::GetSpeakState()
        Say ("    GetSpeakState()     = {0}  {1}" -f $st0, (StateText $st0))
        if ($st0 -eq 3 -or $st0 -eq 4) { Say "  → 读屏通道可用。" } else { Say "  → 读屏通道不可用。" }
        Say ""

        # ---- 独立通道 type=1
        Say "[独立通道 type=1]（争渡接口自己开的一条通道，不要求读屏本体在运行）"
        $rc1 = [ZdsrProbe]::InitTTSName(1, 'ZdsrProbe', 0)
        Say ("    InitTTS(1, `"ZdsrProbe`", 0) = {0}  {1}" -f $rc1, $(if ($rc1 -eq 0) { '成功' } else { '失败' }))
        $st1 = [ZdsrProbe]::GetSpeakState()
        Say ("    GetSpeakState()             = {0}  {1}" -f $st1, (StateText $st1))
        Say ""

        # ---- 试发声
        if ($Speak) {
            Say "[-Speak] 逐条通道试一句："
            foreach ($pair in @(@(0, '读屏通道'), @(1, '独立通道'))) {
                $type = [int]$pair[0]
                $label = [string]$pair[1]
                if ($type -eq 0) { [void][ZdsrProbe]::InitTTS(0, [IntPtr]::Zero, 0) }
                else { [void][ZdsrProbe]::InitTTSName(1, 'ZdsrProbe', 0) }
                $rcS = [ZdsrProbe]::Speak("这是争渡读屏接口自检，$label。", 1)
                Say ("    {0} Speak = {1}  {2}" -f $label, $rcS,
                     $(if ($rcS -eq 0) { '成功（应该能听到这句话）' } else { '失败' }))
                Start-Sleep -Milliseconds 200
                [ZdsrProbe]::StopSpeak()
            }
        } else {
            Say "（没加 -Speak，所以没有真的朗读。想试发声就加 -Speak 再跑一次）"
        }
    } catch {
        Say ("调用接口时出错：{0}" -f $_.Exception.Message)
    }
}

Say ""
Say "==== 报告结束 ===="

$outDir = $PSScriptRoot
if (-not $outDir) { $outDir = (Get-Location).Path }
$out = Join-Path $outDir 'zdsr-probe-report.txt'
try {
    [System.IO.File]::WriteAllLines($out, $lines, (New-Object System.Text.UTF8Encoding $true))
    Write-Host ""
    Write-Host "报告已保存到：$out" -ForegroundColor Green
} catch {
    Write-Host "报告保存失败（$out）：$($_.Exception.Message)" -ForegroundColor Yellow
}
