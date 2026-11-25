<#MOSS365
Name: Groepen van een gebruiker
Description: Toont een gebruiker (kantoor, dienst, bedrijf) en al zijn groepen in Entra; optioneel enkel de groepen met een woord in de naam
Version: 1.0
Connection: Graph
Writes: no
Variable: Gebruiker | User | required | Gebruiker |  |
Variable: Zoekwoord | Text | optional | Woord in de groepsnaam (optioneel) |  |
#>
<#
.SYNOPSIS
    Toont een gebruiker (kantoor, stad, dienst, bedrijf) en al zijn groepen uit Entra, en optioneel welke groepen een
    bepaald woord in de naam hebben.

.DESCRIPTION
    Leest enkel. De gebruiker geef je mee als parameter, of het script vraagt hem bij het draaien. De laatst gebruikte
    waarde staat dan klaar (Enter = overnemen); ze blijft lokaal op deze pc.

.VEREISTEN
    Install-Module Microsoft.Graph -Scope CurrentUser

.EXAMPLE
    .\CheckUserGroups.ps1 -Gebruiker voornaam.naam@contoso.com
#>
[CmdletBinding()]
param(
    [string]$Gebruiker,
    [string]$Zoekwoord
)

$ErrorActionPreference = 'Stop'

# ---- Waarden vragen en onthouden (zelfde blok in elk script; ASCII-only) ----
# Een waarde die je niet als parameter meegeeft, vraagt het script bij het draaien. De laatst gebruikte waarde staat
# tussen [ ] klaar: Enter neemt ze over. De waarden blijven lokaal op deze pc, in
# %LOCALAPPDATA%\Moss365\scripts\waarden\<script>.json (nooit in de repo). Moss365 gebruikt dezelfde bestanden.
function Get-ScriptWaarde {
    param(
        [Parameter(Mandatory = $true)][string]$Bestand,
        [Parameter(Mandatory = $true)][string]$Naam,
        [Parameter(Mandatory = $true)][string]$Vraag,
        [object]$Waarde,
        [object]$Standaard,
        [switch]$Lijst
    )
    $opgeslagen = [ordered]@{}
    if (Test-Path -LiteralPath $Bestand) {
        try {
            $json = Get-Content -LiteralPath $Bestand -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($p in $json.PSObject.Properties) { $opgeslagen[$p.Name] = $p.Value }
        }
        catch { Write-Warning "Onthouden waarden niet leesbaar ($Bestand); ze worden opnieuw gevraagd." }
    }

    $leeg = { param($w) $null -eq $w -or (@($w | Where-Object { "$_".Trim() -ne '' })).Count -eq 0 }
    if (& $leeg $Waarde) {
        $vorige = if ($opgeslagen.Contains($Naam)) { $opgeslagen[$Naam] } else { $Standaard }
        if ($global:MossUitvoer) {
            # In Moss365 komt alles uit het formulier: niet vragen, de laatste of standaardwaarde nemen
            $Waarde = $vorige
        }
        else {
        $toon = if (& $leeg $vorige) { '' } else { ' [' + ((@($vorige) | Select-Object -First 5) -join ', ') + $(if (@($vorige).Count -gt 5) { ', ...' } else { '' }) + ']' }
        $hint = if ($Lijst) { ' (gescheiden door komma''s, of het pad naar een bestand met een waarde per lijn)' } else { '' }
        $antwoord = Read-Host "$Vraag$hint$toon"
        if ([string]::IsNullOrWhiteSpace($antwoord)) { $Waarde = $vorige }
        elseif ($Lijst -and (Test-Path -LiteralPath $antwoord.Trim().Trim('"') -PathType Leaf)) {
            $Waarde = @(Get-Content -LiteralPath $antwoord.Trim().Trim('"') -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        }
        elseif ($Lijst) { $Waarde = @($antwoord -split '[,;]' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }) }
        else { $Waarde = $antwoord.Trim() }
        }
    }
    if (& $leeg $Waarde) { throw "Geen waarde voor $Naam." }

    if ($Lijst) { $Waarde = @($Waarde | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne '' }) }
    $opgeslagen[$Naam] = $Waarde
    $map = Split-Path -Parent $Bestand
    # Onthouden is geen wijziging: ook bij -WhatIf bewaren
    if (-not (Test-Path -LiteralPath $map)) { $null = New-Item -ItemType Directory -Path $map -Force -WhatIf:$false }
    $opgeslagen | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Bestand -Encoding UTF8 -WhatIf:$false
    if ($Lijst) { return , $Waarde }
    return $Waarde
}

$WaardenBestand = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Moss365\scripts\waarden\' + $(if ($PSCommandPath) { [IO.Path]::GetFileNameWithoutExtension($PSCommandPath) } else { 'script' }) + '.json')
# ---- einde blok ----

$Gebruiker = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Gebruiker' -Vraag 'Gebruiker (UPN)' -Waarde $Gebruiker

# Moss365 connects to Microsoft Graph before the script runs; outside the app, connect first yourself
if (-not (Get-MgContext -ErrorAction SilentlyContinue)) { throw 'Geen verbinding met Microsoft Graph: start dit script vanuit Moss365, of verbind eerst zelf.' }

# --- Gebruikersinfo ---
$user = Get-MgUser -UserId $Gebruiker -Property Id, DisplayName, UserPrincipalName, OfficeLocation, City, Department, CompanyName, StreetAddress, OnPremisesExtensionAttributes

Write-Host '=== Gebruiker ===' -ForegroundColor Cyan
$user | Select-Object DisplayName, UserPrincipalName, OfficeLocation, City, Department, CompanyName | Format-List

# --- Groepslidmaatschappen ---
Write-Host '=== Groepen (Id / DisplayName) ===' -ForegroundColor Cyan
$groups = Get-MgUserMemberOf -UserId $user.Id -All |
    Where-Object { $_.AdditionalProperties.'@odata.type' -eq '#microsoft.graph.group' } |
    ForEach-Object {
        [PSCustomObject]@{
            Id              = $_.Id
            DisplayName     = $_.AdditionalProperties.displayName
            MailEnabled     = $_.AdditionalProperties.mailEnabled
            SecurityEnabled = $_.AdditionalProperties.securityEnabled
        }
    }

$groups | Sort-Object DisplayName | Format-Table -AutoSize

# --- Groepen met het zoekwoord in de naam (optioneel) ---
if ($Zoekwoord) {
    $match = $groups | Where-Object { $_.DisplayName -like "*$Zoekwoord*" }
    if ($match) {
        Write-Host "`n[OK] Groepen met '$Zoekwoord' in de naam:" -ForegroundColor Green
        $match | Format-Table -AutoSize
    }
    else {
        Write-Host "`n[!] Geen groep gevonden met '$Zoekwoord' in de naam." -ForegroundColor Yellow
    }
}
