# Contributing to Moss365

Thank you for helping. Moss365 manages identities and access of real organisations, so the rules below are strict.

## The rules every change keeps

1. **Every write is a plan**: build a plan, show it as a preview, ask for confirmation, execute, write the change log.
   Never write to AD, Entra or Exchange from a screen directly.
2. **No secrets**: never store or log passwords, tokens or keys. The technical log contains what happened, counts and
   errors only; never names, parameters or secrets (a test checks this).
3. **The app can do no more than the signed-in account**: delegated permissions only.
4. **Roles are enforced in three places**: page, button and engine.
5. **Scheduled actions never run by themselves.**
6. **Conditional Access**: an enabled policy must exclude the emergency accounts; the engine checks this.
7. **Offboarding never deletes accounts.**
8. **No personal data** in this repository: test data is made up; use `contoso.com`, `fabrikam.com`, `example.org`
   and the documentation IP ranges (`192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24`).
9. **Tests**: every change comes with tests (xUnit for the core, bUnit for screens, Pester for PowerShell).
10. **Database schema**: only by adding a migration at the end; never change an existing one.

## How to contribute

- Open an issue first for anything larger than a small fix, so we can agree on the approach.
- Fork, create a branch, make your change with tests, and open a pull request.
- Run `dotnet test` before you push.
- Code, comments, tests and documentation are in English. Interface texts go into the dictionaries (one per language).

## Contributor Licence Agreement

Moss365 is licensed under the [EUPL-1.2](LICENSE). To keep the option of offering paid support, hosting or a commercial
licence next to the open version, outside contributions are accepted under the [Contributor Licence Agreement](CLA.md).
By ticking the CLA box in the pull request template you confirm that you agree to it. Without that, we cannot merge
your contribution.

## Security issues

Do not open a public issue for a vulnerability; see [SECURITY.md](SECURITY.md).
