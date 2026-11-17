# abaci-bot

`abaci-bot` is a webhook-driven governance daemon and compliance bot built for `openRuyi-Project` package repositories and related infrastructure.

---

## Start from Zero

1. **Create and install the GitHub App.** Configure the webhook URL
   (`https://<your-host>/api/webhook`), the webhook secret, the repository and
   organization permissions, the private key, and the event subscriptions. Install
   the App on the organization and note the installation ID.
   Follow [GitHub App Setup from Scratch](docs/deployment.md#5-github-app-setup-from-scratch).

2. **Create the maintainer team.** Create an organization team named `captains`
   (or set `GitHubApp:TeamName` to your own slug) and add the maintainers who should
   move pull requests into review. The team must be visible to the App's `Members`
   permission.

3. **Create the managed labels.** Every label the bot writes must exist before the
   first webhook arrives; a missing label fails the pipeline for that event.
   Use the commands in [docs/labels.md](docs/labels.md).

4. **Configure the application.** Copy the reference file and fill the five App
   values plus the listen address:

   ```bash
   cp appsettings.demo.jsonc appsettings.json
   ```

   Required: `GitHubApp:AppId`, `GitHubApp:InstallationId`,
   `GitHubApp:PrivateKey`, `GitHubApp:WebhookSecret`, `GitHubApp:TeamName`.
   See [docs/configuration.md](docs/configuration.md) for precedence, secrets, and
   the full option reference.

5. **Start the service.**

   ```bash
   docker compose up -d
   ```

6. **Verify.** Check the health endpoint, then open a draft pull request and confirm
   that `Status: New`, `Workflow: In Dev`, `Target: Rolling`, and the build-system
   labels appear.

   ```bash
   curl -f http://localhost:3456/health
   ```

---

## Capabilities & Modules

| Module | Scope & Function | Documentation |
|---|---|---|
| **Workflow State** | Tracks review progression, WIP titles, draft states, and merge completion. | [`docs/modules/workflow-state.md`](docs/modules/workflow-state.md) |
| **New PR Triage** | Tags newly opened PRs with `Status: New`; removes upon first human response. | [`docs/modules/workflow-state.md`](docs/modules/workflow-state.md#shiny-new-pr-governance-shinynewprmodule) |
| **Target Stream** | Classifies release branches into `Target: Rolling` vs `Target: LTS`. | [`docs/modules/target-classification.md`](docs/modules/target-classification.md) |
| **Stale Lifecycle** | Monitors inactivity, marks `Status: Stale`, and closes abandoned items. | [`docs/modules/stale-lifecycle.md`](docs/modules/stale-lifecycle.md) |
| **Change Type** | Classifies changes as `Type: Packaging`, `Documentation`, `Infrastructure`, `Frontend`. | [`docs/modules/change-type.md`](docs/modules/change-type.md) |
| **Build System** | Detects build tools in specs (`autotools`, `cmake`, `meson`, `rust`, etc.) and tags `CI`. | [`docs/modules/build-system.md`](docs/modules/build-system.md) |
| **AI Assistance** | Detects author disclosures regarding AI assistance in pull request descriptions. | [`docs/modules/ai-assistance.md`](docs/modules/ai-assistance.md) |
| **SPDX Compliance** | Validates that spec `License:` headers conform to canonical SPDX identifiers. | [`docs/modules/spdx-compliance.md`](docs/modules/spdx-compliance.md) |
| **CVE Security** | Scans PRs for CVE mentions, queries OSV/NVD, and manages severity and lifecycle. | [`docs/modules/vulnerability.md`](docs/modules/vulnerability.md) |
| **Rebuild Impact** | Computes transitive reverse build dependencies via RPM repodata; gates heavy PRs. | [`docs/modules/rebuild-impact.md`](docs/modules/rebuild-impact.md) |
| **ABI Compliance** | Compares compiled RPMs using Libabigail `abipkgdiff` to detect binary breaking changes. | [`docs/modules/abi-compliance.md`](docs/modules/abi-compliance.md) |

---

## Health and Diagnostics

```bash
curl -f http://localhost:3456/health
```

The response includes `abiEngine` (`native`, `managed`, `unavailable`, or `disabled`).
When native Libabigail or RPM extraction tools (`abipkgdiff`, `rpm2cpio`, `cpio`) are
missing at startup, the process aborts unless `AbiCompliance:AllowManagedFallback`
is explicitly enabled. See [docs/modules/abi-compliance.md](docs/modules/abi-compliance.md).

---

## Further Reading

- [docs/deployment.md](docs/deployment.md) — GitHub App setup, Docker, systemd, reverse proxy, rollback.
- [docs/configuration.md](docs/configuration.md) — configuration precedence, comments, secrets, environment variables.
- [docs/labels.md](docs/labels.md) — label taxonomy, mutex groups, provisioning and migration.
- [docs/architecture.md](docs/architecture.md) — pipeline, module priorities, background workers.
- [docs/testing.md](docs/testing.md) — test harness, trigger matrix, sandbox validation.
- [docs/README.md](docs/README.md) — documentation index and reading paths.

---

## License

This project is licensed under the MIT License. See the `LICENSE` file for details.
