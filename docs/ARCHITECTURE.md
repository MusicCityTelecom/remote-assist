# Remote Assist architecture

## Objective

Remote Assist is an attended remote-support system. A customer starts the Windows support application, enters a short-lived session code supplied by an authenticated technician, reviews the requested permissions, and explicitly approves the connection. The customer agent initiates all support traffic outbound.

The design deliberately separates short-code enrollment from the live transport credential and does not install an unattended-access password or permanent customer service.

## Current topology

```text
Customer Windows agent
  | HTTPS: lookup / redeem / file transfer
  | WSS: screen transport, input, chat, control/status metadata
  v
Apache :443 on support.example.com
  |
  v
Go support service :8787 (loopback only)
  |-- technician authentication / RBAC
  |-- session/history/audit API
  |-- WebSocket broker
  |-- chat + attachment metadata
  |-- temporary file-transfer store
  |-- network diagnostic persistence
  |-- embedded technician web console
  v
MySQL / MariaDB

Technician browser --------------------+
                                       |
Native technician WebView2 client -----+--> same HTTPS/WSS endpoint
```

All live customer connections remain outbound from the customer network. The VPS relay is intentional because it behaves predictably through NAT, CGNAT, hotel networks, firewalls, and mobile hotspots. P2P/ICE/STUN/TURN remains a future optimization rather than a functional dependency.

## Technician surfaces

### Web console

The browser console provides:

- live session creation and review;
- detachable/pop-out remote viewer with a screen-focused mode;
- local PNG screenshots of the currently rendered remote canvas;
- Fit, 1:1, and fullscreen viewing;
- monitor selection, including a composite **All monitors** virtual desktop;
- capture backend, resolution, JPEG quality, frame-rate, and video-transport controls;
- keyboard/mouse control;
- text clipboard;
- bidirectional files;
- text chat and inline image chat;
- technician-side recording;
- persisted network diagnostics and live refresh;
- on-demand elevation request;
- technician notes, timeline, history, CSV export, team management, and audit.

### Native technician client

The Windows technician application is a .NET 8 WinForms/WebView2 native shell over the same authenticated support application. Website login/cookies, server-side RBAC, sessions, audit, and WebSocket mechanisms remain authoritative, but native mode suppresses duplicated browser chrome and exposes first-class Windows navigation, session actions, viewer controls, and status/telemetry surfaces.

The native application provides a left navigation rail for Live Sessions, History, Team, Audit, Account, and Sign out; a native session header; and a session-only viewer toolbar for monitor selection, capture/video controls, resolution/quality/FPS, Fit/1:1, screenshot, tools/sidebar control, recording, chat, files, clipboard, network tools, elevation request, native fullscreen, and always-on-top behavior.

It is built as both a portable ZIP and an Inno Setup installer.

## Session security model

1. Technician authenticates with a MySQL-backed support account.
2. Technician creates a session. The server generates an 8-digit enrollment code that expires after 15 minutes by default.
3. The database stores an HMAC of the numeric code rather than the code itself.
4. Customer agent uses the code only to retrieve non-secret technician/session metadata.
5. Customer reviews the requested permissions and explicitly accepts.
6. The code is atomically redeemed once.
7. The server issues a separate random 256-bit live agent credential and stores only its SHA-256 digest.
8. The agent authenticates its live WebSocket with that bearer credential.
9. The live credential has a bounded lifetime and can reconnect only within that deadline.
10. Customer or technician termination clears the credential and closes live peers.

The numeric code is therefore an enrollment mechanism, not the live remote-control key.

## Capture and video transport

### Capture

Per-monitor capture prefers **DXGI Desktop Duplication** with Direct3D 11. If DXGI cannot initialize or loses access, the agent automatically falls back to GDI capture and periodically retries DXGI.

The technician can force GDI compatibility mode.

The composite **All monitors** mode captures the Windows virtual desktop bounds with GDI. Input coordinates remain normalized across the corresponding virtual-desktop rectangle, including negative monitor coordinates.

The customer reports each monitor's index, coordinates, dimensions, and primary-display state so the technician UI can represent the actual layout.

### Frame transport

JPEG remains the compatibility/default transport and supports:

- 100%, 75%, and 50% scaling;
- adjustable JPEG quality;
- fixed or adaptive FPS;
- idle-frame suppression;
- latest-frame-wins browser rendering.

The negotiated H.264 path uses:

```text
DXGI/GDI BGRA frame
        |
        v
NV12 conversion
        |
        v
Windows Media Foundation H.264 encoder
        |
        v
Annex-B access unit + Remote Assist frame header
        |
        v
existing WSS relay
        |
        v
browser WebCodecs VideoDecoder
```

H.264 is used only when both endpoints advertise support and the technician selects it. Encoder/decoder failure falls back to JPEG without terminating the support session.

## Input

Keyboard and mouse events are sent as structured JSON and applied with Windows `SendInput`.

Mouse coordinates are normalized against the currently selected capture rectangle. This allows the same input protocol to work with individual monitors, scaled transport frames, and the composite virtual desktop.

The support permission recorded for the session determines whether the broker forwards input messages.

## Clipboard, files, and chat

Clipboard access is explicit, text-only, and bounded.

General file transfers are explicit, authenticated, temporary, and bounded in size. Pending customer-to-technician files survive viewer reconnects for their transfer TTL.

Chat messages are persisted by session. Inline picture chat reuses the temporary transfer mechanism for bounded JPEG/PNG/GIF attachments while storing only message/attachment metadata in MySQL.

## Network diagnostics

The customer agent can report a bounded snapshot containing public IP and adapter information such as IPv4 addresses, masks, gateways, DNS servers, method, and default-route state.

The snapshot is persisted per support session and technicians can request a live refresh.

## Recording

Technician-side recording currently records the rendered remote canvas locally as WebM and downloads it to the technician workstation. One-click screenshots similarly save the rendered canvas locally as PNG; only the screenshot event is written to the session timeline. The VPS does not intentionally retain screen video or screenshot image bytes.

Server-managed recording retention, policy controls, and evidence storage are future work.

## Elevation

The customer application supports an on-demand elevation request during a live session.

The customer must approve the request and the normal Windows UAC prompt. The running support session is handed across the restart so the user does not have to re-enter the enrollment code.

Remote Assist does **not** bypass or automate Windows secure desktop.

## Deliberate boundaries

The current architecture intentionally does not provide:

- unattended customer passwords or a permanent support service;
- Windows secure-desktop capture/control;
- Ctrl+Alt+Del / secure-attention injection;
- silent elevation;
- server-retained screen recordings;
- independent simultaneous video streams for every monitor;
- P2P/ICE traversal;
- multi-technician takeover/transfer;
- MFA/SSO yet.

These boundaries keep attended consent explicit while the product matures toward signed production releases.
