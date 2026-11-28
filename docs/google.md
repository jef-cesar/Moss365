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
- The tokens stay in memory for the session. They are never written to the disk or to a log.
- Reading without a window (R18) never signs in to Google.

## What Moss365 reads (G1)

Moss365 only reads. It asks for these scopes and no others:

| Scope | For |
| --- | --- |
| `openid`, `email` | Who signed in, to check the domain |
| `admin.directory.user.readonly` | Users: name, addresses and aliases, the organisational unit, suspended or archived, administrator, 2-Step Verification, last sign-in, the employee id and the department |
| `admin.directory.group.readonly` | Groups: address, name, description, aliases, the number of direct members |
| `admin.directory.orgunit.readonly` | The organisational units |

Every read becomes a snapshot. The tables are `google-gebruikers.csv`, `google-groepen.csv` and `google-eenheden.csv`,
and as with the other systems only what changed is stored. The Google users are linked to their AD and Entra
accounts on the page Linking persons.

Later steps of G1 add resources, licences, admin roles, the logs and the alerts. Writing comes with G2, and the
Gmail settings of someone else come with the part "Gmail settings" and a service account (U34).
