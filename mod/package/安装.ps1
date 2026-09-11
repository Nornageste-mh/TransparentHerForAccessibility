#Requires -Version 5.1
<#
    安装《透明的她与真实的我》NVDA 朗读模组
    - 只新增文件，绝不覆盖游戏原有文件
    - 发现冲突会直接中止并列出清单
#>
[CmdletBinding()]
param(
    [string]$GameDir = "D:\Steam\steamapps\common\Transparenther"
)

$ErrorActionPreference = "Stop"
$pkg = $PSScriptRoot

function Say($msg, $color = "Gray") { Write-Host $msg -ForegroundColor $color }

Say "=== 透明的她与真实的我 · NVDA 朗读模组 安装程序 ===" "Cyan"
Say ""

# ---- 1. 定位游戏 ----
if (-not (Test-Path -LiteralPath $GameDir)) {
    # 尝试从 Steam 库配置自动查找
    $vdf = "D:\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path -LiteralPath $vdf) {
        $m = Select-String -LiteralPath $vdf -Pattern '"path"\s+"(.+?)"' -AllMatches
        foreach ($hit in $m.Matches) {
            $lib = $hit.Groups[1].Value -replace '\\\\', '\'
            $cand = Join-Path $lib "steamapps\common\Transparenther"
            if (Test-Path -LiteralPath $cand) { $GameDir = $cand; break }
        }
    }
}

if (-not (Test-Path -LiteralPath $GameDir)) {
    Say "找不到游戏目录，请手动指定：" "Red"
    Say "  .\安装.ps1 -GameDir 'X:\你的路径\Transparenther'"
    exit 1
}

if (-not (Test-Path -LiteralPath (Join-Path $GameDir "TransparentHer.exe"))) {
    Say "该目录下没有 TransparentHer.exe，可能不是游戏根目录：$GameDir" "Red"
    exit 1
}
Say "游戏目录：$GameDir" "Green"

# ---- 2. 判定安装模式 ----
$plugRel  = "BepInEx\plugins\TransparentHerA11y.dll"
$coreRel  = "BepInEx\core\BepInEx.Preloader.dll"
$hasWinhttp = Test-Path -LiteralPath (Join-Path $GameDir "winhttp.dll")
$hasCore    = Test-Path -LiteralPath (Join-Path $GameDir $coreRel)

if ($hasWinhttp -and $hasCore) {
    $mode = "upgrade"
    $prev = ""
    try { $prev = (Get-Item -LiteralPath (Join-Path $GameDir $plugRel)).VersionInfo.FileVersion } catch { }
    Say "检测到已安装的模组$(if ($prev) { "（当前版本 $prev）" })，执行升级。" "Cyan"
}
elseif ($hasWinhttp -or $hasCore) {
    Say ""
    Say "检测到不完整或外来的 BepInEx 安装：" "Yellow"
    if ($hasWinhttp) { Say "  · winhttp.dll 存在，但没有 $coreRel" "Yellow" }
    if ($hasCore)    { Say "  · $coreRel 存在，但没有 winhttp.dll" "Yellow" }
    Say ""
    Say "本程序不会覆盖来路不明的文件，以免破坏游戏。" "Yellow"
    Say "请先手动清理后重试。" "Yellow"
    exit 2
}
else {
    $mode = "fresh"
    Say "全新安装。" "Cyan"
}

# ---- 3. 复制文件 ----
$pkgVer = ""
try { $pkgVer = (Get-Item -LiteralPath (Join-Path $pkg $plugRel)).VersionInfo.FileVersion } catch { }
Say ""
if ($mode -eq "upgrade") {
    Say "开始更新$(if ($pkgVer) { "到 $pkgVer " })（覆盖本模组自己的文件）..."
} else {
    Say "开始复制$(if ($pkgVer) { " $pkgVer" })（仅新增，不覆盖）..."
}

$copied = 0; $updated = 0; $skipped = 0
Get-ChildItem -LiteralPath $pkg -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($pkg.Length).TrimStart('\')
    if ($rel -eq "安装.ps1" -or $rel -eq "卸载.ps1" -or $rel -eq "安装说明.txt") { return }
    $dest = Join-Path $GameDir $rel
    $destDir = Split-Path $dest -Parent
    if (-not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    }

    if (Test-Path -LiteralPath $dest) {
        if ($mode -eq "upgrade") {
            # 升级模式：只覆盖本模组自己的文件，且内容不同才动
            $same = $false
            try {
                $same = ((Get-FileHash -LiteralPath $_.FullName -Algorithm MD5).Hash -eq
                         (Get-FileHash -LiteralPath $dest -Algorithm MD5).Hash)
            } catch { }
            if ($same) { $script:skipped++ }
            else {
                Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
                Say "  已更新：$rel" "Green"
                $script:updated++
            }
        } else {
            Say "  跳过（已存在）：$rel" "DarkGray"
            $script:skipped++
        }
    } else {
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
        $script:copied++
    }
}
Say "新增 $copied 个，更新 $updated 个，跳过 $skipped 个。" "Green"

# ---- 4. 校验 ----
Say ""
$checks = @(
    "winhttp.dll",
    "doorstop_config.ini",
    "BepInEx\core\BepInEx.Preloader.dll",
    "BepInEx\plugins\TransparentHerA11y.dll",
    "BepInEx\plugins\nvdaControllerClient.dll"
)
$allOk = $true
foreach ($c in $checks) {
    $p = Join-Path $GameDir $c
    if (Test-Path -LiteralPath $p) { Say "  OK   $c" "Green" }
    else { Say "  缺失 $c" "Red"; $allOk = $false }
}

Say ""
if ($allOk) {
    if ($mode -eq "upgrade") { Say "升级完成！" "Cyan" } else { Say "安装完成！" "Cyan" }
    Say ""
    Say "启动游戏前请确认：" "White"
    Say "  1. NVDA 正在运行"
    Say "  2. 从 Steam 正常启动游戏（不要直接双击 exe）"
    Say ""
    Say "配置文件（首次启动游戏后生成）："
    Say "  $GameDir\BepInEx\config\transparenther.a11y.reader.cfg"
    Say "运行日志："
    Say "  $GameDir\BepInEx\LogOutput.log"
} else {
    Say "安装不完整，请检查上面的缺失项。" "Red"
    exit 3
}
