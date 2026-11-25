<#MOSS365
Name: Waar komt de vergrendeling vandaan
Description: Zoekt bij de domeincontrollers van welk toestel een account vergrendeld werd (gebeurtenis 4740) en waar foute wachtwoorden vandaan komen
Version: 1.0
Connection: AD
Writes: no
Variable: Gebruiker | Text | required | Gebruiker (sAMAccountName) |  |
Variable: Uren | Number | optional | Hoeveel uur terug kijken? | 24 |
#>
<#
.SYNOPSIS
    Finds where an account was locked out: the lockout events (4740) on the PDC emulator, and the failed sign-ins per
    domain controller (badPwdCount, last bad password). An old password on a phone or a mapped drive is the usual cause.
    Reads only. Needs the ActiveDirectory module and the right to read the Security log of the domain controllers.

.EXAMPLE
    .\Find-LockoutSource.ps1 -Gebruiker an.peeters -Uren 48
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Gebruiker,
    [int]$Uren = 24
)

$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory -ErrorAction Stop

$user = Get-ADUser -Identity $Gebruiker -Properties LockedOut, AccountLockoutTime, BadLogonCount
Write-Host "=== $($user.SamAccountName) ===" -ForegroundColor Cyan
Write-Host "Vergrendeld: $($user.LockedOut)  Sinds: $($user.AccountLockoutTime)"

# Per domain controller: how many wrong passwords, and when the last one was
$dcs = @(Get-ADDomainController -Filter *)
$perDc = foreach ($dc in $dcs) {
    try {
        $u = Get-ADUser -Identity $Gebruiker -Server $dc.HostName -Properties badPwdCount, LastBadPasswordAttempt
        [PSCustomObject]@{ Domeincontroller = $dc.HostName; FouteWachtwoorden = $u.badPwdCount; LaatsteFoutePoging = $u.LastBadPasswordAttempt }
    }
    catch {
        [PSCustomObject]@{ Domeincontroller = $dc.HostName; FouteWachtwoorden = $null; LaatsteFoutePoging = "niet bereikbaar: $($_.Exception.Message)" }
    }
}
$perDc | Sort-Object LaatsteFoutePoging -Descending | Format-Table -AutoSize

# The lockout events are written on the PDC emulator; the caller computer name is the source
$pdc = (Get-ADDomain).PDCEmulator
Write-Host "Vergrendelingen op $pdc (laatste $Uren uur):" -ForegroundColor Cyan
$start = (Get-Date).AddHours(-$Uren)
try {
    $events = @(Get-WinEvent -ComputerName $pdc -FilterHashtable @{ LogName = 'Security'; Id = 4740; StartTime = $start } -ErrorAction Stop |
        Where-Object { $_.Properties[0].Value -eq $user.SamAccountName })
}
catch {
    if ($_.Exception.Message -match 'No events') { $events = @() } else { throw }
}

if ($events.Count -eq 0) {
    Write-Host 'Geen vergrendeling gevonden in deze periode.'
}
else {
    $rows = foreach ($e in $events) {
        [PSCustomObject]@{ Tijd = $e.TimeCreated; Gebruiker = $e.Properties[0].Value; Bron = $e.Properties[1].Value }
    }
    $rows | Format-Table -AutoSize
    $rows | Group-Object Bron | Sort-Object Count -Descending | ForEach-Object {
        Write-Host ("Bron {0}: {1} keer. Kijk op dat toestel naar opgeslagen wachtwoorden (mail op de telefoon, gekoppelde schijven, geplande taken, services)." -f $_.Name, $_.Count) -ForegroundColor Yellow
    }
    if ($global:MossUitvoer) { $rows | Export-Csv -Path (Join-Path $global:MossUitvoer 'Vergrendelingen.csv') -NoTypeInformation -Encoding UTF8 }
}
