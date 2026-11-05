<#
.SYNOPSIS
    Check of this PC for Moss365: the PowerShell version and the modules the app and the scripts need.

.DESCRIPTION
    The app starts this script in Windows PowerShell and (if present) in PowerShell 7, because both have their own
    module folder. With -Installeer the script installs a module for the current user from the PowerShell Gallery;
    only the modules in $Toegestaan are possible. ASCII only (PowerShell 5.1).

.PARAMETER Uitvoer
    JSON file for the result.

.PARAMETER Installeer
    Name of a module to install (optional).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Uitvoer,
    [string]$Installeer
)

$ErrorActionPreference = 'Stop'

$Toegestaan = @('ExchangeOnlineManagement', 'Microsoft.Graph.Authentication', 'Microsoft.Graph.Applications', 'Microsoft.Graph.Users',
    'Microsoft.Graph.Identity.SignIns', 'Microsoft.Graph.Groups')
$Bekijken = $Toegestaan + @('ActiveDirectory')

$resultaat = [ordered]@{
    PowerShell  = "$($PSVersionTable.PSVersion)"
    Editie      = "$($PSVersionTable.PSEdition)"
    Modules     = @()
    Installatie = $null
}

if ($Installeer) {
    if ($Toegestaan -notcontains $Installeer) {
        $resultaat.Installatie = "Failed: module '$Installeer' is not allowed."
    }
    else {
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            if ($PSVersionTable.PSEdition -eq 'Desktop' -and -not (Get-PackageProvider -ListAvailable -Name NuGet -ErrorAction SilentlyContinue)) {
                Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Scope CurrentUser -Force | Out-Null
            }
            Install-Module -Name $Installeer -Scope CurrentUser -Repository PSGallery -Force -AllowClobber -ErrorAction Stop
            $resultaat.Installatie = 'Gelukt'
        }
        catch {
            $resultaat.Installatie = "Failed: $($_.Exception.Message)"
        }
    }
}

foreach ($naam in $Bekijken) {
    $gevonden = @(Get-Module -ListAvailable -Name $naam | Sort-Object Version -Descending) | Select-Object -First 1
    $resultaat.Modules += [pscustomobject]@{ Naam = $naam; Versie = $(if ($gevonden) { "$($gevonden.Version)" } else { '' }) }
}

ConvertTo-Json -InputObject $resultaat -Depth 4 | Set-Content -LiteralPath $Uitvoer -Encoding UTF8
