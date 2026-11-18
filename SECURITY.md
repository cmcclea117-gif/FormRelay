# Security policy

FormRelay handles Windows sign-in, stores form submissions (which can contain personal information), and can connect to your databases. Security reports are taken seriously.

## Reporting a vulnerability

**Please don't open a public issue for a security problem.** Report it privately:

1. Go to the [Security tab](https://github.com/cmcclea117-gif/FormRelay/security) and choose **Report a vulnerability**
   (or open https://github.com/cmcclea117-gif/FormRelay/security/advisories/new).
2. Tell us what you found, the version (`FormRelay-Setup.ps1 check` prints it), and the steps to reproduce it. A proof of concept helps. Please don't include real personal data.

FormRelay is maintained by one person in their spare time, so these are targets, not promises: an acknowledgement within about 7 days, and an update on a fix or a decision within about 30 days. Reporters are credited in the release notes unless they ask not to be.

## Supported versions

Only the **latest release** gets security fixes. Update with `FormRelay-Setup.ps1 update -Latest`.

## What counts

Of particular interest:

- Getting past Windows sign-in, role checks (Admin, User) or the public/internal form split
- Reading or changing submissions, lists or users without permission
- Public form endpoints: injection, rate-limit or honeypot bypass, anything that exposes internal forms or lists
- The connector feature: running anything other than a read-only `SELECT`, or reaching resources it shouldn't (SQL, REST and file connectors are admin-only by design)
- Secrets showing up in logs, API responses or the audit log
- The installer and updater: unsafe file permissions, an unverified download, a way to swap in a different package

Not in scope: problems that need an already-compromised administrator account or server, missing hardening on a server you configured yourself (see below), denial of service by sheer volume, and findings from automated scanners with no demonstrated impact.

## Hardening your install

The installer sets safe defaults. These are the things worth checking yourself (`FormRelay-Setup.ps1 check` covers several):

- **Remove `Auth:BootstrapAdmins`** from `C:\ProgramData\FormRelay\config.json` once real admins exist.
- **Never set `ASPNETCORE_ENVIRONMENT` on a server.** Unset means Production; Development turns on shortcuts.
- **Use HTTPS** with a certificate from your CA. Windows sign-in over plain HTTP is fine only on a trusted network.
- **Keep `config.json` private.** It holds the database connection, SMTP password and connector secrets. The installer restricts it to administrators, SYSTEM and the service account; don't loosen that.
- **Use read-only logins for connectors.** A SQL connector accepts only `SELECT`/`WITH` queries, but that is a guard rail, not a boundary. Give it a login that can only read what it needs.
- **Give the service account the least it needs**: `db_datareader` and `db_datawriter` on the FormRelay database only (the installer does this).
- **Microsoft Graph email:** restrict the app to one mailbox with an Application Access Policy (see [docs/admin-request-graph-mailer.md](docs/admin-request-graph-mailer.md)), and watch the certificate expiry.
- **Mark only genuinely public lists as "approved for public forms."** Anything not approved can't appear on a public form.
- **Stay current.** Updates are never automatic; run `update -Latest` when a release lands.
