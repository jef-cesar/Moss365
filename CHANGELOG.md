# Changelog

Every version becomes a release on GitHub; the section under the version is the text of that release.

## 0.19.0

Improvements asked for after 0.18.

- **The sign-in box stands out**: warm amber with a thick border that pulses gently (not with "reduce motion"), in
  light and dark mode, so that it no longer blends into the screen.
- **Status bar** at the bottom of every screen: the version, how fresh the data is (live with date and time, the
  inventory with its date, or the date you are looking at in Geschiedenis), online or offline, and a link to GitHub.
- **AD, Microsoft 365, or both, per tenant** (Settings > Tenants > Naam en kleur): whatever a tenant does not have, the
  app leaves out. Menu items (shared mailboxes, rooms, guests, security, Conditional Access, set-up wizard, AD versus
  Entra), the sections on the user page and Statistics, the connection settings and the load buttons follow the
  choice; nothing reads from or writes to a system the tenant does not have. Existing tenants keep both.
- **Edit groups and mailboxes from the user page**: tick groups to remove, add AD or cloud groups, take away or give
  access to shared mailboxes (through the access group of the mailbox, as its model wants). One plan with preview,
  confirmation and change log; dynamic groups cannot be ticked, privileged groups still need the profile
  Administrator.
- **Roadmap**: Google Workspace after 1.0 (chapter 15, working name MossSpace): the same app with Google as a system of a
  tenant; open decision U13.
- Conditional Access > Beheren no longer points to an internal document.

## 0.18.0

The script library from this repository (step R8 of the [roadmap](docs/roadmap-1.0.md)). Closes #10.

- **Bibliotheek bijwerken** (Scripts): shows which scripts in `scripts/` of the repository are new or changed for this
  tenant, with the difference line by line, and downloads after confirmation. New scripts reach you without a new
  version of the app.
- **Checked**: `scripts/index.json` lists every script with its version and SHA-256. A downloaded script whose SHA-256
  differs from the index is refused; so is a path outside the library. From 1.0 the scripts and the index are also
  signed (U8). A downloaded script never runs by itself: as always, first a preview and a confirmation.
- **Pinned per tenant**: a pinned script stays on its version in that tenant, also when the app or the library brings a
  newer one. Release it to follow the library again.
- A script can ask for a minimum version of Moss365 (`MinimumApp`); an older app does not offer it.
- The update goes to the change log (which scripts, which version, which SHA-256).
- **Script headers in English**: `Name`, `Description`, `Version`, `MinimumApp`, `Connection`, `Writes`, `Variable`.
  Scripts with the Dutch header (`Naam`, `Verbinding`, ...) keep working; the app writes the English one when you save.
- The source of the library follows the own source of the updates (a shared folder), or GitHub.
- The tests check every script in `scripts/`: ASCII only, a header with a version, `-WhatIf` for a script that
  changes something, no fixed e-mail addresses, and an index that matches. How to add one: `CONTRIBUTING.md`.

## 0.17.0

Automatic updates (step R7 of the [roadmap](docs/roadmap-1.0.md), decision U4: Velopack). Closes #9.

- **Install with `Moss365.Desktop-stable-Setup.exe`**: for your own account, without administrator rights. The app is
  installed in `%LOCALAPPDATA%\Moss365.Desktop`; your data stays where it was (`%LOCALAPPDATA%\Moss365`), also when
  the app is removed. The zip still works without installing, but does not update itself.
- **Updates** (Settings > Controle van deze pc): search now, see what is new, **Update now** or **Later**. The download
  is checked against the SHA-256 published with the release; from 1.0 the packages are also signed (U8). Small delta
  packages where possible.
- **Never during work**: no update while a task or script runs, or a plan, form or setting is still open (in any
  tenant). Before installing: a copy of every tenant database and of `moss365.db` (cleaned up with the other copies on
  the Databank screen); the update goes to the change log.
- **Channels**: Stable and Preview (versions such as 0.17.0-preview.1). Until 1.0 every installation follows Preview.
  Going from Preview to Stable can install an older version; the screen says so.
- **Back to an earlier version**: choose a version of the channel and install it.
- **Own source**: a shared folder with the releases, for organisations without access to GitHub.
- **Fixed**: searching for a new version always said the releases were private: GitHub leaves prereleases (every 0.x
  release) out of "latest". The app now reads the list of releases, per channel.

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
