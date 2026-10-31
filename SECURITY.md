# Security Policy

## Supported versions

Security fixes are applied to the latest source on the default branch.

## Reporting a vulnerability

Please do **not** open a public issue that includes exploit details, passwords, or private file paths.

Preferred options:

1. **GitHub Security Advisories** (private disclosure) — if enabled for this repository, use *Security* → *Report a vulnerability*.
2. **GitHub Issues** — for lower-sensitivity reports, open an issue titled something like `[Security] short summary`, and omit secrets / exploit PoCs. Maintainers may ask you to move details to a private channel.

Include when possible:

- Affected version / commit
- OS and .NET runtime version
- Steps to reproduce (without secrets)
- Impact assessment

We will acknowledge reports as soon as practical and coordinate a fix before any public write-up.

## Scope notes

This app shells out to the system `7z.exe`. Issues in 7-Zip itself should be reported upstream: https://www.7-zip.org/
