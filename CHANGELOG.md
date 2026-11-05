# Changelog

Every version becomes a release on GitHub; the section under the version is the text of that release.

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
