# Changelog

Every version becomes a release on GitHub; the section under the version is the text of that release.

## 0.42.0

G1, reading Google Workspace, the rest (decided on 28 November 2026, U40), and G2, writing in Google Workspace
([docs/google.md](docs/google.md)). Not yet tested with a real Workspace domain (issue 19).

- **Writing in Google** (G2): Google is a system of the plans, as AD, Entra and Exchange: every change is a plan with a
  preview, a confirmation, the role of the app (the engine refuses what the role does not allow; a Google administrator
  needs Beheerder), the change log with the label Google Workspace or Gmail, and undo through the values from before.
  Nothing in Google is ever deleted. The sign-in at Google now asks the scopes to change users, groups and calendar
  resources (the base, as the permissions of Microsoft 365); after the update Google asks the consent once more.
- **Google users** on the page of the person: the first and last name, department, employee id, organisational unit and
  suspended; aliases; a new password (only in the plan, never logged); signing out of every session; revoking the
  access of apps. A new page *Nieuwe Google-gebruiker* makes a user by the naming rules of the organisation. When Google
  gets its users from Entra or AD (the source of truth, U33), what that synchronisation carries is shown and changed at
  the source, never in Google (with Password Sync also the password).
- **Google groups** (new page): name and description, the members read when asked, added with a role, removed, or with
  another role; the settings with the part Google group settings, with a warning when a group opens up to outside.
  Groups that follow another platform keep their name and members there.
- **Google calendar resources** (new page): name, capacity, floor and description, edited in their row.
- **Google licences** (part Google licences): assigned and removed on the page of the person, only licences the
  organisation uses.
- **Gmail settings** (new part, U34): forwarding, the automatic reply, delegates, filters (a filter that forwards is
  marked) and the signature of a user, read when asked (in the access log) and changed as plans. Google only allows
  this through a service account with domain-wide delegation: its key (the JSON file) is kept encrypted with the
  Windows account in the tenant database, never shown or logged; *Verbindingen* shows the client id and the scopes for
  the admin console, and *Project controleren* tries it. `tools/Register-GoogleApp.ps1 -Gmail` makes the service
  account and its key.

- **Parts of Google** (U40) on *Verbindingen*: licences, group settings and the alerts have only a Google scope that
  can also write, so each is a part, off by default. Its scope is asked only when it is on; Moss365 only reads with it.
  `tools/Register-GoogleApp.ps1` turns on all four APIs (an API that is on gives no right by itself).
- **Stay signed in** also for Google (the same setting as for Microsoft): the refresh token is kept in the tenant
  database, encrypted with the Windows account, only for the same client, domain and scopes; a refused refresh forgets
  it, and another part on asks a new sign-in.
- **Check the project** on *Verbindingen*: signs in and asks every API one small question, and says what to do: turn an
  API on, sign in again for a scope, or give the account an admin role.
- **The logs of Google kept locally** (as R18): after every read the failed and suspicious logins, those of Google
  administrators and emergency accounts, and every admin activity of the Reports API go to the tenant database, never a
  line twice. The page of the kept logs chooses between Entra ID and Google Workspace, and is there for a tenant with
  only Google too.
- **Licences and the edition** (part Google licences): who has which licence per product; the edition from the
  Workspace SKU most users have, with what it lacks of Vault, context-aware access, advanced endpoint management and the
  security center (data in `Core/Google/GoogleEditions.cs`); the active accounts without a Workspace licence.
- **Group settings** (part Google group settings): who may join, post and see the members, and external members;
  Statistics counts the groups anyone may join, anyone outside may post to, and with external members.
- **Alerts** (part Google alerts): the alerts of the Alert Center kept locally with the logs, and the open ones of the
  last 30 days in Statistics.

## 0.41.0

G0, the platform basis for Google Workspace next to AD and Microsoft 365 (decided on 27 November 2026, U28 to U36);
R21, editing every object; and the first part of G1: Moss365 reads Google Workspace (writing comes with G2).

Not yet tested in a real tenant (issue 19): the new writes in AD (proxyAddresses, accountExpires, extension
attributes), the new fixed Exchange commands (contacts, working hours, primary address) and the sign-in at Google.

- **Google Workspace as a third system** (U28): on *Tenants* and *Verbindingen* three checkboxes choose any combination
  of Active Directory, Microsoft 365 and Google Workspace. The menu, the settings and Ctrl+K show only what the tenant
  has, and the data of a system the tenant does not have is left out (now also Microsoft 365, as AD before).
- **Platform labels** (U30): every plan step, every line of the change log, the tables of the differences between
  snapshots and the sections of the pages (user, group, mailbox, room, guests, devices) show the platform with its name
  on a fixed colour: AD, Entra ID, Exchange Online, Exchange on-premises, Intune, Google Workspace and Gmail. No logos.
- **Where the mail lives** (U31), a new settings page: a default per tenant (proposed from the main domain), per person a
  different system where needed, and an MX check per domain through the DNS of this PC that warns when the mail of a
  domain arrives elsewhere. The page of a person says where the mail is, and offers automatic reply and message trace
  only for mail in Exchange Online.
- **Linking persons** (U32), a new settings page: AD and Entra stay linked by the synchronisation; a Google account is
  linked by its address, then the employee id, then an alias, and a link by hand wins. Doubtful cases are listed, and
  already now also an AD account that is not synchronised while a cloud account has its address (two accounts for one
  person, or a synchronisation that broke).
- **The source of truth** (U33), a new settings page: per kind of object the chain from the source to what follows it
  (AD to Entra ID when Entra Connect synchronises), and where Google gets its accounts (itself, from Entra ID, from AD).
  A change goes to the source; the page of a synchronised person shows "follows from AD".

G1, reading Google Workspace, first part (U35; [docs/google.md](docs/google.md)):

- **Your own Google project**: `tools/Register-GoogleApp.ps1` (gcloud) makes the project and turns on the Admin SDK
  API; the consent screen (Internal) and an OAuth client of the type Desktop app are made by hand in the console, with
  the links the script shows. Never a client of the makers.
- **Signing in** on *Verbindingen* with the primary domain and that client: the OAuth flow for installed apps with
  PKCE and a loopback address of this PC, through the same sign-in bar as Microsoft. Only an administrator of the
  domain of the tenant (or a subdomain) is accepted; the tokens stay in memory; reading without a window never signs in.
- **Reading** with only reading scopes: users (addresses and aliases, organisational unit, suspended or archived,
  administrator, 2-Step Verification, last sign-in, employee id, department), groups, organisational units, calendar
  resources and admin roles, in a lane of its own next to AD, Entra and Exchange. Every read is a snapshot
  (`google-gebruikers.csv`, `google-groepen.csv`, `google-eenheden.csv`, `google-agendabronnen.csv`, `google-rollen.csv`),
  and the Google users are linked to the persons (G0). Calendar resources and admin roles (to a user, for the whole organisation or one organisational unit) are read
  each on their own: a missing right leaves the rest, and what could not be read stays as it was.
- **Shown on the screens**: the page of a person has a section Google Workspace (address and aliases, organisational
  unit, suspended or archived, 2-Step Verification, administrator and admin roles, last sign-in, how it was linked), and
  Statistics a block with the Google accounts, the suspended ones, the administrators and super administrators, those
  without 2-Step Verification, the groups, the calendar resources and the accounts only in Google. A person with only a
  Google account is not edited until G2.

R21, editing every object (decided on 27 November 2026, U27), every item in its first version; the logon name in AD
stays a limitation (issue 57):

- **A user in sections**: identity, organisation, contact and address, access, each with its current values and edited
  in place, with the current value next to the new one and the platform label of where it goes. New fields: company,
  employee id and kind, address, postal code, province, country and usage location. Checked while typing (the usage
  location is two capital letters, a phone number has only digits and + ( ) - . /, an employee id at most 16
  characters; a duplicate employee id is a warning). A synchronised account changes in AD (Entra follows); the usage
  location always in Entra, because Entra Connect does not carry it.
- **A guest**: name, company, member or guest (with a warning about what a member sees), and inviting it again with the
  same account and groups.
- **A computer in AD**, on the panel of the device: description, location, managed by and the OU.
- **Groups**: Public or Private for a Microsoft 365 group; for a mail group of Exchange Online hidden from the address
  book, only senders inside the organisation, and how to become a member.
- **The sign-in name (UPN)**, in the section access: refused while typing when another account has it, a warning for a
  domain no account uses yet, and a confirmation that the person signs in with the new name afterwards. The logon name
  in AD (sAMAccountName) is not changed: it is the key of a person in the app (snapshots, history, documents).
- **Other phones** (home, fax, IP phone; the fax also for a cloud account) and **the last day an account works** in AD
  (accountExpires: the start of the next day, local time; never is 0), with a warning for a day in the past. A sign-in
  name and a display name can no longer be emptied.
- **A shared mailbox**: a copy of what others send from it in its Sent Items, who sends on behalf of it, and a new
  primary address (confirmed; the old address stays as an alias, so mail keeps arriving; a mailbox from AD is changed
  there). A group, a mailbox or a person cannot get an address another recipient has.
- **The primary address of a person**, confirmed on the page: for an account from AD in AD (mail and the whole list of
  proxyAddresses at once, the old primary address as an alias; proxyAddresses is now read from AD), for a cloud account
  on its mailbox in Exchange Online. An address or alias of another person, mailbox, room, group or contact is refused.
- **Mail contacts** (new page Contacts, under Microsoft 365): read with the mailboxes of Exchange Online (with company,
  job title and phone), stored with the inventory and in the snapshots, a new contact (with a unique alias, an address
  nobody has) and changing one (name, address, hidden, first and last name, company, job title, phone) with undo. A
  contact from AD is shown but changed there.
- **A room**: the address (street, postal code, city, country), the equipment (audio, video, screen, wheelchair
  access) and the working hours (days, from and until, time zone, only bookable within them; read with
  Get-MailboxCalendarConfiguration, through the fixed Werkuren), with undo.
- **Extension attributes** 1 to 15 in their own section of a user: read from AD (the Exchange schema) and from Entra
  (onPremisesExtensionAttributes), written at the source (AD for a synchronised account, Entra for a cloud account);
  only the filled in ones are shown until you edit them, and the section only appears when the tenant has them.
- **Several users at once**: the new fields (company, kind of employee, address, country, usage location, logon
  script, the last day the account works) for a selection, as one plan.
- **The history of one field** on the page of a person, from the snapshots and the change log.
- Reading more fields lists no account as changed: a column read for the first time is no change in the snapshots, the
  differences or "since your last visit".
- Every change is a plan with the previous values, so it can be undone.

## 0.40.0

**0.39.0 does not start**: on every PC it stops with an empty window "Moss365 - onverwachte fout". This version repairs
that. Because 0.39.0 cannot open its screens, it cannot update itself: download `Moss365.Desktop-stable-Setup.exe`
below and run it (no administrator rights; your tenants and data stay where they are). If Moss365 still runs in the
background after the error, Setup closes it.

- **The start** (fix): the screens of .NET 10 (BlazorWebView with `WebView2CompositionControl`) need the Windows SDK
  projection, and 0.39.0 did not ship it (the target framework had no Windows version). Moss365 now targets
  `net10.0-windows10.0.17763.0`, Windows 10 1809 or later, like WebView2 itself.
- **The window of an unexpected error** is never empty any more: it shows the message (or that the error has none), the
  kind of error and where it happened; Ctrl+C copies it, for an issue. The technical log keeps the kind and the place.
- **The build starts the app** (`tools/Test-AppStart.ps1`): on every pull request and before every release, the real
  `Moss365.exe` is started on Windows, and the build fails when the main window does not open or an error window appears.
- **Releases no longer wait for the maintainer's approval** (U23 reverted on 27 November 2026): a new version on `main` is
  published right away.

The rest of R16, R17, R19 and R20 (decided on 26 November 2026), in parts.

- **The overview** (R19): every tile also draws a small line of its counts in the chosen period, and the work block
  shows the passwords that expire within 14 days (when a maximum age of passwords is set on *Wachtwoord verloopt*).
- **Access reviews** (R20) also for **Entra groups that hold an admin role** and for the **permissions of apps** found
  by the last check of app permissions. Removing those is done in Entra itself, with the way explained: Moss365 has no
  permission to change roles or apps. The period report has a section on the reviews of the period, and a review that
  is due counts as "attention" with the standards (NIS2 21.2.i and CIS 6).
- The consent of users in the check of app permissions is now in the language of the interface.
- **Permissions per part** (R16, U25, U26): *Tenant inrichten* has parts with a permission of their own, asked only for
  a tenant that turns the part on, and taken away again when it is turned off: *clean up old devices in Entra*
  (Directory.AccessAsUser.All, which Microsoft requires for disabling and deleting devices) and *baseline compliance
  policies* (DeviceManagementConfiguration.ReadWrite.All). `Register-App.ps1` takes the same parts with `-Onderdelen`.
  Every tenant gets the read permission DeviceManagementConfiguration.Read.All, for compliance per policy.
- **Devices** (R17): on the panel of a device, *Nieuwe primaire gebruiker* makes a plan that gives the device to another
  person (with undo). **Offboarding** now takes the devices of the person along: a personal device is retired in the
  same plan, and a company device gets a note to have it returned (it is wiped on *Toestellen* once it is back, never in
  the offboarding plan).
- **Devices in the snapshots** (R17): every read on *Toestellen* keeps the computers in AD and the devices in Entra and
  Intune as a snapshot, like the users (only what changed is stored; of "last seen" only whether it was within 90
  days). On a date in the past, *Toestellen* shows the devices of then, and the overview keeps the device counts per day
  for their trend.
- **Compliance per policy** (R17, U26): *Toestellen* shows per compliance policy which devices fail it, the settings
  that fail most, the platforms without any policy, and whether a device without a policy counts as compliant (with
  where to change that in Intune). The panel of a device shows the policies it fails and, on request, the settings it
  fails. With the part *baseline compliance policies* on, the baselines for Windows, iOS and Android (work profile) are
  plans (made without assignment), and so is marking devices without a policy as not compliant (the devices that would
  then fail are named; undo puts the settings back).
- **Cleaning up old devices** (R17, U18, U25): a device in Entra not seen for 90 days is first disabled (undo possible);
  only after 90 days disabled does a separate plan delete its record in Intune and in Entra, with a warning when
  BitLocker keys would go with it. Hybrid joined devices are cleaned up in AD and Autopilot devices never; Entra is
  asked again just before. Entra only with the part *clean up old devices in Entra* on; records in Intune not seen for
  180 days can always be deleted. The engine refuses the changes of a part that is off, also in a plan made by hand.
- The panel of a device leads to *BitLocker en LAPS* with its name filled in (every lookup still goes to the access
  log).
- **Advice for Active Directory as plans** (R16, U16): the AD check on *Controles* also finds Kerberos
  pre-authentication off, RC4 on service accounts with an SPN, a machine account quota above 0, the AD Recycle Bin off
  and computers without a LAPS password. *Oplossen als plan* makes one plan of the low-risk ones, with undo
  (pre-authentication on, AES only, adminCount leftovers cleared with the inheritance of rights on again, the quota to
  0; turning on the Recycle Bin cannot be undone, and the plan says so). Unconstrained delegation is guided: one account
  per plan, with its services named and what stops working.
- New check **Privileged AD groups**: administrators outside Protected Users or not marked sensitive (guided, one at a
  time, with what changes for the account; an administrator that runs a service first needs a gMSA), disabled members,
  members of Account, Server, Print and Backup Operators, and Schema or Enterprise Admins that are not empty (removing
  them is a plan with undo). The groups are found by their well-known SID, so also on a domain controller in another
  language. A round of checks reads AD once for both AD checks.
- **Advice for Microsoft 365 as plans** (R16): the new check *Instellingen van Microsoft 365* finds MFA for nobody
  (neither Security Defaults nor Conditional Access), apps and tenants made by users, consent to apps without the admin
  consent workflow, guests who see the directory and members who invite them, SMS and voice as second factor, no
  passkeys or Authenticator, and links for everyone in SharePoint. Each becomes a plan with undo: Security Defaults on
  (without Entra ID P1) or the baseline of Conditional Access in report-only (with P1, `CaSafety` as always), users no
  longer register apps or consent themselves (team owners keep theirs) with the workflow on and you as reviewer,
  restricted guests, passkeys and Authenticator on, no links for everyone. Turning off SMS and voice is guided: the plan
  refuses while someone has nothing else, and names them.
- **Admin roles** (R16): the check of admin roles also names a role on an account synchronised from AD or on the daily
  account with a mailbox, and makes a permanent critical role eligible through PIM as a plan (with Entra ID P2; never an
  emergency account or your own; undo makes it active again); without P2 it says so.
- New checks **Gedeelde postvakken met aanmelding** (a plan blocks signing in, in AD for a synchronised one, with undo)
  and **Aanmeldingen van noodaccounts** (every sign-in of an emergency account in 30 days, from the kept sign-ins).
- **Write permissions per part** (R16, U25): four new parts on *Tenant inrichten*, each asked only when turned on and
  refused by the engine when off: *Beleid van de tenant* (Policy.ReadWrite.Authorization, Policy.ReadWrite.ConsentRequest),
  *Verificatiemethoden* (Policy.ReadWrite.AuthenticationMethod), *PIM* (RoleEligibilitySchedule.ReadWrite.Directory,
  RoleAssignmentSchedule.ReadWrite.Directory) and *Delen in SharePoint* (SharePointTenantSettings.ReadWrite.All).
  `Register-App.ps1 -Onderdelen` takes the same. Only the policies of the advice can be changed: the engine refuses any
  other.
- **Advice for Exchange Online as plans** (R16): the new check *Instellingen van Exchange Online* finds SMTP AUTH on,
  automatic forwarding to outside, no External tag in Outlook, the audit log off and no preset security policy (and says
  when Defender for Office 365 is not in the licence). Turning off SMTP AUTH is guided: whoever sent with it in the last
  30 days (the usage report) keeps an exception per mailbox, and the organisation follows only when those worked; the
  rest is one plan with undo. The new check *SPF, DKIM en DMARC* looks at every own domain through DNS (from the
  Exchange process, without anything extra); turning on DKIM is a plan, with the two CNAME records it needs. Every change
  goes through one fixed command of `Exchange.ps1` that only knows these settings and their values.
- **The checks on a date in the past** (R16): the result of every check is kept per day (400 days), so that *Controles*
  on a date in the past shows what was open then, with the hardening score of that day; checking and solving stay on
  today.
- Two scripts in the library: *krbtgt-wachtwoord vernieuwen* (two rounds, a replication check before each, and round 2
  only after the waiting time of the Kerberos policy; with a preview) and *Domeincontrollers controleren* (LDAP signing
  and channel binding, NTLMv1, SMBv1 and the print spooler; reads only). The krbtgt advice leads to the first.

## 0.39.0

.NET 10, the first version of R16 to R20 (security advice, devices and Intune, own data, the overview and access
reviews) and the fixes from the security review of 26 November 2026 (decisions U14 to U24 of the
[roadmap](docs/roadmap-1.0.md)).

**After updating**: open *Tenant inrichten* once per tenant: the actions on Intune devices need two new permissions
(reading works without them). The tenant database gets a table for the logs kept locally; as with every migration, a
copy of the database is made first.

- **.NET 10** instead of .NET 8: .NET 8 gets no security updates since 10 November 2026, and Moss365 ships the runtime
  itself, so this update also brings a supported runtime (until November 2028). The packages moved along (SQLite,
  ASP.NET Core components, the WebView, MSAL, the directory and protection libraries). Nothing changes in how you work.
- **Security advice** (R16, first part), on *Beveiligingscontroles*:
  - every check has an **ⓘ**: what it is, why it matters, what you do about it and what can go wrong, with the attack
    technique (MITRE ATT&CK) and a link to Microsoft Learn;
  - a risk can be **accepted** for a whole check or for one line, with a reason and an end date (at most a year); it
    goes to the change log as evidence, and after that date the advice is back;
  - the last result of every check is **kept** in the tenant database, with the date it was checked;
  - a **hardening score** of Moss365's own, over AD and Microsoft 365 together and weighted per check, with the score
    of an earlier day next to it.
- **Solving advice as a plan** (R16, U16): in *AD-beveiliging*, *Oplossen als plan* turns off "password not required"
  and "reversible encryption" on the accounts found, with preview, confirmation and undo from the change log. The
  engine only changes these flags.
- **Compromised account** (R16), on the page of a user: *Blokkeerplan maken* makes one plan that disables the account
  in AD (and a cloud account in Entra), sets a new random password (kept and shown nowhere), revokes every sign-in
  session and removes the account from privileged AD groups; next to it what to check afterwards (MFA methods, admin
  roles, forwarding, sign-ins, devices). Nothing is deleted; the plan is shown first, as always.
- **Devices and Intune** (R17), on *Toestellen*:
  - the **life cycle of systems** is data that comes with the updates: Windows 10 (support ended on 14 October 2025)
    and the builds of Windows 11 whose support ended are marked, and "support ends <date>" half a year ahead;
  - **one list over AD, Entra and Intune**: which device is where, hybrid joins that fail, devices in Intune without a
    record in Entra, duplicate records and records not seen for 90 days;
  - **a panel per device** (click its name): user, system, compliance, company or personal, model, serial number,
    encryption, enrolment and last sync, and the records in AD and Entra;
  - **actions as a plan** (U17): sync, restart, a new LAPS password or BitLocker key and a Defender quick scan (also for
    the Helpdesk); rename (with undo) and retire (Administrator); wipe and fresh start (Administrator, only for a
    company device, after typing the device name, one device per plan). The engine checks the same, and asks Intune
    again whether the device is personal right before wiping it;
  - the page of a user shows the **devices in Intune** of that person.
  - The app registration needs two new permissions for these actions: `DeviceManagementManagedDevices.PrivilegedOperations.All`
    and, only because Microsoft asks it for a new BitLocker key, `DeviceManagementManagedDevices.ReadWrite.All`. The
    wizard (*Tenant instellen*) adds them; until then the actions fail with a message, reading works as before.
- **Own data** (R18):
  - the **sign-in and audit logs are kept locally** (U19), on by default: after every read of Entra, the failed and
    risky sign-ins, those of administrators and emergency accounts, and the whole directory audit log go to the tenant
    database, for a year (*Databank en versleuteling*, next to the other retention periods; cleaning up shows a preview
    first, as always). Microsoft itself keeps them 7 to 30 days. The new screen *Bewaarde logboeken* (under
    Beveiliging) is for whoever may see sign-ins, and viewing it goes to the access log. Sign-in logs need Entra ID P1;
    without it the screen says so and the audit log is still kept.
  - **Since your last visit**, on the start screen: what changed outside Moss365 since you last opened the tenant: new
    administrators (privileged AD groups, from the snapshots; admin roles, from the audit log), changes to Conditional
    Access, new app consents and sign-ins of emergency accounts. What Moss365 did itself is in the change log and is
    left out.
  - **Reading without a window** (U19), under *Instellingen*, *Lezen zonder venster*: a task in the Windows Task
    Scheduler reads the tenant every day at the hour you choose, without opening Moss365: AD and Entra into a snapshot,
    and the sign-in and audit logs. It only reads (never a write, scheduled actions keep waiting), runs under your own
    Windows account while you are signed in to Windows, runs later when the PC was off, and leaves the reading to the
    app when Moss365 is open. Microsoft 365 needs *Aangemeld blijven*; AD with a separate account (whose password is
    only typed in the app) and a tenant encrypted with a password wait for the app. The page says what it can read
    and how the last read went.
- **Access reviews** (R20), under Beveiliging, *Toegangsreviews*: every quarter (or every month, half year or year,
  per subject) the owner or a reviewer you choose confirms who is in the privileged AD groups and the admin roles, and
  who has access to the shared mailboxes. Mailing the reviewers is a plan; per member you decide to keep or remove, and
  every decision is a line in the change log (evidence for NIS2 and the CIS Controls, without an Entra ID Governance
  licence). Removing from an AD group or a mailbox is a plan as always; admin roles are removed in Entra itself, because
  Moss365 deliberately has no permission to change roles. Only an administrator records a review; the auditor sees
  everything.
- **The overview** (R19) is the start screen of a tenant, in four blocks: **security** (hardening score, open advice,
  accounts without MFA, administrators, since your last visit), **devices** (not compliant, not seen, systems no longer
  supported, not encrypted), **hygiene and costs** (inactive accounts, guests to confirm, euros a month on licences that
  bring nothing, data quality) and **work** (access reviews due, requests, scheduled actions waiting, follow-up, app
  secrets, failed changes, the status of Microsoft 365). Every tile clicks through to the list behind it and shows its
  trend against 7, 30 or 90 days ago ("−3 in 30 days, better"); the counts are kept per day after every read (only
  counts, never names). The order of the blocks follows the role (the helpdesk sees the work first) and can be changed
  per person. The inventory (folders and files) moved to the settings, *Inventaris*.
- **All tenants together** (U20), under the settings: per tenant only scores and counts, side by side, read when the page
  opens from each tenant's own database (locked tenants stay locked). Nothing is stored or exported together, no tenant
  is opened for it, and no name of a person or object leaves a tenant (a test checks every file stays the same).
- **Board summary**, on the overview: one page (HTML, prints as PDF) with the four blocks and their trends, the hardening
  score over time and the report of the last quarter; only numbers, and the access log notes who saved it.
- **Links** that come from Microsoft data (the actions of Secure Score) are only shown when they are an http or https
  address; anything else (such as a script link or a file share) is no longer clickable.
- **Exports for Excel**: a value that starts with a tab or a line break is now protected against formulas too.
- **Faster**: the tiles keep their count until something changes (the change log tiles no longer read 2,000 lines
  every time), the organisation on a date is built once per snapshot, a full read of Entra asks the members of 20
  groups in one call, the alerts from the snapshots no longer slow down with many accounts, and *Alles nakijken* reads
  the app registrations once instead of twice.
- **A new tenant is encrypted** with your Windows account by default (U24): *Tenants*, *Tenant toevoegen* first shows
  the recovery key, and the tenant is made once you typed its last group. Not encrypting stays a choice. Existing
  tenants stay as they are; *Databank en versleuteling* encrypts them.
- **Releases** now wait for the maintainer's approval before they are published and offered as an update (U23).

## 0.38.6

Languages (R15), seventh part: the last Dutch names, and texts the scanner did not see.

- Kinds of groups, kinds of mailboxes, the names of the MFA methods and the attributes of AD compared with Entra now
  follow the language. They have a fixed key (`Group.KindKey`, `SecurityStatistics.MethodGroup`, the field name of the
  attribute), so that a KPI link keeps working after a change of language; only the name shown is translated.
- A correction to 0.38.2 and 0.38.4: the scanner of hard-coded text did not see text in the same element as `@if` or
  `@foreach`, nor next to an expression with a lambda (`=>`), nor the parameter `Legend`. It sees them now (with a test),
  and about 40 such sentences moved to the dictionaries (logon scripts, leavers, Conditional Access, maintenance,
  devices, follow-up, the table and chart components).
- About 120 short texts inside the expressions of the screens (button texts that change, choice lists, chart labels,
  the status of a CA check, task names, messages) moved to the dictionaries too.
- Still not translated on purpose: the contexts of the change log and the entries of the access log (records), addresses
  and file names, and the names of PowerShell variables.

## 0.38.5

Languages (R15), sixth part: also the texts that the core and the connections build follow the language.

- KPI names (also those with a value, such as "guests from contoso.com" or "last signed in 30 to 90 days ago"), the
  explanations and checks of Conditional Access, the security checks, the tenant set-up, every plan (new users, leavers,
  groups, mailboxes, guests, job profiles, attributes, rooms, helpdesk, clean-up, licences, logon scripts, undo), the
  period report (HTML and CSV), the standards (NIS2, CIS), organisation suggestions and templates, service health,
  requests, scripts, encryption and the messages of AD, Graph, Exchange and the system check: about 1,100 texts.
- A plan is described in the language of whoever makes it; the change log keeps that text as it was written.
- Undo recognises "was already so" in every language, so that such an entry is never offered for undo.
- The default mails (password reminder, guest renewal) are in the language of the interface; the placeholders
  `{naam}`, `{datum}`, `{dagen}` and `{gasten}` stay the same.
- Generic instead of local: the usage location for a new licence comes from the tenant (the most used one) instead of
  a fixed country; a CA warning speaks of "the home country"; the error for a missing kind of user and the hint of the
  system check no longer name local government terms.
- The new example script is in English (scripts stay ASCII).
- The test that every key used exists now also covers Core and the connections.
- On purpose not translated: the change log and access log already written, data from AD and Microsoft 365, the
  technical log, CSV column names of the inventory, the status words of the Exchange script and technical names.

## 0.38.4

Languages (R15), fifth part: the whole interface layer now speaks five languages, also what it builds in code.

- Column titles of the tables, labels of the pickers, titles of the plans, the choice lists (systems, roles mode,
  kinds of groups, relative dates, sections), short words in tables ("ingeschakeld", "nooit", "ja"), counts ("12
  gasten"), the rows of a user's details, the messages of the services (tasks, updates, encryption, tenants,
  maintenance, scripts) and the status texts of tasks and message trace.
- Lists of choices are no longer kept in a static field, so that they follow a change of language; a test keeps it so.
- The scanner of hard-coded text also guards the parameters of components (Label, Title, Context, ...).
- On purpose not translated: what is already written in the change log and the access log (records), and data from AD
  and Microsoft 365.
- Still Dutch for now: the texts that Core builds (plan descriptions, check results, KPI names, warnings) and the
  messages of the connections. They are the last part of R15.

## 0.38.3

Languages (R15), fourth part.

- A correction to 0.38.2: the count of "zero hard-coded texts" did not see text that sits next to a value in the same
  sentence (such as "Lid van (12)" or "Wachtwoord laatst ingesteld: ..."). Those 70 sentences are now in the
  dictionaries too, as one text with holes per language.
- The messages that the screens build in code (errors, confirmations, button texts that change, column titles of
  tables, plan titles): about 300 texts moved to the dictionaries.
- Core and the screens share one language (`Localization/Texts`), so that the texts Core builds can follow next.
- A test refuses an attribute written as `title=T[...]` without `@` (it would show the code instead of the text).
- Still Dutch for now: short words in code (such as "ingeschakeld", "nooit"), the texts that Core builds (plan
  descriptions, check results, KPI names) and the messages of the connections. They follow in the next versions.

## 0.38.2

Languages (R15), third part: **every screen** now takes its text from the dictionaries in five languages (English,
Dutch, Brazilian Portuguese, French, German): users, groups, mailboxes, rooms, guests, new user, offboarding, job
profiles, Conditional Access (overview, policy, editor, management), security, licences, devices, Teams and
SharePoint, reports, requests, scripts, the change log, history, statistics, organisation, connections, tenants,
set-up, database and updates. About 1,300 texts moved; the count of hard-coded text in the screens is now zero, and
a test keeps it there.

- A new test checks that every translation keeps the same links and markup as English (a wrong link in a translation
  is caught before release).
- The hint about offices on the Organisation screen named a group and offices of one organisation; it is now general.
- Still Dutch for now: messages that the app builds in code (plan descriptions, check results, KPI names, errors).
  They follow in the next versions.

## 0.38.1

Languages (R15), second part: every shared component now comes from the dictionaries in five languages, as well as
the start screen and the search with Ctrl+K: the plan preview (choose, run, confirm, results), the sign-in box, tables
(filters, columns, export, pages), the helpdesk card, absence and forwarding, message trace, memberships, editing a
user, the script forms and results, the library update, encryption, updates, job profiles, Conditional Access
management, rooms, security and sign-ins. Sentences with a link or a value are one text per language, so a
translation can put the words in its own order. 600 texts fewer in the screens (1,204 left, in the pages).

## 0.38.0

Languages: the start of step R15 of the [roadmap](docs/roadmap-1.0.md).

- The interface comes from **dictionaries in five languages**: English, Dutch, Brazilian Portuguese, French and German
  (see [docs/translating.md](docs/translating.md)). A missing text falls back to English, never an empty button.
- **System language by default**: Moss365 follows the display language of Windows (Dutch for nl-BE and nl-NL, ...);
  a language it does not have gives English.
- **Settings, Language**: choose the language yourself (per Windows user on this PC); it wins over the system
  language and works at once. **Dates and numbers** follow the language.
- Translated in this version: the **menu**, the **settings side bar**, the layout (search, favourites, not live,
  no access, the past), the **status bar**, the **tenant bar** and the language screen. The other screens are still
  Dutch for now and follow in the next versions; data from AD and Microsoft 365 and the content of scripts are never
  translated.
- Tests report missing translations and different holes ({0}) between languages, and refuse **new hard-coded text**
  in the screens (the count of what is left may only go down).

## 0.37.0

Reporting and working as a team: step R14 of the [roadmap](docs/roadmap-1.0.md).

- **Status van Microsoft 365** on the start screen: which services have a problem, the open incidents and advisories,
  and the messages of the message center that ask for an action before a date. "Is it us or Microsoft?"
- **Rapporten** (Inzicht): a report for the last month, the last quarter or dates you choose, built from the stored
  history of the tenant, so also for a period in the past: the indicators at the start and the end, the changes from
  the change log (succeeded and failed), data quality and the standards. **Saved as HTML** (prints well as PDF) or
  **CSV** for Excel; saving goes to the access log.
- **Datakwaliteit**: per department, how many active employees have a job title, office, manager and phone, with a
  score; click a department to see who misses what.
- **Normen**: the security checks mapped to the measures of **NIS2 (article 21)** and the **CIS Controls v8**, with the
  evidence Moss365 keeps (change log, snapshots). Evidence, not a certificate.
- **Gasten verlengen** (Mensen): every guest gets a sponsor who confirms each year that the guest is still needed.
  **Sponsors mailen** (one mail per sponsor) and **Gasten uitschakelen** are plans; nothing is deleted.
- **Aanvragen** (Werk): HR or a department head asks for a new colleague (with job profile and start date) or announces
  a departure. The administrator approves or rejects (in the change log); after approval **Plan maken** opens Nieuwe
  gebruiker filled in, or Uitstroom for the person who leaves.
- **Tenants vergelijken** (Instellingen, Alle tenants): the Conditional Access policies by name (state and the main
  settings, without users or groups) and the settings of Moss365 of this tenant against another one, only the
  differences. Nothing is stored or mixed.
- New read permissions: ServiceHealth.Read.All and ServiceMessage.Read.All; run **Tenant inrichten** again.

## 0.36.0

Devices, Teams and SharePoint: step R13 of the [roadmap](docs/roadmap-1.0.md).

- **Toestellen** (Inzicht): the computers in AD with their **systems and versions** (systems Microsoft no longer
  supports are marked), the number **per OU**, and the computers that have **not signed in** for 90 days (or a number
  of days you choose). **Computers uitschakelen** is a plan as always; nothing is deleted.
- **Devices in Intune**: devices that are **not compliant** or **not seen for 30 days**, with their user, and the
  systems and versions in use.
- **Teams en SharePoint** (Groepen en toegang, only with Microsoft 365): every Microsoft 365 group and Team with its
  owners, members and guests, and its last activity; filters for **no owner**, **with guests** and **not used**.
- **SharePoint**: the **sharing setting** of the tenant in words (with a warning for links without sign-in), the total
  storage, **sites without activity** for 90 days and the largest sites.
- New read permissions: DeviceManagementManagedDevices.Read.All, Reports.Read.All and SharePointTenantSettings.Read.All.
  Run **Tenant inrichten** again to add them. When the usage reports hide names (a Microsoft 365 setting), the screen
  says how to show them.

## 0.35.0

Licences and costs: step R12 of the [roadmap](docs/roadmap-1.0.md).

- **Licenties en kosten** (Inzicht): the subscriptions (in use, total, free) with a **price per month** per licence, the
  cost per month in total, **per division and per department**.
- **Licences that bring nothing**: disabled accounts with a licence, shared mailboxes with a licence, **duplicate plans**
  (a licence that is already part of another one, such as Office 365 E3 next to Microsoft 365 E3) and licences not used
  for 90 days, with the possible saving per month. **Licenties weghalen** is a plan: for a duplicate plan only the
  licence that is already included goes, otherwise all licences of the account (with the warning about the mailbox).
- **Volgens het functieprofiel**: who has more or fewer licences than their job profile.
- The number of accounts with a licence that brings nothing is a **KPI with its trend** (from the snapshots).
- The 0.33 changes came to GitHub with 0.34 (the 0.33 build stopped on a test); the status bar no longer reads the live
  parts while they change.

## 0.34.0

Security checks: step R11 of the [roadmap](docs/roadmap-1.0.md) (the Conditional Access editor of R11 came in 0.30).

- **Beveiligingscontroles** (Beveiliging): every check says OK, aandacht or probleem, and onbekend when the data is not
  enough; **Alles nakijken** runs them all, and every check shows its details. Everything reads only.
  - **Meldingen uit de momentopnames** (last day, 7 or 30 days): a new member of a privileged group or an admin role, a
    disabled account enabled again, a changed Conditional Access policy; also what happened outside Moss365.
  - **Doorsturen naar buiten**: mailboxes that forward outside the own domains, and (optionally, slower) inbox rules
    that forward or redirect: a classic sign of a taken-over account.
  - **Apps**: secrets and certificates that expired or expire within 30 days, broad application permissions and
    consents users gave themselves.
  - **Microsoft Secure Score** with its trend and the actions that gain the most.
  - **Beheerrollen en PIM**: who has which role permanently, active or eligible (with P2), and too many Global
    Administrators.
  - **Zwakke MFA-methoden**: who only has SMS, phone or e-mail, or nothing.
  - **Risicovolle gebruikers** (Entra ID P2).
  - **AD-beveiliging**: accounts with an SPN, unconstrained delegation, "password not required", reversible encryption,
    the age of the krbtgt password, adminCount orphans and outdated operating systems.
  - **Groepsbeleid**: policies that are not linked, empty or switched off.
- **AD-gezondheid**: a new library script for the domain controllers, replication, FSMO roles and time.
- New permissions for the app registration (SecurityEvents.Read.All, RoleManagement.Read.Directory,
  IdentityRiskyUser.Read.All): run **Tenant inrichten** once to add them.

## 0.33.0

Helpdesk and daily work: step R10 of the [roadmap](docs/roadmap-1.0.md).

- **Temporary Access Pass** on the page of a user (one-time or for a while, 1 hour to 7 days), for someone who lost
  their phone or still has to register MFA. The pass is shown once after running and is never stored or logged.
- **BitLocker en LAPS** (Mensen): find a device (or give the key ID BitLocker shows) and look up the recovery key and the
  local administrator password, from Entra and from AD (Windows LAPS and the legacy LAPS). Every lookup goes into the
  access log; nothing is stored.
- **Where a lockout comes from**: for a locked account, the Helpdesk card starts the new library script *Waar komt de
  vergrendeling vandaan* (the lockout events on the domain controllers and the wrong passwords per domain controller).
- **Wachtwoord verloopt binnenkort** (Mensen): the accounts in AD whose password expires within 7, 14 or 30 days (the
  password age of the domain with a suggestion from the data), and a **reminder e-mail** to the user and/or the
  manager, as a plan, with your own text.
- **Berichtspoor** on the page of a user: did the mail arrive, and if not, why (spam, quarantine, failed), the last 10
  days; in the access log.
- **Afwezig en doorsturen** for someone else (illness, leave): an automatic reply between two moments, and forwarding
  with an end date; the end of the forwarding is prepared in Gepland (never run by itself).
- **Alle groepen, ook via andere groepen** on the page of a user, with the groups in between ("via X -> Y").
- **Aangemeld blijven op deze pc** (Verbindingen, off by default): the sign-in stays in the tenant database, encrypted
  with your Windows account; switching it off removes it.
- **Ctrl+K** also finds scripts.
- New permissions for the app registration (UserAuthenticationMethod.ReadWrite.All, BitLockerKey.Read.All,
  DeviceLocalCredential.Read.All, Device.Read.All, Mail.Send): run **Tenant inrichten** once to add them.

## 0.32.0

Logon scripts: with this, step R9 of the [roadmap](docs/roadmap-1.0.md) is done.

- **Logonscripts** (Groepen en toegang, for a tenant with AD): the scripts in NETLOGON (by default
  \\domain controller\NETLOGON, with your Windows account; another path on Verbindingen), with per script how many users
  have it and **what it does in plain language**: the drives it maps (also only for members of a group), printers,
  other scripts and programs it starts.
- **Na te kijken**: users whose logon script does not exist, and scripts nobody uses.
- **Edit a script** in the editor; saving is a plan with the difference (lines added and removed). The version from
  before goes into the change log: undo puts it back, and on the script page every earlier version can be put back in
  the editor.
- **The logon script of users**: choose users (those with the script, and others) and another script or none, as a
  plan (scriptPath in AD).
- **Via groepsbeleid**: the logon and logoff scripts that Group Policies assign (scripts.ini in SYSVOL) are shown, not
  changed.

## 0.31.1

The script library is filled: 14 scripts join the one there was, made general (no names, addresses or servers of an
organisation; English headers; ASCII; the app connects, no Connect- in the scripts). They reach every installation
through **Bibliotheek bijwerken** on the Scripts screen, and come with the download.

- **Rooms**: Room bekijken, Room instellen (name, building, city, floor, capacity, room list), Agendarechten van alle
  rooms, Rooms koppelen in Microsoft Places.
- **Shared mailboxes**: Postvakken vergelijken, Send on Behalf nakijken, Verzonden items bewaren in gedeelde postvakken,
  Toegang tot een gedeeld postvak via een groep, Agenda-export.
- **Users and groups**: Groepen van een gebruiker, Mailgroepen vergelijken, Ontvangers aan een lijst toevoegen,
  Medewerkers en groepslidmaatschap, Diagnose wachtwoordverloop.
- Not taken over: scripts for one organisation only, software removal on workstations (that runs through the RMM tool,
  not through Moss365), and copying groups from one user to another (Spiegelgebruiker and job profiles do that, with a
  plan).

## 0.31.0

New users in the cloud, and guests in groups (step R9 of the [roadmap](docs/roadmap-1.0.md); logon scripts follow).

- **Nieuwe gebruiker in de cloud**: for a tenant without AD, Nieuwe gebruiker creates the account directly in Entra (and a
  hybrid tenant can choose "Enkel in de cloud"). The naming rules, the kind of user (or a domain of the tenant), the
  department, office, job title and a **job profile** work as before; the licences and the cloud groups come in the
  same plan, right after the account. Mail groups and access to shared mailboxes wait until Exchange has made the
  mailbox (a few minutes after the licence) and become a follow-up task. The first password is shown once and never
  stored or logged; the user changes it at the first sign-in. Scheduling for later works too.
- **Guests in groups**: on the page of a guest, its groups can be edited (cloud groups only: a guest does not exist in
  AD); the member picker of a cloud group finds guests; on Gastgebruikers the selected guests go into a group (or out of
  it) in one plan; and inviting a guest can put it in cloud groups right away, in the same plan.
- In the pickers a guest shows as "gast" with its e-mail address.

## 0.30.1

- **Download link on the front page** of the repository: always the newest version (setup, portable zip and
  SHA256SUMS.txt), with a badge that shows the newest version.
- A stable version is now published as the **latest release** instead of a prerelease, so that GitHub's link to the
  newest release works (also the link in Versie en updates). Previews (a version with a suffix) stay prereleases.

## 0.30.0

A big step: the full Conditional Access editor (brought forward from step R11 of the [roadmap](docs/roadmap-1.0.md)),
a choice per tenant for roles, and a status bar that tells whether the data of the tenant is live.

- **Conditional Access editor**: open a policy and choose **Bewerken** to change every part: who (all users, chosen
  users, groups, roles and guests, and who is excluded), apps (all, Office 365, admin portals, chosen apps or a user
  action), client apps, platforms, locations, sign-in and user risk, device code flow and authentication transfer, a
  device filter, the grant (block, or MFA, compliant or hybrid device, approved app, password change, an authentication
  strength, one or all) and the session (sign-in frequency, staying signed in). Fields the editor does not know (an
  authentication context, an app filter, other session settings) stay as they are.
- **Preview before the plan**: what changes in plain language, the safety check (emergency accounts excluded, not
  locking yourself out; a button adds missing emergency accounts), who falls under the policy after the change with
  **who comes in and who drops out**, and the exact JSON for Graph. Only the changed parts go to Graph, with the old
  values in the change log, so it can be undone. The engine checks every change again; a policy is never deleted.
- **New policy without a template** and **Kopie maken** of an existing policy: always in report-only (or off).
- The policy page shows the **JSON** as Entra gives it; Beheren links to the editor; comparing backups now also names
  platforms, risk, transfer, session settings, guests and the device filter.
- **Roles per tenant** (Organisatie > Rollen in Moss365): *Zonder rollen* (everyone who uses the app may do
  everything), *Rollen als ze er zijn* (the transition, as before) or *Rollen verplicht* (without a role only reading).
  Requiring roles is refused when you have no role with Instellingen yourself, so you cannot lock yourself out.
  Verbindingen shows the choice; importing settings keeps it.
- **The status bar is about the open tenant**: *online* when its data is live for every system it has (with the time
  per system), *deels online* when only AD or Entra is live, *offline* with the reason (inventory, the past, no
  network). Click it to go to Verbindingen.

## 0.22.0

Job profiles: what someone in a job gets (step R9 of the [roadmap](docs/roadmap-1.0.md); new users in the cloud and
logon scripts follow).

- **Functieprofielen** under Instellingen > Organisatie: groups (AD, cloud, Microsoft 365 and mail groups), shared
  mailboxes, licences, and the job title, department, office and logon script. Start empty or **from a user** who already
  has the job; dynamic and privileged groups are never part of a profile. Groups and mailboxes are kept by name, so a
  profile also works in another tenant (export and import). A saved profile that changes gets a new version.
- **Nieuwe gebruiker** with a job profile: the profile fills in what the form leaves empty, and its groups, mailbox access
  (through the access group) and licences come along in the plan (AD groups now, the rest after the synchronisation).
- **Mensen > Functieprofielen**: who holds a profile (got it in the app, or has its job title), how much is missing and
  how much is more; per person the comparison and **Plan maken** to apply it. What is more than the profile is only
  proposed, unticked; licences are never removed here. After running, the app remembers who got which version and when.
- The **user page** names the job profile, with what is missing.
- The app's access levels are now called **Rollen in Moss365** (Organisatie > Rollen), so they are not confused with job
  profiles.

## 0.21.1

A tenant with only Microsoft 365 now shows nothing of Active Directory, and the choice is easy to find.

- **The choice is where you look for it**: Verbindingen starts with "Wat heeft deze tenant?" (AD en Microsoft 365,
  enkel Microsoft 365 or enkel Active Directory) with a Bewaren button; adding a tenant asks it right away; on Tenants
  it has its own column and the button is now called Aanpassen (it used to hide behind "Naam en kleur").
- **Nothing of AD for a tenant without AD**: what was read or imported from AD earlier is left out of every screen at
  once (and comes back when you switch AD on again); Gebruikers has no Bron, OU or sAMAccountName and no AD filters,
  Groepen no AD tiles or columns, Uitstroom only the cloud steps (disable, sessions, groups, mailbox, licences), Nieuwe
  groep only the cloud kinds, Spiegelgebruiker only the cloud groups, and Overzicht describes only Microsoft 365.
- **Nieuwe gebruiker** is hidden for a tenant without AD: it creates accounts in AD. Creating users directly in the
  cloud is added to step R9 of the [roadmap](docs/roadmap-1.0.md).

## 0.21.0

A shorter, calmer menu, asked for after 0.20.

- **The menu is grouped by subject**: Mensen, Groepen en toegang, Beveiliging, Inzicht and Werk. The group of the
  page you are on is always open; the others open and close with a click and the app remembers which ones you closed.
  A closed group shows a dot when something under it is still open.
- **Instellingen is one item** with its own screen and side bar: Deze tenant (Organisatie, Verbindingen, Tenant
  inrichten, Databank en versleuteling), Alle tenants (Tenants) and Deze pc en de app (Controle van deze pc, Versie en
  updates).
- **Organisatie is split into parts**, each its own page: Bedrijf en naamgeving, Diensten en afdelingen, Kantoren,
  Nieuwe groepen, Profielen and Noodaccounts.
- **Versie en updates** is a page of its own (about Moss365, the update channel and searching for updates); Controle
  van deze pc keeps only the check of the computer.
- **Favourites**: the star next to a menu item puts it at the top of the menu.
- **Search with Ctrl+K** (or the Zoeken button): pages, users, groups, shared mailboxes and rooms; arrows and Enter to
  go, Escape to close.
- Screen readers now hear whether a menu group is open or closed.

## 0.20.0

The settings of an organisation, suggested from its own data (first part of step R9 of the
[roadmap](docs/roadmap-1.0.md); job profiles and logon scripts follow).

- **Naming of new accounts** per tenant (Organisatie): the part before the @ (an.peeters, anpeeters, a.peeters,
  apeeters, peeters.an, peetersa) and the display name (An Peeters, Peeters An, PEETERS An, Peeters, An). The app
  suggests the pattern most accounts follow; Nieuwe gebruiker uses it.
- **More suggestions from the data**, on the Organisatie page (also for a tenant with only AD) and in the wizard: the
  company, the OU for new users (with its UPN domain), the OU for offboarding, the OU for new groups, the naming, and
  readable names for the licences, next to departments, offices, group prefixes, synchronised OUs and emergency
  accounts. Nothing is taken over by itself.
- **Templates**: local government, school, small business. Applying one only adds what is missing (divisions, kinds of
  users, group prefixes).
- **Export and import** of the settings, to start a tenant from another one. Importing keeps the AD domain and the
  emergency accounts of the tenant; you check and save.
- Two texts no longer point to internal documents.

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
