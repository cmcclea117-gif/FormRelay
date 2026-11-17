# Installing, updating and checking FormRelay

All of it is one script: `FormRelay-Setup.ps1`. Run it from an **elevated Windows PowerShell** (not PowerShell 7; the script hops over by itself if you start it there).

## What an install does

| Step | Detail |
|---|---|
| Files | Copies the app to `C:\Program Files\FormRelay\releases\<version>` and points a `current` junction at it. The runtime is bundled. |
| Service | Windows service `FormRelay`, automatic (delayed) start, restarts itself after a crash. Runs as the virtual account `NT SERVICE\FormRelay` unless you pass `-ServiceAccount` (for example a gMSA like `CONTOSO\svc-formrelay$`). |
| Database | Creates the database if missing, a login for the service account, and gives it `db_datareader` + `db_datawriter` on that database only. Then applies the numbered migrations and records them in `dbo.SchemaVersion`. |
| Settings | Writes `C:\ProgramData\FormRelay\config.json` (readable by administrators, SYSTEM and the service account only). Never overwritten by an update. |
| Network | Kestrel listens on `-HttpPort` (default 8080). With `-CertThumbprint` it also serves HTTPS on `-HttpsPort` (default 443) and HTTP moves to 80. A Windows Firewall rule is added. |
| Verify | Waits for `GET /api/health`, then runs the same checks as `check`. |

```powershell
.\FormRelay-Setup.ps1 install -SqlServer "SQL01" -Database FormRelay -AdminAccount "CONTOSO\jsmith" `
    -SmtpHost smtp.contoso.com -SmtpFrom formrelay@contoso.com -CertThumbprint 1A2B... -WithSamples
```

Anything you leave out is asked for interactively. For unattended installs pass `-NonInteractive` and either all parameters or `-ConfigFile answers.json` (same names as the parameters).

Notes:

- **SQL Server on another machine:** the virtual service account then connects as `DOMAIN\MACHINE$`, and the installer creates that login. The account running the installer needs permission to create databases and logins there.
- **HTTPS:** use a certificate from your CA whose name covers the host users type. `-CreateSelfSignedCert` makes a test one (browsers will warn). The installer grants the service account read access to the private key.
- **Windows sign-in over Kestrel:** works with Kerberos or NTLM. If users reach the site through a DNS alias, register an SPN for it on the service account or they fall back to NTLM.
- **First administrator:** the account you give becomes admin when it first signs in (`Auth:BootstrapAdmins`). After real admins exist, delete that key from `config.json`.

## Updating

```powershell
& "C:\Program Files\FormRelay\FormRelay-Setup.ps1" update -Latest          # newest GitHub release, checksum verified
& "C:\Program Files\FormRelay\FormRelay-Setup.ps1" update -PackagePath C:\temp\FormRelay-0.3.0-win-x64.zip   # offline
```

What happens: version check (refuses a downgrade without `-Force`) → copy-only database backup (best effort; `-SkipDatabaseBackup` to skip) → config backup → stop service → new files → migrations → start → health check. If any step after stopping fails, the previous files are switched back in and started, and the script says so. The last three releases stay on disk; `rollback` flips back by hand.

Migrations are forward-only and written to be backward compatible, so rolling the files back is safe. If you ever need the old *data*, restore the `.bak` named in the output.

For private repositories pass `-GitHubToken <PAT with repo read>`. Updates are never automatic: someone runs the command (or a scheduled task you create) on purpose.

## Check mode

`FormRelay-Setup.ps1 check` changes nothing. It prints one line per check and exits `0` (all ok), `1` (warnings) or `2` (failures), so it also works in monitoring.

| Check | Catches |
|---|---|
| Install / files / service | Missing or stopped service, disabled start type, broken `current` junction |
| Settings file | Missing or invalid `config.json` |
| Connection string | The app silently falling back to `.\SQLEXPRESS` |
| Database (as installer) | Wrong server, no permission, schema older than the package |
| Port / firewall | Another program on the port, no inbound rule |
| Health endpoint | The app answers and can reach the database |
| `app:` lines | Run inside the app when you are an admin: schema version, admins exist, **email outbox stuck or failing**, SMTP reachable or **Graph certificate expiry** and private-key access |
| HTTPS certificate | Expired or soon-to-expire certificate |
| Environment variable | `ASPNETCORE_ENVIRONMENT` set machine-wide |

Admins can also call `GET /api/diagnostics`.

## Microsoft Graph email (optional)

SMTP needs nothing outside the machine. For Graph (app-only `Mail.Send`):

1. `.\FormRelay-Setup.ps1 graph-cert` creates a non-exportable certificate in `LocalMachine\My`, lets the service read its key, exports the public `.cer`, and prints the thumbprint and expiry.
2. Send the `.cer` to your Entra/Exchange admin with [admin-request-graph-mailer.md](admin-request-graph-mailer.md).
3. Put `Email:Provider = Graph` and the `Graph:*` values in `config.json`, then `Restart-Service FormRelay`.

An expired certificate stops mail silently; `check` warns 30 days ahead.

## Removing

`FormRelay-Setup.ps1 uninstall` removes the service, firewall rule and program files. Settings and the database stay unless you delete them (`-RemoveData` removes the settings folder only).
