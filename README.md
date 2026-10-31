# abaci-bot

## Getting Started

### 1. Configure appsettings.json

This repository does **not** include a config file. You must create one yourself based on the provided template:

```bash
cp appsettings.demo.json appsettings.json
```

Then edit `appsettings.json` and fill in your actual values:

| Field | Description |
|---|---|
| `GitHubApp.AppId` | Your GitHub App's numeric ID |
| `GitHubApp.InstallationId` | The installation ID for the target org/repo |
| `GitHubApp.PrivateKey` | The RSA private key generated for your GitHub App (PEM format, newlines as `\n`) |
| `GitHubApp.WebhookSecret` | Shared secret for validating `X-Hub-Signature-256`. Must exactly match your GitHub webhook secret. |
| `GitHubApp.TeamName` | Team slug used for review workflow (for example: `captains`). Review labels are updated only when review is submitted by a member of this team. |
| `Kestrel.Endpoints.Http.Url` | The address and port the server listens on (default: `http://0.0.0.0:3456`) |

### 2. Daily Governance Modules & Options

`abaci-bot` includes an extensible, prioritized governance pipeline enforcing daily lifecycle rules:

- **Instant Indicator (`new`)**: Automatically tags newly opened pull requests with `new`. Removes it as soon as a non-author human leaves a review or comment.
- **Target Release Classification**: Tags PRs targeting rolling branches (e.g. `main`, `master`) with `Target: Rolling`, and LTS branches (e.g. `lts/24.03`, `lts-24.03`) with `Target: LTS`.
- **Build System & CI Analysis**: Inspects PR files to assign build system tags (`BuildSystem: autotools`, `cmake`, `meson`, etc.) and `CI`.
- **Multi-Repo Change Type Classification**: Analyzes modified file paths to assign change categories (`Type: Packaging`, `Type: Documentation`, `Type: Frontend`, `Type: Infrastructure`).
- **Automated Stale Lifecycle & Auto-Close**: Periodically monitors inactive issues/PRs, marks them with `Status: Stale` after 30 days of inactivity, and auto-closes after a 7-day grace period. Human activity (push, comment, review) immediately removes `Status: Stale`.
- **Bot Detection & Protection**: Filters bot actions (`[bot]`, whitelist, account type) so automated activity does not disrupt governance states.

#### Stale Lifecycle Configuration (`StaleLifecycle`)

The stale lifecycle background service is configured in `appsettings.json` under the `StaleLifecycle` section:

| Field | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Master toggle for the stale lifecycle background service. Must be set to `true` to run sweeps. |
| `DryRun` | `bool` | `true` | When `true`, scans and logs intended label additions and closures without mutating GitHub. Must be set to `false` for live execution. |
| `MonitoredRepositories` | `string[]` | `[]` | Explicit list of repositories to scan in `"owner/repo"` format (e.g. `["openRuyi-Project/openRuyi"]`). Required; empty list skips sweeps to prevent accidental mutations. |
| `ExcludedRepositories` | `string[]` | `[]` | Repositories explicitly excluded from sweeps. |
| `StaleDays` | `int` | `30` | Inactive days before an item is marked with `Status: Stale` and notified via comment. |
| `CloseDays` | `int` | `7` | Grace period days after the stale notification comment before an item is automatically closed. |
| `ExemptLabels` | `string[]` | `["Status: Pinned", ...]` | Labels that protect an issue or PR from being marked or closed as stale. |
| `StaleCommentTemplate` | `string` | *(friendly notice)* | Notification comment posted when marked stale (anchors the grace period). |
| `CloseCommentTemplate` | `string` | *(closing notice)* | Comment posted when automatically closing an expired stale item. |

### 3. Configure GitHub App Permissions and Webhook Events

Your GitHub App must be configured with the following permissions:

**Organization permissions**

- Members: **Read-only**

**Repository permissions**

- Pull requests: **Read and write**
- Issues: **Read and write**
- Contents: **Read-only**
- Actions: **Read-only** *(Required for ABI compliance analysis to receive CI build completion events and download workflow run artifacts)*

Your GitHub App must also subscribe to these webhook events:

- Issue comments
- Issues
- Pull request review comments
- Pull request review threads
- Pull request reviews
- Pull requests
- Workflow runs *(Required for ABI compliance: triggers `ProcessWorkflowRunWebhookAsync` on CI job completion)*

> [!IMPORTANT]
> When adding the **Actions: Read-only** permission to an existing GitHub App, GitHub requires repository administrators to approve the updated permission grant. The app's `InstallationId` may also change during re-installation; verify that `GitHubApp:InstallationId` in `appsettings.json` matches the active installation ID.

### 4. Distribution Quality & Compliance Modules

`abaci-bot` includes distribution-grade automated compliance, security, and gating checks:

- **SPDX License Compliance (`SpdxCompliance`)**: Inspects RPM spec files modified in pull requests, parses compound license expressions (`OR`, `AND`, `WITH`), normalizes legacy RPM aliases, and flags non-compliant or unknown licenses with `Compliance: License Issue` and GFM warning notices. Includes a background periodic synchronization service (`SpdxMetadataRefreshService`) that hot-reloads license and exception definitions from official indices.
- **Vulnerability Tracking (`CveSecurity`)**: Detects CVE identifiers (`CVE-YYYY-NNNNN`) in pull request titles and descriptions. Queries OSV.dev with NIST NVD CVE 2.0 fallback to assign CVSS severity levels (`Severity: 1` through `Severity: 4`) and track vulnerability resolution lifecycle.
- **Cascading Rebuild Impact Gate (`RebuildImpact`)**: Downloads and parses repository metadata (`primary.xml.gz`, `primary.xml.zst`, or `repomd.xml`) to build a full reverse dependency DAG. Computes affected downstream packages (`Rebuild: 0` to `Rebuild: 5001+`) and automatically enforces admission gating (`Workflow: Blocked`) when rebuild impact on the `Rolling` branch exceeds the configured threshold.
- **Binary ABI Compliance (`AbiCompliance`)**: Listens for GitHub `workflow_run.completed` events, downloads CI-built RPM artifacts, fetches baseline stable packages, and executes `libabigail` (`abidiff`) to detect binary shared library incompatibilities (`Impact: ABI`, `Impact: Breaking Change`, `Impact: ABI Check Failed`).

#### SPDX Compliance & Periodic Refresh Configuration (`SpdxCompliance`)

The SPDX license compliance module and its periodic background synchronization service (`SpdxMetadataRefreshService`) are configured in `appsettings.json` under the `SpdxCompliance` section:

| Field | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Master toggle for SPDX compliance validation and background synchronization. |
| `UpdateRemoteUrl` | `string` | `"https://spdx.org/licenses/licenses.json"` | Remote URL for official SPDX licenses JSON index. |
| `UpdateExceptionsRemoteUrl` | `string` | `"https://spdx.org/licenses/exceptions.json"` | Remote URL for official SPDX exceptions JSON index. |
| `AutoUpdateIntervalHours` | `double` | `24` | Background synchronization interval in hours. Set to `0` to disable remote network polling (offline embedded baseline only). |
| `AllowLegacyAliases` | `bool` | `true` | Whether to accept legacy RPM license identifiers (e.g., `GPLv2+`, `LGPLv2+`). |

### 5. Repository Labels Prerequisite

Before deploying the bot, ensure that target repositories have created the required workflow and status labels (GitHub API does not automatically create missing labels when applying them):

- **Workflow states**: `Workflow: In Dev`, `Workflow: Ready For Review`, `Workflow: In Review`, `Workflow: Blocked`, `Workflow: Complete`
- **Contributor & Status**: `Community: Contribution`, `Community: Student contribution`, `Commits: Updated`, `AI Assistance`
- **Build & CI**: `CI`, `Target: Rolling`, `Target: LTS`
- **Build Systems**: `BuildSystem: autotools`, `BuildSystem: cmake`, `BuildSystem: golang`, `BuildSystem: golangmodule`, `BuildSystem: meson`, `BuildSystem: misc`, `BuildSystem: pyproject`, `BuildSystem: rust`, `BuildSystem: rustcrate`
- **SPDX License Compliance**: `Compliance: License Issue`
- **Vulnerability Severity & Lifecycle**: `Severity: 1`, `Severity: 2`, `Severity: 3`, `Severity: 4`, `Vulnerability: Detected`, `Vulnerability: Fixed-Pending`, `Vulnerability: Resolved`, `Vulnerability: Dismissed`, `Vulnerability: Pending Verification`
- **Rebuild Impact**: `Rebuild: 0`, `Rebuild: 1-10`, `Rebuild: 11-100`, `Rebuild: 101-500`, `Rebuild: 501-1000`, `Rebuild: 1001-2500`, `Rebuild: 2501-5000`, `Rebuild: 5001+`
- **ABI & Breaking Changes**: `Impact: ABI`, `Impact: Breaking Change`, `Impact: ABI Check Failed`

> **Note**: For the complete, authoritative list of runtime label constants managed by the governance engine, refer to [`Services/GitHubLabels.cs`](Services/GitHubLabels.cs).

### 6. Run

#### Option A: Run directly

```bash
dotnet run
```

Or run the published binary directly:

```bash
./abaci-bot
```

#### Option B: Run with Docker Compose (Recommended)

1. Create your configuration file:
   ```bash
   cp appsettings.demo.json appsettings.json
   # Edit appsettings.json with your GitHub App credentials
   ```

2. Start the bot:
   ```bash
   docker compose up -d
   ```

3. View logs:
   ```bash
   docker compose logs -f
   ```

4. Stop the bot:
   ```bash
   docker compose down
   ```

#### Option C: Run with Docker directly

```bash
# Pull the image
docker pull ghcr.io/openruyi/abaci-bot:latest

# Run with mounted config
docker run -d \
  --name abaci-bot \
  -p 3456:3456 \
  -v $(pwd)/appsettings.json:/app/appsettings.json:ro \
  ghcr.io/openruyi/abaci-bot:latest
```

### 7. Build Docker Image Locally (Optional)

If you want to build the image yourself:

```bash
docker build -t abaci-bot .
```

Or with docker compose:

```bash
docker compose build
```

## Configuration via Environment Variables

You can also configure the bot using environment variables (useful for container orchestration):

```yaml
environment:
  - GitHubApp__AppId=123456
  - GitHubApp__InstallationId=78901234
  - GitHubApp__PrivateKey=-----BEGIN RSA PRIVATE KEY-----\n...\n-----END RSA PRIVATE KEY-----
  - GitHubApp__WebhookSecret=webhook_secret
  - GitHubApp__TeamName=captains
```

> Note: Use double underscores (`__`) to represent nested configuration keys.

## Health Check

The bot exposes a health check endpoint at `/health` for monitoring:

```bash
curl http://localhost:3456/health
```

## LICENSE

MIT License
