<#MOSS365
Name: Domeincontrollers controleren
Description: LDAP-signing, NTLMv1, SMBv1 en de afdrukspooler op elke domeincontroller; leest enkel
Version: 1.0
Connection: AD
Writes: no
#>
<#
.SYNOPSIS
    Checks every domain controller for what attackers use against them: LDAP signing and channel binding not required,
    NTLMv1 or LM still accepted (LmCompatibilityLevel below 5), SMBv1 still on, and the print spooler running (it lets
    anyone make the domain controller sign in elsewhere). Reads only, through PowerShell remoting (WinRM). Needs the
    ActiveDirectory module and an account that may read the registry of the domain controllers.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory -ErrorAction Stop

$dcs = @(Get-ADDomainController -Filter *)
$rows = foreach ($dc in $dcs) {
    try {
        $r = Invoke-Command -ComputerName $dc.HostName -ErrorAction Stop -ScriptBlock {
            $ntds = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\NTDS\Parameters' -ErrorAction SilentlyContinue
            $lsa = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -ErrorAction SilentlyContinue
            $smb1 = $null
            try { $smb1 = (Get-SmbServerConfiguration -ErrorAction Stop).EnableSMB1Protocol } catch { }
            $spooler = Get-Service -Name Spooler -ErrorAction SilentlyContinue
            [PSCustomObject]@{
                LdapSigning    = $ntds.LDAPServerIntegrity
                ChannelBinding = $ntds.LdapEnforceChannelBinding
                LmLevel        = $lsa.LmCompatibilityLevel
                Smb1           = $smb1
                Spooler        = if ($spooler) { "$($spooler.Status) ($($spooler.StartType))" } else { 'niet geinstalleerd' }
                SpoolerOn      = [bool]($spooler -and $spooler.Status -eq 'Running')
            }
        }
        $points = @()
        if ($r.LdapSigning -ne 2) { $points += 'LDAP-signing niet vereist (LDAPServerIntegrity 2)' }
        if ($r.ChannelBinding -ne 2) { $points += 'LDAP channel binding niet altijd (LdapEnforceChannelBinding 2)' }
        if ($null -eq $r.LmLevel -or $r.LmLevel -lt 5) { $points += 'NTLMv1/LM nog aanvaard (LmCompatibilityLevel 5)' }
        if ($r.Smb1 -eq $true) { $points += 'SMBv1 staat aan' }
        if ($r.SpoolerOn) { $points += 'afdrukspooler draait (uitschakelen op een domeincontroller)' }
        [PSCustomObject]@{
            Domeincontroller = $dc.HostName
            LdapSigning      = $r.LdapSigning
            ChannelBinding   = $r.ChannelBinding
            LmCompatibility  = $r.LmLevel
            Smb1             = $r.Smb1
            Afdrukspooler    = $r.Spooler
            Punten           = if ($points.Count -eq 0) { 'in orde' } else { $points -join '; ' }
        }
    }
    catch {
        [PSCustomObject]@{
            Domeincontroller = $dc.HostName
            LdapSigning      = $null
            ChannelBinding   = $null
            LmCompatibility  = $null
            Smb1             = $null
            Afdrukspooler    = $null
            Punten           = "niet bereikbaar: $($_.Exception.Message)"
        }
    }
}

$rows | Format-Table -AutoSize -Wrap
$open = @($rows | Where-Object { $_.Punten -ne 'in orde' })
Write-Host ("`n{0} van {1} domeincontroller(s) met een punt. Pas wijzigingen toe met Groepsbeleid op de OU Domain Controllers, na een test." -f $open.Count, $rows.Count) -ForegroundColor Cyan
if ($global:MossUitvoer) { $rows | Export-Csv -Path (Join-Path $global:MossUitvoer 'Domeincontrollers-hardening.csv') -NoTypeInformation -Encoding UTF8 }
