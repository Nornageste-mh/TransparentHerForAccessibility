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

$n = (Get-ChildItem $pkg -Recurse -File -Force).Count
Ok "安装包就绪：$pkg（$n 个文件）"

Write-Host "`n构建完成。安装：把 $pkg 里的东西全部拷进游戏根目录（有 TransparentHer.exe 的那一层）。`n" -ForegroundColor Cyan
