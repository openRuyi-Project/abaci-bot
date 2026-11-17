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
| **Runtime** | .NET 10 LTS | `libicu`, `libssl`, `libz` | Required for self-contained or framework-dependent .NET 10 execution. |

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
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_HTTP_PORTS=3456
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
```bash
docker compose up -d
docker compose logs -f
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

# Environment Configuration
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_HTTP_PORTS=3456
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

## 5. GitHub App Permission Checklist

Configure the following permissions and events in your GitHub App settings (`https://github.com/settings/apps/<app-name>`):

### Repository Permissions
- **Pull Requests**: Read & Write (labeling, comments, review state)
- **Issues**: Read & Write (stale lifecycle management, CVE tracking issues)
- **Actions**: Read-only (downloading workflow run RPM artifacts)
- **Checks**: Read-only (reading workflow execution outcomes)
- **Contents**: Read-only (inspecting changed spec files, patches, and license expressions)
- **Metadata**: Read-only (repository information)

### Organization Permissions
- **Members**: Read-only (verifying team membership for maintainers / Captains)

### Webhook Event Subscriptions
- `Pull request` (`opened`, `closed`, `reopened`, `synchronize`, `ready_for_review`, `converted_to_draft`)
- `Pull request review` (`submitted`, `dismissed`)
- `Pull request review comment` (`created`)
- `Issue comment` (`created`)
- `Workflow run` (`completed`)

---

## 6. Health Checks & Verification

Verify that the service is running and all governance modules are healthy:

```bash
curl -f http://localhost:3456/health
```

Expected JSON response:
```json
{
  "status": "Healthy",
  "engineTier": "native",
  "nativeSupportsV6": true,
  "modulesLoaded": 10
}
```

> [!IMPORTANT]
> **CDN / Cloudflare Cache Bypass**: Public reverse proxies or CDNs (such as Cloudflare) frequently cache HTTP responses by default (observed cache duration up to `max-age=10800`). Ensure Cloudflare Cache Rules or proxy settings bypass caching for `/health` so monitoring systems receive real-time operational status rather than stale cached responses.

If `engineTier` reports `managed`, the host lacks native Libabigail or RPM extraction tools. Refer to [Troubleshooting](#7-troubleshooting--rollback).

---

## 7. Troubleshooting & Rollback

### Startup Gate Failure
If startup terminates with `InvalidOperationException`:
- Verify that `abipkgdiff`, `rpm2cpio`, and `cpio` are installed and available in `$PATH`.
- Check permissions on `/tmp` or `TempExtractionDirectory`.
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
