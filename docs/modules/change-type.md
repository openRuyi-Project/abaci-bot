# Change Type Classification Module

Classifies pull requests into functional categories based on changed file extensions and directory paths.

---

## 1. Purpose and Trigger Events

Helps maintainers quickly distinguish packaging updates from documentation improvements, infrastructure edits, or frontend changes.

### Trigger Events
- `pull_request` (`opened`, `synchronize`, `reopened`, `ready_for_review`, `converted_to_draft`)

---

## 2. Evaluation Logic and Workflow

```mermaid
flowchart TD
    Files["Changed files"] --> Pick{"First applicable strategy<br/>(priority, then registration order)"}
    Pick -- "repo in PackageRepositories" --> Pkg{"Contains .spec, .patch, SPECS/ or SOURCES/?"}
    Pkg -- Yes --> TagPkg["Type: Packaging"]
    Pkg -- No --> PkgDocs{"Are ALL changed files documentation?"}
    PkgDocs -- Yes --> TagDoc["Type: Documentation"]
    PkgDocs -- No --> PkgCi{"Touches .github/ workflows?"}
    PkgCi -- Yes --> TagInfra["Type: Infrastructure"]
    Pick -- "repo in HomepageRepositories" --> Fe{"Contains frontend extensions?"}
    Fe -- Yes --> TagFe["Type: Frontend"]
    Fe -- No --> HomeDocs{"Are ALL changed files documentation?"}
    HomeDocs -- Yes --> TagDoc2["Type: Documentation"]
    Pick -- "any other repository" --> DefDocs{"Are ALL changed files documentation?"}
    DefDocs -- Yes --> TagDoc3["Type: Documentation"]
    DefDocs -- No --> DefCi{"Touches .github/ workflows?"}
    DefCi -- Yes --> TagInfra2["Type: Infrastructure"]
    TagPkg --> Reconcile["Reconcile the four managed Type labels"]
    TagDoc --> Reconcile
    TagDoc2 --> Reconcile
    TagDoc3 --> Reconcile
    TagFe --> Reconcile
    TagInfra --> Reconcile
    TagInfra2 --> Reconcile
```

Exactly one strategy runs: the first in priority order whose `AppliesTo` accepts the repository, with `DefaultChangeTypeStrategy` (priority 1000) as the catch-all. `OpenRuyiPackageRepoStrategy` and `HomepageRepoStrategy` share priority 10, so registration order decides if a repository appears in both lists. A repository that is neither a package repository nor a homepage repository only ever receives `Type: Documentation` or `Type: Infrastructure`: a `.spec` change there does not produce `Type: Packaging`.

The selected strategy is a pure classifier. It returns the labels implied by the file list, and the module reconciles the four managed labels against that result, which is what retires a stale classification and what empties the set on a zero-file diff.

---

## 3. Managed Labels (Single-Choice, Module-Owned)

### Change Type Labels
- `Type: Packaging`: Modifies RPM spec files, patches, or package source directories.
- `Type: Documentation`: Modifies markdown, text, or documentation directories exclusively.
- `Type: Infrastructure`: Modifies CI workflows, actions, or tooling without spec changes.
- `Type: Frontend`: Modifies web UI files. Active **only** on repositories designated in `HomepageRepositories`.

---

## 4. Configuration Options

Configured under the `ChangeTypeClassification` JSON section:

| Parameter | Type | Default | Description |
|---|---|---|---|
| `DocumentationExtensions` | `string[]` | `[".md", ".txt", ".rst"]` | File extensions recognized as documentation. |
| `DocumentationDirectories` | `string[]` | `["docs/", "doc/"]` | Directory paths recognized as documentation. |
| `PackagingExtensions` | `string[]` | `[".spec", ".patch", ".diff"]` | File extensions indicating RPM packaging changes. |
| `PackagingDirectories` | `string[]` | `["SPECS/", "SOURCES/"]` | Directory paths indicating RPM packaging changes. |
| `FrontendExtensions` | `string[]` | `[".vue", ".ts", ".js", ...]` | File extensions indicating frontend assets. |
| `PackageRepositories` | `string[]` | `["openRuyi"]` | Repositories subject to standard RPM packaging classification. |
| `HomepageRepositories` | `string[]` | `["homepage"]` | Repositories where frontend rules apply instead of packaging rules. |

---

## 5. Failure Handling and Fallback Semantics

- **Mixed Diffs**: If a PR contains both spec files and documentation files, `Type: Packaging` takes precedence. `Type: Documentation` is applied only when **all** changed files are documentation.
- **Repository Isolation**: `Type: Frontend` is never applied to the main package repository (`openRuyi`).
- **Zero-File Diff**: When the branch tree becomes identical to the base, no change type applies and every managed `Type:` label is removed. An empty file list is authoritative because the GitHub file listing raises on API failure instead of returning empty.
- **Reclassification**: The module reconciles the full managed set, so a diff that moves from packaging to documentation removes `Type: Packaging` in the same run instead of leaving both labels. `LabelMutexEngine` does not arbitrate this group, so the module is responsible for the exclusivity.
- **Managed Set Only**: a strategy result outside the four managed labels (including `Type: Build Failure`, which another component owns) is ignored. A managed `Type:` label applied by hand is not sticky: the next `opened`, `synchronize`, `reopened`, `ready_for_review`, or `converted_to_draft` event reconciles it away when the current diff does not imply it.

---

## 6. Minimal Configuration Example

```json
{
  "ChangeTypeClassification": {
    "PackageRepositories": [ "openRuyi" ],
    "HomepageRepositories": [ "homepage" ]
  }
}
```

---

## 7. Relevant Test Suites

- `tests/abaci-bot.Tests/ChangeTypeClassificationModuleTests.cs`
