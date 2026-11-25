<#MOSS365
Name: Ontvangers aan een lijst toevoegen
Description: Voegt bestaande ontvangers (bv. externe contactpersonen) toe aan een distributielijst
Version: 1.0
Connection: Exchange
Writes: yes
Variable: Distributielijst | Group | required | Distributielijst | everyone@contoso.com |
Variable: Adressen | Items | required | E-mailadressen (een per regel) |  |
#>
<#
.SYNOPSIS
    Voegt bestaande ontvangers (bv. externe contactpersonen) toe aan een distributielijst (standaard everyone@contoso.com).

.DESCRIPTION
    Per adres zoekt het script de ontvanger (contactpersoon, gebruiker, ...) en voegt die toe aan de lijst. Een adres
    zonder ontvanger meldt het als "niet gevonden"; maak die contactpersoon eerst aan.
    De lijst en de adressen geef je mee als parameter, of het script vraagt ze bij het draaien. De laatst gebruikte
    waarden staan dan klaar (Enter = overnemen); ze blijven lokaal op deze pc. Met -WhatIf wijzigt het niets.

.PARAMETER Distributielijst
    Naam of adres van de distributielijst.

.PARAMETER Adressen
    E-mailadressen. Bij de vraag: gescheiden door komma's, of het pad naar een bestand met een adres per lijn.

.EXAMPLE
    .\ImportUsersDistributionList.ps1
    .\ImportUsersDistributionList.ps1 -Distributielijst everyone@contoso.com -Adressen C:\Temp\vrijwilligers.txt -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Distributielijst,
    [string[]]$Adressen
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

$Distributielijst = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Distributielijst' -Vraag 'Distributielijst' -Waarde $Distributielijst -Standaard 'everyone@contoso.com'
$Adressen = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Adressen' -Vraag 'E-mailadressen om toe te voegen' -Waarde $Adressen -Lijst

# Moss365 connects to Exchange Online before the script runs; outside the app, connect first yourself
if (-not (Get-Command Get-Mailbox -ErrorAction SilentlyContinue)) { throw 'Geen verbinding met Exchange Online: start dit script vanuit Moss365, of verbind eerst zelf.' }

foreach ($Mail in $Adressen) {
    try {
        $Recipient = Get-Recipient -Identity $Mail -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $Recipient) {
            Write-Host "[NIET GEVONDEN] $Mail" -ForegroundColor Yellow
            continue
        }

        if ($PSCmdlet.ShouldProcess($Distributielijst, "$Mail toevoegen")) {
            Add-DistributionGroupMember -Identity $Distributielijst -Member $Recipient.Guid.ToString() -ErrorAction Stop
            Write-Host "[TOEGEVOEGD] $Mail" -ForegroundColor Green
        }
    }
    catch {
        Write-Host "[FOUT] $Mail" -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor DarkRed
    }
}
