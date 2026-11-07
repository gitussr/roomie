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
$logo = [Drawing.Bitmap]::FromFile((Join-Path $repo 'src\Roomie.App\Assets\roomie.png'))
try {
    # The logo has a clear border. Icons leave it off, so the red square fills them like other apps' icons.
    $left = $logo.Width; $top = $logo.Height; $right = -1; $bottom = -1
    for ($y = 0; $y -lt $logo.Height; $y++) {
        for ($x = 0; $x -lt $logo.Width; $x++) {
            if ($logo.GetPixel($x, $y).A -gt 0) {
                if ($x -lt $left) { $left = $x }; if ($x -gt $right) { $right = $x }
                if ($y -lt $top) { $top = $y }; if ($y -gt $bottom) { $bottom = $y }
            }
        }
    }
    $side = [Math]::Max($right - $left + 1, $bottom - $top + 1)
    $crop = New-Object Drawing.RectangleF ($left + ($right - $left + 1 - $side) / 2), ($top + ($bottom - $top + 1 - $side) / 2), $side, $side

    function Save-Logo([string]$name, [int]$size, [bool]$full) {
        $bitmap = New-Object Drawing.Bitmap $size, $size
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $target = New-Object Drawing.RectangleF 0, 0, $size, $size
        $source = if ($full) { $crop } else { New-Object Drawing.RectangleF 0, 0, $logo.Width, $logo.Height }
        $graphics.DrawImage($logo, $target, $source, [Drawing.GraphicsUnit]::Pixel)
        $graphics.Dispose()
        $bitmap.Save((Join-Path $layout "Assets\$name.png"), [Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
    }

    Save-Logo 'StoreLogo' 50 $false
    Save-Logo 'Square44x44Logo' 44 $false
    Save-Logo 'Square150x150Logo' 150 $false

    # The taskbar and the Start menu's list use these. Without them, Windows 10 shrinks the logo onto a square
    # of the user's accent colour. "Unplated" means drawn as it is; "lightunplated" is for a light taskbar.
    foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
        Save-Logo "Square44x44Logo.targetsize-$($size)_altform-unplated" $size $true
        Save-Logo "Square44x44Logo.targetsize-$($size)_altform-lightunplated" $size $true
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

# Windows finds the taskbar sizes above through this index; without it, it only ever sees Square44x44Logo.png.
# Only the Assets folder is indexed, not the program next to it.
$makepri = Join-Path $binary 'makepri.exe'
$priConfig = Join-Path $work 'priconfig.xml'
$said = & $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o 2>&1
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed:`n$($said -join "`n")" }
$config = [xml](Get-Content $priConfig -Raw)
$config.SelectNodes('//packaging') | ForEach-Object { $null = $_.ParentNode.RemoveChild($_) }
$config.SelectNodes('//indexer-config[@type="folder"]') | ForEach-Object {
    $exclude = $config.CreateElement('exclude')
    $exclude.SetAttribute('type', 'path'); $exclude.SetAttribute('value', 'Roomie'); $exclude.SetAttribute('doNotTraverse', 'true'); $exclude.SetAttribute('doNotIndex', 'true')
    $null = $_.AppendChild($exclude)
}
$config.Save($priConfig)
$said = & $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o 2>&1
if ($LASTEXITCODE -ne 0) { throw "makepri new failed:`n$($said -join "`n")" }

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
