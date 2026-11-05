#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Pester 5-tests voor de pure hulpfuncties van Export-Inventaris.ps1.
# De verbindingen met AD, Entra en Exchange worden niet getest; die vragen een echte omgeving.

BeforeAll {
    $script:ScriptPad = Join-Path (Split-Path -Path $PSScriptRoot -Parent) 'Export-Inventaris.ps1'
    # Dot-sourcen stopt na de functies (zie de bewaker in het script). OutputPath wordt meegegeven
    # zodat de standaardwaarde (%LOCALAPPDATA%) niet nodig is.
    . $script:ScriptPad -OutputPath $TestDrive
}

Describe 'ConvertTo-Patroon' {
    It 'maakt van een naam een vorm zonder letters van de naam' {
        ConvertTo-Patroon 'Anna Smith' | Should -Be 'Aa Aa'
        ConvertTo-Patroon 'anna.smith' | Should -Be 'a.a'
    }
    It 'behoudt het domein bij e-mailadressen als dat gevraagd wordt' {
        ConvertTo-Patroon 'Anna.Smith@Contoso.com' -BehoudDomein | Should -Be 'Aa.Aa@contoso.com'
    }
    It 'verbergt het domein zonder -BehoudDomein' {
        ConvertTo-Patroon 'anna.smith@contoso.com' | Should -Be 'a.a@a.a'
    }
    It 'vervangt cijfers en behandelt accenten als letters' {
        ConvertTo-Patroon 'GBK-00042' | Should -Be 'A-9'
        ConvertTo-Patroon ('Bjorn Ceulemans-Beli' + [char]0x00EB + 'n') | Should -Be 'Aa Aa-Aa'
    }
    It 'geeft (leeg) voor ontbrekende waarden' {
        ConvertTo-Patroon $null | Should -Be '(leeg)'
        ConvertTo-Patroon '  ' | Should -Be '(leeg)'
    }
}

Describe 'Get-TopPatronen' {
    It 'telt en sorteert patronen' {
        $r = @(Get-TopPatronen @('jan.peeters', 'els.janssens', 'kim', 'a.b', $null))
        $r[0].Patroon | Should -Be 'a.a'
        $r[0].Aantal | Should -Be 3
        ($r | Where-Object Patroon -eq '(leeg)').Aantal | Should -Be 1
    }
    It 'respecteert Top' {
        @(Get-TopPatronen @('a', 'A', '1', 'a-b', 'a.b') -Top 2).Count | Should -Be 2
    }
    It 'geeft een lege lijst bij geen invoer' {
        @(Get-TopPatronen @()).Count | Should -Be 0
    }
}

Describe 'Get-GroepPrefix en Get-TopPrefixen' {
    It 'geeft het eerste deel of de eerste twee delen' {
        Get-GroepPrefix 'SG-SM-info-Full' | Should -Be 'SG'
        Get-GroepPrefix 'SG-SM-info-Full' -Aantal 2 | Should -Be 'SG-SM'
    }
    It 'herkent namen zonder prefix' {
        Get-GroepPrefix 'Signatures' | Should -Be '(geen prefix)'
        Get-GroepPrefix 'SG-info' -Aantal 2 | Should -Be '(geen prefix)'
    }
    It 'telt prefixen' {
        $r = @(Get-TopPrefixen @('SG-A-1', 'SG-B-2', 'DL-A-3', 'Los'))
        ($r | Where-Object Prefix -eq 'SG').Aantal | Should -Be 2
        ($r | Where-Object Prefix -eq '(geen prefix)').Aantal | Should -Be 1
    }
}

Describe 'Get-VullingStatistiek' {
    It 'telt ingevulde waarden, ook voor lijsten' {
        $objecten = @(
            [pscustomobject]@{ Department = 'Technische dienst'; Title = ''; Leden = @('a', 'b') }
            [pscustomobject]@{ Department = $null; Title = 'Arbeider'; Leden = @() }
        )
        $r = @(Get-VullingStatistiek -Objecten $objecten -Attributen 'Department', 'Title', 'Leden')
        ($r | Where-Object Attribuut -eq 'Department').Ingevuld | Should -Be 1
        ($r | Where-Object Attribuut -eq 'Title').Percentage | Should -Be 50
        ($r | Where-Object Attribuut -eq 'Leden').Ingevuld | Should -Be 1
    }
    It 'geeft 0 procent bij geen objecten' {
        (@(Get-VullingStatistiek -Objecten @() -Attributen 'X'))[0].Percentage | Should -Be 0
    }
}

Describe 'OU-hulpen' {
    It 'zet een DN om naar een pad van boven naar onder' {
        Get-OuPad 'CN=Jan,OU=Technische Dienst,OU=Personeel,DC=contoso,DC=com' | Should -Be 'Personeel/Technische Dienst/Jan'
        Get-OuPad 'OU=Personeel,DC=contoso,DC=com' | Should -Be 'Personeel'
    }
    It 'houdt rekening met een ontsnapte komma in een naam' {
        Get-OuPad 'OU=Zorg\, Welzijn,DC=x,DC=be' | Should -Be 'Zorg, Welzijn'
    }
    It 'geeft de bovenliggende container' {
        Get-ParentDN 'CN=Jan,OU=Technische Dienst,DC=x,DC=be' | Should -Be 'OU=Technische Dienst,DC=x,DC=be'
        Get-ParentDN 'DC=be' | Should -Be ''
    }
}

Describe 'Get-AgendaRechtenTekst' {
    BeforeAll {
        $script:Rechten = @(
            [pscustomobject]@{ User = 'Default'; AccessRights = 'AvailabilityOnly' }
            [pscustomobject]@{ User = 'Jan Peeters'; AccessRights = 'Editor' }
            [pscustomobject]@{ User = 'Els Janssens'; AccessRights = 'Reviewer' }
        )
    }
    It 'toont alle gebruikers met personen' {
        Get-AgendaRechtenTekst -Rechten $script:Rechten -MetPersonen $true | Should -Be 'Default=AvailabilityOnly|Jan Peeters=Editor|Els Janssens=Reviewer'
    }
    It 'toont zonder personen enkel de standaardinstellingen en een aantal' {
        $tekst = Get-AgendaRechtenTekst -Rechten $script:Rechten -MetPersonen $false
        $tekst | Should -Be 'Default=AvailabilityOnly|(2 anderen)'
        $tekst | Should -Not -Match 'Peeters|Janssens'
    }
    It 'geeft een lege tekst zonder rechten' {
        Get-AgendaRechtenTekst -Rechten @() -MetPersonen $false | Should -Be ''
    }
}

Describe 'Get-AdArgs' {
    It 'geeft een lege lijst zonder server of referenties' {
        (Get-AdArgs).Count | Should -Be 0
    }
    It 'neemt server en referenties op' {
        $cred = [pscustomobject]@{ Naam = 'x' }
        $a = Get-AdArgs -Server 'dc01.contoso.local' -Credential $cred
        $a['Server'] | Should -Be 'dc01.contoso.local'
        $a['Credential'] | Should -Be $cred
    }
    It 'laat een niet opgegeven onderdeel weg' {
        $a = Get-AdArgs -Server 'dc01'
        $a.ContainsKey('Credential') | Should -BeFalse
    }
}

Describe 'Format-Datum en Get-MaxDatum' {
    It 'schrijft datums onafhankelijk van de regio-instelling' {
        Format-Datum ([datetime]'2026-10-15T13:29:48') | Should -Be '2026-10-15 13:29:48'
        Format-Datum ([datetimeoffset]'2026-10-15T13:29:48+00:00') | Should -Be '2026-10-15 13:29:48'
    }
    It 'geeft tekst terug voor niet-datums en leeg voor null' {
        Format-Datum 'abc' | Should -Be 'abc'
        Format-Datum $null | Should -Be ''
    }
    It 'kiest de laatste datum en negeert ontbrekende' {
        $a = [datetime]'2026-01-01'; $b = [datetime]'2026-06-01'
        Get-MaxDatum $a $b | Should -Be $b
        Get-MaxDatum $null $a | Should -Be $a
        Get-MaxDatum $a $null | Should -Be $a
        Get-MaxDatum $null $null | Should -BeNullOrEmpty
    }
}

Describe 'Get-DoorstuurSoort' {
    BeforeAll { $script:Dom = @('contoso.com', 'contoso.onmicrosoft.com') }
    It 'herkent intern en extern, ook met smtp:-voorvoegsel' {
        Get-DoorstuurSoort -SmtpAdres 'smtp:info@contoso.com' -Domeinen $script:Dom | Should -Be 'intern'
        Get-DoorstuurSoort -SmtpAdres 'iemand@example.net' -Domeinen $script:Dom | Should -Be 'extern'
        Get-DoorstuurSoort -SmtpAdres 'SMTP:Iemand@CONTOSO.ONMICROSOFT.COM' -Domeinen $script:Dom | Should -Be 'intern'
    }
    It 'beschouwt een interne ontvanger (ForwardingAddress) als intern' {
        Get-DoorstuurSoort -Adres 'Jan Peeters' -Domeinen $script:Dom | Should -Be 'intern'
    }
    It 'geeft leeg als er niet wordt doorgestuurd' {
        Get-DoorstuurSoort -Domeinen $script:Dom | Should -Be ''
    }
}

Describe 'Get-GastDomein' {
    It 'neemt het domein uit Mail' {
        Get-GastDomein -Mail 'iemand@Fabrikam.com' -Upn 'x' | Should -Be 'fabrikam.com'
    }
    It 'valt terug op de UPN van een gast' {
        Get-GastDomein -Mail '' -Upn 'jan.peeters_example.net#EXT#@contoso.onmicrosoft.com' | Should -Be 'example.net'
    }
    It 'geeft (onbekend) als er niets te halen valt' {
        Get-GastDomein -Mail '' -Upn 'geen-gast@contoso.com' | Should -Be '(onbekend)'
    }
}

Describe 'Get-StandaardGroepen' {
    BeforeAll {
        $script:Leden = @{
            g1 = @('u1', 'u2', 'u3', 'u4')   # 100% van de populatie
            g2 = @('u1', 'u2', 'x9')         # 50%
            g3 = @('u1', 'y8', 'z7')         # 25%
        }
        $script:Namen = @{ g1 = 'Alle gebruikers'; g2 = 'MFA'; g3 = 'Rand' }
    }
    It 'geeft groepen vanaf de drempel, hoogste dekking eerst' {
        $r = @(Get-StandaardGroepen -GebruikerIds @('u1', 'u2', 'u3', 'u4') -LedenPerGroep $script:Leden -GroepNamen $script:Namen -Drempel 50)
        $r.Count | Should -Be 2
        $r[0].Groep | Should -Be 'Alle gebruikers'
        $r[0].Dekking | Should -Be 100
        $r[1].Groep | Should -Be 'MFA'
        $r[1].Dekking | Should -Be 50
    }
    It 'houdt rekening met een hogere drempel' {
        @(Get-StandaardGroepen -GebruikerIds @('u1', 'u2', 'u3', 'u4') -LedenPerGroep $script:Leden -GroepNamen $script:Namen -Drempel 90).Count | Should -Be 1
    }
    It 'geeft niets bij een lege populatie' {
        @(Get-StandaardGroepen -GebruikerIds @() -LedenPerGroep $script:Leden -GroepNamen $script:Namen).Count | Should -Be 0
    }
}

Describe 'Join-Waarden' {
    It 'voegt lijsten samen met | en laat tekst met rust' {
        Join-Waarden @('a', 'b') | Should -Be 'a|b'
        Join-Waarden 'tekst' | Should -Be 'tekst'
        Join-Waarden $null | Should -Be ''
    }
}

Describe 'ConvertTo-MdTabel' {
    It 'maakt een markdown-tabel' {
        $t = ConvertTo-MdTabel -Rijen @([pscustomobject]@{ A = 'x'; B = 1 }) -Kolommen 'A', 'B'
        $t | Should -Match '\| A \| B \|'
        $t | Should -Match '\| x \| 1 \|'
    }
    It 'vermeldt dat er geen gegevens zijn' {
        ConvertTo-MdTabel -Rijen @() -Kolommen 'A' | Should -Match 'geen gegevens'
    }
}

Describe 'New-SamenvattingMarkdown' {
    BeforeAll {
        $map = Join-Path $TestDrive 'inv'
        New-Item -ItemType Directory -Path $map | Out-Null
        $ad = [ordered]@{
            Domein       = [ordered]@{ DNSRoot = 'contoso.local'; NetBIOS = 'CONTOSO'; Forest = 'contoso.local'; DomainMode = 'Windows2016Domain'; AantalDCs = 2 }
            Gebruikers   = [ordered]@{ Totaal = 10; Ingeschakeld = 8; Uitgeschakeld = 2; NooitAangemeld = 1; Inactief180d = 2; WachtwoordVerloptNooit = 3 }
            Patronen     = [ordered]@{
                UPN = @(@{ Patroon = 'a.a@contoso.com'; Aantal = 8 }); SamAccountName = @(@{ Patroon = 'a.a'; Aantal = 8 })
                DisplayName = @(@{ Patroon = 'Aa Aa'; Aantal = 8 }); EmailAddress = @()
            }
            Vulling      = @(@{ Attribuut = 'Department'; Ingevuld = 6; Percentage = 75 })
            Groepen      = [ordered]@{ Totaal = 5; Security = 4; Distribution = 1; Global = 3; DomainLocal = 1; Universal = 1; Leeg = 1; ZonderBeheerder = 4 }
            GroepPrefix1 = @(@{ Prefix = 'SG'; Aantal = 4 })
            GroepPrefix2 = @(@{ Prefix = 'SG-SM'; Aantal = 3 })
            Computers    = @(@{ OS = 'Windows 11 Pro'; Aantal = 7 })
            OUBoom       = @(@{ Pad = 'Personeel'; Diepte = 0; Gebruikers = 2; Groepen = 1 }, @{ Pad = 'Personeel/Technische Dienst'; Diepte = 1; Gebruikers = 6; Groepen = 0 })
            Bevoorrecht  = @(@{ Groep = 'Domain Admins'; AantalLeden = 3 })
        }
        $ad | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $map 'ad-statistiek.json') -Encoding UTF8
        $script:Md = New-SamenvattingMarkdown -Map $map -MetPersonen $true
    }
    It 'bevat de AD-sectie met aantallen en patronen' {
        $script:Md | Should -Match '## On-prem AD'
        $script:Md | Should -Match 'contoso\.local'
        $script:Md | Should -Match 'a\.a@contoso\.com'
        $script:Md | Should -Match 'Domain Admins'
    }
    It 'toont de OU-boom met inspringing' {
        $script:Md | Should -Match '(?m)^Personeel  \[g=2 gr=1\]'
        $script:Md | Should -Match '(?m)^  Technische Dienst  \[g=6 gr=0\]'
    }
    It 'laat secties weg waarvan geen statistiek is' {
        $script:Md | Should -Not -Match '## Exchange Online'
        $script:Md | Should -Not -Match '## Entra ID'
    }
}

Describe 'New-SamenvattingMarkdown (Entra en Exchange)' {
    BeforeAll {
        $map = Join-Path $TestDrive 'inv2'
        New-Item -ItemType Directory -Path $map | Out-Null
        $entra = [ordered]@{
            Tenant     = [ordered]@{ Naam = 'Gemeente'; Domeinen = 'contoso.com'; HybridSync = $true; LaatsteSync = '2026-10-15 13:29:48' }
            Gebruikers = [ordered]@{ Totaal = 10; Ingeschakeld = 9; Gasten = 2; Gesynchroniseerd = 6; CloudOnly = 2; MetLicentie = 5; LicentieDirect = 5; LicentieViaGroep = 0 }
            Patronen   = [ordered]@{ UPN = @(@{ Patroon = 'a.a@contoso.com'; Aantal = 6 }); DisplayName = @(); MailNickname = @() }
            Vulling    = @(); Licenties = @(); GroepPrefix1 = @(); GroepPrefix2 = @(); Beheerders = @()
            Groepen    = [ordered]@{ Totaal = 1; M365 = 0; Beveiliging = 1; MailBeveiliging = 0; Distributie = 0; Dynamisch = 0; Gesynchroniseerd = 0; CloudOnly = 1; Leeg = 0; MetLicentie = 0 }
            Admins     = [ordered]@{ UniekePersonen = 4; MetMeerdereRollen = 1; GlobalAdmins = [ordered]@{ Totaal = 3; Gelicentieerd = 1; Gesynchroniseerd = 1 } }
            Gasten     = [ordered]@{ Totaal = 2; Inactief180d = 1; PerJaar = @(@{ Jaar = 2024; Aantal = 2 }); TopDomeinen = @(@{ Domein = 'example.net'; Aantal = 2 }) }
            Populaties = @(
                [ordered]@{
                    OU = 'GEMEENTE/GEBRUIKERS'; Aantal = 148; Ingeschakeld = 147
                    UpnPatronen = @(@{ Patroon = 'a.a@contoso.com'; Aantal = 140 })
                    Bedrijf = @(@{ Waarde = 'Contoso'; Aantal = 140 }); Licenties = @(@{ Waarde = 'SPB'; Aantal = 90 })
                    StandaardGroepen = @(@{ Groep = 'Alle gebruikers'; Dekking = 100; Leden = 148 })
                }
            )
        }
        $entra | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $map 'entra-statistiek.json') -Encoding UTF8
        $exo = [ordered]@{
            Postvakken = @(@{ Type = 'SharedMailbox'; Aantal = 3 }); Patronen = [ordered]@{ PrimarySmtp = @(); Alias = @() }
            Doorsturen = [ordered]@{ Aantal = 2; Extern = 1; PerType = @(@{ Type = 'SharedMailbox'; Aantal = 2 }) }
            Aanmelding = [ordered]@{ Beschikbaar = $true; PerType = @(@{ Type = 'SharedMailbox'; Totaal = 3; Geblokkeerd = 1 }) }
            GedeeldeRechten = [ordered]@{
                Model = [ordered]@{ AlleenGroep = 1; GroepEnDirect = 1; AlleenDirect = 1; GeenRechten = 0 }
                PerRecht = @(@{ Recht = 'FullAccess'; Groep = 2; Direct = 5 })
            }
            GedeeldePostvakken = @('Info'); Rooms = [ordered]@{ Aantal = 0; PerGebouw = @(); PerAutomateProcessing = @() }
            Groepen = [ordered]@{ Totaal = 0; PerType = @() }; RoomLists = @()
            Transportregels = [ordered]@{ Aantal = 0; Ingeschakeld = 0 }; Domeinen = @('contoso.com')
        }
        $exo | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $map 'exo-statistiek.json') -Encoding UTF8
        $script:Md2 = New-SamenvattingMarkdown -Map $map -MetPersonen $false
    }
    It 'toont de populaties met standaardgroepen' {
        $script:Md2 | Should -Match '#### GEMEENTE/GEBRUIKERS \(148 gebruikers'
        $script:Md2 | Should -Match '- Alle gebruikers \(100%\)'
    }
    It 'toont beheerders en gasten' {
        $script:Md2 | Should -Match 'Global Administrators: \*\*3\*\*'
        $script:Md2 | Should -Match '### Gasten'
        $script:Md2 | Should -Match 'example\.net'
    }
    It 'toont doorsturen, aanmelding en het rechtenmodel van gedeelde postvakken' {
        $script:Md2 | Should -Match 'waarvan naar een extern domein: \*\*1\*\*'
        $script:Md2 | Should -Match 'Aanmelding bij gedeelde en resource-postvakken'
        $script:Md2 | Should -Match 'alleen via groep \*\*1\*\*, groep en direct \*\*1\*\*, alleen direct \*\*1\*\*'
    }
}
