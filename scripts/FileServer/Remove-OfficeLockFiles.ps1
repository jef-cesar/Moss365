<#MOSS365
Naam: Office-lockbestanden opruimen
Omschrijving: Zoekt Office-lockbestanden (~$...) op de bestandsserver en verwijdert ze
Verbinding: Lokaal
Schrijft: ja
Variabele: Map | Map | verplicht | Map om te doorzoeken | \\fileserver\share\teams | 
#>
<#
.SYNOPSIS
    Finds Office lock files (~$...) on the file server and deletes them after confirmation.

.DESCRIPTION
    Runs with your own Windows account. You pass the folder and the log file as parameters, or the script asks for
    them when it runs. The last used values are then ready (Enter = take over); they stay local on this PC.
    With -WhatIf it only shows what it would delete.

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

# ---- Ask and remember values (the same block in every script; ASCII only) ----
# A value you do not pass as a parameter is asked by the script when it runs. The last used value is ready
# between [ ]: Enter takes it over. The values stay local on this PC, in
# %LOCALAPPDATA%\Moss365\scripts\waarden\<script>.json (never in the repository). Moss365 uses the same files.
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
        catch { Write-Warning "Remembered values not readable ($Bestand); they are asked again." }
    }

    $leeg = { param($w) $null -eq $w -or (@($w | Where-Object { "$_".Trim() -ne '' })).Count -eq 0 }
    if (& $leeg $Waarde) {
        $vorige = if ($opgeslagen.Contains($Naam)) { $opgeslagen[$Naam] } else { $Standaard }
        if ($global:MossUitvoer) {
            # In Moss365 everything comes from the form: do not ask, take the last or the default value
            $Waarde = $vorige
        }
        else {
        $toon = if (& $leeg $vorige) { '' } else { ' [' + ((@($vorige) | Select-Object -First 5) -join ', ') + $(if (@($vorige).Count -gt 5) { ', ...' } else { '' }) + ']' }
        $hint = if ($Lijst) { ' (separated by commas, or the path to a file with one value per line)' } else { '' }
        $antwoord = Read-Host "$Vraag$hint$toon"
        if ([string]::IsNullOrWhiteSpace($antwoord)) { $Waarde = $vorige }
        elseif ($Lijst -and (Test-Path -LiteralPath $antwoord.Trim().Trim('"') -PathType Leaf)) {
            $Waarde = @(Get-Content -LiteralPath $antwoord.Trim().Trim('"') -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        }
        elseif ($Lijst) { $Waarde = @($antwoord -split '[,;]' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }) }
        else { $Waarde = $antwoord.Trim() }
        }
    }
    if (& $leeg $Waarde) { throw "No value for $Naam." }

    if ($Lijst) { $Waarde = @($Waarde | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne '' }) }
    $opgeslagen[$Naam] = $Waarde
    $map = Split-Path -Parent $Bestand
    # Remembering is not a change: save also with -WhatIf
    if (-not (Test-Path -LiteralPath $map)) { $null = New-Item -ItemType Directory -Path $map -Force -WhatIf:$false }
    $opgeslagen | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Bestand -Encoding UTF8 -WhatIf:$false
    if ($Lijst) { return , $Waarde }
    return $Waarde
}

$WaardenBestand = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Moss365\scripts\waarden\' + $(if ($PSCommandPath) { [IO.Path]::GetFileNameWithoutExtension($PSCommandPath) } else { 'script' }) + '.json')
# ---- end of block ----

$Documenten = [Environment]::GetFolderPath('MyDocuments')
if (-not $Documenten) { $Documenten = $HOME }

if ($global:MossUitvoer -and -not $Logbestand) { $Logbestand = Join-Path $global:MossUitvoer 'LockFilesLog.txt' }
$Map = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Map' -Vraag 'Folder to search' -Waarde $Map -Standaard '\\fileserver\share\teams'
$Logbestand = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'Logbestand' -Vraag 'Logbestand' -Waarde $Logbestand `
    -Standaard (Join-Path $Documenten 'Moss365\Scripts\LockFilesLog.txt')

$logMap = Split-Path -Parent $Logbestand
if ($logMap -and -not (Test-Path -LiteralPath $logMap)) { $null = New-Item -ItemType Directory -Path $logMap -Force -WhatIf:$false }
function Write-Log { param([string]$Tekst) Add-Content -LiteralPath $Logbestand -Value $Tekst -WhatIf:$false }

Write-Log "=== Scan started: $(Get-Date) ($Map) ==="

# Office lock files start with ~$
$LockFiles = @(Get-ChildItem -LiteralPath $Map -Recurse -Filter '~$*' -File -Force -ErrorAction SilentlyContinue)

if ($LockFiles.Count -eq 0) {
    Write-Host 'No lock files found.'
    Write-Log 'No lock files found.'
    return
}

Write-Host 'Lock files found:' -ForegroundColor Yellow
foreach ($file in $LockFiles) {
    Write-Host $file.FullName
    Write-Log "$(Get-Date) | LOCK FILE: $($file.FullName)"
}

Write-Host "`nTotal found: $($LockFiles.Count) files."
Write-Log "Total found: $($LockFiles.Count) files."

if ($WhatIfPreference) {
    Write-Host 'WhatIf: nothing is deleted.'
}
elseif ($global:MossUitvoer -or (Read-Host 'Do you want to delete these files? (Y/N)') -match '^[Jj]$') {
    # In Moss365 you confirm in the app (after the preview)
    foreach ($file in $LockFiles) {
        if ($PSCmdlet.ShouldProcess($file.FullName, 'Verwijderen')) {
            Remove-Item -LiteralPath $file.FullName -Force
            Write-Host "Deleted: $($file.FullName)" -ForegroundColor Green
            Write-Log "$(Get-Date) | DELETED: $($file.FullName)"
        }
    }
    Write-Host 'All files have been deleted.'
    Write-Log "=== Deletion finished: $(Get-Date) ==="
}
else {
    Write-Host 'No files deleted.'
    Write-Log 'No files deleted.'
}

Write-Log "=== Scan ended: $(Get-Date) ==="
