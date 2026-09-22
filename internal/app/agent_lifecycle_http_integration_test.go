package app

import (
    "bytes"
    "context"
    "encoding/json"
    "net/http"
    "net/http/httptest"
    "os"
    "strings"
    "testing"
    "time"
)

func TestIntegrationAgentRedeemAndEndHTTPMySQL(t *testing.T) {
    dsn := os.Getenv("REMOTE_ASSIST_TEST_MYSQL_DSN")
    if dsn == "" {
        t.Skip("REMOTE_ASSIST_TEST_MYSQL_DSN is not set")
    }
    if !strings.Contains(dsn, "/remote_assist_ci?") {
        t.Fatal("integration test requires the dedicated remote_assist_ci database")
    }

    store, err := OpenStore(dsn)
    if err != nil {
        t.Fatal(err)
    }

    ctx := context.Background()
    id, err := newUUID()
    if err != nil {
        _ = store.Close()
        t.Fatal(err)
    }

    cfg := Config{
        PublicBaseURL:  "https://support.example.com",
        CodeSecret:     []byte(strings.Repeat("f", 32)),
        CookieSecret:   []byte(strings.Repeat("g", 32)),
        SessionTTL:     15 * time.Minute,
        LiveSessionTTL: 75 * time.Minute,
    }

    code := "31415926"
    now := time.Now().UTC()
    session := Session{
        ID:               id,
        CodeHint:         code[len(code)-4:],
        CustomerLabel:    "Agent HTTP Lifecycle Test",
        TechnicianName:   "Integration Test",
        RequestedControl: true,
        CreatedAt:        now,
        ExpiresAt:        now.Add(cfg.SessionTTL),
    }

    if err := store.CreateSession(ctx, session, hmacHex(cfg.CodeSecret, code)); err != nil {
        _ = store.Close()
        t.Fatal(err)
    }

    defer func() {
        _, _ = store.db.ExecContext(context.Background(), "DELETE FROM support_sessions WHERE id=?", id)
        _ = store.Close()
    }()

    handler := NewServer(cfg, store).Handler()

    postJSON := func(path string, body any, bearer string) *httptest.ResponseRecorder {
        t.Helper()

        raw, err := json.Marshal(body)
        if err != nil {
            t.Fatal(err)
        }

        req := httptest.NewRequest(
            http.MethodPost,
            "https://support.example.com"+path,
            bytes.NewReader(raw),
        )
        req.Header.Set("Content-Type", "application/json")
        if bearer != "" {
            req.Header.Set("Authorization", "Bearer "+bearer)
        }

        rec := httptest.NewRecorder()
        handler.ServeHTTP(rec, req)
        return rec
    }

    redeem := postJSON("/api/agent/redeem", map[string]any{
        "code":           code,
        "machine_name":   "AGENT-HTTP-PC",
        "terms_accepted": true,
    }, "")

    if redeem.Code != http.StatusOK {
        t.Fatalf("redeem status=%d body=%s", redeem.Code, redeem.Body.String())
    }

    var redeemed map[string]any
    if err := json.Unmarshal(redeem.Body.Bytes(), &redeemed); err != nil {
        t.Fatal(err)
    }

    sessionID, _ := redeemed["session_id"].(string)
    token, _ := redeemed["agent_token"].(string)
    wsURL, _ := redeemed["websocket_url"].(string)
    liveExpiryRaw, _ := redeemed["live_expires_at"].(string)

    if sessionID != id || token == "" || wsURL == "" || liveExpiryRaw == "" {
        t.Fatalf("unexpected redeem response: %+v", redeemed)
    }

    liveExpiry, err := time.Parse(time.RFC3339Nano, liveExpiryRaw)
    if err != nil {
        t.Fatalf("parse live expiry %q: %v", liveExpiryRaw, err)
    }
    if liveExpiry.Before(now.Add(74 * time.Minute)) {
        t.Fatalf("live deadline was not extended: %s", liveExpiry)
    }

    end := postJSON("/api/agent/end", map[string]any{"session_id": id}, token)
    if end.Code != http.StatusOK {
        t.Fatalf("agent end status=%d body=%s", end.Code, end.Body.String())
    }

    ended, err := store.GetSession(ctx, id)
    if err != nil {
        t.Fatal(err)
    }
    if ended.Status != "ended" || ended.AgentTokenHash != "" {
        t.Fatalf("session was not revoked after customer end: %+v", ended)
    }

    reuse := postJSON("/api/agent/end", map[string]any{"session_id": id}, token)
    if reuse.Code != http.StatusConflict {
        t.Fatalf("revoked token reuse status=%d body=%s", reuse.Code, reuse.Body.String())
    }
}
