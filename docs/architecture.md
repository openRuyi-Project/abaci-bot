# System Architecture

`abaci-bot` is a webhook-driven governance bot and background daemon built for openEuler / openRuyi package repositories and related infrastructure.

---

## 1. Event Ingestion Pipeline

Incoming GitHub webhook events are authenticated, parsed, and dispatched through the pipeline:

```mermaid
flowchart TD
    GH["GitHub Webhook Event<br>(HMAC-SHA256 Signed)"] --> Kestrel["Kestrel Web Server<br>(Port 3456)"]
    Kestrel --> MW["Webhook Verification & Routing<br>(Octokit.Webhooks)"]
    MW --> Lock["PullRequestLockManager<br>(Per-PR In-Memory FIFO Lock)"]
    Lock --> Ctx["PullRequestContext Assembly<br>(Labels, Files, Commits, Sender)"]
    Ctx --> Pipe["PullRequestPipeline<br>(Priority Ordered Modules)"]
    Pipe --> Mutex["LabelMutexEngine<br>(Conflict Resolution & Stripping)"]
    Mutex --> Sync["GitHub Reconciler<br>(Batched Label Add/Remove & Comments)"]
```

### Locking Strategy
To prevent race conditions during rapid event arrivals (such as push batches, synchronized reviews, or automated bots), `PullRequestLockManager` guarantees serialized execution per PR using keyed async semaphore locks (`owner/repo#prNumber`).

---

## 2. Module Priority Order

Each module implements `IPullRequestModule` with a fixed integer priority. Modules with lower priority values execute first:

| Priority | Module | Responsibility | Key Input Events |
|---|---|---|---|
| **10** | `WorkflowStateModule` | Manages review progression, WIP titles, draft states, and merge completion. | `pull_request`, `pull_request_review`, `issue_comment` |
| **12** | `StaleUnstaleModule` | Removes `Status: Stale` immediately upon human activity (commits, comments, reviews). | `pull_request`, `pull_request_review`, `issue_comment` |
| **15** | `ShinyNewPrModule` | Applies `Status: New` to newly opened pull requests; removes upon human engagement. | `pull_request.opened`, `issue_comment`, `pull_request_review` |
| **20** | `UserContributionModule` | Distinguishes student contributions from general community contributions based on author email. | `pull_request` |
| **25** | `TargetClassificationModule` | Tags `Target: Rolling` or `Target: LTS` according to repository and branch naming patterns. | `pull_request.opened`, `pull_request.edited` |
| **30** | `BuildSystemAnalysisModule` | Classifies build systems in changed spec files (`autotools`, `cmake`, `meson`, `rust`, etc.) and detects CI diffs. | `pull_request` file list |
| **35** | `ChangeTypeClassificationModule` | Classifies changes as `Type: Packaging`, `Type: Documentation`, `Type: Infrastructure`, or `Type: Frontend`. | `pull_request` file list |
| **40** | `AiAssistanceModule` | Detects `AI Assistance` checkbox declarations in pull request descriptions. | `pull_request.opened`, `pull_request.edited` |
| **60** | `SpdxComplianceModule` | Validates that spec file `License:` declarations conform to canonical SPDX expressions. | `pull_request.opened`, `pull_request.synchronize`, `pull_request.edited` |
| **70** | `VulnerabilityModule` | Scans commit messages and PR bodies for CVE references, querying OSV and NVD to apply severity and lifecycle labels. | `pull_request.opened`, `pull_request.synchronize`, `pull_request.ready_for_review`, `pull_request.closed` |
| **80** | `RebuildImpactModule` | Evaluates transitive reverse build dependencies via RPM repodata and applies `Rebuild:` tiers. Blocks PR if impact exceeds threshold. | `pull_request.opened`, `pull_request.synchronize`, `pull_request.reopened`, `pull_request.ready_for_review`, `pull_request.edited` |
| **85** | `AbiComplianceModule` | Compares compiled RPM packages against baseline stable builds using Libabigail `abipkgdiff`. | `workflow_run.completed` |

---

## 3. Label Mutex State Machine

The bot guarantees consistent repository metadata by enforcing mutual exclusion through `LabelMutexEngine`. Mutex groups ensure that conflicting labels cannot coexist on an issue or PR.

When a module schedules a label addition, `LabelMutexEngine` automatically schedules removals for all other labels within the same mutex group:

### Mutex Groups
1. **Workflow Lifecycle**: `Workflow: In Dev`, `Workflow: Ready For Review`, `Workflow: In Review`, `Workflow: Blocked`, `Workflow: Complete`
2. **Community Contribution**: `Community: Student contribution`, `Community: Contribution`
3. **Release Target**: `Target: Rolling`, `Target: LTS`
4. **Vulnerability Severity**: `Severity: 1`, `Severity: 2`, `Severity: 3`, `Severity: 4`
5. **Vulnerability Lifecycle**: `Vulnerability: Detected`, `Vulnerability: Fixed-Pending`, `Vulnerability: Resolved`, `Vulnerability: Dismissed`
6. **Rebuild Impact**: `Rebuild: 0`, `Rebuild: 1-10`, `Rebuild: 11-100`, `Rebuild: 101-500`, `Rebuild: 501-1000`, `Rebuild: 1001-2500`, `Rebuild: 2501-5000`, `Rebuild: 5001+`

---

## 4. Background Scheduled Workers

In addition to event-driven webhook processing, `abaci-bot` hosts background services that run on startup or on periodic timers (`IHostedService`):

### AbiEngineProbe
- Probes native `abipkgdiff`, `rpm2cpio`, and `cpio` tool availability on startup.
- Enforces the deployment gate: aborts startup if native tools are missing and `AllowManagedFallback == false`.
- Detects RPM v6 payload extraction capability.

### StaleLifecycleService
- Scans monitored repositories for inactive issues and pull requests.
- Applies `Status: Stale` after `StaleDays` of inactivity and closes items after `CloseDays` unless exempted by `Status: stale-exempt` or protected labels.
- Implements secondary rate limit detection, token bucket throttling, and graceful backoff.

### RebuildMetadataRefreshService
- Periodically checks and downloads repository `primary.xml.gz` metadata.
- Refreshes in-memory reverse dependency indexes for `RebuildImpactModule`.

### SpdxMetadataRefreshService
- Periodically synchronizes canonical license lists and exception identifiers from SPDX upstream endpoints (`https://spdx.org/licenses/`).
- Updates an in-memory trie and LRU cache to validate spec `License:` headers.
