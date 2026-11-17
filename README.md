# abaci-bot

`abaci-bot` is a webhook-driven governance daemon and compliance bot built for `openRuyi-Project` package repositories and related infrastructure.

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

## Quick Start

### 1. Configure the Application
Copy the documented template and populate the 6 required settings:

```bash
cp appsettings.demo.jsonc appsettings.json
```

Required parameters:
- `GitHubApp:AppId`: Numeric GitHub App ID.
- `GitHubApp:InstallationId`: Target GitHub App installation ID.
- `GitHubApp:PrivateKey`: RSA Private Key (PEM format).
- `GitHubApp:WebhookSecret`: Webhook HMAC-SHA256 secret.
- `GitHubApp:TeamName`: Maintainer team slug (e.g. `captains`).
- `Kestrel:Endpoints:Http:Url`: HTTP listening address (`http://0.0.0.0:3456`).

### 2. Launch with Docker Compose
Run the official Fedora 45 production container:

```bash
docker compose up -d
```

### Minimal Configuration Example
```json
{
  "GitHubApp": {
    "AppId": 123456,
    "InstallationId": 78901234,
    "PrivateKey": "-----BEGIN RSA PRIVATE KEY-----\n...\n-----END RSA PRIVATE KEY-----",
    "WebhookSecret": "your_webhook_secret",
    "TeamName": "captains"
  },
  "Kestrel": {
    "Endpoints": { "Http": { "Url": "http://0.0.0.0:3456" } }
  }
}
```

---

## Health Checks & Diagnostics

Verify that the service is running and native tools are operational:

```bash
curl -f http://localhost:3456/health
```

The response indicates operational health and whether the binary ABI engine is operating in native mode (`native`) or managed fallback (`managed`).

If native Libabigail or RPM extraction tools (`abipkgdiff`, `rpm2cpio`, `cpio`) are missing on startup, the application terminates immediately unless `AllowManagedFallback` is explicitly enabled. Refer to [`docs/modules/abi-compliance.md`](docs/modules/abi-compliance.md).

---

## Documentation Center

Comprehensive operational, architecture, and module references are organized in the [`docs/`](docs/README.md) directory:

- [`docs/README.md`](docs/README.md) — Documentation index and reading paths.
- [`docs/architecture.md`](docs/architecture.md) — Pipeline architecture, module priorities, and mutex state machine.
- [`docs/deployment.md`](docs/deployment.md) — System requirements, Docker, systemd unit templates, reverse proxy setup, and rollback.
- [`docs/configuration.md`](docs/configuration.md) — Configuration hierarchy, whole-line comment rules, secrets, and environment variables.
- [`docs/labels.md`](docs/labels.md) — Exhaustive label taxonomy, mutex groups, upstream `gh` setup commands, and sandbox migration.
- [`docs/testing.md`](docs/testing.md) — Test harness, webhook trigger matrix, local testing, and CI verification.

---

## License

This project is licensed under the MIT License. See the `LICENSE` file for details.
