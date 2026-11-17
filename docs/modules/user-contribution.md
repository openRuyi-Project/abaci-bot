# Contributor Classification Module

Categorizes pull requests by the commit author's email domain so maintainers can
filter student and external community contributions.

---

## 1. Purpose and Trigger Events

- **Priority**: 20.
- **Trigger events**: `pull_request` on `opened`, `synchronize`, `reopened`,
  `converted_to_draft`, and `ready_for_review`.
- **Repository scope**: every repository the App serves; no per-repository allowlist.

## 2. Classification Logic

The module reads the author email of the pull request's commits.

| Author email | Result |
|---|---|
| Domain `iscas.ac.cn` (official staff) | No community label; existing `Community:` labels are removed. |
| Ends with `.oerv@isrc.iscas.ac.cn`, `.or@isrc.iscas.ac.cn`, or `.riscv@isrc.iscas.ac.cn` | `Community: Student contribution` |
| Any other parseable domain | `Community: Contribution` |
| Missing or unparseable email | Existing `Community:` labels are removed |

If the email lookup fails because of a transient API or network error, the module
leaves the existing labels untouched instead of guessing.

## 3. Managed Labels and Mutex Group

- `Community: Student contribution`
- `Community: Contribution`

The two labels form a single-choice mutex group in `LabelMutexEngine`; the module
reconciles its complete managed set, so changing the author email removes the stale
classification.

## 4. Configuration Options

This module has no options of its own. It uses the GitHub App credentials and does
not read a configuration section.

## 5. Failure Behaviour

- Email lookup failure: labels are preserved and no exception escapes the module.
- Unknown or missing domain: both community labels are removed.
- Staff addresses never receive a community label.

## 6. Tests

- `tests/abaci-bot.Tests/PipelineAndModulesTests.cs` covers the classification and
  reconciliation paths.
