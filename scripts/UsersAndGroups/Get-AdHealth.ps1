<#MOSS365
Name: AD-gezondheid
Description: Domeincontrollers, replicatie, FSMO-rollen en tijd van het domein; leest enkel
Version: 1.0
Connection: AD
Writes: no
#>
<#
.SYNOPSIS
    Shows the health of Active Directory: the domain controllers (reachable, site, global catalog), replication failures
    and the last successful replication per partner, the FSMO roles and the time offset of every domain controller.
    Reads only. Needs the ActiveDirectory module.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory -ErrorAction Stop

$domain = Get-ADDomain
$forest = Get-ADForest
Write-Host "=== Domein $($domain.DNSRoot) (niveau $($domain.DomainMode)) ===" -ForegroundColor Cyan

Write-Host "`nFSMO-rollen" -ForegroundColor Cyan
[PSCustomObject]@{
    SchemaMaster       = $forest.SchemaMaster
    DomainNamingMaster = $forest.DomainNamingMaster
    PDCEmulator        = $domain.PDCEmulator
    RIDMaster          = $domain.RIDMaster
    InfrastructureMaster = $domain.InfrastructureMaster
} | Format-List

Write-Host "Domeincontrollers" -ForegroundColor Cyan
$dcs = @(Get-ADDomainController -Filter *)
$rows = foreach ($dc in $dcs) {
    $ping = Test-Connection -ComputerName $dc.HostName -Count 1 -Quiet -ErrorAction SilentlyContinue
    $offset = $null
    try {
        $w32 = & w32tm /stripchart /computer:$($dc.HostName) /samples:1 /dataonly 2>$null | Select-Object -Last 1
        if ($w32 -match ',\s*([+-]?[\d\.]+)s') { $offset = [double]$Matches[1] }
    }
    catch { }
    [PSCustomObject]@{
        Naam         = $dc.HostName
        Site         = $dc.Site
        GlobalCatalog = $dc.IsGlobalCatalog
        AlleenLezen  = $dc.IsReadOnly
        Bereikbaar   = $ping
        TijdVerschilSeconden = $offset
        Besturingssysteem = $dc.OperatingSystem
    }
}
$rows | Format-Table -AutoSize

Write-Host "Replicatie" -ForegroundColor Cyan
$failures = @(Get-ADReplicationFailure -Target $domain.DNSRoot -Scope Domain -ErrorAction SilentlyContinue | Where-Object { $_.FailureCount -gt 0 })
if ($failures.Count -eq 0) {
    Write-Host 'Geen replicatiefouten.' -ForegroundColor Green
}
else {
    $failures | Select-Object Server, Partner, FailureCount, FirstFailureTime, LastError | Format-Table -AutoSize
}
$partners = foreach ($dc in $dcs) {
    Get-ADReplicationPartnerMetadata -Target $dc.HostName -ErrorAction SilentlyContinue |
        Select-Object @{ n = 'Server'; e = { $dc.HostName } }, Partner, LastReplicationSuccess, LastReplicationResult
}
$partners | Format-Table -AutoSize

$late = @($partners | Where-Object { $_.LastReplicationSuccess -lt (Get-Date).AddHours(-24) })
$far = @($rows | Where-Object { $null -ne $_.TijdVerschilSeconden -and [Math]::Abs($_.TijdVerschilSeconden) -gt 60 })
$down = @($rows | Where-Object { -not $_.Bereikbaar })
Write-Host "`nSamenvatting" -ForegroundColor Cyan
Write-Host ("Domeincontrollers niet bereikbaar: {0}; replicatie ouder dan 24 uur: {1}; tijdverschil meer dan een minuut: {2}; replicatiefouten: {3}" -f $down.Count, $late.Count, $far.Count, $failures.Count)
if ($global:MossUitvoer) { $rows | Export-Csv -Path (Join-Path $global:MossUitvoer 'Domeincontrollers.csv') -NoTypeInformation -Encoding UTF8 }
