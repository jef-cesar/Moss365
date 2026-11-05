<#
.SYNOPSIS
    Voert Exchange Online-opdrachten uit voor de beheer-app Moss365.

.DESCRIPTION
    De app start dit script in een apart PowerShell-proces (Microsoft.Graph en ExchangeOnlineManagement storen elkaar
    in hetzelfde proces). Het script leest een JSON-lijst opdrachten, voert ze uit en schrijft per opdracht het resultaat
    terug als JSON.

    Veiligheid: alleen de opdrachten en parameters in $Toegestaan zijn mogelijk. Waarden worden als parameters
    doorgegeven (splatting), nooit als scripttekst uitgevoerd. Getallen en ja/nee-waarden zet het script zelf om.

.PARAMETER Opdrachten
    JSON-bestand: [ { "id": "...", "op": "LidToevoegen", "params": { "Identity": "...", "Member": "..." } }, ... ]

.PARAMETER Uitvoer
    JSON-bestand waarin de resultaten komen: [ { "id", "status" (Gelukt/Mislukt), "melding", "data" } ]

.PARAMETER Upn
    Beheeraccount om de aanmelding vooraf in te vullen.

.PARAMETER ZonderVerbinding
    Voor tests: geen module laden en niet aanmelden (de cmdlets zijn dan nagebootst).

.PARAMETER WachtSeconden
    Wachttijd tussen nieuwe pogingen als een net aangemaakt object nog niet gevonden wordt (replicatie).

.NOTES
    Aanmelden: de app geeft een toegangstoken mee in de omgevingsvariabele MOSS365_EXO_TOKEN (nooit op de
    opdrachtregel of op schijf). Zonder token meldt de module zelf aan via de browser.
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
    'Place'            = @('Identity', 'Building', 'Floor', 'FloorLabel', 'Capacity')
    'AgendaRecht'      = @('Identity', 'User', 'AccessRights')
    'NaarGedeeld'      = @('Identity')
    'LeesRooms'        = @()
    'LeesPostvakken'   = @()
    'LeesObjecten'     = @('Adressen')
    'GroepInstellen'   = @('Identity', 'DisplayName', 'PrimarySmtpAddress')
    'Beheerder'        = @('Identity', 'Toevoegen', 'Verwijderen')
    'PostvakInstellen' = @('Identity', 'DisplayName', 'HiddenFromAddressListsEnabled', 'ForwardingSmtpAddress', 'DeliverToMailboxAndForward')
    'AutoAntwoord'     = @('Identity', 'AutoReplyState', 'InternalMessage', 'ExternalMessage')
    'RechtToevoegen'   = @('Identity', 'Recht', 'Trustee')
    'RechtVerwijderen' = @('Identity', 'Recht', 'Trustee')
    'PostvakAanmaken'  = @('Name', 'DisplayName', 'Alias', 'PrimarySmtpAddress')
    'GroepAanmaken'    = @('Name', 'DisplayName', 'Alias', 'PrimarySmtpAddress', 'Type')
}
$Getallen = @('BookingWindowInDays', 'MaximumDurationInMinutes', 'Floor', 'Capacity')
$JaNee = @('AllowConflicts', 'AllowRecurringMeetings', 'AddOrganizerToSubject', 'DeleteSubject', 'HiddenFromAddressListsEnabled', 'DeliverToMailboxAndForward')
$PostvakRechten = @('FullAccess', 'SendAs')
$AutoAntwoordStanden = @('Enabled', 'Disabled')
$AgendaRechten = @('None', 'AvailabilityOnly', 'LimitedDetails', 'Reviewer', 'Author', 'Editor', 'PublishingEditor', 'Owner')

function ConvertTo-OpdrachtParameters {
    param([string]$Op, $Ruw)
    $uit = @{}
    if ($null -eq $Ruw) { return $uit }
    foreach ($eigenschap in $Ruw.PSObject.Properties) {
        if ($Toegestaan[$Op] -notcontains $eigenschap.Name) {
            throw "Parameter '$($eigenschap.Name)' is niet toegestaan voor '$Op'."
        }
        $waarde = [string]$eigenschap.Value
        if ($Getallen -contains $eigenschap.Name) {
            $getal = 0
            if (-not [int]::TryParse($waarde, [ref]$getal)) { throw "'$waarde' is geen geheel getal voor $($eigenschap.Name)." }
            $uit[$eigenschap.Name] = $getal
        }
        elseif ($JaNee -contains $eigenschap.Name) {
            if ($waarde -notin @('True', 'False', 'true', 'false')) { throw "'$waarde' is geen ja/nee-waarde voor $($eigenschap.Name)." }
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
    # Zonder lijst: alle rooms; met lijst: enkel die (bij het opnieuw lezen van enkele objecten)
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
    # Gedeelde postvakken met rechten, doorsturen en automatisch antwoord; Exchange-groepen met leden.
    # Zonder lijsten: alles; met lijsten: enkel die objecten.
    param([object[]]$Postvakken, [object[]]$Groepen)
    $enkele = $PSBoundParameters.ContainsKey('Postvakken')
    $postvakken = if ($enkele) { @($Postvakken) } else {
        @(Get-EXOMailbox -RecipientTypeDetails SharedMailbox, SchedulingMailbox -ResultSize Unlimited `
            -Properties DisplayName, Alias, PrimarySmtpAddress, HiddenFromAddressListsEnabled, IsDirSynced, WhenCreated, ForwardingSmtpAddress, ForwardingAddress, DeliverToMailboxAndForward) }
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
        }
    }
    # Rechthebbenden komen soms terug als interne naam (Name); zet die om naar een adres
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
    # Niet $groepen: voor PowerShell is dat dezelfde variabele als de parameter [object[]]$Groepen
    $exoGroepen = foreach ($g in $alleGroepen) {
        $leden = @()
        try { $leden = @(Get-DistributionGroupMember -Identity "$($g.Identity)" -ResultSize Unlimited -ErrorAction Stop | ForEach-Object { [pscustomobject]@{ Smtp = "$($_.PrimarySmtpAddress)"; Naam = "$($_.DisplayName)" } }) } catch { }
        [pscustomobject]@{
            Type = "$($g.RecipientTypeDetails)"; DisplayName = "$($g.DisplayName)"; Name = "$($g.Name)"; Alias = "$($g.Alias)"; Smtp = "$($g.PrimarySmtpAddress)"
            IsDirSynced = "$($g.IsDirSynced)"; MemberJoinRestriction = "$($g.MemberJoinRestriction)"; ManagedBy = (Join-Tekst $g.ManagedBy)
            Leden = @($leden)
        }
    }
    return [pscustomobject]@{ Postvakken = @($pv); Rechten = @($rechten); Groepen = @($exoGroepen) }
}

function Get-ObjectenVoorApp {
    # Enkele rooms, postvakken of groepen opnieuw lezen; Adressen = adressen gescheiden door |
    param([string]$Adressen)
    $rooms = @(); $postvakken = @(); $groepen = @(); $weg = @()
    foreach ($adres in @($Adressen -split '\|' | Where-Object { $_ })) {
        try { $ontvanger = Get-Recipient -Identity $adres -ErrorAction Stop }
        catch { $weg += $adres; continue }
        switch -Wildcard ("$($ontvanger.RecipientTypeDetails)") {
            'RoomMailbox' {
                $rooms += Get-EXOMailbox -Identity $adres -Properties DisplayName, Alias, PrimarySmtpAddress, HiddenFromAddressListsEnabled
            }
            { $_ -in 'SharedMailbox', 'SchedulingMailbox' } {
                $postvakken += Get-EXOMailbox -Identity $adres -Properties DisplayName, Alias, PrimarySmtpAddress, HiddenFromAddressListsEnabled, IsDirSynced, WhenCreated, ForwardingSmtpAddress, ForwardingAddress, DeliverToMailboxAndForward
            }
            { $_ -like 'Mail*Group' -or $_ -eq 'RoomList' } {
                $groepen += Get-DistributionGroup -Identity $adres
            }
        }
    }
    $rest = Get-PostvakkenVoorApp -Postvakken $postvakken -Groepen $groepen
    return [pscustomobject]@{
        Rooms = @(Get-RoomsVoorApp -Lijst $rooms); Postvakken = $rest.Postvakken; Rechten = $rest.Rechten; Groepen = $rest.Groepen
        NietGevonden = @($weg)
    }
}

function Invoke-MetHerhaling {
    # Net aangemaakte objecten zijn soms enkele seconden niet vindbaar; dan opnieuw proberen
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
            # Een net aangemaakte groep of postvak is soms nog niet vindbaar
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
            if ($AgendaRechten -notcontains $P.AccessRights) { throw "Onbekend agendarecht '$($P.AccessRights)'." }
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
            Set-DistributionGroup @instellen -BypassSecurityGroupManagerCheck -ErrorAction Stop
            return $null
        }
        'Beheerder' {
            $wijziging = @{}
            if ($P.Toevoegen) { $wijziging['Add'] = $P.Toevoegen }
            if ($P.Verwijderen) { $wijziging['Remove'] = $P.Verwijderen }
            if ($wijziging.Count -eq 0) { throw 'Geen beheerder opgegeven.' }
            Set-DistributionGroup -Identity $P.Identity -ManagedBy $wijziging -BypassSecurityGroupManagerCheck -ErrorAction Stop
            return $null
        }
        'PostvakInstellen' {
            $instellen = @{ Identity = $P.Identity }
            foreach ($k in 'DisplayName', 'HiddenFromAddressListsEnabled', 'DeliverToMailboxAndForward') { if ($P.ContainsKey($k)) { $instellen[$k] = $P[$k] } }
            if ($P.ContainsKey('ForwardingSmtpAddress')) {
                # Leeg = doorsturen uitzetten
                $instellen['ForwardingSmtpAddress'] = $(if ($P.ForwardingSmtpAddress) { $P.ForwardingSmtpAddress } else { $null })
            }
            Set-Mailbox @instellen -ErrorAction Stop
            return $null
        }
        'AutoAntwoord' {
            if ($AutoAntwoordStanden -notcontains $P.AutoReplyState) { throw "Onbekende stand '$($P.AutoReplyState)' voor automatisch antwoord." }
            Set-MailboxAutoReplyConfiguration @P -ErrorAction Stop
            return $null
        }
        'RechtToevoegen' {
            if ($PostvakRechten -notcontains $P.Recht) { throw "Onbekend postvakrecht '$($P.Recht)'." }
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
            if ($PostvakRechten -notcontains $P.Recht) { throw "Onbekend postvakrecht '$($P.Recht)'." }
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
        'GroepAanmaken' {
            # Security (mail-beveiligingsgroep, standaard) of Distribution (distributielijst)
            $type = 'Security'
            if ($P.ContainsKey('Type')) { $type = "$($P.Type)"; $P.Remove('Type') }
            if ($type -notin 'Security', 'Distribution') { throw "Onbekend type groep '$type'." }
            New-DistributionGroup @P -Type $type -MemberJoinRestriction Closed -MemberDepartRestriction Closed -ErrorAction Stop | Out-Null
            return $null
        }
    }
}

function New-ExoAanmelding {
    # Parameters voor Connect-ExchangeOnline. Dit proces heeft geen venster; de Windows-aanmeldbroker (WAM, standaard
    # sinds ExchangeOnlineManagement 3.7) eist er een ("A window handle must be configured"). Met -DisableWAM meldt de
    # module aan via de browser. Oudere versies kennen die parameter niet en gebruiken WAM ook niet.
    # Met een token van de app (zelfde aanmelding als Graph) is er geen tweede aanmelding nodig.
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
        $verbindingsFout = "Aanmelden bij Exchange Online mislukt: $($_.Exception.Message)"
    }
}

$resultaten = foreach ($o in $lijst) {
    if ($verbindingsFout) {
        [pscustomobject]@{ id = $o.id; status = 'Mislukt'; melding = $verbindingsFout; data = $null }
        continue
    }
    $parameters = $null
    try {
        if (-not $Toegestaan.ContainsKey([string]$o.op)) { throw "Opdracht '$($o.op)' is niet toegestaan." }
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
