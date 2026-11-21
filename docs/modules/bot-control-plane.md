# Bot Control Plane (`BotControlPlaneModule`)

**Priority:** `96` (`issue_comment.created`)

`BotControlPlaneModule` provides single-comment runtime controls for automated package updates and per-user response language preferences, using GitHub mention commands addressed to the bot's live GitHub handle (resolved automatically from the GitHub API via `BotPat` or GitHub App slug).

---

## 1. Supported Mention Commands

| Command | Allowed Callers | Behavior |
|---|---|---|
| `@<bot> ignore <package> [reason]` | Collaborators (`triage`+) | Persists a runtime ignore entry for `<package>` in `bot-state.json` (`owner/repo`). `PackageUpdateService` skips the package on subsequent sweeps without requiring a spec commit. |
| `@<bot> unignore <package>` | Collaborators (`triage`+) | Removes `<package>` from the runtime ignore list in `bot-state.json` so automated updates can resume. |
| `@<bot> status [package]` | Any commenter | Reports runtime ignore status, in-spec `#!NoAutoUpdate` directive status, and persistent failure cooldown state. When `<package>` is omitted on an `autoupdate/<pkg>` PR, the package name is inferred from the head branch; otherwise lists all runtime-ignored packages in the repository. |
| `@<bot> lang <en\|zh\|reset>` | Any commenter (unless disabled by `RepositoryForcedLocales`) | Sets or clears the caller's personal response language preference (`en` or `zh`). Disabled when the repository configures `BotState:RepositoryForcedLocales`. |

---

## 2. Language Resolution & Retention Policy

- **Direct command replies** (`@<bot> ...` responses in `BotControlPlaneService` and `ConversationGuardService`) resolve the locale for the **command caller (`senderLogin`)**:
  1. `BotState:RepositoryForcedLocales["owner/repo"]` (if set, disables `@<bot> lang`)
  2. Caller preference (`UserLocales[senderLogin]` in `bot-state.json`)
  3. `BotState:RepositoryDefaultLocales["owner/repo"]`
  4. `BotState:DefaultLocale` (default: `"en"`)
- **PR-level governance review comments** (`PullRequestPipeline` consolidated reports) resolve the locale for the **PR author (`AuthorLogin`)** using the same precedence chain.
- **Autonomous bot pull requests** (`PackageUpdateService`) resolve `RepositoryForcedLocales -> RepositoryDefaultLocales -> DefaultLocale` without mixing languages.
- **Retention**: Inactive user language preferences older than `BotState:LocaleRetentionDays` (default: `180` days) are pruned automatically on startup (`JsonBotStateStore` initialization) and whenever preferences are updated.

---

## 3. Unified State Storage & Configuration (`BotState`)

Configured under the `BotState` JSON section:

| Parameter | Type | Default | Description |
|---|---|---|---|
| `StateDirectory` | `string` | `""` | Absolute directory path storing `bot-state.json` (runtime ignores, failure cooldowns, user locales) and default `lookout-first-seen.json` (relative paths fail startup validation). When empty, resolves in order: `LocalApplicationData/abaci-bot` -> `$XDG_DATA_HOME/abaci-bot` -> `$HOME/.local/share/abaci-bot` -> ephemeral temp directory (logging a warning). Mount this directory as a persistent volume in container deployments. |
| `LocaleRetentionDays` | `int` | `180` | Days to retain inactive user language preferences before pruning (`>= 1`). |
| `DefaultLocale` | `string` | `"en"` | Global fallback locale (`"en"` or `"zh"`). |
| `RepositoryForcedLocales` | `object` | `{}` | Map of `"owner/repo"` to `"en"` or `"zh"`. Locks the repository language and disables `@<bot> lang`. |
| `RepositoryDefaultLocales` | `object` | `{}` | Map of `"owner/repo"` to `"en"` or `"zh"`. Fallback when the user has not chosen a personal preference. |
| `EphemeralReplyDeleteMinutes` | `int` | `5` | Delay in minutes (`0..1440`) before ephemeral usage and rejection replies are processed by `EphemeralReplyService` (author confirmation prompts use `AuthorLockConfirmTimeoutMinutes`). Setting `0` disables automatic cleanup. |
| `EphemeralReplyAction` | `enum` | `"Delete"` | Action taken on expired ephemeral replies: `"Delete"` (deletes the comment) or `"Minimize"` (minimizes as `OUTDATED`). |

---

## 4. Failure Handling

- **Atomic Persistence**: `JsonBotStateStore` writes to a temporary file and atomically replaces `bot-state.json`.
- **Corrupt State Fallback**: Invalid JSON logs a warning and falls back to an empty in-memory state instead of crashing startup.
- **Fail-Closed Authorization**: `ignore` and `unignore` verify `triage`+ repository permission via `IRepositoryPermissionService`; lookup errors deny the mutation.
