<#
.SYNOPSIS
    Maakt een alleen-lezen inventaris van on-prem AD, Entra ID (Microsoft 365) en Exchange Online.

.DESCRIPTION
    Dit script wijzigt NIETS. Het leest uit en schrijft bestanden naar een lokale map.
    Doel: de structuur van de omgeving leren kennen (OU-boom, naamgeving van gebruikers en groepen,
    ingevulde attributen, licenties, rooms) zodat de beheer-app patronen kan herkennen en voorstellen
    kan doen voor nieuwe gebruikers en groepen.

    Elk onderdeel (AD, Entra, Exchange) draait in een EIGEN PowerShell-proces. Microsoft.Graph en
    ExchangeOnlineManagement gebruiken allebei MSAL/WAM en storen elkaar in hetzelfde proces
    (zie ook CopyGroupsUser1TOUser2).

    Uitvoer (map Inventaris-<datum>-<uur> + zip):
      00-samenvatting.md        Patronen, aantallen en OU-boom. Geen namen van personen. DEEL DIT EERST.
      ad-*.csv / entra-*.csv / exo-*.csv / *.json   Detail per bron.

    Met -ZonderPersonen worden gebruikers- en ledenlijsten, beheerdersleden en postvakrechten NIET
    geexporteerd. Dan blijven alleen structuur, aantallen en patronen over.

.PARAMETER OutputPath
    Basismap voor de uitvoer. Standaard %LOCALAPPDATA%\Moss365\Inventaris (lokaal, niet OneDrive).

.PARAMETER Onderdelen
    Welke bronnen: AD, Entra, Exchange. Standaard alle drie.

.PARAMETER ZonderPersonen
    Laat alle persoonsgegevens weg (zie hierboven).

.PARAMETER AdServer
    Naam van een domeincontroller, bv. dc01.contoso.local. Verplicht als deze pc niet in het domein zit
    (bv. enkel Entra-joined). Vereist netwerkverbinding met de DC (LAN of VPN), naamresolutie en poort 9389 (ADWS).

.PARAMETER AdAanmelden
    Vraagt een AD-account (leesrechten volstaan, bv. CONTOSO\naam of naam@contoso.com) en gebruikt dat voor
    alle AD-opdrachten. Het wachtwoord wordt niet opgeslagen.

.NOTES
    Vereist (per onderdeel; ontbrekende modules worden gemeld en overgeslagen):
      AD       RSAT: ActiveDirectory-module, op een domein-pc, leesrechten volstaan
      Entra    Microsoft.Graph.Authentication, .Users, .Groups, .Identity.DirectoryManagement
               Scopes (alleen lezen): User.Read.All, Group.Read.All, GroupMember.Read.All,
               Directory.Read.All, RoleManagement.Read.Directory
      Exchange ExchangeOnlineManagement, minstens de rol View-Only Recipients/Configuration

    De uitvoer bevat (tenzij -ZonderPersonen) persoonsgegevens van medewerkers. Bewaar de map lokaal
    en zet hem NOOIT in git of OneDrive/SharePoint-mappen die gedeeld worden.

.EXAMPLE
    .\Export-Inventaris.ps1

.EXAMPLE
    .\Export-Inventaris.ps1 -Onderdelen AD,Entra -ZonderPersonen

.EXAMPLE
    .\Export-Inventaris.ps1 -AdServer dc01.contoso.local -AdAanmelden   # pc zonder domein
#>

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $env:LOCALAPPDATA 'Moss365\Inventaris'),

    [ValidateSet('AD', 'Entra', 'Exchange')]
    [string[]]$Onderdelen = @('AD', 'Entra', 'Exchange'),

    [switch]$ZonderPersonen,

    # AD vanaf een pc die NIET in het domein zit: naam van een domeincontroller (bv. dc01.contoso.local)
    [string]$AdServer,

    # Vraagt een AD-account met leesrechten en gebruikt dat voor alle AD-opdrachten (nodig buiten het domein)
    [switch]$AdAanmelden,

    # Intern: gebruikt door het hoofdproces om een onderdeel in een apart proces te laten lopen
    [ValidateSet('AD', 'Entra', 'Exchange')]
    [string]$Collector,

    # Intern: de uitvoermap die het hoofdproces heeft aangemaakt
    [string]$OutputMap
)

$ErrorActionPreference = 'Stop'

# ============================================================
# HULPFUNCTIES (puur, zonder verbinding; getest in tests\)
# ============================================================

function Join-Waarden {
    # Maakt van een lijst met waarden 1 tekst, gescheiden door |
    param($Waarde)
    if ($null -eq $Waarde) { return '' }
    if (($Waarde -is [datetime]) -or ($Waarde -is [datetimeoffset])) { return (Format-Datum $Waarde) }
    if (($Waarde -is [System.Collections.IEnumerable]) -and ($Waarde -isnot [string])) {
        return (@($Waarde | ForEach-Object { "$_" }) -join '|')
    }
    return "$Waarde"
}

function ConvertTo-Patroon {
    # 'Anna.Smith@contoso.com' -> 'Aa.Aa@contoso.com'; letters en cijfers worden vormen, geen namen.
    param(
        [AllowNull()][AllowEmptyString()][string]$Waarde,
        [switch]$BehoudDomein
    )
    if ([string]::IsNullOrWhiteSpace($Waarde)) { return '(leeg)' }
    $tekst = $Waarde.Trim()
    $domein = $null
    if ($BehoudDomein -and ($tekst -match '^(?<lokaal>[^@]+)@(?<domein>.+)$')) {
        $tekst = $Matches['lokaal']
        $domein = $Matches['domein'].ToLowerInvariant()
    }
    $tekst = [regex]::Replace($tekst, '\p{Lu}', 'A')
    $tekst = [regex]::Replace($tekst, '\p{Ll}', 'a')
    $tekst = [regex]::Replace($tekst, '\p{Nd}', '9')
    $tekst = [regex]::Replace($tekst, '(.)\1+', '$1')
    if ($domein) { $tekst = "$tekst@$domein" }
    return $tekst
}

function Get-TopPatronen {
    param($Waarden, [int]$Top = 12, [switch]$BehoudDomein)
    $lijst = @($Waarden | ForEach-Object { ConvertTo-Patroon -Waarde $_ -BehoudDomein:$BehoudDomein })
    if ($lijst.Count -eq 0) { return @() }
    return @($lijst | Group-Object | Sort-Object -Property Count -Descending | Select-Object -First $Top |
            ForEach-Object { [pscustomobject]@{ Patroon = $_.Name; Aantal = $_.Count } })
}

function Get-GroepPrefix {
    # 'SG-SM-info-Full' -> 'SG' (Aantal 1) of 'SG-SM' (Aantal 2)
    param([string]$Naam, [int]$Aantal = 1)
    if ([string]::IsNullOrWhiteSpace($Naam)) { return '(leeg)' }
    $delen = @($Naam -split '[-_ ]' | Where-Object { $_ -ne '' })
    if ($delen.Count -le $Aantal) { return '(geen prefix)' }
    return (($delen | Select-Object -First $Aantal) -join '-')
}

function Get-TopPrefixen {
    param($Namen, [int]$Aantal = 1, [int]$Top = 15)
    $lijst = @($Namen | ForEach-Object { Get-GroepPrefix -Naam $_ -Aantal $Aantal })
    if ($lijst.Count -eq 0) { return @() }
    return @($lijst | Group-Object | Sort-Object -Property Count -Descending | Select-Object -First $Top |
            ForEach-Object { [pscustomobject]@{ Prefix = $_.Name; Aantal = $_.Count } })
}

function Get-VullingStatistiek {
    # Hoeveel objecten hebben een waarde in elk attribuut?
    param($Objecten, [string[]]$Attributen)
    $lijst = @($Objecten)
    $totaal = $lijst.Count
    foreach ($attribuut in $Attributen) {
        $ingevuld = 0
        foreach ($o in $lijst) {
            $v = Join-Waarden $o.$attribuut
            if ($v -ne '') { $ingevuld++ }
        }
        $pct = 0
        if ($totaal -gt 0) { $pct = [int][math]::Round(100.0 * $ingevuld / $totaal, 0) }
        [pscustomobject]@{ Attribuut = $attribuut; Ingevuld = $ingevuld; Percentage = $pct }
    }
}

function Get-OuPad {
    # 'CN=Jan,OU=Technische Dienst,OU=Personeel,DC=x,DC=be' -> 'Personeel/Technische Dienst/Jan'
    param([string]$DistinguishedName)
    $delen = @($DistinguishedName -split '(?<!\\),')
    $namen = @($delen | Where-Object { $_ -match '^(OU|CN)=' } |
            ForEach-Object { (($_ -replace '^(OU|CN)=', '') -replace '\\,', ',') })
    [array]::Reverse($namen)
    return ($namen -join '/')
}

function Get-ParentDN {
    param([string]$DistinguishedName)
    $delen = @($DistinguishedName -split '(?<!\\),', 2)
    if ($delen.Count -lt 2) { return '' }
    return $delen[1]
}

function Get-AgendaRechtenTekst {
    # Zonder personen: alleen de standaardinstellingen (Default/Anonymous) bij naam, de rest als aantal
    param($Rechten, [bool]$MetPersonen)
    $lijst = @($Rechten)
    if ($MetPersonen) { return (($lijst | ForEach-Object { "$($_.User)=$($_.AccessRights)" }) -join '|') }
    $standaard = @($lijst | Where-Object { $_.User -in 'Default', 'Standaard', 'Anonymous', 'Anoniem' })
    $overig = $lijst.Count - $standaard.Count
    $delen = @($standaard | ForEach-Object { "$($_.User)=$($_.AccessRights)" })
    if ($overig -gt 0) { $delen += "($overig anderen)" }
    return ($delen -join '|')
}

function Get-AdArgs {
    # Gedeelde parameters voor alle AD-cmdlets: expliciete domeincontroller en/of referenties
    param([string]$Server, $Credential)
    $a = @{}
    if ($Server) { $a['Server'] = $Server }
    if ($Credential) { $a['Credential'] = $Credential }
    return $a
}

function Format-Datum {
    # Datums altijd als yyyy-MM-dd HH:mm:ss, onafhankelijk van de regio-instelling van de pc
    param($Waarde)
    if ($null -eq $Waarde) { return '' }
    $cultuur = [System.Globalization.CultureInfo]::InvariantCulture
    if (($Waarde -is [datetime]) -or ($Waarde -is [datetimeoffset])) { return $Waarde.ToString('yyyy-MM-dd HH:mm:ss', $cultuur) }
    return "$Waarde"
}

function Get-MaxDatum {
    param($A, $B)
    if ($null -eq $A) { return $B }
    if ($null -eq $B) { return $A }
    if ($A -gt $B) { return $A }
    return $B
}

function Get-DoorstuurSoort {
    # 'intern' (ontvanger of een eigen domein), 'extern' (ander domein) of '' (geen doorsturen)
    param([string]$SmtpAdres, [string]$Adres, [string[]]$Domeinen)
    if (-not [string]::IsNullOrWhiteSpace($SmtpAdres)) {
        $a = ($SmtpAdres -replace '(?i)^smtp:', '').Trim().ToLowerInvariant()
        $d = ($a -split '@')[-1]
        $eigen = @($Domeinen | ForEach-Object { "$_".ToLowerInvariant() })
        if ($eigen -contains $d) { return 'intern' }
        return 'extern'
    }
    if (-not [string]::IsNullOrWhiteSpace($Adres)) { return 'intern' }
    return ''
}

function Get-GastDomein {
    # Domein van een gastaccount: uit Mail, anders uit de UPN (naam_domein#EXT#@tenant)
    param([string]$Mail, [string]$Upn)
    if ($Mail -match '@(?<d>[^@]+)$') { return $Matches['d'].ToLowerInvariant() }
    if ($Upn -match '^.+_(?<d>[^_#]+)#EXT#') { return $Matches['d'].ToLowerInvariant() }
    return '(onbekend)'
}

function Get-StandaardGroepen {
    # Groepen waarvan minstens $Drempel procent van de opgegeven gebruikers lid is.
    # LedenPerGroep: hashtable groepId -> lijst gebruikers-id's; GroepNamen: hashtable groepId -> naam.
    param($GebruikerIds, $LedenPerGroep, $GroepNamen, [int]$Drempel = 50)
    $ids = @($GebruikerIds)
    if ($ids.Count -eq 0) { return @() }
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($i in $ids) { [void]$set.Add("$i") }
    $uit = foreach ($gid in $LedenPerGroep.Keys) {
        $n = 0
        foreach ($lid in @($LedenPerGroep[$gid])) { if ($set.Contains("$lid")) { $n++ } }
        $pct = [int][math]::Round(100.0 * $n / $ids.Count, 0)
        if ($pct -ge $Drempel) { [pscustomobject]@{ Groep = $GroepNamen[$gid]; Dekking = $pct; Leden = $n } }
    }
    return @($uit | Sort-Object -Property @{ Expression = 'Dekking'; Descending = $true }, 'Groep')
}

function ConvertTo-MdTabel {
    param($Rijen, [string[]]$Kolommen)
    $lijst = @($Rijen)
    if ($lijst.Count -eq 0) { return "_(geen gegevens)_`r`n" }
    $uit = New-Object System.Text.StringBuilder
    [void]$uit.AppendLine('| ' + ($Kolommen -join ' | ') + ' |')
    [void]$uit.AppendLine('|' + (($Kolommen | ForEach-Object { ' --- |' }) -join ''))
    foreach ($r in $lijst) {
        $cellen = foreach ($k in $Kolommen) { (Join-Waarden $r.$k) -replace '\|', '/' }
        [void]$uit.AppendLine('| ' + ($cellen -join ' | ') + ' |')
    }
    return $uit.ToString()
}

function New-SamenvattingMarkdown {
    # Bouwt 00-samenvatting.md uit de *-statistiek.json bestanden. Bevat geen namen van personen.
    param([string]$Map, [bool]$MetPersonen)

    $lees = {
        param($naam)
        $pad = Join-Path $Map $naam
        if (Test-Path -LiteralPath $pad) { return (Get-Content -LiteralPath $pad -Raw -Encoding UTF8 | ConvertFrom-Json) }
        return $null
    }
    $ad = & $lees 'ad-statistiek.json'
    $entra = & $lees 'entra-statistiek.json'
    $exo = & $lees 'exo-statistiek.json'

    $md = New-Object System.Text.StringBuilder
    [void]$md.AppendLine('# Inventaris - samenvatting')
    [void]$md.AppendLine('')
    [void]$md.AppendLine("Gemaakt op $(Get-Date -Format 'yyyy-MM-dd HH:mm'). Modus: $(if ($MetPersonen) { 'volledig (detailbestanden bevatten persoonsgegevens)' } else { 'zonder personen' }).")
    [void]$md.AppendLine('Dit bestand bevat patronen, aantallen en namen van OU''s, groepen en functionele postvakken; geen lijsten met personen.')
    [void]$md.AppendLine('')

    if ($ad) {
        [void]$md.AppendLine('## On-prem AD')
        [void]$md.AppendLine('')
        $d = $ad.Domein
        [void]$md.AppendLine("Domein **$($d.DNSRoot)** (NetBIOS $($d.NetBIOS)), forest $($d.Forest), functioneel niveau $($d.DomainMode), $($d.AantalDCs) domeincontroller(s).")
        [void]$md.AppendLine('')
        $g = $ad.Gebruikers
        [void]$md.AppendLine("Gebruikers: **$($g.Totaal)** (ingeschakeld $($g.Ingeschakeld), uitgeschakeld $($g.Uitgeschakeld)); nooit aangemeld $($g.NooitAangemeld); ingeschakeld maar > 180 dagen niet aangemeld $($g.Inactief180d); wachtwoord verloopt nooit $($g.WachtwoordVerloptNooit).")
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Naamgevingspatronen gebruikers')
        foreach ($sleutel in 'UPN', 'SamAccountName', 'DisplayName', 'EmailAddress') {
            [void]$md.AppendLine('')
            [void]$md.AppendLine("**$sleutel**")
            [void]$md.AppendLine('')
            [void]$md.Append((ConvertTo-MdTabel -Rijen $ad.Patronen.$sleutel -Kolommen 'Patroon', 'Aantal'))
        }
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Ingevulde attributen (gebruikers)')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $ad.Vulling -Kolommen 'Attribuut', 'Ingevuld', 'Percentage'))
        [void]$md.AppendLine('')
        $gr = $ad.Groepen
        [void]$md.AppendLine('### Groepen')
        [void]$md.AppendLine('')
        [void]$md.AppendLine("Totaal **$($gr.Totaal)**: beveiliging $($gr.Security), distributie $($gr.Distribution); global $($gr.Global), domain local $($gr.DomainLocal), universal $($gr.Universal); leeg $($gr.Leeg); zonder beheerder (ManagedBy) $($gr.ZonderBeheerder).")
        [void]$md.AppendLine('')
        [void]$md.AppendLine('Prefix (eerste deel van de naam):')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $ad.GroepPrefix1 -Kolommen 'Prefix', 'Aantal'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine('Prefix (eerste twee delen):')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $ad.GroepPrefix2 -Kolommen 'Prefix', 'Aantal'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Bevoorrechte groepen (aantal leden, recursief)')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $ad.Bevoorrecht -Kolommen 'Groep', 'AantalLeden'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Computers per besturingssysteem')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $ad.Computers -Kolommen 'OS', 'Aantal', 'Actief90d'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### OU-boom (aantal gebruikers / groepen rechtstreeks in de OU)')
        [void]$md.AppendLine('')
        [void]$md.AppendLine('```')
        foreach ($ou in @($ad.OUBoom)) {
            $inspring = '  ' * [int]$ou.Diepte
            $naam = ($ou.Pad -split '/')[-1]
            [void]$md.AppendLine("$inspring$naam  [g=$($ou.Gebruikers) gr=$($ou.Groepen)]")
        }
        [void]$md.AppendLine('```')
        [void]$md.AppendLine('')
    }

    if ($entra) {
        [void]$md.AppendLine('## Entra ID / Microsoft 365')
        [void]$md.AppendLine('')
        $t = $entra.Tenant
        [void]$md.AppendLine("Tenant **$($t.Naam)**; domeinen: $($t.Domeinen); synchronisatie met on-prem AD: $($t.HybridSync); laatste sync: $($t.LaatsteSync).")
        [void]$md.AppendLine('')
        $g = $entra.Gebruikers
        [void]$md.AppendLine("Gebruikers: **$($g.Totaal)** (ingeschakeld $($g.Ingeschakeld), gasten $($g.Gasten)); gesynchroniseerd uit AD $($g.Gesynchroniseerd), cloud-only $($g.CloudOnly); met licentie $($g.MetLicentie): rechtstreeks toegewezen $($g.LicentieDirect), via groep $($g.LicentieViaGroep).")
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Naamgevingspatronen gebruikers')
        foreach ($sleutel in 'UPN', 'DisplayName', 'MailNickname') {
            [void]$md.AppendLine('')
            [void]$md.AppendLine("**$sleutel**")
            [void]$md.AppendLine('')
            [void]$md.Append((ConvertTo-MdTabel -Rijen $entra.Patronen.$sleutel -Kolommen 'Patroon', 'Aantal'))
        }
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Ingevulde attributen (gebruikers)')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $entra.Vulling -Kolommen 'Attribuut', 'Ingevuld', 'Percentage'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Licenties')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $entra.Licenties -Kolommen 'Sku', 'Toegewezen', 'Totaal'))
        [void]$md.AppendLine('')
        $gr = $entra.Groepen
        [void]$md.AppendLine('### Groepen')
        [void]$md.AppendLine('')
        [void]$md.AppendLine("Totaal **$($gr.Totaal)**: Microsoft 365 $($gr.M365), beveiliging $($gr.Beveiliging), mail-beveiliging $($gr.MailBeveiliging), distributie $($gr.Distributie); dynamisch $($gr.Dynamisch); uit AD gesynchroniseerd $($gr.Gesynchroniseerd), cloud-only $($gr.CloudOnly); leeg $($gr.Leeg); met groepslicentie $($gr.MetLicentie).")
        [void]$md.AppendLine('')
        [void]$md.AppendLine('Prefix (eerste deel):')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $entra.GroepPrefix1 -Kolommen 'Prefix', 'Aantal'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine('Prefix (eerste twee delen):')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $entra.GroepPrefix2 -Kolommen 'Prefix', 'Aantal'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Beheerdersrollen (aantal leden)')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $entra.Beheerders -Kolommen 'Rol', 'Aantal'))
        [void]$md.AppendLine('')
        if ($entra.Admins) {
            $ga = $entra.Admins.GlobalAdmins
            [void]$md.AppendLine("Unieke personen met een beheerdersrol: **$($entra.Admins.UniekePersonen)**, waarvan met meer dan 1 rol: $($entra.Admins.MetMeerdereRollen). Global Administrators: **$($ga.Totaal)** (met licentie $($ga.Gelicentieerd), gesynchroniseerd uit AD $($ga.Gesynchroniseerd)).")
            [void]$md.AppendLine('')
        }
        if ($entra.Gasten) {
            $gs = $entra.Gasten
            [void]$md.AppendLine('### Gasten')
            [void]$md.AppendLine('')
            [void]$md.AppendLine("Totaal **$($gs.Totaal)**; zonder aanmelding in de laatste 180 dagen (of nooit): $($gs.Inactief180d).")
            [void]$md.AppendLine('')
            [void]$md.Append((ConvertTo-MdTabel -Rijen $gs.PerJaar -Kolommen 'Jaar', 'Aantal'))
            [void]$md.AppendLine('')
            [void]$md.Append((ConvertTo-MdTabel -Rijen $gs.TopDomeinen -Kolommen 'Domein', 'Aantal'))
            [void]$md.AppendLine('')
        }
        if ($entra.Populaties -and @($entra.Populaties).Count -gt 0) {
            [void]$md.AppendLine('### Populaties (gesynchroniseerde gebruikers per AD-OU)')
            [void]$md.AppendLine('')
            [void]$md.AppendLine('Per OU: naamgeving, bedrijf, licenties (zonder gratis add-ons) en de groepen waarin minstens de helft van de populatie lid is. Dit zijn de kandidaten voor "standaardgroepen" bij een nieuwe gebruiker.')
            [void]$md.AppendLine('')
            foreach ($pop in @($entra.Populaties)) {
                [void]$md.AppendLine("#### $($pop.OU) ($($pop.Aantal) gebruikers, ingeschakeld $($pop.Ingeschakeld))")
                [void]$md.AppendLine('')
                [void]$md.AppendLine('UPN-patroon: ' + ((@($pop.UpnPatronen) | ForEach-Object { "``$($_.Patroon)`` x$($_.Aantal)" }) -join ', '))
                [void]$md.AppendLine('')
                [void]$md.AppendLine('Bedrijf: ' + ((@($pop.Bedrijf) | ForEach-Object { "$($_.Waarde) x$($_.Aantal)" }) -join ', '))
                [void]$md.AppendLine('')
                [void]$md.AppendLine('Licenties: ' + ((@($pop.Licenties) | ForEach-Object { "$($_.Waarde) x$($_.Aantal)" }) -join ', '))
                [void]$md.AppendLine('')
                if (@($pop.StandaardGroepen).Count -gt 0) {
                    [void]$md.AppendLine('Standaardgroepen:')
                    [void]$md.AppendLine('')
                    foreach ($sg in @($pop.StandaardGroepen)) { [void]$md.AppendLine("- $($sg.Groep) ($($sg.Dekking)%)") }
                }
                else { [void]$md.AppendLine('Standaardgroepen: geen groep met minstens 50% dekking.') }
                [void]$md.AppendLine('')
            }
        }
    }

    if ($exo) {
        [void]$md.AppendLine('## Exchange Online')
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Postvakken per type')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $exo.Postvakken -Kolommen 'Type', 'Aantal'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine('**Patroon primair e-mailadres**')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $exo.Patronen.PrimarySmtp -Kolommen 'Patroon', 'Aantal'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine("Postvakken met doorsturen ingesteld: **$($exo.Doorsturen.Aantal)**, waarvan naar een extern domein: **$($exo.Doorsturen.Extern)**.")
        [void]$md.AppendLine('')
        if (@($exo.Doorsturen.PerType).Count -gt 0) {
            [void]$md.Append((ConvertTo-MdTabel -Rijen $exo.Doorsturen.PerType -Kolommen 'Type', 'Aantal'))
            [void]$md.AppendLine('')
        }
        if ($exo.Aanmelding) {
            [void]$md.AppendLine('### Aanmelding bij gedeelde en resource-postvakken')
            [void]$md.AppendLine('')
            if ($exo.Aanmelding.Beschikbaar) {
                [void]$md.AppendLine('Per type: hoeveel van de bijhorende accounts hebben aanmelding geblokkeerd? (aanbevolen: alle)')
                [void]$md.AppendLine('')
                [void]$md.Append((ConvertTo-MdTabel -Rijen $exo.Aanmelding.PerType -Kolommen 'Type', 'Totaal', 'Geblokkeerd'))
            }
            else { [void]$md.AppendLine('_(niet beschikbaar in deze run)_') }
            [void]$md.AppendLine('')
        }
        if ($exo.GedeeldeRechten) {
            $m = $exo.GedeeldeRechten.Model
            [void]$md.AppendLine('### Rechten op gedeelde postvakken')
            [void]$md.AppendLine('')
            [void]$md.AppendLine("Rechtenmodel per gedeeld postvak: alleen via groep **$($m.AlleenGroep)**, groep en direct **$($m.GroepEnDirect)**, alleen direct **$($m.AlleenDirect)**, geen expliciete rechten $($m.GeenRechten).")
            [void]$md.AppendLine('')
            [void]$md.Append((ConvertTo-MdTabel -Rijen $exo.GedeeldeRechten.PerRecht -Kolommen 'Recht', 'Groep', 'Direct'))
            [void]$md.AppendLine('')
        }
        [void]$md.AppendLine('### Gedeelde postvakken')
        [void]$md.AppendLine('')
        [void]$md.AppendLine((@($exo.GedeeldePostvakken) -join ', '))
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Rooms')
        [void]$md.AppendLine('')
        [void]$md.AppendLine("Aantal rooms: **$($exo.Rooms.Aantal)**.")
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $exo.Rooms.PerGebouw -Kolommen 'Gebouw', 'Aantal'))
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $exo.Rooms.PerAutomateProcessing -Kolommen 'AutomateProcessing', 'Aantal'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Room lists')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $exo.RoomLists -Kolommen 'Naam', 'Leden'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Distributie- en mail-beveiligingsgroepen')
        [void]$md.AppendLine('')
        [void]$md.Append((ConvertTo-MdTabel -Rijen $exo.Groepen.PerType -Kolommen 'Type', 'Aantal'))
        [void]$md.AppendLine('')
        [void]$md.AppendLine("Transportregels: **$($exo.Transportregels.Aantal)** (ingeschakeld $($exo.Transportregels.Ingeschakeld)). Geaccepteerde domeinen: $(@($exo.Domeinen) -join ', ').")
        [void]$md.AppendLine('')
    }

    return $md.ToString()
}

# Bij dot-sourcen (tests) stoppen we hier: alleen de functies zijn nodig.
if ($MyInvocation.InvocationName -eq '.') { return }

# ============================================================
# UITVOER-HULPEN (gebruiken $OutputMap)
# ============================================================

function Save-Csv {
    param($Data, [string]$Naam)
    $pad = Join-Path $OutputMap $Naam
    $lijst = @($Data)
    if ($lijst.Count -eq 0) {
        Set-Content -LiteralPath $pad -Value '' -Encoding UTF8
    }
    else {
        $rijen = foreach ($r in $lijst) {
            $h = [ordered]@{}
            foreach ($p in $r.PSObject.Properties) {
                $v = $p.Value
                if (($v -is [datetime]) -or ($v -is [datetimeoffset])) { $v = Format-Datum $v }
                $h[$p.Name] = $v
            }
            [pscustomobject]$h
        }
        $rijen | Export-Csv -LiteralPath $pad -NoTypeInformation -Delimiter ';' -Encoding UTF8
    }
    Write-Host ("  -> {0} ({1} rijen)" -f $Naam, $lijst.Count)
}

function Save-Json {
    param($Data, [string]$Naam, [int]$Diepte = 6)
    $pad = Join-Path $OutputMap $Naam
    $json = $Data | ConvertTo-Json -Depth $Diepte -WarningAction SilentlyContinue
    Set-Content -LiteralPath $pad -Value $json -Encoding UTF8
    Write-Host "  -> $Naam"
}

function Test-Module {
    param([string[]]$Namen)
    $ontbreekt = @($Namen | Where-Object { -not (Get-Module -ListAvailable -Name $_) })
    if ($ontbreekt.Count -gt 0) {
        Write-Warning ("Ontbrekende module(s): {0}. Installeer met: Install-Module {1} -Scope CurrentUser" -f ($ontbreekt -join ', '), ($ontbreekt[0]))
        return $false
    }
    return $true
}

# ============================================================
# COLLECTOR: AD
# ============================================================

function Invoke-ADCollector {
    if (-not (Test-Module 'ActiveDirectory')) { throw 'ActiveDirectory-module ontbreekt (installeer RSAT: Active Directory-hulpprogramma''s).' }
    # De waarschuwing over het 'standaardstation' (AD:-schijf) is onschuldig buiten het domein en wordt verborgen
    Import-Module ActiveDirectory -ErrorAction Stop -WarningAction SilentlyContinue

    # Op een pc buiten het domein (bv. enkel Entra-joined) is een expliciete DC en een AD-account nodig
    $credential = $null
    if ($AdAanmelden) { $credential = Get-Credential -Message 'AD-account met leesrechten (bv. CONTOSO\naam of naam@contoso.com)' }
    $inDomein = $true
    try { $inDomein = [bool](Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop).PartOfDomain } catch { }
    if ((-not $inDomein) -and (-not $AdServer)) {
        throw 'Deze pc zit niet in een domein. Start opnieuw met: -AdServer <domeincontroller> -AdAanmelden (bv. -AdServer dc01.contoso.local -AdAanmelden).'
    }
    $adArgs = Get-AdArgs -Server $AdServer -Credential $credential

    Write-Host 'AD: domein en domeincontrollers...'
    $domein = Get-ADDomain @adArgs
    $dcs = @(Get-ADDomainController -Filter * @adArgs)

    Write-Host 'AD: OU''s, gebruikers, groepen, computers (dit kan even duren)...'
    $ous = @(Get-ADOrganizationalUnit -Filter * -Properties Description, gPLink, ProtectedFromAccidentalDeletion, whenCreated @adArgs)
    $basisAttr = @('SamAccountName', 'UserPrincipalName', 'DisplayName', 'GivenName', 'Surname', 'Title', 'Department',
        'Company', 'Office', 'City', 'Description', 'EmailAddress', 'EmployeeID', 'EmployeeNumber', 'Manager', 'Enabled',
        'DistinguishedName', 'whenCreated', 'LastLogonDate', 'PasswordLastSet', 'PasswordNeverExpires', 'LockedOut',
        'HomeDirectory', 'ScriptPath', 'ProfilePath', 'proxyAddresses', 'MemberOf', 'OfficePhone', 'MobilePhone')
    # extensionAttribute1-15 komen uit het Exchange-schema. Ontbreekt dat (geen on-prem Exchange), dan weigert AD de
    # hele query met "Een of meer eigenschappen zijn ongeldig". Eerst testen dus, en anders zonder deze kenmerken.
    $extAttr = @(1..15 | ForEach-Object { "extensionAttribute$_" })
    try { [void]@(Get-ADUser -Filter * -ResultSetSize 1 -Properties $extAttr -ErrorAction Stop @adArgs) }
    catch {
        Write-Warning 'extensionAttribute1-15 bestaan niet in het AD-schema (geen Exchange-schema). Verder zonder deze kenmerken.'
        $extAttr = @()
    }
    $gebruikerAttr = $basisAttr + $extAttr
    $users = @(Get-ADUser -Filter * -Properties $gebruikerAttr @adArgs)
    $groepen = @(Get-ADGroup -Filter * -Properties Description, ManagedBy, mail, member, whenCreated, info @adArgs)
    $computers = @(Get-ADComputer -Filter * -Properties OperatingSystem, LastLogonDate @adArgs)

    # --- Statistiek (geen persoonsnamen) ---
    $grens = (Get-Date).AddDays(-180)
    $ingeschakeld = @($users | Where-Object { $_.Enabled })
    $stat = [ordered]@{}
    $stat.Domein = [ordered]@{
        DNSRoot = $domein.DNSRoot; NetBIOS = $domein.NetBIOSName; Forest = $domein.Forest
        DomainMode = "$($domein.DomainMode)"; AantalDCs = $dcs.Count
    }
    $stat.Gebruikers = [ordered]@{
        Totaal                = $users.Count
        Ingeschakeld          = $ingeschakeld.Count
        Uitgeschakeld         = $users.Count - $ingeschakeld.Count
        NooitAangemeld        = @($ingeschakeld | Where-Object { -not $_.LastLogonDate }).Count
        Inactief180d          = @($ingeschakeld | Where-Object { $_.LastLogonDate -and $_.LastLogonDate -lt $grens }).Count
        WachtwoordVerloptNooit = @($ingeschakeld | Where-Object { $_.PasswordNeverExpires }).Count
    }
    $stat.Patronen = [ordered]@{
        UPN            = @(Get-TopPatronen ($users | ForEach-Object { $_.UserPrincipalName }) -BehoudDomein)
        SamAccountName = @(Get-TopPatronen ($users | ForEach-Object { $_.SamAccountName }))
        DisplayName    = @(Get-TopPatronen ($users | ForEach-Object { $_.DisplayName }))
        EmailAddress   = @(Get-TopPatronen ($users | ForEach-Object { $_.EmailAddress }) -BehoudDomein)
    }
    $vulAttr = @('UserPrincipalName', 'DisplayName', 'GivenName', 'Surname', 'Title', 'Department', 'Company', 'Office',
        'Description', 'EmailAddress', 'EmployeeID', 'EmployeeNumber', 'Manager', 'HomeDirectory', 'ScriptPath', 'ProfilePath') + $extAttr
    $stat.Vulling = @(Get-VullingStatistiek -Objecten $ingeschakeld -Attributen $vulAttr)

    $stat.Groepen = [ordered]@{
        Totaal          = $groepen.Count
        Security        = @($groepen | Where-Object { "$($_.GroupCategory)" -eq 'Security' }).Count
        Distribution    = @($groepen | Where-Object { "$($_.GroupCategory)" -eq 'Distribution' }).Count
        Global          = @($groepen | Where-Object { "$($_.GroupScope)" -eq 'Global' }).Count
        DomainLocal     = @($groepen | Where-Object { "$($_.GroupScope)" -eq 'DomainLocal' }).Count
        Universal       = @($groepen | Where-Object { "$($_.GroupScope)" -eq 'Universal' }).Count
        Leeg            = @($groepen | Where-Object { @($_.member).Count -eq 0 }).Count
        ZonderBeheerder = @($groepen | Where-Object { -not $_.ManagedBy }).Count
    }
    $stat.GroepPrefix1 = @(Get-TopPrefixen -Namen ($groepen | ForEach-Object { $_.Name }) -Aantal 1)
    $stat.GroepPrefix2 = @(Get-TopPrefixen -Namen ($groepen | ForEach-Object { $_.Name }) -Aantal 2)
    $grens90 = (Get-Date).AddDays(-90)
    $stat.Computers = @($computers | Group-Object { if ($_.OperatingSystem) { $_.OperatingSystem } else { '(onbekend)' } } |
            Sort-Object Count -Descending | ForEach-Object {
                [pscustomobject]@{
                    OS        = $_.Name
                    Aantal    = $_.Count
                    Actief90d = @($_.Group | Where-Object { $_.Enabled -and $_.LastLogonDate -and $_.LastLogonDate -ge $grens90 }).Count
                }
            })

    # OU-boom met aantallen rechtstreeks in de OU of container
    $gebrPerOu = @{}
    foreach ($g in ($users | Group-Object { Get-ParentDN $_.DistinguishedName })) { $gebrPerOu[$g.Name] = $g.Count }
    $grpPerOu = @{}
    foreach ($g in ($groepen | Group-Object { Get-ParentDN $_.DistinguishedName })) { $grpPerOu[$g.Name] = $g.Count }
    $dns = @($ous | ForEach-Object { $_.DistinguishedName }) + @($gebrPerOu.Keys) + @($grpPerOu.Keys) | Sort-Object -Unique
    $domeinDN = $domein.DistinguishedName
    $boom = foreach ($dn in $dns) {
        if ($dn -eq $domeinDN -or [string]::IsNullOrEmpty($dn)) { continue }
        $pad = Get-OuPad $dn
        [pscustomobject]@{
            Pad        = $pad
            Diepte     = @($pad -split '/').Count - 1
            Gebruikers = [int]$gebrPerOu[$dn]
            Groepen    = [int]$grpPerOu[$dn]
        }
    }
    $stat.OUBoom = @($boom | Sort-Object Pad)

    # Bevoorrechte groepen: lookup op SID zodat taal van het domein niet uitmaakt
    $sidBasis = $domein.DomainSID.Value
    $bevoorrechtDef = @(
        @{ Naam = 'Domain Admins'; Sid = "$sidBasis-512" }
        @{ Naam = 'Schema Admins'; Sid = "$sidBasis-518" }
        @{ Naam = 'Enterprise Admins'; Sid = "$sidBasis-519" }
        @{ Naam = 'Administrators (builtin)'; Sid = 'S-1-5-32-544' }
        @{ Naam = 'Account Operators'; Sid = 'S-1-5-32-548' }
        @{ Naam = 'Server Operators'; Sid = 'S-1-5-32-549' }
        @{ Naam = 'Print Operators'; Sid = 'S-1-5-32-550' }
        @{ Naam = 'Backup Operators'; Sid = 'S-1-5-32-551' }
    )
    $bevoorrechtTel = @()
    $bevoorrechtLeden = @()
    foreach ($def in $bevoorrechtDef) {
        try {
            $grp = Get-ADGroup -Identity $def.Sid -ErrorAction Stop @adArgs
            $leden = @(Get-ADGroupMember -Identity $grp -Recursive -ErrorAction Stop @adArgs)
            $bevoorrechtTel += [pscustomobject]@{ Groep = $def.Naam; AantalLeden = $leden.Count }
            foreach ($l in $leden) {
                $bevoorrechtLeden += [pscustomobject]@{ Groep = $def.Naam; Lid = $l.SamAccountName; Type = $l.objectClass; LidDN = $l.distinguishedName }
            }
        }
        catch {
            $bevoorrechtTel += [pscustomobject]@{ Groep = $def.Naam; AantalLeden = 'niet leesbaar' }
        }
    }
    $stat.Bevoorrecht = @($bevoorrechtTel)
    Save-Json $stat 'ad-statistiek.json'

    # --- Structuur (geen personen) ---
    Save-Csv ($ous | ForEach-Object {
            [pscustomobject]@{
                Pad = Get-OuPad $_.DistinguishedName; DistinguishedName = $_.DistinguishedName; Description = $_.Description
                BeschermdTegenVerwijderen = $_.ProtectedFromAccidentalDeletion; AantalGPOLinks = ([regex]::Matches("$($_.gPLink)", '\[LDAP://')).Count
                WhenCreated = $_.whenCreated
            } }) 'ad-ous.csv'
    Save-Csv ($groepen | ForEach-Object {
            [pscustomobject]@{
                Name = $_.Name; SamAccountName = $_.SamAccountName; DistinguishedName = $_.DistinguishedName
                OU = Get-ParentDN $_.DistinguishedName; Category = "$($_.GroupCategory)"; Scope = "$($_.GroupScope)"
                Description = $_.Description; Mail = $_.mail; AantalLeden = @($_.member).Count
                ManagedBy = $(if ($ZonderPersonen) { if ($_.ManagedBy) { 'ja' } else { 'nee' } } else { $_.ManagedBy })
                WhenCreated = $_.whenCreated
            } }) 'ad-groepen.csv'

    # --- Personen (alleen in volledige modus) ---
    if (-not $ZonderPersonen) {
        Save-Csv ($users | ForEach-Object {
                $u = $_
                $rij = [ordered]@{ OU = Get-ParentDN $u.DistinguishedName }
                foreach ($a in $gebruikerAttr) { $rij[$a] = Join-Waarden $u.$a }
                [pscustomobject]$rij
            }) 'ad-gebruikers.csv'
        $ledenRijen = foreach ($g in $groepen) {
            foreach ($lid in @($g.member)) { [pscustomobject]@{ Groep = $g.Name; GroepDN = $g.DistinguishedName; LidDN = $lid } }
        }
        Save-Csv $ledenRijen 'ad-groepsleden.csv'
        Save-Csv $bevoorrechtLeden 'ad-bevoorrechte-leden.csv'
    }
}

# ============================================================
# COLLECTOR: ENTRA
# ============================================================

function Invoke-EntraCollector {
    $nodig = @('Microsoft.Graph.Authentication', 'Microsoft.Graph.Users', 'Microsoft.Graph.Groups', 'Microsoft.Graph.Identity.DirectoryManagement')
    if (-not (Test-Module $nodig)) { throw 'Microsoft Graph-modules ontbreken.' }
    Import-Module Microsoft.Graph.Authentication, Microsoft.Graph.Users, Microsoft.Graph.Groups, Microsoft.Graph.Identity.DirectoryManagement -ErrorAction Stop

    Write-Host 'Entra: aanmelden bij Microsoft Graph (alleen-lezen scopes)...'
    Connect-MgGraph -Scopes 'User.Read.All', 'Group.Read.All', 'GroupMember.Read.All', 'Directory.Read.All', 'RoleManagement.Read.Directory', 'AuditLog.Read.All' -NoWelcome

    $org = Get-MgOrganization | Select-Object -First 1
    $skus = @(Get-MgSubscribedSku -All)
    $skuNaam = @{}
    foreach ($s in $skus) { $skuNaam["$($s.SkuId)"] = $s.SkuPartNumber }

    Write-Host 'Entra: gebruikers...'
    $userProps = @('id', 'userPrincipalName', 'displayName', 'givenName', 'surname', 'mail', 'mailNickname', 'accountEnabled',
        'userType', 'department', 'jobTitle', 'officeLocation', 'city', 'companyName', 'employeeId', 'createdDateTime', 'usageLocation',
        'onPremisesSyncEnabled', 'onPremisesSamAccountName', 'onPremisesDistinguishedName', 'onPremisesLastSyncDateTime',
        'assignedLicenses', 'licenseAssignmentStates', 'businessPhones', 'mobilePhone')
    # Laatste aanmelding (signInActivity) vraagt AuditLog.Read.All en Entra ID P1; zo niet, dan gaan we verder zonder
    $heeftAanmeldData = $true
    try { $users = @(Get-MgUser -All -Property ($userProps + 'signInActivity') -ErrorAction Stop) }
    catch {
        Write-Warning "Laatste aanmelding niet beschikbaar ($($_.Exception.Message)). Verder zonder."
        $heeftAanmeldData = $false
        $users = @(Get-MgUser -All -Property $userProps)
    }

    Write-Host 'Entra: groepen...'
    $groepProps = @('id', 'displayName', 'description', 'mail', 'mailNickname', 'mailEnabled', 'securityEnabled', 'groupTypes',
        'membershipRule', 'membershipRuleProcessingState', 'onPremisesSyncEnabled', 'onPremisesSamAccountName',
        'onPremisesLastSyncDateTime', 'createdDateTime', 'visibility', 'assignedLicenses')
    $groepen = @(Get-MgGroup -All -Property $groepProps)

    $groepRij = foreach ($g in $groepen) {
        $types = @($g.GroupTypes)
        $type = 'Beveiliging'
        if ($types -contains 'Unified') { $type = 'M365' }
        elseif ($g.MailEnabled -and $g.SecurityEnabled) { $type = 'MailBeveiliging' }
        elseif ($g.MailEnabled) { $type = 'Distributie' }
        $bron = 'Cloud'
        if ($g.OnPremisesSyncEnabled) { $bron = 'AD' }
        [pscustomobject]@{
            Id = $g.Id; DisplayName = $g.DisplayName; Description = $g.Description; Mail = $g.Mail; MailNickname = $g.MailNickname
            Type = $type; Bron = $bron; Dynamisch = ($types -contains 'DynamicMembership'); Lidmaatschapsregel = $g.MembershipRule
            OnPremisesSamAccountName = $g.OnPremisesSamAccountName; Zichtbaarheid = $g.Visibility
            GroepLicentie = (@($g.AssignedLicenses).Count -gt 0); CreatedDateTime = $g.CreatedDateTime
        }
    }

    # Ledentellingen (en leden, enkel in volledige modus)
    Write-Host 'Entra: groepsleden (1 aanroep per groep)...'
    $ledenRijen = @()
    $ledenTelling = @{}
    $ledenPerGroep = @{}
    $teller = 0
    foreach ($g in $groepen) {
        $teller++
        if (($teller % 50) -eq 0) { Write-Host "  $teller / $($groepen.Count)" }
        try {
            $leden = @(Get-MgGroupMember -GroupId $g.Id -All -ErrorAction Stop)
            $ledenTelling[$g.Id] = $leden.Count
            $ledenPerGroep[$g.Id] = @($leden | Where-Object { "$($_.AdditionalProperties['@odata.type'])" -eq '#microsoft.graph.user' } | ForEach-Object { $_.Id })
            if (-not $ZonderPersonen) {
                foreach ($m in $leden) {
                    $ap = $m.AdditionalProperties
                    $lidType = ("$($ap['@odata.type'])" -replace '#microsoft.graph.', '')
                    $lidNaam = "$($ap['userPrincipalName'])"
                    if (-not $lidNaam) { $lidNaam = "$($ap['displayName'])" }
                    $ledenRijen += [pscustomobject]@{ GroepId = $g.Id; Groep = $g.DisplayName; LidId = $m.Id; LidType = $lidType; Lid = $lidNaam }
                }
            }
        }
        catch { $ledenTelling[$g.Id] = -1 }
    }

    # Beheerdersrollen (aantal; namen enkel in volledige modus)
    Write-Host 'Entra: beheerdersrollen...'
    $rolTelling = @()
    $rolLeden = @()
    try {
        foreach ($r in @(Get-MgDirectoryRole -All)) {
            $leden = @(Get-MgDirectoryRoleMember -DirectoryRoleId $r.Id -All)
            $rolTelling += [pscustomobject]@{ Rol = $r.DisplayName; Aantal = $leden.Count }
            foreach ($m in $leden) {
                $ap = $m.AdditionalProperties
                $naam = "$($ap['userPrincipalName'])"
                if (-not $naam) { $naam = "$($ap['displayName'])" }
                $rolLeden += [pscustomobject]@{ Rol = $r.DisplayName; LidId = $m.Id; LidType = ("$($ap['@odata.type'])" -replace '#microsoft.graph.', ''); Lid = $naam }
            }
        }
    }
    catch { Write-Warning "Beheerdersrollen niet leesbaar: $($_.Exception.Message)" }

    # --- Statistiek ---
    $ingeschakeld = @($users | Where-Object { $_.AccountEnabled })
    $metLicentie = @($users | Where-Object { @($_.AssignedLicenses).Count -gt 0 })
    $direct = 0; $viaGroep = 0
    foreach ($u in $metLicentie) {
        $staten = @($u.LicenseAssignmentStates)
        if (@($staten | Where-Object { -not $_.AssignedByGroup }).Count -gt 0) { $direct++ }
        if (@($staten | Where-Object { $_.AssignedByGroup }).Count -gt 0) { $viaGroep++ }
    }
    $stat = [ordered]@{}
    $stat.Tenant = [ordered]@{
        Naam = $org.DisplayName; Domeinen = (@($org.VerifiedDomains | ForEach-Object { $_.Name }) -join ', ')
        HybridSync = [bool]$org.OnPremisesSyncEnabled; LaatsteSync = (Format-Datum $org.OnPremisesLastSyncDateTime)
    }
    $stat.Gebruikers = [ordered]@{
        Totaal = $users.Count; Ingeschakeld = $ingeschakeld.Count
        Gasten = @($users | Where-Object { $_.UserType -eq 'Guest' }).Count
        Gesynchroniseerd = @($users | Where-Object { $_.OnPremisesSyncEnabled }).Count
        CloudOnly = @($users | Where-Object { -not $_.OnPremisesSyncEnabled -and $_.UserType -ne 'Guest' }).Count
        MetLicentie = $metLicentie.Count; LicentieDirect = $direct; LicentieViaGroep = $viaGroep
    }
    $stat.Patronen = [ordered]@{
        UPN          = @(Get-TopPatronen ($users | ForEach-Object { $_.UserPrincipalName }) -BehoudDomein)
        DisplayName  = @(Get-TopPatronen ($users | ForEach-Object { $_.DisplayName }))
        MailNickname = @(Get-TopPatronen ($users | ForEach-Object { $_.MailNickname }))
    }
    $vulAttr = @('DisplayName', 'GivenName', 'Surname', 'Mail', 'Department', 'JobTitle', 'OfficeLocation', 'CompanyName', 'EmployeeId', 'UsageLocation')
    $stat.Vulling = @(Get-VullingStatistiek -Objecten $ingeschakeld -Attributen $vulAttr)
    $stat.Licenties = @($skus | ForEach-Object {
            [pscustomobject]@{ Sku = $_.SkuPartNumber; Toegewezen = $_.ConsumedUnits; Totaal = $_.PrepaidUnits.Enabled }
        })
    $stat.Groepen = [ordered]@{
        Totaal = $groepRij.Count
        M365 = @($groepRij | Where-Object { $_.Type -eq 'M365' }).Count
        Beveiliging = @($groepRij | Where-Object { $_.Type -eq 'Beveiliging' }).Count
        MailBeveiliging = @($groepRij | Where-Object { $_.Type -eq 'MailBeveiliging' }).Count
        Distributie = @($groepRij | Where-Object { $_.Type -eq 'Distributie' }).Count
        Dynamisch = @($groepRij | Where-Object { $_.Dynamisch }).Count
        Gesynchroniseerd = @($groepRij | Where-Object { $_.Bron -eq 'AD' }).Count
        CloudOnly = @($groepRij | Where-Object { $_.Bron -eq 'Cloud' }).Count
        Leeg = @($groepRij | Where-Object { $ledenTelling[$_.Id] -eq 0 }).Count
        MetLicentie = @($groepRij | Where-Object { $_.GroepLicentie }).Count
    }
    $stat.GroepPrefix1 = @(Get-TopPrefixen -Namen ($groepen | ForEach-Object { $_.DisplayName }) -Aantal 1)
    $stat.GroepPrefix2 = @(Get-TopPrefixen -Namen ($groepen | ForEach-Object { $_.DisplayName }) -Aantal 2)
    $stat.Beheerders = @($rolTelling | Sort-Object Aantal -Descending)

    # Beheerders: unieke personen en de eigenschappen van de Global Administrators (alleen aantallen)
    $userById = @{}
    foreach ($u in $users) { $userById[$u.Id] = $u }
    $beheerUsers = @($rolLeden | Where-Object { $_.LidType -eq 'user' })
    $uniek = @($beheerUsers | Group-Object LidId)
    $gaLeden = @($beheerUsers | Where-Object { $_.Rol -match '^(Global Administrator|Company Administrator)$' } |
            ForEach-Object { $userById[$_.LidId] } | Where-Object { $_ })
    $stat.Admins = [ordered]@{
        UniekePersonen    = $uniek.Count
        MetMeerdereRollen = @($uniek | Where-Object { $_.Count -gt 1 }).Count
        GlobalAdmins      = [ordered]@{
            Totaal           = $gaLeden.Count
            Gelicentieerd    = @($gaLeden | Where-Object { @($_.AssignedLicenses).Count -gt 0 }).Count
            Gesynchroniseerd = @($gaLeden | Where-Object { $_.OnPremisesSyncEnabled }).Count
        }
    }

    # Gasten
    $gasten = @($users | Where-Object { $_.UserType -eq 'Guest' })
    $grens180 = (Get-Date).AddDays(-180)
    $laatsteVan = { param($u) Get-MaxDatum $u.SignInActivity.LastSignInDateTime $u.SignInActivity.LastNonInteractiveSignInDateTime }
    $stat.Gasten = [ordered]@{
        Totaal      = $gasten.Count
        PerJaar     = @($gasten | Group-Object { if ($_.CreatedDateTime) { $_.CreatedDateTime.Year } else { 0 } } | Sort-Object Name |
                ForEach-Object { [pscustomobject]@{ Jaar = $_.Name; Aantal = $_.Count } })
        TopDomeinen = @($gasten | Group-Object { Get-GastDomein -Mail "$($_.Mail)" -Upn "$($_.UserPrincipalName)" } |
                Sort-Object Count -Descending | Select-Object -First 10 |
                ForEach-Object { [pscustomobject]@{ Domein = $_.Name; Aantal = $_.Count } })
    }
    if ($heeftAanmeldData) {
        $stat.Gasten.Inactief180d = @($gasten | Where-Object { $d = & $laatsteVan $_; (-not $d) -or ($d -lt $grens180) }).Count
        $leden = @($users | Where-Object { $_.UserType -ne 'Guest' -and $_.AccountEnabled })
        $stat.Gebruikers.Inactief180d = @($leden | Where-Object { $d = & $laatsteVan $_; (-not $d) -or ($d -lt $grens180) }).Count
    }
    else {
        $stat.Gasten.Inactief180d = 'niet beschikbaar'
        $stat.Gebruikers.Inactief180d = 'niet beschikbaar'
    }

    # Populaties: gesynchroniseerde gebruikers per AD-OU (afgeleid uit onPremisesDistinguishedName),
    # met naamgeving, licentie en de groepen waarin minstens de helft van de populatie zit
    $groepNaamMap = @{}
    foreach ($g in $groepen) { $groepNaamMap[$g.Id] = $g.DisplayName }
    $gratisSkus = @('FLOW_FREE', 'POWER_BI_STANDARD')
    $gesync = @($users | Where-Object { $_.OnPremisesSyncEnabled -and $_.UserType -ne 'Guest' })
    $populaties = foreach ($pg in @($gesync | Group-Object { if ($_.OnPremisesDistinguishedName) { Get-OuPad (Get-ParentDN $_.OnPremisesDistinguishedName) } else { '(geen OU)' } } | Sort-Object Count -Descending)) {
        if ($pg.Count -lt 5) { continue }
        $groepLeden = @($pg.Group)
        $licCombos = @($groepLeden | ForEach-Object {
                $namen = @($_.AssignedLicenses | ForEach-Object { $skuNaam["$($_.SkuId)"] } | Where-Object { $gratisSkus -notcontains $_ } | Sort-Object)
                if ($namen.Count -gt 0) { $namen -join '+' } else { '(geen)' }
            })
        [pscustomobject]@{
            OU               = $pg.Name
            Aantal           = $groepLeden.Count
            Ingeschakeld     = @($groepLeden | Where-Object { $_.AccountEnabled }).Count
            UpnPatronen      = @(Get-TopPatronen ($groepLeden | ForEach-Object { $_.UserPrincipalName }) -Top 3 -BehoudDomein)
            Bedrijf          = @($groepLeden | Group-Object { if ($_.CompanyName) { $_.CompanyName } else { '(leeg)' } } | Sort-Object Count -Descending |
                    Select-Object -First 3 | ForEach-Object { [pscustomobject]@{ Waarde = $_.Name; Aantal = $_.Count } })
            Licenties        = @($licCombos | Group-Object | Sort-Object Count -Descending | Select-Object -First 3 |
                    ForEach-Object { [pscustomobject]@{ Waarde = $_.Name; Aantal = $_.Count } })
            StandaardGroepen = @(Get-StandaardGroepen -GebruikerIds ($groepLeden | ForEach-Object { $_.Id }) -LedenPerGroep $ledenPerGroep -GroepNamen $groepNaamMap -Drempel 50)
        }
    }
    $stat.Populaties = @($populaties)
    Save-Json $stat 'entra-statistiek.json'

    # --- Structuur (geen personen) ---
    Save-Csv ($groepRij | ForEach-Object {
            $r = $_
            [pscustomobject]@{
                Id = $r.Id; DisplayName = $r.DisplayName; Description = $r.Description; Mail = $r.Mail; MailNickname = $r.MailNickname
                Type = $r.Type; Bron = $r.Bron; Dynamisch = $r.Dynamisch; Lidmaatschapsregel = $r.Lidmaatschapsregel
                OnPremisesSamAccountName = $r.OnPremisesSamAccountName; GroepLicentie = $r.GroepLicentie
                AantalLeden = $ledenTelling[$r.Id]; CreatedDateTime = $r.CreatedDateTime
            } }) 'entra-groepen.csv'
    Save-Csv ($skus | ForEach-Object {
            [pscustomobject]@{ SkuId = "$($_.SkuId)"; SkuPartNumber = $_.SkuPartNumber; Toegewezen = $_.ConsumedUnits; Totaal = $_.PrepaidUnits.Enabled }
        }) 'entra-licenties.csv'

    # --- Personen (alleen in volledige modus) ---
    if (-not $ZonderPersonen) {
        Save-Csv ($users | ForEach-Object {
                $u = $_
                $lic = @($u.AssignedLicenses | ForEach-Object { $skuNaam["$($_.SkuId)"] })
                [pscustomobject]@{
                    Id = $u.Id; UserPrincipalName = $u.UserPrincipalName; DisplayName = $u.DisplayName; GivenName = $u.GivenName
                    Surname = $u.Surname; Mail = $u.Mail; MailNickname = $u.MailNickname; AccountEnabled = $u.AccountEnabled
                    UserType = $u.UserType; Department = $u.Department; JobTitle = $u.JobTitle; OfficeLocation = $u.OfficeLocation; City = $u.City
                    BusinessPhone = "$(@($u.BusinessPhones) | Select-Object -First 1)"; MobilePhone = $u.MobilePhone
                    CompanyName = $u.CompanyName; EmployeeId = $u.EmployeeId; UsageLocation = $u.UsageLocation
                    CreatedDateTime = $u.CreatedDateTime; OnPremisesSyncEnabled = $u.OnPremisesSyncEnabled
                    OnPremisesSamAccountName = $u.OnPremisesSamAccountName; OnPremisesDistinguishedName = $u.OnPremisesDistinguishedName
                    OnPremisesLastSyncDateTime = $u.OnPremisesLastSyncDateTime; Licenties = ($lic -join '|')
                    LaatsteAanmelding = $(if ($heeftAanmeldData) { Format-Datum (& $laatsteVan $u) } else { '' })
                    LicentieDirect = @($u.LicenseAssignmentStates | Where-Object { -not $_.AssignedByGroup }).Count
                    LicentieViaGroep = @($u.LicenseAssignmentStates | Where-Object { $_.AssignedByGroup }).Count
                }
            }) 'entra-gebruikers.csv'
        Save-Csv $ledenRijen 'entra-groepsleden.csv'
        Save-Csv $rolLeden 'entra-beheerders.csv'
    }
}

# ============================================================
# COLLECTOR: EXCHANGE ONLINE
# ============================================================

function Get-AgendaRechten {
    # De agendamap heet 'Calendar' of 'Agenda', afhankelijk van de taal van het postvak
    param([string]$Smtp)
    foreach ($map in 'Calendar', 'Agenda') {
        try {
            return @(Get-MailboxFolderPermission -Identity ("{0}:\{1}" -f $Smtp, $map) -ErrorAction Stop |
                    ForEach-Object { [pscustomobject]@{ User = "$($_.User)"; AccessRights = (@($_.AccessRights) -join ',') } })
        }
        catch { }
    }
    return @()
}

function Invoke-ExchangeCollector {
    if (-not (Test-Module 'ExchangeOnlineManagement')) { throw 'ExchangeOnlineManagement-module ontbreekt.' }
    Import-Module ExchangeOnlineManagement -ErrorAction Stop
    Write-Host 'Exchange: aanmelden bij Exchange Online...'
    Connect-ExchangeOnline -ShowBanner:$false

    Write-Host 'Exchange: postvakken...'
    $props = @('RecipientTypeDetails', 'PrimarySmtpAddress', 'Alias', 'DisplayName', 'HiddenFromAddressListsEnabled',
        'ForwardingSmtpAddress', 'ForwardingAddress', 'DeliverToMailboxAndForward', 'GrantSendOnBehalfTo', 'WhenCreated', 'IsDirSynced')
    # AccountDisabled (aanmelding geblokkeerd) hoort bij Get-Mailbox; lukt het niet via EXO, dan gaan we verder zonder
    $heeftAccountInfo = $true
    try { $mbx = @(Get-EXOMailbox -ResultSize Unlimited -Properties ($props + 'AccountDisabled') -ErrorAction Stop) }
    catch {
        Write-Warning "AccountDisabled niet beschikbaar ($($_.Exception.Message)). Verder zonder."
        $heeftAccountInfo = $false
        $mbx = @(Get-EXOMailbox -ResultSize Unlimited -Properties $props)
    }
    $doorsturen = @($mbx | Where-Object { $_.ForwardingSmtpAddress -or $_.ForwardingAddress })
    $gedeeld = @($mbx | Where-Object { "$($_.RecipientTypeDetails)" -eq 'SharedMailbox' })
    $roomBox = @($mbx | Where-Object { "$($_.RecipientTypeDetails)" -eq 'RoomMailbox' })

    Write-Host "Exchange: $($roomBox.Count) rooms (alle attributen: postvak, agendaverwerking, Place, agendarechten)..."
    $roomDetail = foreach ($r in $roomBox) {
        $smtp = "$($r.PrimarySmtpAddress)"
        $volMailbox = $null; $cal = $null; $place = $null
        try { $volMailbox = Get-Mailbox -Identity $smtp -ErrorAction Stop } catch { Write-Warning "Get-Mailbox $smtp : $($_.Exception.Message)" }
        try { $cal = Get-CalendarProcessing -Identity $smtp -ErrorAction Stop } catch { Write-Warning "Get-CalendarProcessing $smtp : $($_.Exception.Message)" }
        try { $place = Get-Place -Identity $smtp -ErrorAction Stop } catch { }
        [pscustomobject]@{
            Smtp = $smtp; Mailbox = $volMailbox; CalendarProcessing = $cal; Place = $place; Agendarechten = @(Get-AgendaRechten -Smtp $smtp)
        }
    }
    if (-not $ZonderPersonen) { Save-Json $roomDetail 'exo-rooms.json' 5 }
    Save-Csv ($roomDetail | ForEach-Object {
            $d = $_
            [pscustomobject]@{
                Smtp = $d.Smtp; DisplayName = $d.Mailbox.DisplayName; Alias = $d.Mailbox.Alias
                Gebouw = $d.Place.Building; Verdieping = $d.Place.Floor; VerdiepingLabel = $d.Place.FloorLabel; Capaciteit = $d.Place.Capacity
                Hidden = $d.Mailbox.HiddenFromAddressListsEnabled
                AutomateProcessing = "$($d.CalendarProcessing.AutomateProcessing)"; AllBookInPolicy = $d.CalendarProcessing.AllBookInPolicy
                AllRequestInPolicy = $d.CalendarProcessing.AllRequestInPolicy; BookInPolicy = $(if ($ZonderPersonen) { @($d.CalendarProcessing.BookInPolicy).Count } else { Join-Waarden $d.CalendarProcessing.BookInPolicy })
                ResourceDelegates = $(if ($ZonderPersonen) { @($d.CalendarProcessing.ResourceDelegates).Count } else { Join-Waarden $d.CalendarProcessing.ResourceDelegates })
                BookingWindowInDays = $d.CalendarProcessing.BookingWindowInDays; MaximumDurationInMinutes = $d.CalendarProcessing.MaximumDurationInMinutes
                AllowConflicts = $d.CalendarProcessing.AllowConflicts; AllowRecurringMeetings = $d.CalendarProcessing.AllowRecurringMeetings
                AddOrganizerToSubject = $d.CalendarProcessing.AddOrganizerToSubject; DeleteSubject = $d.CalendarProcessing.DeleteSubject
                AgendaRechten = (Get-AgendaRechtenTekst -Rechten $d.Agendarechten -MetPersonen (-not $ZonderPersonen))
            } }) 'exo-rooms-overzicht.csv'

    Write-Host 'Exchange: groepen en room lists...'
    $dgs = @(Get-DistributionGroup -ResultSize Unlimited)
    $ledenTelling = @{}
    $dgLeden = @()
    foreach ($g in $dgs) {
        try {
            $leden = @(Get-DistributionGroupMember -Identity $g.Identity -ResultSize Unlimited -ErrorAction Stop)
            $ledenTelling["$($g.Identity)"] = $leden.Count
            if (-not $ZonderPersonen) {
                foreach ($m in $leden) { $dgLeden += [pscustomobject]@{ Groep = $g.DisplayName; Type = "$($g.RecipientTypeDetails)"; Lid = "$($m.PrimarySmtpAddress)"; LidNaam = $m.DisplayName } }
            }
        }
        catch { $ledenTelling["$($g.Identity)"] = -1 }
    }
    $roomLists = @($dgs | Where-Object { "$($_.RecipientTypeDetails)" -eq 'RoomList' })

    $transport = @()
    try { $transport = @(Get-TransportRule -ResultSize Unlimited) } catch { Write-Warning "Transportregels niet leesbaar: $($_.Exception.Message)" }
    $domeinen = @(Get-AcceptedDomain | ForEach-Object { $_.DomainName })

    # Rechten op gedeelde postvakken; groep of persoon bepalen we aan de hand van de bekende groepen
    Write-Host "Exchange: rechten op $($gedeeld.Count) gedeelde postvakken..."
    $groepSleutels = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($g in $dgs) {
        foreach ($n in @($g.DisplayName, $g.Alias, "$($g.PrimarySmtpAddress)", $g.Name, "$($g.Identity)")) {
            if ($n) { [void]$groepSleutels.Add("$n".ToLowerInvariant()) }
        }
    }
    # Exchange geeft rechthebbenden soms als interne naam (Name) terug; zet die om naar een adres
    $adresCache = @{}
    $adresVan = {
        param([string]$Naam)
        if (-not $Naam -or $Naam -match '@' -or $Naam -like 'S-1-5-*') { return '' }
        if (-not $adresCache.ContainsKey($Naam)) {
            $adresCache[$Naam] = ''
            try { $adresCache[$Naam] = "$((Get-Recipient -Identity $Naam -ErrorAction Stop).PrimarySmtpAddress)" } catch { }
        }
        return $adresCache[$Naam]
    }
    $rechten = foreach ($s in $gedeeld) {
        $smtp = "$($s.PrimarySmtpAddress)"
        try {
            foreach ($p in @(Get-EXOMailboxPermission -Identity $smtp -ErrorAction Stop | Where-Object { -not $_.IsInherited -and $_.User -ne 'NT AUTHORITY\SELF' })) {
                [pscustomobject]@{ Postvak = $smtp; Recht = (@($p.AccessRights) -join ','); Gebruiker = "$($p.User)"; Adres = (& $adresVan "$($p.User)"); IsGroep = $groepSleutels.Contains("$($p.User)".ToLowerInvariant()) }
            }
            foreach ($p in @(Get-EXORecipientPermission -Identity $smtp -ErrorAction Stop | Where-Object { $_.Trustee -ne 'NT AUTHORITY\SELF' })) {
                [pscustomobject]@{ Postvak = $smtp; Recht = (@($p.AccessRights) -join ','); Gebruiker = "$($p.Trustee)"; Adres = (& $adresVan "$($p.Trustee)"); IsGroep = $groepSleutels.Contains("$($p.Trustee)".ToLowerInvariant()) }
            }
        }
        catch { Write-Warning "Rechten op $smtp niet leesbaar: $($_.Exception.Message)" }
    }
    $rechten = @($rechten)
    $rechtenPerPostvak = @{}
    foreach ($r in $rechten) {
        if (-not $rechtenPerPostvak.ContainsKey($r.Postvak)) { $rechtenPerPostvak[$r.Postvak] = @() }
        $rechtenPerPostvak[$r.Postvak] += $r
    }
    $modelTelling = [ordered]@{ AlleenGroep = 0; GroepEnDirect = 0; AlleenDirect = 0; GeenRechten = 0 }
    foreach ($s in $gedeeld) {
        $rs = @($rechtenPerPostvak["$($s.PrimarySmtpAddress)"])
        $metGroep = @($rs | Where-Object { $_.IsGroep }).Count -gt 0
        $metDirect = @($rs | Where-Object { $_ -and -not $_.IsGroep }).Count -gt 0
        if ($metGroep -and $metDirect) { $modelTelling.GroepEnDirect++ }
        elseif ($metGroep) { $modelTelling.AlleenGroep++ }
        elseif ($metDirect) { $modelTelling.AlleenDirect++ }
        else { $modelTelling.GeenRechten++ }
    }

    # --- Statistiek ---
    $stat = [ordered]@{}
    $stat.Postvakken = @($mbx | Group-Object { "$($_.RecipientTypeDetails)" } | Sort-Object Count -Descending |
            ForEach-Object { [pscustomobject]@{ Type = $_.Name; Aantal = $_.Count } })
    $stat.Patronen = [ordered]@{
        PrimarySmtp = @(Get-TopPatronen ($mbx | Where-Object { "$($_.RecipientTypeDetails)" -eq 'UserMailbox' } | ForEach-Object { "$($_.PrimarySmtpAddress)" }) -BehoudDomein)
        Alias       = @(Get-TopPatronen ($mbx | Where-Object { "$($_.RecipientTypeDetails)" -eq 'UserMailbox' } | ForEach-Object { $_.Alias }))
    }
    $stat.Doorsturen = [ordered]@{
        Aantal  = $doorsturen.Count
        Extern  = @($doorsturen | Where-Object { (Get-DoorstuurSoort -SmtpAdres "$($_.ForwardingSmtpAddress)" -Adres "$($_.ForwardingAddress)" -Domeinen $domeinen) -eq 'extern' }).Count
        PerType = @($doorsturen | Group-Object { "$($_.RecipientTypeDetails)" } | ForEach-Object { [pscustomobject]@{ Type = $_.Name; Aantal = $_.Count } })
    }
    $aanmeldTypes = @('SharedMailbox', 'RoomMailbox', 'EquipmentMailbox', 'SchedulingMailbox')
    $stat.Aanmelding = [ordered]@{
        Beschikbaar = $heeftAccountInfo
        PerType     = @($mbx | Where-Object { $aanmeldTypes -contains "$($_.RecipientTypeDetails)" } | Group-Object { "$($_.RecipientTypeDetails)" } |
                ForEach-Object { [pscustomobject]@{ Type = $_.Name; Totaal = $_.Count; Geblokkeerd = @($_.Group | Where-Object { $_.AccountDisabled }).Count } })
    }
    $stat.GedeeldeRechten = [ordered]@{
        Model = $modelTelling
        PerRecht = @($rechten | Group-Object Recht | ForEach-Object {
                [pscustomobject]@{ Recht = $_.Name; Groep = @($_.Group | Where-Object { $_.IsGroep }).Count; Direct = @($_.Group | Where-Object { -not $_.IsGroep }).Count }
            })
    }
    $stat.GedeeldePostvakken = @($gedeeld | ForEach-Object { $_.DisplayName } | Sort-Object)
    $stat.Rooms = [ordered]@{
        Aantal = $roomBox.Count
        PerGebouw = @($roomDetail | Group-Object { if ($_.Place.Building) { $_.Place.Building } else { '(leeg)' } } |
                ForEach-Object { [pscustomobject]@{ Gebouw = $_.Name; Aantal = $_.Count } })
        PerAutomateProcessing = @($roomDetail | Group-Object { "$($_.CalendarProcessing.AutomateProcessing)" } |
                ForEach-Object { [pscustomobject]@{ AutomateProcessing = $_.Name; Aantal = $_.Count } })
    }
    $stat.Groepen = [ordered]@{
        Totaal = $dgs.Count
        PerType = @($dgs | Group-Object { "$($_.RecipientTypeDetails)" } | ForEach-Object { [pscustomobject]@{ Type = $_.Name; Aantal = $_.Count } })
    }
    $stat.RoomLists = @($roomLists | ForEach-Object { [pscustomobject]@{ Naam = $_.DisplayName; Leden = $ledenTelling["$($_.Identity)"] } })
    $stat.Transportregels = [ordered]@{ Aantal = $transport.Count; Ingeschakeld = @($transport | Where-Object { "$($_.State)" -eq 'Enabled' }).Count }
    $stat.Domeinen = $domeinen
    Save-Json $stat 'exo-statistiek.json'

    # --- Structuur (geen personen) ---
    Save-Csv ($mbx | Where-Object { "$($_.RecipientTypeDetails)" -ne 'UserMailbox' } | ForEach-Object {
            [pscustomobject]@{
                Type = "$($_.RecipientTypeDetails)"; DisplayName = $_.DisplayName; Alias = $_.Alias; PrimarySmtpAddress = "$($_.PrimarySmtpAddress)"
                Hidden = $_.HiddenFromAddressListsEnabled; IsDirSynced = $_.IsDirSynced; WhenCreated = $_.WhenCreated
                AanmeldingGeblokkeerd = $(if ($heeftAccountInfo) { $_.AccountDisabled } else { '' })
                Doorsturen = (Get-DoorstuurSoort -SmtpAdres "$($_.ForwardingSmtpAddress)" -Adres "$($_.ForwardingAddress)" -Domeinen $domeinen)
            } }) 'exo-postvakken-niet-gebruikers.csv'
    Save-Csv ($dgs | ForEach-Object {
            [pscustomobject]@{
                Type = "$($_.RecipientTypeDetails)"; DisplayName = $_.DisplayName; Name = $_.Name; Alias = $_.Alias; PrimarySmtpAddress = "$($_.PrimarySmtpAddress)"
                IsDirSynced = $_.IsDirSynced; MemberJoinRestriction = "$($_.MemberJoinRestriction)"
                AantalLeden = $ledenTelling["$($_.Identity)"]; WhenCreated = $_.WhenCreated
                ManagedBy = $(if ($ZonderPersonen) { if (@($_.ManagedBy).Count -gt 0) { 'ja' } else { 'nee' } } else { Join-Waarden $_.ManagedBy })
            } }) 'exo-groepen.csv'
    Save-Csv ($transport | ForEach-Object {
            [pscustomobject]@{ Name = $_.Name; State = "$($_.State)"; Priority = $_.Priority; Mode = "$($_.Mode)"; Comments = $_.Comments }
        }) 'exo-transportregels.csv'

    # --- Personen (alleen in volledige modus) ---
    if (-not $ZonderPersonen) {
        Save-Csv ($mbx | Where-Object { "$($_.RecipientTypeDetails)" -eq 'UserMailbox' } | ForEach-Object {
                [pscustomobject]@{
                    DisplayName = $_.DisplayName; Alias = $_.Alias; PrimarySmtpAddress = "$($_.PrimarySmtpAddress)"
                    Hidden = $_.HiddenFromAddressListsEnabled; ForwardingSmtpAddress = "$($_.ForwardingSmtpAddress)"
                    ForwardingAddress = "$($_.ForwardingAddress)"; DeliverToMailboxAndForward = $_.DeliverToMailboxAndForward
                    GrantSendOnBehalfTo = Join-Waarden $_.GrantSendOnBehalfTo; IsDirSynced = $_.IsDirSynced; WhenCreated = $_.WhenCreated
                } }) 'exo-gebruikerspostvakken.csv'
        Save-Csv $dgLeden 'exo-groepsleden.csv'

        Save-Csv $rechten 'exo-gedeelde-postvakrechten.csv'
        Save-Csv ($doorsturen | ForEach-Object {
                [pscustomobject]@{
                    Postvak = "$($_.PrimarySmtpAddress)"; Type = "$($_.RecipientTypeDetails)"
                    Soort = (Get-DoorstuurSoort -SmtpAdres "$($_.ForwardingSmtpAddress)" -Adres "$($_.ForwardingAddress)" -Domeinen $domeinen)
                    Bestemming = "$($_.ForwardingSmtpAddress)$($_.ForwardingAddress)"; KopieBewaard = $_.DeliverToMailboxAndForward
                } }) 'exo-doorsturen.csv'
    }
}

# ============================================================
# HOOFDPROCES
# ============================================================

if ($Collector) {
    # Dit proces voert precies 1 onderdeel uit en sluit af.
    try {
        switch ($Collector) {
            'AD' { Invoke-ADCollector }
            'Entra' { Invoke-EntraCollector }
            'Exchange' { Invoke-ExchangeCollector }
        }
        exit 0
    }
    catch {
        Write-Host ("FOUT in onderdeel {0}: {1}" -f $Collector, $_.Exception.Message) -ForegroundColor Red
        if ($_.InvocationInfo -and $_.InvocationInfo.Line) {
            Write-Host ("   regel {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.InvocationInfo.Line.Trim()) -ForegroundColor Red
        }
        exit 1
    }
}

Write-Host ''
Write-Host '=== Inventaris (alleen lezen) ===' -ForegroundColor Cyan
Write-Host "Onderdelen : $($Onderdelen -join ', ')"
if ($ZonderPersonen) {
    Write-Host 'Modus      : ZONDER personen (structuur, aantallen, patronen)'
}
else {
    Write-Host 'Modus      : VOLLEDIG - de detailbestanden bevatten persoonsgegevens van medewerkers' -ForegroundColor Yellow
}

$OutputMap = Join-Path $OutputPath ('Inventaris-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmm'))
New-Item -ItemType Directory -Force -Path $OutputMap | Out-Null
Write-Host "Uitvoer    : $OutputMap"
Write-Host ''

$hostExe = (Get-Process -Id $PID).Path
$mislukt = @()
foreach ($onderdeel in $Onderdelen) {
    Write-Host "--- $onderdeel ---" -ForegroundColor Cyan
    $argLijst = @('-NoProfile', '-File', ('"{0}"' -f $PSCommandPath), '-Collector', $onderdeel, '-OutputMap', ('"{0}"' -f $OutputMap))
    if ($ZonderPersonen) { $argLijst += '-ZonderPersonen' }
    if ($AdServer) { $argLijst += @('-AdServer', ('"{0}"' -f $AdServer)) }
    if ($AdAanmelden) { $argLijst += '-AdAanmelden' }
    $proces = Start-Process -FilePath $hostExe -ArgumentList $argLijst -Wait -PassThru -NoNewWindow
    if ($proces.ExitCode -ne 0) { $mislukt += $onderdeel }
}

Write-Host ''
Write-Host 'Samenvatting maken...'
$md = New-SamenvattingMarkdown -Map $OutputMap -MetPersonen (-not $ZonderPersonen)
Set-Content -LiteralPath (Join-Path $OutputMap '00-samenvatting.md') -Value $md -Encoding UTF8

$zip = "$OutputMap.zip"
Compress-Archive -Path (Join-Path $OutputMap '*') -DestinationPath $zip -Force

Write-Host ''
Write-Host 'Klaar.' -ForegroundColor Green
Write-Host "  Map : $OutputMap"
Write-Host "  Zip : $zip"
Write-Host "  Deel eerst enkel: $(Join-Path $OutputMap '00-samenvatting.md')"
if ($mislukt.Count -gt 0) {
    Write-Host ("  Mislukt: {0} (zie de foutmelding hierboven; de rest is wel gemaakt)" -f ($mislukt -join ', ')) -ForegroundColor Yellow
}
if (-not $ZonderPersonen) {
    Write-Host '  Let op: de detailbestanden bevatten persoonsgegevens. Niet in git of een gedeelde map zetten.' -ForegroundColor Yellow
}
