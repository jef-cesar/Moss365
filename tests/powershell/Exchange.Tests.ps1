#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Pester tests for Exchange.ps1 (the script the app starts in a separate PowerShell process).
# The Exchange cmdlets are faked; the script runs with -ZonderVerbinding.

BeforeAll {
    $script:Script = Join-Path $PSScriptRoot '..\..\src\Moss365.Connections\Exchange\Exchange.ps1'
    $global:ExoAanroepen = [System.Collections.Generic.List[string]]::new()

    function global:Add-DistributionGroupMember { param($Identity, $Member, [switch]$BypassSecurityGroupManagerCheck, $ErrorAction)
        if ($Member -eq 'al@test.be') { throw "The recipient al@test.be is already a member of the group $Identity." }
        $global:ExoAanroepen.Add("Add $Identity $Member") }
    function global:Remove-DistributionGroupMember { param($Identity, $Member, [switch]$BypassSecurityGroupManagerCheck, $Confirm, $ErrorAction)
        $global:ExoAanroepen.Add("Remove $Identity $Member") }
    function global:Set-CalendarProcessing { param($Identity, $AutomateProcessing, $BookingWindowInDays, $AllowConflicts, $DeleteSubject, $ErrorAction)
        $global:ExoAanroepen.Add("Cal $Identity $($BookingWindowInDays.GetType().Name)=$BookingWindowInDays $($DeleteSubject.GetType().Name)=$DeleteSubject") }
    function global:Set-Place { param($Identity, $Floor, $Capacity, $Building, $ErrorAction) $global:ExoAanroepen.Add("Place $Identity $Floor $Capacity") }
    function global:Set-MailboxFolderPermission { param($Identity, $User, $AccessRights, $ErrorAction)
        if ($Identity -like '*:\Calendar') { throw 'map niet gevonden' }
        $global:ExoAanroepen.Add("Perm $Identity $User $AccessRights") }
    function global:Set-Mailbox { param($Identity, $Type, $DisplayName, $HiddenFromAddressListsEnabled, $ForwardingSmtpAddress, $DeliverToMailboxAndForward, $ErrorAction)
        if ($Type) { $global:ExoAanroepen.Add("Mbx $Identity $Type"); return }
        $fwd = if ($PSBoundParameters.ContainsKey('ForwardingSmtpAddress')) { if ($null -eq $ForwardingSmtpAddress) { 'NULL' } else { $ForwardingSmtpAddress } } else { '-' }
        $global:ExoAanroepen.Add("SetMbx $Identity Fwd=$fwd Hidden=$HiddenFromAddressListsEnabled") }
    function global:Get-EXOMailbox { param($Identity, $RecipientTypeDetails, $ResultSize, $Properties)
        if (@($RecipientTypeDetails) -contains 'RoomMailbox' -or $Identity -eq 'rm-a@test.be') {
            return [pscustomobject]@{ DisplayName = 'Zaal A'; Alias = 'rm-a'; PrimarySmtpAddress = 'rm-a@test.be'; HiddenFromAddressListsEnabled = $false }
        }
        [pscustomobject]@{ RecipientTypeDetails = 'SharedMailbox'; DisplayName = 'Info'; Alias = 'info'; PrimarySmtpAddress = 'info@test.be'; HiddenFromAddressListsEnabled = $true
            IsDirSynced = $false; ForwardingSmtpAddress = 'smtp:x@fabrikam.com'; ForwardingAddress = $null; DeliverToMailboxAndForward = $true } }
    function global:Get-MailboxAutoReplyConfiguration { param($Identity, $ErrorAction) [pscustomobject]@{ AutoReplyState = 'Enabled'; InternalMessage = 'Afwezig'; ExternalMessage = '' } }
    function global:Get-EXOMailboxPermission { param($Identity, $ErrorAction)
        @([pscustomobject]@{ User = 'sg@test.be'; AccessRights = @('FullAccess'); IsInherited = $false }, [pscustomobject]@{ User = 'NT AUTHORITY\SELF'; AccessRights = @('FullAccess'); IsInherited = $false }
            [pscustomobject]@{ User = 'Interne Naam2'; AccessRights = @('FullAccess'); IsInherited = $false }) }
    function global:Get-Recipient { param($Identity, $ErrorAction)
        $type = @{ 'Interne Naam2' = 'UserMailbox'; 'rm-a@test.be' = 'RoomMailbox'; 'info@test.be' = 'SharedMailbox'; 'sg@test.be' = 'MailUniversalSecurityGroup' }
        if ($Identity -eq 'Interne Naam2') { return [pscustomobject]@{ PrimarySmtpAddress = 'intern@test.be'; RecipientTypeDetails = 'UserMailbox' } }
        if ($type.ContainsKey($Identity)) { return [pscustomobject]@{ PrimarySmtpAddress = $Identity; RecipientTypeDetails = $type[$Identity] } }
        throw "couldn't be found" }
    function global:Get-EXORecipientPermission { param($Identity, $ErrorAction) @([pscustomobject]@{ Trustee = 'bo@test.be'; AccessRights = @('SendAs') }) }
    function global:Get-DistributionGroup { param($Identity, $ResultSize)
        [pscustomobject]@{ Identity = 'sg'; RecipientTypeDetails = 'MailUniversalSecurityGroup'; DisplayName = 'SG'; Alias = 'sg'; PrimarySmtpAddress = 'sg@test.be'; IsDirSynced = $false; MemberJoinRestriction = 'Closed'; ManagedBy = @('an', 'bo') } }
    function global:Get-DistributionGroupMember { param($Identity, $ResultSize, $ErrorAction) [pscustomobject]@{ PrimarySmtpAddress = 'an@test.be'; DisplayName = 'An' } }
    function global:Set-DistributionGroup { param($Identity, $DisplayName, $PrimarySmtpAddress, $ManagedBy, [switch]$BypassSecurityGroupManagerCheck, $ErrorAction)
        if ($ManagedBy) { $global:ExoAanroepen.Add("Beheer $Identity " + (($ManagedBy.Keys | Sort-Object | ForEach-Object { "$_=$($ManagedBy[$_])" }) -join ' ')); return }
        $global:ExoAanroepen.Add("SetDg $Identity $DisplayName $PrimarySmtpAddress") }
    function global:Set-MailboxAutoReplyConfiguration { param($Identity, $AutoReplyState, $InternalMessage, $ExternalMessage, $ErrorAction)
        $global:ExoAanroepen.Add("Auto $Identity $AutoReplyState $InternalMessage") }
    function global:Add-MailboxPermission { param($Identity, $User, $AccessRights, $InheritanceType, [switch]$AutoMapping, $ErrorAction)
        if ($Identity -eq 'nieuw@test.be' -and -not $global:NieuwBestaat) { $global:NieuwBestaat = $true; throw "The operation couldn't be performed because object 'nieuw@test.be' couldn't be found." }
        $global:ExoAanroepen.Add("AddFull $Identity $User AutoMapping=$($AutoMapping.IsPresent)") }
    function global:Add-RecipientPermission { param($Identity, $Trustee, $AccessRights, $Confirm, $ErrorAction) $global:ExoAanroepen.Add("AddSendAs $Identity $Trustee") }
    function global:Remove-MailboxPermission { param($Identity, $User, $AccessRights, $InheritanceType, $Confirm, $ErrorAction) $global:ExoAanroepen.Add("DelFull $Identity $User") }
    function global:Remove-RecipientPermission { param($Identity, $Trustee, $AccessRights, $Confirm, $ErrorAction)
        if ($Trustee -eq 'weg@test.be') { throw "The ACE doesn't exist on the object." }
        $global:ExoAanroepen.Add("DelSendAs $Identity $Trustee") }
    function global:New-Mailbox { param([switch]$Shared, $Name, $DisplayName, $Alias, $PrimarySmtpAddress, $ErrorAction) $global:ExoAanroepen.Add("NewMbx Shared=$($Shared.IsPresent) $PrimarySmtpAddress $Alias") }
    function global:New-DistributionGroup { param($Name, $DisplayName, $Alias, $PrimarySmtpAddress, $Type, $MemberJoinRestriction, $MemberDepartRestriction, $ErrorAction)
        $global:ExoAanroepen.Add("NewDg $Type $Name $PrimarySmtpAddress $MemberJoinRestriction") }
    function global:Get-CalendarProcessing { param($Identity, $ErrorAction)
        [pscustomobject]@{ AutomateProcessing = 'AutoAccept'; BookingWindowInDays = 600; MaximumDurationInMinutes = 1440; AllowConflicts = $false
            AllowRecurringMeetings = $true; AddOrganizerToSubject = $true; DeleteSubject = $true; AllBookInPolicy = $true; AllRequestInPolicy = $false
            BookInPolicy = @(); ResourceDelegates = @('a@test.be', 'b@test.be') } }
    function global:Get-Place { param($Identity, $ErrorAction) [pscustomobject]@{ Building = 'Gemeentehuis'; Floor = 2; FloorLabel = '2'; Capacity = 8 } }
    function global:Get-MailboxFolderPermission { param($Identity, $ErrorAction)
        @([pscustomobject]@{ User = 'Default'; AccessRights = @('Reviewer') }, [pscustomobject]@{ User = 'Anonymous'; AccessRights = @('None') }) }

    function script:Voer {
        param([object[]]$Opdrachten)
        $global:ExoAanroepen.Clear()
        $in = Join-Path $TestDrive ('in-' + [guid]::NewGuid() + '.json')
        $uit = Join-Path $TestDrive ('uit-' + [guid]::NewGuid() + '.json')
        ConvertTo-Json -InputObject @($Opdrachten) -Depth 5 | Set-Content -LiteralPath $in -Encoding UTF8
        & $script:Script -Opdrachten $in -Uitvoer $uit -ZonderVerbinding -WachtSeconden 0
        return @(Get-Content -LiteralPath $uit -Raw -Encoding UTF8 | ConvertFrom-Json)
    }
}

AfterAll {
    Remove-Variable -Name ExoAanroepen -Scope Global -ErrorAction SilentlyContinue
    foreach ($f in 'Add-DistributionGroupMember', 'Remove-DistributionGroupMember', 'Set-CalendarProcessing', 'Set-Place', 'Set-MailboxFolderPermission',
        'Set-Mailbox', 'Get-EXOMailbox', 'Get-CalendarProcessing', 'Get-Place', 'Get-MailboxFolderPermission', 'Get-MailboxAutoReplyConfiguration',
        'Get-EXOMailboxPermission', 'Get-EXORecipientPermission', 'Get-DistributionGroup', 'Get-DistributionGroupMember', 'Set-DistributionGroup',
        'Set-MailboxAutoReplyConfiguration', 'Add-MailboxPermission', 'Add-RecipientPermission', 'Remove-MailboxPermission', 'Remove-RecipientPermission',
        'New-Mailbox', 'New-DistributionGroup', 'Get-Recipient') {
        Remove-Item -Path "function:global:$f" -ErrorAction SilentlyContinue
    }
}

Describe 'Exchange.ps1' {
    It 'adds a member and treats "already a member" as succeeded' {
        $r = Voer @(
            @{ id = '1'; op = 'LidToevoegen'; params = @{ Identity = 'sg@test.be'; Member = 'eva@test.be' } }
            @{ id = '2'; op = 'LidToevoegen'; params = @{ Identity = 'sg@test.be'; Member = 'al@test.be' } }
        )
        $r.Count | Should -Be 2
        $r[0].status | Should -Be 'Gelukt'
        $r[1].status | Should -Be 'Gelukt'
        $r[1].melding | Should -Be 'Was al lid.'
        $global:ExoAanroepen | Should -Contain 'Add sg@test.be eva@test.be'
    }

    It 'converts numbers and yes/no values to the right type' {
        $r = Voer @(@{ id = 'c'; op = 'Agendaverwerking'; params = @{ Identity = 'rm@test.be'; BookingWindowInDays = '180'; DeleteSubject = 'False' } })
        $r[0].status | Should -Be 'Gelukt'
        $global:ExoAanroepen[0] | Should -Be 'Cal rm@test.be Int32=180 Boolean=False'
    }

    It 'refuses a command or parameter that is not allowed' {
        $r = Voer @(
            @{ id = 'x'; op = 'Remove-Mailbox'; params = @{ Identity = 'iemand@test.be' } }
            @{ id = 'y'; op = 'LidToevoegen'; params = @{ Identity = 'sg@test.be'; Member = 'eva@test.be'; Confirm = 'False' } }
            @{ id = 'z'; op = 'Agendaverwerking'; params = @{ Identity = 'rm@test.be'; BookingWindowInDays = '1; Remove-Mailbox x' } }
        )
        $r | ForEach-Object { $_.status | Should -Be 'Mislukt' }
        $r[0].melding | Should -Match 'not allowed'
        $r[1].melding | Should -Match 'not allowed'
        $r[2].melding | Should -Match 'not a whole number'
        $global:ExoAanroepen.Count | Should -Be 0
    }

    It 'sets the calendar permission on the Dutch folder when Calendar does not exist, and refuses unknown permissions' {
        $r = Voer @(
            @{ id = 'p'; op = 'AgendaRecht'; params = @{ Identity = 'rm@test.be'; User = 'Default'; AccessRights = 'Reviewer' } }
            @{ id = 'q'; op = 'AgendaRecht'; params = @{ Identity = 'rm@test.be'; User = 'Default'; AccessRights = 'Alles' } }
        )
        $r[0].status | Should -Be 'Gelukt'
        $global:ExoAanroepen | Should -Contain 'Perm rm@test.be:\Agenda Default Reviewer'
        $r[1].status | Should -Be 'Mislukt'
    }

    It 'reads the rooms in the format of the app' {
        $r = Voer @(@{ id = 'rooms'; op = 'LeesRooms'; params = @{} })
        $r[0].status | Should -Be 'Gelukt'
        $room = @($r[0].data)[0]
        $room.Smtp | Should -Be 'rm-a@test.be'
        $room.BookingWindowInDays | Should -Be '600'
        $room.Capaciteit | Should -Be '8'
        $room.ResourceDelegates | Should -Be 'a@test.be|b@test.be'
        $room.AgendaRechten | Should -Be 'Default=Reviewer|Anonymous=None'
    }

    It 'converts a mailbox to shared and removes a member' {
        $r = Voer @(
            @{ id = 'm'; op = 'NaarGedeeld'; params = @{ Identity = 'eva@test.be' } }
            @{ id = 'n'; op = 'LidVerwijderen'; params = @{ Identity = 'sg@test.be'; Member = 'eva@test.be' } }
        )
        ($r | Where-Object status -eq 'Gelukt').Count | Should -Be 2
        $global:ExoAanroepen | Should -Contain 'Mbx eva@test.be Shared'
        $global:ExoAanroepen | Should -Contain 'Remove sg@test.be eva@test.be'
    }

    It 'manages permissions on a mailbox, also right after creating it' {
        $global:NieuwBestaat = $false
        $r = Voer @(
            @{ id = 'a'; op = 'PostvakAanmaken'; params = @{ Name = 'Nieuw'; DisplayName = 'Nieuw'; Alias = 'nieuw'; PrimarySmtpAddress = 'nieuw@test.be' } }
            @{ id = 'b'; op = 'GroepAanmaken'; params = @{ Name = 'SG-SM-nieuw-Full'; DisplayName = 'SG-SM-nieuw-Full'; Alias = 'SG-SM-nieuw-Full'; PrimarySmtpAddress = 'sg-sm-nieuw-full@test.be' } }
            @{ id = 'c'; op = 'RechtToevoegen'; params = @{ Identity = 'nieuw@test.be'; Recht = 'FullAccess'; Trustee = 'sg-sm-nieuw-full@test.be' } }
            @{ id = 'd'; op = 'RechtToevoegen'; params = @{ Identity = 'nieuw@test.be'; Recht = 'SendAs'; Trustee = 'sg-sm-nieuw-full@test.be' } }
            @{ id = 'e'; op = 'RechtVerwijderen'; params = @{ Identity = 'info@test.be'; Recht = 'SendAs'; Trustee = 'bo@test.be' } }
            @{ id = 'f'; op = 'RechtVerwijderen'; params = @{ Identity = 'info@test.be'; Recht = 'SendAs'; Trustee = 'weg@test.be' } }
            @{ id = 'g'; op = 'RechtToevoegen'; params = @{ Identity = 'info@test.be'; Recht = 'Owner'; Trustee = 'x@test.be' } }
        )
        ($r | Where-Object status -eq 'Gelukt').Count | Should -Be 6
        ($r | Where-Object id -eq 'f').melding | Should -Match 'al niet meer'
        ($r | Where-Object id -eq 'g').melding | Should -Match 'Unknown mailbox permission'
        $global:ExoAanroepen | Should -Contain 'NewMbx Shared=True nieuw@test.be nieuw'
        $global:ExoAanroepen | Should -Contain 'NewDg Security SG-SM-nieuw-Full sg-sm-nieuw-full@test.be Closed'
        $global:ExoAanroepen | Should -Contain 'AddFull nieuw@test.be sg-sm-nieuw-full@test.be AutoMapping=False'
        $global:ExoAanroepen | Should -Contain 'AddSendAs nieuw@test.be sg-sm-nieuw-full@test.be'
        $global:ExoAanroepen | Should -Contain 'DelSendAs info@test.be bo@test.be'
    }

    It 'creates a distribution list, and refuses an unknown group type (0.8)' {
        $r = Voer @(
            @{ id = 'a'; op = 'GroepAanmaken'; params = @{ Name = 'Feest'; DisplayName = 'Feest'; Alias = 'feest'; PrimarySmtpAddress = 'feest@test.be'; Type = 'Distribution' } }
            @{ id = 'b'; op = 'GroepAanmaken'; params = @{ Name = 'X'; DisplayName = 'X'; Alias = 'x'; PrimarySmtpAddress = 'x@test.be'; Type = 'Dynamic' } }
        )
        ($r | Where-Object id -eq 'a').status | Should -Be 'Gelukt'
        ($r | Where-Object id -eq 'b').melding | Should -Match 'Unknown group type'
        $global:ExoAanroepen | Should -Contain 'NewDg Distribution Feest feest@test.be Closed'
    }

    It 'configures a mailbox and a group' {
        $r = Voer @(
            @{ id = 'a'; op = 'PostvakInstellen'; params = @{ Identity = 'info@test.be'; ForwardingSmtpAddress = ''; HiddenFromAddressListsEnabled = 'True' } }
            @{ id = 'b'; op = 'AutoAntwoord'; params = @{ Identity = 'info@test.be'; AutoReplyState = 'Enabled'; InternalMessage = 'Afwezig' } }
            @{ id = 'c'; op = 'AutoAntwoord'; params = @{ Identity = 'info@test.be'; AutoReplyState = 'Misschien' } }
            @{ id = 'd'; op = 'GroepInstellen'; params = @{ Identity = 'sg@test.be'; DisplayName = 'SG nieuw' } }
            @{ id = 'e'; op = 'Beheerder'; params = @{ Identity = 'sg@test.be'; Toevoegen = 'an@test.be' } }
        )
        ($r | Where-Object status -eq 'Gelukt').Count | Should -Be 4
        $global:ExoAanroepen | Should -Contain 'SetMbx info@test.be Fwd=NULL Hidden=True'
        $global:ExoAanroepen | Should -Contain 'Auto info@test.be Enabled Afwezig'
        $global:ExoAanroepen | Should -Contain 'SetDg sg@test.be SG nieuw '
        $global:ExoAanroepen | Should -Contain 'Beheer sg@test.be Add=an@test.be'
    }

    It 'reads shared mailboxes with permissions and groups' {
        $r = Voer @(@{ id = 'p'; op = 'LeesPostvakken'; params = @{} })
        $r[0].status | Should -Be 'Gelukt'
        $d = $r[0].data
        $d.Postvakken[0].Smtp | Should -Be 'info@test.be'
        $d.Postvakken[0].DoorsturenNaar | Should -Be 'x@fabrikam.com'
        $d.Postvakken[0].AutoAntwoord | Should -Be 'Enabled'
        @($d.Rechten).Count | Should -Be 3
        @($d.Rechten).Trustee | Should -Contain 'intern@test.be' 
        @($d.Rechten | Where-Object Trustee -eq 'bo@test.be').Recht | Should -Be 'SendAs'
        $d.Groepen[0].ManagedBy | Should -Be 'an|bo'
        $d.Groepen[0].Leden[0].Smtp | Should -Be 'an@test.be'
    }

    It 'reads a few objects again according to their kind, and reports what no longer exists' {
        $r = Voer @(@{ id = 'o'; op = 'LeesObjecten'; params = @{ Adressen = 'rm-a@test.be|info@test.be|sg@test.be|weg@test.be' } })
        $r[0].status | Should -Be 'Gelukt'
        $d = $r[0].data
        @($d.Rooms).Count | Should -Be 1
        $d.Rooms[0].Smtp | Should -Be 'rm-a@test.be'
        @($d.Postvakken).Count | Should -Be 1
        $d.Postvakken[0].Smtp | Should -Be 'info@test.be'
        @($d.Rechten).Count | Should -Be 3
        @($d.Groepen).Count | Should -Be 1
        $d.Groepen[0].Smtp | Should -Be 'sg@test.be'
        @($d.NietGevonden) | Should -Be @('weg@test.be')
    }

    It 'returns empty lists without addresses' {
        $r = Voer @(@{ id = 'o'; op = 'LeesObjecten'; params = @{ Adressen = '' } })
        $r[0].status | Should -Be 'Gelukt'
        @($r[0].data.Rooms).Count | Should -Be 0
        @($r[0].data.Postvakken).Count | Should -Be 0
        @($r[0].data.Groepen).Count | Should -Be 0
    }

    Context 'Aanmelden' {
        BeforeAll {
            # Load only the function, without running the script
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:Script, [ref]$null, [ref]$null)
            $f = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-ExoAanmelding' }, $true)
            . ([scriptblock]::Create($f.Extent.Text))
        }

        It 'turns WAM off when the module knows it (process without a window)' {
            $a = New-ExoAanmelding -Upn 'beheer@test.be' -BeschikbareParameters @('UserPrincipalName', 'ShowBanner', 'DisableWAM')
            $a['DisableWAM'] | Should -BeTrue
            $a['UserPrincipalName'] | Should -Be 'beheer@test.be'
            $a['ShowBanner'] | Should -BeFalse
        }

        It 'uses the token of the app when there is one' {
            $a = New-ExoAanmelding -Upn 'beheer@test.be' -Token 'abc' -BeschikbareParameters @('UserPrincipalName', 'ShowBanner', 'DisableWAM', 'AccessToken')
            $a['AccessToken'] | Should -Be 'abc'
            $a.ContainsKey('DisableWAM') | Should -BeFalse
        }

        It 'leaves out DisableWAM for an older module' {
            $a = New-ExoAanmelding -Upn '' -BeschikbareParameters @('UserPrincipalName', 'ShowBanner')
            $a.ContainsKey('DisableWAM') | Should -BeFalse
            $a.ContainsKey('UserPrincipalName') | Should -BeFalse
        }
    }
}

Describe 'Register-App.ps1' {
    BeforeAll {
        . (Join-Path $PSScriptRoot '..\..\tools\Register-App.ps1')
    }

    It 'finds the id of every requested permission' {
        $sp = [pscustomobject]@{ Oauth2PermissionScopes = @(
                [pscustomobject]@{ Value = 'User.ReadWrite.All'; Id = 'id-1' }
                [pscustomobject]@{ Value = 'Group.ReadWrite.All'; Id = 'id-2' }) }
        $r = @(Get-ScopeIds -GraphSp $sp -Namen 'User.ReadWrite.All', 'Group.ReadWrite.All')
        $r.Count | Should -Be 2
        $r[0].Id | Should -Be 'id-1'
        $r[0].Type | Should -Be 'Scope'
    }

    It 'asks for the permissions up to and including 0.9 at once, and nothing beyond' {
        @($Rechten) | Sort-Object | Should -Be (@(
            'Application.Read.All', 'AuditLog.Read.All', 'Calendars.Read.Shared', 'Directory.Read.All', 'Group.ReadWrite.All',
            'GroupMember.ReadWrite.All', 'OnPremDirectorySynchronization.Read.All', 'Policy.Read.All',
            'Policy.ReadWrite.ConditionalAccess', 'User.ReadWrite.All') | Sort-Object)
        # No permissions that can do more than needed (e.g. read all mail, or write the whole directory)
        @($Rechten | Where-Object { $_ -in 'Mail.Read', 'Mail.ReadWrite', 'Directory.ReadWrite.All', 'RoleManagement.ReadWrite.Directory' }).Count | Should -Be 0
    }

    It 'stops at an unknown permission' {
        { Get-ScopeIds -GraphSp ([pscustomobject]@{ Oauth2PermissionScopes = @() }) -Namen 'Bestaat.Niet' } | Should -Throw '*Bestaat.Niet*'
    }

    It 'creates the five profiles with the same id as the app (0.8)' {
        $code = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\src\Moss365.Core\Permissions\Permissions.cs') -Raw
        @($Profielen).Count | Should -Be 5
        foreach ($p in $Profielen) {
            $code | Should -Match ([regex]::Escape("(""$($p.Waarde)"", Role.$($p.Naam), ""$($p.Groep)"", ""$($p.Id)"")"))
        }
    }

    It 'keeps other app roles and updates the profiles without duplicating them' {
        $bestaand = @(
            [pscustomobject]@{ Id = 'aaaaaaaa-0000-0000-0000-000000000001'; Value = 'Ander.Recht'; DisplayName = 'Ander'; Description = 'x'; IsEnabled = $true; AllowedMemberTypes = @('User') }
            [pscustomobject]@{ Id = $Profielen[0].Id; Value = 'Moss365.Lezer'; DisplayName = 'oud'; Description = 'oud'; IsEnabled = $true; AllowedMemberTypes = @('User') }
        )
        $r = @(Get-AppRollen -Bestaand $bestaand)
        $r.Count | Should -Be 6
        @($r | Where-Object { $_.Id -eq $Profielen[0].Id }).Count | Should -Be 1
        ($r | Where-Object { $_.Id -eq $Profielen[0].Id }).DisplayName | Should -Be 'Moss365 Reader'
        @($r | Where-Object { $_.Value -eq 'Ander.Recht' }).Count | Should -Be 1
    }

    It 'is ASCII only' {
        $pad = Join-Path $PSScriptRoot '..\..\tools\Register-App.ps1'
        @([System.IO.File]::ReadAllBytes($pad) | Where-Object { $_ -gt 127 }).Count | Should -Be 0
    }
}

Describe 'Check.ps1' {
    BeforeAll {
        $script:Controle = Join-Path $PSScriptRoot '..\..\src\Moss365.Connections\Check\Check.ps1'
    }

    It 'is ASCII only (Windows PowerShell 5.1)' {
        foreach ($pad in $script:Controle, (Join-Path $PSScriptRoot '..\..\src\Moss365.Connections\Exchange\Exchange.ps1'),
            (Join-Path $PSScriptRoot '..\..\src\Moss365.Connections\Scripts\Startup.ps1')) {
            @([System.IO.File]::ReadAllBytes($pad) | Where-Object { $_ -gt 127 }).Count | Should -Be 0
        }
    }

    It 'reports the PowerShell version and the modules' {
        $uit = Join-Path $TestDrive 'controle.json'
        & $script:Controle -Uitvoer $uit
        $r = Get-Content -LiteralPath $uit -Raw -Encoding UTF8 | ConvertFrom-Json
        $r.PowerShell | Should -Be "$($PSVersionTable.PSVersion)"
        @($r.Modules).Naam | Should -Contain 'ExchangeOnlineManagement'
        @($r.Modules).Naam | Should -Contain 'ActiveDirectory'
        $r.Installatie | Should -BeNullOrEmpty
    }

    It 'installs only allowed modules' {
        $uit = Join-Path $TestDrive 'installeer.json'
        & $script:Controle -Uitvoer $uit -Installeer 'Iets.Anders'
        (Get-Content -LiteralPath $uit -Raw -Encoding UTF8 | ConvertFrom-Json).Installatie | Should -Match 'not allowed'
    }
}
