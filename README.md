# Moss365

**Moss365** is a management app for **Active Directory, Entra ID and Exchange Online** (Microsoft 365), for IT teams
that manage one or more organisations (tenants). Users, groups, shared mailboxes, rooms, guests, licences, security and
Conditional Access on one screen, with the history of the organisation over time.

> **Status**: in preparation. The app exists as an internal version (0.9) and moves to this public repository following
> the roadmap. There is no download here yet.

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
