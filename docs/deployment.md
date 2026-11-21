# Deployment Guide

This guide covers production deployment options, host prerequisites, containerization, systemd service configuration, reverse proxy setup, and operational runbooks for `abaci-bot`.

---

## 1. System Requirements

`abaci-bot` requires native binary inspection tools for binary ABI compliance verification.

| Component | Minimum Version | Required Tools / Libraries | Notes |
|---|---|---|---|
| **Operating System** | Linux (Fedora 45+ recommended) | Kernel 6.x+ | Fedora 45 includes native RPM v4 and v6 (07070X indexed format) extraction support. |
| **ABI Tools** | Libabigail 2.6+ | `abipkgdiff`, `libabigail` | Executes binary shared library comparison. |
| **Package Tools** | RPM 4.20+ / 6.0+ | `rpm`, `rpm2cpio`, `cpio` | Required to unpack payload archives from RPM packages. |
| **Runtime** | .NET 10 LTS | `libicu`, `tzdata`, `libssl`, `libz` | Required for .NET 10 execution and IANA timezone resolution (`MilestoneSync:TimeZoneId`). |

---

## 2. Docker & Docker Compose Deployment (Recommended)

The recommended production deployment runs the official OCI container image based on Fedora 45.

### Container Image
```
ghcr.io/openruyi-project/abaci-bot:latest
```

### Directory Structure
```
/opt/abaci-bot/
├── appsettings.json
└── docker-compose.yml
```

### `docker-compose.yml`
```yaml
services:
  abaci-bot:
    image: ghcr.io/openruyi-project/abaci-bot:latest
    container_name: abaci-bot
    restart: unless-stopped
    ports:
      - "127.0.0.1:3456:3456"
    volumes:
      - ./appsettings.json:/app/appsettings.json:ro
      - ./state:/state
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_HTTP_PORTS=3456
      - BotState__StateDirectory=/state
      - ABACI_IMAGE_DIGEST=${ABACI_IMAGE_DIGEST:-unknown}
      # Optional environment variable overrides:
      # - GitHubApp__AppId=123456
      # - GitHubApp__InstallationId=78901234
      # - GitHubApp__WebhookSecret=${WEBHOOK_SECRET}
      # - GitHubApp__PrivateKey="-----BEGIN RSA PRIVATE KEY-----\n...\n-----END RSA PRIVATE KEY-----"
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:3456/health"]
      interval: 30s
      timeout: 5s
      start_period: 10s
      retries: 3
    security_opt:
      - no-new-privileges:true
    read_only: false
    user: "1654:1654"
```

### Starting the Service
Create and grant ownership of the persistent state directory to container UID/GID `1654:1654` before starting Docker Compose (otherwise Docker creates `./state` as `root` and startup will fail fast because uid `1654` cannot write to `/state`):

```bash
mkdir -p state
sudo chown 1654:1654 state

# Download the release deployment-manifest.env artifact (contains ABACI_IMAGE_DIGEST)
gh run download -R openRuyi-Project/abaci-bot --name deployment-manifest --dir .
docker compose --env-file deployment-manifest.env up -d
docker compose logs -f
```

When building a container image locally instead of pulling from GHCR, use the wrapper script so `GIT_COMMIT` and `GIT_DIRTY` are injected automatically:

```bash
./scripts/docker-build.sh -t abaci-bot:local
```

---

## 3. Native systemd Deployment

For bare-metal or VM deployments running Fedora 45 without Docker.

### Install Host Dependencies
```bash
sudo dnf install -y --setopt=install_weak_deps=False \
    curl \
    libabigail \
    rpm \
    cpio \
    libicu
```

### Create Service User
```bash
sudo useradd -r -s /sbin/nologin -d /opt/abaci-bot abaci
sudo mkdir -p /opt/abaci-bot /var/log/abaci-bot /var/cache/abaci-bot
sudo chown -R abaci:abaci /opt/abaci-bot /var/log/abaci-bot /var/cache/abaci-bot
```

### systemd Service Unit (`/etc/systemd/system/abaci-bot.service`)
```ini
[Unit]
Description=abaci-bot GitHub Governance Daemon
After=network.target network-online.target
Wants=network-online.target

[Service]
Type=simple
User=abaci
Group=abaci
WorkingDirectory=/opt/abaci-bot
ExecStart=/opt/abaci-bot/abaci-bot
Restart=always
RestartSec=10
KillMode=process
LimitNOFILE=65536

# Security sandbox hardening
ProtectSystem=full
ProtectHome=true
NoNewPrivileges=true
PrivateTmp=true
StateDirectory=abaci-bot

# Environment Configuration
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_HTTP_PORTS=3456
Environment=BotState__StateDirectory=/var/lib/abaci-bot
EnvironmentFile=-/etc/default/abaci-bot

[Install]
WantedBy=multi-user.target
```

### Reload and Enable Service
```bash
sudo systemctl daemon-reload
sudo systemctl enable --now abaci-bot
sudo systemctl status abaci-bot
```

---

## 4. Nginx Reverse Proxy & TLS Configuration

Place `abaci-bot` behind a reverse proxy to terminate TLS and forward GitHub webhook events.

### `/etc/nginx/conf.d/abaci-bot.conf`
```nginx
server {
    listen 443 ssl http2;
    server_name bot.openruyi.cn;

    ssl_certificate /etc/letsencrypt/live/bot.openruyi.cn/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/bot.openruyi.cn/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;

    client_max_body_size 25M;
    client_body_buffer_size 1M;

    location / {
        proxy_pass http://127.0.0.1:3456;
        proxy_http_version 1.1;

        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Webhook timeout configuration
        proxy_connect_timeout 10s;
        proxy_send_timeout 60s;
        proxy_read_timeout 60s;
    }

    location = /health {
        proxy_pass http://127.0.0.1:3456/health;
        access_log off;
        add_header Cache-Control "no-store, no-cache, must-revalidate, proxy-revalidate, max-age=0";
    }
}
```

---

## 5. GitHub App Setup from Scratch

The bot authenticates as a GitHub App, not as a personal account. Complete these steps
once per organization.

### 5.1 Create the App

1. Open **Organization Settings → Developer settings → GitHub Apps → New GitHub App**.
2. Set a name and homepage URL.
3. Set **Webhook URL** to `https://<your-host>/api/webhook` and keep **Active** checked.
   The host must be reachable over public HTTPS.
4. Set **Webhook secret** to a random value:
   ```bash
   openssl rand -hex 32
   ```
   Store it; it goes into `GitHubApp:WebhookSecret` and is used for HMAC verification.
5. Leave **SSL verification** enabled.

### 5.2 Repository and organization permissions

| Scope | Permission | Why |
|---|---|---|
| Repository | **Actions: Read-only** | List and download workflow artifacts for ABI comparison. |
| Repository | **Contents: Read-only** | Read changed specs, patches, and license expressions. |
| Repository | **Issues: Read & write** | Apply labels, post comments, file CVE/update issues. |
| Repository | **Pull requests: Read & write** | Apply labels, post comments, read review state. |
| Repository | **Metadata: Read-only** | Mandatory for every GitHub App. |
| Organization | **Members: Read-only** | Resolve the `captains` team for review transitions. |

`Contents: write` is only needed if you want the App itself to merge pull requests; the
bot does not. No `Workflows` or `Checks` permission is required while community
governance (`ConversationGuard`) remains disabled.

> [!NOTE]
> **Elevated permissions for `ConversationGuard`:** When `ConversationGuard:Enabled`
> is set to `true`, authorizing `@<bot> lock`, `@<bot> unlock`, and `@<bot> hide` against
> authoritative repository collaborator roles (`triage`/`write`+) requires granting the
> GitHub App **Repository -> Administration: Read-only** (or **Contents: Read & write**).
> Additionally enabling `ConversationGuard:ArchiveEnabled=true` (`@<bot> archive`) requires
> **Repository -> Administration: Read & write** on the GitHub App (or a `triage`+
> `PackageUpdate:BotPat`), because standard minimal App tokens receive `FORBIDDEN` from
> GitHub's `archivePullRequest` GraphQL mutation.
> On startup (including when `DryRun` is `true`), `ConversationGuardPermissionProbe`
> verifies collaborator endpoint access (and archive permission when `ArchiveEnabled=true`)
> on every repository in `ConversationGuard:RepositoryAllowList` and aborts startup if any
> required permission is missing, ensuring dry-run simulation and archive readiness match
> production credentials. Keep `ConversationGuard:Enabled` set to `false` to preserve the
> baseline minimal permission set.

### 5.3 Event subscriptions

Subscribe to exactly these events:

- **Issue comments**
- **Pull requests**
- **Pull request reviews**
- **Pull request review comments**
- **Workflow runs**

`Issues`, `Workflow jobs`, and `Pull request review threads` are not handled by the
current code; subscribing to them only produces deliveries that return 200 and are
discarded.

### 5.4 Generate the private key

In the App settings, click **Generate a private key** and download the `.pem` file.
Treat it as a secret. The bot reads it from `GitHubApp:PrivateKey`; literal `\n`
sequences in a JSON string are converted to newlines at startup.

### 5.5 Install the App and get the installation ID

1. Click **Install App** and select the target organization.
2. Choose **All repositories** or a selected repository list.
3. After installation, the browser URL ends with `/settings/installations/<number>`.
   That number is `GitHubApp:InstallationId`.

If the App is reinstalled, the installation ID can change; update the configuration.

### 5.6 Create the captain team

Create an organization team named `captains` (or set `GitHubApp:TeamName` to your slug)
and add the maintainers who should move pull requests into review. The team must be
visible to the App's `Members` permission; keep it non-secret.

### 5.7 Fill the configuration

| Setting | Where to find it |
|---|---|
| `GitHubApp:AppId` | App settings page, **App ID**. |
| `GitHubApp:InstallationId` | Installation URL, `/settings/installations/<id>`. |
| `GitHubApp:PrivateKey` | Contents of the generated `.pem` file. |
| `GitHubApp:WebhookSecret` | The secret from step 5.1. |
| `GitHubApp:TeamName` | The team slug, for example `captains`. |
| `Kestrel:Endpoints:Http:Url` | `http://0.0.0.0:3456` inside the container. |

Deployments adapting `abaci-bot` to other RPM-based Linux distributions should review [Multi-Distribution Configuration Boundary](configuration.md#8-multi-distribution-configuration-boundary) for timezone and endpoint defaults to override in `appsettings.json`.

### 5.8 Create labels and verify deliveries

1. Create every managed label before the first webhook; see
   [labels.md](labels.md#3-creating-labels-upstream).
2. In the App settings, open **Advanced → Recent Deliveries** and use **Redeliver**.
   A correctly configured deployment returns 200.
3. Check `http://<your-host>/health` and open a draft pull request to confirm the
   `Status: New` and workflow labels.

---

## 6. Health Checks & Verification

Verify that the service is running and all governance modules are healthy:

```bash
curl -f http://localhost:3456/health
```

Expected default JSON response (liveness + immutable build/artifact identity):
```json
{
  "status": "healthy",
  "timestamp": "2026-11-20T16:28:03.1146402Z",
  "version": "1.0.0",
  "commit": "0a5160c",
  "imageDigest": "unknown",
  "buildTime": "2026-11-20T16:20:00Z",
  "deployment": "container"
}
```
Pass `?verbose=true` (`curl -f "http://localhost:3456/health?verbose=true"`) for operational diagnostics including `startedAt`, `uptimeSeconds`, `capabilities` (`abiEngine`, `conversationGuardPermission`), `modules` (non-secret enabled/dryRun flags), `runtime` (`lastError`, `lastErrorAt`), and `sources` (per-source snapshots for `Lookout` and `RustCrates`: `status` as `healthy` / `degraded` / `failed` / `disabled`, `lastAttemptAt`, `lastSuccessAt`, `lastCandidateCount`, `consecutiveFailures`, and sanitized `lastError`). External upstream source outages do **not** cause the default liveness probe `GET /health` to return non-200 status (so containers are not restart-looped due to third-party API downtime).
Use `./scripts/docker-build.sh` (or `./scripts/docker-build.ps1`) when building the Docker image locally so `GIT_COMMIT` and `GIT_DIRTY` are detected automatically; native `dotnet build`/`dotnet publish` and the release workflow (`github.sha` + `git status --porcelain`) populate `commit` automatically (appending `-dirty` when the working tree has uncommitted changes). Because the OCI registry manifest digest (`sha256:...`) does not exist until after a container image is built and pushed, the release workflow captures `build-and-push.outputs.digest` into a `deployment-manifest.env` artifact (`ABACI_IMAGE_DIGEST`), which you fetch with `gh run download -R openRuyi-Project/abaci-bot --name deployment-manifest --dir .` and pass via `docker compose --env-file deployment-manifest.env up -d` (defaulting to `"unknown"` when omitted). Neither `/health` nor `/health?verbose=true` exposes filesystem paths, environment variables, or secrets.

In verbose mode, `capabilities.abiEngine` reports `native`, `managed`, `unavailable`, or
`disabled`.

> [!IMPORTANT]
> **CDN / Cloudflare Cache Bypass**: Public reverse proxies or CDNs (such as Cloudflare) frequently cache HTTP responses by default (observed cache duration up to `max-age=10800`). Ensure Cloudflare Cache Rules or proxy settings bypass caching for `/health` so monitoring systems receive real-time operational status rather than stale cached responses.

If `abiEngine` reports `managed`, the host lacks native Libabigail or RPM extraction tools. Refer to [Troubleshooting](#7-troubleshooting--rollback).

---

## 7. Troubleshooting & Rollback

### Startup Gate Failure
If startup terminates with `InvalidOperationException`:
- Verify that `abipkgdiff`, `rpm2cpio`, and `cpio` are installed and available in `$PATH`.
- Check permissions on `/tmp` or `TempExtractionDirectory`.
- Ensure no other active `abaci-bot` process is already holding the exclusive OS lock on `BotState:StateDirectory/bot-state.lock` (single-writer lock; stop the existing instance before starting a replacement on the same state volume).
- If running under Docker, ensure the image is built from Fedora 45 (`registry.fedoraproject.org/fedora:45`).

### Rollback Procedure
1. Revert to the previous container image tag:
   ```bash
   sed -i 's/abaci-bot:v2.2/abaci-bot:v2.1/g' docker-compose.yml
   docker compose up -d
   ```
2. For systemd deployments, stop the service, swap the binary directory to backup, and restart:
   ```bash
   sudo systemctl stop abaci-bot
   sudo cp -r /opt/abaci-bot.bak/* /opt/abaci-bot/
   sudo systemctl start abaci-bot
   ```
