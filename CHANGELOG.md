# Changelog

Every version becomes a release on GitHub; the section under the version is the text of that release.

## 0.16.0

Encryption per tenant (second part of step R6 of the [roadmap](docs/roadmap-1.0.md); details in
[docs/encryption.md](docs/encryption.md)).

- **Versleuteling** (Settings > Databank): encrypt the database of a tenant with SQLCipher (AES-256). Off by default
  (decision U7). Three ways to open it:
  - **Windows account**: nothing to type; only you, signed in on this PC;
  - **Password**: asked when the tenant opens (at start, or when switching to it);
  - **AD group**: every member of the group, on a domain PC.
- **Recovery key**: shown once (type its last group to confirm), kept nowhere by the app. It opens the database on its
  own: another Windows user, a forgotten password, another PC, a lost `moss365.db`.
- Change the protection, make a **new key** (the old recovery key stops working) or turn encryption off again. Every
  switch makes a checked copy first; the copies from before database updates are removed then.
- **Backups** of an encrypted tenant are encrypted too; restoring asks for the recovery key.
- The study of option D (the Microsoft 365 sign-in via Azure Key Vault) is done: it works, but needs an Azure
  subscription; it comes after 1.0.
- Under the hood: SQLite now comes with SQLCipher (`SQLitePCLRaw.bundle_e_sqlcipher`); databases that are not
  encrypted open as before.

## 0.15.0

Maintenance of the database (first part of step R6 of the [roadmap](docs/roadmap-1.0.md); encryption follows in 0.16).

- **Databank** (Settings): the size of each part of the tenant database (snapshots, change log, access log, technical
  log, script runs, inventories, Conditional Access backups, scripts, settings), the integrity check and compacting.
- **Retention per tenant** (decision U9): snapshots daily for a year, then one per month; change log 5 years (was 2);
  access log 5 years; technical log 90 days; script runs a year; copies made before a database update 30 days (the
  newest always stays). All adjustable on the screen.
- **Cleaning up** always shows first what would go, per part; you choose and confirm. The change log and the access log
  go to a CSV in `Documents\Moss365\<tenant>\Archief` first; only then are they removed. The clean-up itself is in
  the change log.
- **Thinning snapshots** keeps every date that stays exact: the organisation on such a date looks the same as before;
  only the in-between versions go.
- The technical log and script runs follow the retention of the tenant when cleaned up at start.

## 0.14.0

Setting up a tenant and this computer (step R5 of the [roadmap](docs/roadmap-1.0.md)).

- **Tenant inrichten** (Settings): a wizard for the open tenant. You type a domain, sign in once as an Application or
  Global Administrator, and the app finds the rest:
  - the tenant ID (from Microsoft, without signing in);
  - the app registration `Moss365`: found and checked, or created. What is missing (permissions, admin consent,
    "assignment required", the profiles as app roles, a group `GG-Moss365-...` per profile, your own profiles
    Administrator and AppAdmin) is a plan with a preview; only the difference, and everything in the change log;
  - the client ID goes into Connections by itself; the domain controller of this PC and your account for Exchange with
    one click;
  - hybrid or cloud only;
  - after the first read: suggestions for the organisation (AD domain, synchronised OUs, departments, offices, group
    prefixes) and candidates for the emergency accounts (cloud-only Global Administrators outside every Conditional
    Access policy). You choose what is taken over.
  The wizard signs in with Microsoft Graph PowerShell, because the app registration may not exist yet. Setting up is
  for the profile AppAdmin. `Register-App.ps1` stays, for whoever prefers PowerShell.
- **Controle van deze pc**: also the clock (sign-in fails at more than five minutes off), PowerShell 7 (installed via
  winget) and the AD module RSAT (installed as a Windows capability; Windows asks for administrator rights). "Install
  everything that is missing" asks once; optionally the app proposes it right after the check. Every installation is
  in the change log.
- **WebView2**: when the runtime is missing, the app offers to download Microsoft's installer and run it.

## 0.13.0

Snapshots and the organisation over time (step R4 of the [roadmap](docs/roadmap-1.0.md)).

- **Every read is a snapshot** in the database of the tenant: a live read (of the parts that were read in full) and
  every inventory folder that is read in. The database only keeps what changed, so a daily read costs little space.
- **The organisation on a date** (menu Geschiedenis): all screens show the organisation as it was then, read only.
  A banner says which date is shown; plans, loading and scripts are off until you go back to now.
- **What changed between two dates**: new, vanished and changed objects (users, groups, mailboxes, rooms, ...), with
  the columns that changed. This also shows what happened outside Moss365.
- **Timeline of a user**: the versions of its AD and Entra account next to the changes made with Moss365, with
  "Terugzetten zoals toen": a plan (preview, confirmation, change log) that puts the attributes and AD groups back as in
  that version. Only what really differs; privileged and dynamic groups follow the usual rules.
- **KPI trends per day, week, month and year** on Statistics. "KPI's herberekenen" computes the value of every day
  again from the snapshots, also for a KPI that was added later.
- Known limits: an inventory folder older than the last snapshot stays an inventory but does not become a snapshot;
  thinning old snapshots (daily for a year, then monthly) comes with maintenance in R6.

## 0.12.1

- **Fixed**: in 0.11.0 and 0.12.0 the pages of a user, a group, an offboarding, a script run and starting a script
  opened empty from a link (the address still used the old Dutch parameter name). A test now checks every address.

## 0.12.0

One database per tenant, and several tenants side by side (steps R2 and R3 of the [roadmap](docs/roadmap-1.0.md)).

- **Several organisations (tenants)**: add, rename and colour them under Settings > Tenants. The name and colour of the
  open tenant are always at the top, and every plan asks "Ja, uitvoeren in <tenant>". Each tenant has its own
  connections, sign-in and tokens; switching reloads the screens, so that no screen keeps data of another tenant.
- **Everything of a tenant in its own database** (`%LOCALAPPDATA%\Moss365\tenants\<id>\tenant.db`): the change log,
  the access log, the technical log (kept 90 days), the organisation, the inventory, Conditional Access backups, own
  scripts, last values, script runs with their output files, scheduled actions, follow-up tasks and view choices. No
  loose files next to it any more; `moss365.db` only holds the list of tenants.
- **Exports per tenant**: `Documents\Moss365\<tenant>\`.
- **Backup and restore** of a tenant: the whole database in one file; restoring always creates a new tenant.
- **Automatic copy** of a tenant database before its schema is migrated (`tenant.db.bak-<date>`).
- **From 0.11**: at the first start the existing data becomes the first tenant (named after the company in your
  organisation). Nothing is deleted: the old files move to `%LOCALAPPDATA%\Moss365\old-0.11\`; remove that folder
  when everything works.
- **The inventory of `Export-Inventaris.ps1`**: read a folder into the open tenant on the start page.
- Script runs: while a script runs its files are in a temporary folder; when it is done they move into the database.
  "Openen" on a file of a run puts a copy in the export folder.

## 0.11.0

The code in English (step R1, part B of the [roadmap](docs/roadmap-1.0.md)). Nothing changes in what the app does.

- **Code, comments and tests in English**: every class, method, property, enum value and file, and every comment in the
  C#, Razor and PowerShell code. The app roles in Entra are now `Moss365.Reader`, `Moss365.Helpdesk`,
  `Moss365.Administrator`, `Moss365.Auditor` and `Moss365.AppAdmin` (with the groups `GG-Moss365-Readers`, ...). The old
  role values (`Moss365.Lezer`, `Moss365.Beheerder`, `Moss365.Appbeheer`) keep working; run `tools/Register-App.ps1`
  again to get the new ones.
- **Your data stays readable**: settings, the organisation, the change log, scheduled actions, follow-up tasks, script
  runs and Conditional Access backups saved by 0.10 or earlier load as before; they are written with the new names the
  next time they are saved. Undo works on change log entries from before 0.11.
- **The sample organisation** (`config/organisatie.json`) uses the new English names.
- `tools/Registreer-App.ps1` is now `tools/Register-App.ps1`.

Still in Dutch: the interface texts (dictionaries in five languages come in R15), the CSV inventory with
`tools/Export-Inventaris.ps1` (R2 moves the inventory into the database) and the keys of the script header (R8).

The download is not signed yet (signing starts at 1.0): Windows SmartScreen will warn. Verify the download with
`SHA256SUMS.txt`.

## 0.10.0

First public release of Moss365, moved over from the internal version 0.9 (step R1, part A of the
[roadmap](docs/roadmap-1.0.md)).

- **Moss365 everywhere**: the program, projects and namespaces (`Moss365.Core`, `Moss365.Connections`, `Moss365.UI`,
  `Moss365.Desktop`), the data folder `%LOCALAPPDATA%\Moss365` and the default name of the app registration.
- **Migrating an existing installation**: at the first start the app looks for a previous installation in
  `%LOCALAPPDATA%` (recognised by its database and organisation settings) and copies it. The old folder stays until you
  remove it.
- **No organisation-specific content**: the sample configuration is a made-up organisation (Contoso), group rules of
  one organisation were removed, test data uses example domains.
- **Script library**: the first generic script (`scripts/FileServer/Remove-OfficeLockFiles.ps1`); more follow in R8.
- **Repository**: EUPL-1.2, contributor licence agreement, security policy, privacy check, CodeQL, Dependabot, and
  releases with a SHA-256 fingerprint (`SHA256SUMS.txt`).

Still in Dutch: names in the code and the interface. The code moves to English in 0.11 (R1, part B); the interface gets
dictionaries in five languages in R15.

The download is not signed yet (signing starts at 1.0): Windows SmartScreen will warn. Verify the download with
`SHA256SUMS.txt`.
