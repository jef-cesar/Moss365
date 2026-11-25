<#MOSS365
Name: Diagnose wachtwoordverloop
Description: Waarom iemand telkens opnieuw moet aanmelden of zijn wachtwoord moet wijzigen (Entra, AD, aanmeldingen); leest enkel
Version: 1.0
Connection: Graph, AD
Writes: no
Variable: UserPrincipalName | User | required | Gebruiker |  |
Variable: Dagen | Number | optional | Aanmeldingen van hoeveel dagen (1 tot 30)? | 30 |
Variable: SkipOnPrem | YesNo | optional | AD overslaan |  |
#>
<#
.SYNOPSIS
    Achterhaalt waarom een gebruiker periodiek opnieuw moet aanmelden of zijn wachtwoord moet wijzigen,
    bv. Apple Mail op de iPhone die om de paar maanden opnieuw het wachtwoord vraagt.

.DESCRIPTION
    Read-only diagnose: het script wijzigt niets. Het controleert:

    1. Entra ID (Microsoft Graph)
       - Cloud-only of gesynchroniseerd vanuit on-prem AD (Entra Connect)?
       - passwordPolicies op de gebruiker (DisablePasswordExpiration)
       - Wachtwoordverloop van het UPN-domein (PasswordValidityPeriodInDays), Managed of Federated
       - Entra Connect-feature CloudPasswordPolicyForPasswordSyncedUsersEnabled
       - forceChangePasswordNextSignIn, laatste wachtwoordwijziging, laatste intrekking van sessies
       - Conditional Access: Sign-in frequency (periodiek opnieuw aanmelden) en "wachtwoordwijziging
         vereisen", inclusief of de gebruiker in scope valt (via gebruiker, groepen en rollen)
       - Identity Protection: huidig risico, risicogeschiedenis (herstel via wachtwoordwijziging?)
         en risicodetecties van de laatste 90 dagen

    2. On-prem Active Directory (alleen als de gebruiker gesynchroniseerd is)
       - PasswordNeverExpires, PasswordLastSet, berekende vervaldatum
       - Fine-Grained Password Policy (PSO) die effectief van toepassing is, plus alle PSO's
       - Domeinwachtwoordbeleid (MaxPasswordAge) en welke GPO op de domeinroot het instelt

    3. Aanmeldlogboeken van Apple Mail ("iOS Accounts") voor de laatste -Dagen dagen (max. 30)
       - Interactieve aanmeldingen = momenten waarop de gebruiker zelf opnieuw zijn wachtwoord ingaf
       - Mislukte aanmeldingen/token-vernieuwingen per foutcode, met uitleg
         (bv. 50173 tokens ingetrokken na wachtwoordwijziging, 70043 sign-in frequency, 700082 inactiviteit)
       - Auditlog: wie of wat het wachtwoord wijzigde of resette

    4. Conclusie: welke bron het gedrag veroorzaakt en waar je het aanpast.

.PARAMETER UserPrincipalName
    UPN van de gebruiker.

.PARAMETER Dagen
    Hoeveel dagen aanmeldlogboek bekijken (1-30). Entra bewaart sign-in logs maximaal 30 dagen
    (7 dagen zonder Entra ID P1).

.PARAMETER SkipOnPrem
    Sla de on-prem AD-controle over.

.EXAMPLE
    .\Check-WachtwoordVerloop.ps1
    .\Check-WachtwoordVerloop.ps1 -UserPrincipalName iemand@contoso.com -Dagen 14

.NOTES
    Vereisten
      - Microsoft Graph PowerShell SDK:  Install-Module Microsoft.Graph -Scope CurrentUser
      - Deel 2: RSAT ActiveDirectory + GroupPolicy. Uitvoeren op een DC of domeinlid,
        als Domain Admin (of met leesrechten op de Password Settings Container),
        anders zijn PSO's onzichtbaar en lijkt het alsof er geen PSO is.
    Graph-scopes (alleen lezen): User.Read.All, Domain.Read.All, Directory.Read.All, AuditLog.Read.All,
        OnPremDirectorySynchronization.Read.All, Policy.Read.All, IdentityRiskyUser.Read.All,
        IdentityRiskEvent.Read.All
#>
[CmdletBinding()]
param(
    [string]$UserPrincipalName,
    [ValidateRange(1, 30)][int]$Dagen = 30,
    [switch]$SkipOnPrem
)

$ErrorActionPreference = 'Stop'

# ---- Waarden vragen en onthouden (zelfde blok in elk script; ASCII-only) ----
# Een waarde die je niet als parameter meegeeft, vraagt het script bij het draaien. De laatst gebruikte waarde staat
# tussen [ ] klaar: Enter neemt ze over. De waarden blijven lokaal op deze pc, in
# %LOCALAPPDATA%\Moss365\scripts\waarden\<script>.json (nooit in de repo). Moss365 gebruikt dezelfde bestanden.
function Get-ScriptWaarde {
    param(
        [Parameter(Mandatory = $true)][string]$Bestand,
        [Parameter(Mandatory = $true)][string]$Naam,
        [Parameter(Mandatory = $true)][string]$Vraag,
        [object]$Waarde,
        [object]$Standaard,
        [switch]$Lijst
    )
    $opgeslagen = [ordered]@{}
    if (Test-Path -LiteralPath $Bestand) {
        try {
            $json = Get-Content -LiteralPath $Bestand -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($p in $json.PSObject.Properties) { $opgeslagen[$p.Name] = $p.Value }
        }
        catch { Write-Warning "Onthouden waarden niet leesbaar ($Bestand); ze worden opnieuw gevraagd." }
    }

    $leeg = { param($w) $null -eq $w -or (@($w | Where-Object { "$_".Trim() -ne '' })).Count -eq 0 }
    if (& $leeg $Waarde) {
        $vorige = if ($opgeslagen.Contains($Naam)) { $opgeslagen[$Naam] } else { $Standaard }
        if ($global:MossUitvoer) {
            # In Moss365 komt alles uit het formulier: niet vragen, de laatste of standaardwaarde nemen
            $Waarde = $vorige
        }
        else {
        $toon = if (& $leeg $vorige) { '' } else { ' [' + ((@($vorige) | Select-Object -First 5) -join ', ') + $(if (@($vorige).Count -gt 5) { ', ...' } else { '' }) + ']' }
        $hint = if ($Lijst) { ' (gescheiden door komma''s, of het pad naar een bestand met een waarde per lijn)' } else { '' }
        $antwoord = Read-Host "$Vraag$hint$toon"
        if ([string]::IsNullOrWhiteSpace($antwoord)) { $Waarde = $vorige }
        elseif ($Lijst -and (Test-Path -LiteralPath $antwoord.Trim().Trim('"') -PathType Leaf)) {
            $Waarde = @(Get-Content -LiteralPath $antwoord.Trim().Trim('"') -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        }
        elseif ($Lijst) { $Waarde = @($antwoord -split '[,;]' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }) }
        else { $Waarde = $antwoord.Trim() }
        }
    }
    if (& $leeg $Waarde) { throw "Geen waarde voor $Naam." }

    if ($Lijst) { $Waarde = @($Waarde | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne '' }) }
    $opgeslagen[$Naam] = $Waarde
    $map = Split-Path -Parent $Bestand
    # Onthouden is geen wijziging: ook bij -WhatIf bewaren
    if (-not (Test-Path -LiteralPath $map)) { $null = New-Item -ItemType Directory -Path $map -Force -WhatIf:$false }
    $opgeslagen | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Bestand -Encoding UTF8 -WhatIf:$false
    if ($Lijst) { return , $Waarde }
    return $Waarde
}

$WaardenBestand = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Moss365\scripts\waarden\' + $(if ($PSCommandPath) { [IO.Path]::GetFileNameWithoutExtension($PSCommandPath) } else { 'script' }) + '.json')
# ---- einde blok ----

$UserPrincipalName = Get-ScriptWaarde -Bestand $WaardenBestand -Naam 'UserPrincipalName' -Vraag 'Gebruiker (UPN)' -Waarde $UserPrincipalName
$NooitVerlopen = 2147483647
$AppleAppId    = 'f8d98a96-0999-43f5-8af3-69971c7bb423'   # "iOS Accounts" / "Apple Internet Accounts" (Apple Mail, Agenda, Contacten)
$conclusies    = [System.Collections.Generic.List[string]]::new()

# Foutcodes die verklaren waarom een app opnieuw om het wachtwoord vraagt
$Foutcodes = @{
    50055  = 'Wachtwoord verlopen'
    50133  = 'Sessie ongeldig door wachtwoordverloop of recente wachtwoordwijziging'
    50173  = 'Tokens ingetrokken (wachtwoordwijziging/reset of admin "sessies intrekken"): opnieuw aanmelden nodig'
    70043  = 'Token verlopen door Sign-in frequency (Conditional Access)'
    700082 = 'Refresh token verlopen door inactiviteit (90 dagen niet gebruikt)'
    50126  = 'Verkeerde gebruikersnaam of wachtwoord (vaak: iPhone probeert nog het oude wachtwoord)'
    50053  = 'Account vergrendeld (smart lockout), vaak door herhaalde pogingen met oud wachtwoord'
    50074  = 'MFA vereist'
    50076  = 'MFA vereist (configuratiewijziging of nieuwe locatie)'
    50079  = 'Gebruiker moet MFA registreren'
    53000  = 'Toestel niet compliant (Conditional Access)'
    53003  = 'Geblokkeerd door Conditional Access'
}

function Write-Kop   ([string]$Tekst)         { Write-Host "`n=== $Tekst ===" -ForegroundColor Cyan }
function Write-Regel ([string]$Label, $Waarde) { Write-Host ('{0,-52} {1}' -f $Label, $Waarde) }
function ConvertTo-Lokaal ($Tijd) { if ($Tijd) { return ([datetime]$Tijd).ToLocalTime() } }

# MaxPasswordAge van 0 of TimeSpan.MinValue betekent "verloopt nooit"
function Test-Verloopt ($Duur) {
    if ($null -eq $Duur) { return $false }
    $d = [TimeSpan]$Duur
    return ($d.TotalDays -gt 0 -and $d.TotalDays -le 36500)
}
function Format-Dagen ($Duur) {
    if ($null -eq $Duur)      { return '(onbekend)' }
    if (Test-Verloopt $Duur) { return ('{0} dagen' -f [math]::Round(([TimeSpan]$Duur).TotalDays)) }
    return 'verloopt nooit'
}

function Format-Frequentie ($Beleid) {
    $f = $Beleid.SessionControls.SignInFrequency
    if ($f.FrequencyInterval -eq 'everyTime') { return 'bij elke aanmelding' }
    if ($f.Type -eq 'hours') { $eenheid = 'uur' } else { $eenheid = 'dagen' }
    return "elke $($f.Value) $eenheid"
}

# Valt de gebruiker onder een CA-beleid? (gebruiker, groepen, rollen; uitsluitingen gaan voor)
function Test-InScope ($Beleid) {
    $u   = $Beleid.Conditions.Users
    $in  = ($u.IncludeUsers -contains 'All') -or ($u.IncludeUsers -contains $user.Id) -or
           (@($u.IncludeGroups | Where-Object { $_ -and $mijnGroepen -contains $_ }).Count -gt 0) -or
           (@($u.IncludeRoles  | Where-Object { $_ -and $mijnRollen  -contains $_ }).Count -gt 0)
    $uit = ($u.ExcludeUsers -contains $user.Id) -or
           (@($u.ExcludeGroups | Where-Object { $_ -and $mijnGroepen -contains $_ }).Count -gt 0) -or
           (@($u.ExcludeRoles  | Where-Object { $_ -and $mijnRollen  -contains $_ }).Count -gt 0)
    return ($in -and -not $uit)
}

# Sign-in logs via beta-endpoint (enige manier om ook niet-interactieve aanmeldingen op te halen)
function Get-AanmeldLog ([string]$Filter) {
    $uri   = 'https://graph.microsoft.com/beta/auditLogs/signIns?$filter=' + [uri]::EscapeDataString($Filter) + '&$top=500'
    $lijst = [System.Collections.Generic.List[object]]::new()
    while ($uri) {
        $pagina = Invoke-MgGraphRequest -Method GET -Uri $uri -OutputType PSObject
        foreach ($r in $pagina.value) { $lijst.Add($r) }
        $uri = $pagina.'@odata.nextLink'
    }
    return $lijst
}

# -----------------------------------------------------------------------------
# 1. Entra ID
# -----------------------------------------------------------------------------
Write-Kop '1. Entra ID (Microsoft Graph)'

# Moss365 connects to Microsoft Graph before the script runs; outside the app, connect first yourself
if (-not (Get-MgContext -ErrorAction SilentlyContinue)) { throw 'Geen verbinding met Microsoft Graph: start dit script vanuit Moss365, of verbind eerst zelf.' }

$eigenschappen = 'id', 'displayName', 'userPrincipalName', 'createdDateTime', 'passwordPolicies',
                 'passwordProfile', 'lastPasswordChangeDateTime', 'signInSessionsValidFromDateTime',
                 'onPremisesSyncEnabled', 'onPremisesSamAccountName', 'onPremisesDistinguishedName',
                 'onPremisesDomainName', 'onPremisesLastSyncDateTime'
$user = Get-MgUser -UserId $UserPrincipalName -Property $eigenschappen

$gesynct            = [bool]$user.OnPremisesSyncEnabled
$cloudNooitVerlopen = [string]$user.PasswordPolicies -match 'DisablePasswordExpiration'
$forceChange        = $user.PasswordProfile.ForceChangePasswordNextSignIn

Write-Regel 'Gebruiker' "$($user.DisplayName) <$($user.UserPrincipalName)>"
Write-Regel 'Aangemaakt' $user.CreatedDateTime
if ($gesynct) { $bronTekst = 'On-prem AD (gesynchroniseerd via Entra Connect)' } else { $bronTekst = 'Cloud-only' }
Write-Regel 'Bron van het account' $bronTekst
if ($gesynct) {
    Write-Regel 'On-prem sAMAccountName' $user.OnPremisesSamAccountName
    Write-Regel 'On-prem domein' $user.OnPremisesDomainName
    Write-Regel 'On-prem DN' $user.OnPremisesDistinguishedName
    Write-Regel 'Laatste sync' $user.OnPremisesLastSyncDateTime
}
Write-Regel 'Laatste wachtwoordwijziging (lokale tijd)' (ConvertTo-Lokaal $user.LastPasswordChangeDateTime)
Write-Regel 'Sessies/tokens geldig vanaf (lokale tijd)' "$(ConvertTo-Lokaal $user.SignInSessionsValidFromDateTime)  (alles van daarvoor is ingetrokken)"
if ($user.PasswordPolicies) { $ppTekst = $user.PasswordPolicies } else { $ppTekst = 'None' }
Write-Regel 'passwordPolicies (gebruiker)' $ppTekst
if ($null -ne $forceChange) { $fcTekst = $forceChange } else { $fcTekst = '(niet leesbaar)' }
Write-Regel 'forceChangePasswordNextSignIn' $fcTekst

# Viel de laatste intrekking van alle tokens samen met de laatste wachtwoordwijziging?
$intrekkingDoorWachtwoord = $false
$intrekkingLos            = $false
if ($user.SignInSessionsValidFromDateTime -and $user.LastPasswordChangeDateTime) {
    $verschil = [math]::Abs(($user.SignInSessionsValidFromDateTime - $user.LastPasswordChangeDateTime).TotalMinutes)
    $intrekkingDoorWachtwoord = $verschil -le 10
    $intrekkingLos = $user.SignInSessionsValidFromDateTime -gt $user.LastPasswordChangeDateTime.AddMinutes(10)
}

# Domeinbeleid in Entra
$domeinNaam = $UserPrincipalName.Split('@')[1]
$domein     = Get-MgDomain -DomainId $domeinNaam
$geldigheid = $domein.PasswordValidityPeriodInDays
$federated  = $domein.AuthenticationType -eq 'Federated'

if ($null -eq $geldigheid) {
    $cloudDagen = 90; $geldigheidTekst = 'niet ingesteld (Graph-standaard: 90 dagen)'
} elseif ($geldigheid -eq $NooitVerlopen) {
    $cloudDagen = 0;  $geldigheidTekst = "$geldigheid = verloopt nooit"
} else {
    $cloudDagen = $geldigheid; $geldigheidTekst = "$geldigheid dagen"
}
$cloudDomeinVerloopt = $cloudDagen -gt 0

Write-Regel "Domein $domeinNaam - authenticatie" $domein.AuthenticationType
Write-Regel "Domein $domeinNaam - wachtwoordverloop" $geldigheidTekst
Write-Regel "Domein $domeinNaam - meldingsvenster" "$($domein.PasswordNotificationWindowInDays) dagen"

# Entra Connect: dwingt Entra ook verloop af voor gesynchroniseerde gebruikers?
$cloudBeleidVoorGesynct = $null
try {
    $sync = Get-MgDirectoryOnPremiseSynchronization | Select-Object -First 1
    $cloudBeleidVoorGesynct = [bool]$sync.Features.CloudPasswordPolicyForPasswordSyncedUsersEnabled
    Write-Regel 'CloudPasswordPolicyForPasswordSyncedUsersEnabled' $cloudBeleidVoorGesynct
} catch {
    Write-Regel 'CloudPasswordPolicyForPasswordSyncedUsersEnabled' "niet leesbaar ($($_.Exception.Message))"
}

# Groepen en rollen van de gebruiker (nodig om CA-scope te bepalen)
$mijnGroepen = @(); $mijnRollen = @()
try {
    $lidVan      = Get-MgUserTransitiveMemberOf -UserId $user.Id -All
    $mijnGroepen = @($lidVan | Where-Object { $_.AdditionalProperties['@odata.type'] -eq '#microsoft.graph.group' } |
                     ForEach-Object { $_.Id })
    $mijnRollen  = @($lidVan | Where-Object { $_.AdditionalProperties['@odata.type'] -eq '#microsoft.graph.directoryRole' } |
                     ForEach-Object { $_.AdditionalProperties['roleTemplateId'] })
} catch {
    Write-Warning "Kon groepslidmaatschap niet lezen; CA-scope wordt onnauwkeurig: $($_.Exception.Message)"
}

# Conditional Access
$alleCa = @()
try {
    $alleCa = @(Get-MgIdentityConditionalAccessPolicy -All)
} catch {
    Write-Regel 'Conditional Access' "niet leesbaar ($($_.Exception.Message))"
}
$caFreq        = @($alleCa | Where-Object { $_.SessionControls.SignInFrequency.IsEnabled })
$caFreqVoorMij = @($caFreq | Where-Object { $_.State -eq 'enabled' -and (Test-InScope $_) })
$caPw          = @($alleCa | Where-Object { $_.GrantControls.BuiltInControls -contains 'passwordChange' })

if ($caFreq.Count -gt 0) {
    foreach ($p in $caFreq) {
        if (Test-InScope $p) { $scopeTekst = 'GELDT voor deze gebruiker' } else { $scopeTekst = 'niet voor deze gebruiker' }
        Write-Regel 'CA Sign-in frequency' ('{0} [{1}] {2}, apps: {3} -> {4}' -f $p.DisplayName, $p.State,
            (Format-Frequentie $p), ($p.Conditions.Applications.IncludeApplications -join ','), $scopeTekst)
    }
} else {
    Write-Regel 'CA Sign-in frequency' 'geen beleid'
}

if ($caPw.Count -gt 0) {
    foreach ($p in $caPw) {
        Write-Regel 'CA "wachtwoordwijziging vereisen"' ('{0} [{1}] gebruikersrisico: {2}' -f
            $p.DisplayName, $p.State, ($p.Conditions.UserRiskLevels -join ','))
    }
} else {
    Write-Regel 'CA "wachtwoordwijziging vereisen"' 'geen beleid'
}

# Identity Protection: een risicoherstel via wachtwoordwijziging trekt ook alle tokens in
$risicoHersteld = @()
try {
    $risico = Get-MgRiskyUser -RiskyUserId $user.Id
    Write-Regel 'Gebruikersrisico (Identity Protection)' ("{0} / {1} / {2}, laatst gewijzigd {3}" -f
        $risico.RiskLevel, $risico.RiskState, $risico.RiskDetail, (ConvertTo-Lokaal $risico.RiskLastUpdatedDateTime))
    try {
        $historiek = @(Get-MgRiskyUserHistory -RiskyUserId $user.Id -All | Sort-Object RiskLastUpdatedDateTime)
        if ($historiek.Count -gt 0) {
            Write-Host "`nRisicogeschiedenis:"
            $historiek | Select-Object `
                @{ n = 'Tijdstip';    e = { ConvertTo-Lokaal $_.RiskLastUpdatedDateTime } },
                @{ n = 'Niveau';      e = { $_.RiskLevel } },
                @{ n = 'Status';      e = { $_.RiskState } },
                @{ n = 'Detail';      e = { $_.RiskDetail } },
                @{ n = 'Detecties';   e = { $_.Activity.RiskEventTypes -join ',' } },
                @{ n = 'GestartDoor'; e = { $_.InitiatedBy } } |
                Format-Table -AutoSize -Wrap | Out-Host
            $risicoHersteld = @($historiek | Where-Object { [string]$_.RiskDetail -match 'Password' })
        }
    } catch {
        Write-Warning "Kon risicogeschiedenis niet lezen: $($_.Exception.Message)"
    }
} catch {
    Write-Regel 'Gebruikersrisico (Identity Protection)' 'geen record (nooit risicovol) of niet beschikbaar (P2/rechten)'
}

try {
    $detecties = @(Get-MgRiskDetection -Filter "userId eq '$($user.Id)'" -All | Sort-Object DetectedDateTime -Descending)
    if ($detecties.Count -gt 0) {
        Write-Host "Risicodetecties (laatste 90 dagen, nieuwste eerst, max. 25):"
        $detecties | Select-Object -First 25 -Property `
            @{ n = 'Tijdstip'; e = { ConvertTo-Lokaal $_.DetectedDateTime } },
            @{ n = 'Type';     e = { $_.RiskEventType } },
            @{ n = 'Niveau';   e = { $_.RiskLevel } },
            @{ n = 'Status';   e = { $_.RiskState } },
            @{ n = 'IP';       e = { $_.IPAddress } },
            @{ n = 'Locatie';  e = { "$($_.Location.City) $($_.Location.CountryOrRegion)".Trim() } } |
            Format-Table -AutoSize | Out-Host
    } else {
        Write-Regel 'Risicodetecties' 'geen'
    }
} catch {
    Write-Regel 'Risicodetecties' "niet leesbaar ($($_.Exception.Message))"
}

# -----------------------------------------------------------------------------
# 2. On-prem Active Directory
# -----------------------------------------------------------------------------
Write-Kop '2. On-prem Active Directory'
$adUser = $null; $adMaxLeeftijd = $null; $adBron = $null; $winnendeGpo = $null

if (-not $gesynct) {
    Write-Host 'Overgeslagen: gebruiker is cloud-only.'
}
elseif ($SkipOnPrem) {
    Write-Host 'Overgeslagen (-SkipOnPrem).'
}
elseif (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
    Write-Warning 'ActiveDirectory-module niet gevonden. Voer het script uit op een DC of een domeinlid met RSAT.'
}
else {
    Import-Module ActiveDirectory
    $adServer = @{}; $gpDomein = @{}
    if ($user.OnPremisesDomainName) {
        $adServer.Server = $user.OnPremisesDomainName
        $gpDomein.Domain = $user.OnPremisesDomainName
    }

    $adEigenschappen = 'PasswordNeverExpires', 'PasswordLastSet', 'PasswordExpired', 'Enabled',
                       'msDS-UserPasswordExpiryTimeComputed'
    $adUser = Get-ADUser -Identity $user.OnPremisesSamAccountName -Properties $adEigenschappen @adServer

    $vervalRaw = $adUser.'msDS-UserPasswordExpiryTimeComputed'
    if ($vervalRaw -eq 0 -or $vervalRaw -eq [Int64]::MaxValue) {
        $vervalTekst = 'geen (verloopt niet)'
    } else {
        $vervalTekst = [datetime]::FromFileTime($vervalRaw)
    }
    if ($adUser.PasswordLastSet) { $plsTekst = $adUser.PasswordLastSet } else { $plsTekst = 'leeg = moet wijzigen bij volgende aanmelding' }

    Write-Regel 'PasswordNeverExpires (AD)' $adUser.PasswordNeverExpires
    Write-Regel 'PasswordLastSet (AD)' $plsTekst
    Write-Regel 'PasswordExpired (AD)' $adUser.PasswordExpired
    Write-Regel 'Berekende vervaldatum (AD)' $vervalTekst

    # Fine-Grained Password Policy (PSO) gaat voor op het domeinbeleid
    $pso = $null
    try {
        $pso = Get-ADUserResultantPasswordPolicy -Identity $adUser @adServer
    } catch {
        Write-Warning "Kon effectieve PSO niet lezen: $($_.Exception.Message)"
    }
    $ddp = Get-ADDefaultDomainPasswordPolicy @adServer

    if ($pso) {
        $adMaxLeeftijd = $pso.MaxPasswordAge
        $adBron = "Fine-Grained Password Policy '$($pso.Name)' (ADAC > System > Password Settings Container)"
        Write-Regel 'Effectieve PSO' "$($pso.Name) (precedence $($pso.Precedence))"
        Write-Regel '  MaxPasswordAge (PSO)' (Format-Dagen $pso.MaxPasswordAge)
        Write-Regel '  AppliesTo' ($pso.AppliesTo -join '; ')
    } else {
        Write-Regel 'Effectieve PSO' 'geen -> domeinwachtwoordbeleid geldt'
    }
    Write-Regel 'Domeinbeleid MaxPasswordAge' (Format-Dagen $ddp.MaxPasswordAge)

    try {
        $allePso = Get-ADFineGrainedPasswordPolicy -Filter * @adServer | Sort-Object Precedence
        if ($allePso) {
            Write-Host "`nAlle PSO's in het domein:"
            $allePso | Select-Object Name, Precedence,
                @{ n = 'MaxPasswordAge'; e = { Format-Dagen $_.MaxPasswordAge } },
                @{ n = 'AppliesTo';      e = { $_.AppliesTo -join '; ' } } |
                Format-Table -AutoSize | Out-Host
        }
    } catch {
        Write-Warning "Kon PSO's niet opsommen: $($_.Exception.Message)"
    }

    # Welke GPO op de domeinroot zet MaximumPasswordAge? (laagste linkvolgorde wint)
    if (-not $pso) {
        if (Get-Module -ListAvailable -Name GroupPolicy) {
            Import-Module GroupPolicy
            $domeinDN = (Get-ADDomain @adServer).DistinguishedName
            $links = (Get-GPInheritance -Target $domeinDN @gpDomein).GpoLinks |
                     Where-Object Enabled | Sort-Object Order
            foreach ($link in $links) {
                [xml]$rapport = Get-GPOReport -Guid $link.GpoId -ReportType Xml @gpDomein
                $node = $rapport.SelectSingleNode("//*[local-name()='Account'][*[local-name()='Name']='MaximumPasswordAge']")
                if ($node) {
                    $waarde = $node.SelectSingleNode("*[local-name()='SettingNumber']").InnerText
                    Write-Regel 'GPO op domeinroot met MaximumPasswordAge' "$($link.DisplayName) (linkvolgorde $($link.Order)): $waarde dagen"
                    if (-not $winnendeGpo) { $winnendeGpo = $link.DisplayName }
                }
            }
            if (-not $winnendeGpo) {
                Write-Regel 'GPO op domeinroot met MaximumPasswordAge' 'geen gevonden; waarde staat rechtstreeks op het domeinobject'
            }
        } else {
            Write-Warning 'GroupPolicy-module niet gevonden; kan de bron-GPO niet bepalen (meestal "Default Domain Policy").'
        }
        $adMaxLeeftijd = $ddp.MaxPasswordAge
        if ($winnendeGpo) {
            $adBron = "GPO '$winnendeGpo' (gekoppeld aan de domeinroot)"
        } else {
            $adBron = 'domeinwachtwoordbeleid (meestal Default Domain Policy)'
        }
    }

    Write-Host "`nLet op: wachtwoordbeleid in GPO's gekoppeld aan een OU geldt NIET voor domeinaccounts, alleen voor lokale accounts op de pc's in die OU."
}

# -----------------------------------------------------------------------------
# 3. Aanmeldlogboeken Apple Mail / iOS Accounts
# -----------------------------------------------------------------------------
Write-Kop "3. Aanmeldlogboeken Apple Mail / iOS Accounts (laatste $Dagen dagen)"

$sinds = (Get-Date).ToUniversalTime().AddDays(-$Dagen).ToString('yyyy-MM-ddTHH:mm:ssZ')
$basis = "userId eq '$($user.Id)' and createdDateTime ge $sinds and appId eq '$AppleAppId'"
$appleInteractief = @(); $appleNietInteractief = @(); $fouten = @(); $logLeesbaar = $false

try {
    $appleInteractief     = @(Get-AanmeldLog "$basis and signInEventTypes/any(t: t eq 'interactiveUser')")
    $appleNietInteractief = @(Get-AanmeldLog "$basis and signInEventTypes/any(t: t eq 'nonInteractiveUser')")
    $logLeesbaar = $true
} catch {
    Write-Warning "Kon aanmeldlogboeken niet lezen: $($_.Exception.Message)"
    Write-Warning 'Vereist AuditLog.Read.All, een rol zoals Reports Reader/Global Reader, en Entra ID P1 voor meer dan 7 dagen.'
}

if ($logLeesbaar) {
    Write-Regel 'Interactieve aanmeldingen (wachtwoord ingegeven)' $appleInteractief.Count
    Write-Regel 'Niet-interactieve aanmeldingen (token-vernieuwing)' $appleNietInteractief.Count

    if ($appleInteractief.Count -gt 0) {
        Write-Host "`nInteractieve Apple-aanmeldingen (de momenten waarop hij opnieuw moest inloggen):"
        $appleInteractief | Sort-Object { [datetime]$_.createdDateTime } | Select-Object `
            @{ n = 'Tijdstip';  e = { ([datetime]$_.createdDateTime).ToLocalTime() } },
            @{ n = 'Resultaat'; e = { if ($_.status.errorCode -eq 0) { 'OK' } else { [string]$_.status.errorCode } } },
            @{ n = 'Toestel';   e = { ("$($_.deviceDetail.operatingSystem) $($_.deviceDetail.displayName)").Trim() } },
            @{ n = 'Client';    e = { $_.clientAppUsed } } |
            Format-Table -AutoSize | Out-Host
    } else {
        Write-Host "Geen interactieve Apple-aanmeldingen in deze periode: de laatste keer dat hij opnieuw moest inloggen"
        Write-Host "valt buiten de bewaartermijn. Draai het script opnieuw vlak nadat het weer gebeurt."
    }

    $fouten = @(@($appleInteractief) + @($appleNietInteractief) | Where-Object { $_.status.errorCode -ne 0 })
    if ($fouten.Count -gt 0) {
        Write-Host "Mislukte aanmeldingen en token-vernieuwingen per foutcode:"
        $fouten | Group-Object { $_.status.errorCode } | Sort-Object Count -Descending | ForEach-Object {
            $tijden = @($_.Group | ForEach-Object { ([datetime]$_.createdDateTime).ToLocalTime() } | Sort-Object)
            $code   = [int]$_.Name
            if ($Foutcodes.ContainsKey($code)) { $uitleg = $Foutcodes[$code] } else { $uitleg = $_.Group[0].status.failureReason }
            [pscustomobject]@{ Code = $code; Aantal = $_.Count; Eerste = $tijden[0]; Laatste = $tijden[-1]; Uitleg = $uitleg }
        } | Format-Table -AutoSize -Wrap | Out-Host
    } else {
        Write-Host 'Geen mislukte Apple-aanmeldingen in deze periode.'
    }
}

# Wie of wat wijzigde het wachtwoord? Elke wijziging of reset trekt de tokens van Apple Mail in.
$wwEvents = @()
try {
    $wwEvents = @(Get-MgAuditLogDirectoryAudit -All -Filter "activityDateTime ge $sinds and targetResources/any(t: t/id eq '$($user.Id)')" |
                  Where-Object { $_.ActivityDisplayName -match 'password' } | Sort-Object ActivityDateTime)
    if ($wwEvents.Count -gt 0) {
        Write-Host "`nWachtwoordgebeurtenissen in het auditlog:"
        $wwEvents | Select-Object `
            @{ n = 'Tijdstip';    e = { ConvertTo-Lokaal $_.ActivityDateTime } },
            @{ n = 'Activiteit';  e = { $_.ActivityDisplayName } },
            @{ n = 'Resultaat';   e = { $_.Result } },
            @{ n = 'GestartDoor'; e = { if ($_.InitiatedBy.User.UserPrincipalName) { $_.InitiatedBy.User.UserPrincipalName } else { $_.InitiatedBy.App.DisplayName } } },
            @{ n = 'Dienst';      e = { $_.LoggedByService } } |
            Format-Table -AutoSize -Wrap | Out-Host
    } else {
        Write-Regel 'Wachtwoordgebeurtenissen in auditlog' 'geen in deze periode'
    }
} catch {
    Write-Warning "Kon het auditlog niet lezen: $($_.Exception.Message)"
}

# -----------------------------------------------------------------------------
# 4. Conclusie
# -----------------------------------------------------------------------------
Write-Kop '4. Conclusie'

if ($forceChange) {
    $conclusies.Add('forceChangePasswordNextSignIn staat aan in Entra: eenmalige verplichte wijziging (bv. na reset door een admin), geen periodiek beleid.')
}

$adVerloopt = ($null -ne $adUser) -and (-not $adUser.PasswordNeverExpires) -and
              ($null -ne $adMaxLeeftijd) -and (Test-Verloopt $adMaxLeeftijd)

# --- Waar zit het wachtwoordverloop? ---
if (-not $gesynct) {
    if ($cloudNooitVerlopen) {
        $conclusies.Add('Cloud-only account met DisablePasswordExpiration: Entra laat het wachtwoord NIET verlopen.')
    } elseif ($cloudDomeinVerloopt) {
        $conclusies.Add("WACHTWOORDVERLOOP: Entra-beleid van domein $domeinNaam ($cloudDagen dagen).")
        $conclusies.Add("  Aanpassen: M365-beheercentrum > Instellingen > Organisatie-instellingen > Beveiliging en privacy > Beleid voor wachtwoordverloop, of Update-MgDomain -DomainId $domeinNaam -PasswordValidityPeriodInDays 2147483647. Enkel deze gebruiker: Update-MgUser -UserId $UserPrincipalName -PasswordPolicies DisablePasswordExpiration")
    } else {
        $conclusies.Add('Cloud-only en het domeinbeleid laat wachtwoorden niet verlopen.')
    }
}
else {
    if ($null -eq $adUser) {
        $conclusies.Add('Gesynchroniseerd account: wachtwoordverloop komt vrijwel zeker uit on-prem AD. Draai het script op een DC of domeinlid met RSAT om de exacte bron te zien.')
    } elseif ($adVerloopt) {
        $conclusies.Add("WACHTWOORDVERLOOP: on-prem AD - $adBron, MaxPasswordAge $(Format-Dagen $adMaxLeeftijd); PasswordNeverExpires staat uit op het account.")
    } else {
        $conclusies.Add('On-prem AD laat dit wachtwoord niet verlopen (PasswordNeverExpires aan, of MaxPasswordAge = 0).')
    }

    if ($cloudBeleidVoorGesynct -and $cloudDomeinVerloopt -and -not $cloudNooitVerlopen) {
        $conclusies.Add("EXTRA: CloudPasswordPolicyForPasswordSyncedUsersEnabled staat aan en domein $domeinNaam verloopt na $cloudDagen dagen, dus Entra dwingt ook voor dit gesynchroniseerde account verloop af.")
    }
    if ($federated) {
        $conclusies.Add("Domein $domeinNaam is gefedereerd: aanmelding en wachtwoordverloop lopen volledig via on-prem (ADFS/AD).")
    }
}

# --- Verband met Apple Mail ---
$verloopDagen = $null
if ($adVerloopt) {
    $verloopDagen = [math]::Round(([TimeSpan]$adMaxLeeftijd).TotalDays)
} elseif ($cloudDomeinVerloopt -and -not $cloudNooitVerlopen -and (-not $gesynct -or $cloudBeleidVoorGesynct)) {
    $verloopDagen = $cloudDagen
}
if ($verloopDagen) {
    $conclusies.Add("APPLE MAIL: elke wachtwoordwijziging (verplicht om de $verloopDagen dagen) trekt de bestaande tokens in, waarna Apple Mail het nieuwe wachtwoord vraagt. Ligt dit rond 60 dagen, dan verklaart dit het patroon.")
}

foreach ($p in $caFreqVoorMij) {
    $conclusies.Add("APPLE MAIL: CA-beleid '$($p.DisplayName)' dwingt opnieuw aanmelden af ($(Format-Frequentie $p)). Controleer de voorwaarden (apps, platformen, client apps) of test met What If in Entra > Conditional Access.")
}

if ($intrekkingDoorWachtwoord) {
    $conclusies.Add("De laatste intrekking van alle tokens ($(ConvertTo-Lokaal $user.SignInSessionsValidFromDateTime)) valt samen met de laatste wachtwoordwijziging.")
} elseif ($intrekkingLos) {
    $conclusies.Add("Tokens werden op $(ConvertTo-Lokaal $user.SignInSessionsValidFromDateTime) ingetrokken LOS van een wachtwoordwijziging (admin 'Sessies intrekken', script of beveiligingstool).")
}

foreach ($h in $risicoHersteld) {
    $conclusies.Add("IDENTITY PROTECTION: op $(ConvertTo-Lokaal $h.RiskLastUpdatedDateTime) werd een risico op het account hersteld via het wachtwoord ($($h.RiskDetail)). Zo'n wijziging trekt alle tokens in, ook die van Apple Mail. Kijk bij de risicodetecties welk type dit telkens triggert.")
}
if ($wwEvents.Count -gt 0) {
    $conclusies.Add("Het wachtwoord werd in deze periode $($wwEvents.Count) keer gewijzigd of gereset (zie auditlog: wie of wat). Na elke wijziging vraagt Apple Mail opnieuw het wachtwoord.")
}

$codesGezien = @($fouten | ForEach-Object { [int]$_.status.errorCode } | Select-Object -Unique)
if (@($codesGezien | Where-Object { $_ -in 50173, 50133, 50055 }).Count -gt 0) {
    $conclusies.Add('LOGBOEK BEVESTIGT: Apple Mail faalde omdat het wachtwoord gewijzigd/verlopen was of de tokens ingetrokken waren.')
}
if ($codesGezien -contains 70043) {
    $conclusies.Add('LOGBOEK BEVESTIGT: Sign-in frequency (Conditional Access) liet de token van Apple Mail verlopen.')
}
if ($codesGezien -contains 700082) {
    $conclusies.Add('LOGBOEK: refresh token verlopen door 90 dagen inactiviteit (Apple Mail werd lang niet gebruikt).')
}
if (@($codesGezien | Where-Object { $_ -in 50126, 50053 }).Count -gt 0) {
    $conclusies.Add('LOGBOEK: de iPhone probeerde met een verkeerd/oud wachtwoord aan te melden (50126/50053).')
}

if ($caPw.Count -gt 0) {
    $conclusies.Add('Er bestaat CA-beleid dat een wachtwoordwijziging vereist bij gebruikersrisico; dat triggert alleen bij risico, niet op vaste intervallen.')
}

$oorzaakGevonden = $verloopDagen -or $caFreqVoorMij.Count -gt 0 -or $risicoHersteld.Count -gt 0 -or
                   $wwEvents.Count -gt 0 -or $codesGezien.Count -gt 0 -or $intrekkingLos
if (-not $oorzaakGevonden) {
    $conclusies.Add("Geen periodieke oorzaak gevonden in de laatste $Dagen dagen. Draai het script opnieuw vlak nadat Apple Mail weer om het wachtwoord vraagt, zodat het moment in de logboeken zit.")
}

$conclusies | ForEach-Object { Write-Host "- $_" }
