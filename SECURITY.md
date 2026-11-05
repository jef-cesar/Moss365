# Security policy

Moss365 manages identities and access, so security reports are welcome and handled with priority.

## Reporting a vulnerability

- **Do not open a public issue.**
- Use GitHub's private vulnerability reporting: *Security* tab, *Report a vulnerability*.
- Describe the problem, how to reproduce it and the impact. Do not include real personal data or tenant information.

You get a first answer within one week. Once a fix is released, the report is published as a security advisory, with
credit if you want it.

## Supported versions

Until 1.0, only the latest release is supported. From 1.0, the latest minor version of each supported major version.

## Design principles

- Delegated permissions only: the app can do no more than the signed-in account.
- No secrets on disk or in logs; no telemetry.
- Every write is a plan with preview, confirmation and change log; the engine enforces the roles and the Conditional
  Access safety checks.
- Releases come with SHA-256 fingerprints; from 1.0 they are signed (Authenticode).
