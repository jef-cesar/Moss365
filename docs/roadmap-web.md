# Roadmap Moss365 Server (webversie, na 1.0)

Eerst komt **Moss365 1.0 Desktop** ([roadmap-1.0.md](roadmap-1.0.md)). Deze roadmap beschrijft wat daarna komt: dezelfde
app als **webapp op een eigen Windows-server met IIS**, voor teams die samen beheren. De Desktop blijft een volwaardige
editie, voor wie geen server nodig heeft en als noodoplossing als de server niet bereikbaar is.

Dit is een plan voor later. Details kunnen nog veranderen door wat we in 1.0 leren.

## 1. Waarom een webversie

| | Desktop | Server |
| --- | --- | --- |
| Waar | Op elke beheer-pc apart | Eén server; openen in de browser, ook op een pc zonder installatie |
| Aanmelden | Per pc, met de link in de app | **Single sign-on met Microsoft 365**: MFA en Conditional Access gelden zoals voor elke Microsoft-app |
| Gegevens | Een databank per tenant per pc | Een databank per tenant op de server, **gedeeld door het team** |
| Laden | Wanneer de app open is | **Altijd**: de server laadt op de achtergrond, ook 's nachts; momentopnamen zonder gaten |
| Waarschuwingen | Bij het openen van de app | **Meteen**: mail of Teams-bericht bij een nieuwe beheerder, een gewijzigd beleid, een verlopend geheim |
| Geplande acties | Klaar bij het openen | Klaar voor het hele team, met een melding |

Wat niet verandert: elke wijziging is een plan met voorbeeld en bevestiging, de rollen gelden in de motor, geplande
acties worden nooit vanzelf uitgevoerd, de app kan niet meer dan de persoon die ze gebruikt.

## 2. Architectuur

```
Browser  --HTTPS + websocket-->  IIS: Moss365.Server (ASP.NET Core, Blazor Server)
                                   |- aanmelden: Entra ID (OpenID Connect), rollen in het token
                                   |- dezelfde schermen als de Desktop (Moss365.UI)
                                   |- achtergronddienst per tenant: laden, momentopnamen, waarschuwingen
                                   |- een databank per tenant (zoals de Desktop)
                                   '- technisch log in de databank en het Windows-logboek
   lezen:                              schrijven:
   Graph met de identiteit van de      Graph en Exchange namens de aangemelde persoon (on-behalf-of)
   server (alleen-lezen rechten)        AD namens de aangemelde persoon (Kerberos-delegatie) of
   Exchange met een certificaat         met een serviceaccount met beperkte rechten (keuze S3)
   AD met een gMSA (alleen lezen)
```

- **Lezen** gebeurt met de identiteit van de server: een Lezer heeft zelf geen beheerrechten nodig.
- **Schrijven** gebeurt namens de persoon zelf: de audit van Microsoft toont de echte naam, en wie in Entra of AD niets
  mag, kan ook via de app niets.
- **Meerdere tenants**: een databank en een achtergronddienst per tenant; een gebruiker ziet enkel de tenants waar hij
  een rol heeft, met zijn rol per tenant. Een nieuwe rol **Serverbeheerder** voegt tenants toe en ziet de gezondheid
  van alle tenants, maar niet hun gegevens.

## 3. Keuzes

| # | Keuze | Voorstel |
| --- | --- | --- |
| S1 | Toegang van buitenaf | Enkel op het interne netwerk en via VPN; later eventueel Entra Private Access of Application Proxy |
| S2 | Naam en certificaat | Een eigen naam per organisatie (bv. `moss365.<domein>`), certificaat van de interne CA of publiek |
| S3 | Schrijven in AD | Kerberos-delegatie (AD ziet de echte persoon) als het in de omgeving werkt; anders een gMSA met gedelegeerde rechten en de persoon in het logboek |
| S4 | Versleuteling op de server | Zoals de Desktop: AD-groep (DPAPI-NG) of Key Vault (hoofdstuk 6 van de 1.0-roadmap) |
| S5 | Waarschuwingen | Mail via Graph (een eigen postvak voor Moss365) en/of een Teams-kanaal |
| S6 | Vier ogen | Optioneel per actie (bv. Conditional Access aanzetten, een bevoorrecht account wijzigen): een tweede beheerder keurt goed |

## 4. Stappen

| Stap | Inhoud | Klaar als |
| --- | --- | --- |
| **S1 Webhost** | `Moss365.Server`: aanmelden met Entra ID, rollen uit het token, dezelfde schermen; de toestand gesplitst in gedeeld (per tenant) en persoonlijk (per gebruiker) | De schermen werken in de browser met SSO, met de rechten per rol |
| **S2 Verbindingen vanaf de server** | Lezen met de identiteit van de server; schrijven namens de gebruiker (on-behalf-of voor Graph en Exchange, S3 voor AD), met een technische proef aan het begin | Een Helpdesk-medewerker zet een wachtwoord terug via de webapp, en de audit van AD en Entra toont zijn naam |
| **S3 Achtergronddienst** | Laden per tenant volgens een schema, momentopnamen, waarschuwingen (S5), dagelijkse samenvatting | Een nieuwe beheerder in Entra geeft binnen het uur een waarschuwing |
| **S4 Meerdere tenants op de server** | Tenants toevoegen (de wizard van de Desktop), rol per tenant, Serverbeheerder, isolatie getest | Een gebruiker van tenant A kan via geen enkele URL of knop gegevens van B zien |
| **S5 Beveiliging en installatie** | Installatiescript voor IIS, certificaat, gMSA; versleuteling (S4); hardening; back-up en hersteltest; een controlepagina | Een installatie op een nieuwe server met enkel de handleiding |
| **S6 Pilot** | Een organisatie gebruikt de Server naast de Desktop | Na een maand geen blokkerende problemen |
| **Server 1.x** | Vier ogen (S6), aanvraagformulieren voor HR en diensthoofden, een API voor koppelingen | Volgens de noden van de pilot |

## 5. Risico's

| Risico | Aanpak |
| --- | --- |
| On-behalf-of of Kerberos-delegatie werkt niet zoals verwacht | Technische proef aan het begin van S2; terugvaloptie per verbinding |
| Een centrale server met persoonsgegevens van meerdere organisaties | Een databank per tenant, versleuteld; rollen per tenant; de server als kritiek systeem beschermen; DPIA per organisatie |
| Blazor Server vraagt een stabiele verbinding | Websockets toestaan; automatisch opnieuw verbinden; formulieren bewaren |
| Eén server, geen tweede voor beschikbaarheid | Bewust eenvoudig; back-up en hersteltest; de Desktop als noodoplossing |
