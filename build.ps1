# Build LogiBatteryTray and copy the output to dist\
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host 'Building...'
dotnet build .\src\LogiBatteryTray\LogiBatteryTray.csproj -c Release -v m -p:DebugType=None -p:DebugSymbols=false -p:PathMap="$root=/_/src"
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

# Locate the newest build output directory (name depends on the target framework)
$releaseRoot = Join-Path $root 'src\LogiBatteryTray\bin\Release'
$out = $null

if (Test-Path $releaseRoot) {
    $candidate = Get-ChildItem -Path $releaseRoot -Directory |
        Where-Object { $_.Name -like 'net*windows*' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($candidate) { $out = $candidate.FullName }
}

if (-not $out) { throw "build output folder not found under $releaseRoot" }
Write-Host "Output: $out"

$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

Get-ChildItem -Path $out -File |
    Where-Object { $_.Extension -eq '.exe' -or $_.Extension -eq '.dll' -or $_.Extension -eq '.json' } |
    Copy-Item -Destination $dist -Force

Write-Host "Done: $dist"
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
