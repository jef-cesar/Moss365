<#
.SYNOPSIS
    Maakt in Entra ID de app-registratie "Moss365" aan voor de beheer-app (eenmalig, als Global Administrator).

.DESCRIPTION
    De beheer-app spreekt Microsoft Graph aan met de aanmelding van de beheerder (gedelegeerde rechten). Daarvoor is een
    app-registratie nodig. Dit script:
      1. maakt een openbare client-app aan (geen geheim, geen certificaat), alleen voor deze tenant,
         met als omleidings-URI http://localhost (de app toont de aanmeldlink; plak hem in een browser op deze pc);
      2. vraagt enkel deze gedelegeerde rechten:
           Microsoft Graph: User.ReadWrite.All, Group.ReadWrite.All, GroupMember.ReadWrite.All, Directory.Read.All,
                            AuditLog.Read.All, Policy.Read.All (Conditional Access lezen, 0.6),
                            Calendars.Read.Shared (agenda-export, 0.7), OnPremDirectorySynchronization.Read.All
                            (diagnose wachtwoordverloop, 0.7), Policy.ReadWrite.ConditionalAccess en
                            Application.Read.All (Conditional Access beheren, 0.9)
           Sinds 0.6.1 vraagt het script alle rechten tot en met 0.9 in een keer, zodat je het niet bij elke versie
           opnieuw moet draaien. De app gebruikt een recht pas in de versie die het nodig heeft.
           Exchange Online: Exchange.Manage (zodat Exchange dezelfde aanmelding gebruikt als Graph)
         Gedelegeerd betekent: de app kan nooit meer dan de aangemelde beheerder zelf;
      3. geeft beheerderstoestemming voor die rechten;
      4. zet "toewijzing vereist" aan en wijst alleen de opgegeven beheerders toe: andere gebruikers kunnen zich niet
         aanmelden bij de app, ook al proberen ze het;
      5. (sinds 0.8) maakt de vijf profielen aan als app-rollen: Moss365.Lezer, Moss365.Helpdesk, Moss365.Beheerder,
         Moss365.Auditor en Moss365.Appbeheer. De opgegeven beheerders krijgen Beheerder en Appbeheer. Met -GroepenMaken
         maakt het script ook de groepen GG-Moss365-Lezers, -Helpdesk, -Beheerders, -Auditors en -Appbeheer (cloud,
         beveiliging) en koppelt elke groep aan zijn rol; wie je in een groep zet, krijgt dat profiel.

    Op het einde toont het script de Tenant ID en de Client ID; vul die in de app in (scherm Verbindingen).
    Bestaat de app al, dan werkt het script de rechten en toewijzingen bij in plaats van een tweede aan te maken.
    Draai het dus gerust opnieuw na een nieuwe versie van de app (versie 0.3 voegt Exchange.Manage toe,
    versie 0.6 Policy.Read.All, versie 0.6.1 de rechten tot en met 0.9, versie 0.8 de profielen).

.PARAMETER GroepenMaken
    Maakt de groepen GG-Moss365-... aan (als ze nog niet bestaan) en wijst ze toe aan de rollen. Toewijzen aan groepen
    vraagt Entra ID P1.

.PARAMETER Beheerders
    UPN's van de beheerders die de app mogen gebruiken. Standaard: wie dit script uitvoert.

.PARAMETER Naam
    Naam van de app-registratie. Standaard "Moss365".

.EXAMPLE
    .\Registreer-App.ps1

.EXAMPLE
    .\Registreer-App.ps1 -Beheerders naam@contoso.com, collega@contoso.com

.EXAMPLE
    .\Registreer-App.ps1 -GroepenMaken
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
    'Policy.Read.All',                                         # 0.6: Conditional Access lezen
    'Calendars.Read.Shared',                                   # 0.7: agenda-export (enkel agenda's waarop je zelf rechten hebt)
    'OnPremDirectorySynchronization.Read.All',                 # 0.7: script diagnose wachtwoordverloop
    'Policy.ReadWrite.ConditionalAccess', 'Application.Read.All'  # 0.9: Conditional Access beheren
)
$ExchangeRechten = @('Exchange.Manage')

# De profielen (0.8). De id's liggen vast: dezelfde als in de app (Core/Rechten/Rechten.cs), zodat opnieuw draaien niets dubbel maakt.
$Profielen = @(
    @{ Waarde = 'Moss365.Lezer';     Naam = 'Lezer';     Groep = 'GG-Moss365-Lezers';     Id = '6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8001'; Uitleg = 'Zoeken en bekijken, statistieken; wijzigt niets.' }
    @{ Waarde = 'Moss365.Helpdesk';  Naam = 'Helpdesk';  Groep = 'GG-Moss365-Helpdesk';   Id = '6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8002'; Uitleg = 'Wachtwoorden, ontgrendelen, leden van gewone groepen, gasten uitnodigen, exporteren.' }
    @{ Waarde = 'Moss365.Beheerder'; Naam = 'Beheerder'; Groep = 'GG-Moss365-Beheerders'; Id = '6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8003'; Uitleg = 'Alle wijzigingen, ook bevoorrechte accounts en groepen; scripts.' }
    @{ Waarde = 'Moss365.Auditor';   Naam = 'Auditor';   Groep = 'GG-Moss365-Auditors';   Id = '6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8004'; Uitleg = 'Bekijken, beveiliging, aanmeldingen en het volledige logboek; wijzigt niets.' }
    @{ Waarde = 'Moss365.Appbeheer'; Naam = 'Appbeheer'; Groep = 'GG-Moss365-Appbeheer';  Id = '6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8005'; Uitleg = 'De instellingen van Moss365 (organisatie, keuzelijsten).' }
)

function Get-AppRollen {
    # De app-rollen van de app: de bestaande (andere) rollen blijven, de profielen worden toegevoegd of bijgewerkt
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
    # Zoekt de id's van de gevraagde gedelegeerde rechten op een service-principal (Microsoft Graph of Exchange Online)
    param($GraphSp, [string[]]$Namen)
    foreach ($naam in $Namen) {
        $scope = @($GraphSp.Oauth2PermissionScopes | Where-Object { $_.Value -eq $naam }) | Select-Object -First 1
        if (-not $scope) { throw "Recht $naam niet gevonden bij $($GraphSp.DisplayName)." }
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
    Write-Host "App '$Naam' bestaat al ($($app.AppId)); rechten en omleiding worden bijgewerkt." -ForegroundColor Yellow
    if ($PSCmdlet.ShouldProcess($Naam, 'App-registratie bijwerken')) {
        Update-MgApplication -ApplicationId $app.Id -IsFallbackPublicClient -PublicClient @{ RedirectUris = @('http://localhost') } -RequiredResourceAccess $vereist `
            -AppRoles @(Get-AppRollen -Bestaand $app.AppRoles)
    }
}
elseif ($PSCmdlet.ShouldProcess($Naam, 'App-registratie aanmaken')) {
    $app = New-MgApplication -DisplayName $Naam -SignInAudience 'AzureADMyOrg' -IsFallbackPublicClient `
        -PublicClient @{ RedirectUris = @('http://localhost') } -RequiredResourceAccess $vereist -AppRoles @(Get-AppRollen -Bestaand @()) `
        -Notes 'Beheer-app Contoso (github.com/jef-cesar/Moss365). Gedelegeerde rechten; alleen toegewezen beheerders.'
    Write-Host "App '$Naam' aangemaakt." -ForegroundColor Green
}

$sp = @(Get-MgServicePrincipal -Filter "appId eq '$($app.AppId)'") | Select-Object -First 1
if (-not $sp -and $PSCmdlet.ShouldProcess($Naam, 'Service-principal aanmaken')) {
    $sp = New-MgServicePrincipal -AppId $app.AppId -AppRoleAssignmentRequired
}
elseif ($sp -and $PSCmdlet.ShouldProcess($Naam, 'Toewijzing vereist aanzetten')) {
    Update-MgServicePrincipal -ServicePrincipalId $sp.Id -AppRoleAssignmentRequired
}

# Beheerderstoestemming voor de gedelegeerde rechten, per bron (Graph en Exchange Online)
foreach ($bron in @(@{ Sp = $graphSp; Rechten = $Rechten }, @{ Sp = $exchangeSp; Rechten = $ExchangeRechten })) {
    $bestaand = @(Get-MgOauth2PermissionGrant -Filter "clientId eq '$($sp.Id)' and resourceId eq '$($bron.Sp.Id)'") | Select-Object -First 1
    $scopeTekst = $bron.Rechten -join ' '
    if ($bestaand) {
        if ($PSCmdlet.ShouldProcess($Naam, "Toestemming bijwerken ($($bron.Sp.DisplayName)): $scopeTekst")) {
            Update-MgOauth2PermissionGrant -OAuth2PermissionGrantId $bestaand.Id -Scope $scopeTekst
        }
    }
    elseif ($PSCmdlet.ShouldProcess($Naam, "Toestemming geven ($($bron.Sp.DisplayName)): $scopeTekst")) {
        New-MgOauth2PermissionGrant -ClientId $sp.Id -ConsentType 'AllPrincipals' -ResourceId $bron.Sp.Id -Scope $scopeTekst | Out-Null
    }
}

# Alleen de opgegeven beheerders mogen zich aanmelden, met de profielen Beheerder en Appbeheer
$toewijzingen = @(Get-MgServicePrincipalAppRoleAssignedTo -ServicePrincipalId $sp.Id -All)
$beheerRollen = @($Profielen | Where-Object { $_.Naam -in 'Beheerder', 'Appbeheer' })
foreach ($upn in $Beheerders) {
    $gebruiker = Get-MgUser -UserId $upn
    foreach ($rol in $beheerRollen) {
        $al = @($toewijzingen | Where-Object { $_.PrincipalId -eq $gebruiker.Id -and "$($_.AppRoleId)" -eq $rol.Id })
        if ($al.Count -eq 0 -and $PSCmdlet.ShouldProcess($upn, "Profiel $($rol.Naam) toewijzen")) {
            New-MgServicePrincipalAppRoleAssignedTo -ServicePrincipalId $sp.Id -PrincipalId $gebruiker.Id -ResourceId $sp.Id -AppRoleId $rol.Id | Out-Null
            Write-Host "  $upn heeft het profiel $($rol.Naam)."
        }
    }
}

# Een groep per profiel; wie je in de groep zet, krijgt het profiel (en mag zich aanmelden)
if ($GroepenMaken) {
    foreach ($p in $Profielen) {
        $groep = @(Get-MgGroup -Filter "displayName eq '$($p.Groep)'") | Select-Object -First 1
        if (-not $groep -and $PSCmdlet.ShouldProcess($p.Groep, 'Groep aanmaken')) {
            $groep = New-MgGroup -DisplayName $p.Groep -MailEnabled:$false -MailNickname $p.Groep -SecurityEnabled `
                -Description "Profiel $($p.Naam) in Moss365. $($p.Uitleg)"
            Write-Host "  Groep $($p.Groep) aangemaakt."
        }
        if ($groep) {
            $al = @($toewijzingen | Where-Object { $_.PrincipalId -eq $groep.Id -and "$($_.AppRoleId)" -eq $p.Id })
            if ($al.Count -eq 0 -and $PSCmdlet.ShouldProcess($p.Groep, "Toewijzen aan het profiel $($p.Naam)")) {
                New-MgServicePrincipalAppRoleAssignedTo -ServicePrincipalId $sp.Id -PrincipalId $groep.Id -ResourceId $sp.Id -AppRoleId $p.Id | Out-Null
                Write-Host "  $($p.Groep) -> profiel $($p.Naam)."
            }
        }
    }
}

Write-Host ''
Write-Host 'Vul deze waarden in de app in (scherm Verbindingen):' -ForegroundColor Cyan
Write-Host "  Tenant ID : $($context.TenantId)"
Write-Host "  Client ID : $($app.AppId)"
