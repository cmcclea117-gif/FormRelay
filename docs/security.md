# Security and what the installer does to your computer

FormRelay is designed to be **internal-only until you decide otherwise**. This page says exactly what the installer changes, why it needs administrator rights, who can reach the app, and how to check all of it yourself.

## Why it asks for administrator rights

Installing a Windows service, opening a firewall port and creating a database login are all things Windows only lets an administrator do. Nothing about *using* FormRelay needs administrator rights afterwards.

Before it changes anything, the installer prints a plan and waits for you:

```
FormRelay 0.1.2 will make these changes on OFFICE-PC:
  - Copy the program to C:\Program Files\FormRelay
  - Create a Windows service 'FormRelay' that runs as NT SERVICE\FormRelay. That account has no administrator rights...
  - Create the database 'FormRelay' on .\SQLEXPRESS, and give NT SERVICE\FormRelay read and write access to that database only
  - Save settings (including any passwords) in C:\ProgramData\FormRelay, readable only by Administrators, SYSTEM and the service
  - Open Windows Firewall for TCP 8080: only LocalSubnet on Domain and Private networks (never Public networks)
  - It sends nothing anywhere...
Continue? [y/N]
```

Pressing Enter cancels. `update` and `uninstall` show a plan the same way. `-Yes` skips the question for unattended installs (the plan is still printed). `check` never needs administrator rights and changes nothing.

## What it changes, and what it does not

| It does | Detail |
|---|---|
| Copies program files | `C:\Program Files\FormRelay` |
| Creates one Windows service | Runs as a **virtual service account** (`NT SERVICE\FormRelay`): no password, no administrator rights, can only read its program and its settings |
| Creates a database login | For that service account only, `db_datareader` + `db_datawriter` on the FormRelay database only. It never gets rights on other databases or on the server |
| Writes `config.json` | `C:\ProgramData\FormRelay`, readable only by Administrators, SYSTEM and the service |
| Opens one firewall port | Scoped; see below |

| It does **not** | |
|---|---|
| Send data anywhere | No telemetry, no phone-home. It reaches the internet only if **you** run `update -Latest` (downloads from GitHub) or configure outgoing email or a connector |
| Change Windows security settings | No changes to Defender, UAC, execution policy (machine-wide), accounts or other firewall rules |
| Touch other databases or programs | |
| Open anything to the internet | See next section |

## Who can reach it (the defaults)

- The app listens on port **8080** (HTTP).
- The firewall rule allows **only your local subnet**, on **Domain and Private** networks. Not Public networks (café or guest Wi-Fi), and not the internet.
- It does not use UPnP and never changes your router, so it is **not reachable from the internet** unless somebody forwards a port to this computer or the computer has a public IP address.

Change the scope when you install:

```powershell
FormRelay-Setup.ps1 install -LocalOnly                          # this computer only, no firewall rule at all (listens on 127.0.0.1)
FormRelay-Setup.ps1 install -AllowFrom 10.0.0.0/8,192.168.0.0/16  # your internal ranges (several subnets)
FormRelay-Setup.ps1 install -AllowAnyNetwork                    # any address on any network: not recommended
```

If this computer's network is classed **Public**, other computers cannot connect until you change it to **Private** (Settings → Network & internet). The installer tells you when it notices.

**Plain HTTP:** the default is unencrypted HTTP, which is reasonable on a trusted internal network but means Windows sign-in and form contents cross the network unencrypted. Use HTTPS (`-CertThumbprint`, see [install.md](install.md)) for anything beyond a small trusted network.

## Check your own exposure

```powershell
FormRelay-Setup.ps1 check                         # firewall scope and encryption are in the report
Get-NetFirewallRule -DisplayName FormRelay | Get-NetFirewallAddressFilter   # who the rule allows
Get-NetConnectionProfile                          # is this network Public, Private or Domain?
```

An anonymous visitor who *can* reach the port sees only the sign-in prompt and a bare `{"status":"ok"}` on `/api/health`. Everything else needs a Windows sign-in as a user listed in the app, and the very first administrator can only be the one account you named at install.

Other measures in the app: security headers on every response (`nosniff`, no framing, no referrer leakage, `no-store` on the API), no `Server` header, generic error responses (never stack traces), rate limiting and a honeypot on public forms, public forms and lists only by explicit admin choice, an audit log, and a self-check that warns when the bootstrap admin is still set.

## Verify what you downloaded

1. **Checksum.** Each release has a `.sha256` file: `(Get-FileHash .\FormRelay-x.y.z-win-x64.zip).Hash` should match it. `update -Latest` checks it for you before installing.
2. **Build provenance** (releases from 0.1.2 on). The release workflow attests that the zip was built by this repository's GitHub Actions from a specific commit. With the [GitHub CLI](https://cli.github.com): `gh attestation verify FormRelay-x.y.z-win-x64.zip --repo cmcclea117-gif/FormRelay`.
3. **Read it.** Everything the installer does is in two plain-text files: `FormRelay-Setup.cmd` (about 50 lines) and `FormRelay-Setup.ps1`. The whole project is open source.
4. **SmartScreen.** The files are not yet code-signed, so Windows may show an "unknown publisher" warning. That is expected today; the steps above are how to be sure you have the real thing.

## Taking it out

`FormRelay-Setup.ps1 uninstall` shows what it will remove (the service, the firewall rule, the program files), keeps your settings and database unless you ask otherwise, and changes nothing else.

## If you want public forms reachable from the internet

You don't need to for an internal install, and I'd leave it for later. When you do:

- **Do not forward port 8080 to FormRelay.** The internal app (Windows sign-in, admin pages, submissions) and the public forms are one process. Putting it directly on the internet exposes all of it to attack.
- Put a **reverse proxy or tunnel** in front (for example Cloudflare Tunnel, IIS with URL Rewrite/ARR, nginx or Caddy) that forwards **only** the public paths and blocks everything else:
  `/f/*` (the form pages), `/api/public/*` (loading and submitting public forms), and the static files (`/assets/*`, `/index.html`, `/favicon.svg`, `/icons.svg`).
  The proxy gives you HTTPS, request limits and logging; the allow-list is what keeps the admin side private.
- Keep Windows sign-in internal. Don't pass Negotiate authentication through the proxy.
- Keep each public form's lists to ones you approved for public use, and consider a captcha at the proxy.

A separate, thin public host (so the internal app never faces the internet at all) is on the [roadmap](roadmap.md).

## Hardening checklist

- [ ] Install with the narrowest scope that works (`-LocalOnly`, or `-AllowFrom` your ranges).
- [ ] Use HTTPS if more than a handful of trusted people connect.
- [ ] After the first admin has signed in, remove `Auth:BootstrapAdmins` from `config.json` (`check` reminds you).
- [ ] Give SQL connectors a **read-only** database login.
- [ ] Keep the machine patched; run `update -Latest` when releases land.
- [ ] Don't set `ASPNETCORE_ENVIRONMENT` on a server.
- [ ] Report anything odd: see [SECURITY.md](../SECURITY.md).
