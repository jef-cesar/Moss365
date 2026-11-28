# Roadmap to Moss365 1.0 (Desktop)

**Moss365** is a management app for Active Directory, Entra ID and Exchange Online (Microsoft 365), for IT teams that
manage more than one organisation (tenant), or want to manage one organisation thoroughly. It started as the internal
management app of a local government (versions 0.5 to 0.9) and becomes **universal** in this public repository: no
fixed names, rules or structures of one organisation any more, everything configurable per tenant.

This document is the plan towards **1.0: the Desktop edition** (Windows app). The web version (Server) comes after
that and has its own roadmap: [roadmap-web.md](roadmap-web.md).

Contents:

1. [What Moss365 is and what never changes](#1-what-moss365-is-and-what-never-changes)
2. [What changes compared to the internal version](#2-what-changes-compared-to-the-internal-version)
3. [Storage: one database per tenant, with snapshots](#3-storage-one-database-per-tenant-with-snapshots)
4. [Environment: setting up a tenant](#4-environment-setting-up-a-tenant)
5. [Environment: this computer](#5-environment-this-computer)
6. [Environment: database encryption and maintenance](#6-environment-database-encryption-and-maintenance)
7. [Scripts from this repository](#7-scripts-from-this-repository)
8. [Automatic updates](#8-automatic-updates)
9. [Organisation: universal settings, job profiles and logon scripts](#9-organisation-universal-settings-job-profiles-and-logon-scripts)
10. [Proposals: what AD and Microsoft 365 administrators want](#10-proposals-what-ad-and-microsoft-365-administrators-want)
11. [Open source, with room for paid support](#11-open-source-with-room-for-paid-support)
12. [Decisions](#12-decisions)
13. [The steps to 1.0](#13-the-steps-to-10)
14. [Risks](#14-risks)
15. [Google Workspace next to AD and Microsoft 365](#15-google-workspace-next-to-ad-and-microsoft-365)

---

## 1. What Moss365 is and what never changes

Moss365 brings AD, Entra ID and Exchange Online together on one screen: users, groups, shared mailboxes, rooms,
guests, licences, security (MFA, admin roles, Conditional Access), statistics, scripts, scheduled onboarding and
offboarding, and a log of everything the app changed.

What stays in every version, guarded by the tests:

- **Every change is a plan**: first a preview, then a confirmation, then the log. Undo wherever possible.
- **Never store secrets**: no passwords or tokens on disk or in a log. An initial password only lives in memory.
- **The app can do no more than your own account.** Delegated permissions: what you may not do in Entra, Exchange or
  AD, you cannot do through Moss365 either.
- **Roles in the app** (Reader, Helpdesk, Administrator, Auditor, AppAdmin), enforced per screen, per button and in the
  engine.
- **Scheduled actions never run by themselves**: when the time comes, the app shows the plan and waits.
- **Conditional Access never without emergency accounts**: an enabled policy must exclude them; the engine itself
  checks this.
- **Offboarding never deletes**: disable, remove from groups, move.
- **No telemetry.** The app sends nothing to its makers. Data stays with the organisation.
- **Data under your own control** (the motto, "data in eigen beheer"): the Desktop stays a full edition after 1.0, also
  next to the Server; no feature needs a server or a service of the makers, and the makers never keep an organisation's
  data (U14, U21).
- **No personal data in this repository**: test data is made up; examples use `contoso.com` or `example.org`.

## 2. What changes compared to the internal version

| | Internal version (0.9) | Moss365 1.0 Desktop |
| --- | --- | --- |
| Name | Moss365 for the program, roles and groups; the old project name still in the data folder, the app registration and the namespaces | **Moss365 everywhere**: also data folder, namespaces, app registration and Windows event log |
| Language | Dutch code, documentation and interface | **English** code and documentation; the interface in five languages (R15) |
| Organisations | One | **Multiple tenants**, strictly separated, each with its own database |
| Storage | SQLite for the log and settings; inventories as CSV folders; backups as JSON | **Everything in the tenant database**, including inventories, technical log, backups and script runs |
| History | A daily count of a few indicators | **Snapshots of the whole organisation**: what it looked like on any date, and the trend of every indicator |
| Setup | A configuration file and a script for the app registration | **Wizard per tenant**: finds the app registration "Moss365" (or creates it) and fills in everything itself |
| Organisation logic | Tuned to one organisation (departments, OUs, naming rules, group names) | **Universal**: everything configurable, with suggestions from the tenant's own data. Job profiles and logon scripts |
| This computer | A check screen | Check at startup; missing components are **installed** (with confirmation) |
| Database | Not encrypted | **Encryption** (choice per tenant), compacting and clean-up |
| Scripts | Shipped with the download | **Synchronised** with the library in this repository, verified |
| Updates | A notice when a new version exists | **Checked and installed at startup**, with rollback |

### Everything is called Moss365

| What | Becomes |
| --- | --- |
| Program | `Moss365.exe` |
| Projects and namespaces | `Moss365.Core`, `Moss365.Connections`, `Moss365.UI`, `Moss365.Desktop`, later `Moss365.Server` |
| Data folder | `%LOCALAPPDATA%\Moss365` (temporary files in `%TEMP%\Moss365`) |
| Registry (when the app writes anything, e.g. the installer) | `HKCU\Software\Moss365` |
| Windows event log | Application, source `Moss365` |
| App registration in Entra | Always **`Moss365`** |
| Roles (app roles) | `Moss365.Reader`, `Moss365.Helpdesk`, `Moss365.Administrator`, `Moss365.Auditor`, `Moss365.AppAdmin` |
| Groups for the roles | `GG-Moss365-Readers`, `GG-Moss365-Helpdesk`, ... (prefix configurable per tenant) |
| Script header | `<#MOSS365 ... #>` |
| Environment variables for scripts | `MOSS365_*` |
| Databases | `moss365.db` (the app) and `tenant.db` (per tenant) |

**Migrating** an existing installation of the internal version: at first start Moss365 finds the old data folder and
offers to import it as the first tenant (settings, log, schedule, scripts, inventories). The old folder stays until you
remove it yourself. An existing app registration with the old name is recognised; the wizard offers to rename it to
`Moss365` (client ID, permissions and assignments stay). The app roles keep their IDs, so assignments are not lost; the
app accepts both the old (Dutch) and the new role values. Existing groups for the roles are recognised and kept.

## 3. Storage: one database per tenant, with snapshots

### Layout

```
%LOCALAPPDATA%\Moss365\
  moss365.db                the app: list of tenants, app settings, update status (no tenant data)
  tenants\<tenant-id>\
    tenant.db               everything of that tenant (see below)
    tenant.db.bak-<date>    automatic copy before a migration or update
  library\                  synchronised scripts (shared, no data)
```

**`tenant.db`** holds everything of that tenant:

| Part | Contents |
| --- | --- |
| Settings | Connection (tenant ID, client ID, domain controller), organisation settings, job profiles, emergency accounts |
| Snapshots | All objects and relations that were read, with their versions over time (see below) |
| Change log | Every executed change: who, when, what, before and after, result (append only) |
| Access log | Who exported, who viewed sign-ins or security data |
| Technical log | What happened, counts and errors; never names, parameters or secrets |
| Work | Scheduled actions, follow-up tasks, script runs and their output |
| Backups | Conditional Access policies and named locations, logon scripts |
| View | Columns, filters and choices per app user |

Never data of two tenants in one database, list, plan or export.

### Snapshots: the organisation over time

Every read (full or quick) becomes a **snapshot**. The database does not store everything again each time, only
**what changed**: every object (user, group, mailbox, room, guest, policy, licence, ...) and every relation
(membership, mailbox permission, manager, role) has versions with "valid from" and "valid until".

```
Snapshot  (id, time, source: AD / Entra / Exchange / full / quick)
Object    (kind, key)
Version   (object, from-snapshot, until-snapshot, content, fingerprint)
Relation  (kind, from, to, from-snapshot, until-snapshot)
```

What that makes possible:

- **The organisation on a date**: every screen (lists, statistics, Conditional Access) can show a date in the past
  ("as of 1 March"), read only.
- **What changed between two dates**: new and removed accounts, who changed department, who joined a privileged group,
  which policies changed. Including changes made outside Moss365.
- **The timeline of one object**: when someone joined a group, when their job title changed, when the account was
  disabled (with the change log next to it: was it Moss365 or someone else?).
- **The trend of every indicator**: every KPI can be recalculated for any snapshot, even a KPI that is added later.
  Statistics shows the evolution (day, week, month, year) and the difference with a previous period.
- **Restore from the past**: attributes or memberships of an object as they were on a date, as a plan.

Size: an organisation with a few hundred accounts changes little per day; the database grows by a few MB per year. For
speed the app keeps a daily table of computed KPIs (counts only); it can always be rebuilt from the snapshots.

## 4. Environment: setting up a tenant

An **Add tenant** wizard does the work; you only fill in what the app cannot find itself.

1. **Tenant**: enter a domain (e.g. `contoso.com`) or the tenant ID. The app looks up the tenant ID itself (Microsoft's
   OpenID metadata).
2. **Sign in** with an admin account of that tenant (the link appears in the app, as today).
3. **App registration `Moss365`** (always that name):
   - if it exists, the app reads the client ID, permissions, redirect URI, app roles and assignments, and fills in
     everything;
   - if something is missing (a permission, a role, admin consent), the app shows the difference as a plan;
   - if it does not exist, the app creates it, as a plan (what `Register-App.ps1` does today). That needs an admin with
     the Application Administrator or Global Administrator role, once.
4. **Groups for the roles** (`GG-Moss365-...`): find them, offer to create them and link them to the roles.
5. **Active Directory**: if the PC is joined to the domain, the app finds the domain and a domain controller itself. A
   tenant without AD (cloud only) is fine too; the AD parts then disappear from the screens.
6. **Hybrid or not**: the app sees whether AD is synchronised (Entra Connect or Cloud Sync) and so knows where to write.
7. **Exchange Online**: check the module and sign in with the same account.
8. **Organisation settings**: the app reads the data and **suggests** (see chapter 9): departments, offices, OUs, naming
   rules, group prefixes, the shared mailbox model, dynamic groups. You confirm or adjust.
9. **Emergency accounts**: the app suggests candidates (cloud-only Global Administrators not covered by any policy).
10. **First full read**: the first snapshot.

Each tenant gets a **name and colour** in the bar, and every plan asks for confirmation with the tenant name, so you
never work in the wrong organisation by mistake.

## 5. Environment: this computer

At every start (and in the **This computer** screen) the app checks:

| Component | Used for | If missing |
| --- | --- | --- |
| WebView2 runtime | The screens | Install (Microsoft's bootstrapper) |
| PowerShell 7 (5.1 is enough for most scripts) | Exchange and scripts | Install via winget |
| Module `ExchangeOnlineManagement` (minimum version) | Exchange | `Install-Module -Scope CurrentUser` (no admin rights needed) |
| Module `Microsoft.Graph` (parts) | Scripts from the library | Same |
| RSAT Active Directory | Scripts only (the app itself reads AD directly) | Install the Windows capability (needs admin rights: Windows asks for confirmation) |
| Connection to the domain controller (LDAP, Kerberos), Graph and Exchange | Reading and writing | Explanation of what is wrong (port, proxy, DNS) |
| PC clock | Kerberos and sign-in fail when it drifts | Warning |
| Outdated versions | Bugs and security | Update |

Installing **always asks for confirmation**, only from official sources (PowerShell Gallery, winget, Microsoft), and is
written to the change log. Optionally, turn on **"Install missing components right away"** in Settings; the app then
asks only once.

## 6. Environment: database encryption and maintenance

### Encryption (choice per tenant)

The database holds personal data and the history of an organisation. It can be encrypted with SQLCipher (AES-256, open
source); the key is never stored in the database, the repository or a log.

| Option | How | Good for | Mind |
| --- | --- | --- | --- |
| **A. Windows account** (DPAPI) | The key is encrypted with your Windows sign-in | Nothing to type; protects against a stolen disk or another user | Anyone signed in as you can open it |
| **B. Password** | A password when opening (key derived with PBKDF2 or Argon2) | Shared PCs, portable installations | Password lost = database lost; so always a **recovery key** (print it, keep it in the safe) |
| **C. AD group** (DPAPI-NG) | The key is encrypted for an AD group (e.g. `GG-Moss365-Administrators`) | Several admins on the same PC or server, in a domain | Domain-joined PCs only |
| **D. Microsoft 365 sign-in (SSO)** | The key is "wrapped" with a key in the tenant's **Azure Key Vault**; unwrapping needs your Entra sign-in and the Key Vault Crypto User role (granted via the Moss365 groups) | Follows the tenant's permissions and MFA; whoever leaves the group can no longer open it | Needs an Azure subscription (a few cents a month); cannot be opened offline, so also a recovery key |

**Study for D** (at the start of step R6):

- A Microsoft token cannot itself be a key: it changes at every sign-in. Hence a key that never leaves Key Vault and only
  wraps and unwraps, with the user's identity.
- Without Azure there is no safe place in Microsoft 365 to keep a key (a file in OneDrive or SharePoint is readable by
  anyone with access). So without Azure: A, B or C.
- To investigate: working offline (an unwrapped key kept in memory for a while), key rotation (`PRAGMA rekey`), and how
  this works on the Server (same approach, or C with the service account).

Decided (U7): **no encryption by default**; can be turned on per tenant with A, B or C. **D after a successful study**. Done in 0.16; the study of D is in [encryption.md](encryption.md): D comes after 1.0.
Changed by U24 (26 November 2026): a **new tenant is encrypted with the Windows account (A) by default**; turning it
off stays possible. Done in 0.39.0.

### Maintenance

A **Database** screen per tenant:

- size per part (snapshots, change log, technical log, script runs, backups);
- **compact** (`VACUUM`, also automatically after a clean-up);
- **clean up old data** according to a retention period per part, always as a preview first and, for the change log,
  an archive first (CSV or a separate database). Defaults (U9), adjustable per tenant:
  - snapshots: daily for 1 year, then one per month;
  - change log: 5 years (whatever the organisation agrees with its DPO, shorter or longer);
  - technical log: 90 days; script runs: 1 year;
- **backup and restore** of a tenant (one file, encrypted like the database);
- **integrity check** (`PRAGMA integrity_check`) and an automatic copy before every migration or update.

## 7. Scripts from this repository

- The library lives in this repository, in `scripts/`, with the header `<#MOSS365 ... #>` (name, description,
  connection, writes or not, variables, minimum app version).
- The workflow tests every script (Pester: ASCII, `-WhatIf` for scripts that change things, no names or addresses) and
  builds an **index** (name, version, SHA-256). From 1.0 it also **signs** the scripts and the index (Authenticode, U8).
- In the app, **Update library** shows what is new or changed (with the diff) and downloads after your confirmation.
  The app checks the fingerprint (and from 1.0 the signature) and refuses a script that does not match.
- A downloaded script never runs by itself: as today, first a preview (`-WhatIf`) and a confirmation.
- Per tenant you can pin a version ("this tenant stays on version X of this script").
- Your own scripts stay in the tenant database. Sharing a script with everyone happens through a pull request to this
  repository.

## 8. Automatic updates

- At startup the app checks GitHub for a newer version (without blocking your work).
- You see what is new (the changelog of that version) and choose **Update now** or **Later**.
- The update has a published **fingerprint** (SHA-256) that the app verifies; from 1.0 it is also **signed**
  (Authenticode, U8).
- Never update while a plan or task is running. Before the update: a copy of every database; after it: the migrations.
- If the new version fails, go back to the previous one (it is kept).
- Channels: **stable** (default) and **preview**.
- For organisations without access to GitHub: a custom source (a shared folder with the releases).
- Technology (U4): **Velopack** (open source, small delta updates, per-user install without admin rights).
- Done in 0.17: the app installs in `%LOCALAPPDATA%\Moss365.Desktop` (pack id `Moss365.Desktop`), so that removing it
  never touches the data in `%LOCALAPPDATA%\Moss365`. Until 1.0 the default channel is preview.
- Decided (U23, 26 November 2026): every release waits for the maintainer's approval in GitHub (an environment with a
  required reviewer) before it is published and offered as an update. **Reverted on 27 November 2026**: the maintainer
  does not approve releases for now, so a new version on `main` is published right away; raising the version is the
  decision to release.

## 9. Organisation: universal settings, job profiles and logon scripts

### Everything configurable per tenant

What was fixed in the internal version becomes a setting, with a suggestion from the tenant's own data:

| Setting | Suggested from the data |
| --- | --- |
| Departments and their divisions | The values found in AD and Entra, grouping spellings that barely differ |
| Offices and cities | Same |
| Organisation name (Company) | The most used value |
| OUs: new users, offboarded accounts, new groups, service accounts | The OUs with the most (disabled) accounts |
| Naming rules: UPN, sAMAccountName, display name, e-mail | The pattern most accounts follow (e.g. `firstname.lastname`) |
| Group names: prefixes per kind (security, distribution, department, access) | The prefixes already in use |
| Shared mailboxes: access via a group (and its naming pattern) or direct | What most mailboxes have today |
| Dynamic groups (never filled by hand) | From Entra |
| Privileged groups | The built-in ones (Domain Admins, ...) plus what you add |
| Licences (readable names, default per kind of user) | From the subscriptions |
| Rooms: the standard settings | The most common settings |
| Emergency accounts | Candidates (see chapter 4) |

A tenant can also start from a **template** (e.g. "local government", "school", "small business"), and a tenant can
export its settings and import them into another.

### Job profiles

A job profile describes what someone in a job gets:

- AD groups, cloud groups and Microsoft 365 groups (Teams);
- access to shared mailboxes (via their access group);
- licences;
- OU, department, office and other attributes;
- logon script.

Use:

- **New user**: pick a job profile, and the plan contains everything at once.
- **Apply profile** to an existing user: the plan shows what gets added and what can go.
- **Deviations**: who has more or less than their profile (a list for clean-up or audit).
- **Mirror user** stays, but a profile is better: it does not depend on one person.
- Profiles have versions (snapshots), so you can see which version was applied when.

Done in 0.22: profiles on Organisatie (also made from a user), New user with a profile, the Functieprofielen screen
(who holds a profile, by assignment or job title; what is missing or more; apply as a plan, with what is more only
proposed), the version and date per person, and the profile on the user page.

### New users in the cloud

For a tenant with only Microsoft 365 (no AD), **New user** creates the account directly in Entra ID through Graph, with
its licence, groups and mailbox access in the same plan (no follow-up after synchronisation). The first password is
shown once, never stored or logged; the user changes it at the first sign-in. **Done in 0.31**, also as a choice for a
hybrid tenant; mail groups (and so access to shared mailboxes) become a follow-up task until the mailbox exists.

(Naming: to avoid confusion with the app's roles, these are called **job profiles**, and the app's access levels are
called **roles**.)

### Logon scripts

A **Logon scripts** screen per tenant:

- all scripts in the NETLOGON share, and which users have which script (`scriptPath`);
- references to scripts that do not exist, and scripts nobody uses;
- **view and edit** in the app's code editor: saving is a plan with the diff, the previous version is kept (and can be
  restored);
- what a script does in plain language where possible (e.g. the network drives it maps);
- change the logon script of users (one or a selection), as a plan;
- logon scripts assigned through Group Policy (GPO) are shown, but not changed.

Done in 0.32 (R9 is complete): the Logonscripts screen with all of the above; versions are kept in the change log.

## 10. Proposals: what AD and Microsoft 365 administrators want

What administrators often miss in practice. All of them are in the steps to 1.0 (chapter 13); each heading names its
step (R10 to R22; R16 to R20 were added on 26 November 2026, U15, and R21 and R22 on 27 November 2026, U27). Everything that changes something goes through a
plan as always; everything that shows sensitive data (recovery keys, passwords, message traces) is written to the access
log and requires the right role.

### Helpdesk and daily work (R10)

| Proposal | Why |
| --- | --- |
| **Temporary Access Pass** for someone who lost their phone (one-time or with a lifetime) | The most frequent helpdesk question since MFA |
| **BitLocker recovery key and LAPS password** lookup (AD and Entra), with the access log | Today admins search three portals |
| **Where does a lockout come from**: the PC or device, from the domain controllers' events | An old password on a phone locks an account again and again |
| **Password expires soon**: a list, and an e-mail reminder to the user (or their manager) | Especially for people rarely on the network (home working, VPN) |
| **Message trace** for one user: did the mail arrive, and if not, why? | The second most frequent question |
| **Out-of-office and forwarding** for someone else, with an end date | Illness and leave |
| **Member of which groups, and why**: nested groups expanded, "through which group does this person have access" | Answering access questions without searching |
| **Quick sign-in at every start**: an encrypted token cache per tenant (optional, Windows account) | No new sign-in every time |
| Search everything with **Ctrl+K** (users, groups, mailboxes, PCs, scripts, screens) | Faster than menus |

Done in 0.33 (R10): all of the above; devices are found on the BitLocker and LAPS page (the device list itself comes with
R13).

### Security (R11)

| Proposal | Why |
| --- | --- |
| **Forwarding to external addresses** (mailboxes and inbox rules) | A classic sign of a compromised account |
| **App registrations and enterprise apps**: expiring secrets and certificates, apps with broad permissions, user consents, unused apps | An expired secret breaks integrations; a risky consent is a leak |
| **Alerts from the snapshots**: new member of a privileged group, changed policy, new admin role, new app consent, a disabled account enabled again | Also sees what happened outside Moss365 |
| **AD security check** (light): accounts with an SPN (kerberoasting), unconstrained delegation, "password not required", reversible encryption, old krbtgt, adminCount orphans, outdated operating systems | What an attacker looks for first |
| **AD health**: domain controllers, replication, FSMO roles, time | See problems before users notice |
| **Group Policy (GPO)**: which policies are linked where, disabled and empty policies (read only) | An overview the standard tools make hard |
| **Microsoft Secure Score** with its trend and the actions that gain the most | One number for management |
| **Admin roles and PIM**: who has what permanently, who is eligible | Least privilege |
| **Conditional Access editor**: change every condition of an existing policy (users and groups, apps, platforms, locations, client apps, device filters, grant and session controls), with the same checks as today (emergency accounts excluded, never lock yourself out, a preview with the difference, never delete) | Since 0.9 the app switches policies on and off, manages exclusions, named locations, templates and backups. **Done in 0.30** (ahead of R11): every condition, a free new policy, a copy, the impact on who falls under it, and the JSON |
| **Weak MFA methods** (SMS, phone) and who has no stronger one yet | Becoming phishing resistant |
| **Risky users and sign-ins** (requires Entra ID P2) | React quickly |

Done in 0.34 (R11): the Beveiligingscontroles screen with all of the above; AD health as a library script.

### Licences and costs (R12)

| Proposal | Why |
| --- | --- |
| **Licences that bring nothing**: disabled accounts with a licence, shared mailboxes with a licence, duplicate plans, never-used licences | Direct savings |
| **Cost per month** (price per licence configurable), per department and division, with its trend | For the budget |
| **Licences according to the job profile**: who has more or less than their profile | Clean-up and fair distribution |

Done in 0.35 (R12): the Licenties en kosten screen with all of the above; the KPI "licentie-verspild" has its trend.

### Devices, Teams and SharePoint (R13)

| Proposal | Why |
| --- | --- |
| **Computers in AD**: no longer signed in, operating system and version, which OU | Clean-up and security |
| **Devices in Intune**: non-compliant, not seen any more, version, owner | Conditional Access with "compliant device" only makes sense with this |
| **Teams and Microsoft 365 groups without an owner**, with guests, no longer used | The most heard complaint about Teams |
| **SharePoint sites**: external sharing, storage, unused | Data leaks and costs |

Done in 0.36 (R13): the Toestellen screen (computers in AD with systems, OU and a plan to disable stale ones; devices in
Intune) and the Teams en SharePoint screen (owners, guests, activity, sites, the sharing setting). New read permissions:
DeviceManagementManagedDevices.Read.All, Reports.Read.All, SharePointTenantSettings.Read.All.

### Reporting and working as a team (R14)

| Proposal | Why |
| --- | --- |
| **Microsoft service health** (incidents and the message center) on the start screen | "Is it us or Microsoft?" |
| **Data quality**: per department, how many accounts have a job title, department, office, manager and phone | Good data makes dynamic groups and the address book reliable |
| **Monthly or quarterly report** (PDF or Excel): indicators, trends, clean-up, changes | For management and information security |
| **Checks mapped to standards** (e.g. NIS2 and public-sector information security standards), with evidence from the change log and the snapshots | Audit |
| **Guest management with renewal**: the sponsor confirms each year whether a guest is still needed | Otherwise guests stay forever |
| **Onboarding and offboarding as a request**: a form for HR or the department head, the admin approves and the plan follows from the job profile | Fewer e-mails, everything in the change log |
| **Compare tenants**: the same Conditional Access baseline and settings in every tenant, with the deviations | For those managing several organisations |

Done in 0.37 (R14): service health on the start screen, the Rapporten screen (report per month, quarter or chosen
dates from the stored history, as HTML or CSV, with data quality and the checks mapped to NIS2 article 21 and the CIS
Controls v8), Gasten verlengen (sponsor per guest, yearly confirmation, mails and disabling as plans), Aanvragen
(onboarding and offboarding requests, approval in the change log, the plan on Nieuwe gebruiker or Uitstroom) and
Tenants vergelijken (CA policies by name without ids, settings; nothing stored). New read permissions:
ServiceHealth.Read.All and ServiceMessage.Read.All.

### Languages (R15)

| Proposal | Why |
| --- | --- |
| **Dictionaries for the interface**: every text of the app (buttons, titles, messages, help, errors) lives in a dictionary per language, no longer in the code | Adding a language = translating a dictionary, without touching the code; outside contributors can translate too |
| **Languages at 1.0**: **English (EN)**, Dutch (NL), **Brazilian Portuguese (PT-BR)**, **French (FR)** and **German (DE)** | A public app for organisations in and outside Belgium |
| **System language by default**: the app follows the Windows display language (in the web version: the browser language). If that language is not available: English | Nothing to configure for most users |
| **Set it yourself**: a choice in Settings (per user) that overrides the system language, effective immediately | For those who work in a language other than their Windows |
| **Dates, numbers and currency** according to the chosen language and region | 05/11/2026 or 11/05/2026, 1.234,5 or 1,234.5 |
| **Missing translations** fall back to English (and a test reports untranslated texts); a glossary keeps fixed terms consistent (plan, snapshot, tenant, role) | Never an empty button; the same words everywhere |
| **What is not translated**: data from AD and Microsoft 365 (names of groups, departments, policies) and the content of scripts | That is the organisation's data, not app text |
| **Documentation** in English (U10); translations when translators volunteer | Start small |

Started in 0.38 (R15): the dictionaries in five languages (`Localization/Texts`, see [translating](translating.md)),
the system language by default and a choice in Settings, dates and numbers per language, the fallback to English, a
glossary, and tests that report missing translations and refuse new hard-coded text. Since 0.38.2 every screen takes its
text from the dictionaries (the count of hard-coded text in the screens is zero); since 0.38.4 also what the screens
build in code, and since 0.38.5 the texts of the core and the connections (KPIs, plans, checks, reports, messages).
Left for R15: a review of the translations by native speakers. It comes after R20 (U15), so that it covers the new texts
too.

### Security advice you can carry out (R16)

Every check of R11 becomes **advice**: what was found, an explanation behind an ⓘ, the solution as a plan, and the
choice of an exception. Carrying out is **guided** (U16): low risk with undo is a plan right away; high risk first shows
the impact (who or what still uses it), then a plan in steps or for a pilot group first.

| Proposal | Why |
| --- | --- |
| **Explanation (ⓘ)** per advice: what it is, why it matters (the attack, with its MITRE ATT&CK technique), what the plan does, what can go wrong (users, applications), how to undo it, and links to Microsoft Learn and the standards (NIS2, CIS). In the five languages, like every text | Nobody should carry out what they do not understand |
| **Solve as a plan**, guided for high risk (U16): preview, confirmation, change log and undo where possible | The same safety as every other change |
| **Exception**: accept a risk with a reason, who and until when; after that date the advice returns. In the change log and the report | Not every advice fits every organisation; evidence for an audit |
| **Hardening score**: a score of our own over AD and Microsoft 365 together, computed locally from the advice (weighted by severity; an exception counts as handled), with its trend | One number that also sees AD, next to Microsoft Secure Score |
| **Results in the tenant database** (until now they are kept only while the app is open) | A trend per advice, and what was open on a date in the past |
| **Compromised account**: one plan that blocks the account in AD and Entra, sets a new password, revokes the sessions and removes it from privileged groups, and shows the recent MFA methods, inbox rules, forwarding, sign-ins and devices (R17) | Act within minutes, without forgetting a step |
| **Write permissions per part**: the wizard asks for the new write permissions only for the parts a tenant uses | Least privilege, also for the app registration |

Advice for **Active Directory**:

| Advice | Carried out as |
| --- | --- |
| "Password not required", reversible encryption, Kerberos pre-authentication off (AS-REP roasting) | Plan with undo |
| Administrators in **Protected Users** and "sensitive and cannot be delegated" | Guided: first the services and tasks that run as an administrator |
| User accounts with an SPN (kerberoasting): AES only, a gMSA where possible | Plan with undo (AES), advice (gMSA) |
| adminCount orphans; disabled members of privileged groups; members of Account, Server, Print and Backup Operators; Schema and Enterprise Admins not empty | Plan with undo |
| `ms-DS-MachineAccountQuota` to 0 (users can no longer join computers to the domain) | Plan with undo |
| AD Recycle Bin | Plan; cannot be undone, and the preview says so |
| krbtgt password older than 180 days | Guided: two rounds, with a replication check and the waiting time in between |
| Unconstrained delegation | Guided: first who uses it, then a plan per account |
| Computers without a LAPS password | Advice (Group Policy or Intune) |
| LDAP signing, NTLMv1, SMBv1 and the print spooler on domain controllers | A check with a library script; advice |

Advice for **Microsoft 365**:

| Advice | Carried out as |
| --- | --- |
| No Security Defaults and no Conditional Access | The baseline from the existing CA templates in one plan (`CaSafety` as always) |
| Admin roles on accounts synchronised from AD, or on the daily account with a mailbox | Advice per account |
| Permanent admin roles | Plan to make them eligible (PIM, needs Entra ID P2) |
| Users who register apps, create tenants or consent to apps themselves | Plan, with the admin consent workflow |
| Guests: limited access to the directory; only administrators and Guest Inviters invite | Plan with undo |
| SMS and voice still allowed; passkeys or Microsoft Authenticator not on | Guided: first who has no other method yet |
| Emergency accounts: an alert at every sign-in | Check (with the sign-ins of R18) |
| Exchange: SMTP AUTH, automatic forwarding to outside, the "external" tag in Outlook, the audit log | Plan with fixed commands in `Exchange.ps1`; SMTP AUTH guided (first who still uses it) |
| Defender for Office: the Standard or Strict preset | Plan, where licensed |
| SPF, DKIM and DMARC per domain | Check through DNS; turning DKIM on is a plan, the DNS records come with an explanation |
| Shared mailboxes where signing in is allowed | Plan with undo |
| SharePoint: links for everyone without sign-in | Plan with undo |

Started in 0.39.0: the explanation (ⓘ), accepted risks, the kept results, the hardening score, the compromised-account
plan, and the first advice solved as a plan ("password not required" and reversible encryption off, with undo). The rest
in 0.40.0: every advice of the two tables as a plan (guided for unconstrained delegation, Protected Users, SMS and voice
and SMTP AUTH; krbtgt and the checks on the domain controllers as library scripts with a preview), new checks for the
privileged AD groups, the settings of Microsoft 365 and Exchange Online, SPF, DKIM and DMARC, shared mailboxes with
sign-in and the sign-ins of emergency accounts, and the write permissions per part (U25). With that, R16 is complete;
what remains is the test in a test tenant (issue 19).

Carrying out advice needs the Administrator role; the Auditor sees everything, read only. Advice that needs a licence
the tenant does not have (Entra ID P2, Defender for Office) says so instead of "problem".

### Devices and Intune (R17)

| Proposal | Why |
| --- | --- |
| **One device list over AD, Entra and Intune** (like AD against Entra for users): which device is where, where the hybrid join fails, duplicate and orphaned records | One device, one line, three systems |
| **A page per device**: model, serial number, encryption, last sync, primary user, company or personal, the compliance settings that fail, BitLocker and LAPS (access log); on the user page a **Devices** block | The helpdesk sees everything in one place |
| **Actions as a plan** (U17): safe (sync, restart, rotate the LAPS password and the BitLocker key, Defender scan; also for the Helpdesk), management (rename, change the primary user, retire) and destructive (wipe, fresh start) | Initial management without the Intune portal |
| **Safeguards for destructive actions**: Administrator only, one device per plan, confirmed by typing the device name, never for a personal device (retire instead), never scheduled; the engine checks it too, like `CaSafety` | A wipe cannot be undone |
| **Compliance**: per policy which devices fail and on which setting; the setting "devices without a policy are not compliant"; baseline compliance policies as templates (like the CA templates), as a plan | "Compliant device" in Conditional Access only makes sense with this |
| **Offboarding**: the devices of the person who leaves in the same plan: retire for a personal device, return or wipe for a company device | Company data does not leave with the person |
| **Clean-up** (U18): a stale device is first disabled in Entra (undo possible); only after 90 days disabled, a separate plan deletes the Entra and Intune record, with a warning when BitLocker keys are still attached (they go with the record) | Clean lists, without losing a recovery key by accident |
| **Life cycle of systems** as data that comes with the updates (build and end of support), instead of a fixed list in the code. Today Windows 10 is not marked, although its support ended on 14 October 2025 (except with ESU) | Correct without a code change for every date |
| **Devices in the snapshots**: devices and their compliance get versions, like users | The trend of compliance and of the move to Windows 11; the devices on a date |

Offboarding still never deletes: deleting a device record is only ever the separate clean-up plan.

Started in 0.39.0: the life cycle as data, one device list over AD, Entra and Intune (with what does not match), a
panel per device with its details and the actions on three levels with their safeguards (in the planner and in the
engine), and the Intune devices of a person on the user page. The rest in 0.40.0: changing the primary user, compliance
per policy (on the panel of a device too, with the settings it fails; the baselines and the setting for devices without
a policy as plans of a part, U26), the devices in offboarding, the clean-up (U18, U25: Entra only with its part, never a
hybrid joined or Autopilot device) and the devices in the snapshots. With that, R17 is complete; what remains is the
test in a test tenant (issue 19).

### Own data: logs and reading without a window (R18)

| Proposal | Why |
| --- | --- |
| **Sign-in and audit logs kept locally** (U19), an option that is **on by default**: failed, risky, administrator and emergency account sign-ins and the directory audit log, for 1 year (adjustable with the other retention periods) | Microsoft keeps them 7 to 30 days, depending on the licence; an incident is often found later |
| **Reading without a window** (U19): a task in the Windows Task Scheduler starts Moss365 without a window at an **hour you choose**, per tenant. It only reads: it never writes, and scheduled actions keep waiting for an administrator. It runs under your own Windows account and needs "stay signed in" and a tenant without encryption or with the Windows account or AD group (not with a password); AD only when a domain controller is reachable at that hour | A snapshot every day, without gaps and without a server |
| **Since your last visit**: at opening, what changed outside Moss365 since you last opened the tenant (new administrators, changed policies, new app consents, emergency account sign-ins) | The alerts of the Server, on the Desktop |

Done in 0.39.0: all three. The logs are kept after every read of Entra (failed, risky, administrator and emergency
account sign-ins and the whole directory audit log); reading without a window reads AD and Entra (not Exchange) and
skips while the app is open; since your last visit compares the snapshots and the kept logs, without what Moss365 did
itself.

### The overview (R19)

| Proposal | Why |
| --- | --- |
| **Overview** as the start screen of a tenant, in four blocks: **security** (hardening score, open advice by severity, MFA coverage, administrators, since your last visit), **devices** (compliance, not seen, outdated systems, encryption), **hygiene and costs** (inactive accounts, guests without renewal, wasted licences in euros, data quality) and **work** (what expires soon: app secrets, guests, passwords; requests, scheduled actions, follow-up, the status of Microsoft 365). Every tile shows its trend and clicks through to the list behind it | One look: how are we doing? |
| **Compared with a previous period** ("+3 since last month"), and the overview on a date in the past | Progress, also for management |
| **A layout per role** (Helpdesk, Administrator, Auditor), adjustable per person | Everyone sees first what they need |
| **All tenants** (U20): per tenant only scores and counts, side by side, computed when shown from each tenant's own database (unlocked tenants only); never stored or exported together | For those who manage several organisations |
| **Board summary**: one page with the reports (HTML, prints as PDF) | For management and information security |

The inventory part of today's start screen (folders and files) moves to the settings.

Done in 0.39.0: the overview in four blocks with trends (7, 30 or 90 days) and links, the layout per role (adjustable
per person), the overview on a date in the past (the tiles that come from the data), All tenants (U20) and the board
summary; the inventory moved to the settings.

### Access reviews (R20)

| Proposal | Why |
| --- | --- |
| **Light access reviews**, like Gasten verlengen (R14): every quarter (adjustable) the owner, or a reviewer you choose, confirms the members of privileged groups (AD and Entra), the access to shared mailboxes and the app permissions. Asking is a plan of mails, removing is a plan, every decision is a change log line, and overdue reviews show on the overview | Evidence for NIS2 and the CIS Controls, without an Entra ID Governance licence |

Done in 0.39.0: privileged AD groups, admin roles and shared mailboxes, with the reviewer and the interval per subject,
the decisions in the change log and the removals as plans (admin roles in Entra itself: Moss365 has no permission to
change roles). In 0.40.0: Entra groups that hold an admin role and the app permissions (removed in Entra itself), and the
reviews in the period report and with the standards.

### Edit every object (R21)

Added on 27 November 2026 (U27). Today a few fields per kind can be changed: for a user the names, job title,
department, office, city, phones, manager and logon script; for a room its place and booking; for a shared mailbox its
name, visibility, forwarding, automatic reply and permissions; for a group its name, description, owners and members.
The rest of an object is only visible, or not even that.

| Proposal | Why |
| --- | --- |
| **Every field of every kind**: users (also company, employee id and type, address, country and usage location, other phones, UPN and logon name, primary address and aliases, extension attributes, account expiry), guests (name, company, sponsors, invite again, member or guest), rooms and equipment (name, address, capacity, place, features such as screen and camera, accessibility, booking window, working hours, delegates, who may book), shared mailboxes (addresses, sent items copy, Send on behalf), groups of every kind (addresses, who may mail them, hidden, approval for members, private or public for a Microsoft 365 group), mail contacts, and computers in AD (description, location, managed by, OU) | One screen per object instead of the admin centers of Microsoft and the AD tools |
| **One way of editing for every kind**: the page of an object in sections (identity, contact, organisation, mail, access, membership), each section edited in place with the current value next to the new one, checked per field while typing (format, uniqueness of an address or logon name, the choice lists of Organisatie), and saved as one plan with the difference shown first | Intuitive and the same everywhere: whoever can edit a user, can edit a room |
| **Where a field lives**: a synchronised account is edited in AD (Entra follows after the next synchronisation, and the screen says so), a cloud account in Entra, mail settings in Exchange; fields a tenant does not have (no AD, no Exchange) are not shown | Nothing written where it is overwritten again |
| **Fields with consequences** get their explanation (ⓘ) and, where needed, a confirmation: a new UPN or primary address (sign-in; the old address stays as an alias), a logon name, a group made public | Nobody changes what they do not understand |
| **Several objects at once**: the same field for a selection (department, office, manager, who may book) as one plan | Daily work after a reorganisation |
| **History per field**: from the snapshots, who changed what and when, also outside Moss365 | Answers "since when is this so?" |
| **Design**: the same components for every field type (text, choice list, person, date, number, yes/no, list of addresses), clear labels from the dictionaries, keyboard and screen reader, a narrow window works too | Intuitive screens, in five languages |

Rules: every change is a plan with undo through `Previous`; roles per section (the Helpdesk the contact fields, the
Administrator everything, privileged accounts only the Administrator); on a date in the past only reading.

### The mailbox as PST at offboarding (R22)

Added on 27 November 2026 (U27). Offboarding converts the mailbox to a shared one and never deletes; often the
organisation also wants a copy outside Microsoft 365 for its archive.

| Proposal | Why |
| --- | --- |
| **Export the mailbox as PST** as an optional step of offboarding, and on the page of a mailbox: through the export of Microsoft Purview eDiscovery (Graph: a case of Moss365, a search on the mailbox, an export in PST format), which runs in Microsoft 365 and can take hours | A copy for the archive before the licence goes |
| **Downloading is a follow-up item**, not something that runs by itself: once the export is ready, the follow-up list shows it, and the administrator downloads it to a folder of their choice. The change log keeps who, when, where and the SHA-256 of the file; the access log keeps the download | Personal data of a leaver: traceable, under own control |
| **The permission as a part** (U25): eDiscovery.ReadWrite.All only for a tenant that turns the part on; the signed-in administrator needs the role eDiscovery Manager in Purview, and the screen says so when the role or the licence is missing | Least privilege |

Decided interactively on 28 November 2026 (U37 to U39):

- **The route (U37)**: the mailbox export API of Graph (beta, `exportItems`), with the PST written by Moss365 itself,
  rather than the export of Purview eDiscovery. This needs no Purview licence and no eDiscovery role, but it is more
  work: Moss365 writes the PST format (MS-PST) from the exported items, and the API is still beta. The permission is a
  part (U25), so only a tenant that turns it on asks for it.
- **Where the PST goes (U38)**: a folder of choice, asked at every export (for example an archive share). The change log
  keeps who, when, where and the SHA-256 of the file; the access log keeps the export.
- **Nothing left behind (U39)**: once the PST is written and its SHA-256 checked, Moss365 removes every temporary part
  of the export; nothing of the mailbox stays in the tenant database or next to it.

## 11. Open source, with room for paid support

Moss365 is **open source**: anyone can read, use, change and improve the code. Later it must be possible to ask for
**support and money**. Both go together:

| Always free | Can be paid for later |
| --- | --- |
| The app (Desktop and Server), the source code, the script library, the documentation, the updates | **Support** with agreements (response times, help with problems) |
| | **Installation and setup** (wizard, job profiles, Conditional Access) |
| | **Training** for administrators and helpdesk |
| | **Installing and running the Server** for an organisation, always on its own infrastructure (its own server or Azure subscription): the makers never keep an organisation's data (U21) |
| | **Priority** for requested features, or custom work |

The licence must allow and protect that:

- **EUPL-1.2** (the open source licence of the European Union, made for the public sector, available in all EU
  languages):
  - whoever modifies and distributes Moss365, or offers it as a service, must publish their changes too. Nobody can turn
    it into a closed product;
  - paid support, installation, training and hosting are explicitly allowed;
  - compatible with other open source licences (GPL, AGPL, MPL, ...).
- **A contributor licence agreement** (CLA) for outside contributions: the contributor allows their contribution to be
  distributed under another licence as well. That keeps it possible to offer a commercial licence or paid extras next to
  the open version later (dual licensing), without asking everyone again.

Further:

- **Name and trademark**: "Moss365" is the project name. The README and the app state that Moss365 is **not affiliated
  with Microsoft**; "Microsoft 365", "Entra" and "Exchange" are trademarks of Microsoft. The name could be registered as a
  trademark later (Benelux), so nobody else sells a Moss365.
- **Data**: no telemetry, not even in a paid version without explicit consent, and no hosting of an organisation's data
  by the makers (U21).

## 12. Decisions

U1 to U12 were decided on 5 November 2026 (U1 to U3 directly, the rest interactively the same day); U13 on 23 November 2026;
U14 to U26 interactively on 26 November 2026; U27 to U36 on 27 November 2026 (U28 to U36 interactively); U37 to U40 interactively on 28 November 2026.

| # | Decision | Outcome |
| --- | --- | --- |
| U1 | **Name** | **Moss365**, everywhere (this repository: `jef-cesar/Moss365`) |
| U2 | **Open source** | **Open source, with the option to ask for support and money later** (chapter 11) |
| U3 | **Code history** | **A fresh start without history**: the internal repository mentions the organisation where the app started. The code is moved over after a review |
| U12 | **Licence** | **EUPL-1.2 with a contributor licence agreement (CLA)** (chapter 11) |
| U4 | **Updates** | **Velopack**: per-user install without admin rights, delta updates, rollback to the previous version |
| U5 | **Signing in to a tenant** | **An app registration `Moss365` of its own in each tenant**: each organisation grants and revokes consent itself; no shared app run by the makers |
| U6 | **First sign-in in the wizard** | **The public client of Microsoft Graph PowerShell** (as used by `Connect-MgGraph`), with the link in the app; fallback: `Register-App.ps1` |
| U7 | **Encryption** | **No encryption by default**; per tenant: Windows account (A), password with recovery key (B) or AD group (C). Microsoft 365 via Key Vault (D) after the study. Changed by U24: a new tenant is encrypted with the Windows account by default |
| U8 | **Signing** | **Later**: until 1.0 the app and users verify the published fingerprints (SHA-256); from 1.0 a code signing certificate (Authenticode) for the app, the updates and the scripts |
| U9 | **Retention** (defaults, adjustable per tenant) | **Longer**: snapshots daily for 1 year, then monthly; change log 5 years; technical log 90 days; script runs 1 year |
| U10 | **Language of code and documentation** | **English**: names in the code, comments, tests and documentation (from R1). The interface in five languages via dictionaries (R15) |
| U11 | **House style** | **Moss green and purple** by default (fits the name), colour and logo per tenant |
| U13 | **Google Workspace** | **In the same app**, as a system a tenant can have, next to or instead of AD and Microsoft 365; not a separate edition (decided 23 November 2026, chapter 15; the working name MossSpace was dropped with U29) |
| U14 | **Desktop after 1.0** | **The Desktop stays a full edition**, also next to the Server. The motto is **data under your own control** ("data in eigen beheer"): no feature needs a server or a service of the makers (chapter 1) |
| U15 | **More before 1.0** | **R16 to R20 come before 1.0**, starting with R16: security advice (with the compromised-account plan), devices and Intune, own data, the overview (security, devices, hygiene and costs, work) and light access reviews. The native-speaker review of R15 moves to the end, so that it covers the new texts too |
| U16 | **Carrying out security advice** | **Guided**: low risk with undo is a plan right away; high risk (krbtgt, unconstrained delegation, SMTP AUTH, Protected Users, ...) first shows the impact, then a plan in steps or a pilot group first. Always with an explanation (ⓘ) and the choice of an exception with a reason and an end date |
| U17 | **Actions on Intune devices** | **All levels**: safe (sync, restart, rotate keys, Defender scan), management (rename, primary user, retire), destructive (wipe, fresh start) and clean-up; each as a plan, the destructive ones with the safeguards of R17 |
| U18 | **Cleaning up devices** | **First disable, delete later**: disabled in Entra first (undo possible); only after 90 days disabled is deleting the Entra and Intune record a separate plan, with a warning about BitLocker keys |
| U19 | **Own data** | **Sign-in and audit logs kept locally**, an option that is on by default (1 year, adjustable); **reading without a window** through the Windows Task Scheduler at an hour you choose, read only |
| U20 | **All tenants on one screen** | **Yes, only scores and counts per tenant**, side by side; computed when shown, never stored or exported together. Personal and object data of two tenants never come together |
| U21 | **Hosted edition** | **Only at the organisation itself**: paid installation and management are allowed, always on the organisation's own server or Azure subscription; the makers never keep an organisation's data (chapter 11) |
| U22 | **.NET 10 before R16** | **First move from .NET 8 to .NET 10** (supported until November 2028): .NET 8 gets no security updates since 10 November 2026, and the app ships the runtime itself. Then R16; the tests in a test tenant (issue 19) run alongside |
| U23 | **Approval before a release** | Every release waits for the maintainer's approval in GitHub before it is published and offered as an update. **Reverted on 27 November 2026**: no approval for now; a new version on `main` is published right away. Not chosen: a separate job with write permissions, pinned actions, `main` only through pull requests, signing before 1.0 |
| U24 | **Encryption by default** | **A new tenant is encrypted with the Windows account (A) by default**; changes U7. Backups (issue 22) and the roles mode (Transition) stay as they are for now |
| U25 | **Clean-up of devices in Entra** (26 November 2026) | Disabling and deleting old devices in Entra needs Directory.AccessAsUser.All (the app may then do everything in the directory the administrator may): **only for a tenant that turns the part "clean up old devices in Entra" on**; without it, Moss365 cleans up Intune and shows the list for Entra |
| U26 | **Compliance policies** (26 November 2026) | **Reading** compliance per policy (DeviceManagementConfiguration.Read.All) for every tenant; **making** baseline compliance policies from templates (ReadWrite.All) only for a tenant that turns that part on. Together with U25 this is the start of "write permissions per part" (R16) |
| U27 | **Edit every object, and the mailbox as PST** (27 November 2026) | **On the roadmap as R21 and R22, for the next run**, before the review of the translations and 1.0: every field of every kind of object editable in the same intuitive way (sections, edited in place, checked while typing, one plan with undo), and at offboarding the option to export the mailbox as PST. The route for the PST is decided at the start of R22 |
| U28 | **Google Workspace before 1.0** (27 November 2026) | **Completely before 1.0** (changes "after 1.0" of chapter 15), starting with **G0, the platform basis**, then R21 and R22, then G1 to G5 (chapter 15) |
| U29 | **The name** | **Moss365 stays the name** of the one app; Google is called "Google Workspace" in it; the working name MossSpace is dropped. The data folder and the update package do not change |
| U30 | **Which platform a screen is about** | **A label with a name and a fixed colour** at every section, action and plan step (no logos: trademarks); the menu stays per task and only shows what the tenant has |
| U31 | **Where the mail lives** | **Per user, with a default per tenant**, and an MX check per domain that shows where the mail really arrives |
| U32 | **Linking persons** | **Automatically by fixed rules, with links by hand that win** (kept per tenant) and a list of doubtful cases |
| U33 | **The source of truth** | **Chosen per tenant**: Moss365 proposes the chain from what it sees and asks what it cannot see; a change goes to the source, the other platforms show "follows" |
| U34 | **Gmail settings of someone else** | **A part per tenant** with a service account with domain-wide delegation; the key encrypted in the tenant database, never in a log |
| U35 | **The Google project** | **Its own project per organisation**: a script (gcloud) makes the project, the APIs and the OAuth client; the consent screen and the domain-wide delegation by hand in the admin console, explained and checked afterwards; never a client of the makers |
| U36 | **What comes with Google** | **All four extras**: the person overview with differences, onboarding and offboarding over all platforms in one plan, Google in the hardening score, advice and costs, and migration help between Google and Microsoft 365 |
| U37 | **The route of the PST (R22)** (28 November 2026) | **The mailbox export API of Graph, with the PST written by Moss365** (not Purview eDiscovery, not only a guide): no Purview licence or eDiscovery role; the permission as a part |
| U38 | **Where the PST goes** | **A folder of choice**, asked at every export; the change log keeps who, when, where and the SHA-256 |
| U39 | **What stays after the export** | **Nothing**: once the PST is written and checked, every temporary part is removed; nothing of the mailbox stays in the tenant database |
| U40 | **Google scopes that can also write** (28 November 2026) | **A part per tenant, off by default** (as U25): licences, group settings and the alerts have only a scope that can also write; it is asked only when its part is on, although Moss365 only reads with it in G1 |

## 13. The steps to 1.0

| Step | Contents | Done when |
| --- | --- | --- |
| **R0 Repository** | Licence EUPL-1.2 and CLA (U12), `SECURITY.md`, contributing guide, issue templates, "not affiliated with Microsoft" notice, workflow (build, test, release with fingerprints), secret scanning and push protection, CodeQL, Dependabot. A check in the workflow that refuses personal data and real domains | An empty app builds, tests and appears as a release |
| **R1 Move the code, Moss365 and English everywhere** | Move the 0.9 code without history (U3), after a review: organisation-specific settings, conventions and examples out, a made-up sample organisation in. Rename everything (chapter 2). Code, tests and documentation in English (U10). Migration of an existing installation | All tests green; the old project name only appears in the migration code; an existing installation imports correctly |
| **R2 One database per tenant, everything in it** | `moss365.db` and `tenant.db`; inventories, technical log, backups, script runs and settings in the database; backup and restore of a tenant | No files next to the database any more (except the library and the copies) |
| **R3 Multiple tenants** | Tenant picker at the top, colour and name, confirmation with the name, tokens per tenant, sign-in per tenant (U5) | Two tenants side by side; a test proves no list, plan, export or log line mixes them |
| **R4 Snapshots and timeline** | Versioned objects and relations; every read a snapshot; the organisation on a date; differences between dates; timeline per object; KPIs for every snapshot; Statistics with trends per day, week, month and year; restore from the past | Every KPI shows its trend; a screen shows "as of" a date; a test rebuilds the state of a date exactly |
| **R5 Environment** | Wizard per tenant (find or create the `Moss365` app registration, fill in automatically, groups, AD, hybrid, Exchange, suggested settings, emergency accounts); This computer with installing | A new test tenant is ready in under 15 minutes without typing anything the app could find |
| **R6 Encryption and maintenance** | Encryption A, B and C per tenant, off by default (U7), recovery key, study D; Database screen: size, compact, clean-up with preview and archive, integrity check | An encrypted tenant only opens with the right key; clean-up never deletes without a preview |
| **R7 Updates** | Velopack (U4): check at startup, fingerprint (signed from 1.0, U8), copy before migration, rollback, channels, custom source | An update from preview to stable and back works on a test PC |
| **R8 Scripts from the repository** | `scripts/` with index and fingerprints, Update library in the app, pin per tenant | A new script in the repository appears in the app after Update; a changed script with a wrong fingerprint (from 1.0: without a valid signature) is refused |
| **R9 Universal organisation** | Settings with suggestions from the data, templates, export and import; job profiles; new users in the cloud; logon scripts | A sample organisation with different naming rules, OUs and group names works without a single code change |
| **R10 Helpdesk** | Everything from chapter 10, Helpdesk and daily work | Each proposal with tests, in the change log or access log, with the right role |
| **R11 Security** | Everything from chapter 10, Security (alerts build on R4) | Same; every check says "OK", "attention" or "problem", and "unknown" when the data is insufficient |
| **R12 Licences and costs** | Everything from chapter 10, Licences and costs | Same; the savings are a KPI with its trend |
| **R13 Devices, Teams and SharePoint** | Everything from chapter 10, Devices, Teams and SharePoint (the needed read permissions are added in the wizard) | Same |
| **R14 Reporting and working as a team** | Everything from chapter 10, Reporting and working as a team | Same; a quarterly report comes from the snapshots, also for a period in the past |
| **R15 Languages** | Dictionaries for the interface (one per language, with a glossary); EN, NL, PT-BR, FR and DE; system language by default, configurable in Settings; dates and numbers per language; fallback to English | Every screen in five languages; the app follows the Windows language and the choice in Settings wins; a test finds no hard-coded text in the screens and reports missing translations |
| **.NET 10** | From .NET 8 (no support since 10 November 2026) to .NET 10 (U22): target frameworks, the SDK in `global.json`, the packages; check the SQLCipher build. Done in 0.39.0, which did not start: the screens of BlazorWebView 10 need a Windows version in the target framework (repaired in 0.40.0, and the build now starts the real exe) | All tests green on .NET 10; the single-file build starts on Windows 10 and 11; an encrypted tenant from 0.38 still opens |
| **R16 Security advice** | Everything from chapter 10, Security advice you can carry out: the explanation (ⓘ), solving as a plan (guided for high risk, U16), exceptions, the hardening score with its trend, the results in the tenant database, the compromised-account plan, write permissions per part in the wizard | Every check gives advice with an explanation; every solution is a plan with preview, change log and undo where possible; a test proves that the wrong role and a historic view cannot carry out anything |
| **R17 Devices and Intune** | Everything from chapter 10, Devices and Intune: one list over AD, Entra and Intune, a page per device, actions on three levels with their safeguards (U17), compliance, offboarding, clean-up (U18), the life cycle as data, devices in the snapshots | Every action is a plan with the right role; the planner and the engine refuse a destructive action for more than one device or for a personal device |
| **R18 Own data** | Everything from chapter 10, Own data: sign-in and audit logs kept locally (on by default), reading without a window at an hour you choose, since your last visit | A read at night makes a snapshot without the app being open and never writes; the logs follow the retention |
| **R19 The overview** | Everything from chapter 10, The overview: the start screen per tenant in four blocks, comparison with a period, a layout per role, All tenants (U20), the board summary | Every tile shows its trend and clicks through to the list behind it; a test proves that All tenants stores nothing and shows no names |
| **R20 Access reviews** | Everything from chapter 10, Access reviews | Every decision is in the change log; asking and removing are plans |
| **G0 Platform basis** | Chapter 15 (U28): Google Workspace as a third system per tenant, the platform labels, the menu and screens per combination of systems, where the mail lives, linking persons, the source of truth | Each of the seven combinations of systems only shows its platforms; every section, action and plan step has its label |
| **R21 Edit every object** | Everything from chapter 10, Edit every object (U27): every field of users, guests, rooms, shared mailboxes, groups, contacts and computers; one way of editing in sections, checked while typing; several objects at once; history per field. **First part for 0.41.0**: users in sections with more fields, guests, computers in AD, the settings of groups and shared mailboxes, several users at once, the history of one field. Then (28 November 2026): the UPN with a confirmation, other phones and the last day an account works, the primary address of a shared mailbox, the address and equipment of a room. The logon name in AD is not changed (it is the key of a person in the app; a limitation). Then the primary address of a person (AD: mail and proxyAddresses; cloud: the mailbox), mail contacts (a page, new, edit), the working hours of a room and the extension attributes. Every item of R21 has its first version; the logon name in AD stays a limitation (issue 57) | Every field a tenant has can be changed from the page of its object, as a plan with undo; a test proves that a synchronised field is written to AD, never to Entra, and that the wrong role or a date in the past cannot change anything |
| **R22 Mailbox as PST** | Everything from chapter 10, The mailbox as PST at offboarding (U27): the export as an optional step of offboarding and on the mailbox, the download as a follow-up item, the permission as a part | A mailbox of a test tenant comes out as a PST that opens in Outlook; the change log keeps the SHA-256; nothing runs by itself |
| **G1 Read Google** | Chapter 15: the Google project, signing in, reading, snapshots, Statistics. **First part for 0.41.0** (28 November 2026): the project script, signing in (installed app, PKCE, loopback, only the domain), reading users, groups and organisational units, snapshots and the linking of persons. Still to do: calendar resources, licences, admin roles, the logs and alerts, the group settings, Statistics and the edition | A test Workspace domain is read, shown and linked to its AD and Entra accounts |
| **G2 Write in Google** | Chapter 15: plans for users, groups, resources and licences; the part "Gmail settings" | Every change is a plan with the right role and undo where possible |
| **G3 Onboarding and offboarding over all** | Chapter 15: one plan over AD, Microsoft 365 and Google, with Drive, calendars and Vault | A leaver on all three platforms is handled in one plan |
| **G4 Security, advice and costs** | Chapter 15: Google in the hardening score, advice as plans, access reviews, devices, licences and costs | Every check has its result and explanation; a missing licence is said as such |
| **G5 Hybrid and migration** | Chapter 15: the person overview with differences, writing to the source, migration help | Every person shown once, every change written once |
| **1.0 Desktop** | Code signing certificate for app, updates and scripts (U8); guide per role, installation guide, migration guide, privacy information (what the app stores and where, including the sign-in and audit logs of R18), a DPIA template | An organisation that does not know Moss365 installs and uses it with the documentation alone |

Order: R0 and R1 first. R2 and R3 belong together (one database per tenant). R4 builds on them, and R11 and R14 build
on R4. The other steps can be done in a different order if that works better; every step is a release of its own
(0.10, 0.11, ...). First the move to .NET 10 (U22); then R16 to R20 (U15), in that order; R19 builds on R16 to R18; the
tests in a test tenant (issue 19) run alongside R16. 0.39.0 brings the first version of R16 to R20; decided on
26 November 2026: next the rest of R16, R17, R19 and R20 (done for 0.40.0), and only then the tests in a test tenant.
Decided on 27 November 2026 (U27, U28): first G0, then R21 and R22, then G1 to G5 (Google Workspace, chapter 15), all
before 1.0. The review of the translations by native speakers (the rest of R15) comes after G5, so that it covers every
new text. Then the web version: [roadmap-web.md](roadmap-web.md).

## 14. Risks

| Risk | Approach |
| --- | --- |
| Organisation-specific data ends up in the public repository | Fresh start without history (U3), a check in the workflow, secret scanning with push protection, made-up examples |
| A hijacked update or script | Fingerprints verified and everything signed from 1.0 (U8), only from the official source, scripts never run by themselves; a release only for a new version on `main`, raised with the maintainer's go-ahead. Until 1.0 Windows SmartScreen warns at installation |
| A runtime without security updates | .NET 10 before R16 (U22); from then on, the next LTS version of .NET before the current one loses its support |
| An unencrypted database (default, U7) on a stolen or shared PC | The installation guide recommends BitLocker; the wizard suggests encryption when the disk is not encrypted |
| Moving everything to English (U10) makes R1 big | Renaming with IDE tools and the tests as a safety net; in small pieces |
| Encrypted database, key lost | Recovery key at setup; the tenant's own data can be read again (only the change log and history are unique) |
| Snapshots become too large or too slow | Store changes only, thin out according to the retention, a daily KPI table, tests with a large made-up organisation |
| Working in the wrong tenant | Colour and name in the bar, confirmation with the name, one database per tenant |
| The wizard creates or changes an app registration with too many permissions | Delegated permissions only, the list is in the code and the documentation, always as a plan with preview |
| Installing components automatically | Official sources only, always a confirmation (or a one-time choice), in the change log |
| Sensitive data in the helpdesk features (recovery keys, LAPS, message traces) | Only with the right role, always in the access log, never in an export or the technical log |
| Outside contributions | Pull requests with tests and review, a CLA, the rules of chapter 1 in the contributing guide |
| The name suggests a Microsoft product | The "not affiliated with Microsoft" notice in the README and the app; no Microsoft logos |
| Too much at once for one maintainer | Every step a small release with tests; the order of R5 to R15 is free, so what helps most goes first |
| More before 1.0 (U15) makes 1.0 later | Every step its own release with tests (0.39, 0.40, ...), usable as soon as it is done; the order of R16 to R20 puts what helps most first |
| A security solution breaks something (krbtgt, delegation, SMTP AUTH, Protected Users) | Guided (U16): the impact first, steps or a pilot group, undo where possible; the explanation says what can go wrong |
| A wipe of the wrong device | Administrator only, one device per plan, the device name typed, never for a personal device; the engine checks it too (R17) |
| Deleting a device record also deletes its BitLocker keys | Disabled first; deleting only after 90 days, with a warning in the preview (U18) |
| Sign-in logs on the PC are more personal data | Only what matters, a retention (1 year by default), encryption recommended, in the privacy information and the DPIA template |
| Reading without a window with a remembered sign-in | Read only, under your own Windows account; "stay signed in" stays a choice; the task is only created after your confirmation |
| All tenants shows several organisations together | Only scores and counts, computed when shown, never stored or exported together (U20) |
| A leaked key of the Google service account reads every mailbox | Only with the part "Gmail settings" (U34), only the scopes it needs, the key encrypted in the tenant database, never in a log, replaced from the wizard |
| Writing in Google what a synchronisation overwrites again | The source per tenant (U33): a change goes to the source, the platforms that follow are read only; a test for every hybrid environment |
| Google Workspace before 1.0 (U28) makes 1.0 later again | The platform basis first (G0) so that nothing is built twice; every step its own release with tests |
| The name suggests a product of Google or Microsoft | The "not affiliated" notice also names Google; labels instead of logos (U30) |

## 15. Google Workspace next to AD and Microsoft 365

Many organisations, schools above all, use **Google Workspace** instead of, or next to, Microsoft 365, often with AD on
premises. Moss365 manages Google Workspace in the same app (U13) with the same approach: every change a plan, every
read a snapshot, the roles of the app, one database per tenant, encryption, the wizard. Decided on 27 November 2026,
interactively (U28 to U36): Google Workspace comes **before 1.0**, starting with a platform basis that the other steps
build on; the app keeps the name Moss365, and the working name MossSpace is dropped.

What the maintainer asked for: users linked over the platforms; per tenant the choice of AD, Microsoft 365 and Google
Workspace, in any combination; where the mail lives; menus and screens that only show what a tenant has; and screens
that make clear which platform they are about.

### Hybrid environments

| Environment | How identities flow | Where Moss365 writes (U33) |
| --- | --- | --- |
| Google Workspace only | No synchronisation | Google |
| AD and Google Workspace | AD to Google with Google Cloud Directory Sync, often with Password Sync | AD for what the synchronisation carries, Google for the rest |
| Microsoft 365 and Google Workspace | Side by side (two mail systems, or during a migration), or Entra to Google with provisioning and single sign-on | The source chosen per tenant; the mail per user (U31) |
| AD, Microsoft 365 and Google Workspace | AD to Entra (Entra Connect), then Entra to Google (provisioning) or AD to Google (Directory Sync) | AD |
| Google as the sign-in for Microsoft 365 | Google to Entra (federation) | Google for the account, Entra for what only Microsoft 365 has |

### What is possible and what not

| Subject | In Moss365 | Why |
| --- | --- | --- |
| Users, organisational units, groups and their settings, calendar resources, licences, admin roles | **Read and write**, with the administrator's own sign-in | Admin SDK: Directory, Groups Settings, Licensing |
| 2-Step Verification | **Read** the status per user and the enforcement per organisational unit; turning on enforcement is explained | Enforcement is a setting of the admin console without a write API |
| Sign-in and audit logs, alerts | **Read**, and kept locally as in R18 (Google keeps them about six months) | Reports API, Alert Center API |
| Gmail settings of someone else: forwarding, automatic reply, delegation, filters, signature | **Only with the part "Gmail settings"** (U34) | Google only allows it through a service account with domain-wide delegation |
| Shared mailboxes | **As Google has them**: a delegated mailbox (with the part), or a group as collaborative inbox | Gmail has no shared mailbox |
| Drive | Shared drives, sharing outside the organisation (from the audit log), the transfer of files and calendars at offboarding | Drive API with domain administrator access, Data Transfer API |
| Vault | Holds, and the export of a mailbox as **PST or MBOX** at offboarding | Needs a Vault licence (Business Plus, Enterprise, Education) |
| Devices | Mobile devices (approve, block, account wipe; a full wipe only with advanced management), ChromeOS (disable), endpoint verification; in one list with AD, Entra and Intune | Directory API, Cloud Identity Devices API |
| Context-aware access | **Read** | Only in the Enterprise and Education editions; writing it belongs in the console |
| DKIM | **Checked through DNS**; generating the key and turning it on is explained | There is no API for it |
| MX records and mail routing | **Checked through DNS**, per domain | Moss365 does not change DNS or mail routing |
| Synchronisation from AD (Directory Sync) | **Set per tenant** (U33) | Google does not mark which users come from AD |
| Logos | **No logos**: labels with a name and a colour (U30) | The trademarks of Google and Microsoft |
| Deleting a user | **Never in offboarding** (suspend instead), as for AD and Microsoft 365 | The rules of Moss365 |

### Platform labels and context (U30)

- Every platform has a **label with its name and a fixed colour**: AD, Entra ID, Exchange Online, Exchange
  (on-premises), Google Workspace and Gmail. It stands at every section, field, action and plan step, in the change log
  and in the differences between snapshots. The colour is never the only sign: the name is always there.
- The **menu stays per task** (users, groups, mail, devices, security, ...) and only shows what the tenant has
  (`Access.FitsTenant` with the three systems); a page only shows the sections of the platforms of the tenant, and
  Ctrl+K only finds those pages.
- The **page of a person** shows each of their accounts with its label; what follows from the source says "follows from
  AD" (or Entra) and cannot be edited there.
- Lists over several platforms have a column with the label and a filter per platform.

### Linking persons (U32)

- **Automatically**, by fixed rules in this order: AD and Entra through the synchronisation anchor (as today); AD or
  Entra and Google through the primary address or the UPN; then the employee id; then an alias.
- A **link made by hand wins** over the rules and is kept per tenant (document `koppelingen`), and so is a link undone
  by hand.
- **Doubtful cases in a list**: two candidates, a manual link that a rule contradicts, an account without a counterpart.
- The **person overview with differences** (U36): who is where, and what does not match, such as suspended in Google but
  active in AD, another name or department, or a mailbox on the wrong platform.

### Where the mail lives (U31)

- A **default per tenant** (Exchange Online, Exchange on-premises, Gmail or another system), and **per user** a different
  one where needed (co-existence, migrations), proposed from what Moss365 finds: an Exchange mailbox, or Gmail turned on
  for the user.
- An **MX check per domain** (DNS) shows where the mail really arrives, and warns when that differs from the setting.
- The screens for mail (mailboxes, automatic reply, forwarding, message trace, shared mailboxes) work with the platform
  of each user, and a plan step names it.

### The source of truth (U33)

- Per tenant a **chain per kind of object** (users, groups): Moss365 proposes it from what it sees (Entra Connect, the
  enterprise app that provisions to Google) and asks what it cannot see (Directory Sync, Password Sync).
- A change goes **to the source**; the other platforms show "follows from ..."; a field the synchronisation does not carry
  (set per tenant) is written on the platform itself.
- A test proves that nothing is written where a synchronisation overwrites it.

### Signing in and the Google project (U35, U34)

- The administrator signs in with their own Google account (OAuth for an installed app, with PKCE). "Stay signed in"
  keeps the token as for Microsoft: only when chosen, protected with the Windows account.
- **Every organisation has its own Google Cloud project** with its own OAuth client, like the app registration in Entra;
  never a client of the makers. `tools/Register-GoogleApp.ps1` (with gcloud) makes the project, turns on the APIs and
  makes the client; the consent screen (internal) and, for the part, the domain-wide delegation are done by hand in the
  admin console, explained step by step, and the wizard checks them afterwards.
- The part **"Gmail settings"** (U34, like U25): a service account with domain-wide delegation, only for a tenant that
  turns the part on; its key encrypted in the tenant database, never in a log, replaceable from the wizard.

### What maps to what

| Microsoft | Google Workspace | In Moss365 |
| --- | --- | --- |
| Users (Entra ID) | Users (Directory API) | Users, with their fields, status and last sign-in |
| OUs (AD) | Organisational units | OUs, also for new users and offboarding |
| Security and Microsoft 365 groups | Google Groups (with the Groups Settings API) | Groups: members, owners and managers, settings |
| Shared mailboxes | Delegated mailboxes and collaborative inboxes | Shared mailboxes: who has access |
| Rooms (Exchange) | Calendar resources | Rooms: buildings, features, booking |
| Automatic reply, forwarding | Gmail settings (with the part) | Helpdesk (R10) |
| Licences | Licence assignments (Licensing API) | Licences and costs (R12) |
| MFA, admin roles | 2-Step Verification, admin roles | Security (R11, R16) |
| Conditional Access | Context-aware access (read) | Security |
| Sign-in and audit logs | Reports API, Alert Center | Security, the timeline and the kept logs (R18) |
| Devices (Intune) | Endpoint management (mobile, ChromeOS) | Devices (R17) |
| SharePoint, OneDrive | Shared drives, Drive | Sharing and storage (R13) |
| Purview (PST, R22) | Vault (PST or MBOX) | Offboarding |

### The steps

| Step | Contents | Done when |
| --- | --- | --- |
| **G0 Platform basis** (before R21) | Google Workspace as a third system per tenant; the platform labels; the menu and the screens per combination of systems; where the mail lives, with the MX check; linking persons, with manual links and doubtful cases; the source of truth per tenant. **First version for 0.41.0**; the Google side gets its data from G1; the employee id is read for AD and Entra since R21 | Each of the seven combinations of systems only shows its platforms; every section, action and plan step has its label; a test proves it for every combination |
| **G1 Read Google** | The Google project (script and wizard), signing in, reading users, OUs, groups and their settings, resources, licences, admin roles, 2-Step Verification, the logs (kept locally) and the alerts; snapshots and Statistics; the edition, and what it lacks said as such | A test Workspace domain is read, shown on the existing screens and in Statistics, and linked to its AD and Entra accounts |
| **G2 Write in Google** | Plans for users (new, fields through the editor of R21, OU, suspend, password, sign out, revoke tokens, aliases), groups (members, owners, settings), resources and licences; the part "Gmail settings" (forwarding, automatic reply, delegation, filters, signature); undo through `Previous` | Every change is a plan with the right role, in the change log, with undo where possible; the engine refuses a Gmail setting with the part off |
| **G3 Onboarding and offboarding over all** | One plan over AD, Microsoft 365 and Google: a new person in the source with the others following; at offboarding suspend or block, sessions and tokens revoked, devices, automatic reply and forwarding, Drive and calendars to the manager, the mailbox as PST (R22 for Exchange, Vault for Gmail), licences; never deleting | A leaver with accounts on all three platforms is handled in one plan, each step with its label |
| **G4 Security, advice and costs** | Google checks in the hardening score, with advice as plans where Google allows it (2-Step Verification, super administrators, apps with broad access, sharing outside, forwarding outside, SPF, DKIM and DMARC, alerts); access reviews for Google roles and groups; the devices in the one list; the Google licences and costs next to those of Microsoft | Every check says OK, attention, problem or unknown, with its explanation; a missing licence is said as such |
| **G5 Hybrid and migration** | The person overview with differences; writing to the source in every hybrid environment; migration help between Google and Microsoft 365: who is where (mailbox, files, groups), what is still to do, the mail location per user during the move (the migration itself with the tools of Microsoft or Google) | A tenant with AD, Microsoft 365 and Google shows every person once and writes every change once, and the move of a test user is followed from start to end |

Order (U28): G0 first, then R21 and R22 (so that the editor knows from the start where a field lives), then G1 to G5,
then the review of the translations and 1.0.
