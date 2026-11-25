<#MOSS365
Name: Agenda-export
Description: Alle afspraken van een postvak (gedeeld postvak, room of gebruiker) in een periode, als CSV voor Excel
Version: 1.0
Connection: Graph
Writes: no
Variable: Postvak | Mailbox | required | Postvak waarvan je de agenda exporteert |  |
Variable: Van | Date | optional | Eerste dag | @werkjaar.van |
Variable: Tot | Date | optional | Laatste dag (inbegrepen) | @werkjaar.tot |
#>
<#
.SYNOPSIS
    Exporteert de agenda van een postvak (gedeeld postvak, room of gebruiker) via Microsoft Graph naar een CSV voor Excel.

.DESCRIPTION
    Alle afspraken in een periode, ook de herhalingen van terugkerende afspraken als losse items (Graph calendarView),
    met de tijden in Belgische tijd. Kolommen: Startdatum, Starttijd, Einddatum, Eindtijd, Onderwerp, Locatie, Categorie,
    Organisator. Puntkomma als scheidingsteken en UTF-8 met BOM, zodat Excel het meteen goed opent (ook met accenten).

    Wat je niet als parameter meegeeft, vraagt het script bij het draaien; de laatst gebruikte waarden staan dan klaar
    (Enter = overnemen). Ze blijven lokaal op deze pc. Standaardperiode: het werkjaar (1 september tot en met 31 augustus).

    Rechten: je account heeft minstens Reviewer nodig op de agenda van het postvak, bv. in Exchange Online:
        Add-MailboxFolderPermission -Identity "postvak@contoso.com:\Agenda" -User voornaam.naam@contoso.com -AccessRights Reviewer
    (de map heet ":\Calendar" in een Engelstalig postvak). Nieuwe rechten worden soms pas na enkele minuten actief:
    het script probeert dan tot 8 keer opnieuw, om de 30 seconden.

    Dit script doet ALLEEN het Graph-deel. Start het in een apart, schoon proces (de module van Exchange Online botst
    met die van Graph):
        pwsh -NoProfile -File .\Export-Agenda.ps1
    De agendarechten geef en neem je in een gewoon venster via Exchange Online.

.PARAMETER Postvak
    Adres van het postvak waarvan je de agenda exporteert, bv. calendar@contoso.com.

.PARAMETER Account
    Het account waarmee je aanmeldt (met Reviewer-rechten op de agenda), meestal je gewone account, niet je
    beheeraccount. Het script controleert dat je met dit account aangemeld bent.

.PARAMETER Van
    Eerste dag (inbegrepen), bv. 1/9/2026 of 2026-09-01.

.PARAMETER Tot
    Laatste dag (inbegrepen), bv. 31/8/2027 of 2027-08-31.

.PARAMETER Map
    Map voor het CSV-bestand; standaard het bureaublad. De bestandsnaam volgt uit het postvak en de periode.

.VEREISTEN
    Install-Module Microsoft.Graph.Authentication -Scope CurrentUser

.EXAMPLE
    pwsh -NoProfile -File .\Export-Agenda.ps1
    pwsh -NoProfile -File .\Export-Agenda.ps1 -Postvak calendar@contoso.com -Van 1/9/2026 -Tot 31/8/2027
#>
[CmdletBinding()]
param(
    [string]$Postvak,
    [string]$Account,
    [string]$Van,
    [string]$Tot,
    [string]$Map
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

# ---- Hulpfuncties (getest in Beheer-App/tests/powershell/Scripts.Tests.ps1) ----

function Get-BelgischeTijdzone {
    foreach ($id in 'Romance Standard Time', 'Europe/Brussels') {
        try { return [TimeZoneInfo]::FindSystemTimeZoneById($id) } catch { }
    }
    throw 'Tijdzone van Belgie niet gevonden.'
}

function ConvertTo-Datum {
    # "1/9/2026", "01/09/2026", "1-9-2026" of "2026-09-01" naar een datum (zonder tijd)
    param([Parameter(Mandatory = $true)][string]$Tekst)
    $formaten = [string[]]@('d/M/yyyy', 'd-M-yyyy', 'd.M.yyyy', 'yyyy-M-d')
    $d = [datetime]::MinValue
    if ([datetime]::TryParseExact($Tekst.Trim(), $formaten, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$d)) {
        return $d.Date
    }
    throw "Datum niet herkend: '$Tekst' (gebruik bv. 1/9/2026 of 2026-09-01)."
}

function ConvertTo-GraphTijd {
    # Middernacht (Belgische tijd) aan het begin van deze dag, als UTC-tekst voor Graph
    param([Parameter(Mandatory = $true)][datetime]$Dag)
    $lokaal = [datetime]::SpecifyKind($Dag.Date, [DateTimeKind]::Unspecified)
    [TimeZoneInfo]::ConvertTimeToUtc($lokaal, (Get-BelgischeTijdzone)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
}

function Get-StandaardPeriode {
    # Het werkjaar waarin de dag valt: 1 september tot en met 31 augustus
    param([datetime]$Vandaag = (Get-Date))
    $jaar = if ($Vandaag.Month -ge 9) { $Vandaag.Year } else { $Vandaag.Year - 1 }
    @{ Van = (Get-Date -Year $jaar -Month 9 -Day 1).ToString('d/M/yyyy', [Globalization.CultureInfo]::InvariantCulture)
       Tot = (Get-Date -Year ($jaar + 1) -Month 8 -Day 31).ToString('d/M/yyyy', [Globalization.CultureInfo]::InvariantCulture) }
}

function ConvertTo-AgendaRij {
    # Een event uit Graph (met tijden in Belgische tijd) naar een rij voor de CSV
    param([Parameter(Mandatory = $true)]$Item)
    $s = [datetime]::Parse($Item.start.dateTime, [Globalization.CultureInfo]::InvariantCulture)
    $e = [datetime]::Parse($Item.end.dateTime, [Globalization.CultureInfo]::InvariantCulture)
    if ($Item.isAllDay) { $e = $e.AddDays(-1) }     # de einddatum van een hele-dag-item is exclusief
    [pscustomobject]@{
        Startdatum  = $s.ToString('dd/MM/yyyy')
        Starttijd   = if ($Item.isAllDay) { 'hele dag' } else { $s.ToString('HH:mm') }
        Einddatum   = $e.ToString('dd/MM/yyyy')
        Eindtijd    = if ($Item.isAllDay) { '' } else { $e.ToString('HH:mm') }
        Onderwerp   = $Item.subject
        Locatie     = $Item.location.displayName
        Categorie   = (@($Item.categories) -join ', ')
        Organisator = $Item.organizer.emailAddress.name
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }

# ---- Waarden ----
if ($global:MossUitvoer) {
    # In Moss365: aangemeld als de gebruiker van de app, het bestand in de map van de run
    if (-not $Account) { $Account = $global:MossUpn }
    if (-not $Map) { $Map = $global:MossUitvoer }
}
$periode = Get-StandaardPeriode
$Postvak = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Postvak' -Vraag 'Postvak waarvan je de agenda exporteert (adres)' -Waarde $Postvak
$Account = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Account' -Vraag 'Account waarmee je aanmeldt (met Reviewer-rechten op de agenda)' -Waarde $Account
$Van = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Van' -Vraag 'Eerste dag (bv. 1/9/2026)' -Waarde $Van -Standaard $periode.Van
$Tot = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Tot' -Vraag 'Laatste dag, inbegrepen (bv. 31/8/2027)' -Waarde $Tot -Standaard $periode.Tot
$bureaublad = [Environment]::GetFolderPath('Desktop')
if (-not $bureaublad) { $bureaublad = $HOME }
$Map = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Map' -Vraag 'Map voor het CSV-bestand' -Waarde $Map -Standaard $bureaublad

$vanDag = ConvertTo-Datum $Van
$totDag = ConvertTo-Datum $Tot
if ($totDag -lt $vanDag) { throw "De laatste dag ($Tot) ligt voor de eerste dag ($Van)." }
$vanUtc = ConvertTo-GraphTijd $vanDag
$totUtc = ConvertTo-GraphTijd $totDag.AddDays(1)      # Graph: einde exclusief, dus de dag na de laatste dag

$naam = ($Postvak -split '@')[0] -replace '[^A-Za-z0-9._-]', '_'
$Csv = Join-Path $Map ('{0}_agenda_{1:yyyy-MM-dd}_{2:yyyy-MM-dd}.csv' -f $naam, $vanDag, $totDag)

# Modules die een eigen (oudere) Microsoft.Identity.Client laden, botsen met Graph
$bots = Get-Module ExchangeOnlineManagement, PnP.PowerShell, Az.Accounts
if ($bots) {
    throw "Deze sessie heeft al $($bots.Name -join ', ') geladen. Start zo: pwsh -NoProfile -File .\Export-Agenda.ps1"
}

# Moss365 connects to Microsoft Graph before the script runs; outside the app, connect first yourself
if (-not (Get-MgContext -ErrorAction SilentlyContinue)) { throw 'Geen verbinding met Microsoft Graph: start dit script vanuit Moss365, of verbind eerst zelf.' }
$ctx = Get-MgContext
if ($ctx.Account -ne $Account) {
    throw "Aangemeld als $($ctx.Account), verwacht $Account. Doe Disconnect-MgGraph en probeer opnieuw."
}

# 2. Events ophalen (alle pagina's); wacht automatisch als nieuwe rechten nog niet actief zijn
$hdr = @{ Prefer = 'outlook.timezone="Romance Standard Time"' }   # tijden in Belgische tijd
$uri = "https://graph.microsoft.com/v1.0/users/$([uri]::EscapeDataString($Postvak))/calendarView" +
       "?startDateTime=$vanUtc&endDateTime=$totUtc" +
       "&`$top=250&`$select=subject,start,end,isAllDay,location,categories,organizer"

$events = [System.Collections.Generic.List[object]]::new()
do {
    $r = $null
    for ($i = 1; -not $r; $i++) {
        try {
            # In Moss365 zonder de Graph-module (Invoke-MossGraph); anders met de module
            $r = if (Get-Command Invoke-MossGraph -ErrorAction SilentlyContinue) { Invoke-MossGraph -Pad $uri -Headers $hdr } else { Invoke-MgGraphRequest -Method GET -Uri $uri -Headers $hdr }
        }
        catch {
            if ($i -ge 8 -or "$_" -notmatch 'Forbidden|denied') { throw }
            Write-Host 'Nog geen toegang (rechten worden nog actief), nieuwe poging over 30 s...'
            Start-Sleep -Seconds 30
        }
    }
    foreach ($item in $r.value) { $events.Add($item) }
    $uri = $r.'@odata.nextLink'
} while ($uri)
Write-Host "$($events.Count) items gevonden in de agenda van $Postvak ($Van tot en met $Tot)"

# 3. CSV voor Excel (puntkomma + UTF-8 met BOM, zodat accenten goed staan)
if (-not (Test-Path -LiteralPath $Map)) { $null = New-Item -ItemType Directory -Path $Map -Force }
$enc = if ($PSVersionTable.PSVersion.Major -ge 6) { 'utf8BOM' } else { 'UTF8' }
$events | Sort-Object { [datetime]::Parse($_.start.dateTime, [Globalization.CultureInfo]::InvariantCulture) } |
    ForEach-Object { ConvertTo-AgendaRij $_ } |
    Export-Csv -LiteralPath $Csv -NoTypeInformation -Delimiter ';' -Encoding $enc

Disconnect-MgGraph | Out-Null
Write-Host "Opgeslagen: $Csv" -ForegroundColor Green
