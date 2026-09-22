# Remote Assist protocol notes

## HTTP API

Production traffic is expected to use HTTPS through Apache.

### Technician authentication and operations

- `POST /api/login`
- `POST /api/logout`
- `GET /api/me`
- `POST /api/account/password`
- `GET /api/dashboard/metrics`
- `GET /api/technicians`
- `GET /api/sessions`
- `GET /api/sessions/export`
- `POST /api/sessions`
- `GET /api/sessions/{id}`
- `POST /api/sessions/{id}/notes`
- `POST /api/sessions/{id}/files` — technician uploads a temporary file for the customer.
- `GET /api/sessions/{id}/files/{transfer}` — technician downloads a customer-offered temporary file.
- `POST /api/sessions/{id}/end`

Administrators additionally use:

- `GET /api/admins`
- `POST /api/admins`
- `PATCH /api/admins/{id}`
- `POST /api/admins/{id}/password`
- `GET /api/admin-audit`

Technician authentication uses a MySQL-backed account and an HttpOnly, Secure, SameSite=Strict HMAC-signed browser cookie.

### Customer agent

- `POST /api/agent/lookup` — validate an unredeemed enrollment code and return technician/request metadata.
- `POST /api/agent/redeem` — atomically consume the code after consent and return the separate random live agent credential, WSS URL, and live-session deadline.
- `POST /api/agent/end` — bearer-token-authenticated customer termination. This clears the live token and ends the session server-side.
- `POST /api/agent/files?session=<uuid>` — customer uploads a temporary file for the connected technician.
- `GET /api/agent/files/{transfer}?session=<uuid>` — customer downloads a technician-offered temporary file.

### Downloads / health

- `GET /api/health`
- `GET /api/download-status`
- `GET /download/windows`

## Session lifetime

A support session has two phases that use the same persisted `expires_at` field:

1. **Enrollment phase.** The 8-digit code expires after `SESSION_TTL_MINUTES` (15 minutes by default).
2. **Live phase.** Once the customer redeems that code, `expires_at` is replaced by the live-session deadline using `LIVE_SESSION_TTL_MINUTES` (480 minutes / 8 hours by default).

The enrollment code is single-use. It cannot be used to reconnect after redemption.

The separate random live token may reconnect only until the live deadline. The token is cleared when the customer or technician ends the session or when the live session expires.

After an application-server restart, previously connected sessions with a still-valid live token are normalized to `approved`, because no WebSocket can survive the process restart. The running customer agent can then reconnect using its existing live token.

## WebSockets

### Agent

`GET /ws/agent?session=<uuid>` with:

`Authorization: Bearer <256-bit-token>`

on the WebSocket upgrade request.

The token is generated only after consent and the database stores only its SHA-256 digest. It is carried in the Authorization header rather than the URL so it does not appear in ordinary Apache request logs.

Agent -> server/technician:

- Binary messages: complete JPEG screen frames.
- Text messages: hello/status/capture metadata.

Example hello:

```json
{
  "type": "hello",
  "machine_name": "FRONTDESK-PC",
  "elevated": false,
  "control": true,
  "clipboard": true,
  "file_transfer": true,
  "monitors": [
    {
      "index": 0,
      "name": "\\\\.\\DISPLAY1",
      "width": 1920,
      "height": 1080,
      "primary": true
    }
  ],
  "active_monitor": 0,
  "jpeg_quality": 55,
  "fps": 6,
  "capture_mode": "auto",
  "live_expires_at": "2026-09-19T08:00:00Z"
}
```

Capture-setting acknowledgement:

```json
{
  "type": "capture_settings",
  "active_monitor": 0,
  "jpeg_quality": 55,
  "fps": 6,
  "capture_mode": "auto"
}
```

The server also sends viewer-presence metadata to the agent:

```json
{"type":"viewer_status","connected":true}
```

When no technician viewer is attached, the customer agent keeps the session/WebSocket alive but pauses JPEG capture and upload. Capture resumes when a technician viewer attaches.

### Technician

`GET /ws/tech?session=<uuid>`

Authentication is the technician browser session cookie.

Technician -> agent input example:

```json
{
  "type": "input",
  "input": {
    "kind": "mouse_move",
    "x": 0.42,
    "y": 0.31
  }
}
```

Input kinds are:

- `mouse_move`
- `mouse_button`
- `mouse_wheel`
- `key`

Mouse coordinates are normalized from 0.0 to 1.0 against the currently selected monitor.

The broker drops technician `input` messages when the session was created without keyboard/mouse control permission.

Capture settings can be changed independently of control permission:

```json
{
  "type": "capture_settings",
  "monitor": 1,
  "jpeg_quality": 70,
  "fps": 8,
  "capture_mode": "auto"
}
```

The Windows agent clamps:

- monitor index to an available display;
- JPEG quality to 25-85;
- FPS to 1-12;
- capture mode to `auto` (DXGI preferred) or `gdi` compatibility mode.

## Clipboard text

Clipboard is an explicit per-session permission and is text-only.

Technician -> customer:

```json
{"type":"clipboard_set","text":"example"}
```

Technician requests the remote clipboard:

```json
{"type":"clipboard_get"}
```

Customer agent -> technician:

```json
{"type":"clipboard_data","text":"example","length":7}
```

The relay forwards clipboard messages only when `requested_clipboard=true`. Clipboard text is capped at 256 KiB. The browser UI performs clipboard reads/writes only after a technician presses the corresponding clipboard button.

## File transfer

File transfer is also an explicit per-session permission.

Transfers use authenticated HTTPS for file bytes and WebSocket metadata only for offers/status. This avoids mixing file payloads with JPEG screen frames.

Limits and lifecycle:

- 25 MB maximum per file;
- 30-minute relay TTL;
- temporary files live only in the support service's private `/tmp` namespace;
- pending transfer files are deleted when the support session ends;
- customer inbound files require a Yes/No prompt plus a Save File dialog;
- customer outbound files require the customer to choose the file using a normal Open File dialog;
- no file is silently written to or read from the customer machine.

Technician-to-customer offer metadata:

```json
{
  "type":"file_offer",
  "transfer_id":"...",
  "direction":"to_agent",
  "name":"diagnostic.txt",
  "size":12345,
  "expires_at":"2026-09-19T04:00:00Z"
}
```

Customer-to-technician offers use `direction:"to_tech"`. The technician downloads the offered file from the authenticated session sidebar.

## Reconnection

The Windows agent performs bounded automatic reconnect attempts after transient WebSocket/network failure:

- 1 second
- 2 seconds
- 4 seconds
- 8 seconds
- 10 seconds

The same live token is reused. Reconnection stops when:

- the customer explicitly ends the session;
- the server closes the socket with a normal `session ended` close reason;
- the live deadline has passed;
- all reconnect attempts fail.

## Capture implementation

Phase 4 separates **desktop capture** from **frame transport**.

Capture path:

- `auto` mode prefers DXGI Desktop Duplication using Direct3D 11;
- if DXGI initialization or runtime capture fails, the agent falls back to the existing GDI `Graphics.CopyFromScreen` path;
- after fallback, the agent periodically retries DXGI automatically;
- `DXGI_ERROR_ACCESS_LOST` resets the duplication object so desktop/UAC/session transitions can recover;
- technicians can force `gdi` compatibility mode from the viewer without rebuilding the agent;
- selecting a different monitor recreates accelerated capture for that output.

Idle-frame behavior:

- when DXGI reports no new frame, the agent skips both JPEG encoding and transmission;
- the GDI fallback suppresses exact duplicate JPEGs before transmission;
- screen capture still pauses entirely when no technician viewer is attached.

Transport remains JPEG over the existing WebSocket. Browser telemetry reports received FPS/bandwidth, while agent telemetry reports the active backend, frames sent/skipped, encoded bytes, and average capture+JPEG cost.

Hardware H.264/H.265 encoding remains the next major transport/encoding architecture step.

## Input implementation

Windows input uses `SendInput`.

Phase 3 retains expanded keyboard support to include:

- letters and number row;
- F1-F24;
- arrows/navigation keys;
- left/right Shift, Control, Alt, Windows keys;
- OEM punctuation keys;
- numeric keypad;
- lock keys and Print Screen.

Secure attention (Ctrl+Alt+Del) and Windows secure-desktop control remain intentionally unsupported.
