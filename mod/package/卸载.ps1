#Requires -Version 5.1
<#
    卸载《透明的她与真实的我》NVDA 朗读模组
    - 只删除本模组新增的文件
    - 绝不触碰游戏原有文件
#>
[CmdletBinding()]
param(
    [string]$GameDir = "D:\Steam\steamapps\common\Transparenther"
)

$ErrorActionPreference = "Continue"

function Say($msg, $color = "Gray") { Write-Host $msg -ForegroundColor $color }

Say "=== 透明的她与真实的我 · NVDA 朗读模组 卸载程序 ===" "Cyan"
Say ""

if (-not (Test-Path -LiteralPath (Join-Path $GameDir "TransparentHer.exe"))) {
    Say "找不到游戏目录：$GameDir" "Red"
    Say "请用 -GameDir 参数指定。"
    exit 1
}
Say "游戏目录：$GameDir" "Green"
Say ""

# 只删这些（全部是本模组新增的）
$files = @(
    "winhttp.dll",
    "doorstop_config.ini",
    ".doorstop_version",
    "changelog.txt",
    "nvdaControllerClient.dll"
)
$dirs = @(
    "BepInEx"
)

$n = 0
foreach ($f in $files) {
    $p = Join-Path $GameDir $f
    if (Test-Path -LiteralPath $p) {
        Remove-Item -LiteralPath $p -Force
        Say "  已删除 $f" "Green"
        $n++
    }
}
foreach ($d in $dirs) {
    $p = Join-Path $GameDir $d
    if (Test-Path -LiteralPath $p) {
        Remove-Item -LiteralPath $p -Recurse -Force
        Say "  已删除 $d\" "Green"
        $n++
    }
}

Say ""
if ($n -eq 0) {
    Say "没有找到本模组的文件，可能已经卸载过了。" "Yellow"
} else {
    Say "卸载完成，共移除 $n 项。" "Cyan"
    Say "游戏已恢复原样。" "Cyan"
}
