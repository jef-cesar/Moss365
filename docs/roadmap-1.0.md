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

## 10. Proposals: what AD and Microsoft 365 administrators want

What administrators often miss in practice. All of them are in the steps to 1.0 (chapter 13); each heading names its
step (R10 to R15). Everything that changes something goes through a plan as always; everything that shows sensitive
data (recovery keys, passwords, message traces) is written to the access log and requires the right role.

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
| **Weak MFA methods** (SMS, phone) and who has no stronger one yet | Becoming phishing resistant |
| **Risky users and sign-ins** (requires Entra ID P2) | React quickly |

### Licences and costs (R12)

| Proposal | Why |
| --- | --- |
| **Licences that bring nothing**: disabled accounts with a licence, shared mailboxes with a licence, duplicate plans, never-used licences | Direct savings |
| **Cost per month** (price per licence configurable), per department and division, with its trend | For the budget |
| **Licences according to the job profile**: who has more or less than their profile | Clean-up and fair distribution |

### Devices, Teams and SharePoint (R13)

| Proposal | Why |
| --- | --- |
| **Computers in AD**: no longer signed in, operating system and version, which OU | Clean-up and security |
| **Devices in Intune**: non-compliant, not seen any more, version, owner | Conditional Access with "compliant device" only makes sense with this |
| **Teams and Microsoft 365 groups without an owner**, with guests, no longer used | The most heard complaint about Teams |
| **SharePoint sites**: external sharing, storage, unused | Data leaks and costs |

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

## 11. Open source, with room for paid support

Moss365 is **open source**: anyone can read, use, change and improve the code. Later it must be possible to ask for
**support and money**. Both go together:

| Always free | Can be paid for later |
| --- | --- |
| The app (Desktop and Server), the source code, the script library, the documentation, the updates | **Support** with agreements (response times, help with problems) |
| | **Installation and setup** (wizard, job profiles, Conditional Access) |
| | **Training** for administrators and helpdesk |
| | **Hosted Server edition** (Moss365 as a service, for those who do not want their own server) |
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
- **Data**: no telemetry, not even in a paid version without explicit consent.

## 12. Decisions

All decisions were made on 5 November 2026 (U1 to U3 directly, the rest interactively the same day).

| # | Decision | Outcome |
| --- | --- | --- |
| U1 | **Name** | **Moss365**, everywhere (this repository: `jef-cesar/Moss365`) |
| U2 | **Open source** | **Open source, with the option to ask for support and money later** (chapter 11) |
| U3 | **Code history** | **A fresh start without history**: the internal repository mentions the organisation where the app started. The code is moved over after a review |
| U12 | **Licence** | **EUPL-1.2 with a contributor licence agreement (CLA)** (chapter 11) |
| U4 | **Updates** | **Velopack**: per-user install without admin rights, delta updates, rollback to the previous version |
| U5 | **Signing in to a tenant** | **An app registration `Moss365` of its own in each tenant**: each organisation grants and revokes consent itself; no shared app run by the makers |
| U6 | **First sign-in in the wizard** | **The public client of Microsoft Graph PowerShell** (as used by `Connect-MgGraph`), with the link in the app; fallback: `Register-App.ps1` |
| U7 | **Encryption** | **No encryption by default**; per tenant: Windows account (A), password with recovery key (B) or AD group (C). Microsoft 365 via Key Vault (D) after the study |
| U8 | **Signing** | **Later**: until 1.0 the app and users verify the published fingerprints (SHA-256); from 1.0 a code signing certificate (Authenticode) for the app, the updates and the scripts |
| U9 | **Retention** (defaults, adjustable per tenant) | **Longer**: snapshots daily for 1 year, then monthly; change log 5 years; technical log 90 days; script runs 1 year |
| U10 | **Language of code and documentation** | **English**: names in the code, comments, tests and documentation (from R1). The interface in five languages via dictionaries (R15) |
| U11 | **House style** | **Moss green and purple** by default (fits the name), colour and logo per tenant |

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
| **R9 Universal organisation** | Settings with suggestions from the data, templates, export and import; job profiles; logon scripts | A sample organisation with different naming rules, OUs and group names works without a single code change |
| **R10 Helpdesk** | Everything from chapter 10, Helpdesk and daily work | Each proposal with tests, in the change log or access log, with the right role |
| **R11 Security** | Everything from chapter 10, Security (alerts build on R4) | Same; every check says "OK", "attention" or "problem", and "unknown" when the data is insufficient |
| **R12 Licences and costs** | Everything from chapter 10, Licences and costs | Same; the savings are a KPI with its trend |
| **R13 Devices, Teams and SharePoint** | Everything from chapter 10, Devices, Teams and SharePoint (the needed read permissions are added in the wizard) | Same |
| **R14 Reporting and working as a team** | Everything from chapter 10, Reporting and working as a team | Same; a quarterly report comes from the snapshots, also for a period in the past |
| **R15 Languages** | Dictionaries for the interface (one per language, with a glossary); EN, NL, PT-BR, FR and DE; system language by default, configurable in Settings; dates and numbers per language; fallback to English | Every screen in five languages; the app follows the Windows language and the choice in Settings wins; a test finds no hard-coded text in the screens and reports missing translations |
| **1.0 Desktop** | Code signing certificate for app, updates and scripts (U8); guide per role, installation guide, migration guide, privacy information (what the app stores and where), a DPIA template | An organisation that does not know Moss365 installs and uses it with the documentation alone |

Order: R0 and R1 first. R2 and R3 belong together (one database per tenant). R4 builds on them, and R11 and R14 build
on R4. The other steps can be done in a different order if that works better; every step is a release of its own
(0.10, 0.11, ...). Then the web version: [roadmap-web.md](roadmap-web.md).

## 14. Risks

| Risk | Approach |
| --- | --- |
| Organisation-specific data ends up in the public repository | Fresh start without history (U3), a check in the workflow, secret scanning with push protection, made-up examples |
| A hijacked update or script | Fingerprints verified and everything signed from 1.0 (U8), only from the official source, scripts never run by themselves. Until 1.0 Windows SmartScreen warns at installation |
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
