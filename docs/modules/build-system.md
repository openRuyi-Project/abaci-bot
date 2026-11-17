# Build System Analysis Module

Inspects changed RPM `.spec` files to detect and tag the declared build system (`autotools`, `cmake`, `meson`, `rust`, `golang`, etc.), and tags workflow modifications with `CI`.

---

## 1. Purpose and Trigger Events

RPM spec files declare build tools and compilation workflows. This module identifies the underlying build toolchain to assist maintainers and route review assignments.

### Trigger Events
- `pull_request` (`opened`, `synchronize`)

---

## 2. Evaluation Logic and Workflow

1. **CI Detection**: If any changed file is under `.github/workflows/` or matches workflow patterns, applies the `CI` label.
2. **Spec BuildSystem Inspection**:
   - Parses `BuildSystem:` header lines in changed `.spec` files.
   - Matches known build system identifiers:
     - `autotools` -> `BuildSystem: autotools`
     - `cmake` -> `BuildSystem: cmake`
     - `golang` / `golangmodule` -> `BuildSystem: golang` / `golangmodule`
     - `rust` / `rustcrate` -> `BuildSystem: rust` / `rustcrate`
     - `meson` -> `BuildSystem: meson`
     - `pyproject` -> `BuildSystem: pyproject`
     - Other or unrecognized -> `BuildSystem: misc`

---

## 3. Managed Labels

- `CI`
- `BuildSystem: autotools`
- `BuildSystem: cmake`
- `BuildSystem: golangmodule`
- `BuildSystem: golang`
- `BuildSystem: rustcrate`
- `BuildSystem: rust`
- `BuildSystem: meson`
- `BuildSystem: pyproject`
- `BuildSystem: misc`

---

## 4. Configuration Options

This module operates without external configuration keys. It parses files retrieved via GitHub pull request file endpoints.

---

## 5. Failure Handling and Fallback Semantics

- **Missing BuildSystem Header**: If a spec file builds via custom shell scripts or does not declare a `BuildSystem:` directive, `BuildSystem: misc` is applied.
- **Spec Deletion**: If a spec file is removed in the PR, it is ignored during build system parsing.

---

## 6. Minimal Configuration Example

No configuration entries required. Module runs by default in the pipeline.

---

## 7. Relevant Test Suites

- `tests/abaci-bot.Tests/BuildSystemAnalysisModuleTests.cs`
