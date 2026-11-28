# Google Workspace in Moss365

Google Workspace is the third system a tenant can have, next to Active Directory and Microsoft 365. You turn it on on
the page Tenants (G0). From G1 on, Moss365 reads it, and from G2 on it changes it, always as a plan. The plan for
onboarding and offboarding over all platforms, security and costs is chapter 15 of the [roadmap](roadmap-1.0.md).

## Your own Google project (U35)

Moss365 talks to Google with an OAuth client of the organisation itself, never with a client of the makers. You make
this project once per organisation.

### With the wizard (U41)

On the page *Inrichten* (settings), a tenant with Google has a part *Google Workspace inrichten*:

1. Install the [Google Cloud CLI](https://cloud.google.com/sdk/docs/install) on this PC (or `winget install
   Google.CloudSDK`). Fill in a project id (6 to 30 lowercase letters, digits and hyphens, for example
   `moss365-contoso`) and the primary domain, and choose *Controleren met gcloud*. *Aanmelden bij gcloud* signs gcloud in
   in the browser, with an account that may create projects; gcloud uses its own client for that, as the wizard of
   Microsoft 365 uses Graph PowerShell.
2. The overview says what is in order and what is missing. What gcloud can do becomes a plan: the project, its APIs
   (the Admin SDK and those of the parts that are on) and, with the part Gmail settings, the service account and its
   key. The key goes straight into the tenant database, encrypted with your Windows account; the file gcloud writes is in
   a folder of its own in the temporary folder and is removed at once.
3. By hand, as Google has no command for it: the consent screen of the type Internal and an OAuth client of the type
   Desktop app (fill in its client ID and secret on the same page), and with the part Gmail settings the domain-wide
   delegation (the page shows the client id and the scopes to copy).
4. *Opnieuw controleren* after each step signs in at Google and checks the sign-in, the admin role, every API and the
   delegation.

### With the script

Whoever prefers a script, or works on another PC, does the same with the script:

1. **The project and the API.** Run [`tools/Register-GoogleApp.ps1`](../tools/Register-GoogleApp.ps1) with the Google
   Cloud CLI (`gcloud`), signed in as someone who may create projects:

   ```powershell
   .\tools\Register-GoogleApp.ps1 -ProjectId moss365-contoso
   ```

   The script creates the project, or uses it when it exists already, and turns on the Admin SDK API. Running it
   again changes nothing that is already there.
2. **The consent screen.** In the console, make the OAuth consent screen of the type **Internal**, so that only
   accounts of the organisation can use it. The script shows the link. Google has no command for this step.
3. **The OAuth client.** Also in the console, make an OAuth client of the type **Desktop app**. Again the script
   shows the link.
4. **Connections in Moss365.** Fill in three things for the tenant:
   - the primary domain of Google Workspace;
   - the client ID;
   - the client secret.

   Google says a Desktop app cannot keep its secret secret, so it is no real secret. Moss365 still keeps it only in
   the encrypted tenant database, and never shows or logs it.

## Signing in

**Read Google Workspace** on Connections, or **Load everything live**, shows a sign-in link in the bar at the top,
as for Microsoft. Open it in a browser on this PC and sign in as an administrator of the domain.

- Google sends the browser back to a loopback address of this PC (`http://127.0.0.1` on a free port). There Moss365
  takes the code.
- The flow is the OAuth flow for installed apps with PKCE: the code is only of use to whoever started the sign-in.
- Moss365 accepts only an account of the domain of the tenant, or of a subdomain of it. An account of another domain
  is refused.
- The tokens stay in memory for the session and are never written to a log. With **Stay signed in on this PC** (the
  same setting as for Microsoft) the refresh token is kept in the tenant database, encrypted with your Windows account;
  another part on asks a new sign-in, and turning the setting off removes it.
- Reading without a window (R18) never signs in to Google.

## The scopes

Every sign-in asks these scopes and no others (since G2 also to change users, groups and calendar resources, as the
base permissions of Microsoft 365; after the update to 0.42.0 Google asks the consent once more):

| Scope | For |
| --- | --- |
| `openid`, `email` | Who signed in, to check the domain |
| `admin.directory.user` | Users: read them (name, addresses and aliases, the organisational unit, suspended or archived, administrator, 2-Step Verification, last sign-in, the employee id and the department), and change them (G2) |
| `admin.directory.user.security` | Signing a user out and revoking the access of apps (G2) |
| `admin.directory.group` | Groups: address, name, description, aliases, the members; and change them (G2) |
| `admin.directory.orgunit.readonly` | The organisational units (only read) |
| `admin.directory.resource.calendar` | Calendar resources: rooms and equipment, with capacity, building and floor; and change them (G2) |
| `admin.directory.rolemanagement.readonly` | Admin roles of users, for the whole organisation or one organisational unit (only read) |
| `admin.reports.audit.readonly` | The sign-in and admin logs (only read) |

Licences, group settings, the alerts and the Gmail settings are each a **part** of Google on *Verbindingen* (U40,
U34), off by default: its scope is asked only when the part is on. After turning a part on or off you sign in to
Google again, with the new consent.

| Part | Scope | API |
| --- | --- | --- |
| Google licences | `apps.licensing` | `licensing.googleapis.com` |
| Google group settings | `apps.groups.settings` | `groupssettings.googleapis.com` |
| Google alerts | `apps.alerts` | `alertcenter.googleapis.com` |
| Gmail settings | `gmail.settings.basic`, `gmail.settings.sharing`, for the service account only | `gmail.googleapis.com` |

The script turns all these APIs on; an API that is on gives no right by itself.

## What Moss365 reads (G1)

**Check the project** on *Verbindingen* signs in and asks every API one small question. When something does not work
it says what to do: turn an API on, sign in again for a scope, or give the account an admin role.

Every read becomes a snapshot. The tables are `google-gebruikers.csv`, `google-groepen.csv`, `google-eenheden.csv`,
`google-agendabronnen.csv` and `google-rollen.csv`, and as with the other systems only what changed is stored.
Resources and roles are read each on their own. When the signed-in administrator lacks the right for one of them, the
rest is still read, and what could not be read stays as it was. The Google users are linked to their AD and Entra
accounts on the page Linking persons.

After every read, the login and admin activities of the Reports API are kept locally, as for Entra (R18): the failed
and suspicious logins, those of Google administrators and emergency accounts, and every admin activity. The page of the
kept logs shows them under Google Workspace, with the alerts when that part is on.

With the part **Google licences**, Statistics shows the edition (the Workspace SKU most users have), what that edition
lacks of Vault, context-aware access, advanced endpoint management and the security center, and the active accounts
without a Workspace licence. The editions are data in `src/Moss365.Core/Google/GoogleEditions.cs`: check them against
Google's comparison of the editions when Google changes them.

## What Moss365 changes (G2)

Every change is a plan, as for AD and Microsoft 365: a preview, the confirmation, the role of the app, the change log
(label Google Workspace or Gmail) and undo with the values from before. The engine checks the role and the parts once
more before it runs a step. Moss365 never deletes a user or a group in Google.

- **Users**, in the section Google Workspace on the page of the person: the first and last name, the department, the
  employee id, the organisational unit and suspended; aliases (an address of a domain of the tenant that nobody has);
  a new password (only in the plan, never in a log; Helpdesk); signing out of every session (Helpdesk); revoking the
  access of every app (cannot be undone). A Google administrator as the target needs the profile Beheerder.
- **A new user** on the page *Nieuwe Google-gebruiker*: the address by the naming rules of the organisation, the
  organisational unit, the department and the employee id; the first password has to be changed at the first sign-in.
- **The source of truth** (U33, the page *Bron van waarheid*): when Google gets its users from Entra (the provisioning
  of the enterprise app) or from AD (Directory Sync), the names, the department, the employee id and suspended follow
  from there, and with Directory Sync also the organisational unit (and with Password Sync the password). Those fields
  are shown, not edited: Moss365 refuses to write them in Google, where the synchronisation would overwrite them. The
  same holds for the name and the members of groups that follow another platform.
- **Groups**, on the page *Google-groepen* and the page of a group: the name and the description; the members, read
  when you ask, added with a role (member, manager, owner), removed, or given another role; an owner or a manager needs
  Beheerder, a member Groepsleden. With the part Google group settings: who may join, post and see the members, and
  members from outside, with a warning when the group opens up.
- **Calendar resources**, on the page *Google-agendabronnen*: the name, the capacity, the floor and the description.
- **Licences**, with the part Google licences, on the page of the person: assign a licence the organisation already
  uses, or remove one (Gmail, Drive and Calendar stop for that user).

## Gmail settings (part, U34)

Google only lets an administrator change the Gmail settings of someone else through a **service account with
domain-wide delegation**. Moss365 then reads and changes, on the page of the person: forwarding (to an address outside
the organisation the receiver has to confirm it first, and the plan warns), the automatic reply, delegates (only of the
organisation), filters (a filter that forwards is marked; a removed filter comes back with undo) and the signature.
Reading them goes to the access log.

1. **The service account and its key.** Run the script with `-Gmail`:

   ```powershell
   .\tools\Register-GoogleApp.ps1 -ProjectId moss365-contoso -Gmail
   ```

   It creates the service account `moss365-gmail` in the project and a key in `moss365-gmail-sleutel.json`, and shows
   its client id and the scopes. An organisation policy (`iam.disableServiceAccountKeyCreation`) may forbid keys of
   service accounts; a super administrator then allows keys for this project first.
2. **The domain-wide delegation.** As a super administrator in the admin console of Google: Security, API controls,
   Domain-wide delegation, *Add new*: the client id, with the scopes
   `https://www.googleapis.com/auth/gmail.settings.basic,https://www.googleapis.com/auth/gmail.settings.sharing`.
   Moss365 never asks for more than these two.
3. **The key in Moss365.** Turn on the part Gmail settings on *Verbindingen* and choose the JSON file under *Serviceaccount
   voor Gmail*. Moss365 keeps the key in the tenant database, encrypted with your Windows account, and never shows or
   logs it; delete the file afterwards. *Vervangen* takes a new key, *Verwijderen* removes it.
4. **Check the project** tries the service account on behalf of the administrator who signed in, and says when the
   delegation is missing.

A token of the service account is asked per user, for an hour, with a JWT signed with the key (RS256), and stays in
memory. Only Google's token address (`oauth2.googleapis.com`) gets it: a key file that names another one is refused.
