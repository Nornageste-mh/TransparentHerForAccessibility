#Requires -Version 5.1
<#
    提交前安全闸门：确认暂存区里没有任何游戏版权内容或第三方二进制。

    背景：本仓库的分析产物（提取的剧本文本、反编译的游戏代码）属于游戏
    著作权人的资产，一旦提交并推送即构成再分发。这个检查曾抓到
    mod/verify/ 目录漏网（.gitignore 只写了根级 /verify/）。

    用法：
        .\tools\check-staged.ps1          # 检查
        git commit ...                    # 通过后再提交
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    # core.quotepath=false：否则 git 会把中文文件名输出成 \345\215\270 这样的
    # 八进制转义，反斜杠在 Windows 路径里非法，GetExtension 会直接抛异常
    $staged = @(git -c core.quotepath=false diff --cached --name-only)
    if ($staged.Count -eq 0) {
        Write-Host "暂存区为空，没有要检查的内容。" -ForegroundColor Yellow
        exit 0
    }

    # 禁止出现的路径片段
    $forbidden = @(
        'extracted/',
        'decompiled/',
        'verify/',
        'textassets/',
        'bep_dl/',
        'nvda_dl/',
        'addressable_ids.txt'
    )
    # 禁止出现的文件扩展名
    $forbiddenExt = @('.dll', '.zip', '.exe', '.pdb')

    $violations = @()
    foreach ($f in $staged) {
        foreach ($bad in $forbidden) {
            if ($f -like "*$bad*") { $violations += "$f   (命中禁用路径: $bad)"; break }
        }
        try { $ext = [System.IO.Path]::GetExtension($f).ToLower() } catch { $ext = "" }
        if ($forbiddenExt -contains $ext) { $violations += "$f   (禁用扩展名: $ext)" }
    }

    Write-Host "暂存文件数: $($staged.Count)" -ForegroundColor Gray
    if ($violations.Count -gt 0) {
        Write-Host "`n禁止提交以下内容：" -ForegroundColor Red
        $violations | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Write-Host "`n这些是游戏版权内容或第三方二进制，入库并推送等于再分发。" -ForegroundColor Red
        Write-Host "请修正 .gitignore 后重试。" -ForegroundColor Red
        exit 1
    }

    Write-Host "通过：暂存区没有版权内容或二进制。" -ForegroundColor Green
    exit 0
} finally { Pop-Location }
