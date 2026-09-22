package app

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"
)

func TestIntegrationOperationsMySQL(t *testing.T) {
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

	const bootstrapPassword = "CI-Bootstrap-Password-2026!"
	created, err := store.EnsureBootstrapAdmin(ctx, "admin", bootstrapPassword)
	if err != nil || !created {
		t.Fatalf("bootstrap admin: created=%v err=%v", created, err)
	}
	created, err = store.EnsureBootstrapAdmin(ctx, "ignored", "Another-Password-2026!")
	if err != nil || created {
		t.Fatalf("bootstrap must be idempotent once admins exist: created=%v err=%v", created, err)
	}

	cred, err := store.GetAdminCredentialByUsername(ctx, "ADMIN")
	if err != nil {
		t.Fatal(err)
	}
	if !cred.Active || cred.Role != "admin" || !verifyPassword(bootstrapPassword, cred.PasswordSalt, cred.PasswordHash, cred.PasswordIterations) {
		t.Fatal("bootstrap credential did not round-trip")
	}

	tech, err := store.CreateAdmin(ctx, "tech.one", "Tech One", "technician", "CI-Technician-Password-2026!")
	if err != nil {
		t.Fatal(err)
	}
	if tech.Role != "technician" || !tech.Active {
		t.Fatalf("unexpected technician: %+v", tech)
	}

	if _, err := store.UpdateAdmin(ctx, cred.ID, cred.DisplayName, "technician", true); err == nil {
		t.Fatal("last active administrator was allowed to demote itself")
	}

	now := time.Now().UTC()
	session := Session{
		ID:                 "11111111-2222-4333-8444-555555555555",
		CodeHint:           "5678",
		CustomerLabel:      "Integration Hotel",
		TechnicianName:     tech.DisplayName,
		TechnicianID:       &tech.ID,
		RequestedControl:   true,
		RequestedElevation: false,
		CreatedAt:          now,
		ExpiresAt:          now.Add(15 * time.Minute),
	}
	const codeHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	if err := store.CreateSession(ctx, session, codeHash); err != nil {
		t.Fatal(err)
	}
	store.SetSessionTechnicianID(ctx, session.ID, tech.ID)
	store.AddEvent(ctx, session.ID, tech.Username, "session_created", "")

	result, err := store.SearchSessions(ctx, SessionQuery{Search: "Integration", Status: "active", Limit: 50})
	if err != nil {
		t.Fatal(err)
	}
	if result.Total != 1 || len(result.Sessions) != 1 || result.Sessions[0].TechnicianID == nil || *result.Sessions[0].TechnicianID != tech.ID {
		t.Fatalf("unexpected search result: %+v", result)
	}

	note, err := store.AddSessionNote(ctx, session.ID, tech, "Confirmed front desk connectivity.")
	if err != nil {
		t.Fatal(err)
	}
	if note.Body == "" {
		t.Fatal("note body missing")
	}
	notes, err := store.ListSessionNotes(ctx, session.ID)
	if err != nil || len(notes) != 1 {
		t.Fatalf("notes: len=%d err=%v", len(notes), err)
	}

	redeemed, err := store.RedeemSession(ctx, codeHash, strings.Repeat("b", 64), "FRONTDESK-PC", true)
	if err != nil {
		t.Fatal(err)
	}
	if redeemed.Status != "approved" {
		t.Fatalf("expected approved, got %s", redeemed.Status)
	}
	if err := store.MarkAgentConnected(ctx, session.ID); err != nil {
		t.Fatal(err)
	}
	if err := store.EndSession(ctx, session.ID); err != nil {
		t.Fatal(err)
	}

	metrics, err := store.DashboardMetrics(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if metrics.TotalThirtyDays != 1 || metrics.Connected != 0 {
		t.Fatalf("unexpected metrics: %+v", metrics)
	}

	store.AddAdminAudit(ctx, &cred.ID, cred.Username, "integration_test", "session", session.ID, "ok", "127.0.0.1")
	audit, err := store.ListAdminAudit(ctx, 20)
	if err != nil || len(audit) == 0 || audit[0].Event != "integration_test" {
		t.Fatalf("audit: %+v err=%v", audit, err)
	}

	if err := store.ChangeAdminPassword(ctx, tech.ID, "CI-New-Technician-Password-2026!"); err != nil {
		t.Fatal(err)
	}
	newCred, err := store.GetAdminCredentialByUsername(ctx, tech.Username)
	if err != nil {
		t.Fatal(err)
	}
	if newCred.AuthVersion <= tech.AuthVersion || !verifyPassword("CI-New-Technician-Password-2026!", newCred.PasswordSalt, newCred.PasswordHash, newCred.PasswordIterations) {
		t.Fatal("password reset did not bump auth version or persist new password")
	}
}
