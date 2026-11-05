<#
.SYNOPSIS
    Builds the portable zip and the signed MSIX for the version in Directory.Build.props.
.DESCRIPTION
    Creates the signing certificate on first use, in this user's certificate store. Everything is written to
    Desktop\Roomie-<version>, which also gets the certificate and the install script to take to each PC.
    Needs no administrator. The packaging tools come from NuGet, so the Windows SDK needn't be installed.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Stop'
$Publisher = 'CN=Roomie'
$ToolsVersion = '10.0.26100.1742'
$repo = Split-Path $PSScriptRoot -Parent

[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = ([string]$props.Project.PropertyGroup.Version).Trim()
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

$work = Join-Path ([IO.Path]::GetTempPath()) "roomie-msix-$version"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$layout = Join-Path $work 'layout'
$null = New-Item -ItemType Directory -Force (Join-Path $layout 'Assets')

$out = Join-Path $OutputRoot "Roomie-$version"
$null = New-Item -ItemType Directory -Force $out

# The tools.
$tools = Join-Path $env:LOCALAPPDATA "RoomieBuild\sdk-build-tools-$ToolsVersion"
$binary = Join-Path $tools 'bin\10.0.26100.0\x64'
if (-not (Test-Path (Join-Path $binary 'makeappx.exe'))) {
    Write-Host 'Downloading the packaging tools...'
    $null = New-Item -ItemType Directory -Force $tools
    $package = Join-Path $tools 'tools.zip'
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$ToolsVersion/microsoft.windows.sdk.buildtools.$ToolsVersion.nupkg" -OutFile $package -UseBasicParsing
    Expand-Archive $package $tools -Force
    Remove-Item $package
}
$makeappx = Join-Path $binary 'makeappx.exe'
$signtool = Join-Path $binary 'signtool.exe'

# The certificate. Only its subject has to stay the same for updates to install, so a replacement can be made at any time.
$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) {
    Write-Host 'Creating the signing certificate...'
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Publisher -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(10) -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.19={text}CA=false')
}
$certificateFile = Join-Path $out 'Roomie.cer'
$null = Export-Certificate -Cert $certificate -FilePath $certificateFile -Type CERT

# The program. The same build goes in the zip and the package.
Write-Host "Publishing Roomie $version..."
$publish = Join-Path $work 'publish'
& dotnet publish (Join-Path $repo 'src\Roomie.App') -c Release -r win-x64 --self-contained true -o $publish --nologo -v q `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$zip = Join-Path $out "Roomie-portable-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip

Copy-Item $publish (Join-Path $layout 'Roomie') -Recurse

# The pictures Windows wants, from the app's own logo.
Add-Type -AssemblyName System.Drawing
$logo = [Drawing.Image]::FromFile((Join-Path $repo 'src\Roomie.App\Assets\roomie.png'))
try {
    foreach ($item in @(@('StoreLogo', 50), @('Square44x44Logo', 44), @('Square150x150Logo', 150))) {
        $bitmap = New-Object Drawing.Bitmap $item[1], $item[1]
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($logo, 0, 0, $item[1], $item[1])
        $graphics.Dispose()
        $bitmap.Save((Join-Path $layout "Assets\$($item[0]).png"), [Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
    }
}
finally {
    $logo.Dispose()
}

# The package.
$manifest = (Get-Content (Join-Path $PSScriptRoot 'AppxManifest.template.xml') -Raw -Encoding UTF8).
    Replace('@VERSION@', "$version.0").
    Replace('@PUBLISHER@', $Publisher)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))

$msix = Join-Path $out "Roomie-$version.msix"
Write-Host 'Packing...'
$said = & $makeappx pack /o /d $layout /p $msix 2>&1
if ($LASTEXITCODE -ne 0) { throw "makeappx failed:`n$($said -join "`n")" }

$said = & $signtool sign /fd SHA256 /sha1 $certificate.Thumbprint /s My $msix 2>&1
if ($LASTEXITCODE -ne 0) { throw "signtool failed:`n$($said -join "`n")" }

Copy-Item (Join-Path $PSScriptRoot 'install-roomie.ps1') $out -Force
Copy-Item (Join-Path $PSScriptRoot 'Install Roomie.cmd') $out -Force
Remove-Item $work -Recurse -Force

Write-Host "Done: $out"
Get-ChildItem $out | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
