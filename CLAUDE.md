# Moss365 - notes for contributors and coding agents

**Moss365**: a Windows management app for Active Directory, Entra ID and Exchange Online (Microsoft 365), open source
under the EUPL-1.2. Public repository: never add personal or organisation data (see `CONTRIBUTING.md`; the privacy
check in `.github/workflows/privacy.yml` refuses e-mail addresses outside the example domains, public IPs and a secret
blocklist). The maintainer does not write code: changes must be tested, documented and built by GitHub Actions.
Motto: **data under your own control** ("data in eigen beheer"): the Desktop stays a full edition after 1.0 (U14); no
feature needs a server or a service of the makers.

Plan: [docs/roadmap-1.0.md](docs/roadmap-1.0.md). Current state: R1 to R14 are done (0.37.0); R15: every screen, Core and the connections take their text from the dictionaries since 0.38.5 (translations still to be reviewed by native speakers): code in English, one database
per tenant, several tenants, snapshots, the set-up wizard, maintenance, encryption, updates and the script library. The code targets .NET 10 (U22, in the CHANGELOG under 0.39.0: .NET 8 has no support since 10 November 2026; `bunit` stays 1.40 until a separate move to bUnit 2). Next, before 1.0 (decided 26 November 2026, U15): R16 security advice (explanation, solving as a plan, guided for high risk, exceptions, hardening score, compromised account), R17 devices and Intune (a destructive action: one device per plan, never a personal device; a device record is deleted only after 90 days disabled), R18 own data (sign-in and audit logs kept locally, reading without a window), R19 the overview (All tenants: only scores and counts, never stored together), R20 access reviews; then the native-speaker review of R15 and 1.0. Open work is in the
GitHub issues (label `roadmap` per step, `limitation`, `test`). The interface texts move to dictionaries (R15, [docs/translating.md](docs/translating.md)): new text in a screen goes through `T["key"]` (in Core and Connections `Texts.T["key"]`, never in a `static readonly` field or `const`) with the key in all five `Localization/Texts/*.json`, and `tests/Moss365.Tests/Localization/untranslated.txt` may only go down; the inventory
keeps the (Dutch) CSV file names of `tools/Export-Inventaris.ps1` (they are also the table names of the snapshots). Script headers are English since 0.18 (`Name:`, `Version:`, ...); the
Dutch keys of older scripts are still read. Data saved before 0.11 uses Dutch
property and enum names: `Storage/LegacyNames.cs` reads them (use `WithLegacyNames()` on every `JsonSerializerOptions`
that reads saved data, and `LegacyNames.TryParseEnum` for enum names stored as text).

## Layout

- `src/Moss365.Core` (net10.0): all logic, no UI, no Windows dependencies, fully tested.
- `src/Moss365.Connections` (net10.0): AD via LDAP, Graph via HttpClient + MSAL, Exchange via a separate PowerShell
  process with the embedded `Exchange/Exchange.ps1` (fixed commands, ASCII only), scripts via `Scripts/Startup.ps1`.
- `src/Moss365.UI` (Razor class library): the screens; logic in screens limited to presentation. `TenantHub` holds the
  tenants; every tenant has its own `ManagementState` (database, connections, sign-in). Screens get the open tenant's
  state (scoped); switching reloads the page.
- `src/Moss365.Desktop` (net10.0-windows, WPF + BlazorWebView): the window and dependency injection only.
- `tests/Moss365.Tests`: xUnit for the core, bUnit for the screens; `tests/powershell` and `tools/tests`: Pester.
- `config/organisatie.json`: the sample organisation (Contoso, made up).
- `scripts/`: the script library (header `<#MOSS365 ... #>`), shipped in the download.
- Storage: `%LOCALAPPDATA%\Moss365\moss365.db` (`AppDatabase`: the list of tenants) and
  `tenants\<id>\tenant.db` (`Database`): everything of a tenant, files included (`IFileStore`, table File). Never data
  of two tenants in one database, list, plan, export or log; never loose files next to a tenant database.
- Snapshots (`Storage/Snapshots.cs`, R4): every read becomes a snapshot (tables Snapshot, Object, Version); only
  changed rows are stored. `ManagementState.AsOf`/`IsHistoric` shows the past read only: `Can` refuses writes and
  `ExecuteAsync` throws, so every new write path must go through those.
- Set-up (`Core/Setup`, R5): `TenantSetup.Assess` compares what the wizard read with `AppManifest` and plans only the
  difference; `TenantSetupConnection` runs it with the Graph PowerShell client. Keep `AppManifest` and
  `tools/Register-App.ps1` the same (permissions, app role ids).
- Maintenance (`Storage/Maintenance.cs`): retention in `OrganisationConfig.Retention`; cleaning up is always preview,
  confirmation, archive (change log, access log), then remove. `RemoveSnapshots` keeps every remaining date exact
  (a test compares all of them).
- Encryption (`Storage/Encryption.cs`, `TenantHub.Encryption.cs`, [docs/encryption.md](docs/encryption.md)): a random
  key per tenant, opened with `Database(path, key)`; moss365.db keeps only the protected key. Never log a key, password
  or recovery key; `TenantHub.Open` throws `TenantLockedException` until the tenant is unlocked. A new tenant is encrypted
  with the Windows account by default (U24: `TenantHub.AddNew` with a key from `NewKey`, after the recovery key is
  confirmed on Tenants; `TryKey` accepts the key while the database does not exist yet).
- Updates (R7): Velopack, pack id `Moss365.Desktop` (never `Moss365`: Velopack removes its install folder on uninstall,
  and `%LOCALAPPDATA%\Moss365` holds the data). `Program.Main` runs `VelopackApp` first. `AppUpdates` refuses while
  anything runs or is open and copies every database first. The release workflow packs channel `stable`, or `preview`
  for a version with a suffix. The job `release` only runs for a new `Version` and waits for the maintainer's approval
  (U23: the GitHub environment `release` with a required reviewer); never work around it.
- Script library (R8, `Scripts/ScriptIndex.cs`): `scripts/index.json` must match `scripts/` (a test; regenerate with
  `MOSS365_WRITE_INDEX=1`, see CONTRIBUTING). Downloads are checked against the SHA-256 in the index; pins per tenant in
  `scripts/pins.json` of the tenant store. Never change the bytes of `scripts/` through Git (`.gitattributes`).
- Systems per tenant (0.19, `TenantInfo.Systems`: AD, Microsoft 365 or both): connections always use
  `ManagementState.EffectiveSettings`, never `Settings`, so that nothing touches a system the tenant does not have;
  screens check `State.HasAd` / `State.HasMicrosoft365`, the menu `Access.FitsTenant`. Without AD, `State.Data` leaves
  every AD object out (`InventoryData.WithoutAd`); background steps update the full data (`_data`), never `Data`.
- Organisation settings (R9): naming in `Configuration/Naming.cs` (`OrganisationConfig.Naming`, used by
  `PlanBuilder.NewUser`), suggestions in `Setup/OrganisationSuggestions.cs` (shown by `SuggestionsPanel` on Organisatie
  and in the wizard), templates in `Configuration/OrganisationTemplates.cs` (only add what is missing). Job profiles
  (0.22): `OrganisationConfig.JobProfiles` (groups and mailboxes by name; `JobProfiles.BumpVersions` on save),
  `Changes/JobProfilePlans.cs` (compare, apply, from a person, holders); who got which version: document
  `functieprofielen` (`ManagementState.ProfileAssignments`). "Profiles" in `ProfilesRequired` are the app roles; since 0.30
  `OrganisationConfig.Roles` (`RolesMode`: Off, Transition, Required; `RolesModeEffective`) decides, per tenant. New users
  in the cloud (0.31): `Changes/CloudUserPlans.cs` (`GraphCreateUser`, password only in `Secret`); `GraphConnection`
  remembers ids it just created or invited, so later steps of the same plan can use the UPN or address. Logon scripts
  (0.32): `LogonScripts/LogonScripts.cs` (analysis, explanation, diff, plans), `ILogonScriptShare` (real: `Ad/NetlogonShare.cs`),
  writing through `AdWriteLogonScript` in `AdLdap`; versions are the change log entries. Helpdesk (0.33):
  `Helpdesk/HelpdeskTools.cs`; a Temporary Access Pass comes back in `Change.Secret` (never logged); BitLocker, LAPS and
  message trace go through `ManagementState.Helpdesk.cs`, which writes the access log. "Stay signed in" keeps the MSAL
  cache in the tenant database, DPAPI-encrypted (`ProtectedTokenStore`), only when chosen. Security checks (0.34):
  `Security/SecurityChecks.cs` computes OK/attention/problem/unknown from readings (`IGraphConnection.ReadAppsAsync`, ...,
  `IAdConnection.ReadSecurityAsync`/`ReadGpoAsync`, `IExchangeConnection.ReadForwardingAsync`); run by `ManagementState.SecurityChecks.cs`. Licences (0.35): `Licences/LicenceAnalysis.cs`
  (waste, costs with `OrganisationConfig.LicencePrices`, against job profiles); `GraphRemoveLicences` takes optional `skuIds`.
  Devices, Teams and SharePoint (0.36): `Devices/DeviceAnalysis.cs` (stale computers, systems, Intune, groups without
  owner/with guests/unused, sites); reads `IAdConnection.ReadComputersAsync`, `IGraphConnection.ReadIntuneDevicesAsync`/
  `ReadCollaborationAsync` (usage reports from the beta endpoint, JSON); disabling computers is `AdDisableAccount`.
  Reporting (0.37, `Reporting/`): `PeriodReport` (from `ReadKpiSnapshot` and the change log; `ToHtml`/`ToCsv`, CSV never
  starts a cell with a formula), `DataQuality`, `Standards` (checks mapped to NIS2/CIS), `GuestRenewal` (document
  `gastenverlenging`), `StaffRequests` (document `aanvragen`; decisions are change log lines with action `Request`),
  `ServiceHealth` (`IGraphConnection.ReadServiceHealthAsync`), `TenantComparison` (only differences, nothing stored).
- Menu (0.21, `UI/Services/MenuModel.cs`): the main menu groups and the settings side bar; a new page is added there
  (and to `Access` for its role). Settings pages use `@layout SettingsLayout`; Ctrl+K searches `MenuModel.AllPages`.
- `CHANGELOG.md`: a release is created when `Version` in `Directory.Build.props` has no release yet; the text comes
  from the `## <version>` section.

## Build and test (Linux works too)

```
dotnet test tests/Moss365.Tests
dotnet publish src/Moss365.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Rules (see also CONTRIBUTING.md)

- Every write is a plan: preview (`PlanView`), confirmation, execution (`PlanExecutor`), change log. Undo via
  `Change.Previous` and `Undo`.
- Never store or log secrets. The technical log holds no names, parameters or secrets (a test checks this).
- Roles are enforced on the page (`Access.ForPath`), the buttons (`State.Can`) and in the engine (`refuse`).
- Conditional Access: `CaSafety` checks every write in the planner and in the engine; never delete a policy. The editor
  (`Analysis/CaEditor.cs`, 0.30) builds on the JSON as read and sends only the changed parts (`Patch`), with `Previous`.
- Scheduled actions never run by themselves. Offboarding never deletes accounts.
- New `ChangeAction` values are appended at the end (stored as numbers in older data, as names in the change log).
- Database schema changes: a migration appended to `Database.Migrations`; never change an existing one.
- PowerShell embedded in the app and in `scripts/` is ASCII only.
- One branch: `main`.
