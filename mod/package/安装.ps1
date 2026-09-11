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

# ---- 2. 冲突检查（绝不覆盖）----
$targets = @(
    "winhttp.dll",
    "doorstop_config.ini",
    ".doorstop_version",
    "changelog.txt",
    "BepInEx"
)
$conflicts = @()
foreach ($t in $targets) {
    $p = Join-Path $GameDir $t
    if (Test-Path -LiteralPath $p) {
        # BepInEx 目录已存在视为「已安装」，允许继续（更新插件）
        if ($t -eq "BepInEx") { continue }
        $conflicts += $t
    }
}
if ($conflicts.Count -gt 0) {
    Say ""
    Say "检测到以下文件已存在，本程序不会覆盖它们：" "Yellow"
    foreach ($c in $conflicts) { Say "  · $c" "Yellow" }
    Say ""
    Say "可能原因：已安装过 BepInEx / 其他模组，或游戏目录不干净。" "Yellow"
    Say "请先手动处理这些文件，然后重新运行。" "Yellow"
    exit 2
}

# ---- 3. 复制文件 ----
Say ""
Say "开始复制文件（仅新增，不覆盖）..."
$copied = 0
Get-ChildItem -LiteralPath $pkg -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($pkg.Length).TrimStart('\')
    if ($rel -eq "安装.ps1" -or $rel -eq "卸载.ps1" -or $rel -eq "安装说明.txt") { return }
    $dest = Join-Path $GameDir $rel
    $destDir = Split-Path $dest -Parent
    if (-not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    }
    if (Test-Path -LiteralPath $dest) {
        Say "  跳过（已存在）：$rel" "DarkGray"
    } else {
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
        $script:copied++
    }
}
Say "已新增 $copied 个文件。" "Green"

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
    Say "安装完成！" "Cyan"
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
