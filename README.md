# Remote Assist

Remote Assist is a self-hosted, open-source **attended remote desktop and support framework**. A technician creates a short-lived session, the customer runs a temporary Windows client, reviews the requested permissions, and explicitly approves the connection. The agent then opens an outbound encrypted connection to the server, so normal deployments do not require inbound customer-side port forwarding.

> **Status:** functional beta/preview. The framework is usable for testing and controlled deployments, but remote-control software is security-sensitive. Review the code, use HTTPS, protect technician accounts, and validate your deployment before exposing it broadly.

## Features

- Attended, customer-approved remote desktop sessions
- Single-use enrollment codes and separate random live-session credentials
- Browser technician console with history, notes, metrics, audit data, and team management
- Native Windows technician application using the same website authentication, cookies, RBAC, and APIs
- Windows customer agent with DXGI capture and GDI fallback
- JPEG transport with optional negotiated H.264/Annex-B and automatic JPEG fallback
- Mouse and keyboard control
- Multi-monitor selection and composite **All monitors** view
- Clipboard and bidirectional file transfer
- Text chat and temporary image chat
- Local technician recording and PNG screenshots
- Customer network diagnostics
- Customer-approved elevation handoff
- Session revocation and reconnect controls
- MySQL/MariaDB persistence
- HTTPS/WSS deployment behind Apache
- CI builds for the Linux server and Windows customer/technician clients

Remote Assist intentionally does **not** install unattended access, store a permanent customer password, bypass the Windows secure desktop, or provide Ctrl+Alt+Del injection.

## Architecture

```text
Customer Windows agent -- outbound WSS --> Remote Assist server <-- HTTPS/WSS -- Technician
                                             |
                                             +--> MySQL/MariaDB
```

See [Architecture](docs/ARCHITECTURE.md), [Protocol](docs/PROTOCOL.md), [Operations](docs/OPERATIONS.md), [Security model](docs/SECURITY.md), and the [beta acceptance checklist](docs/ACCEPTANCE-TEST.md).

## Server requirements

- Ubuntu 24.04 or 26.04 for the included deployment scripts
- Go 1.23+
- Apache 2
- MySQL or MariaDB
- A public DNS name
- A valid TLS certificate for that DNS name

A typical Ubuntu host can install the service prerequisites with:

```bash
sudo apt update
sudo apt install -y apache2 mariadb-server curl openssl git golang-go python3
sudo systemctl enable --now apache2 mariadb
```

Obtain a trusted TLS certificate with your preferred ACME client before running the installer. By default, Remote Assist looks for `/etc/letsencrypt/live/<your-domain>/fullchain.pem` and `privkey.pem`. If your certificate lives elsewhere, pass `TLS_CERT_DIR=/path/to/certificate-directory`.

## Quick deployment

Clone this repository using its GitHub URL, then set the public support hostname:

```bash
git clone https://github.com/<owner>/remote-assist.git
cd remote-assist
sudo SUPPORT_DOMAIN=support.example.com bash deploy/install-vps.sh
# Or, for a certificate stored elsewhere:
# sudo SUPPORT_DOMAIN=support.example.com TLS_CERT_DIR=/etc/ssl/remote-assist bash deploy/install-vps.sh
```

The installer builds/tests the Go server, creates a dedicated `remote-assist` system account, creates the `remote_assist` database/user on first install, generates bootstrap secrets, installs a hardened systemd service, and writes an Apache HTTPS/WSS reverse-proxy vhost. It requires an existing TLS certificate and does not alter UFW.

After deployment:

```bash
systemctl status remote-assist.service --no-pager -l
curl -fsS https://support.example.com/api/health
journalctl -u remote-assist.service -f
```

Generated bootstrap credentials and secrets are stored root-only in `/etc/remote-assist-app/app.env`. Change the initial administrator password after first login.

## Configuration

Start from [.env.example](.env.example). Required values are `MYSQL_DSN`, `TECH_USERNAME`, `TECH_PASSWORD`, `COOKIE_SECRET`, and `CODE_SECRET`. Generate secrets with `openssl rand -hex 32`.

Common optional values include `LISTEN_ADDR`, `PUBLIC_BASE_URL`, `SESSION_TTL_MINUTES`, `LIVE_SESSION_TTL_MINUTES`, and `TRUST_PROXY`.

## Build from source

Server:

```bash
go mod download
go test ./...
go vet ./...
go build -trimpath -o dist/remote-assist-server ./cmd/server
```

Customer Windows agent:

```powershell
dotnet publish agent/RemoteAssist.Agent.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o dist/agent
```

Technician application:

```powershell
dotnet publish technician/RemoteAssist.Technician.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o dist/technician
```

## Updating

```bash
sudo bash deploy/update-vps.sh
```

The updater validates source, snapshots MySQL when `mysqldump` is available, builds a replacement server, health-checks it, and rolls the binary back if the new server does not become healthy.

## Security

Remote desktop software creates a high-trust channel. Use HTTPS/WSS, strong unique administrator credentials, loopback binding behind the reverse proxy, restricted server/database access, code-signed Windows binaries for production, patched dependencies, and the acceptance checklist before field use. See [SECURITY.md](SECURITY.md) for vulnerability-reporting guidance.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Author

Tommy Heggie

## License

MIT. See [LICENSE](LICENSE).
