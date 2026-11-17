# Testing and Verification Guide

This page describes the automated testing architecture, webhook simulation harness, coverage requirements, and local execution commands for `abaci-bot`.

---

## 1. Testing Framework and Architecture

`abaci-bot` maintains a comprehensive test suite in `tests/abaci-bot.Tests/` built on:
- **xUnit**: Test runner and assertion framework.
- **NSubstitute**: Mocking framework for GitHub API clients, HTTP services, and process runners.
- **Coverlet**: Code coverage instrumentation for line and branch analysis.

The test suite tests end-to-end webhook processing with fake GitHub API responses, verifying label mutations, comments, and concurrency locking.

---

## 2. Webhook Event Trigger Matrix

The test suite simulates GitHub webhook events across various scenarios:

| Event Type | Action | Scenarios Tested |
|---|---|---|
| `pull_request` | `opened` | Tagging `Status: New`, classifying release stream (`Target: Rolling` / `Target: LTS`), checking author email for `Community:`, detecting build system, validating SPDX license, parsing CVEs, computing rebuild impact. |
| `pull_request` | `synchronize` | Updating rebuild impact on new commits, adding `Commits: Updated` if currently in `Workflow: Blocked` (removed only when a non-author Captain responds), triggering re-evaluation of spec licenses. |
| `pull_request` | `closed` | Setting `Workflow: Complete` on merge, transitioning CVE tracking to `Vulnerability: Resolved`. |
| `pull_request` | `ready_for_review` | Transitioning from `Workflow: In Dev` to `Workflow: Ready For Review`. |
| `pull_request` | `converted_to_draft` | Reverting to `Workflow: In Dev`. |
| `pull_request_review` | `submitted` | Team Captain reviews advance PR from `Ready For Review` to `In Review`, removing `Status: New`. |
| `issue_comment` | `created` | First human non-author comment removes `Status: New`. |
| `workflow_run` | `completed` | Downloading RPM artifacts, executing Libabigail `abipkgdiff`, and applying `Impact: ABI`, `Impact: Breaking Change`, or `Status: ABI Unverified`. |

---

## 3. Local Execution Commands

### Run All Tests
```powershell
dotnet test
```

### Run Tests with Code Coverage
```powershell
dotnet test -c Release --verbosity normal --collect:"XPlat Code Coverage" --settings coverlet.runsettings
```

### Filter Specific Tests
```powershell
# Run only ABI compliance tests
dotnet test --filter "FullyQualifiedName~AbiCompliance"

# Run only Rebuild Impact tests
dotnet test --filter "FullyQualifiedName~RebuildImpact"
```

### Release Compilation Gate
Verify that zero warnings and zero compiler diagnostics exist:
```powershell
dotnet build -c Release -warnaserror
```

---

## 4. Quality & Verification Gates

All pull requests and deployment builds must satisfy the following criteria:

| Metric | Required Threshold | Verification Command |
|---|---|---|
| **Test Pass Rate** | **100%** (0 failures, 0 skipped) | `dotnet test` |
| **Line Coverage** | **>= 80.0%** | `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` |
| **Branch Coverage** | **>= 80.0%** | `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` |
| **Compiler Warnings** | **0 warnings** (`-warnaserror`) | `dotnet build -c Release -warnaserror` |
| **Obsolete Labels** | **0 occurrences in source code** (excluding docs/migration references) | `git grep` check on `Services/`, `Modules/`, `Pipelines/` |
| **Policy Compliance** | **0 emojis, 0 internal task codes** | Codebase audit |

---

## 5. End-to-End Sandbox Testing

When deploying to a testing or staging repository:
1. Provision required labels using `docs/labels.md`.
2. Configure webhook delivery in GitHub repository settings pointing to your testing endpoint (`https://<your-endpoint>/`).
3. Open a test PR modifying a `.spec` file to verify automated labeling and comment posting.
