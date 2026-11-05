# Roadmap naar Moss365 1.0 (Desktop)

**Moss365** is een beheerapp voor Active Directory, Entra ID en Exchange Online (Microsoft 365), voor IT-diensten die
meer dan één organisatie (tenant) beheren of één organisatie grondig willen beheren. De app ontstond als interne
beheerapp van een lokaal bestuur (versies 0.5 tot 0.9) en wordt in deze openbare repository **universeel**: geen
vaste namen, regels of structuren van één organisatie meer, alles instelbaar per tenant.

Dit document is het plan naar **1.0: de Desktop-editie** (Windows-app). De webversie (Server) komt daarna; die heeft
een eigen roadmap: [roadmap-web.md](roadmap-web.md).

Inhoud:

1. [Wat Moss365 is en wat altijd blijft](#1-wat-moss365-is-en-wat-altijd-blijft)
2. [Wat er verandert tegenover de interne versie](#2-wat-er-verandert-tegenover-de-interne-versie)
3. [Opslag: één databank per tenant, met momentopnamen](#3-opslag-één-databank-per-tenant-met-momentopnamen)
4. [Omgeving: installatie per tenant](#4-omgeving-installatie-per-tenant)
5. [Omgeving: deze computer](#5-omgeving-deze-computer)
6. [Omgeving: versleuteling en onderhoud van de databank](#6-omgeving-versleuteling-en-onderhoud-van-de-databank)
7. [Scripts uit deze repository](#7-scripts-uit-deze-repository)
8. [Automatisch bijwerken](#8-automatisch-bijwerken)
9. [Organisatie: universeel, functieprofielen en logonscripts](#9-organisatie-universeel-functieprofielen-en-logonscripts)
10. [Voorstellen: wat AD- en Microsoft 365-beheerders graag zien](#10-voorstellen-wat-ad--en-microsoft-365-beheerders-graag-zien)
11. [Open source, met ruimte voor ondersteuning](#11-open-source-met-ruimte-voor-ondersteuning)
12. [Keuzes](#12-keuzes)
13. [De stappen naar 1.0](#13-de-stappen-naar-10)
14. [Risico's](#14-risicos)

---

## 1. Wat Moss365 is en wat altijd blijft

Moss365 brengt AD, Entra ID en Exchange Online samen op één scherm: gebruikers, groepen, gedeelde postvakken, rooms,
gasten, licenties, beveiliging (MFA, beheerrollen, Conditional Access), statistieken, scripts, geplande in- en
uitstroom en een logboek van alles wat de app wijzigde.

Wat in elke versie blijft, en wat de tests bewaken:

- **Elke wijziging is een plan**: eerst een voorbeeld, dan een bevestiging, dan het logboek. Ongedaan maken waar het kan.
- **Nooit geheimen bewaren**: geen wachtwoorden of tokens op schijf of in een log. Een startwachtwoord leeft enkel in het
  geheugen.
- **De app kan niet meer dan jouw eigen account.** Gedelegeerde rechten: wat jij in Entra, Exchange of AD niet mag, kan
  je ook via Moss365 niet.
- **Rollen in de app** (Lezer, Helpdesk, Beheerder, Auditor, Appbeheer), afgedwongen per scherm, per knop en in de motor.
- **Geplande acties worden nooit vanzelf uitgevoerd**: op het moment zelf toont de app het plan en wacht.
- **Conditional Access nooit zonder noodaccounts**: een beleid dat aan staat, moet ze uitsluiten; dat controleert de
  motor zelf.
- **Bij uitstroom nooit verwijderen**: uitschakelen, groepen weghalen, verplaatsen.
- **Geen telemetrie.** De app stuurt niets naar de makers. Gegevens blijven bij de organisatie.
- **Geen persoonsgegevens in deze repository**: testgegevens zijn verzonnen; voorbeelden gebruiken `contoso.com` of
  `example.org`.

## 2. Wat er verandert tegenover de interne versie

| | Interne versie (0.9) | Moss365 1.0 Desktop |
| --- | --- | --- |
| Naam | Moss365 voor het programma, de rollen en de groepen; de oude projectnaam nog in de gegevensmap, de app-registratie en de namespaces | **Overal Moss365**: ook gegevensmap, namespaces, app-registratie en Windows-logboek |
| Organisaties | Eén | **Meerdere tenants**, strikt gescheiden, elk met een eigen databank |
| Opslag | SQLite voor logboek en instellingen; inventarissen als CSV-mappen; back-ups als JSON | **Alles in de databank van de tenant**, ook inventarissen, technisch log, back-ups en scriptruns |
| Geschiedenis | Een stand per dag van enkele indicatoren | **Momentopnamen van de hele organisatie**: hoe zag ze eruit op elke datum, en het verloop van elke indicator |
| Instellen | Configuratiebestand en een script voor de app-registratie | **Wizard per tenant**: zoekt de app-registratie "Moss365" op (of maakt ze), vult alles zelf in |
| Organisatielogica | Afgesteld op één organisatie (diensten, OU's, naamregels, groepsnamen) | **Universeel**: alles instelbaar, met voorstellen uit de eigen gegevens. Functieprofielen en logonscripts |
| Deze computer | Een controlescherm | Controle bij het opstarten; ontbrekende onderdelen **installeren** (met bevestiging) |
| Databank | Niet versleuteld | **Versleuteling** (keuze per tenant), compacteren en opkuisen |
| Scripts | Meegeleverd in de download | **Synchroniseren** met de bibliotheek in deze repository, met handtekening |
| Bijwerken | Een melding als er een nieuwe versie is | **Bij het opstarten nakijken en bijwerken**, met terugval |

### Alles heet Moss365

| Wat | Wordt |
| --- | --- |
| Programma | `Moss365.exe` |
| Projecten en namespaces | `Moss365.Core`, `Moss365.Verbindingen`, `Moss365.UI`, `Moss365.Desktop`, later `Moss365.Server` |
| Gegevensmap | `%LOCALAPPDATA%\Moss365` (tijdelijke bestanden in `%TEMP%\Moss365`) |
| Register (als de app er iets zet, bv. de installer) | `HKCU\Software\Moss365` |
| Windows-logboek | Toepassing, bron `Moss365` |
| App-registratie in Entra | Altijd **`Moss365`** |
| Rollen (app-rollen) | `Moss365.Lezer`, `Moss365.Helpdesk`, `Moss365.Beheerder`, `Moss365.Auditor`, `Moss365.Appbeheer` |
| Groepen voor de rollen | `GG-Moss365-Lezers`, `GG-Moss365-Helpdesk`, ... (voorvoegsel instelbaar per tenant) |
| Scriptkop | `<#MOSS365 ... #>` |
| Omgevingsvariabelen voor scripts | `MOSS365_*` |
| Databank | `moss365.db` (de app) en `tenant.db` (per tenant) |

**Overzetten** van een bestaande installatie van de interne versie: bij de eerste start vindt Moss365 de oude
gegevensmap en stelt voor om ze als eerste tenant in te lezen (instellingen, logboek, gepland, scripts, inventarissen).
De oude map blijft staan tot je ze zelf weghaalt. Een bestaande app-registratie met de oude naam wordt herkend; de wizard
stelt voor ze te hernoemen naar `Moss365` (client-id, rechten en toewijzingen blijven). De rollen, de groepen, de
scriptkop en de omgevingsvariabelen heetten al Moss365: daar verandert niets.

## 3. Opslag: één databank per tenant, met momentopnamen

### Indeling

```
%LOCALAPPDATA%\Moss365\
  moss365.db                 de app: lijst van tenants, app-instellingen, updatestatus (geen gegevens van een tenant)
  tenants\<tenant-id>\
    tenant.db               alles van die tenant (zie hieronder)
    tenant.db.bak-<datum>   automatische kopie vóór een migratie of update
  bibliotheek\              gesynchroniseerde scripts (gedeeld, zonder gegevens)
```

In **`tenant.db`** staat alles van die tenant:

| Onderdeel | Inhoud |
| --- | --- |
| Instellingen | Verbinding (tenant-id, client-id, domeincontroller), organisatie-instellingen, functieprofielen, noodaccounts |
| Momentopnamen | Alle gelezen objecten en relaties, met hun versies in de tijd (zie hieronder) |
| Logboek | Elke uitgevoerde wijziging: wie, wanneer, wat, voor en na, resultaat (enkel toevoegen) |
| Toegangslog | Wie exporteerde, wie aanmeldingen of beveiligingsgegevens bekeek |
| Technisch log | Wat er gebeurde, aantallen en fouten; nooit namen, parameters of geheimen |
| Werk | Geplande acties, opvolgtaken, scriptruns en hun uitvoer |
| Back-ups | Conditional Access-beleiden en benoemde locaties, logonscripts |
| Weergave | Kolommen, filters en keuzes per gebruiker van de app |

Nooit gegevens van twee tenants in één databank, lijst, plan of export.

### Momentopnamen: de organisatie door de tijd

Elke lezing (volledig of snel) wordt een **momentopname**. De databank bewaart niet telkens alles opnieuw, maar enkel
**wat veranderde**: elk object (gebruiker, groep, postvak, room, gast, beleid, licentie, ...) en elke relatie
(lidmaatschap, postvakrecht, manager, rol) heeft versies met "geldig van" en "geldig tot".

```
Momentopname (id, tijd, bron: AD / Entra / Exchange / volledig / snel)
Object       (soort, sleutel)
Versie       (object, van-momentopname, tot-momentopname, inhoud, vingerafdruk)
Relatie      (soort, van, naar, van-momentopname, tot-momentopname)
```

Wat dat mogelijk maakt:

- **De organisatie op een datum**: elk scherm (lijsten, statistieken, Conditional Access) kan een datum in het verleden
  tonen ("zoals op 1 maart"), alleen lezen.
- **Wat veranderde tussen twee datums**: nieuwe en verdwenen accounts, wie van dienst veranderde, wie in een
  bevoorrechte groep kwam, welke beleiden wijzigden. Ook wat er buiten Moss365 veranderde.
- **De tijdlijn van één object**: wanneer werd iemand lid van een groep, wanneer veranderde zijn functie, wanneer werd
  het account uitgeschakeld (met het logboek ernaast: deed Moss365 het, of iemand anders?).
- **Het verloop van elke indicator**: elke KPI kan voor elke momentopname opnieuw berekend worden, ook een KPI die pas
  later bestaat. Statistieken toont de evolutie (dag, week, maand, jaar) en het verschil met een vorige periode.
- **Terugzetten uit het verleden**: kenmerken of lidmaatschappen van een object zoals ze waren op een datum, als plan.

Omvang: een organisatie met enkele honderden accounts verandert per dag weinig; de databank groeit met enkele MB per
jaar. Voor snelheid houdt de app per dag een tabel met de berekende KPI's bij (alleen aantallen); die kan altijd
opnieuw opgebouwd worden uit de momentopnamen.

## 4. Omgeving: installatie per tenant

Een wizard **Tenant toevoegen** doet het werk; je vult enkel in wat de app niet zelf kan vinden.

1. **Tenant**: geef een domein (bv. `contoso.com`) of het tenant-id. De app zoekt het tenant-id zelf op (OpenID-gegevens
   van Microsoft).
2. **Aanmelden** met een beheeraccount van die tenant (de link verschijnt in de app, zoals nu).
3. **App-registratie `Moss365`** (altijd die naam):
   - bestaat ze, dan leest de app het client-id, de rechten, de redirect-URI, de app-rollen en de toewijzingen, en vult
     alles in;
   - ontbreekt er iets (een recht, een rol, toestemming door een beheerder), dan toont de app het verschil als plan;
   - bestaat ze niet, dan maakt de app ze, als plan (wat `Registreer-App.ps1` nu doet). Daarvoor is eenmalig een
     beheerder met de rol Application Administrator of Global Administrator nodig.
4. **Groepen voor de rollen** (`GG-Moss365-...`): zoeken, voorstellen om ze te maken en aan de rollen te koppelen.
5. **Active Directory**: is de pc lid van het domein, dan vindt de app domein en domeincontroller zelf. Een tenant zonder
   AD (alleen cloud) kan ook; dan verdwijnen de AD-onderdelen uit de schermen.
6. **Hybride of niet**: de app ziet of AD gesynchroniseerd wordt (Entra Connect of Cloud Sync) en weet zo waar ze moet
   schrijven.
7. **Exchange Online**: controle van de module en de aanmelding met hetzelfde account.
8. **Organisatie-instellingen**: de app leest de gegevens en **stelt voor** (zie hoofdstuk 9): diensten, kantoren, OU's,
   naamregels, groepsvoorvoegsels, het model van de gedeelde postvakken, dynamische groepen. Jij bevestigt of past aan.
9. **Noodaccounts**: de app stelt kandidaten voor (cloud-only Global Administrators die nergens onder een beleid vallen).
10. **Eerste volledige lezing**: de eerste momentopname.

Elke tenant krijgt een **naam en kleur** in de balk, en elk plan vraagt de bevestiging met de naam van de tenant, zodat
je nooit in de verkeerde organisatie werkt.

## 5. Omgeving: deze computer

Bij elke start (en in het scherm **Deze computer**) kijkt de app na:

| Onderdeel | Waarvoor | Ontbreekt het |
| --- | --- | --- |
| WebView2-runtime | De schermen | Installeren (bootstrapper van Microsoft) |
| PowerShell 7 (5.1 volstaat voor de meeste scripts) | Exchange en scripts | Installeren via winget |
| Module `ExchangeOnlineManagement` (minimumversie) | Exchange | `Install-Module -Scope CurrentUser` (geen beheerrechten nodig) |
| Module `Microsoft.Graph` (onderdelen) | Scripts uit de bibliotheek | Idem |
| RSAT Active Directory | Enkel voor scripts (de app zelf leest AD rechtstreeks) | Windows-onderdeel installeren (vraagt beheerrechten: Windows vraagt bevestiging) |
| Verbinding met de domeincontroller (LDAP, Kerberos), Graph en Exchange | Laden en wijzigen | Uitleg wat er mis is (poort, proxy, DNS) |
| Klok van de pc | Kerberos en aanmelden falen bij een afwijking | Melding |
| Verouderde versies | Fouten en veiligheid | Bijwerken |

Installeren gebeurt **altijd met bevestiging**, enkel uit de officiële bronnen (PowerShell Gallery, winget, Microsoft),
en komt in het logboek. Wie wil, zet bij Instellingen **"Ontbrekende onderdelen meteen installeren"** aan; dan vraagt
de app het maar één keer.

## 6. Omgeving: versleuteling en onderhoud van de databank

### Versleuteling (keuze per tenant)

De databank bevat persoonsgegevens en de geschiedenis van een organisatie. Ze kan versleuteld worden met SQLCipher
(AES-256, open source); de sleutel staat nooit in de databank, de repository of een log.

| Optie | Hoe | Goed voor | Let op |
| --- | --- | --- | --- |
| **A. Windows-account** (DPAPI) | De sleutel is versleuteld met je Windows-aanmelding | Standaard op de Desktop: niets in te tikken; beschermt tegen een gestolen schijf of een andere gebruiker | Wie als jij aangemeld is, kan erbij |
| **B. Wachtwoord** | Een wachtwoord bij het openen (sleutel afgeleid met PBKDF2 of Argon2) | Gedeelde pc's, draagbare installaties | Wachtwoord kwijt = databank kwijt; daarom altijd een **herstelsleutel** (afdrukken, in de kluis) |
| **C. AD-groep** (DPAPI-NG) | De sleutel is versleuteld voor een AD-groep (bv. `GG-Moss365-Beheerders`) | Meerdere beheerders op dezelfde pc of server, in een domein | Enkel op pc's in het domein |
| **D. Microsoft 365-aanmelding (SSO)** | De sleutel is "ingepakt" met een sleutel in **Azure Key Vault** van de tenant; uitpakken kan enkel met je Entra-aanmelding en de rol Key Vault Crypto User (toe te kennen via de groepen van Moss365) | Volgt de rechten en de MFA van de tenant; wie uit de groep gaat, kan niet meer openen | Vraagt een Azure-abonnement (enkele eurocent per maand); zonder internet niet te openen, dus ook hier een herstelsleutel |

**Studie bij D** (aan het begin van de stap R6):

- Een token van Microsoft kan zelf geen sleutel zijn: het verandert bij elke aanmelding. Daarom een sleutel die nooit uit
  Key Vault komt en enkel inpakt en uitpakt (wrap/unwrap), met de identiteit van de gebruiker.
- Zonder Azure is er in Microsoft 365 geen veilige plek om een sleutel te bewaren (een bestand in OneDrive of SharePoint
  is leesbaar voor wie erbij kan). Dus zonder Azure: A, B of C.
- Te onderzoeken: offline werken (een in het geheugen bewaarde, tijdelijk uitgepakte sleutel), sleutels roteren
  (`PRAGMA rekey`), en hoe dit op de Server werkt (dezelfde aanpak, of C met het serviceaccount).

Voorstel: **A standaard**, B en C als keuze, **D na een geslaagde studie**.

### Onderhoud

Een scherm **Databank** per tenant:

- grootte per onderdeel (momentopnamen, logboek, technisch log, scriptruns, back-ups);
- **compacteren** (`VACUUM`, ook automatisch na een opkuis);
- **oude gegevens opkuisen** volgens een bewaartermijn per onderdeel, altijd eerst als voorbeeld en, voor het logboek,
  eerst een archief (CSV of een aparte databank). Voorstel voor de standaarden:
  - momentopnamen: alles 90 dagen, daarna één per week tot 1 jaar, daarna één per maand;
  - logboek: 2 jaar (instelbaar; wat de organisatie met haar DPO afspreekt);
  - technisch log: 30 dagen; scriptruns: 90 dagen;
- **back-up en terugzetten** van een tenant (één bestand, versleuteld zoals de databank);
- **controle** van de databank (`PRAGMA integrity_check`) en automatisch een kopie vóór elke migratie of update.

## 7. Scripts uit deze repository

- De bibliotheek staat in deze repository, in `scripts/`, met de kop `<#MOSS365 ... #>` (naam, omschrijving, verbinding,
  schrijft of niet, variabelen, minimumversie van de app).
- De workflow test elk script (Pester: ASCII, `-WhatIf` voor scripts die wijzigen, geen namen of adressen), maakt een
  **index** (naam, versie, SHA-256) en **ondertekent** de scripts en de index (Authenticode).
- In de app: **Bibliotheek bijwerken** toont wat nieuw of gewijzigd is (met het verschil), en downloadt na je bevestiging.
  De app controleert handtekening en vingerafdruk, en weigert een script dat niet klopt.
- Een gedownload script draait nooit vanzelf: zoals nu eerst een voorbeeld (`-WhatIf`) en een bevestiging.
- Per tenant kan je een versie vastzetten ("deze tenant blijft op versie X van dit script").
- Eigen scripts blijven per tenant in de databank. Een eigen script delen met iedereen gebeurt via een pull request
  naar deze repository.

## 8. Automatisch bijwerken

- Bij het opstarten kijkt de app (zonder het werk tegen te houden) of er op GitHub een nieuwere versie is.
- Je ziet wat er nieuw is (de changelog van die versie) en kiest **Nu bijwerken** of **Later**.
- De update is **ondertekend** (Authenticode) en heeft een gepubliceerde vingerafdruk; de app controleert beide.
- Nooit bijwerken terwijl een plan of taak loopt. Voor de update: een kopie van elke databank; na de update: de
  migraties.
- Lukt de nieuwe versie niet, dan terug naar de vorige (blijft bewaard).
- Kanalen: **stabiel** (standaard) en **voorvertoning**.
- Voor organisaties zonder toegang tot GitHub: een eigen bron instellen (een gedeelde map met de releases).
- Techniek: een updater zoals **Velopack** (open source, kleine delta-updates, werkt met een gewone installatie per
  gebruiker) of **MSIX** met App Installer (keuze U4).

## 9. Organisatie: universeel, functieprofielen en logonscripts

### Alles instelbaar per tenant

Wat in de interne versie vast lag, wordt een instelling, met een voorstel uit de eigen gegevens:

| Instelling | Voorstel uit de gegevens |
| --- | --- |
| Diensten (Department) en hun afdelingen | De waarden die in AD en Entra voorkomen, met de schrijfwijzen die maar weinig verschillen |
| Kantoren en steden | Idem |
| Naam van de organisatie (Company) | De meest gebruikte waarde |
| OU's: nieuwe gebruikers, uitgestroomde accounts, nieuwe groepen, dienstaccounts | De OU's met de meeste (uitgeschakelde) accounts |
| Naamregels: UPN, sAMAccountName, weergavenaam, e-mail | Het patroon dat de meeste accounts volgen (bv. `voornaam.achternaam`) |
| Groepsnamen: voorvoegsels per soort (beveiliging, distributie, afdeling, toegang) | De voorvoegsels die al gebruikt worden |
| Gedeelde postvakken: toegang via een groep (en het naampatroon) of rechtstreeks | Wat de meeste postvakken nu hebben |
| Dynamische groepen (nooit zelf vullen) | Uit Entra |
| Bevoorrechte groepen | De ingebouwde (Domain Admins, ...) plus wat je toevoegt |
| Licenties (leesbare namen, standaard per soort gebruiker) | Uit de abonnementen |
| Rooms: de standaardinstellingen | De meest voorkomende instellingen |
| Noodaccounts | Kandidaten (zie hoofdstuk 4) |

Een tenant kan ook starten van een **sjabloon** (bv. "lokaal bestuur", "school", "kmo") en een tenant kan zijn
instellingen exporteren en in een andere inlezen.

### Functieprofielen

Een functieprofiel beschrijft wat iemand in een functie krijgt:

- AD-groepen, cloudgroepen en Microsoft 365-groepen (Teams);
- toegang tot gedeelde postvakken (via hun toegangsgroep);
- licenties;
- OU, dienst, kantoor en andere kenmerken;
- logonscript.

Gebruik:

- **Nieuwe gebruiker**: kies een functieprofiel, en het plan bevat alles in één keer.
- **Profiel toepassen** op een bestaande gebruiker: het plan toont wat erbij komt en wat weg kan.
- **Afwijkingen**: wie heeft meer of minder dan zijn profiel (een lijst voor de opkuis of een audit).
- **Spiegelgebruiker** blijft bestaan, maar een profiel is beter: het hangt niet af van één persoon.
- Profielen hebben versies (momentopnamen), zodat je ziet welke versie wanneer toegepast werd.

(Naamgeving: om verwarring met de rollen van de app te vermijden, heten de rollen in 1.0 **rollen** en de functies
**functieprofielen**.)

### Logonscripts

Een scherm **Logonscripts** per tenant:

- alle scripts in de NETLOGON-share, en welke gebruikers welk script hebben (`scriptPath`);
- verwijzingen naar scripts die niet bestaan, en scripts die niemand gebruikt;
- **bekijken en bewerken** in de code-editor van de app: opslaan is een plan met het verschil, de vorige versie blijft
  bewaard (en terug te zetten);
- wat een script doet in gewone taal waar dat kan (bv. de netwerkschijven die het koppelt);
- het logonscript van gebruikers wijzigen (één of een selectie), als plan;
- Logonscripts via groepsbeleid (GPO) worden getoond, maar niet gewijzigd.


## 10. Voorstellen: wat AD- en Microsoft 365-beheerders graag zien

Uit wat beheerders in de praktijk vaak missen. Ze staan allemaal in de stappen naar 1.0 (hoofdstuk 13); bij elke
titel staat de stap (R10 tot R15). Alles wat wijzigt, gaat zoals altijd via een plan; wat gevoelige gegevens toont (herstelsleutels,
wachtwoorden, berichtsporen), komt in het toegangslog en vraagt de juiste rol.

### Helpdesk en dagelijks werk (R10)

| Voorstel | Waarom |
| --- | --- |
| **Temporary Access Pass** maken voor iemand die zijn telefoon kwijt is (eenmalig of met een geldigheid) | De meest gestelde vraag aan een helpdesk sinds MFA |
| **BitLocker-herstelsleutel en LAPS-wachtwoord** opzoeken (AD en Entra), met het toegangslog | Nu zoeken beheerders in drie portalen |
| **Waar komt een vergrendeling vandaan**: de pc of het toestel uit de gebeurtenissen van de domeincontrollers | Een oud wachtwoord op een gsm vergrendelt een account telkens opnieuw |
| **Wachtwoord verloopt bijna**: lijst, en een herinnering per mail aan de gebruiker (of zijn manager) | Vooral voor wie zelden op het netwerk komt (thuiswerk, VPN) |
| **Berichtspoor** (message trace) voor één gebruiker: kwam de mail aan, en zo niet, waarom? | De tweede meest gestelde vraag |
| **Afwezigheidsbericht en doorsturen** voor iemand anders instellen, met een einddatum | Ziekte en verlof |
| **Lid van welke groepen, en waarom**: geneste groepen uitgevouwen, "via welke groep heeft deze persoon toegang" | Toegangsvragen beantwoorden zonder te zoeken |
| **Snel aanmelden bij elke start**: een versleutelde tokencache per tenant (optioneel, Windows-account) | Niet elke keer opnieuw aanmelden |
| Zoeken over alles met **Ctrl+K** (gebruikers, groepen, postvakken, pc's, scripts, schermen) | Sneller dan menu's |

### Beveiliging (R11)

| Voorstel | Waarom |
| --- | --- |
| **Doorsturen naar buiten** (postvakken en inboxregels) | Een klassiek teken van een gekaapt account |
| **App-registraties en bedrijfsapps**: geheimen en certificaten die verlopen, apps met veel rechten, toestemmingen door gebruikers, ongebruikte apps | Een verlopen geheim legt koppelingen stil; een riskante toestemming is een lek |
| **Waarschuwingen uit de momentopnamen**: nieuw lid van een bevoorrechte groep, gewijzigd beleid, nieuwe beheerrol, nieuwe toestemming voor een app, een uitgeschakeld account dat weer aan staat | Ziet ook wat buiten Moss365 gebeurde |
| **AD-beveiligingscontrole** (licht): accounts met een SPN (kerberoasting), onbeperkte delegatie, "wachtwoord niet vereist", omkeerbare versleuteling, oude krbtgt, adminCount-wezen, verouderde besturingssystemen | Wat een aanvaller eerst zoekt |
| **Gezondheid van AD**: domeincontrollers, replicatie, FSMO-rollen, tijd | Problemen zien voor gebruikers ze merken |
| **Groepsbeleid (GPO)**: welke beleiden waar gekoppeld zijn, uitgeschakelde en lege beleiden (alleen lezen) | Overzicht dat de standaardtools moeilijk geven |
| **Microsoft Secure Score** met zijn verloop en de stappen die het meest opleveren | Eén getal voor het management |
| **Beheerrollen en PIM**: wie heeft wat permanent, wie komt in aanmerking | Minste rechten |
| **Zwakke MFA-methodes** (sms, telefoon) en wie nog geen sterkere heeft | Phishingbestendig worden |
| **Riskante gebruikers en aanmeldingen** (vraagt Entra ID P2) | Snel reageren |

### Licenties en kosten (R12)

| Voorstel | Waarom |
| --- | --- |
| **Licenties die niets opbrengen**: uitgeschakelde accounts met een licentie, gedeelde postvakken met een licentie, dubbele plannen, nooit gebruikte licenties | Rechtstreeks geld |
| **Kost per maand** (prijs per licentie instelbaar), per dienst en per afdeling, met het verloop | Voor het budget |
| **Licenties volgens het functieprofiel**: wie heeft meer of minder dan zijn profiel | Opkuis en eerlijke verdeling |

### Toestellen, Teams en SharePoint (R13)

| Voorstel | Waarom |
| --- | --- |
| **Computers in AD**: niet meer aangemeld, besturingssysteem en versie, in welke OU | Opkuis en veiligheid |
| **Toestellen in Intune**: niet conform, niet meer gezien, versie, eigenaar | Conditional Access met "conform toestel" heeft er pas zin mee |
| **Teams en Microsoft 365-groepen zonder eigenaar**, met gasten, niet meer gebruikt | De meest gehoorde klacht over Teams |
| **SharePoint-sites**: extern delen, opslag, niet gebruikt | Gegevenslekken en kosten |

### Rapporteren en werken als team (R14)

| Voorstel | Waarom |
| --- | --- |
| **Toestand van de diensten van Microsoft** (storingen en het berichtencentrum) op het startscherm | "Ligt het aan ons of aan Microsoft?" |
| **Kwaliteit van de gegevens**: per dienst hoeveel accounts een functie, dienst, kantoor, manager en telefoon hebben | Goede gegevens maken dynamische groepen en het adresboek betrouwbaar |
| **Rapport per maand of kwartaal** (pdf of Excel): indicatoren, verloop, opkuis, wijzigingen | Voor het management en de informatieveiligheid |
| **Controles gekoppeld aan normen** (bv. NIS2 en de normen voor informatieveiligheid bij de overheid), met bewijs uit het logboek en de momentopnamen | Audit |
| **Gastbeheer met verlengen**: de sponsor bevestigt jaarlijks of een gast nog nodig is | Gasten blijven anders voor altijd |
| **In- en uitstroom als aanvraag**: een formulier voor HR of het diensthoofd, de beheerder keurt goed en het plan volgt uit het functieprofiel | Minder mails, alles in het logboek |
| **Tenants vergelijken**: dezelfde basislijn voor Conditional Access en de instellingen in elke tenant, met de afwijkingen | Voor wie meerdere organisaties beheert |

### Talen (R15)

| Voorstel | Waarom |
| --- | --- |
| De app in het **Nederlands, Engels en Frans** (keuze per gebruiker), de documentatie eerst in het Nederlands en het Engels | Een openbare app, gemaakt in België |

## 11. Open source, met ruimte voor ondersteuning

Moss365 is **open source**: iedereen kan de code lezen, gebruiken, aanpassen en verbeteren. Er moet later wel
**ondersteuning en geld** gevraagd kunnen worden. Dat gaat samen:

| Wat altijd vrij blijft | Wat later betaald kan worden |
| --- | --- |
| De app (Desktop en Server), de broncode, de scriptbibliotheek, de documentatie, de updates | **Ondersteuning** met afspraken (antwoordtijd, hulp bij problemen) |
| | **Installatie en inrichting** (wizard, functieprofielen, Conditional Access) |
| | **Opleiding** voor beheerders en helpdesk |
| | **Gehoste Server-editie** (Moss365 als dienst, voor wie geen eigen server wil) |
| | **Voorrang** voor gevraagde functies of maatwerk |

De licentie moet dat toelaten en beschermen. Voorstel:

- **EUPL-1.2** (de open-sourcelicentie van de Europese Unie, gemaakt voor de overheid, in alle EU-talen):
  - wie Moss365 aanpast en verdeelt, of aanbiedt als dienst, moet zijn aanpassingen ook openbaar maken. Zo kan niemand
    er een gesloten product van maken;
  - betaalde ondersteuning, installatie, opleiding en hosting zijn uitdrukkelijk toegelaten;
  - verenigbaar met andere open-sourcelicenties (GPL, AGPL, MPL, ...).
- **Een bijdrageovereenkomst** (CLA) voor bijdragen van buitenaf: de bijdrager geeft toestemming om zijn bijdrage ook
  onder een andere licentie te verdelen. Zo blijft het later mogelijk om naast de open versie een commerciële licentie
  of betaalde extra's aan te bieden (dubbele licentie), zonder iedereen opnieuw te moeten vragen.
- Alternatief: **Apache-2.0** (eenvoudiger en ruimer: iedereen mag Moss365 ook gesloten verder verkopen, en dan heb je
  geen CLA nodig, maar ook minder bescherming).

Laat de keuze door een jurist bevestigen voor de eerste release. Verder:

- **Naam en merk**: "Moss365" is de naam van het project. De README en de app vermelden dat Moss365 **niet verbonden is
  met Microsoft**; "Microsoft 365", "Entra" en "Exchange" zijn merken van Microsoft. Eventueel de naam later als merk
  registreren (Benelux), zodat niemand anders een Moss365 verkoopt.
- **Gegevens**: geen telemetrie, ook niet in een betaalde versie zonder uitdrukkelijke toestemming.

## 12. Keuzes

### Beslist

| # | Keuze | Beslissing |
| --- | --- | --- |
| U1 | **Naam** | **Moss365**, overal (deze repository: `jef-cesar/Moss365`) |
| U2 | **Open source** | **Open source, met de mogelijkheid om later ondersteuning en geld te vragen** (hoofdstuk 11). De precieze licentie: zie U12 |
| U3 | **Geschiedenis van de code** | **Een nieuwe start zonder geschiedenis**: de interne repository vermeldt de organisatie waar de app ontstond. De code komt over na een controle |

### Nog te beslissen

| # | Keuze | Voorstel |
| --- | --- | --- |
| U12 | **Licentie** | **EUPL-1.2 met een CLA** (hoofdstuk 11); alternatief Apache-2.0 |
| U4 | **Bijwerken** | Velopack (installatie per gebruiker, delta-updates) of MSIX (App Installer) |
| U5 | **Aanmelden bij een tenant** | Een eigen app-registratie `Moss365` per tenant (strengste scheiding, elke organisatie geeft zelf toestemming). Niet: één gedeelde multi-tenant app van de makers |
| U6 | **Eerste aanmelding in de wizard** (er is nog geen app-registratie) | De openbare client van Microsoft Graph PowerShell (zoals `Connect-MgGraph`), met de link in de app; terugval: `Registreer-App.ps1` |
| U7 | **Versleuteling** | A (Windows-account) standaard; B en C als keuze; D na de studie |
| U8 | **Ondertekenen** van app, updates en scripts | Een codesigningcertificaat voor het project (Authenticode). Zonder certificaat: enkel vingerafdrukken (zwakker) |
| U9 | **Bewaartermijnen** (standaard) | Zoals in hoofdstuk 6; per tenant aan te passen |
| U10 | **Taal van de code en de documentatie** | Nederlands zoals nu; Engels erbij voor wie bijdraagt (README, bijdragegids); de app in drie talen (R15) |
| U11 | **Huisstijl** | Mosgroen en paars als standaard (past bij de naam), kleur en logo per tenant |

## 13. De stappen naar 1.0

| Stap | Inhoud | Klaar als |
| --- | --- | --- |
| **R0 Repository** | Licentie (U12) en CLA, `SECURITY.md`, bijdragegids, issue-sjablonen, vermelding "niet verbonden met Microsoft", workflow (bouwen, testen, ondertekenen, release), geheimscanning en push-bescherming, CodeQL, Dependabot. Een controle in de workflow die persoonsgegevens en echte domeinen weigert | Een lege app bouwt, test en verschijnt als release |
| **R1 Overzetten en Moss365 overal** | De code van 0.9 overzetten zonder geschiedenis (U3), na een controle: organisatie-specifieke instellingen, conventies en voorbeelden eruit, een verzonnen voorbeeldorganisatie erin. Alles hernoemen (hoofdstuk 2). Overzetten van een bestaande installatie | Alle tests groen; de oude projectnaam komt enkel nog voor in het overzetten; een bestaande installatie leest correct in |
| **R2 Eén databank per tenant, alles erin** | `moss365.db` en `tenant.db`; inventarissen, technisch log, back-ups, scriptruns en instellingen in de databank; back-up en terugzetten van een tenant | Geen bestanden meer naast de databank (behalve de bibliotheek en de kopieën) |
| **R3 Meerdere tenants** | Tenant kiezen bovenaan, kleur en naam, bevestiging met de naam, tokens per tenant, aanmelden per tenant (U5) | Twee tenants naast elkaar; een test bewijst dat geen lijst, plan, export of logboekregel ze mengt |
| **R4 Momentopnamen en tijdlijn** | Objecten en relaties met versies; elke lezing een momentopname; de organisatie op een datum; verschillen tussen datums; tijdlijn per object; KPI's voor elke momentopname; Statistieken met verloop per dag, week, maand en jaar; terugzetten uit het verleden | Elke KPI toont zijn verloop; een scherm toont "zoals op" een datum; een test herbouwt de stand van een datum exact |
| **R5 Omgeving** | Wizard per tenant (app-registratie `Moss365` opzoeken of maken, automatisch invullen, groepen, AD, hybride, Exchange, voorstellen voor de instellingen, noodaccounts); Deze computer met installeren | Een nieuwe testtenant is in minder dan 15 minuten klaar zonder iets in te tikken dat de app kon vinden |
| **R6 Versleuteling en onderhoud** | Versleuteling A, B en C (U7), herstelsleutel, studie D; scherm Databank: grootte, compacteren, opkuis met voorbeeld en archief, controle | Een versleutelde tenant opent enkel met de juiste sleutel; opkuis verwijdert nooit zonder voorbeeld |
| **R7 Bijwerken** | Nakijken bij het opstarten, ondertekende updates, kopie vóór migratie, terugval, kanalen, eigen bron (U4, U8) | Een update van de voorvertoning naar stabiel en terug werkt op een testpc |
| **R8 Scripts uit de repository** | `scripts/` met index en handtekening, Bibliotheek bijwerken in de app, vastzetten per tenant | Een nieuw script in de repository verschijnt na Bijwerken in de app; een gewijzigd script zonder geldige handtekening wordt geweigerd |
| **R9 Organisatie universeel** | Instellingen met voorstellen uit de gegevens, sjablonen, export en import; functieprofielen; logonscripts | Een voorbeeldorganisatie met andere naamregels, OU's en groepsnamen werkt zonder één aanpassing in de code |
| **R10 Helpdesk** | Alles uit hoofdstuk 10, Helpdesk en dagelijks werk | Elk voorstel met tests, in het logboek of het toegangslog, met de juiste rol |
| **R11 Beveiliging** | Alles uit hoofdstuk 10, Beveiliging (waarschuwingen bouwen op R4) | Idem; elke controle zegt "in orde", "aandacht" of "probleem", en "onbekend" als de gegevens niet volstaan |
| **R12 Licenties en kosten** | Alles uit hoofdstuk 10, Licenties en kosten | Idem; de besparing staat als KPI met haar verloop |
| **R13 Toestellen, Teams en SharePoint** | Alles uit hoofdstuk 10, Toestellen, Teams en SharePoint (de nodige rechten komen bij in de wizard, enkel lezen) | Idem |
| **R14 Rapporteren en werken als team** | Alles uit hoofdstuk 10, Rapporteren en werken als team | Idem; een rapport per kwartaal komt uit de momentopnamen, ook voor een periode in het verleden |
| **R15 Talen** | De teksten van de app in bronbestanden; Nederlands, Engels en Frans; datums en getallen per taal | Elk scherm in drie talen; een test vindt geen vaste tekst meer in de schermen |
| **1.0 Desktop** | Handleiding per rol, installatiegids, overzettingsgids, privacy-informatie (wat de app bewaart en waar), een model voor de DPIA | Een organisatie die Moss365 niet kent, installeert en gebruikt het met enkel de documentatie |

Volgorde: R0 en R1 eerst. R2 en R3 horen samen (een databank per tenant). R4 bouwt daarop, en R11 en R14 bouwen op R4.
De andere stappen kunnen in een andere volgorde als dat beter uitkomt; elke stap is een eigen release (0.10, 0.11, ...).
Daarna de webversie: [roadmap-web.md](roadmap-web.md).

## 14. Risico's

| Risico | Aanpak |
| --- | --- |
| Organisatie-specifieke gegevens raken mee in de openbare repository | Nieuwe start zonder geschiedenis (U3), een controle in de workflow, geheimscanning met push-bescherming, verzonnen voorbeelden |
| Een gekaapte update of een gekaapt script | Alles ondertekend, vingerafdrukken gecontroleerd, enkel uit de officiële bron, scripts draaien nooit vanzelf |
| Versleutelde databank, sleutel kwijt | Herstelsleutel bij het instellen; de gegevens van de tenant zelf zijn opnieuw te lezen (enkel logboek en geschiedenis zijn uniek) |
| De momentopnamen worden te groot of te traag | Enkel wijzigingen bewaren, uitdunnen volgens de bewaartermijn, een KPI-tabel per dag, tests met een grote verzonnen organisatie |
| Werken in de verkeerde tenant | Kleur en naam in de balk, bevestiging met de naam, één databank per tenant |
| De wizard maakt of wijzigt een app-registratie met te veel rechten | Enkel gedelegeerde rechten, de lijst staat in de code en in de documentatie, altijd als plan met voorbeeld |
| Automatisch installeren van onderdelen | Enkel officiële bronnen, altijd een bevestiging (of één keer de keuze), in het logboek |
| Gevoelige gegevens in de helpdeskfuncties (herstelsleutels, LAPS, berichtsporen) | Enkel met de juiste rol, altijd in het toegangslog, nooit in een export of het technisch log |
| Bijdragen van buitenaf | Pull requests met tests en review, een CLA, de regels van hoofdstuk 1 in de bijdragegids |
| De naam wekt de indruk van een product van Microsoft | De vermelding "niet verbonden met Microsoft" in de README en de app; geen logo's van Microsoft |
| Te veel tegelijk: veel stappen voor één beheerder | Elke stap een kleine release met tests; de volgorde van R5 tot R15 is vrij, dus wat het meest helpt eerst |
