<#
.SYNOPSIS
    Runs Exchange Online commands for the management app Moss365.

.DESCRIPTION
    The app starts this script in a separate PowerShell process (Microsoft.Graph and ExchangeOnlineManagement disturb
    each other in the same process). The script reads a JSON list of commands, runs them and writes the result of each
    command back as JSON.

    Safety: only the commands and parameters in $Toegestaan are possible. Values are passed as parameters (splatting),
    never run as script text. The script converts numbers and yes/no values itself.

.PARAMETER Opdrachten
    JSON file: [ { "id": "...", "op": "LidToevoegen", "params": { "Identity": "...", "Member": "..." } }, ... ]

.PARAMETER Uitvoer
    JSON file for the results: [ { "id", "status" (Gelukt/Mislukt), "melding", "data" } ]

.PARAMETER Upn
    Admin account to fill in the sign-in beforehand.

.PARAMETER ZonderVerbinding
    For tests: do not load the module and do not sign in (the cmdlets are faked then).

.PARAMETER WachtSeconden
    Wait between retries when an object that was just created is not found yet (replication).

.NOTES
    Signing in: the app passes an access token in the environment variable MOSS365_EXO_TOKEN (never on the command
    line or on disk). Without a token the module signs in by itself via the browser.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Opdrachten,
    [Parameter(Mandatory = $true)][string]$Uitvoer,
    [string]$Upn,
    [switch]$ZonderVerbinding,
    [int]$WachtSeconden = 10
)

$ErrorActionPreference = 'Stop'

$Toegestaan = @{
    'LidToevoegen'     = @('Identity', 'Member')
    'LidVerwijderen'   = @('Identity', 'Member')
    'Agendaverwerking' = @('Identity', 'AutomateProcessing', 'BookingWindowInDays', 'MaximumDurationInMinutes', 'AllowConflicts',
        'AllowRecurringMeetings', 'AddOrganizerToSubject', 'DeleteSubject')
    'Place'            = @('Identity', 'Building', 'Floor', 'FloorLabel', 'Capacity', 'City', 'Street', 'PostalCode', 'CountryOrRegion',
                           'AudioDeviceName', 'VideoDeviceName', 'DisplayDeviceName', 'IsWheelChairAccessible')
    'AgendaRecht'      = @('Identity', 'User', 'AccessRights')
    'NaarGedeeld'      = @('Identity')
    'LeesRooms'        = @()
    'LeesPostvakken'   = @()
    'LeesObjecten'     = @('Adressen')
    'GroepInstellen'   = @('Identity', 'DisplayName', 'PrimarySmtpAddress', 'HiddenFromAddressListsEnabled', 'RequireSenderAuthenticationEnabled', 'MemberJoinRestriction')
    'Beheerder'        = @('Identity', 'Toevoegen', 'Verwijderen')
    'PostvakInstellen' = @('Identity', 'DisplayName', 'HiddenFromAddressListsEnabled', 'ForwardingSmtpAddress', 'DeliverToMailboxAndForward', 'MessageCopyForSentAsEnabled', 'MessageCopyForSendOnBehalfEnabled', 'GrantSendOnBehalfTo', 'WindowsEmailAddress')
    'AutoAntwoord'     = @('Identity', 'AutoReplyState', 'InternalMessage', 'ExternalMessage', 'StartTime', 'EndTime')
    'Berichtspoor'     = @('SenderAddress', 'RecipientAddress', 'Dagen')
    'Doorsturen'       = @('MetRegels')
    'RechtToevoegen'   = @('Identity', 'Recht', 'Trustee')
    'RechtVerwijderen' = @('Identity', 'Recht', 'Trustee')
    'PostvakAanmaken'  = @('Name', 'DisplayName', 'Alias', 'PrimarySmtpAddress')
    'GroepAanmaken'    = @('Name', 'DisplayName', 'Alias', 'PrimarySmtpAddress', 'Type')
    'ContactAanmaken'  = @('Name', 'DisplayName', 'ExternalEmailAddress', 'Alias', 'FirstName', 'LastName', 'Company', 'Title', 'Phone')
    'ContactInstellen' = @('Identity', 'DisplayName', 'ExternalEmailAddress', 'HiddenFromAddressListsEnabled', 'FirstName', 'LastName', 'Company', 'Title', 'Phone')
    'LeesOrganisatie'  = @()
    'OrganisatieInstellen' = @('Instelling', 'Waarde', 'Identity')
}
$Getallen = @('BookingWindowInDays', 'MaximumDurationInMinutes', 'Floor', 'Capacity')
$JaNee = @('AllowConflicts', 'AllowRecurringMeetings', 'AddOrganizerToSubject', 'DeleteSubject', 'HiddenFromAddressListsEnabled', 'DeliverToMailboxAndForward',
    'RequireSenderAuthenticationEnabled', 'MessageCopyForSentAsEnabled', 'MessageCopyForSendOnBehalfEnabled', 'IsWheelChairAccessible')
$PostvakRechten = @('FullAccess', 'SendAs')
$AutoAntwoordStanden = @('Enabled', 'Disabled', 'Scheduled')
$AgendaRechten = @('None', 'AvailabilityOnly', 'LimitedDetails', 'Reviewer', 'Author', 'Editor', 'PublishingEditor', 'Owner')

function ConvertTo-OpdrachtParameters {
    param([string]$Op, $Ruw)
    $uit = @{}
    if ($null -eq $Ruw) { return $uit }
    foreach ($eigenschap in $Ruw.PSObject.Properties) {
        if ($Toegestaan[$Op] -notcontains $eigenschap.Name) {
            throw "Parameter '$($eigenschap.Name)' is not allowed for '$Op'."
        }
        $waarde = [string]$eigenschap.Value
        if ($Getallen -contains $eigenschap.Name) {
            $getal = 0
            if (-not [int]::TryParse($waarde, [ref]$getal)) { throw "'$waarde' is not a whole number for $($eigenschap.Name)." }
            $uit[$eigenschap.Name] = $getal
        }
        elseif ($JaNee -contains $eigenschap.Name) {
            if ($waarde -notin @('True', 'False', 'true', 'false')) { throw "'$waarde' is not a yes/no value for $($eigenschap.Name)." }
            $uit[$eigenschap.Name] = ($waarde -eq 'True' -or $waarde -eq 'true')
        }
        else {
            $uit[$eigenschap.Name] = $waarde
        }
    }
    return $uit
}

function Join-Tekst {
    param($Waarde)
    if ($null -eq $Waarde) { return '' }
    return (@($Waarde | ForEach-Object { "$_" }) -join '|')
}

function Get-AgendaRechtenTekst {
    param([string]$Smtp)
    foreach ($map in 'Calendar', 'Agenda') {
        try {
            $rechten = @(Get-MailboxFolderPermission -Identity ("{0}:\{1}" -f $Smtp, $map) -ErrorAction Stop)
            return (($rechten | ForEach-Object { "{0}={1}" -f $_.User, (@($_.AccessRights) -join ',') }) -join '|')
        }
        catch { }
    }
    return ''
}

function Get-RoomsVoorApp {
    # Without a list: all rooms; with a list: only those (when reading a few objects again)
    param([object[]]$Lijst)
    $rooms = if ($PSBoundParameters.ContainsKey('Lijst')) { @($Lijst) } else {
        @(Get-EXOMailbox -RecipientTypeDetails RoomMailbox -ResultSize Unlimited -Properties DisplayName, Alias, PrimarySmtpAddress, HiddenFromAddressListsEnabled) }
    foreach ($r in $rooms) {
        $smtp = "$($r.PrimarySmtpAddress)"
        $cal = $null; $place = $null
        try { $cal = Get-CalendarProcessing -Identity $smtp -ErrorAction Stop } catch { }
        try { $place = Get-Place -Identity $smtp -ErrorAction Stop } catch { }
        [pscustomobject]@{
            Smtp                     = $smtp
            DisplayName              = "$($r.DisplayName)"
            Alias                    = "$($r.Alias)"
            Gebouw                   = "$($place.Building)"
            Verdieping               = "$($place.Floor)"
            VerdiepingLabel          = "$($place.FloorLabel)"
            Capaciteit               = "$($place.Capacity)"
            Stad                     = "$($place.City)"
            Straat                   = "$($place.Street)"
            Postcode                 = "$($place.PostalCode)"
            Land                     = "$($place.CountryOrRegion)"
            Audio                    = "$($place.AudioDeviceName)"
            Video                    = "$($place.VideoDeviceName)"
            Scherm                   = "$($place.DisplayDeviceName)"
            Rolstoel                 = "$($place.IsWheelChairAccessible)"
            Hidden                   = "$($r.HiddenFromAddressListsEnabled)"
            AutomateProcessing       = "$($cal.AutomateProcessing)"
            AllBookInPolicy          = "$($cal.AllBookInPolicy)"
            AllRequestInPolicy       = "$($cal.AllRequestInPolicy)"
            BookInPolicy             = (Join-Tekst $cal.BookInPolicy)
            ResourceDelegates        = (Join-Tekst $cal.ResourceDelegates)
            BookingWindowInDays      = "$($cal.BookingWindowInDays)"
            MaximumDurationInMinutes = "$($cal.MaximumDurationInMinutes)"
            AllowConflicts           = "$($cal.AllowConflicts)"
            AllowRecurringMeetings   = "$($cal.AllowRecurringMeetings)"
            AddOrganizerToSubject    = "$($cal.AddOrganizerToSubject)"
            DeleteSubject            = "$($cal.DeleteSubject)"
            AgendaRechten            = (Get-AgendaRechtenTekst -Smtp $smtp)
        }
    }
}

function Get-PostvakkenVoorApp {
    # Shared mailboxes with permissions, forwarding and automatic reply; Exchange groups with members.
    # Without lists: everything; with lists: only those objects.
    param([object[]]$Postvakken, [object[]]$Groepen, [object[]]$Contacten)
    $enkele = $PSBoundParameters.ContainsKey('Postvakken')
    $postvakken = if ($enkele) { @($Postvakken) } else {
        @(Get-EXOMailbox -RecipientTypeDetails SharedMailbox, SchedulingMailbox -ResultSize Unlimited `
            -Properties DisplayName, Alias, PrimarySmtpAddress, HiddenFromAddressListsEnabled, IsDirSynced, WhenCreated, ForwardingSmtpAddress, ForwardingAddress, DeliverToMailboxAndForward, MessageCopyForSentAsEnabled, MessageCopyForSendOnBehalfEnabled, GrantSendOnBehalfTo) }
    $pv = foreach ($m in $postvakken) {
        $smtp = "$($m.PrimarySmtpAddress)"
        $auto = $null
        try { $auto = Get-MailboxAutoReplyConfiguration -Identity $smtp -ErrorAction Stop } catch { }
        [pscustomobject]@{
            Type = "$($m.RecipientTypeDetails)"; DisplayName = "$($m.DisplayName)"; Alias = "$($m.Alias)"; Smtp = $smtp
            Hidden = "$($m.HiddenFromAddressListsEnabled)"; IsDirSynced = "$($m.IsDirSynced)"; WhenCreated = "$($m.WhenCreated)"
            DoorsturenNaar = (("$($m.ForwardingSmtpAddress)" -replace '(?i)^smtp:', ''), "$($m.ForwardingAddress)" | Where-Object { $_ } | Select-Object -First 1)
            DoorsturenEnBewaren = "$($m.DeliverToMailboxAndForward)"
            AutoAntwoord = "$($auto.AutoReplyState)"; AutoAntwoordIntern = "$($auto.InternalMessage)"; AutoAntwoordExtern = "$($auto.ExternalMessage)"
            KopieVerzonden = "$($m.MessageCopyForSentAsEnabled)"; KopieNamens = "$($m.MessageCopyForSendOnBehalfEnabled)"; NamensVan = (Join-Tekst $m.GrantSendOnBehalfTo)
        }
    }
    # Trustees sometimes come back as the internal name (Name); convert those to an address
    $adressen = @{}
    $adresVan = {
        param([string]$Naam)
        if (-not $Naam -or $Naam -match '@' -or $Naam -like 'S-1-5-*') { return $Naam }
        if (-not $adressen.ContainsKey($Naam)) {
            $adressen[$Naam] = $Naam
            try { $adressen[$Naam] = "$((Get-Recipient -Identity $Naam -ErrorAction Stop).PrimarySmtpAddress)" } catch { }
        }
        return $adressen[$Naam]
    }
    $rechten = foreach ($m in @($postvakken | Where-Object { "$($_.RecipientTypeDetails)" -eq 'SharedMailbox' })) {
        $smtp = "$($m.PrimarySmtpAddress)"
        try {
            foreach ($r in @(Get-EXOMailboxPermission -Identity $smtp -ErrorAction Stop | Where-Object { -not $_.IsInherited -and $_.User -ne 'NT AUTHORITY\SELF' })) {
                [pscustomobject]@{ Postvak = $smtp; Recht = (@($r.AccessRights) -join ','); Trustee = (& $adresVan "$($r.User)") }
            }
            foreach ($r in @(Get-EXORecipientPermission -Identity $smtp -ErrorAction Stop | Where-Object { $_.Trustee -ne 'NT AUTHORITY\SELF' })) {
                [pscustomobject]@{ Postvak = $smtp; Recht = (@($r.AccessRights) -join ','); Trustee = (& $adresVan "$($r.Trustee)") }
            }
        }
        catch { }
    }
    $alleGroepen = if ($enkele) { @($Groepen) } else { @(Get-DistributionGroup -ResultSize Unlimited) }
    # Not $groepen: for PowerShell that is the same variable as the parameter [object[]]$Groepen
    $exoGroepen = foreach ($g in $alleGroepen) {
        $leden = @()
        try { $leden = @(Get-DistributionGroupMember -Identity "$($g.Identity)" -ResultSize Unlimited -ErrorAction Stop | ForEach-Object { [pscustomobject]@{ Smtp = "$($_.PrimarySmtpAddress)"; Naam = "$($_.DisplayName)" } }) } catch { }
        [pscustomobject]@{
            Type = "$($g.RecipientTypeDetails)"; DisplayName = "$($g.DisplayName)"; Name = "$($g.Name)"; Alias = "$($g.Alias)"; Smtp = "$($g.PrimarySmtpAddress)"
            IsDirSynced = "$($g.IsDirSynced)"; MemberJoinRestriction = "$($g.MemberJoinRestriction)"; ManagedBy = (Join-Tekst $g.ManagedBy)
            Hidden = "$($g.HiddenFromAddressListsEnabled)"; RequireSenderAuthenticationEnabled = "$($g.RequireSenderAuthenticationEnabled)"
            Leden = @($leden)
        }
    }
    # R21: mail contacts, with the details of Get-Contact (company, job title, phone); $null when they cannot be read
    $mailContacten = $null
    try {
        $lijst = if ($enkele) { @($Contacten) } else { @(Get-MailContact -ResultSize Unlimited -ErrorAction Stop) }
        $details = @{}
        $bron = if ($enkele) { @($lijst | ForEach-Object { Get-Contact -Identity "$($_.Guid)" -ErrorAction SilentlyContinue }) } else { @(Get-Contact -RecipientTypeDetails MailContact -ResultSize Unlimited -ErrorAction SilentlyContinue) }
        foreach ($c in $bron) { if ($c) { $details["$($c.Guid)"] = $c } }
        $mailContacten = @(foreach ($c in $lijst) {
            $d = $details["$($c.Guid)"]
            [pscustomobject]@{
                DisplayName = "$($c.DisplayName)"; Name = "$($c.Name)"; Alias = "$($c.Alias)"
                Extern = ("$($c.ExternalEmailAddress)" -replace '(?i)^smtp:', ''); Smtp = "$($c.PrimarySmtpAddress)"
                Hidden = "$($c.HiddenFromAddressListsEnabled)"; IsDirSynced = "$($c.IsDirSynced)"
                Voornaam = "$($d.FirstName)"; Achternaam = "$($d.LastName)"; Bedrijf = "$($d.Company)"; Functie = "$($d.Title)"; Telefoon = "$($d.Phone)"
            }
        })
    }
    catch { $mailContacten = $null }
    return [pscustomobject]@{ Postvakken = @($pv); Rechten = @($rechten); Groepen = @($exoGroepen); Contacten = $mailContacten }
}

function Get-ObjectenVoorApp {
    # Read a few rooms, mailboxes or groups again; Adressen = addresses separated by |
    param([string]$Adressen)
    $rooms = @(); $postvakken = @(); $groepen = @(); $contacten = @(); $weg = @()
    foreach ($adres in @($Adressen -split '\|' | Where-Object { $_ })) {
        try { $ontvanger = Get-Recipient -Identity $adres -ErrorAction Stop }
        catch { $weg += $adres; continue }
        switch -Wildcard ("$($ontvanger.RecipientTypeDetails)") {
            'RoomMailbox' {
                $rooms += Get-EXOMailbox -Identity $adres -Properties DisplayName, Alias, PrimarySmtpAddress, HiddenFromAddressListsEnabled
            }
            { $_ -in 'SharedMailbox', 'SchedulingMailbox' } {
                $postvakken += Get-EXOMailbox -Identity $adres -Properties DisplayName, Alias, PrimarySmtpAddress, HiddenFromAddressListsEnabled, IsDirSynced, WhenCreated, ForwardingSmtpAddress, ForwardingAddress, DeliverToMailboxAndForward, MessageCopyForSentAsEnabled, MessageCopyForSendOnBehalfEnabled, GrantSendOnBehalfTo
            }
            { $_ -like 'Mail*Group' -or $_ -eq 'RoomList' } {
                $groepen += Get-DistributionGroup -Identity $adres
            }
            'MailContact' {
                $contacten += Get-MailContact -Identity $adres
            }
        }
    }
    $rest = Get-PostvakkenVoorApp -Postvakken $postvakken -Groepen $groepen -Contacten $contacten
    return [pscustomobject]@{
        Rooms = @(Get-RoomsVoorApp -Lijst $rooms); Postvakken = $rest.Postvakken; Rechten = $rest.Rechten; Groepen = $rest.Groepen; Contacten = $rest.Contacten
        NietGevonden = @($weg)
    }
}

function Invoke-MetHerhaling {
    # Objects that were just created are sometimes not found for a few seconds; then try again
    param([scriptblock]$Actie)
    for ($poging = 1; ; $poging++) {
        try { return (& $Actie) }
        catch {
            if ($poging -ge 6 -or $_.Exception.Message -notmatch "couldn't be found|could not be found|wasn't found|niet gevonden") { throw }
            Start-Sleep -Seconds $WachtSeconden
        }
    }
}

function Invoke-Opdracht {
    param([string]$Op, [hashtable]$P)
    switch ($Op) {
        'LidToevoegen' {
            # A group or mailbox that was just created is sometimes not found yet
            Invoke-MetHerhaling { Add-DistributionGroupMember -Identity $P.Identity -Member $P.Member -BypassSecurityGroupManagerCheck -ErrorAction Stop }
            return $null
        }
        'LidVerwijderen' {
            Remove-DistributionGroupMember -Identity $P.Identity -Member $P.Member -BypassSecurityGroupManagerCheck -Confirm:$false -ErrorAction Stop
            return $null
        }
        'Agendaverwerking' {
            Set-CalendarProcessing @P -ErrorAction Stop
            return $null
        }
        'Place' {
            Set-Place @P -ErrorAction Stop
            return $null
        }
        'AgendaRecht' {
            if ($AgendaRechten -notcontains $P.AccessRights) { throw "Unknown calendar permission '$($P.AccessRights)'." }
            $fout = $null
            foreach ($map in 'Calendar', 'Agenda') {
                try {
                    Set-MailboxFolderPermission -Identity ("{0}:\{1}" -f $P.Identity, $map) -User $P.User -AccessRights $P.AccessRights -ErrorAction Stop
                    return $null
                }
                catch { $fout = $_ }
            }
            throw $fout
        }
        'NaarGedeeld' {
            Set-Mailbox -Identity $P.Identity -Type Shared -ErrorAction Stop
            return $null
        }
        'LeesRooms' {
            return @(Get-RoomsVoorApp)
        }
        'LeesPostvakken' {
            return (Get-PostvakkenVoorApp)
        }
        'LeesObjecten' {
            return (Get-ObjectenVoorApp -Adressen $P.Adressen)
        }
        'GroepInstellen' {
            $instellen = @{ Identity = $P.Identity }
            foreach ($k in 'DisplayName', 'PrimarySmtpAddress') { if ($P.ContainsKey($k) -and $P[$k]) { $instellen[$k] = $P[$k] } }
            # R21: the yes/no settings (already booleans: $JaNee), and only the three known ways to join
            foreach ($k in 'HiddenFromAddressListsEnabled', 'RequireSenderAuthenticationEnabled') { if ($P.ContainsKey($k)) { $instellen[$k] = $P[$k] } }
            if ($P.ContainsKey('MemberJoinRestriction') -and $P['MemberJoinRestriction']) {
                if ($P['MemberJoinRestriction'] -notin @('Open', 'Closed', 'ApprovalRequired')) { throw 'Onbekende manier om lid te worden' }
                $instellen['MemberJoinRestriction'] = $P['MemberJoinRestriction']
            }
            Set-DistributionGroup @instellen -BypassSecurityGroupManagerCheck -ErrorAction Stop
            return $null
        }
        'Beheerder' {
            $wijziging = @{}
            if ($P.Toevoegen) { $wijziging['Add'] = $P.Toevoegen }
            if ($P.Verwijderen) { $wijziging['Remove'] = $P.Verwijderen }
            if ($wijziging.Count -eq 0) { throw 'No manager given.' }
            Set-DistributionGroup -Identity $P.Identity -ManagedBy $wijziging -BypassSecurityGroupManagerCheck -ErrorAction Stop
            return $null
        }
        'PostvakInstellen' {
            $instellen = @{ Identity = $P.Identity }
            foreach ($k in 'DisplayName', 'HiddenFromAddressListsEnabled', 'DeliverToMailboxAndForward', 'MessageCopyForSentAsEnabled', 'MessageCopyForSendOnBehalfEnabled') { if ($P.ContainsKey($k)) { $instellen[$k] = $P[$k] } }
            # R21: who may send on behalf of the mailbox: the whole list, addresses separated by | (empty = nobody)
            if ($P.ContainsKey('GrantSendOnBehalfTo')) {
                $instellen['GrantSendOnBehalfTo'] = @($P.GrantSendOnBehalfTo -split '\|' | Where-Object { $_ })
            }
            # R21: a new primary address (the old one stays as an alias in Exchange Online); never empty
            if ($P.ContainsKey('WindowsEmailAddress')) {
                if (-not $P.WindowsEmailAddress) { throw 'No primary address given.' }
                $instellen['WindowsEmailAddress'] = $P.WindowsEmailAddress
            }
            if ($P.ContainsKey('ForwardingSmtpAddress')) {
                # Empty = turn forwarding off
                $instellen['ForwardingSmtpAddress'] = $(if ($P.ForwardingSmtpAddress) { $P.ForwardingSmtpAddress } else { $null })
            }
            Set-Mailbox @instellen -ErrorAction Stop
            return $null
        }
        'AutoAntwoord' {
            if ($AutoAntwoordStanden -notcontains $P.AutoReplyState) { throw "Unknown state '$($P.AutoReplyState)' for the automatic reply." }
            foreach ($t in 'StartTime', 'EndTime') {
                if ($P.ContainsKey($t)) { $P[$t] = [datetime]::Parse($P[$t], [Globalization.CultureInfo]::InvariantCulture) }
            }
            Set-MailboxAutoReplyConfiguration @P -ErrorAction Stop
            return $null
        }
        'Doorsturen' {
            # Read only: forwarding on every mailbox, and (MetRegels) inbox rules that forward or redirect
            $uit = New-Object System.Collections.Generic.List[object]
            $postvakken = @(Get-Mailbox -ResultSize Unlimited -ErrorAction Stop)
            foreach ($m in $postvakken) {
                foreach ($doel in @("$($m.ForwardingSmtpAddress)", "$($m.ForwardingAddress)")) {
                    if ($doel) { $uit.Add([ordered]@{ Mailbox = "$($m.PrimarySmtpAddress)"; Kind = 'postvak'; Name = ''; Target = $doel }) }
                }
            }
            if ($P.MetRegels) {
                foreach ($m in $postvakken | Where-Object { $_.RecipientTypeDetails -in 'UserMailbox', 'SharedMailbox' }) {
                    foreach ($r in @(Get-InboxRule -Mailbox "$($m.PrimarySmtpAddress)" -ErrorAction SilentlyContinue | Where-Object { $_.Enabled })) {
                        foreach ($doel in @($r.ForwardTo) + @($r.RedirectTo) + @($r.ForwardAsAttachmentTo)) {
                            if ($doel) { $uit.Add([ordered]@{ Mailbox = "$($m.PrimarySmtpAddress)"; Kind = 'regel'; Name = "$($r.Name)"; Target = "$doel" }) }
                        }
                    }
                }
            }
            return @($uit)
        }
        'Berichtspoor' {
            # Message trace (read only): the last days (at most 10), for a sender and/or a recipient
            $dagen = [Math]::Max(1, [Math]::Min(10, [int]$P.Dagen))
            $zoek = @{ StartDate = (Get-Date).AddDays(-$dagen); EndDate = (Get-Date) }
            if ($P.SenderAddress) { $zoek['SenderAddress'] = $P.SenderAddress }
            if ($P.RecipientAddress) { $zoek['RecipientAddress'] = $P.RecipientAddress }
            $cmd = if (Get-Command Get-MessageTraceV2 -ErrorAction SilentlyContinue) { 'Get-MessageTraceV2' } else { 'Get-MessageTrace' }
            return @(& $cmd @zoek -ErrorAction Stop | Select-Object -First 1000 | ForEach-Object {
                [ordered]@{
                    Received = "$($_.Received.ToString('o'))"; Sender = "$($_.SenderAddress)"; Recipient = "$($_.RecipientAddress)"
                    Subject = "$($_.Subject)"; Status = "$($_.Status)"; Size = [long]$_.Size; Id = "$($_.MessageTraceId)"
                }
            })
        }
        'RechtToevoegen' {
            if ($PostvakRechten -notcontains $P.Recht) { throw "Unknown mailbox permission '$($P.Recht)'." }
            Invoke-MetHerhaling {
                if ($P.Recht -eq 'FullAccess') {
                    Add-MailboxPermission -Identity $P.Identity -User $P.Trustee -AccessRights FullAccess -InheritanceType All -AutoMapping:$false -ErrorAction Stop | Out-Null
                }
                else {
                    Add-RecipientPermission -Identity $P.Identity -Trustee $P.Trustee -AccessRights SendAs -Confirm:$false -ErrorAction Stop | Out-Null
                }
            }
            return $null
        }
        'RechtVerwijderen' {
            if ($PostvakRechten -notcontains $P.Recht) { throw "Unknown mailbox permission '$($P.Recht)'." }
            if ($P.Recht -eq 'FullAccess') {
                Remove-MailboxPermission -Identity $P.Identity -User $P.Trustee -AccessRights FullAccess -InheritanceType All -Confirm:$false -ErrorAction Stop
            }
            else {
                Remove-RecipientPermission -Identity $P.Identity -Trustee $P.Trustee -AccessRights SendAs -Confirm:$false -ErrorAction Stop
            }
            return $null
        }
        'PostvakAanmaken' {
            New-Mailbox -Shared @P -ErrorAction Stop | Out-Null
            return $null
        }
        'LeesOrganisatie' {
            return (Get-OrganisatieVoorApp)
        }
        'OrganisatieInstellen' {
            Set-OrganisatieVoorApp -P $P
            return $null
        }
        'ContactAanmaken' {
            # R21: a mail contact; company, job title and phone through Set-Contact once it can be found
            $nieuw = @{}
            foreach ($k in 'Name', 'DisplayName', 'ExternalEmailAddress', 'Alias', 'FirstName', 'LastName') { if ($P.ContainsKey($k) -and $P[$k]) { $nieuw[$k] = $P[$k] } }
            if (-not $nieuw['ExternalEmailAddress'] -or -not $nieuw['Name']) { throw 'A contact needs a name and an address.' }
            $contact = New-MailContact @nieuw -ErrorAction Stop
            $meer = @{}
            foreach ($k in 'Company', 'Title', 'Phone') { if ($P.ContainsKey($k) -and $P[$k]) { $meer[$k] = $P[$k] } }
            # Set-Contact knows the contact by its GUID, not by its address
            if ($meer.Count -gt 0) { Invoke-MetHerhaling { Set-Contact -Identity "$($contact.Guid)" @meer -ErrorAction Stop } | Out-Null }
            return $null
        }
        'ContactInstellen' {
            # R21: the address book entry (Set-MailContact) and the details (Set-Contact); empty clears a detail
            $mail = @{}
            foreach ($k in 'DisplayName', 'ExternalEmailAddress') {
                if ($P.ContainsKey($k)) {
                    if (-not $P[$k]) { throw "$k cannot be empty." }
                    $mail[$k] = $P[$k]
                }
            }
            if ($P.ContainsKey('HiddenFromAddressListsEnabled')) { $mail['HiddenFromAddressListsEnabled'] = $P['HiddenFromAddressListsEnabled'] }
            $contact = Get-MailContact -Identity $P.Identity -ErrorAction Stop
            if ($mail.Count -gt 0) { Set-MailContact -Identity "$($contact.Guid)" @mail -ErrorAction Stop }
            $meer = @{}
            foreach ($k in 'FirstName', 'LastName', 'Company', 'Title', 'Phone') { if ($P.ContainsKey($k)) { $meer[$k] = $(if ($P[$k]) { $P[$k] } else { $null }) } }
            if ($meer.Count -gt 0) { Set-Contact -Identity "$($contact.Guid)" @meer -ErrorAction Stop }
            return $null
        }
        'GroepAanmaken' {
            # Security (mail-enabled security group, default) or Distribution (distribution list)
            $type = 'Security'
            if ($P.ContainsKey('Type')) { $type = "$($P.Type)"; $P.Remove('Type') }
            if ($type -notin 'Security', 'Distribution') { throw "Unknown group type '$type'." }
            New-DistributionGroup @P -Type $type -MemberJoinRestriction Closed -MemberDepartRestriction Closed -ErrorAction Stop | Out-Null
            return $null
        }
    }
}

function Get-TxtRecords {
    # TXT records of a name through DNS (for SPF and DMARC); an empty list when there are none or DNS does not answer
    param([string]$Naam, [string]$Begin)
    try {
        return @(Resolve-DnsName -Name $Naam -Type TXT -DnsOnly -ErrorAction Stop |
            Where-Object { $_.Strings } | ForEach-Object { ($_.Strings -join '') } | Where-Object { $_ -like "$Begin*" })
    }
    catch { return @() }
}

function Get-OrganisatieVoorApp {
    # Read only (R16): the organisation settings the advice looks at, and SPF, DKIM and DMARC per accepted domain
    $transport = Get-TransportConfig -ErrorAction Stop
    $smtp = @(Get-CASMailbox -ResultSize Unlimited -Filter 'SmtpClientAuthenticationDisabled -eq $false' -ErrorAction SilentlyContinue |
        ForEach-Object { "$($_.PrimarySmtpAddress)" })
    $spam = @(Get-HostedOutboundSpamFilterPolicy -ErrorAction SilentlyContinue |
        ForEach-Object { [ordered]@{ Name = "$($_.Name)"; Mode = "$($_.AutoForwardingMode)"; Default = [bool]$_.IsDefault } })
    $extern = $null
    try { $extern = [bool](@(Get-ExternalInOutlook -ErrorAction Stop)[0].Enabled) } catch { }
    $audit = $null
    try { $audit = [bool](Get-AdminAuditLogConfig -ErrorAction Stop).UnifiedAuditLogIngestionEnabled } catch { }
    $dkim = @{}
    foreach ($d in @(Get-DkimSigningConfig -ErrorAction SilentlyContinue)) { $dkim["$($d.Domain)"] = $d }
    $domeinen = foreach ($a in @(Get-AcceptedDomain -ErrorAction SilentlyContinue)) {
        $naam = "$($a.DomainName)"
        $k = $dkim[$naam]
        # An empty array through an if statement becomes $null: so first in a variable
        $spf = @()
        $dmarc = @()
        if ($naam -notlike '*.onmicrosoft.com') {
            $spf = @(Get-TxtRecords -Naam $naam -Begin 'v=spf1')
            $dmarc = @(Get-TxtRecords -Naam "_dmarc.$naam" -Begin 'v=DMARC1')
        }
        [ordered]@{
            Domain      = $naam
            Spf         = @($spf)
            Dmarc       = @($dmarc)
            DkimEnabled = if ($k) { [bool]$k.Enabled } else { $null }
            Selector1   = if ($k) { "$($k.Selector1CNAME)" } else { '' }
            Selector2   = if ($k) { "$($k.Selector2CNAME)" } else { '' }
        }
    }
    $preset = $null
    try {
        $eop = @(Get-EOPProtectionPolicyRule -ErrorAction Stop | ForEach-Object { [ordered]@{ Name = "$($_.Name)"; State = "$($_.State)" } })
        $atp = @()
        $atpOk = $true
        try { $atp = @(Get-ATPProtectionPolicyRule -ErrorAction Stop | ForEach-Object { [ordered]@{ Name = "$($_.Name)"; State = "$($_.State)" } }) }
        catch { $atpOk = $false }
        $preset = [ordered]@{ Eop = @($eop); Atp = @($atp); AtpBeschikbaar = $atpOk }
    }
    catch { }
    return [ordered]@{
        SmtpAuthUit        = [bool]$transport.SmtpClientAuthenticationDisabled
        SmtpAuthPostvakken = @($smtp)
        Doorsturen         = @($spam)
        ExternLabel        = $extern
        Auditlog           = $audit
        Domeinen           = @($domeinen)
        Preset             = $preset
    }
}

function Set-OrganisatieVoorApp {
    # Only these settings, with these values (R16); the app keeps the previous value of each (undo)
    param([hashtable]$P)
    $aan = "$($P.Waarde)" -in @('True', 'true')
    switch ([string]$P.Instelling) {
        'SmtpAuth' { Set-TransportConfig -SmtpClientAuthenticationDisabled (-not $aan) -ErrorAction Stop }
        'SmtpAuthPostvak' {
            # An empty value: the mailbox follows the organisation again
            if ("$($P.Waarde)" -eq '') { Set-CASMailbox -Identity $P.Identity -SmtpClientAuthenticationDisabled $null -ErrorAction Stop }
            else { Set-CASMailbox -Identity $P.Identity -SmtpClientAuthenticationDisabled (-not $aan) -ErrorAction Stop }
        }
        'Doorsturen' {
            if ("$($P.Waarde)" -notin @('Automatic', 'On', 'Off')) { throw "'$($P.Waarde)' is not a forwarding mode." }
            Set-HostedOutboundSpamFilterPolicy -Identity $P.Identity -AutoForwardingMode $P.Waarde -ErrorAction Stop
        }
        'ExternLabel' { Set-ExternalInOutlook -Enabled $aan -ErrorAction Stop }
        'Auditlog' { Set-AdminAuditLogConfig -UnifiedAuditLogIngestionEnabled $aan -ErrorAction Stop }
        'Dkim' {
            if (Get-DkimSigningConfig -Identity $P.Identity -ErrorAction SilentlyContinue) { Set-DkimSigningConfig -Identity $P.Identity -Enabled $aan -ErrorAction Stop }
            else { New-DkimSigningConfig -DomainName $P.Identity -Enabled $aan -ErrorAction Stop }
        }
        'Preset' {
            # The preset rule of Standard or Strict for every accepted domain; the Defender for Office part where it exists
            if ("$($P.Identity)" -notin @('Standard Preset Security Policy', 'Strict Preset Security Policy')) { throw "'$($P.Identity)' is not a preset policy." }
            $atp = Get-ATPProtectionPolicyRule -Identity $P.Identity -ErrorAction SilentlyContinue
            if ($aan) {
                $domeinen = @(Get-AcceptedDomain -ErrorAction Stop | ForEach-Object { "$($_.DomainName)" })
                Set-EOPProtectionPolicyRule -Identity $P.Identity -RecipientDomainIs $domeinen -ErrorAction Stop
                Enable-EOPProtectionPolicyRule -Identity $P.Identity -Confirm:$false -ErrorAction Stop
                if ($atp) {
                    Set-ATPProtectionPolicyRule -Identity $P.Identity -RecipientDomainIs $domeinen -ErrorAction Stop
                    Enable-ATPProtectionPolicyRule -Identity $P.Identity -Confirm:$false -ErrorAction Stop
                }
            }
            else {
                Disable-EOPProtectionPolicyRule -Identity $P.Identity -Confirm:$false -ErrorAction Stop
                if ($atp) { Disable-ATPProtectionPolicyRule -Identity $P.Identity -Confirm:$false -ErrorAction Stop }
            }
        }
        default { throw "Setting '$($P.Instelling)' is not allowed." }
    }
}

function New-ExoAanmelding {
    # Parameters for Connect-ExchangeOnline. This process has no window; the Windows sign-in broker (WAM, default
    # since ExchangeOnlineManagement 3.7) needs one ("A window handle must be configured"). With -DisableWAM the
    # module signs in via the browser. Older versions do not know that parameter and do not use WAM either.
    # With a token from the app (the same sign-in as Graph) no second sign-in is needed.
    param([string]$Upn, [string]$Token, [string[]]$BeschikbareParameters)
    $aanmelding = @{ ShowBanner = $false }
    if ($Upn) { $aanmelding['UserPrincipalName'] = $Upn }
    if ($Token -and $BeschikbareParameters -contains 'AccessToken') {
        $aanmelding['AccessToken'] = $Token
        return $aanmelding
    }
    if ($BeschikbareParameters -contains 'DisableWAM') { $aanmelding['DisableWAM'] = $true }
    return $aanmelding
}

$lijst = @(Get-Content -LiteralPath $Opdrachten -Raw -Encoding UTF8 | ConvertFrom-Json)

$verbindingsFout = $null
if (-not $ZonderVerbinding) {
    try {
        Import-Module ExchangeOnlineManagement -ErrorAction Stop
        $token = $env:MOSS365_EXO_TOKEN
        $env:MOSS365_EXO_TOKEN = $null
        $aanmelding = New-ExoAanmelding -Upn $Upn -Token $token -BeschikbareParameters @((Get-Command Connect-ExchangeOnline).Parameters.Keys)
        Connect-ExchangeOnline @aanmelding
    }
    catch {
        $verbindingsFout = "Signing in to Exchange Online failed: $($_.Exception.Message)"
    }
}

$resultaten = foreach ($o in $lijst) {
    if ($verbindingsFout) {
        [pscustomobject]@{ id = $o.id; status = 'Mislukt'; melding = $verbindingsFout; data = $null }
        continue
    }
    $parameters = $null
    try {
        if (-not $Toegestaan.ContainsKey([string]$o.op)) { throw "Command '$($o.op)' is not allowed." }
        $parameters = ConvertTo-OpdrachtParameters -Op $o.op -Ruw $o.params
        $data = Invoke-Opdracht -Op $o.op -P $parameters
        [pscustomobject]@{ id = $o.id; status = 'Gelukt'; melding = ''; data = $data }
    }
    catch {
        $melding = $_.Exception.Message
        if ($o.op -eq 'LidToevoegen' -and $melding -match 'already a member') {
            [pscustomobject]@{ id = $o.id; status = 'Gelukt'; melding = 'Was al lid.'; data = $null }
        }
        elseif ($o.op -eq 'RechtVerwijderen' -and $melding -match "doesn't exist|does not exist|bestaat niet") {
            [pscustomobject]@{ id = $o.id; status = 'Gelukt'; melding = 'Recht stond er al niet meer.'; data = $null }
        }
        else {
            [pscustomobject]@{ id = $o.id; status = 'Mislukt'; melding = $melding; data = $null }
        }
    }
}

$json = ConvertTo-Json -InputObject @($resultaten) -Depth 6
Set-Content -LiteralPath $Uitvoer -Value $json -Encoding UTF8

if (-not $ZonderVerbinding -and -not $verbindingsFout) {
    Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue
}
