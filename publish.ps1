# 一键发布到 GitHub（需要先安装并登录 GitHub CLI）
# 用法： .\publish.ps1            -> 创建 public 仓库并推送
#        .\publish.ps1 -Visibility private

param(
    [string]$RepoName = "logi-battery-tray",
    [ValidateSet("public", "private")]
    [string]$Visibility = "public"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Test-Path ".git")) {
    throw "当前目录不是 git 仓库，请先运行 git init"
}

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    Write-Host "未检测到 GitHub CLI (gh)。" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "请先安装（任选其一）："
    Write-Host "  1) winget install --id GitHub.cli -e"
    Write-Host "  2) 安装 GitHub Desktop：File -> Add local repository -> Publish repository"
    Write-Host ""
    exit 1
}

gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "尚未登录 GitHub，即将打开浏览器登录…" -ForegroundColor Yellow
    gh auth login
    if ($LASTEXITCODE -ne 0) { throw "登录失败" }
}

if (git remote get-url origin *> $null) {
    Write-Host "已存在 origin，直接推送…"
    git push -u origin HEAD
}
else {
    gh repo create $RepoName --$Visibility --source . --remote origin --push
}

Write-Host ""
Write-Host "完成。仓库地址：" -ForegroundColor Green
gh repo view --json url -q .url
