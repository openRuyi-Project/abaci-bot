# Workflow State Module

Tracks pull request review lifecycle states, work-in-progress status, maintainer triage, and merge completion. Also covers `ShinyNewPrModule` for initial triage tagging.

---

## 1. Purpose and Trigger Events

`WorkflowStateModule` ensures that every open pull request carries exactly one active workflow status label reflecting its development and review progression.

### Trigger Events
- `pull_request` (`opened`, `reopened`, `closed`, `ready_for_review`, `converted_to_draft`, `synchronize`, `labeled`, `unlabeled`)
- `pull_request_review` (`submitted`)
- `issue_comment` (`created`)

---

## 2. Evaluation Logic and Workflow

```mermaid
flowchart TD
    PR["Incoming PR Event"] --> Merged{"Is Merged?"}
    Merged -- Yes --> Complete["Workflow: Complete"]
    Merged -- No --> WIP{"Draft OR Title starts with WIP?"}
    WIP -- Yes --> InDev["Workflow: In Dev"]
    WIP -- No --> Blocked{"Rebuild Impact Blocked<br>OR Maintainer Block?"}
    Blocked -- Yes --> IsBlocked["Workflow: Blocked"]
    Blocked -- No --> Review{"Captain Comment OR Review?"}
    Review -- Yes --> InReview["Workflow: In Review"]
    Review -- No --> Ready["Workflow: Ready For Review"]
```

### Shiny New PR Governance (`ShinyNewPrModule`)
- **Trigger**: When a PR is opened, `ShinyNewPrModule` applies `Status: New`.
- **Removal**: Removed upon the first human review or comment from someone other than the author and automated bot accounts.

---

## 3. Managed Labels and Mutex Group

### Workflow Lifecycle (Single-Choice Mutex)
- `Workflow: In Dev`
- `Workflow: Ready For Review`
- `Workflow: In Review`
- `Workflow: Blocked`
- `Workflow: Complete`

### Notification & Status Indicators
- `Status: New`: Managed by `ShinyNewPrModule`.
- `Commits: Updated`: Applied when new commits are pushed to a PR currently in `Workflow: Blocked`. Removed when a Captain responds.

---

## 4. Configuration Options

`WorkflowStateModule` operates using core repository metadata and `GitHubApp:TeamName`. `ShinyNewPrModule` uses `BotDetectionOptions`.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `GitHubApp:TeamName` | `string` | Required (no code default; example: `captains`) | Team slug whose members are recognized as repository maintainers / Captains. |
| `BotDetection:Whitelist` | `string[]` | Six built-in accounts (see below) | List of account usernames ignored during first-response triage evaluations. |

Default bot whitelist:
`["github-actions", "github-actions[bot]", "abaci-bot", "abaci-bot[bot]", "dependabot", "dependabot[bot]"]`

A deployment may add more accounts (for example an update bot) in its own `appsettings.json`; those additions are not code defaults.

---

## 5. Failure Handling and Fallback Semantics

- **Team Membership Lookup Failure**: `GitHubService.GetTeamMembersAsync` has no catch. An API error propagates out of the module, and `PullRequestPipeline` aborts the whole pipeline fail-fast for that event; no label writes are attempted.
- **Fail-Fast Label Absence**: If any of the workflow labels do not exist on the target repository, the request aborts fail-fast to alert operators.

---

## 6. Minimal Configuration Example

```json
{
  "GitHubApp": {
    "TeamName": "captains"
  },
  "BotDetection": {
    "Whitelist": [ "github-actions[bot]", "abaci-bot[bot]" ]
  }
}
```

---

## 7. Relevant Test Suites

- `tests/abaci-bot.Tests/PipelineAndModulesTests.cs`
- `tests/abaci-bot.Tests/ShinyNewPrModuleTests.cs`
- `tests/abaci-bot.Tests/GitHubWebhookProcessorTests.cs`
