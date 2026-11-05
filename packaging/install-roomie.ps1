<#
.SYNOPSIS
    Installs Roomie on this PC from the files next to this script.
.DESCRIPTION
    Windows only installs a package whose signer it trusts, and Roomie's certificate is its own, so this trusts that
    certificate for installing packages on this PC. It goes in Trusted People, not the root store, so Windows trusts
    nothing else because of it. That step needs an administrator, which "Install Roomie.cmd" asks for.
#>
$ErrorActionPreference = 'Stop'

# Names of real network connections that Windows treats as Public. Virtual adapters are left out, because Windows
# often marks them Public and Roomie's traffic doesn't depend on them.
function Get-PublicNetworkName {
    $physical = @(Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object ifIndex)
    @(Get-NetConnectionProfile |
        Where-Object { $_.NetworkCategory -eq 'Public' -and $physical -contains $_.InterfaceIndex } |
        ForEach-Object { $_.Name })
}

$msix =Get-ChildItem $PSScriptRoot -Filter 'Roomie-*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$certificate = Join-Path $PSScriptRoot 'Roomie.cer'
if (-not $msix -or -not (Test-Path $certificate)) {
    throw 'Keep this script in the same folder as the .msix and Roomie.cer.'
}

$build = [Environment]::OSVersion.Version.Build
if ($build -lt 19041) {
    throw "Roomie's package needs Windows 10 version 2004 or later, and this PC has build $build. Use the portable zip here."
}

$null = Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\LocalMachine\TrustedPeople

try {
    Add-AppxPackage -Path $msix.FullName -ForceUpdateFromAnyVersion -ForceApplicationShutdown
}
catch {
    Write-Host ''
    Write-Host 'Windows refused the install. If this PC is managed by your office, its policy may block installing' -ForegroundColor Yellow
    Write-Host 'apps from outside the Store. The portable zip works without installing anything.' -ForegroundColor Yellow
    throw
}

Write-Host "Roomie is installed. Open it from the Start menu."

# Only a warning: changing a PC's network type is the user's or IT's decision, and Roomie is already installed.
try {
    foreach ($name in Get-PublicNetworkName) {
        Write-Host ''
        Write-Host "Warning: Windows treats the network '$name' as Public." -ForegroundColor Yellow
        Write-Host "Roomie's firewall rules cover Private and Domain networks only, so other PCs may not be able to find" -ForegroundColor Yellow
        Write-Host 'or connect to this one. If this is your trusted office network, set it to Private under Settings >' -ForegroundColor Yellow
        Write-Host 'Network & Internet > Properties, or ask your IT team.' -ForegroundColor Yellow
    }
}
catch {
    Write-Host ''
    Write-Host "Roomie couldn't check this PC's network type. Run Get-NetConnectionProfile: NetworkCategory should be Private or DomainAuthenticated." -ForegroundColor Yellow
}
