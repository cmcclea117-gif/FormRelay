# Contributing to FormRelay

Thanks for wanting to help. FormRelay is meant to be a form app anyone can run and improve, so bug reports, fixes, docs, and new connector kinds are all welcome.

By contributing you agree that your contribution is licensed under the [Apache License 2.0](LICENSE), the same as the project (section 5 of the license).

## Before you start

- **Bugs and ideas:** open an issue first for anything bigger than a small fix, so we can agree on the direction before you spend time on it.
- **Security problems:** don't open an issue. See [SECURITY.md](SECURITY.md).
- **Scope:** FormRelay is on-premises first (Windows service, SQL Server, Windows sign-in). Cloud hosting is a possible future direction but isn't the focus now. See [docs/roadmap.md](docs/roadmap.md).

## Set up

You need the .NET 10 SDK, Node 22, and a SQL Server (Express is fine). The steps are in the README under [Develop](README.md#develop).

```powershell
dotnet test                         # API and connector tests
cd web; npm ci; npm run lint; npm run build
```

## Making a change

1. Fork the repo, then branch from `main`.
2. Keep the change focused. One concern per pull request is easier to review and to roll back.
3. Add or update tests when you change behavior (`tests/FormRelay.Tests`).
4. Update the docs if users would notice (`README.md`, `docs/`).
5. Open a pull request against `main`. `main` is protected: the four CI checks (`web`, `api`, `installer`, `migrations`) must pass and changes go in through a pull request. A maintainer has to approve the first workflow run from a new contributor.

### Database changes

Migrations are numbered files in `db/` (`NNN_name.sql`). They must be:

- **Idempotent:** safe to run twice. CI runs every migration two times against SQL Server.
- **Backward compatible** with the previous app version. A failed update rolls the *files* back, not the database, so add columns and tables; don't drop or rename in the same release that stops using them.
- **Next in sequence:** use the next number and never edit one that has shipped.

### Installer changes (`installer/FormRelay-Setup.ps1`)

It has to run in Windows PowerShell 5.1. CI parses it and runs PSScriptAnalyzer and `check` mode. If you change install or update behavior, say in the pull request how you tested it (a throwaway VM is ideal), because CI can't fully exercise a real install.

### Connectors

A new connector kind should implement only the fetch (rows of value and label) in `src/FormRelay.Api/Connectors/ConnectorService.cs` and reuse the validation, scheduling, status and list replacement already there. Secrets go under `Connectors:Secrets` in `config.json`, never in the database. Add tests.

## Style

Follow what is already there: the code is plain, with comments that explain *why*. Don't reformat files you aren't otherwise changing. For commits, a short imperative summary line ("Fix outbox retry delay") and a sentence or two of why when it isn't obvious.

## Reviews

Reviews are by one maintainer in spare time, so a pull request may wait a few days. A small, tested, well-explained change moves fastest.

## Be kind

Be respectful and assume good faith. Harassment and personal attacks aren't tolerated; maintainers may remove comments or block accounts that do it.
