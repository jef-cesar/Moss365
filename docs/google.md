# Google Workspace in Moss365

Google Workspace is the third system a tenant can have, next to Active Directory and Microsoft 365. You turn it on on
the page Tenants (G0). From G1 on, Moss365 reads it. The plan for writing, onboarding and offboarding over all
platforms, security and costs is chapter 15 of the [roadmap](roadmap-1.0.md).

## Your own Google project (U35)

Moss365 talks to Google with an OAuth client of the organisation itself, never with a client of the makers. You make
this project once per organisation. The steps:

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

## What Moss365 reads (G1)

Moss365 only reads. It asks for these scopes and no others:

| Scope | For |
| --- | --- |
| `openid`, `email` | Who signed in, to check the domain |
| `admin.directory.user.readonly` | Users: name, addresses and aliases, the organisational unit, suspended or archived, administrator, 2-Step Verification, last sign-in, the employee id and the department |
| `admin.directory.group.readonly` | Groups: address, name, description, aliases, the number of direct members |
| `admin.directory.orgunit.readonly` | The organisational units |
| `admin.directory.resource.calendar.readonly` | Calendar resources: rooms and equipment, with capacity, building and floor |
| `admin.directory.rolemanagement.readonly` | Admin roles of users, for the whole organisation or one organisational unit |
| `admin.reports.audit.readonly` | The sign-in and admin logs |

For licences, group settings and the alerts, Google has no scope that only reads. Each of them is a **part** of
Google on *Verbindingen* (U40), off by default: its scope is asked only when the part is on, and Moss365 only reads
with it. After turning a part on or off you sign in to Google again, with the new consent.

| Part | Scope | API |
| --- | --- | --- |
| Google licences | `apps.licensing` | `licensing.googleapis.com` |
| Google group settings | `apps.groups.settings` | `groupssettings.googleapis.com` |
| Google alerts | `apps.alerts` | `alertcenter.googleapis.com` |

The script turns all these APIs on; an API that is on gives no right by itself.

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
Google's comparison of the editions when Google changes them. Writing comes with G2, and the
Gmail settings of someone else come with the part "Gmail settings" and a service account (U34).
