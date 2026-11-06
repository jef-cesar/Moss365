# Moss365 - notes for contributors and coding agents

**Moss365**: a Windows management app for Active Directory, Entra ID and Exchange Online (Microsoft 365), open source
under the EUPL-1.2. Public repository: never add personal or organisation data (see `CONTRIBUTING.md`; the privacy
check in `.github/workflows/privacy.yml` refuses e-mail addresses outside the example domains, public IPs and a secret
blocklist). The maintainer does not write code: changes must be tested, documented and built by GitHub Actions.

Plan: [docs/roadmap-1.0.md](docs/roadmap-1.0.md). Current state: R1 to R5 are done (0.14.0): code in English, one
database per tenant, several tenants, snapshots, the set-up wizard. The interface texts are still Dutch and move to dictionaries in R15; the inventory
keeps the (Dutch) CSV file names of `tools/Export-Inventaris.ps1` (they are also the table names of the snapshots), and the script
header keys (`<#MOSS365 Naam: ... #>`) are renamed in R8. Data saved before 0.11 uses Dutch
property and enum names: `Storage/LegacyNames.cs` reads them (use `WithLegacyNames()` on every `JsonSerializerOptions`
that reads saved data, and `LegacyNames.TryParseEnum` for enum names stored as text).

## Layout

- `src/Moss365.Core` (net8.0): all logic, no UI, no Windows dependencies, fully tested.
- `src/Moss365.Connections` (net8.0): AD via LDAP, Graph via HttpClient + MSAL, Exchange via a separate PowerShell
  process with the embedded `Exchange/Exchange.ps1` (fixed commands, ASCII only), scripts via `Scripts/Startup.ps1`.
- `src/Moss365.UI` (Razor class library): the screens; logic in screens limited to presentation. `TenantHub` holds the
  tenants; every tenant has its own `ManagementState` (database, connections, sign-in). Screens get the open tenant's
  state (scoped); switching reloads the page.
- `src/Moss365.Desktop` (net8.0-windows, WPF + BlazorWebView): the window and dependency injection only.
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
- Conditional Access: `CaSafety` checks every write in the planner and in the engine; never delete a policy.
- Scheduled actions never run by themselves. Offboarding never deletes accounts.
- New `ChangeAction` values are appended at the end (stored as numbers in older data, as names in the change log).
- Database schema changes: a migration appended to `Database.Migrations`; never change an existing one.
- PowerShell embedded in the app and in `scripts/` is ASCII only.
- One branch: `main`.
