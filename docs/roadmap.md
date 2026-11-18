# Roadmap

Done in the skeleton: Kestrel Windows-service hosting, installer (install, update with rollback, check, rollback, uninstall, graph-cert), `SelfCheck` + `/api/diagnostics`, schema versioning, connector backend (sql, rest, csv) with scheduler and tests, CI, tagged releases, sample forms.

## Next

1. **Connector screens.** A click-through "Add connector" wizard: pick a source, fill the settings, press Test to see real sample rows, map value/label with dropdowns, name the list, choose a refresh schedule, save. Admin page with last run status and Run now. The wizard works only through `/api/connectors/test` and the existing CRUD.
2. **Diagnostics screen.** Admin page showing `/api/diagnostics` with plain-language fixes; a banner when a Graph certificate is within 30 days or the outbox is failing.
3. **"Update available" banner.** Admins see a notice when a newer GitHub release exists. Never auto-installs.
4. **More connector kinds.** OData, SharePoint/Microsoft Graph lists, LDAP/AD groups. Live type-ahead lookups (as opposed to whole-list sync).
5. **Secrets in the UI.** Write connector secrets into `config.json` from the wizard (admin only, never echoed back), so no hand editing is needed.
6. **Installer polish.** Smoke test on a hosted Windows runner (`installer-smoke.yml` exists as a manual workflow), MSI/winget wrapper if wanted, optional scheduled self-update task.

## Repo hardening to come back to

- [ ] **Require actions to be pinned to a full-length commit SHA** (Settings → Actions → General → "Require actions to be pinned to a full-length commit SHA"). Not on yet: the workflows use version tags like `actions/checkout@v7`. To turn it on: pin each `uses:` line in `.github/workflows/*.yml` to a SHA with the version in a comment (`uses: actions/checkout@<sha> # v7`), then flip the setting. Dependabot's `github-actions` ecosystem keeps SHA-pinned actions current.
- [ ] **CodeQL code scanning** (Settings → Advanced Security → CodeQL analysis → Set up → Default). Free for public repos; covers C# and JavaScript/TypeScript.
- [ ] **Grouped security updates** (optional), if Dependabot security PRs become noisy.
- [ ] Decide whether to ignore Dependabot's major bumps for `@types/node` so it tracks the Node version CI uses (22).
- [ ] Run a real install and update on a throwaway VM, then tag a patch release to exercise `update -Latest` and the release workflow with the new action versions.

## Not planned yet

File attachments, CAPTCHA for public forms, a separate public host, DB Mail alerts.
