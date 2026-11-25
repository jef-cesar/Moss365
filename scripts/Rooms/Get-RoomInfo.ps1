<#MOSS365
Name: Room bekijken
Description: Alle gegevens van een vergaderruimte: postvak, plaats (gebouw, verdieping, capaciteit), agendaverwerking en roomlijsten
Version: 1.0
Connection: Exchange
Writes: no
Variable: Room | Room | required | Welke room? |  |
Variable: Alles | YesNo | optional | Alle eigenschappen tonen (ruw) |  |
#>
<#
.SYNOPSIS
    Shows a meeting room: the mailbox, the place (building, floor, capacity), calendar processing and the room lists
    it is in. With -Alles every property (raw). Reads only.

.EXAMPLE
    .\Get-RoomInfo.ps1 -Room room1@contoso.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Room,
    [switch]$Alles
)

$ErrorActionPreference = 'Stop'

# Moss365 connects to Exchange Online before the script runs; outside the app, connect first yourself
if (-not (Get-Command Get-Mailbox -ErrorAction SilentlyContinue)) { throw 'Geen verbinding met Exchange Online: start dit script vanuit Moss365, of verbind eerst zelf.' }

$mb = Get-Mailbox -Identity $Room
$place = Get-Place -Identity $mb.PrimarySmtpAddress.ToString()
$calendar = Get-CalendarProcessing -Identity $mb.PrimarySmtpAddress.ToString()

if ($Alles) {
    Write-Host '--- Postvak ---' -ForegroundColor Yellow
    $mb | Format-List *
    Write-Host '--- Plaats ---' -ForegroundColor Yellow
    $place | Format-List *
    Write-Host '--- Agendaverwerking ---' -ForegroundColor Yellow
    $calendar | Format-List *
    return
}

# The room lists this room is a member of
$lists = foreach ($rl in Get-DistributionGroup -RecipientTypeDetails RoomList -ResultSize Unlimited) {
    $members = Get-DistributionGroupMember -Identity $rl.Identity -ResultSize Unlimited -ErrorAction SilentlyContinue
    if ($members.PrimarySmtpAddress -contains $mb.PrimarySmtpAddress) { $rl.Name }
}

[PSCustomObject]@{
    Naam              = $mb.DisplayName
    Adres             = $mb.PrimarySmtpAddress
    Soort             = $mb.RecipientTypeDetails
    Gebouw            = $place.Building
    Stad              = $place.City
    Verdieping        = $place.Floor
    Verdiepingslabel  = $place.FloorLabel
    Capaciteit        = $place.Capacity
    Verwerking        = $calendar.AutomateProcessing
    BoekingsvensterDagen = $calendar.BookingWindowInDays
    MaxDuurMinuten    = $calendar.MaximumDurationInMinutes
    Roomlijsten       = ($lists -join ', ')
}
