<#MOSS365
Name: Toegang tot een gedeeld postvak via een groep
Description: Zet wie FullAccess heeft op een gedeeld postvak in een (nieuwe) mail-beveiligingsgroep; de rechtstreekse rechten blijven
Version: 1.0
Connection: Exchange
Writes: yes
Variable: SharedMailbox | Mailbox | required | Gedeeld postvak |  |
Variable: GroupName | Text | required | Naam van de groep, bv. SG-SM-info-Full |  |
Variable: Owner | User | required | Eigenaar van de groep |  |
Variable: AssignPermission | YesNo | optional | De groep zelf FullAccess geven |  |
#>
<#
.SYNOPSIS
    Zet de FullAccess-delegaties van een gedeelde mailbox om naar een mail-enabled security group.

.DESCRIPTION
    1. Lijst iedereen met FullAccess op de gedeelde mailbox (en exporteert naar CSV).
    2. Maakt een mail-enabled security group aan (indien nog niet bestaand) met de opgegeven owner.
    3. Voegt alle gedelegeerden toe als lid.
    4. Optioneel (-AssignPermission): geeft de SG zelf FullAccess op de mailbox.
       De individuele rechten worden NIET verwijderd; dat doe je best manueel na controle.

.NOTES
    Vereist module ExchangeOnlineManagement:  Install-Module ExchangeOnlineManagement -Scope CurrentUser
    Een mail-enabled security group is nodig: Exchange Online aanvaardt geen
    cloud-only (niet mail-enabled) Entra-groepen voor mailboxrechten.
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SharedMailbox,
    [string]$GroupName,
    [string]$Owner,
    [switch]$AssignPermission
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

$SharedMailbox = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'SharedMailbox' -Vraag 'Gedeeld postvak (adres)' -Waarde $SharedMailbox
$GroupName = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'GroupName' -Vraag 'Naam van de groep' -Waarde $GroupName -Standaard ('SG-SM-' + ($SharedMailbox -split '@')[0] + '-Full')
$Owner = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Owner' -Vraag 'Eigenaar van de groep (UPN)' -Waarde $Owner

# --- Verbinden met Exchange Online --------------------------------------------
# Moss365 connects to Exchange Online before the script runs; outside the app, connect first yourself
if (-not (Get-Command Get-Mailbox -ErrorAction SilentlyContinue)) { throw 'Geen verbinding met Exchange Online: start dit script vanuit Moss365, of verbind eerst zelf.' }

# --- 1. Delegaties ophalen ----------------------------------------------------
Write-Host "`n[1] FullAccess-delegaties op $SharedMailbox ophalen..." -ForegroundColor Cyan

$permissions = Get-MailboxPermission -Identity $SharedMailbox |
    Where-Object {
        $_.AccessRights -contains 'FullAccess' -and
        -not $_.IsInherited -and
        -not $_.Deny -and
        $_.User -notlike 'NT AUTHORITY\*' -and
        $_.User -notlike 'S-1-5-*'          # verweesde SID's
    }

if (-not $permissions) {
    Write-Warning "Geen gedelegeerden met FullAccess gevonden. Script stopt."
    return
}

$delegates = foreach ($p in $permissions) {
    $recipient = Get-Recipient -Identity $p.User -ErrorAction SilentlyContinue
    [pscustomobject]@{
        User          = $p.User
        DisplayName   = $recipient.DisplayName
        PrimarySmtp   = $recipient.PrimarySmtpAddress
        RecipientType = $recipient.RecipientTypeDetails
        AccessRights  = ($p.AccessRights -join ',')
    }
}

$delegates | Format-Table DisplayName, PrimarySmtp, RecipientType, AccessRights -AutoSize

$csvMap = if ($global:MossUitvoer) { $global:MossUitvoer } elseif ($PSScriptRoot) { $PSScriptRoot } else { '.' }
$csvPath = Join-Path $csvMap ("Delegates_{0}_{1:yyyyMMdd_HHmm}.csv" -f ($SharedMailbox -split '@')[0], (Get-Date))
$delegates | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8 -WhatIf:$false
Write-Host "Lijst geexporteerd naar $csvPath" -ForegroundColor Green

# --- 2. Security group aanmaken -----------------------------------------------
Write-Host "`n[2] Security group $GroupName aanmaken..." -ForegroundColor Cyan

$group = Get-DistributionGroup -Identity $GroupName -ErrorAction SilentlyContinue
if ($group) {
    Write-Host "Groep bestaat al, wordt hergebruikt." -ForegroundColor Yellow
}
elseif (-not $PSCmdlet.ShouldProcess($GroupName, 'Mail-beveiligingsgroep aanmaken')) {
    Write-Host "Voorbeeld: de groep $GroupName zou aangemaakt worden met $(@($delegates).Count) leden."
    return
}
else {
    $alias = $GroupName -replace '[^A-Za-z0-9\-_]', ''
    $group = New-DistributionGroup `
        -Name $GroupName `
        -DisplayName $GroupName `
        -Alias $alias `
        -Type Security `
        -ManagedBy $Owner `
        -MemberDepartRestriction Closed `
        -MemberJoinRestriction Closed `
        -Notes "Leden met FullAccess op $SharedMailbox"

    # Verbergen in de GAL en externe mail weigeren
    Set-DistributionGroup -Identity $group.Identity `
        -HiddenFromAddressListsEnabled $true `
        -RequireSenderAuthenticationEnabled $true

    Write-Host "Groep aangemaakt: $($group.PrimarySmtpAddress) (owner: $Owner)" -ForegroundColor Green
}

# --- 3. Leden toevoegen -------------------------------------------------------
Write-Host "`n[3] Leden toevoegen..." -ForegroundColor Cyan

$currentMembers = Get-DistributionGroupMember -Identity $group.Identity -ResultSize Unlimited |
    Select-Object -ExpandProperty PrimarySmtpAddress

foreach ($d in $delegates) {
    if (-not $d.PrimarySmtp) {
        Write-Warning "Kan $($d.User) niet resolven, overgeslagen."
        continue
    }
    if ($currentMembers -contains $d.PrimarySmtp) {
        Write-Host "  = $($d.PrimarySmtp) is al lid"
        continue
    }
    if (-not $PSCmdlet.ShouldProcess($GroupName, "Lid maken: $($d.PrimarySmtp)")) { continue }
    try {
        Add-DistributionGroupMember -Identity $group.Identity -Member $d.PrimarySmtp -BypassSecurityGroupManagerCheck
        Write-Host "  + $($d.PrimarySmtp)" -ForegroundColor Green
    }
    catch {
        Write-Warning "Toevoegen van $($d.PrimarySmtp) mislukt: $($_.Exception.Message)"
    }
}

# --- 4. (Optioneel) SG FullAccess geven ---------------------------------------
if ($AssignPermission -and $PSCmdlet.ShouldProcess($SharedMailbox, "FullAccess voor $GroupName")) {
    Write-Host "`n[4] FullAccess toekennen aan $GroupName op $SharedMailbox..." -ForegroundColor Cyan
    Add-MailboxPermission -Identity $SharedMailbox -User $group.PrimarySmtpAddress `
        -AccessRights FullAccess -InheritanceType All -AutoMapping $false | Out-Null
    Write-Host "FullAccess toegekend (let op: automapping werkt niet via groepen)." -ForegroundColor Green
}

Write-Host "`nKlaar. Controle:" -ForegroundColor Cyan
Get-DistributionGroupMember -Identity $group.Identity -ResultSize Unlimited |
    Format-Table DisplayName, PrimarySmtpAddress -AutoSize
