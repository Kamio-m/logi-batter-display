# 一键发布到 GitHub
# 用法：
#   .\publish.ps1                -> 创建 public 仓库并推送
#   .\publish.ps1 -Visibility private
#   .\publish.ps1 -CheckOnly     -> 只检查 gh 与登录状态

param(
    [string]$RepoName = "logi-battery-tray",
    [ValidateSet("public", "private")]
    [string]$Visibility = "public",
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Test-Path ".git")) {
    throw "当前目录不是 git 仓库，请先运行 git init"
}

# 优先用 PATH 里的 gh，找不到就去默认安装目录找
$ghCommand = Get-Command gh -ErrorAction SilentlyContinue
$ghExe = if ($ghCommand) { $ghCommand.Source } else { $null }
if (-not $ghExe) {
    $candidates = @(
        "$env:ProgramFiles\GitHub CLI\gh.exe",
        "${env:ProgramFiles(x86)}\GitHub CLI\gh.exe",
        "$env:LOCALAPPDATA\GitHubCLI\gh.exe",
        "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe"
    )
    $ghExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $ghExe) {
    Write-Host "未检测到 GitHub CLI (gh)。" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "请先安装（任选其一）："
    Write-Host "  1) winget install --id GitHub.cli -e"
    Write-Host "  2) 安装 GitHub Desktop：File -> Add local repository -> Publish repository"
    Write-Host ""
    exit 1
}

Write-Host "GitHub CLI: $ghExe"

$previousEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $ghExe auth status *> $null
$loggedIn = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $previousEap

if ($CheckOnly) {
    Write-Host ("登录状态: " + $(if ($loggedIn) { "已登录" } else { "未登录（运行 .\publish.ps1 会引导登录）" }))
    exit 0
}

if (-not $loggedIn) {
    if ($env:GH_TOKEN -or $env:GITHUB_TOKEN) {
        Write-Host "检测到 GH_TOKEN / GITHUB_TOKEN 环境变量，跳过交互登录。"
    }
    else {
        Write-Host "尚未登录 GitHub，即将打开浏览器授权…" -ForegroundColor Yellow
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        & $ghExe auth login --hostname github.com --git-protocol https --web 2>&1 |
            ForEach-Object { Write-Host $_ }
        $loginExit = $LASTEXITCODE
        $ErrorActionPreference = $previousEap

        if ($loginExit -ne 0) {
            Write-Host ""
            Write-Host "登录失败。常见原因：" -ForegroundColor Red
            Write-Host "  1) 网络到不了 github.com —— 需要代理，先设置："
            Write-Host '       set HTTPS_PROXY=http://127.0.0.1:7890'
            Write-Host '       set HTTP_PROXY=http://127.0.0.1:7890'
            Write-Host "     然后重新运行 publish.cmd"
            Write-Host "  2) 浏览器没有完成授权（设备码过期就重来一次）"
            Write-Host "  3) 改用令牌：在 GitHub 生成 PAT 后执行"
            Write-Host '       set GH_TOKEN=你的令牌'
            Write-Host "       publish.cmd"
            exit 1
        }
    }
}

$existing = git remote get-url origin 2>$null
if ($LASTEXITCODE -eq 0 -and $existing) {
    Write-Host "已存在 origin（$existing），直接推送…"
    git push -u origin HEAD
}
else {
    & $ghExe repo create $RepoName --$Visibility --source . --remote origin --push
}

Write-Host ""
Write-Host "完成。仓库地址：" -ForegroundColor Green
& $ghExe repo view --json url -q .url
