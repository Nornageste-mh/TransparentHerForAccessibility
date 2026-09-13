# 打 ZIP：条目名必须用正斜杠。
# PowerShell 5.1 的 Compress-Archive 会把分隔符写成反斜杠（.NET Framework 的
# ZipFile.CreateFromDirectory 用的是 Path.DirectorySeparatorChar），那不是合法
# ZIP 路径：Windows 资源管理器能忍，7-Zip / Linux unzip / Python zipfile 会把
# "BepInEx\core\BepInEx.dll" 当成一个文件名里带反斜杠的平铺条目。
# 所以这里手工建条目，名字自己保证是 '/'。
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Zip
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path -LiteralPath $Source)) { throw "找不到源目录: $Source" }
$srcFull = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')

if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }
$zipDir = Split-Path $Zip -Parent
if (-not (Test-Path -LiteralPath $zipDir)) { New-Item -ItemType Directory -Force -Path $zipDir | Out-Null }

$fs = [System.IO.File]::Open($Zip, [System.IO.FileMode]::CreateNew)
try {
    $arch = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create, $true)

    # 目录条目（保留空目录；也让解压工具能建出目录结构）
    foreach ($d in (Get-ChildItem -LiteralPath $Source -Recurse -Directory -Force)) {
        $rel = $d.FullName.Substring($srcFull.Length + 1).Replace('\', '/') + '/'
        $null = $arch.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::NoCompression)
    }

    foreach ($f in (Get-ChildItem -LiteralPath $Source -Recurse -File -Force)) {
        $rel = $f.FullName.Substring($srcFull.Length + 1).Replace('\', '/')
        $entry = $arch.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
        $es = $entry.Open()
        try {
            $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
            $es.Write($bytes, 0, $bytes.Length)
        } finally { $es.Dispose() }
    }

    $arch.Dispose()
} finally { $fs.Dispose() }

Write-Host ("已生成: " + $Zip + "  " + [Math]::Round((Get-Item -LiteralPath $Zip).Length / 1KB, 1) + " KB")
