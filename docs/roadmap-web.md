# Roadmap Moss365 Server (web version, after 1.0)

First comes **Moss365 1.0 Desktop** ([roadmap-1.0.md](roadmap-1.0.md)). This roadmap describes what comes next: the same
app as a **web app on your own Windows server with IIS**, for teams that manage together. The Desktop stays a full
edition, for those who do not need a server and as a fallback when the server is unreachable.

This is a plan for later. Details may change based on what we learn in 1.0.

## 1. Why a web version

| | Desktop | Server |
| --- | --- | --- |
| Where | On every admin PC separately | One server; opened in the browser, also on a PC without installation |
| Sign-in | Per PC, with the link in the app | **Single sign-on with Microsoft 365**: MFA and Conditional Access apply as for any Microsoft app |
| Data | One database per tenant per PC | One database per tenant on the server, **shared by the team** |
| Reading | When the app is open | **Always**: the server reads in the background, also at night; snapshots without gaps |
| Alerts | When the app is opened | **Immediately**: e-mail or Teams message on a new admin, a changed policy, an expiring secret |
| Scheduled actions | Ready when the app is opened | Ready for the whole team, with a notification |

What does not change: every change is a plan with preview and confirmation, the roles are enforced in the engine,
scheduled actions never run by themselves, the app can do no more than the person using it.

## 2. Architecture

```
Browser  --HTTPS + websocket-->  IIS: Moss365.Server (ASP.NET Core, Blazor Server)
                                   |- sign-in: Entra ID (OpenID Connect), roles in the token
                                   |- the same screens as the Desktop (Moss365.UI)
                                   |- background service per tenant: reading, snapshots, alerts
                                   |- one database per tenant (as on the Desktop)
                                   '- technical log in the database and the Windows event log
   reading:                            writing:
   Graph with the server's own         Graph and Exchange on behalf of the signed-in person (on-behalf-of)
   identity (read-only permissions)    AD on behalf of the signed-in person (Kerberos delegation) or
   Exchange with a certificate         with a service account with limited rights (decision S3)
   AD with a gMSA (read only)
```

- **Reading** uses the server's identity: a Reader needs no admin rights of their own.
- **Writing** happens on behalf of the person: Microsoft's audit shows the real name, and whoever may do nothing in
  Entra or AD cannot do it through the app either.
- **Multiple tenants**: one database and one background service per tenant; a user only sees the tenants where they
  have a role, with their role per tenant. A new **Server administrator** role adds tenants and sees the health of all
  tenants, but not their data.

## 3. Decisions

| # | Decision | Proposal |
| --- | --- | --- |
| S1 | Access from outside | Internal network and VPN only; later possibly Entra Private Access or Application Proxy |
| S2 | Name and certificate | A name per organisation (e.g. `moss365.<domain>`), certificate from the internal CA or a public one |
| S3 | Writing to AD | Kerberos delegation (AD sees the real person) if it works in the environment; otherwise a gMSA with delegated rights and the person in the change log |
| S4 | Encryption on the server | As on the Desktop: AD group (DPAPI-NG) or Key Vault (chapter 6 of the 1.0 roadmap) |
| S5 | Alerts | E-mail via Graph (a dedicated mailbox for Moss365) and/or a Teams channel |
| S6 | Four eyes | Optional per action (e.g. enabling Conditional Access, changing a privileged account): a second admin approves |

## 4. Steps

| Step | Contents | Done when |
| --- | --- | --- |
| **S1 Web host** | `Moss365.Server`: sign-in with Entra ID, roles from the token, the same screens; state split into shared (per tenant) and personal (per user) | The screens work in the browser with SSO, with the permissions per role |
| **S2 Connections from the server** | Reading with the server's identity; writing on behalf of the user (on-behalf-of for Graph and Exchange, S3 for AD), with a technical spike at the start | A Helpdesk member resets a password through the web app, and the AD and Entra audit shows their name |
| **S3 Background service** | Reading per tenant on a schedule, snapshots, alerts (S5), daily summary | A new admin in Entra raises an alert within the hour |
| **S4 Multiple tenants on the server** | Adding tenants (the Desktop wizard), role per tenant, Server administrator, isolation tested | A user of tenant A cannot see data of B through any URL or button |
| **S5 Security and installation** | Installation script for IIS, certificate, gMSA; encryption (S4); hardening; backup and restore test; a health page | An installation on a new server with the guide alone |
| **S6 Pilot** | An organisation uses the Server next to the Desktop | No blocking issues after a month |
| **Server 1.x** | Four eyes (S6), request forms for HR and department heads, an API for integrations | Based on the needs from the pilot |

## 5. Risks

| Risk | Approach |
| --- | --- |
| On-behalf-of or Kerberos delegation does not work as expected | Technical spike at the start of S2; fallback per connection |
| A central server with personal data of several organisations | One database per tenant, encrypted; roles per tenant; protect the server as a critical system; a DPIA per organisation |
| Blazor Server needs a stable connection | Allow websockets; reconnect automatically; keep form state |
| One server, no second one for availability | Deliberately simple; backup and restore test; the Desktop as fallback |
