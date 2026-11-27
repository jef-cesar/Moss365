<#MOSS365
Name: krbtgt-wachtwoord vernieuwen
Description: Vernieuwt het wachtwoord van krbtgt in twee rondes, met een replicatiecontrole en de wachttijd ertussen
Version: 1.0
Connection: AD
Writes: yes
Duration: 30
Variable: Ronde | Choice | required | Welke ronde? (1 eerst; 2 pas na de wachttijd) | 1 | 1;2
#>
<#
.SYNOPSIS
    Renews the password of krbtgt (the key that signs every Kerberos ticket) in two rounds, as Microsoft advises.
    Round 1 checks the replication, sets a new password, replicates it to every writable domain controller and says
    when round 2 may run: after the longest ticket lifetime plus the clock skew of the Kerberos policy (10 hours and
    5 minutes by default). Round 2 refuses before that moment and renews the password a second time, so that tickets
    signed with the old key stop working. Run it with -WhatIf first: it then only shows what it would do.
    Needs the ActiveDirectory module and Domain Admins. Read-only domain controllers have their own krbtgt account and
    are not part of this.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('1', '2')]
    [string]$Ronde = '1'
)

$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory -ErrorAction Stop

$domain = Get-ADDomain
$pdc = $domain.PDCEmulator
$dcs = @(Get-ADDomainController -Filter * | Where-Object { -not $_.IsReadOnly })

# The waiting time between the rounds: the Kerberos policy of the Default Domain Policy, or the defaults
$maxTicketHours = 10
$skewMinutes = 5
$inf = "\\$($domain.DNSRoot)\SYSVOL\$($domain.DNSRoot)\Policies\{31B2F340-016D-11D2-945F-00C04FB984F9}\MACHINE\Microsoft\Windows NT\SecEdit\GptTmpl.inf"
if (Test-Path -LiteralPath $inf) {
    foreach ($line in Get-Content -LiteralPath $inf) {
        if ($line -match '^\s*MaxTicketAge\s*=\s*(\d+)') { $maxTicketHours = [int]$Matches[1] }
        if ($line -match '^\s*MaxClockSkew\s*=\s*(\d+)') { $skewMinutes = [int]$Matches[1] }
    }
}
$wait = New-TimeSpan -Hours $maxTicketHours -Minutes $skewMinutes

function Get-KrbtgtState {
    Get-ADUser -Identity krbtgt -Server $pdc -Properties PasswordLastSet, 'msDS-KeyVersionNumber'
}

function Test-Replication {
    $failures = @(Get-ADReplicationFailure -Target $domain.DNSRoot -Scope Domain -ErrorAction SilentlyContinue | Where-Object { $_.FailureCount -gt 0 })
    if ($failures.Count -gt 0) {
        $failures | Select-Object Server, Partner, FailureCount, LastError | Format-Table -AutoSize
        throw 'De replicatie heeft fouten: los die eerst op. Er is niets gewijzigd.'
    }
    Write-Host ("Replicatie zonder fouten tussen {0} domeincontrollers." -f $dcs.Count) -ForegroundColor Green
}

function New-RandomPassword {
    # The domain controller makes its own strong key for krbtgt; the value given only has to meet the policy
    $bytes = New-Object byte[] 48
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    ConvertTo-SecureString -String ([Convert]::ToBase64String($bytes) + 'aA1!') -AsPlainText -Force
}

function Sync-Krbtgt([int]$version) {
    $krbtgt = Get-KrbtgtState
    foreach ($dc in $dcs | Where-Object { $_.HostName -ne $pdc }) {
        try { Sync-ADObject -Object $krbtgt.DistinguishedName -Source $pdc -Destination $dc.HostName -ErrorAction Stop }
        catch { Write-Warning ("Replicatie naar {0} lukte niet: {1}" -f $dc.HostName, $_.Exception.Message) }
    }
    $rows = foreach ($dc in $dcs) {
        $seen = Get-ADUser -Identity krbtgt -Server $dc.HostName -Properties 'msDS-KeyVersionNumber' -ErrorAction SilentlyContinue
        [PSCustomObject]@{
            Domeincontroller = $dc.HostName
            Sleutelversie    = $seen.'msDS-KeyVersionNumber'
            Bijgewerkt       = ($seen.'msDS-KeyVersionNumber' -ge $version)
        }
    }
    $rows | Format-Table -AutoSize
    $late = @($rows | Where-Object { -not $_.Bijgewerkt })
    if ($late.Count -gt 0) {
        Write-Warning ("{0} domeincontroller(s) hebben de nieuwe sleutel nog niet: wacht op de replicatie voor u verder gaat." -f $late.Count)
    }
    if ($global:MossUitvoer) { $rows | Export-Csv -Path (Join-Path $global:MossUitvoer 'krbtgt-replicatie.csv') -NoTypeInformation -Encoding UTF8 }
}

$before = Get-KrbtgtState
Write-Host ("krbtgt: wachtwoord gezet op {0}, sleutelversie {1}; wachttijd tussen de rondes {2} uur en {3} minuten." -f $before.PasswordLastSet, $before.'msDS-KeyVersionNumber', $maxTicketHours, $skewMinutes) -ForegroundColor Cyan

if ($Ronde -eq '2') {
    $allowed = $before.PasswordLastSet + $wait
    if ((Get-Date) -lt $allowed) {
        throw ("Ronde 2 kan pas vanaf {0}: tickets met de sleutel van ronde 1 moeten eerst verlopen. Er is niets gewijzigd." -f $allowed)
    }
}

Test-Replication

if ($PSCmdlet.ShouldProcess("krbtgt in $($domain.DNSRoot)", "Nieuw wachtwoord (ronde $Ronde)")) {
    Set-ADAccountPassword -Identity krbtgt -Server $pdc -Reset -NewPassword (New-RandomPassword)
    $after = Get-KrbtgtState
    Write-Host ("Nieuw wachtwoord gezet; sleutelversie {0}." -f $after.'msDS-KeyVersionNumber') -ForegroundColor Green
    Sync-Krbtgt -version $after.'msDS-KeyVersionNumber'
    if ($Ronde -eq '1') {
        Write-Host ("Ronde 2 kan vanaf {0}. Start dit script dan opnieuw met ronde 2." -f ((Get-Date) + $wait)) -ForegroundColor Cyan
    }
    else {
        Write-Host 'Klaar: tickets die met een oude sleutel getekend zijn, werken niet meer. Zet krbtgt binnen zes maanden opnieuw.' -ForegroundColor Green
    }
}
else {
    Write-Host ("Voorbeeld: ronde {0} zou het wachtwoord van krbtgt vernieuwen op {1} en het naar {2} domeincontroller(s) repliceren." -f $Ronde, $pdc, $dcs.Count)
}
