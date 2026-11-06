# Moss365

**Moss365** is a management app for **Active Directory, Entra ID and Exchange Online** (Microsoft 365), for IT teams
that manage one or more organisations (tenants). Users, groups, shared mailboxes, rooms, guests, licences, security and
Conditional Access on one screen, with the history of the organisation over time.

> **Status**: on the way to 1.0. Every step of the [roadmap](docs/roadmap-1.0.md) is a prerelease (0.x) on the
> [Releases](https://github.com/jef-cesar/Moss365/releases) page. The interface is still in Dutch until the language
> step (R15).

## Getting started

1. Download the zip of the newest release and check it against `SHA256SUMS.txt` (until 1.0 the app is not signed).
2. Unzip it anywhere and start `Moss365.exe` (Windows 10 or 11; the app offers to install the WebView2 runtime if it is
   missing).
3. **Instellingen > Tenant inrichten**: type a domain of your organisation, sign in once as an Application or Global
   Administrator, and confirm the plan. The app creates the app registration `Moss365`, the profiles and their groups,
   and fills in the connection settings itself. Then **Controle van deze pc** installs what is missing.

## How Moss365 works

- **Every change is a plan**: first a preview, then a confirmation, then the change log. Undo wherever possible.
- **The app can do no more than your own account**: delegated permissions, no secrets on disk or in a log.
- **Roles** in the app (Reader, Helpdesk, Administrator, Auditor, AppAdmin), enforced down to the engine.
- **Multiple tenants**, strictly separated, each with its own database.
- **Snapshots**: what the organisation looked like on any date, and the trend of every indicator.
- **Five languages** for the interface: English, Dutch, Brazilian Portuguese, French and German, following the Windows
  display language by default.
- **No telemetry**: data stays with the organisation.

## Roadmap

- [Roadmap to 1.0 (Desktop)](docs/roadmap-1.0.md): what comes first.
- [Roadmap Moss365 Server (web version)](docs/roadmap-web.md): after that.

## Licence

Moss365 is open source under the [European Union Public Licence v. 1.2](LICENSE) (EUPL-1.2). Outside contributions are
accepted under a contributor licence agreement, so paid support, hosting or a commercial licence can be offered next to
the open version later. The app itself stays free.

Moss365 is not affiliated with Microsoft. Microsoft 365, Entra ID and Exchange Online are trademarks of Microsoft.
