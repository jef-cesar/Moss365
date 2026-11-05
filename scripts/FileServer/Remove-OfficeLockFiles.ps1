<#MOSS365
Naam: Office-lockbestanden opruimen
Omschrijving: Zoekt Office-lockbestanden (~$...) op de bestandsserver en verwijdert ze
Verbinding: Lokaal
Schrijft: ja
Variabele: Map | Map | verplicht | Map om te doorzoeken | \\fileserver\share\teams | 
#>
<#
.SYNOPSIS
    Zoekt Office-lockbestanden (~$...) op de bestandsserver en verwijdert ze na bevestiging.

.DESCRIPTION
    Draait met je eigen Windows-account. De map en het logbestand geef je mee als parameter, of het script vraagt ze
    bij het draaien. De laatst gebruikte waarden staan dan klaar (Enter = overnemen); ze blijven lokaal op deze pc.
    Met -WhatIf toont het enkel wat het zou verwijderen.

.EXAMPLE
    & '.\Remove-OfficeLockFiles.ps1'
    & '.\Remove-OfficeLockFiles.ps1' -Map \\fileserver\share\teams -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Map,
    [string]$Logbestand
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

$Documenten = [Environment]::GetFolderPath('MyDocuments')
if (-not $Documenten) { $Documenten = $HOME }

if ($global:MossUitvoer -and -not $Logbestand) { $Logbestand = Join-Path $global:MossUitvoer 'LockFilesLog.txt' }
$Map = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Map' -Vraag 'Map om te doorzoeken' -Waarde $Map -Standaard '\\fileserver\share\teams'
$Logbestand = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Logbestand' -Vraag 'Logbestand' -Waarde $Logbestand `
    -Standaard (Join-Path $Documenten 'Moss365\Scripts\LockFilesLog.txt')

$logMap = Split-Path -Parent $Logbestand
if ($logMap -and -not (Test-Path -LiteralPath $logMap)) { $null = New-Item -ItemType Directory -Path $logMap -Force -WhatIf:$false }
function Write-Log { param([string]$Tekst) Add-Content -LiteralPath $Logbestand -Value $Tekst -WhatIf:$false }

Write-Log "=== Scan gestart: $(Get-Date) ($Map) ==="

# Office-lockbestanden beginnen met ~$
$LockFiles = @(Get-ChildItem -LiteralPath $Map -Recurse -Filter '~$*' -File -Force -ErrorAction SilentlyContinue)

if ($LockFiles.Count -eq 0) {
    Write-Host 'Geen lock-bestanden gevonden.'
    Write-Log 'Geen lock-bestanden gevonden.'
    return
}

Write-Host 'Gevonden lock-bestanden:' -ForegroundColor Yellow
foreach ($file in $LockFiles) {
    Write-Host $file.FullName
    Write-Log "$(Get-Date) | LOCK FILE: $($file.FullName)"
}

Write-Host "`nTotaal gevonden: $($LockFiles.Count) bestanden."
Write-Log "Totaal gevonden: $($LockFiles.Count) bestanden."

if ($WhatIfPreference) {
    Write-Host 'WhatIf: er wordt niets verwijderd.'
}
elseif ($global:MossUitvoer -or (Read-Host 'Wil je deze bestanden verwijderen? (J/N)') -match '^[Jj]$') {
    # In Moss365 bevestig je in de app (na het voorbeeld)
    foreach ($file in $LockFiles) {
        if ($PSCmdlet.ShouldProcess($file.FullName, 'Verwijderen')) {
            Remove-Item -LiteralPath $file.FullName -Force
            Write-Host "Verwijderd: $($file.FullName)" -ForegroundColor Green
            Write-Log "$(Get-Date) | VERWIJDERD: $($file.FullName)"
        }
    }
    Write-Host 'Alle bestanden zijn verwijderd.'
    Write-Log "=== Verwijdering voltooid: $(Get-Date) ==="
}
else {
    Write-Host 'Geen bestanden verwijderd.'
    Write-Log 'Geen bestanden verwijderd.'
}

Write-Log "=== Scan beeindigd: $(Get-Date) ==="
