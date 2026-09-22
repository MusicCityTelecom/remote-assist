package app

import (
	"context"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
)

func TestIntegrationAdminCookieRevocationMySQL(t *testing.T) {
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
		if _, err := store.db.ExecContext(ctx, "DELETE FROM support_admins"); err != nil {
			t.Fatalf("clear support_admins: %v", err)
		}
	}
	reset()
	t.Cleanup(func() {
		reset()
		_ = store.Close()
	})

	admin, err := store.CreateAdmin(ctx, "cookie.admin", "Cookie Admin", "admin", "Cookie-Test-Password-2026!")
	if err != nil {
		t.Fatal(err)
	}

	cfg := Config{CookieSecret: []byte(strings.Repeat("c", 32))}
	rec := httptest.NewRecorder()
	if err := issueAdminCookie(rec, cfg, admin); err != nil {
		t.Fatal(err)
	}
	resp := rec.Result()
	if len(resp.Cookies()) != 1 {
		t.Fatalf("expected one auth cookie, got %d", len(resp.Cookies()))
	}
	cookie := resp.Cookies()[0]

	req := httptest.NewRequest("GET", "https://support.example.com/api/me", nil)
	req.AddCookie(cookie)
	got, err := authenticateAdmin(req, cfg, store)
	if err != nil {
		t.Fatalf("fresh cookie rejected: %v", err)
	}
	if got.ID != admin.ID || got.Username != admin.Username {
		t.Fatalf("unexpected authenticated admin: %+v", got)
	}

	if err := store.ChangeAdminPassword(ctx, admin.ID, "Cookie-Test-New-Password-2026!"); err != nil {
		t.Fatal(err)
	}

	req2 := httptest.NewRequest("GET", "https://support.example.com/api/me", nil)
	req2.AddCookie(cookie)
	if _, err := authenticateAdmin(req2, cfg, store); err == nil {
		t.Fatal("old cookie remained valid after password reset")
	}
}
