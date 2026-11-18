# FormRelay

**An all-in-one for collecting form submissions and getting them where they need to go, on your own server.**

> ### Why FormRelay?
> FormRelay is meant to be an all-in-one for collecting form submissions and getting them where they need to go. Build a form, share it with your staff or with the public, and every submission is stored, emailed to the people who need it, and tracked through to done. Admins can see what came in, who has it, and whether the email actually went out. Picklists can pull their choices from your other systems, so people choose instead of typing.
>
> It's built for organizations that want to run this themselves: one Windows server, SQL Server, no per-user fees, and no cloud account required. Your forms and your data stay on your own machines. It's open source (Apache-2.0), and it's free to use, change and share.

<p>
<a href="LICENSE"><img alt="License: Apache-2.0" src="https://img.shields.io/badge/license-Apache--2.0-blue.svg"></a>
<a href="https://github.com/cmcclea117-gif/FormRelay/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/cmcclea117-gif/FormRelay/actions/workflows/ci.yml/badge.svg"></a>
<a href="https://github.com/cmcclea117-gif/FormRelay/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/cmcclea117-gif/FormRelay"></a>
</p>

**Build** forms in a drag-and-drop builder, **share** them internally (Windows sign-in) or publicly, **relay** every submission by email, **track** it to done, and **monitor** the whole thing with a built-in self-check.

A form is a versioned JSON definition; submissions are stored in SQL Server, emailed to a mailbox, tracked by admins, and available through a read API. Picklists can be filled from other systems with **connectors** (SQL, REST/JSON, CSV).

It runs as a single **Windows service** (ASP.NET Core on Kestrel) with a React UI, and installs, updates and checks itself with one PowerShell script.

## Install in five minutes

**You need** a Windows computer (Windows 10/11 or Server 2019+) and **SQL Server**. The free **Express** edition is plenty: [download SQL Server Express](https://www.microsoft.com/en-us/sql-server/sql-server-downloads) (choose *Express*, then the **Basic** install, and keep the default instance name `SQLEXPRESS`). No .NET runtime or IIS is needed; FormRelay carries its own.

1. Download `FormRelay-<version>-win-x64.zip` from [Releases](../../releases/latest).
2. **Right-click the zip → Extract All.** (Windows marks downloaded files as untrusted. The launcher below clears that for you. If you ever run the `.ps1` directly and Windows complains, right-click the zip → Properties → **Unblock** *before* extracting, or run `Get-ChildItem -Recurse | Unblock-File` in the extracted folder.)
3. **Double-click `FormRelay-Setup.cmd`** and choose **1. Install**. It explains why it needs administrator permission, then prints exactly what it will change and asks before doing anything (details: [docs/security.md](docs/security.md)). By default it is reachable only from your own local network. Press Enter to accept the defaults (SQL Server `.\SQLEXPRESS`, you as the first administrator); it will ask how to send email and you can skip that for now.
4. Open the address it prints and sign in with Windows. Done.

Later: `"C:\Program Files\FormRelay\FormRelay-Setup.cmd"` and choose **2. Update**. Something wrong? Choose **3. Check**. Email: `FormRelay-Setup.ps1 test-email -To you@example.com`. All the details, command-line options and troubleshooting are in [docs/install.md](docs/install.md).

## What is in the box

| | |
|---|---|
| **Form builder** | Drag-and-drop fields, dropdowns from lists, number limits, "show only when" rules, versioned saves, internal or public audience, per-form owners. |
| **Email** | Submissions queue in an outbox and a background sender delivers them (up to 5 tries). **SMTP** by default; **Microsoft Graph** (app-only, certificate) as an option. |
| **Connectors** | Pull picklist choices from a SQL query, a REST/JSON endpoint or a CSV file, on a schedule or on demand. See [docs/connectors.md](docs/connectors.md). |
| **Sign-in** | Windows (Negotiate). Users and roles live in the database. Public forms need no sign-in and are rate limited with a honeypot. |
| **Self-check** | `FormRelay-Setup.ps1 check` and `GET /api/diagnostics` report what is wrong without changing anything. |
| **Updates** | One command: backup, swap, migrate, health check, automatic rollback if the new version does not come up. |

## Layout

```
src/FormRelay.Api/    ASP.NET Core minimal API (Dapper + SQL Server); serves the built UI from wwwroot
  Connectors/         connector fetch + scheduler
  Email/              SMTP and Graph senders, the outbox worker
  SelfCheck.cs        the read-only diagnostics
web/                  React + TypeScript (Vite)
db/                   numbered, idempotent migrations (001..); db/samples = optional sample forms
installer/            FormRelay-Setup.ps1: install | update | check | rollback | uninstall | graph-cert
build/package.ps1     builds the release zip (also used by CI)
tests/                xUnit tests
.github/workflows/    ci.yml, release.yml, installer-smoke.yml
docs/                 install, connectors, Graph email request template, troubleshooting
```

## Develop

```powershell
# SQL Server running locally, then once:
sqlcmd -S ".\SQLEXPRESS" -E -C -Q "IF DB_ID('FormRelay') IS NULL CREATE DATABASE FormRelay"
foreach ($f in Get-ChildItem db\0*.sql) { sqlcmd -S ".\SQLEXPRESS" -E -C -d FormRelay -b -i $f.FullName }
sqlcmd -S ".\SQLEXPRESS" -E -C -d FormRelay -b -i db\samples\100_sample_forms.sql   # optional

cd web; npm ci; npm run build; cd ..     # builds the UI into src\FormRelay.Api\wwwroot
dotnet run --project src\FormRelay.Api   # http://localhost:5105 (Development: first sign-in becomes admin)
```

`npm run dev` in `web/` runs the UI with hot reload and proxies `/api` to the running API. Run `dotnet test` and `npm run lint` before pushing.

Settings are normal ASP.NET configuration keys. On an installed server they live in `C:\ProgramData\FormRelay\config.json` (outside the program folder, so updates never touch them); in development use `dotnet user-secrets` or `appsettings.Development.json`. Do **not** set `ASPNETCORE_ENVIRONMENT` on a server.

### Releasing

Merge to `main` (CI must pass), then tag: `git tag v0.2.0 && git push origin v0.2.0`. The release workflow tests, builds the self-contained zip with a SHA-256 file and publishes a GitHub Release; `update -Latest` picks it up. A tag with a dash (`v0.2.0-beta.1`) is a pre-release and is not offered as "latest".

### Migrations

Add `db/NNN_name.sql` (next number), idempotent, safe to re-run, and **backward compatible** with the previous app version: an update that fails after migrating rolls the *files* back, not the database. Add columns and tables; do not drop or rename in the same release that stops using them.

## Status

Skeleton complete: app, installer, diagnostics, connector backend, CI/CD. Next: connector setup screens in the UI, a diagnostics page in the UI, an "update available" banner. See [docs/roadmap.md](docs/roadmap.md).

## License

[Apache-2.0](LICENSE). Use it, change it, share it. Contributions are welcome and are accepted under the same license.
