# abaci-bot

## Getting Started

### 1. Configure appsettings.json

This repository does **not** include a config file. You must create one yourself based on the provided template:

```bash
cp appsettings.demo.json appsettings.json
```

Then edit `appsettings.json` and fill in your actual values:

| Field | Description |
|---|---|
| `GitHubApp.AppId` | Your GitHub App's numeric ID |
| `GitHubApp.InstallationId` | The installation ID for the target org/repo |
| `GitHubApp.PrivateKey` | The RSA private key generated for your GitHub App (PEM format, newlines as `\n`) |
| `GitHubApp.WebhookSecret` | Shared secret for validating `X-Hub-Signature-256`. Must exactly match your GitHub webhook secret. |
| `GitHubApp.TeamName` | Team slug used for review workflow (for example: `captains`). Review labels are updated only when review is submitted by a member of this team. |
| `Kestrel.Endpoints.Http.Url` | The address and port the server listens on (default: `http://0.0.0.0:3456`) |

### 2. Configure GitHub App permissions and webhook events

Your GitHub App must be configured with the following permissions:

**Organization permissions**

- Members: **Read-only**

**Repository permissions**

- Pull requests: **Read and write**
- Issues: **Read and write**
- Contents: **Read-only**

Your GitHub App must also subscribe to these webhook events:

- Issue comments
- Issues
- Pull request review comments
- Pull request review threads
- Pull request reviews
- Pull requests

### 3. Run

#### Option A: Run directly

```bash
dotnet run
```

Or run the published binary directly:

```bash
./abaci-bot
```

#### Option B: Run with Docker Compose (Recommended)

1. Create your configuration file:
   ```bash
   cp appsettings.demo.json appsettings.json
   # Edit appsettings.json with your GitHub App credentials
   ```

2. Start the bot:
   ```bash
   docker compose up -d
   ```

3. View logs:
   ```bash
   docker compose logs -f
   ```

4. Stop the bot:
   ```bash
   docker compose down
   ```

#### Option C: Run with Docker directly

```bash
# Pull the image
docker pull ghcr.io/openruyi/abaci-bot:latest

# Run with mounted config
docker run -d \
  --name abaci-bot \
  -p 3456:3456 \
  -v $(pwd)/appsettings.json:/app/appsettings.json:ro \
  ghcr.io/openruyi/abaci-bot:latest
```

### 4. Build Docker Image Locally (Optional)

If you want to build the image yourself:

```bash
docker build -t abaci-bot .
```

Or with docker compose:

```bash
docker compose build
```

## Configuration via Environment Variables

You can also configure the bot using environment variables (useful for container orchestration):

```yaml
environment:
  - GitHubApp__AppId=123456
  - GitHubApp__InstallationId=78901234
  - GitHubApp__PrivateKey=-----BEGIN RSA PRIVATE KEY-----\n...\n-----END RSA PRIVATE KEY-----
  - GitHubApp__WebhookSecret=webhook_secret
  - GitHubApp__TeamName=captains
```

> Note: Use double underscores (`__`) to represent nested configuration keys.

## Health Check

The bot exposes a health check endpoint at `/health` for monitoring:

```bash
curl http://localhost:3456/health
```

## LICENSE

MIT License
