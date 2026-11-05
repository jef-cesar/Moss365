<#
.SYNOPSIS
    Creates the app registration "Moss365" for the management app in Entra ID (once, as Global Administrator).

.DESCRIPTION
    The management app calls Microsoft Graph with the sign-in of the administrator (delegated permissions). That needs
    an app registration. This script:
      1. creates a public client app (no secret, no certificate), for this tenant only, with the redirect URI
         http://localhost (the app shows the sign-in link; paste it into a browser on this PC);
      2. asks only for these delegated permissions:
           Microsoft Graph: User.ReadWrite.All, Group.ReadWrite.All, GroupMember.ReadWrite.All, Directory.Read.All,
                            AuditLog.Read.All, Policy.Read.All (read Conditional Access, 0.6),
                            Calendars.Read.Shared (calendar export, 0.7), OnPremDirectorySynchronization.Read.All
                            (diagnosis of password expiry, 0.7), Policy.ReadWrite.ConditionalAccess and
                            Application.Read.All (manage Conditional Access, 0.9)
           Since 0.6.1 the script asks for all permissions up to and including 0.9 at once, so that you do not have
           to run it again for every version. The app uses a permission only in the version that needs it.
           Exchange Online: Exchange.Manage (so that Exchange uses the same sign-in as Graph)
         Delegated means: the app can never do more than the signed-in administrator;
      3. grants admin consent for those permissions;
      4. turns on "assignment required" and assigns only the given administrators: other users cannot sign in to
         the app, even if they try;
      5. (since 0.8) creates the five profiles as app roles: Moss365.Reader, Moss365.Helpdesk, Moss365.Administrator,
         Moss365.Auditor and Moss365.AppAdmin. The given administrators get Administrator and AppAdmin. With
         -GroepenMaken the script also creates the groups GG-Moss365-Readers, -Helpdesk, -Administrators, -Auditors and
         -AppAdmins (cloud, security) and links every group to its role; whoever you put in a group gets that profile.

    At the end the script shows the Tenant ID and the Client ID; fill them in in the app (Connections screen).
    If the app already exists, the script updates the permissions and assignments instead of creating a second one.
    So feel free to run it again after a new version of the app (version 0.3 adds Exchange.Manage, version 0.6
    Policy.Read.All, version 0.6.1 the permissions up to and including 0.9, version 0.8 the profiles).

.PARAMETER GroepenMaken
    Creates the groups GG-Moss365-... (when they do not exist yet) and assigns them to the roles. Assigning to groups
    needs Entra ID P1.

.PARAMETER Beheerders
    UPNs of the administrators who may use the app. Default: whoever runs this script.

.PARAMETER Naam
    Name of the app registration. Default "Moss365".

.EXAMPLE
    .\Register-App.ps1

.EXAMPLE
    .\Register-App.ps1 -Beheerders name@contoso.com, colleague@contoso.com

.EXAMPLE
    .\Register-App.ps1 -GroepenMaken
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string[]]$Beheerders,
    [string]$Naam = 'Moss365',
    [switch]$GroepenMaken
)

$ErrorActionPreference = 'Stop'

$GraphAppId = '00000003-0000-0000-c000-000000000000'
$ExchangeAppId = '00000002-0000-0ff1-ce00-000000000000'
$Rechten = @(
    'User.ReadWrite.All', 'Group.ReadWrite.All', 'GroupMember.ReadWrite.All', 'Directory.Read.All', 'AuditLog.Read.All',
    'Policy.Read.All',                                         # 0.6: read Conditional Access
    'Calendars.Read.Shared',                                   # 0.7: calendar export (only calendars you have permissions on yourself)
    'OnPremDirectorySynchronization.Read.All',                 # 0.7: script diagnose wachtwoordverloop
    'Policy.ReadWrite.ConditionalAccess', 'Application.Read.All'  # 0.9: manage Conditional Access
)
$ExchangeRechten = @('Exchange.Manage')

# The profiles (0.8). The ids are fixed: the same as in the app (Core/Permissions/Permissions.cs), so that running again duplicates nothing.
$Profielen = @(
    @{ Waarde = 'Moss365.Reader';    Naam = 'Reader';    Groep = 'GG-Moss365-Readers';    Id = '6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8001'; Uitleg = 'Search and view, statistics; changes nothing.' }
    @{ Waarde = 'Moss365.Helpdesk';  Naam = 'Helpdesk';  Groep = 'GG-Moss365-Helpdesk';   Id = '6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8002'; Uitleg = 'Passwords, unlocking, members of normal groups, inviting guests, exporting.' }
    @{ Waarde = 'Moss365.Administrator'; Naam = 'Administrator'; Groep = 'GG-Moss365-Administrators'; Id = '6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8003'; Uitleg = 'All changes, also privileged accounts and groups; scripts.' }
    @{ Waarde = 'Moss365.Auditor';   Naam = 'Auditor';   Groep = 'GG-Moss365-Auditors';   Id = '6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8004'; Uitleg = 'View, security, sign-ins and the full change log; changes nothing.' }
    @{ Waarde = 'Moss365.AppAdmin';  Naam = 'AppAdmin';  Groep = 'GG-Moss365-AppAdmins';  Id = '6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8005'; Uitleg = 'The settings of Moss365 (organisation, choice lists).' }
)

function Get-AppRollen {
    # The app roles of the app: the existing (other) roles stay, the profiles are added or updated
    param($Bestaand)
    $eigen = @($Profielen | ForEach-Object { $_.Id })
    $rollen = @($Bestaand | Where-Object { $eigen -notcontains "$($_.Id)" } | ForEach-Object {
        @{ Id = "$($_.Id)"; Value = $_.Value; DisplayName = $_.DisplayName; Description = $_.Description; IsEnabled = $_.IsEnabled; AllowedMemberTypes = @($_.AllowedMemberTypes) }
    })
    foreach ($p in $Profielen) {
        $rollen += @{ Id = $p.Id; Value = $p.Waarde; DisplayName = "Moss365 $($p.Naam)"; Description = $p.Uitleg; IsEnabled = $true; AllowedMemberTypes = @('User') }
    }
    $rollen
}

function Get-ScopeIds {
    # Finds the ids of the requested delegated permissions on a service principal (Microsoft Graph or Exchange Online)
    param($GraphSp, [string[]]$Namen)
    foreach ($naam in $Namen) {
        $scope = @($GraphSp.Oauth2PermissionScopes | Where-Object { $_.Value -eq $naam }) | Select-Object -First 1
        if (-not $scope) { throw "Permission $naam not found on $($GraphSp.DisplayName)." }
        @{ Id = $scope.Id; Type = 'Scope' }
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }

Import-Module Microsoft.Graph.Authentication, Microsoft.Graph.Applications, Microsoft.Graph.Identity.SignIns, Microsoft.Graph.Users -ErrorAction Stop
if ($GroepenMaken) { Import-Module Microsoft.Graph.Groups -ErrorAction Stop }
$scopes = @('Application.ReadWrite.All', 'DelegatedPermissionGrant.ReadWrite.All', 'AppRoleAssignment.ReadWrite.All', 'User.Read.All')
if ($GroepenMaken) { $scopes += 'Group.ReadWrite.All' }
Connect-MgGraph -Scopes $scopes -NoWelcome
$context = Get-MgContext
if (-not $Beheerders -or $Beheerders.Count -eq 0) { $Beheerders = @($context.Account) }

$graphSp = Get-MgServicePrincipal -Filter "appId eq '$GraphAppId'"
$exchangeSp = Get-MgServicePrincipal -Filter "appId eq '$ExchangeAppId'"
$vereist = @(
    @{ ResourceAppId = $GraphAppId; ResourceAccess = @(Get-ScopeIds -GraphSp $graphSp -Namen $Rechten) }
    @{ ResourceAppId = $ExchangeAppId; ResourceAccess = @(Get-ScopeIds -GraphSp $exchangeSp -Namen $ExchangeRechten) }
)

$app = @(Get-MgApplication -Filter "displayName eq '$Naam'") | Select-Object -First 1
if ($app) {
    Write-Host "App '$Naam' already exists ($($app.AppId)); permissions and redirect are updated." -ForegroundColor Yellow
    if ($PSCmdlet.ShouldProcess($Naam, 'Update the app registration')) {
        Update-MgApplication -ApplicationId $app.Id -IsFallbackPublicClient -PublicClient @{ RedirectUris = @('http://localhost') } -RequiredResourceAccess $vereist `
            -AppRoles @(Get-AppRollen -Bestaand $app.AppRoles)
    }
}
elseif ($PSCmdlet.ShouldProcess($Naam, 'Create the app registration')) {
    $app = New-MgApplication -DisplayName $Naam -SignInAudience 'AzureADMyOrg' -IsFallbackPublicClient `
        -PublicClient @{ RedirectUris = @('http://localhost') } -RequiredResourceAccess $vereist -AppRoles @(Get-AppRollen -Bestaand @()) `
        -Notes 'Management app Moss365 (github.com/jef-cesar/Moss365). Delegated permissions; assigned administrators only.'
    Write-Host "App '$Naam' created." -ForegroundColor Green
}

$sp = @(Get-MgServicePrincipal -Filter "appId eq '$($app.AppId)'") | Select-Object -First 1
if (-not $sp -and $PSCmdlet.ShouldProcess($Naam, 'Create the service principal')) {
    $sp = New-MgServicePrincipal -AppId $app.AppId -AppRoleAssignmentRequired
}
elseif ($sp -and $PSCmdlet.ShouldProcess($Naam, 'Turn on assignment required')) {
    Update-MgServicePrincipal -ServicePrincipalId $sp.Id -AppRoleAssignmentRequired
}

# Admin consent for the delegated permissions, per resource (Graph and Exchange Online)
foreach ($bron in @(@{ Sp = $graphSp; Rechten = $Rechten }, @{ Sp = $exchangeSp; Rechten = $ExchangeRechten })) {
    $bestaand = @(Get-MgOauth2PermissionGrant -Filter "clientId eq '$($sp.Id)' and resourceId eq '$($bron.Sp.Id)'") | Select-Object -First 1
    $scopeTekst = $bron.Rechten -join ' '
    if ($bestaand) {
        if ($PSCmdlet.ShouldProcess($Naam, "Update consent ($($bron.Sp.DisplayName)): $scopeTekst")) {
            Update-MgOauth2PermissionGrant -OAuth2PermissionGrantId $bestaand.Id -Scope $scopeTekst
        }
    }
    elseif ($PSCmdlet.ShouldProcess($Naam, "Grant consent ($($bron.Sp.DisplayName)): $scopeTekst")) {
        New-MgOauth2PermissionGrant -ClientId $sp.Id -ConsentType 'AllPrincipals' -ResourceId $bron.Sp.Id -Scope $scopeTekst | Out-Null
    }
}

# Only the given administrators may sign in, with the profiles Administrator and AppAdmin
$toewijzingen = @(Get-MgServicePrincipalAppRoleAssignedTo -ServicePrincipalId $sp.Id -All)
$beheerRollen = @($Profielen | Where-Object { $_.Naam -in 'Administrator', 'AppAdmin' })
foreach ($upn in $Beheerders) {
    $gebruiker = Get-MgUser -UserId $upn
    foreach ($rol in $beheerRollen) {
        $al = @($toewijzingen | Where-Object { $_.PrincipalId -eq $gebruiker.Id -and "$($_.AppRoleId)" -eq $rol.Id })
        if ($al.Count -eq 0 -and $PSCmdlet.ShouldProcess($upn, "Assign profile $($rol.Naam)")) {
            New-MgServicePrincipalAppRoleAssignedTo -ServicePrincipalId $sp.Id -PrincipalId $gebruiker.Id -ResourceId $sp.Id -AppRoleId $rol.Id | Out-Null
            Write-Host "  $upn has the profile $($rol.Naam)."
        }
    }
}

# One group per profile; whoever you put in the group gets the profile (and may sign in)
if ($GroepenMaken) {
    foreach ($p in $Profielen) {
        $groep = @(Get-MgGroup -Filter "displayName eq '$($p.Groep)'") | Select-Object -First 1
        if (-not $groep -and $PSCmdlet.ShouldProcess($p.Groep, 'Create group')) {
            $groep = New-MgGroup -DisplayName $p.Groep -MailEnabled:$false -MailNickname $p.Groep -SecurityEnabled `
                -Description "Profile $($p.Naam) in Moss365. $($p.Uitleg)"
            Write-Host "  Group $($p.Groep) created."
        }
        if ($groep) {
            $al = @($toewijzingen | Where-Object { $_.PrincipalId -eq $groep.Id -and "$($_.AppRoleId)" -eq $p.Id })
            if ($al.Count -eq 0 -and $PSCmdlet.ShouldProcess($p.Groep, "Assign to the profile $($p.Naam)")) {
                New-MgServicePrincipalAppRoleAssignedTo -ServicePrincipalId $sp.Id -PrincipalId $groep.Id -ResourceId $sp.Id -AppRoleId $p.Id | Out-Null
                Write-Host "  $($p.Groep) -> profile $($p.Naam)."
            }
        }
    }
}

Write-Host ''
Write-Host 'Fill in these values in the app (Connections screen):' -ForegroundColor Cyan
Write-Host "  Tenant ID : $($context.TenantId)"
Write-Host "  Client ID : $($app.AppId)"
