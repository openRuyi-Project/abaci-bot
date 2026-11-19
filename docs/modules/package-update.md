# Package Update Service

Autonomous background worker that periodically polls upstream package releases, evaluates upgrade admission criteria, updates RPM SPEC files, backfills continuous integration checksums, and publishes automated pull requests.

---

## 1. Purpose and Trigger Events

Automates the routine discovery, preparation, and publication of upstream package upgrades for RPM specifications in package repositories. Operates as an independent scheduled background service (`IHostedService`).

### Trigger Mechanism
- Periodic background timer configured by `CheckIntervalHours` (default: every 12 hours).
- Does not rely on incoming webhook events.

---

## 2. Evaluation Logic and Workflow

```mermaid
flowchart TD
    Start["Scheduled Sweep Timer"] --> QuotaCheck{"Core Rate Limit >= MinRateLimitRemaining?"}
    QuotaCheck -- No --> Defer["Log Warning & Defer Sweep"]
    QuotaCheck -- Yes --> CleanPrs["Clean Stale Failed PRs (FailedPrTtlDays)"]
    CleanPrs --> ConcurrencyCheck{"Active Bot PRs >= MaxConcurrentPullRequests?"}
    ConcurrencyCheck -- Yes --> PostTasks["Run Post-Sweep OBS Verification"]
    ConcurrencyCheck -- No --> Discover["Discover Candidates (Lookout & crates.io)"]
    Discover --> FilterCandidates["Filter Cooldown & Admitted Versions"]
    FilterCandidates --> RewriteSpec["Rewrite SPEC (Plain or Rust)"]
    RewriteSpec --> PublishPr["Create Branch & Publish Pull Request"]
    PublishPr --> PostTasks
    PostTasks --> Done["End Sweep"]
```

### Key Functional Behaviors
1. **Admission Rules**: Prevents accidental cross-major SemVer and CalVer breaking changes (`AllowCrossMajorVersion = false`) and skips packages containing downstream `Patch` directives (`SkipPackagesWithPatches = true`). Respects `#!NoAutoUpdate` opt-out tags inside SPEC files.
2. **Spec Version Rewriters**:
   - `PlainSpecVersionRewriter`: Updates `Version:` and resets `Release: 1%{?dist}`.
   - `RustSpecVersionRewriter`: Resolves new crate checksums, rewrites package versions, and maintains vendor cargo configurations.
3. **CI Checksum Backfill**: Detects failed pull requests caused by invalid `#!RemoteAsset` checksums and automatically backfills correct SHA-256 digests.
4. **Failure Cooldown & Tracking**: Tracks consecutive CI failures per package (`MaxConsecutiveFailures`). When the threshold is reached, creates a tracking issue carrying `Auto-Update: Blocked` and `Status: stale-exempt` and enters cooldown (`CooldownHours` / `GiveUpIssueCooldownDays`).
5. **Post-Merge OBS Verification**: `ObsPostMergeVerifier` polls Open Build Service (`build.openruyi.cn`) to verify that merged pull request commits build successfully. If `RequireObsSuccessAfterMerge` is enabled and OBS fails, applies `Auto-Update: Blocked`.

---

## 3. Managed Labels and Mutex Group

- `Auto-Update`: Base label applied to all pull requests published by the package update service.
- `Auto-Update: Rust`: Applied to pull requests updating Rust Cargo crates.
- `Auto-Update: Blocked`: Applied to tracking issues when repeated failures occur or to merged PRs failing post-merge OBS verification.
- `Status: stale-exempt`: Applied to tracking issues so automated stale sweeps do not close them.

---

## 4. Configuration Options

Configured under the `PackageUpdate` JSON section in `appsettings.json`:

| Parameter | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Master toggle for the package update background service. |
| `DryRun` | `bool` | `true` | When `true`, discovers candidates and rewrites specs in memory without pushing branches or mutating GitHub. |
| `TargetRepository` | `string` | `"openRuyi-Project/openRuyi"` | Target repository to publish pull requests against in `"owner/repo"` format. |
| `BotLogin` | `string` | `"aqua-rium"` | GitHub login used to author automated update pull requests. |
| `BotPat` | `string` | `""` | Optional GitHub Personal Access Token. When empty, falls back to GitHub App authentication. |
| `BranchPrefix` | `string` | `"autoupdate/"` | Prefix prepended to generated git branches. |
| `CheckIntervalHours` | `int` | `12` | Frequency in hours between candidate discovery sweeps. |
| `Lookout` | `LookoutUpdateOptions` | (object) | Configuration for the Release-Monitoring / Anitya upstream bridge. |
| `RustCratePoller` | `RustCratePollerOptions` | (object) | Configuration for the crates.io sparse index poller. |
| `Obs` | `ObsOptions` | (object) | Configuration for post-merge Open Build Service build verification. |
| `AllowCrossMajorVersion` | `bool` | `false` | Whether to permit updates crossing major version boundaries. |
| `SkipPackagesWithPatches` | `bool` | `true` | When `true`, packages with downstream `Patch` directives are skipped. |
| `MaxCandidatesPerSweep` | `int` | `5` | Maximum number of update candidates admitted and published in a single sweep. |
| `MaxConcurrentPullRequests` | `int` | `5` | Maximum open update pull requests allowed simultaneously. |
| `FailedPrTtlDays` | `int` | `7` | Days an inactive pull request with failing CI remains open before automated closure. |
| `CooldownHours` | `int` | `72` | Hours a package remains in cooldown after repeated CI failures. |
| `MaxConsecutiveFailures` | `int` | `3` | Consecutive failed pull requests before entering cooldown. |
| `IssueOnGivingUp` | `bool` | `true` | Whether to file a tracking issue when a package reaches consecutive failure limits. |
| `GiveUpIssueCooldownDays` | `int` | `30` | Fallback cooldown period in days when `CooldownHours` is zero. |
| `GiveUpIssueLabel` | `string` | `"Status: stale-exempt"` | Protection label applied to failure tracking issues. |
| `MinRateLimitRemaining` | `int` | `500` | Minimum core GitHub API quota required to begin a sweep. |
| `RequestThrottleMilliseconds` | `int` | `2000` | Inter-request delay between GitHub API mutations to prevent secondary rate limits. |

### Nested Options

#### `LookoutUpdateOptions` (`PackageUpdate:Lookout`)
- `Enabled` (`bool`, default: `true`): Toggles Lookout candidate source.
- `BaseUrl` (`string`, default: `"https://lookout.openruyi.cn"`): Base endpoint URL for Lookout API.
- `StaleThresholdHours` (`int`, default: `48`): Cache expiry for Lookout candidate queries.

#### `RustCratePollerOptions` (`PackageUpdate:RustCratePoller`)
- `Enabled` (`bool`, default: `true`): Toggles crates.io candidate poller.
- `IndexBaseUrl` (`string`, default: `"https://index.crates.io"`): Base URL for crates.io sparse index.
- `MaxCratesPerSweep` (`int`, default: `60`): Maximum crate versions queried per sweep.
- `RequestDelayMilliseconds` (`int`, default: `300`): Inter-query throttle delay for crates.io queries.

#### `ObsOptions` (`PackageUpdate:Obs`)
- `BaseUrl` (`string`, default: `"https://build.openruyi.cn"`): OBS public API endpoint.
- `Project` (`string`, default: `"openruyi"`): Target OBS project name.
- `TargetRepository` (`string`, default: `"riscv64"`): Target OBS repository name.
- `TargetArch` (`string`, default: `"riscv64"`): Target OBS architecture filter.
- `RequireObsSuccessAfterMerge` (`bool`, default: `false`): Applies `Auto-Update: Blocked` if post-merge OBS build does not succeed.

---

## 5. Failure Handling and Fallback Semantics

- **Quota Starvation**: Sweeps defer automatically when remaining GitHub API quota drops below `MinRateLimitRemaining`.
- **Merge Commit Synchronization**: OBS verification checks `_service` revision against the PR's `MergeCommitSha` to avoid checking stale build results before synchronization completes.
- **Fail-Safe Dry Run**: Starts in `DryRun = true` and `Enabled = false` by default.

---

## 6. Minimal Configuration Example

```json
{
  "PackageUpdate": {
    "Enabled": true,
    "DryRun": false,
    "TargetRepository": "openRuyi-Project/openRuyi",
    "CheckIntervalHours": 12,
    "MaxCandidatesPerSweep": 5,
    "MaxConcurrentPullRequests": 5
  }
}
```
