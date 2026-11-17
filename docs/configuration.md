# Configuration Model and Guidelines

This page documents the configuration model, loading hierarchy, comment conventions, secret handling, and validation rules for `abaci-bot`.

---

## 1. Configuration Loading Hierarchy

`abaci-bot` uses standard ASP.NET Core configuration providers. Settings are merged in the following order of precedence (last applied wins):

1. **`appsettings.json`** (Base configuration file)
2. **`appsettings.{Environment}.json`** (Environment-specific overrides, e.g. `appsettings.Production.json`)
3. **Environment Variables** (Hierarchical keys delimited with double underscores `__`)
4. **Command Line Arguments**

---

## 2. Whole-Line Comments Convention

`abaci-bot` supports comments in JSON configuration files (`appsettings.json` and `appsettings.demo.jsonc`).

### Rules:
- **Whole-line comments only**: Always place `//` on its own line above the setting.
- **No inline/trailing comments**: Do not place `//` at the end of a line containing JSON data (to avoid syntax highlighting discrepancies and diff noise).
- **No secrets in comments**: Never record tokens, private URLs, or real credentials in comments.

### Comment content:

Each setting comment answers four questions, in this order:

1. **Unit or format** — `days`, `hours`, `milliseconds`, `bytes`, `"owner/repo"`, `absolute URL`, `label name`, ...
2. **Range or allowed values** — `>= 1`, `0 or 1`, `"Rolling" | "LTS" | null`, ...
3. **Default and where it comes from** — the value plus the owning `Options/*.cs` class, or the startup code for `GitHubApp`/`Kestrel`.
4. **Behavior when empty, zero, false, or null** — what the bot does instead.

Section headers state the defaults source once; individual settings then only repeat the
default value. Comments that merely restate the property name are not useful and should
be replaced with the four facts above.

### Example:
```jsonc
// Defaults source: Options/StaleLifecycleOptions.cs
"StaleLifecycle": {
  // Unit: days. Range: > 0. Default: 30.
  // Only evaluated when Enabled=true and DryRun=false.
  "StaleDays": 30,

  // Default: true.
  // true: candidates are only logged; false: labels, comments, and closes are applied.
  "DryRun": true
}
```

ASP.NET Core's configuration builder natively reads JSON files with comments and trailing commas enabled (`JsonCommentHandling.Skip` and `AllowTrailingCommas = true`).

---

## 3. Required Settings Checklist

Before launching `abaci-bot`, the following settings **must** be populated:

| Key | Type | Description | Production Source |
|---|---|---|---|
| `GitHubApp:AppId` | `int` | Unique GitHub App ID. | Environment variable `GitHubApp__AppId` |
| `GitHubApp:InstallationId` | `int` | GitHub App Installation ID on target organization/repo. | Environment variable `GitHubApp__InstallationId` |
| `GitHubApp:PrivateKey` | `string` | RSA Private Key in PEM format. | Secret mount / environment variable `GitHubApp__PrivateKey` |
| `GitHubApp:WebhookSecret` | `string` | Webhook verification secret. | Environment variable `GitHubApp__WebhookSecret` |
| `GitHubApp:TeamName` | `string` | GitHub team slug for triage maintainers (default: `captains`). | `appsettings.json` or environment variable |

If any of these required values are missing or zero, the service terminates immediately on startup.

---

## 4. Environment Variable Mapping

Any setting in `appsettings.json` can be overridden using environment variables by replacing `:` or nested object keys with double underscores `__`.

### Examples:
| Setting | Equivalent Environment Variable |
|---|---|
| `GitHubApp.AppId` | `GitHubApp__AppId=123456` |
| `GitHubApp.WebhookSecret` | `GitHubApp__WebhookSecret="secret_token"` |
| `AbiCompliance.AllowManagedFallback` | `AbiCompliance__AllowManagedFallback=false` |
| `StaleLifecycle.Enabled` | `StaleLifecycle__Enabled=true` |
| `StaleLifecycle.ExemptLabels[0]` | `StaleLifecycle__ExemptLabels__0="Status: stale-exempt"` |

---

## 5. Secret Management

Do not commit private keys or webhook secrets to version control.

### Recommended Approaches:
1. **Mounted Configuration File**: Mount a production `appsettings.json` with restricted permissions (`chmod 600 appsettings.json`):
   ```yaml
   volumes:
     - ./appsettings.json:/app/appsettings.json:ro
   ```
2. **Environment Variable Injection**: Provide secrets via environment variables or an `.env` file with restricted read permissions (`chmod 600 .env`):
   ```bash
   GitHubApp__WebhookSecret=your_webhook_secret_here
   GitHubApp__PrivateKey="-----BEGIN RSA PRIVATE KEY-----\nMIIE...\n-----END RSA PRIVATE KEY-----"
   ```
   Note: The runtime automatically converts literal `\n` in `GitHubApp:PrivateKey` to standard newline characters.

---

## 6. Verifying Effective Configuration

Verify active runtime settings through the health endpoint:
```bash
curl http://localhost:3456/health
```

The response confirms:
- Service operational status (`Healthy`)
- Active ABI engine tier (`native` vs `managed`)
- RPM v6 extraction capability (`true` / `false`)
- Count of loaded governance modules
