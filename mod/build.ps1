#Requires -Version 5.1
<#
    可复现构建：下载依赖 → 编译插件 → 组装安装包

    用法：
        cd D:\DSHWorkBase\transparenther_a11y\mod
        .\build.ps1

    产物：
        .\package\    ← 直接拷进游戏根目录即可用

    安装包不含安装程序：补丁是 BepInEx 运行时挂载的，不修改游戏文件，
    安装就是「把 package\ 里的东西拷进有 TransparentHer.exe 的那一层」。
    手写说明见 package\安装说明.txt。

    安装包里带了第三方二进制（BepInEx、UnityDoorstop、HarmonyX、Mono.Cecil、
    MonoMod、NVDA Controller Client），它们的许可证与来源说明放在 licenses\，
    由本脚本从 .\licenses\ 复制进去（LGPL-2.1 要求随二进制分发附上许可证文本）。
#>
[CmdletBinding()]
param(
    [string]$GameDir = "D:\Steam\steamapps\common\Transparenther",
    # 依赖版本（如需升级改这里）
    [string]$BepInExVersion = "5.4.23.5",
    [string]$NvdaClientUrl  = "https://download.nvaccess.org/releases/stable/nvda_2026.2_controllerClient.zip"
)

$ErrorActionPreference = "Stop"
$mod = $PSScriptRoot
$dl  = Join-Path $mod "dl"
New-Item -ItemType Directory -Force -Path $dl | Out-Null

function Step($msg) { Write-Host "`n>>> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }

# ------------------------------------------------------------
Step "1/5  检查游戏目录"
if (-not (Test-Path -LiteralPath (Join-Path $GameDir "TransparentHer.exe"))) {
    Write-Host "    找不到游戏：$GameDir" -ForegroundColor Red
    Write-Host "    用 -GameDir 参数指定游戏根目录。"
    exit 1
}
Ok $GameDir

# ------------------------------------------------------------
Step "2/5  获取 BepInEx $BepInExVersion (win x64)"
$bepZip = Join-Path $dl "BepInEx_win_x64_$BepInExVersion.zip"
$bepDir = Join-Path $mod "bep_dl"
if (Test-Path -LiteralPath (Join-Path $bepDir "BepInEx\core\BepInEx.dll")) {
    Ok "已存在，跳过下载"
} else {
    if (-not (Test-Path -LiteralPath $bepZip)) {
        $url = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"
        Invoke-WebRequest -Uri $url -OutFile $bepZip -UseBasicParsing -TimeoutSec 180
    }
    if (Test-Path -LiteralPath $bepDir) { Remove-Item $bepDir -Recurse -Force }
    Expand-Archive -LiteralPath $bepZip -DestinationPath $bepDir -Force
    Ok "已解压到 bep_dl\"
}

# ------------------------------------------------------------
Step "3/5  获取 NVDA Controller Client (x64)"
$nvdaZip = Join-Path $dl "nvda_controllerClient.zip"
$nvdaDir = Join-Path $mod "nvda_dl"
$nvdaDll = $null
if (Test-Path -LiteralPath $nvdaDir) {
    $nvdaDll = Get-ChildItem $nvdaDir -Recurse -File -Filter "nvdaControllerClient.dll" |
               Where-Object { $_.DirectoryName -like "*x64*" } | Select-Object -First 1
}
if ($nvdaDll) {
    Ok "已存在，跳过下载"
} else {
    if (-not (Test-Path -LiteralPath $nvdaZip)) {
        Invoke-WebRequest -Uri $NvdaClientUrl -OutFile $nvdaZip -UseBasicParsing -TimeoutSec 300
    }
    Expand-Archive -LiteralPath $nvdaZip -DestinationPath $nvdaDir -Force
    $nvdaDll = Get-ChildItem $nvdaDir -Recurse -File -Filter "nvdaControllerClient.dll" |
               Where-Object { $_.DirectoryName -like "*x64*" } | Select-Object -First 1
    if (-not $nvdaDll) { Write-Host "    压缩包里找不到 x64\nvdaControllerClient.dll" -ForegroundColor Red; exit 1 }
    Ok $nvdaDll.FullName
}

# ------------------------------------------------------------
Step "4/5  编译插件"

# 发布前守卫：BepInPlugin 的版本号必须能被解析成版本号。
# 它不是给人看的显示名 —— 填 "0.5.8a" 这类带字母的写法，BepInEx 会判定
# 「version is invalid」并**静默跳过整个插件**：日志里只有一行 Warning，
# 表现是模组完全没加载、游戏里一片安静，极难排查（v0.5.8a 第一版就踩了）。
# 想表达 0.5.8a 这种「第五版修订」，用第四位数字：0.5.8.1。
$pluginCs = Join-Path $mod "src\TransparentHerA11y\Plugin.cs"
$m = Select-String -LiteralPath $pluginCs -Pattern 'BepInPlugin\([^)]*"([^"]+)"\s*\)' | Select-Object -First 1
if (-not $m) { Write-Host "    在 Plugin.cs 里找不到 BepInPlugin 特性" -ForegroundColor Red; exit 1 }
$pluginVer = $m.Matches[0].Groups[1].Value
$parsed = $null
if (-not [System.Version]::TryParse($pluginVer, [ref]$parsed)) {
    Write-Host "    BepInPlugin 版本号 `"$pluginVer`" 不是合法版本号。" -ForegroundColor Red
    Write-Host "    BepInEx 会因此跳过整个插件（日志：version is invalid）。"
    Write-Host "    请改成纯数字形式，例如 0.5.8.1"
    exit 1
}
try {
    $asmVer = ([xml](Get-Content -LiteralPath (Join-Path $mod "src\TransparentHerA11y\TransparentHerA11y.csproj") -Raw)).Project.PropertyGroup.Version
    if ($asmVer -and $asmVer.Trim() -ne $pluginVer) {
        Write-Host "    版本号不一致：csproj=$asmVer  BepInPlugin=$pluginVer" -ForegroundColor Red
        Write-Host "    两处必须一致，否则插件版本与程序集版本会对不上。"
        exit 1
    }
} catch { Write-Host "    （csproj 版本号读取失败，已跳过一致性检查）" -ForegroundColor Yellow }
Ok "版本号 $pluginVer（合法，且与 csproj 一致）"
Push-Location (Join-Path $mod "src\TransparentHerA11y")
try {
    dotnet build -c Release -v minimal -nowarn:MSB3277
    if ($LASTEXITCODE -ne 0) { throw "编译失败" }
} finally { Pop-Location }
$pluginDll = Join-Path $mod "src\TransparentHerA11y\bin\Release\TransparentHerA11y.dll"
if (-not (Test-Path -LiteralPath $pluginDll)) { Write-Host "    找不到编译产物" -ForegroundColor Red; exit 1 }
Ok $pluginDll

# ------------------------------------------------------------
Step "5/5  组装安装包"
$pkg = Join-Path $mod "package"
# 保留自产的说明文件，其余重建
$keep = @("安装说明.txt")
$saved = @{}
foreach ($k in $keep) {
    $p = Join-Path $pkg $k
    if (Test-Path -LiteralPath $p) { $saved[$k] = Get-Content -LiteralPath $p -Raw -Encoding UTF8 }
}
if (Test-Path -LiteralPath $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pkg | Out-Null
foreach ($k in $saved.Keys) {
    [System.IO.File]::WriteAllText((Join-Path $pkg $k), $saved[$k], (New-Object System.Text.UTF8Encoding $true))
}

Copy-Item (Join-Path $bepDir "*") -Destination $pkg -Recurse -Force
$plugDir = Join-Path $pkg "BepInEx\plugins"
New-Item -ItemType Directory -Force -Path $plugDir | Out-Null
Copy-Item $pluginDll -Destination $plugDir -Force
Copy-Item $nvdaDll.FullName -Destination $plugDir -Force
# 同时放一份到包根：Mono 的 DllImport 会先查应用目录
Copy-Item $nvdaDll.FullName -Destination (Join-Path $pkg "nvdaControllerClient.dll") -Force

# 第三方许可证与来源说明（随二进制分发的合规要求）
$licDir = Join-Path $mod "licenses"
if (Test-Path -LiteralPath $licDir) {
    Copy-Item $licDir -Destination $pkg -Recurse -Force
    Ok "已附上第三方许可证：licenses\（$((Get-ChildItem $licDir -Recurse -File).Count) 个文件）"
} else {
    Write-Host "    警告：找不到 licenses\，安装包将缺少第三方许可证" -ForegroundColor Yellow
}

$n = (Get-ChildItem $pkg -Recurse -File -Force).Count
Ok "安装包就绪：$pkg（$n 个文件）"

Write-Host "`n构建完成。安装：把 $pkg 里的东西全部拷进游戏根目录（有 TransparentHer.exe 的那一层）。`n" -ForegroundColor Cyan
