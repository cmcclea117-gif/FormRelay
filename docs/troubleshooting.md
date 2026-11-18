# Troubleshooting

Start with `FormRelay-Setup.ps1 check`. Most real failures in the app this was built from were one missing setting or permission, and `check` is built to find exactly those.

| Symptom | Likely cause | Fix |
|---|---|---|
| `/api/health` returns 500, SQL "error 26" in the log | `ConnectionStrings:Default` missing from `config.json`, so the app used its built-in default | Fix `config.json`, `Restart-Service FormRelay` |
| Double-clicking `FormRelay-Setup.cmd` flashes a window and nothing happens | Launcher bugs in early releases: 0.1.1 could not ask Windows for administrator rights when double-clicked, and the 0.1.2 launcher has a syntax error ("`. was unexpected at this time`"). Both are fixed in 0.1.3 | Use 0.1.3 or later. On 0.1.2 or earlier, open **Windows PowerShell as administrator**, `cd` into the extracted folder, and run `.\FormRelay-Setup.ps1 install` (or `update`); the PowerShell script itself is not affected. |
| `update` says "Cannot overwrite the item ... FormRelay-Setup.ps1 with itself" and rolls back | You are updating *from* 0.1.0 or 0.1.1, whose installer has this bug | Extract the new release zip and run **its** `FormRelay-Setup.cmd` → **2. Update** once. See [install.md](install.md#updating). |
| Service won't start | Port taken, bad `config.json` JSON, or the service account can't read the folder | Event Viewer > Windows Logs > Application, source `FormRelay` or `.NET Runtime` |
| Sign-in prompt loops / 401 | Browser doesn't consider the site "intranet", or host reached through an alias without an SPN | Add the site to the Local intranet zone; register the SPN |
| Users get "no access" | They are not in the app's user list | An admin adds them on the Users page |
| Nobody is admin | `Auth:BootstrapAdmins` empty and no admin row | Add the account to `Auth:BootstrapAdmins`, restart, sign in |
| Form submitted but no email | The form has no notify address (not an error), or the outbox is failing | `check` shows outbox pending/failed; failed rows hold `LastError` in `dbo.Notifications` |
| Mail worked, now silently stopped | Graph certificate expired, or SMTP credentials changed | `check` shows expiry and SMTP reachability |
| "can't read the private key" | Service account lacks read access to the certificate's key | Re-run `graph-cert` (or `icacls` the key file for the service account) |
| HTTPS fails to start | `-CertThumbprint` certificate missing from `LocalMachine\My` or key unreadable | `check` HTTPS line; reinstall the certificate, re-run install with `-Force` to rewrite settings |
| Update printed "rolled back" | New version didn't pass the health check | Read the error above it, `check`, then fix and re-run `update` |
| Connector shows `failed` | See `lastMessage` on `GET /api/connectors` | Usually a missing `Connectors:Secrets` key or a wrong column name |

Logs: Windows Event Viewer > Application. The ASP.NET Core event log logger writes Warning and above by default. To see more, add `"Logging": { "LogLevel": { "Default": "Information" } }` to `config.json`. Installer transcripts are in `C:\ProgramData\FormRelay\logs`.
