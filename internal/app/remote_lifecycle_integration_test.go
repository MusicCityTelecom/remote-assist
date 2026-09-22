package app

import (
	"context"
	"database/sql"
	"errors"
	"os"
	"strings"
	"testing"
	"time"
)

func TestIntegrationRemoteLifecycleMySQL(t *testing.T) {
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
		t.Fatal(err)
	}
	codeHash := sha256Hex("phase2-code-" + id)
	tokenHash := sha256Hex("phase2-token-" + id)
	now := time.Now().UTC()

	session := Session{
		ID:                 id,
		CodeHint:           "4242",
		CustomerLabel:      "Phase 2 Lifecycle Test",
		TechnicianName:     "Integration Test",
		RequestedControl:   true,
		RequestedElevation: false,
		CreatedAt:          now,
		ExpiresAt:          now.Add(15 * time.Minute),
	}

	if err := store.CreateSession(ctx, session, codeHash); err != nil {
		t.Fatal(err)
	}
	defer func() {
		_, _ = store.db.ExecContext(context.Background(), "DELETE FROM support_sessions WHERE id=?", id)
		_ = store.Close()
	}()

	liveTTL := 90 * time.Minute
	redeemed, err := store.RedeemSession(ctx, codeHash, tokenHash, "PHASE2-PC", true, liveTTL)
	if err != nil {
		t.Fatal(err)
	}
	if redeemed.Status != "approved" {
		t.Fatalf("expected approved, got %s", redeemed.Status)
	}
	if redeemed.ExpiresAt.Before(now.Add(89 * time.Minute)) {
		t.Fatalf("live session expiry was not extended: %s", redeemed.ExpiresAt)
	}

	if err := store.MarkAgentConnected(ctx, id); err != nil {
		t.Fatal(err)
	}
	if err := store.recoverSessionStates(ctx); err != nil {
		t.Fatal(err)
	}
	recovered, err := store.GetSession(ctx, id)
	if err != nil {
		t.Fatal(err)
	}
	if recovered.Status != "approved" || recovered.AgentTokenHash != tokenHash {
		t.Fatalf("restart recovery did not preserve reconnectable session: %+v", recovered)
	}
	if err := store.MarkAgentConnected(ctx, id); err != nil {
		t.Fatal(err)
	}
	if err := store.EndSessionByAgentToken(ctx, id, tokenHash); err != nil {
		t.Fatal(err)
	}

	ended, err := store.GetSession(ctx, id)
	if err != nil {
		t.Fatal(err)
	}
	if ended.Status != "ended" {
		t.Fatalf("expected ended, got %s", ended.Status)
	}
	if ended.AgentTokenHash != "" {
		t.Fatal("agent token hash was not cleared after customer revocation")
	}

	err = store.EndSessionByAgentToken(ctx, id, tokenHash)
	if !errors.Is(err, sql.ErrNoRows) {
		t.Fatalf("revoked token should not end session twice, got %v", err)
	}
}
