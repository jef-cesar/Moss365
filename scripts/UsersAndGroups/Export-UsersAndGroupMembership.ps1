<#MOSS365
Name: Medewerkers en groepslidmaatschap
Description: Actieve medewerkers met naam en functie, en per gekozen groep of ze er lid van zijn (CSV)
Version: 1.0
Connection: Graph, Exchange
Writes: no
Variable: GroepNamen | Items | required | Groepen (een per regel) |  |
#>
<#
.SYNOPSIS
    Exporteert een lijst van actieve M365-gebruikers (dus GEEN gedeelde/room/equipment
    postvakken) met naam en functie, aangevuld met kolommen die aangeven of de gebruiker
    lid is van een aantal opgegeven groepen.

.OUTPUT
    CSV met kolommen: Naam, UPN, Functie, en per gekozen groep een kolom (waarde "Ja" / "Nee").
    Handig voor bv. een handtekeningtool die per groep een andere handtekening toont.

.VEREISTEN
    Install-Module ExchangeOnlineManagement -Scope CurrentUser
    Install-Module Microsoft.Graph -Scope CurrentUser

    Rechten nodig:
      - Exchange Online: minstens "View-Only Recipients"
      - Graph scopes: User.Read.All, Group.Read.All, GroupMember.Read.All
        (een Global Reader of vergelijkbare rol volstaat)
#>

# ============================================================
# CONFIGURATIE - als parameter, met deze standaardwaarden
# ============================================================
[CmdletBinding()]
param(
    # De groepen die je als kolom wil terugzien (naam moet exact overeenkomen met displayName)
    [Parameter(Mandatory = $true)][string[]]$GroepNamen,
    [string]$Uitvoer
)

# In Moss365 komt het bestand in de map van de run
$outputPath = if ($Uitvoer) { $Uitvoer } elseif ($global:MossUitvoer) { Join-Path $global:MossUitvoer 'M365_Gebruikers_Groepen.csv' } else { '.\M365_Gebruikers_Groepen.csv' }
$groupNames = $GroepNamen

# Moss365 connects to Exchange Online and Microsoft Graph before the script runs; outside the app, connect first
if (-not (Get-MgContext -ErrorAction SilentlyContinue) -or -not (Get-Command Get-EXOMailbox -ErrorAction SilentlyContinue)) {
    throw 'Geen verbinding met Microsoft Graph en Exchange Online: start dit script vanuit Moss365, of verbind eerst zelf.'
}

# ============================================================
# STAP 1: alle "echte" gebruikersmailboxen ophalen
#          (RecipientTypeDetails UserMailbox sluit gedeelde,
#          room- en equipment-mailboxen automatisch uit)
# ============================================================
Write-Host "Mailboxen ophalen (UserMailbox, geen gedeelde postvakken)..." -ForegroundColor Cyan
$mailboxUpns = Get-EXOMailbox -RecipientTypeDetails UserMailbox -ResultSize Unlimited |
    Select-Object -ExpandProperty UserPrincipalName

$mailboxUpnSet = @{}
foreach ($upn in $mailboxUpns) { $mailboxUpnSet[$upn.ToLower()] = $true }

Write-Host "  -> $($mailboxUpns.Count) gebruikersmailboxen gevonden." -ForegroundColor DarkGray

# ============================================================
# STAP 2: alle actieve (enabled) gebruikers uit Entra ID ophalen
#          in 1 call, en enkel de kruising met de mailboxlijst
#          hierboven bijhouden
# ============================================================
Write-Host "Actieve gebruikers ophalen uit Microsoft Graph..." -ForegroundColor Cyan
$allEnabledUsers = Get-MgUser -All -Filter "accountEnabled eq true" `
    -Property "Id,DisplayName,JobTitle,UserPrincipalName,AccountEnabled"

$userDetails = @{}   # key = Graph Id, value = PSCustomObject
foreach ($u in $allEnabledUsers) {
    if ($mailboxUpnSet.ContainsKey($u.UserPrincipalName.ToLower())) {
        $userDetails[$u.Id] = [PSCustomObject]@{
            Id                = $u.Id
            UserPrincipalName = $u.UserPrincipalName
            Naam              = $u.DisplayName
            Functie           = $u.JobTitle
        }
    }
}
Write-Host "  -> $($userDetails.Count) actieve gebruikers overgehouden na filtering." -ForegroundColor DarkGray

# ============================================================
# STAP 3: leden van elke groep ophalen (werkt voor zowel
#          distributielijsten als security groups, want beide
#          zijn 'groups' in Graph)
# ============================================================
Write-Host "Groepslidmaatschappen ophalen..." -ForegroundColor Cyan
$groupMembers = [ordered]@{}   # key = groepnaam, value = hashtable van user-Id's

foreach ($groupName in $groupNames) {
    $group = Get-MgGroup -Filter "displayName eq '$groupName'" -ConsistencyLevel eventual -CountVariable c

    if (-not $group) {
        Write-Warning "Groep '$groupName' niet gevonden - kolom wordt overal 'Nee'."
        $groupMembers[$groupName] = @{}
        continue
    }
    if ($group -is [array] -and $group.Count -gt 1) {
        Write-Warning "Meerdere groepen gevonden met naam '$groupName' - de eerste wordt gebruikt."
        $group = $group[0]
    }

    $memberIds = Get-MgGroupMember -GroupId $group.Id -All | Select-Object -ExpandProperty Id
    $lookup = @{}
    foreach ($id in $memberIds) { $lookup[$id] = $true }
    $groupMembers[$groupName] = $lookup

    Write-Host "  -> '$groupName': $($memberIds.Count) leden." -ForegroundColor DarkGray
}

# ============================================================
# STAP 4: resultaat samenstellen (1 rij per gebruiker,
#          1 kolom per groep)
# ============================================================
Write-Host "Resultaat samenstellen..." -ForegroundColor Cyan
$result = foreach ($u in $userDetails.Values) {
    $row = [ordered]@{
        Naam    = $u.Naam
        UPN     = $u.UserPrincipalName
        Functie = $u.Functie
    }
    foreach ($groupName in $groupNames) {
        $row[$groupName] = if ($groupMembers[$groupName].ContainsKey($u.Id)) { 1 } else { 0 }
    }
    [PSCustomObject]$row
}

# ============================================================
# STAP 5: wegschrijven naar CSV
# ============================================================
$result | Sort-Object Naam | Export-Csv -Path $outputPath -NoTypeInformation -Encoding UTF8 -Delimiter ";"

Write-Host "`nKlaar! CSV weggeschreven naar: $outputPath" -ForegroundColor Green

# ============================================================
# OPKUISEN
# ============================================================
#Disconnect-ExchangeOnline -Confirm:$false
#Disconnect-MgGraph | Out-Null