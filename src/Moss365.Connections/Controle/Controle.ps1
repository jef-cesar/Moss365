<#
.SYNOPSIS
    Controle van deze pc voor Moss365: PowerShell-versie en de modules die de app en de scripts nodig hebben.

.DESCRIPTION
    De app start dit script in Windows PowerShell en (als die er is) in PowerShell 7, want beide hebben hun eigen
    modulemap. Met -Installeer installeert het script een module voor de huidige gebruiker uit de PowerShell Gallery;
    alleen de modules in $Toegestaan kunnen. ASCII-only (PowerShell 5.1).

.PARAMETER Uitvoer
    JSON-bestand voor het resultaat.

.PARAMETER Installeer
    Naam van een module om te installeren (optioneel).
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
        $resultaat.Installatie = "Mislukt: module '$Installeer' is niet toegestaan."
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
            $resultaat.Installatie = "Mislukt: $($_.Exception.Message)"
        }
    }
}

foreach ($naam in $Bekijken) {
    $gevonden = @(Get-Module -ListAvailable -Name $naam | Sort-Object Version -Descending) | Select-Object -First 1
    $resultaat.Modules += [pscustomobject]@{ Naam = $naam; Versie = $(if ($gevonden) { "$($gevonden.Version)" } else { '' }) }
}

ConvertTo-Json -InputObject $resultaat -Depth 4 | Set-Content -LiteralPath $Uitvoer -Encoding UTF8
