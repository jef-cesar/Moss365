<#MOSS365
Name: Send on Behalf nakijken
Description: Heeft een gebruiker (nog) verzenden namens op een gedeeld postvak, rechtstreeks of via een groep?
Version: 1.0
Connection: Exchange
Writes: no
Variable: Postvak | Mailbox | required | Gedeeld postvak |  |
Variable: Gebruiker | User | required | Gebruiker |  |
#>
<#
.SYNOPSIS
    Kijkt na of een gebruiker (nog) "verzenden namens" (Send on Behalf) heeft op een gedeeld postvak, rechtstreeks of
    via een groep.

.DESCRIPTION
    Leest enkel; wijzigt niets. Het postvak en de gebruiker geef je mee als parameter, of het script vraagt ze bij het
    draaien. De laatst gebruikte waarden staan dan klaar (Enter = overnemen); ze blijven lokaal op deze pc.

.EXAMPLE
    .\Check-SendOnBehalf.ps1
    .\Check-SendOnBehalf.ps1 -Postvak info@contoso.com -Gebruiker voornaam.naam@contoso.com
#>
[CmdletBinding()]
param(
    [string]$Postvak,
    [string]$Gebruiker
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

$Postvak = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Postvak' -Vraag 'Gedeeld postvak (adres)' -Waarde $Postvak
$Gebruiker = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Gebruiker' -Vraag 'Gebruiker om na te kijken (adres)' -Waarde $Gebruiker

# Moss365 connects to Exchange Online before the script runs; outside the app, connect first yourself
if (-not (Get-Command Get-Mailbox -ErrorAction SilentlyContinue)) { throw 'Geen verbinding met Exchange Online: start dit script vanuit Moss365, of verbind eerst zelf.' }

# Send on Behalf op het postvak
$mailbox = Get-Mailbox -Identity $Postvak
$sendOnBehalfUsers = @($mailbox.GrantSendOnBehalfTo)

Write-Host "`nSend on Behalf op ${Postvak}:"
foreach ($user in $sendOnBehalfUsers) {
    Write-Host "- $user"
}

$gebruikerObj = Get-Recipient -Identity $Gebruiker
if ($sendOnBehalfUsers | Where-Object { $_ -eq $Gebruiker -or $_ -eq $gebruikerObj.Name -or $_ -eq $gebruikerObj.DisplayName }) {
    Write-Host "`n$Gebruiker heeft nog Send on Behalf (rechtstreeks)." -ForegroundColor Yellow
}
else {
    Write-Host "`n$Gebruiker heeft geen rechtstreekse Send on Behalf." -ForegroundColor Green
}

# Via een groep die Send on Behalf heeft
Write-Host "`nGroepen met Send on Behalf nakijken..."
foreach ($delegate in $sendOnBehalfUsers) {
    $delegateObj = Get-Recipient -Identity $delegate
    if ($delegateObj.RecipientType -eq 'MailUniversalSecurityGroup' -or $delegateObj.RecipientType -eq 'MailUniversalDistributionGroup') {
        foreach ($member in Get-DistributionGroupMember -Identity $delegate -ResultSize Unlimited) {
            if ($member.PrimarySmtpAddress -eq $gebruikerObj.PrimarySmtpAddress) {
                Write-Host "$Gebruiker is lid van de groep $delegate (die Send on Behalf heeft)." -ForegroundColor Yellow
            }
        }
    }
}
