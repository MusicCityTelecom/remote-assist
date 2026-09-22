# Beta acceptance test

This checklist qualifies an Remote Assist preview before wider field use. Run it on clean or representative Windows 10/11 customer and technician systems over a real Internet path.

## 1. VPS update and health

On the support VPS:

```bash
cd /opt/remote-assist
git status --short
git branch --show-current
git rev-parse HEAD
sudo bash deploy/update-vps.sh
```

The updater must complete its Go tests/vet/build, restart the service, pass the loopback health check, refresh the Windows release artifacts, validate Apache, and pass the public HTTPS health check.

Record:

```bash
git rev-parse HEAD
systemctl is-active remote-assist.service
curl -fsS http://127.0.0.1:8787/api/health
curl -fsS https://support.example.com/api/health
sha256sum /opt/remote-assist/downloads/RemoteAssist.exe
sha256sum /opt/remote-assist/downloads/Remote-Assist-Technician-Portable.zip
sha256sum /opt/remote-assist/downloads/Remote-Assist-Technician-Setup.exe
```

## 2. Clean technician installation

Use either the portable ZIP or installer from the authenticated console/release.

Verify:

- product is **Remote Assist Technician**;
- FileVersion is **0.10.0.0**;
- the installer reports **0.10.0-preview**;
- the `remote-assist-tech://` browser handoff opens the installed technician client;
- login uses the existing support account and no second credential system appears;
- the native toolbar exposes monitor, capture, transport, resolution, quality, FPS, detach, Fit/1:1, Screenshot, Tools, Record, Chat, Files, Network, Elevate, fullscreen, and always-on-top controls.

Unsigned preview builds may still show Windows publisher warnings until Authenticode signing is connected. Do not treat that as production acceptance.

## 3. Customer enrollment and consent

Download a fresh customer executable from:

```text
https://support.example.com/download/windows
```

Verify its Windows FileVersion is **0.10.0.0**.

Create an attended session and confirm:

1. the technician receives an 8-digit enrollment code;
2. the customer can look up the intended technician/session;
3. requested permissions are shown before redemption;
4. customer acceptance is required;
5. the enrollment code cannot be redeemed a second time;
6. the connected session shows the customer's machine and exact agent build.

## 4. Remote desktop and input

Test all applicable cases:

- DXGI Auto mode;
- forced GDI compatibility mode;
- 100%, 75%, and 50% resolution;
- JPEG quality presets;
- fixed FPS selections;
- Adaptive FPS;
- Fit;
- 1:1;
- browser fullscreen;
- native fullscreen;
- mouse movement/click/drag/wheel;
- normal typing;
- function keys;
- numpad/OEM keys used in the environment.

Leave the desktop idle and verify capture/bandwidth falls rather than continuously transmitting identical frames.

## 5. H.264 and JPEG fallback

When the customer and technician endpoints both report support:

1. select H.264;
2. confirm live video continues;
3. exercise motion, window dragging, scrolling, and text-heavy screens;
4. change monitor/resolution and confirm renegotiation works;
5. return to JPEG and confirm immediate recovery.

Repeat on at least one Intel, AMD, or NVIDIA machine when available. A machine without a usable H.264 path must remain fully usable through JPEG fallback.

## 6. Multi-monitor

With two or more displays:

- select each monitor independently;
- select **All monitors**;
- confirm monitor geometry matches the Windows layout;
- include a layout with a monitor left of or above the primary display if available;
- verify clicks map to the correct physical display;
- test Fit and 1:1 in composite mode.

Independent simultaneous per-monitor video windows are not part of this beta.

## 7. Focused viewer and screenshots

Open the detachable/pop-out viewer.

Verify:

- the pop-out starts in screen-focused mode with tools hidden;
- Show tools / Hide tools works without disconnecting;
- Screenshot saves a local PNG;
- the screenshot is of the currently rendered remote canvas;
- a screenshot event appears in the session timeline;
- screenshot image bytes are not uploaded to or retained by the VPS;
- native Screenshot and Tools actions work;
- Chat, Files, and Network automatically reveal the sidebar when necessary.

## 8. Clipboard, files, chat, and image chat

With the corresponding session permissions enabled:

- technician -> customer text clipboard;
- customer -> technician text clipboard;
- technician -> customer file;
- customer -> technician file;
- disconnect/reconnect the viewer while a customer file is pending and confirm it remains discoverable within its TTL;
- text chat in both directions;
- inline PNG/JPEG/GIF image chat;
- expiration behavior for temporary image/file content.

Confirm no file is silently written on the customer side without the expected customer interaction.

## 9. Network diagnostics

Verify initial and refreshed diagnostics include the expected applicable data:

- public IP;
- adapter names/types;
- IPv4 addresses and masks;
- gateways;
- DNS servers;
- default-route indication.

Use **Refresh Net** and confirm the persisted session detail updates.

## 10. Recording

Start local technician recording, exercise remote activity, stop recording, and verify a playable WebM is saved to the technician workstation.

Confirm the VPS is not retaining the screen recording.

## 11. Elevation handoff

Request elevation during a live session.

Verify:

1. the customer receives the request;
2. customer approval is required;
3. normal Windows UAC is shown;
4. the application restarts elevated;
5. the existing support session resumes without re-entering the enrollment code;
6. rejecting elevation leaves the ordinary support session usable.

Do not disable Windows secure desktop as a workaround. Secure-desktop capture/control and Ctrl+Alt+Del injection are intentionally out of scope.

## 12. Revocation and reconnect

Test:

- temporary network interruption and agent reconnect within the live deadline;
- browser/technician reconnect;
- customer End Session;
- technician End Session;
- closing the customer application during a live session;
- reconnect attempt after explicit termination.

A revoked or ended live credential must not regain access.

## 13. History and audit

After ending the test session verify:

- session history/search;
- timestamps and duration;
- technician attribution;
- customer machine/build;
- requested permissions;
- notes;
- chat metadata;
- network diagnostics;
- screenshot event;
- elevation/session lifecycle events;
- CSV export;
- administrator audit events where applicable.

## 14. Beta acceptance

For this preview, record any failure with:

- customer OS/build;
- technician OS/build;
- GPU/graphics adapter;
- capture backend;
- video transport;
- selected monitor/resolution/FPS/quality;
- exact customer and technician application versions;
- relevant browser/WebView2 version;
- server commit;
- server journal excerpt around the failure.

Broad production rollout remains blocked on Authenticode signing, stronger technician authentication/RBAC, independent security review, and the other controls documented in `docs/SECURITY.md`.
