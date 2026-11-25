<#MOSS365
Name: Agendarechten van alle rooms
Description: Iedereen in de organisatie mag de agenda van alle vergaderruimtes zien (Reviewer of LimitedDetails)
Version: 1.0
Connection: Exchange
Writes: yes
Variable: AccessRights | Choice | optional | Welke rechten voor iedereen? | Reviewer | Reviewer;LimitedDetails
Variable: IncludePrivate | YesNo | optional | Ook prive-items zichtbaar maken |  |
#>
<#
.SYNOPSIS
Stelt alle vergaderruimte-agenda's (Room mailboxes) in zodat iedereen in de organisatie
alle details kan zien.

.PARAMETER AccessRights
'Reviewer' (alles) of 'LimitedDetails' (alleen onderwerp + locatie). Standaard 'Reviewer'.

.PARAMETER IncludePrivate
Maak ook 'Prive' items zichtbaar (verwijdert de Private-vlag van binnenkomende aanvragen).

.NOTES
Gebruikt ExchangeOnlineManagement en Get-EXOMailbox.
#>

[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='Medium')]
param(
    [ValidateSet('Reviewer','LimitedDetails')]
    [string]$AccessRights = 'Reviewer',
    [switch]$IncludePrivate
)

# Helpers om -WhatIf/-Confirm door te geven aan subcmdlets alleen als de caller ze gebruikt
$commonSwitches = @{}
if ($PSBoundParameters.ContainsKey('WhatIf'))   { $commonSwitches['WhatIf']   = $true }
if ($PSBoundParameters.ContainsKey('Confirm'))  { $commonSwitches['Confirm']  = $true }

# Moss365 connects to Exchange Online before the script runs; outside the app, connect first yourself
if (-not (Get-Command Get-Mailbox -ErrorAction SilentlyContinue)) { throw 'Geen verbinding met Exchange Online: start dit script vanuit Moss365, of verbind eerst zelf.' }

# 1) Back-up huidige instellingen
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = @()

$rooms = Get-EXOMailbox -RecipientTypeDetails RoomMailbox -ResultSize Unlimited

foreach ($room in $rooms) {
    $folderName = (
        Get-MailboxFolderStatistics -Identity $room.Identity |
        Where-Object FolderType -eq 'Calendar' |
        Select-Object -First 1 -ExpandProperty Name
    )
    if (-not $folderName) { Write-Warning "Geen agenda gevonden: $($room.PrimarySmtpAddress)"; continue }
    $folderIdentity = "$($room.PrimarySmtpAddress):\$folderName"

    $calProc = Get-CalendarProcessing -Identity $room.Identity
    $defPerm = $null; $anonPerm = $null
    try { $defPerm = Get-MailboxFolderPermission -Identity $folderIdentity -User Default -ErrorAction Stop } catch {}
    try { $anonPerm = Get-MailboxFolderPermission -Identity $folderIdentity -User Anonymous -ErrorAction Stop } catch {}

    $backup += [pscustomobject]@{
        Room                   = $room.PrimarySmtpAddress
        FolderIdentity         = $folderIdentity
        AccessRightsDefault    = ($defPerm.AccessRights -join ',')
        AccessRightsAnonymous  = ($anonPerm.AccessRights -join ',')
        AddOrganizerToSubject  = $calProc.AddOrganizerToSubject
        DeleteSubject          = $calProc.DeleteSubject
        DeleteComments         = $calProc.DeleteComments
        RemovePrivateProperty  = $calProc.RemovePrivateProperty
        AutomateProcessing     = $calProc.AutomateProcessing
    }
}

$backupPath = "RoomCalendarSettings-Backup-$timestamp.csv"
$backup | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $backupPath
Write-Host "Back-up weggeschreven: $backupPath"

# 2) Wijzigingen doorvoeren
foreach ($room in $rooms) {
    $folderName = (
        Get-MailboxFolderStatistics -Identity $room.Identity |
        Where-Object FolderType -eq 'Calendar' |
        Select-Object -First 1 -ExpandProperty Name
    )
    if (-not $folderName) { continue }
    $folderIdentity = "$($room.PrimarySmtpAddress):\$folderName"

    if ($PSCmdlet.ShouldProcess("$($room.PrimarySmtpAddress)", "CalendarProcessing + permissions toepassen")) {

        # a) Kalenderverwerking - onderwerp behouden, organisator tonen, comments behouden
        #    Automatisch accepteren; prive-vlag verwijderen alleen als -IncludePrivate is gezet.
        $removePrivate = $IncludePrivate.IsPresent
        $paramsCal = @{
            Identity               = $room.Identity
            AutomateProcessing     = 'AutoAccept'
            AddOrganizerToSubject  = $true
            DeleteSubject          = $false
            DeleteComments         = $false
            RemovePrivateProperty  = $removePrivate
            Confirm                = $false
        } + $commonSwitches
        Set-CalendarProcessing @paramsCal

        # b) Maprechten - Default = alle interne gebruikers
        $existing = $null
        try { $existing = Get-MailboxFolderPermission -Identity $folderIdentity -User Default -ErrorAction Stop } catch {}

        if ($existing) {
            $paramsSet = @{
                Identity     = $folderIdentity
                User         = 'Default'
                AccessRights = $AccessRights
                Confirm      = $false
            } + $commonSwitches
            Set-MailboxFolderPermission @paramsSet
        } else {
            $paramsAdd = @{
                Identity     = $folderIdentity
                User         = 'Default'
                AccessRights = $AccessRights
                Confirm      = $false
            } + $commonSwitches
            Add-MailboxFolderPermission @paramsAdd
        }

        # c) Externen niets tonen
        try {
            Get-MailboxFolderPermission -Identity $folderIdentity -User Anonymous -ErrorAction Stop | Out-Null
            $paramsAnonSet = @{
                Identity     = $folderIdentity
                User         = 'Anonymous'
                AccessRights = 'None'
                Confirm      = $false
            } + $commonSwitches
            Set-MailboxFolderPermission @paramsAnonSet
        } catch {
            $paramsAnonAdd = @{
                Identity     = $folderIdentity
                User         = 'Anonymous'
                AccessRights = 'None'
                Confirm      = $false
            } + $commonSwitches
            Add-MailboxFolderPermission @paramsAnonAdd
        }
    }
}

Write-Host "Klaar."
Disconnect-ExchangeOnline -Confirm:$false