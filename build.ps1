# Build LogiBatteryTray and copy the output to dist\
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# Keep dotnet temp/cache inside the project (works in restricted sandboxes)
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host 'Building...'
dotnet build .\src\LogiBatteryTray\LogiBatteryTray.csproj -c Release -v m -p:DebugType=None -p:DebugSymbols=false -p:PathMap="$root=/_/src"
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$out = Join-Path $root 'src\LogiBatteryTray\bin\Release\net10.0-windows'
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

$files = @(
    'LogiBatteryTray.exe',
    'LogiBatteryTray.dll',
    'LogiBatteryTray.deps.json',
    'LogiBatteryTray.runtimeconfig.json'
)

foreach ($name in $files) {
    Copy-Item -Path (Join-Path $out $name) -Destination $dist -Force
}

Write-Host "Done: $dist"
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
