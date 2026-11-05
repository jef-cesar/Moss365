# Moss365 - notes for contributors and coding agents

**Moss365**: a Windows management app for Active Directory, Entra ID and Exchange Online (Microsoft 365), open source
under the EUPL-1.2. Public repository: never add personal or organisation data (see `CONTRIBUTING.md`; the privacy
check in `.github/workflows/privacy.yml` refuses e-mail addresses outside the example domains, public IPs and a secret
blocklist). The maintainer does not write code: changes must be tested, documented and built by GitHub Actions.

Plan: [docs/roadmap-1.0.md](docs/roadmap-1.0.md). Current state: step R1 part A is done (0.10.0); the code, comments
and tests are still Dutch and move to English in R1 part B (0.11). Write new code in English from now on; keep
identifiers consistent within a file until it is translated.

## Layout

- `src/Moss365.Core` (net8.0): all logic, no UI, no Windows dependencies, fully tested.
- `src/Moss365.Connections` (net8.0): AD via LDAP, Graph via HttpClient + MSAL, Exchange via a separate PowerShell
  process with the embedded `Exchange/Exchange.ps1` (fixed commands, ASCII only).
- `src/Moss365.UI` (Razor class library): the screens; logic in screens limited to presentation.
- `src/Moss365.Desktop` (net8.0-windows, WPF + BlazorWebView): the window and dependency injection only.
- `tests/Moss365.Tests`: xUnit for the core, bUnit for the screens; `tests/powershell` and `tools/tests`: Pester.
- `config/organisatie.json`: the sample organisation (Contoso, made up).
- `scripts/`: the script library (header `<#MOSS365 ... #>`), shipped in the download.
- `CHANGELOG.md`: a release is created when `Version` in `Directory.Build.props` has no release yet; the text comes
  from the `## <version>` section.

## Build and test (Linux works too)

```
dotnet test tests/Moss365.Tests
dotnet publish src/Moss365.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Rules (see also CONTRIBUTING.md)

- Every write is a plan: preview (`PlanWeergave`), confirmation, execution (`PlanUitvoerder`), change log. Undo via
  `Wijziging.Vorige` and `Terugdraaien`.
- Never store or log secrets. The technical log holds no names, parameters or secrets (a test checks this).
- Roles are enforced on the page (`Toegang.VoorPad`), the buttons (`State.Mag`) and in the engine (`weiger`).
- Conditional Access: `CaVeiligheid` checks every write in the planner and in the engine; never delete a policy.
- Scheduled actions never run by themselves. Offboarding never deletes accounts.
- New `Actie` values are appended at the end (stored as numbers in older data).
- Database schema changes: a migration appended to `Databank.Migraties`; never change an existing one.
- PowerShell embedded in the app and in `scripts/` is ASCII only.
- One branch: `main`.
