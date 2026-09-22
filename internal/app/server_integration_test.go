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

func TestIntegrationOperationsHTTPMySQL(t *testing.T) {
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
    reset := func() {
        for _, stmt := range []string{
            "SET FOREIGN_KEY_CHECKS=0",
            "TRUNCATE TABLE support_session_notes",
            "TRUNCATE TABLE support_events",
            "TRUNCATE TABLE support_sessions",
            "TRUNCATE TABLE support_admin_audit",
            "TRUNCATE TABLE support_admins",
            "SET FOREIGN_KEY_CHECKS=1",
        } {
            if _, err := store.db.ExecContext(ctx, stmt); err != nil {
                t.Fatalf("reset database: %s: %v", stmt, err)
            }
        }
    }
    reset()
    t.Cleanup(func() {
        reset()
        _ = store.Close()
    })

    const password = "HTTP-Integration-Password-2026!"
    if _, err := store.EnsureBootstrapAdmin(ctx, "admin", password); err != nil {
        t.Fatal(err)
    }

    cfg := Config{
        PublicBaseURL:     "https://support.example.com",
        CookieSecret:      []byte(strings.Repeat("d", 32)),
        CodeSecret:        []byte(strings.Repeat("e", 32)),
        SessionTTL:        15 * time.Minute,
        AgentDownloadPath: "/nonexistent/agent.exe",
    }
    h := NewServer(cfg, store).Handler()

    doJSON := func(method, path string, body any, cookie *http.Cookie) *httptest.ResponseRecorder {
        t.Helper()
        var raw []byte
        if body != nil {
            var err error
            raw, err = json.Marshal(body)
            if err != nil {
                t.Fatal(err)
            }
        }
        req := httptest.NewRequest(method, "https://support.example.com"+path, bytes.NewReader(raw))
        if body != nil {
            req.Header.Set("Content-Type", "application/json")
        }
        if method != http.MethodGet && method != http.MethodHead {
            req.Header.Set("Origin", "https://support.example.com")
        }
        if cookie != nil {
            req.AddCookie(cookie)
        }
        rec := httptest.NewRecorder()
        h.ServeHTTP(rec, req)
        return rec
    }

    login := doJSON(http.MethodPost, "/api/login", map[string]any{"username": "admin", "password": password}, nil)
    if login.Code != http.StatusOK {
        t.Fatalf("login status=%d body=%s", login.Code, login.Body.String())
    }
    if len(login.Result().Cookies()) != 1 {
        t.Fatalf("expected login cookie, got %d", len(login.Result().Cookies()))
    }
    cookie := login.Result().Cookies()[0]

    me := doJSON(http.MethodGet, "/api/me", nil, cookie)
    if me.Code != http.StatusOK || !strings.Contains(me.Body.String(), "\"role\":\"admin\"") {
        t.Fatalf("me status=%d body=%s", me.Code, me.Body.String())
    }

    create := doJSON(http.MethodPost, "/api/sessions", map[string]any{
        "customer_label": "HTTP Integration Hotel",
        "requested_control": true,
        "requested_elevation": false,
    }, cookie)
    if create.Code != http.StatusCreated {
        t.Fatalf("create session status=%d body=%s", create.Code, create.Body.String())
    }
    var created struct {
        Session Session `json:"session"`
        Code string `json:"code"`
    }
    if err := json.Unmarshal(create.Body.Bytes(), &created); err != nil {
        t.Fatal(err)
    }
    if len(created.Code) != 8 || created.Session.ID == "" || created.Session.TechnicianID == nil {
        t.Fatalf("unexpected create response: %+v", created)
    }

    note := doJSON(http.MethodPost, "/api/sessions/"+created.Session.ID+"/notes",
        map[string]any{"body": "HTTP integration note."}, cookie)
    if note.Code != http.StatusCreated {
        t.Fatalf("note status=%d body=%s", note.Code, note.Body.String())
    }

    detail := doJSON(http.MethodGet, "/api/sessions/"+created.Session.ID, nil, cookie)
    if detail.Code != http.StatusOK || !strings.Contains(detail.Body.String(), "HTTP integration note.") {
        t.Fatalf("detail status=%d body=%s", detail.Code, detail.Body.String())
    }

    admins := doJSON(http.MethodGet, "/api/admins", nil, cookie)
    if admins.Code != http.StatusOK || !strings.Contains(admins.Body.String(), "\"username\":\"admin\"") {
        t.Fatalf("admins status=%d body=%s", admins.Code, admins.Body.String())
    }

    technicians := doJSON(http.MethodGet, "/api/technicians", nil, cookie)
    if technicians.Code != http.StatusOK || !strings.Contains(technicians.Body.String(), "\"username\":\"admin\"") {
        t.Fatalf("technicians status=%d body=%s", technicians.Code, technicians.Body.String())
    }

    metrics := doJSON(http.MethodGet, "/api/dashboard/metrics", nil, cookie)
    if metrics.Code != http.StatusOK {
        t.Fatalf("metrics status=%d body=%s", metrics.Code, metrics.Body.String())
    }

    end := doJSON(http.MethodPost, "/api/sessions/"+created.Session.ID+"/end", map[string]any{}, cookie)
    if end.Code != http.StatusOK {
        t.Fatalf("end status=%d body=%s", end.Code, end.Body.String())
    }

    history := doJSON(http.MethodGet, "/api/sessions?status=ended&q=HTTP+Integration", nil, cookie)
    if history.Code != http.StatusOK || !strings.Contains(history.Body.String(), created.Session.ID) {
        t.Fatalf("history status=%d body=%s", history.Code, history.Body.String())
    }
}
