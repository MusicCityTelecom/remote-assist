# Security model and deployment cautions

This repository contains remote-support software. Treat code signing, release integrity, technician authentication, server hardening, and customer consent as security-critical parts of the product.

## MVP safeguards

- Customer initiates the connection outbound.
- Session codes expire and can be redeemed only once.
- Short codes are HMACed before database storage.
- Live agent credentials use 256 bits of randomness; only their SHA-256 digests are stored.
- Redeemed live credentials have a bounded lifetime (8 hours by default), are cleared when either side ends the session, and cannot be reused after revocation/expiry.
- The customer app makes a bearer-authenticated server-side end request when the customer explicitly ends or closes an active session.
- Explicit terms/consent is required before code redemption.
- Technician web authentication uses MySQL-backed accounts, per-user salted PBKDF2-HMAC-SHA256 password hashes, and a Secure/HttpOnly/SameSite=Strict signed cookie.
- Browser cookies are bound to an account authentication version; password resets, role changes, and account disable/enable operations invalidate older cookies.
- Built-in `admin` and `technician` roles separate team/audit administration from ordinary support work.
- Administrative actions are written to a separate durable audit log.
- Login and code enrollment are rate-limited in process.
- The Go service listens on loopback by default and is exposed through Apache HTTPS/WSS.
- The broker does not intentionally persist remote screen frames.
- Screen capture pauses when no technician viewer is attached, reducing unnecessary relay exposure and bandwidth.
- Clipboard text access is an explicit per-session permission and is capped at 256 KB per message.
- File transfer is an explicit per-session permission; each file is capped at 25 MB, stored only in the service's private temporary directory, and expires after 30 minutes.
- Customer-side file receive requires a confirmation prompt and Save File dialog; customer-side file send requires an Open File dialog.
- The customer executable does not install a permanent remote-access password.
- The customer can terminate access by closing the app.
- Administrator elevation invokes normal Windows UAC; it is not bypassed.

## Before production use

1. Digitally sign the Windows executable with a Remote Assist Project code-signing identity.
2. Add MFA/SSO and a finer-grained permission matrix beyond the current `admin` / `technician` roles.
3. Add durable distributed rate limiting if more than one application server is introduced.
4. Perform an independent security review of the WebSocket broker, input handling, deployment script, and Windows process/elevation boundary.
5. Define and enforce retention controls for support history, technician notes, and administrative audit records appropriate for customer agreements.
6. Add release signing/checksums and a controlled update channel.
7. Add automated dependency scanning and operating-system patching.
8. Keep the service behind HTTPS. Do not expose port 8787 publicly.

## Out of scope by design

The current agent cannot control the Windows secure desktop, does not inject the secure attention sequence, and does not install a SYSTEM service. Do not weaken UAC or disable secure-desktop policy as a workaround.
