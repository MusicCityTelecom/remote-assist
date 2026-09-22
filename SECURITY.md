# Security Policy

Remote Assist is security-sensitive software because it provides interactive remote access.

## Reporting a vulnerability

Do not publish exploit details, credentials, private customer information, or active server information in a public issue. Use the repository owner's private security-reporting channel when available. If private reporting is not enabled, open a minimal public issue asking for a private contact method without including exploit details.

## Deployment guidance

- Use HTTPS/WSS only for Internet-facing deployments.
- Keep the Go service bound to loopback behind a reverse proxy.
- Use strong, unique administrator credentials.
- Protect `/etc/remote-assist-app/app.env` as root-only.
- Code-sign distributed Windows binaries for production use.
- Review logs and audit events after support sessions.
- Keep the OS, Go toolchain, .NET dependencies, and database patched.
- Do not add unattended access or secure-desktop bypasses without a separate threat-model review.
