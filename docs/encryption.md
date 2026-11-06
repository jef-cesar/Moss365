# Encryption of a tenant database

Since 0.16 (step R6 of the [roadmap](roadmap-1.0.md)) the database of a tenant can be encrypted. It is **off by
default** (decision U7) and is turned on per tenant under **Instellingen > Databank > Versleuteling**.

## How it works

- The database is encrypted with **SQLCipher** (AES-256, open source, BSD licence) through
  `SQLitePCLRaw.bundle_e_sqlcipher`. The same build opens databases that are not encrypted.
- Every tenant gets its own **random 256-bit key**. The key is never stored in the database, the repository or a log.
- `moss365.db` keeps, per tenant, only the key **protected** by one of the methods below (setting `encryption.<id>`).
- The **recovery key** is the key itself with two check bytes, written in groups of five characters (an alphabet
  without 0, O, 1 and I). It opens the database on its own: on another PC, after a reinstall, or when `moss365.db` is
  lost. The app shows it once, asks you to type its last group, and keeps it nowhere.
- Turning encryption on or off, or renewing the key, makes a **copy** with the new key (`sqlcipher_export`), checks it
  (integrity, row counts, schema version) and only then replaces the database. The automatic copies made before a
  database update are removed at that moment, because they still have the old protection.
- A **backup** (Tenants > Back-up) of an encrypted tenant is encrypted with the same key. Restoring it asks for the
  recovery key; the restored tenant is then protected with this Windows account (or a new password).

| Method | Protection of the key | Asked when opening | Lost when |
| --- | --- | --- | --- |
| **A. Windows account** | DPAPI, current user (`ProtectedData`, scope CurrentUser) | nothing | another Windows user, or the Windows profile is reset |
| **B. Password** | AES-256-GCM with a key from PBKDF2-SHA256, 600,000 iterations, random salt | the password, at every start | the password is forgotten |
| **C. AD group** | DPAPI-NG for `SID=<group>` (`NCryptProtectSecret`) | nothing, for members on a domain PC | no longer a member of the group |

In every case the recovery key still opens the database.

## Study D: the Microsoft 365 sign-in (Azure Key Vault)

The roadmap asked whether the database could open with the Microsoft 365 sign-in of the administrator. Conclusions:

- **A token is not a key.** An access token changes at every sign-in and expires within the hour; nothing derived from
  it can open the same database tomorrow.
- **The key needs a keeper that checks the identity.** Azure Key Vault can *wrap* and *unwrap* the database key with a
  key that never leaves the vault (`wrapKey`/`unwrapKey`), only for whoever has the role *Key Vault Crypto User*. That
  follows the tenant: MFA, Conditional Access, and whoever leaves the group can no longer open the database.
- **Without Azure there is no such keeper in Microsoft 365.** A file in OneDrive or SharePoint is readable by everyone
  with access to it, so it cannot hold a key. Without an Azure subscription only A, B and C remain.
- **Offline**: an unwrapped key could stay in memory while the app is open, but not on disk; starting without a network
  then needs the recovery key.
- **Cost and setup**: a vault and one RSA key cost a few cents a month; the wizard (R5) could create them, but that needs
  an Azure subscription and the role to create resources, which many Microsoft 365 organisations do not have.
- **Server (W1)**: the same approach works for a service account (managed identity), or C with the service account.

**Decision**: D is technically sound and can be added later as a fourth method, with the recovery key as a fallback.
It is not part of 1.0: A, B and C cover the cases without an Azure subscription, and D only adds value where one
exists. It stays on the list for after 1.0.

## What it does not protect against

- Someone who is signed in as you (A), knows the password (B) or is in the group (C).
- Memory of a running app, and screenshots or exports (CSV files in the export folder are not encrypted).
- Backups made before encryption was turned on: they stay as they were. Remove them if needed.
- The technical log never contains keys, passwords or the recovery key (a test checks the log for names and secrets).
