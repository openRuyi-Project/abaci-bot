# AI Assistance Module

Detects author disclosures regarding AI assistance in pull request descriptions.

---

## 1. Purpose and Trigger Events

Supports community transparency policies regarding machine-generated or AI-assisted contributions.

### Trigger Events
- `pull_request` (`opened`, `edited`)

---

## 2. Evaluation Logic and Workflow

The module scans the pull request body text for checked markdown checkbox patterns corresponding to AI assistance declarations:
- `[x] ... AI ...`
- `[X] ... AI ...`

If a checked declaration is present:
- Adds `AI Assistance`.

If the PR description is subsequently edited to uncheck or remove the declaration:
- Automatically schedules removal of `AI Assistance`.

---

## 3. Managed Labels

- `AI Assistance`

---

## 4. Configuration Options

This module operates without configuration keys.

---

## 5. Failure Handling and Fallback Semantics

- **Empty PR Body**: If the description is empty or null, the module takes no action.
- **Dynamic Uncheck**: Subsequent edits that remove the checkbox safely remove the label.

---

## 6. Minimal Configuration Example

No configuration entries required.

---

## 7. Relevant Test Suites

- `tests/abaci-bot.Tests/AiAssistanceModuleTests.cs`
