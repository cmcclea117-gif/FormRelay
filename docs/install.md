# Installing, updating and checking FormRelay

All of it is one script, `FormRelay-Setup.ps1`, plus a double-click launcher, `FormRelay-Setup.cmd`, for people who would rather not open PowerShell.

## Before you start

| You need | Notes |
|---|---|
| Windows 10/11 or Windows Server 2019+ | Anything that can run SQL Server 2022/2025 Express. |
| **SQL Server** | The free **Express** edition is enough for small and medium use (10 GB per database on 2022, 50 GB on 2025). [Download SQL Server Express](https://www.microsoft.com/en-us/sql-server/sql-server-downloads) (always the current release); choose *Express*, then **Basic**. Basic keeps the instance name `SQLEXPRESS`, so the installer's default `.\SQLEXPRESS` just works. SQL Server Management Studio is optional. |
| An administrator account | To install the service and create the database. Basic install makes the local Administrators group a SQL sysadmin. |
| (Optional) a way to send email | See [Email](#email-smtp) below. You can skip it and add it later. |

## Starting the installer

**Easiest: the launcher.** Extract the zip (right-click → Extract All), double-click **`FormRelay-Setup.cmd`**, and pick Install, Update or Check from the menu. It asks Windows for administrator rights, runs the script without the PowerShell execution-policy block, and clears the "downloaded from the internet" mark from the extracted files. You can also pass a mode to skip the menu: `FormRelay-Setup.cmd update -Latest`.

**From PowerShell.** Open **Windows PowerShell as administrator** (Windows PowerShell 5.1 is built into Windows; if you start PowerShell 7 the script switches to 5.1 by itself). Right-click → "Run with PowerShell" on the `.ps1` will *not* work: it isn't elevated, it passes no options, and downloaded scripts are blocked by the default execution policy. In the extracted folder:

```powershell
Set-ExecutionPolicy -Scope Process Bypass      # this window only
Get-ChildItem -Recurse | Unblock-File          # clears the "downloaded" mark
.\FormRelay-Setup.ps1 install
```

If Windows says the script "is not digitally signed" or "cannot be loaded", it is one of those two lines. (Right-click the zip → Properties → **Unblock** *before* extracting does the same job.)

## What an install does

| Step | Detail |
|---|---|
| Files | Copies the app to `C:\Program Files\FormRelay\releases\<version>` and points a `current` junction at it. The runtime is bundled. |
| Service | Windows service `FormRelay`, automatic (delayed) start, restarts itself after a crash. Runs as the virtual account `NT SERVICE\FormRelay` unless you pass `-ServiceAccount` (for example a gMSA like `CONTOSO\svc-formrelay$`). |
| Database | Creates the database if missing, a login for the service account, and gives it `db_datareader` + `db_datawriter` on that database only. Then applies the numbered migrations and records them in `dbo.SchemaVersion`. |
| Settings | Writes `C:\ProgramData\FormRelay\config.json` (readable by administrators, SYSTEM and the service account only). Never overwritten by an update. |
| Network | Kestrel listens on `-HttpPort` (default 8080). With `-CertThumbprint` it also serves HTTPS on `-HttpsPort` (default 443) and HTTP moves to 80. A Windows Firewall rule is added that allows **only your local subnet on Domain and Private networks** (never Public networks, never the internet). Narrow or widen it with `-LocalOnly`, `-AllowFrom`, `-AllowAnyNetwork`: see [security.md](security.md). |
| Verify | Waits for `GET /api/health`, then runs the same checks as `check`. |

Before it changes anything the installer **prints this plan and asks "Continue? [y/N]"** (Enter cancels; `-Yes` skips the question). See [security.md](security.md) for exactly what it changes and why it needs administrator rights.

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

## Email (SMTP)

FormRelay sends notifications over SMTP. The installer asks how, and fills in the server details for the common cases. You can skip it and add email later by editing the `Smtp` section of `C:\ProgramData\FormRelay\config.json` and running `Restart-Service FormRelay`.

| Choice | Server filled in | What you need |
|---|---|---|
| **Skip** | none | Nothing. Forms work and submissions are saved; notification emails wait in the outbox until a server is set. |
| **Microsoft 365** | `smtp.office365.com`, port 587, TLS | A mailbox with *Authenticated SMTP* enabled. Microsoft is phasing out password-based SMTP in some tenants; if it's refused, use the Graph option below. |
| **Gmail / Google Workspace** | `smtp.gmail.com`, port 587, TLS | 2-step verification on the account and an [app password](https://myaccount.google.com/apppasswords) (not the normal password). |
| **Your own mail server** | you enter it | Host, port, whether it uses TLS (port 587 usually yes, 25 usually no), and a user name and password if it needs them. |

Non-interactive: `-SmtpPreset none|microsoft365|gmail|custom`, plus `-SmtpHost`, `-SmtpPort`, `-SmtpUseTls`, `-SmtpUser`, `-SmtpPassword`, `-SmtpFrom`.

**Always test it:**

```powershell
.\FormRelay-Setup.ps1 test-email -To you@example.com
```

It sends one message using the settings in `config.json` and, if it fails, says what to look at (wrong password, cannot reach the server, TLS mismatch). The mail password lives in `config.json`, which only administrators, SYSTEM and the service account can read.

To try FormRelay with no real mail account, run a throwaway local mail catcher such as [smtp4dev](https://github.com/rnwood/smtp4dev) and point `Smtp:Host` at `localhost`.

## Updating

```powershell
& "C:\Program Files\FormRelay\FormRelay-Setup.ps1" update -Latest          # newest GitHub release, checksum verified
& "C:\Program Files\FormRelay\FormRelay-Setup.ps1" update -PackagePath C:\temp\FormRelay-0.3.0-win-x64.zip   # offline
```

**Updating from 0.1.0 or 0.1.1:** those versions have a bug that makes `update` fail (and roll back, harmlessly) with *"Cannot overwrite the item ... FormRelay-Setup.ps1 with itself"*. The update runs the *installed* installer, which can't be fixed from outside. Do the first update from the new release instead: download and extract the new `FormRelay-<version>-win-x64.zip`, double-click **its** `FormRelay-Setup.cmd`, and choose **2. Update**. That update installs the fixed installer, so every update after it works the normal way.

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
