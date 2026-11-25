<#MOSS365
Name: Room instellen
Description: Naam, gebouw, stad, verdieping en capaciteit van een vergaderruimte, en de roomlijst van het gebouw
Version: 1.0
Connection: Exchange
Writes: yes
Variable: Room | Room | required | Welke room? |  |
Variable: Weergavenaam | Text | optional | Nieuwe naam (leeg = niet wijzigen) |  |
Variable: Gebouw | Text | optional | Gebouw (leeg = niet wijzigen) |  |
Variable: Stad | Text | optional | Stad (leeg = niet wijzigen) |  |
Variable: Verdieping | Number | optional | Verdieping (leeg = niet wijzigen) |  |
Variable: Verdiepingslabel | Text | optional | Verdiepingslabel, bv. 2de verdieping |  |
Variable: Capaciteit | Number | optional | Aantal plaatsen (leeg = niet wijzigen) |  |
Variable: Roomlijst | Group | optional | Roomlijst van het gebouw (leeg = niet wijzigen) |  |
#>
<#
.SYNOPSIS
    Sets the name, building, city, floor, floor label and capacity of a meeting room, and adds it to the room list of
    the building. Only the values you fill in change. With -WhatIf it shows what would change.

.EXAMPLE
    .\Set-RoomPlace.ps1 -Room room1@contoso.com -Gebouw 'Building A' -Verdieping 2 -Capaciteit 8 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$Room,
    [string]$Weergavenaam,
    [string]$Gebouw,
    [string]$Stad,
    [Nullable[int]]$Verdieping,
    [string]$Verdiepingslabel,
    [Nullable[int]]$Capaciteit,
    [string]$Roomlijst
)

$ErrorActionPreference = 'Stop'

# Moss365 connects to Exchange Online before the script runs; outside the app, connect first yourself
if (-not (Get-Command Get-Mailbox -ErrorAction SilentlyContinue)) { throw 'Geen verbinding met Exchange Online: start dit script vanuit Moss365, of verbind eerst zelf.' }

$mb = Get-Mailbox -Identity $Room
$address = $mb.PrimarySmtpAddress.ToString()
Write-Host '=== Voor ===' -ForegroundColor Cyan
Get-Place -Identity $address | Format-List DisplayName, Building, City, Floor, FloorLabel, Capacity

if ($Weergavenaam -and $PSCmdlet.ShouldProcess($address, "Naam wordt '$Weergavenaam'")) {
    Set-Mailbox -Identity $address -DisplayName $Weergavenaam -Name $Weergavenaam
    # The place follows the mailbox with a delay; set it there too and check a few times
    for ($i = 0; $i -lt 5; $i++) {
        Set-Place -Identity $address -DisplayName $Weergavenaam -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        if ((Get-Place -Identity $address).DisplayName -eq $Weergavenaam) { break }
    }
}

$settings = @{ Identity = $address }
if ($Gebouw) { $settings.Building = $Gebouw }
if ($Stad) { $settings.City = $Stad }
if ($null -ne $Verdieping) { $settings.Floor = $Verdieping }
if ($Verdiepingslabel) { $settings.FloorLabel = $Verdiepingslabel }
if ($null -ne $Capaciteit) { $settings.Capacity = $Capaciteit }
if ($settings.Count -gt 1 -and $PSCmdlet.ShouldProcess($address, 'Plaats instellen: ' + (($settings.Keys | Where-Object { $_ -ne 'Identity' }) -join ', '))) {
    Set-Place @settings
}

if ($Roomlijst) {
    $members = Get-DistributionGroupMember -Identity $Roomlijst -ResultSize Unlimited -ErrorAction SilentlyContinue
    if ($members.PrimarySmtpAddress -contains $address) {
        Write-Host "Al lid van de roomlijst $Roomlijst."
    }
    elseif ($PSCmdlet.ShouldProcess($Roomlijst, "Room $address toevoegen")) {
        Add-DistributionGroupMember -Identity $Roomlijst -Member $address
    }
}

if (-not $WhatIfPreference) {
    Write-Host '=== Na ===' -ForegroundColor Cyan
    Get-Place -Identity $address | Format-List DisplayName, Building, City, Floor, FloorLabel, Capacity
}
