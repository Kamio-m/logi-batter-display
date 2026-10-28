# Build a signed MSIX package for LogiBatteryTray
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
Set-Location $repo

$version = '1.2.7.1'
$publisher = 'CN=LogiBatteryTray'
$layout = Join-Path $root 'layout'
$pkgDir = Join-Path $root 'out'

# --- 1. clean ---
foreach ($dir in @($layout, $pkgDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
New-Item -ItemType Directory -Path (Join-Path $layout 'Assets') -Force | Out-Null

# --- 2. copy app files ---
Write-Host 'Copying app files...'
Get-ChildItem (Join-Path $repo 'dist') -File | Copy-Item -Destination $layout -Force

# --- 3. icons ---
Write-Host 'Generating icons...'
Add-Type -AssemblyName System.Drawing

function New-Icon([int]$size, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::FromArgb(26, 26, 29))

    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(58, 130, 255))
    $g.FillEllipse($brush, [int]($size * 0.12), [int]($size * 0.12), [int]($size * 0.76), [int]($size * 0.76))

    $font = New-Object System.Drawing.Font 'Segoe UI', ([float]($size * 0.42)), ([System.Drawing.FontStyle]::Bold)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $g.DrawString('L', $font, $white, $rect, $fmt)

    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $brush.Dispose(); $white.Dispose(); $font.Dispose()
}

Copy-Item (Join-Path $repo 'src\LogiBatteryTray\Assets\icon44.png') (Join-Path $layout 'Assets\Square44x44Logo.png') -Force
Copy-Item (Join-Path $repo 'src\LogiBatteryTray\Assets\icon150.png') (Join-Path $layout 'Assets\Square150x150Logo.png') -Force
Copy-Item (Join-Path $repo 'src\LogiBatteryTray\Assets\icon48.png') (Join-Path $layout 'Assets\StoreLogo.png') -Force
Copy-Item (Join-Path $repo 'src\LogiBatteryTray\Assets\icon310.png') (Join-Path $layout 'Assets\Wide310x150Logo.png') -Force

# --- 4. manifest ---
Write-Host 'Writing manifest...'
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package
    xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
    xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
    xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
    xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
    xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
    IgnorableNamespaces="uap uap3 uap5 rescap">

  <Identity Name="LogiBatteryTray" Publisher="$publisher" Version="$version" ProcessorArchitecture="x64" />

  <Properties>
    <DisplayName>LogiBatteryTray</DisplayName>
    <PublisherDisplayName>LogiBatteryTray</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>

  <Resources>
    <Resource Language="zh-CN" />
    <Resource Language="en-us" />
  </Resources>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>

  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>

  <Applications>
    <Application Id="LogiBatteryTray" Executable="LogiBatteryTray.exe" EntryPoint="Windows.FullTrustApplication">
      <Extensions>
        <uap5:Extension Category="windows.startupTask" Executable="LogiBatteryTray.exe" EntryPoint="Windows.FullTrustApplication">
          <uap5:StartupTask TaskId="LogiBatteryTrayStartup" Enabled="true" DisplayName="LogiBatteryTray" />
        </uap5:Extension>
        <uap3:Extension Category="windows.appExtension">
          <uap3:AppExtension Name="com.microsoft.windows.lighting" Id="ambientLighting" DisplayName="Ambient Lighting" PublicFolder="Public" />
        </uap3:Extension>
      </Extensions>
      <uap:VisualElements
          DisplayName="LogiBatteryTray"
          Description="罗技无线鼠标电量显示"
          BackgroundColor="transparent"
          Square150x150Logo="Assets\Square150x150Logo.png"
          Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>

</Package>
"@
[System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding($false)))

# --- 5. pack ---
Write-Host 'Packing...'
$makeappx = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe'
$msix = Join-Path $pkgDir 'LogiBatteryTray.msix'
& $makeappx pack /d $layout /p $msix /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }

# --- 6. certificate + signing ---
Write-Host 'Creating certificate and signing...'
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher `
    -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(5) `
    -KeyUsage DigitalSignature -FriendlyName 'LogiBatteryTray MSIX'

$pwd = ConvertTo-SecureString -String 'LogiBatteryTray' -Force -AsPlainText
$pfx = Join-Path $pkgDir 'LogiBatteryTray.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pwd | Out-Null

if (-not (Test-Path $pfx)) { throw 'pfx export failed' }

$signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
& $signtool sign /fd SHA256 /a /f $pfx /p 'LogiBatteryTray' $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }

Write-Host ''
Write-Host "MSIX: $msix"
Write-Host "PFX : $pfx"
Write-Host "Cert thumbprint: $($cert.Thumbprint)"
Get-Item $msix | Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
