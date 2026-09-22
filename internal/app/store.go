package app

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"time"

	_ "github.com/go-sql-driver/mysql"
)

type Session struct {
	ID                 string     `json:"id"`
	CodeHint           string     `json:"code_hint"`
	CustomerLabel      string     `json:"customer_label"`
	TechnicianName     string     `json:"technician_name"`
	TechnicianID       *int64     `json:"technician_id,omitempty"`
	Status             string     `json:"status"`
	RequestedControl      bool       `json:"requested_control"`
	RequestedElevation    bool       `json:"requested_elevation"`
	RequestedClipboard    bool       `json:"requested_clipboard"`
	RequestedFileTransfer bool       `json:"requested_file_transfer"`
	TermsAccepted         bool       `json:"terms_accepted"`
	MachineName        string     `json:"machine_name"`
	AgentTokenHash     string     `json:"-"`
	CreatedAt          time.Time  `json:"created_at"`
	ExpiresAt          time.Time  `json:"expires_at"`
	RedeemedAt         *time.Time `json:"redeemed_at,omitempty"`
	ConnectedAt        *time.Time `json:"connected_at,omitempty"`
	EndedAt            *time.Time `json:"ended_at,omitempty"`
}

type Event struct {
	ID        int64     `json:"id"`
	SessionID string    `json:"session_id"`
	Actor     string    `json:"actor"`
	Event     string    `json:"event"`
	Details   string    `json:"details"`
	CreatedAt time.Time `json:"created_at"`
}

type Store struct{ db *sql.DB }

func OpenStore(dsn string) (*Store, error) {
	db, err := sql.Open("mysql", dsn)
	if err != nil {
		return nil, err
	}
	db.SetConnMaxLifetime(3 * time.Minute)
	db.SetMaxOpenConns(10)
	db.SetMaxIdleConns(5)
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := db.PingContext(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	s := &Store{db: db}
	if err := s.migrate(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	if err := s.migrateOperations(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	if err := s.migrateChat(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	if err := s.migrateNetwork(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	if err := s.recoverSessionStates(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	return s, nil
}

func (s *Store) recoverSessionStates(ctx context.Context) error {
	if _, err := s.db.ExecContext(ctx, `UPDATE support_sessions
		SET status='expired', ended_at=COALESCE(ended_at, UTC_TIMESTAMP(6)), agent_token_hash=NULL
		WHERE status IN ('waiting','approved','connected') AND expires_at <= UTC_TIMESTAMP(6)`); err != nil {
		return err
	}
	_, err := s.db.ExecContext(ctx, `UPDATE support_sessions
		SET status='approved'
		WHERE status='connected' AND agent_token_hash IS NOT NULL AND expires_at > UTC_TIMESTAMP(6)`)
	return err
}

func (s *Store) Close() error { return s.db.Close() }

func (s *Store) migrate(ctx context.Context) error {
	stmts := []string{
		`CREATE TABLE IF NOT EXISTS support_sessions (
			id VARCHAR(36) PRIMARY KEY,
			code_hash CHAR(64) NOT NULL UNIQUE,
			code_hint CHAR(4) NOT NULL,
			customer_label VARCHAR(160) NOT NULL DEFAULT '',
			technician_name VARCHAR(120) NOT NULL,
			status VARCHAR(24) NOT NULL,
			requested_control BOOLEAN NOT NULL DEFAULT TRUE,
			requested_elevation BOOLEAN NOT NULL DEFAULT FALSE,
			requested_clipboard BOOLEAN NOT NULL DEFAULT FALSE,
			requested_file_transfer BOOLEAN NOT NULL DEFAULT FALSE,
			terms_accepted BOOLEAN NOT NULL DEFAULT FALSE,
			machine_name VARCHAR(255) NOT NULL DEFAULT '',
			agent_token_hash CHAR(64) NULL,
			created_at DATETIME(6) NOT NULL,
			expires_at DATETIME(6) NOT NULL,
			redeemed_at DATETIME(6) NULL,
			connected_at DATETIME(6) NULL,
			ended_at DATETIME(6) NULL,
			INDEX idx_support_sessions_status (status),
			INDEX idx_support_sessions_created (created_at),
			INDEX idx_support_sessions_expires (expires_at)
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci`,
		`CREATE TABLE IF NOT EXISTS support_events (
			id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
			session_id VARCHAR(36) NOT NULL,
			actor VARCHAR(64) NOT NULL,
			event VARCHAR(64) NOT NULL,
			details TEXT NOT NULL,
			created_at DATETIME(6) NOT NULL,
			INDEX idx_support_events_session (session_id, created_at),
			CONSTRAINT fk_support_events_session FOREIGN KEY (session_id) REFERENCES support_sessions(id) ON DELETE CASCADE
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci`,
	}
	for _, stmt := range stmts {
		if _, err := s.db.ExecContext(ctx, stmt); err != nil {
			return err
		}
	}
	return nil
}

func (s *Store) CreateSession(ctx context.Context, session Session, codeHash string) error {
	_, err := s.db.ExecContext(ctx, `INSERT INTO support_sessions
		(id, code_hash, code_hint, customer_label, technician_name, status,
		 requested_control, requested_elevation, requested_clipboard, requested_file_transfer, created_at, expires_at)
		VALUES (?, ?, ?, ?, ?, 'waiting', ?, ?, ?, ?, ?, ?)`,
		session.ID, codeHash, session.CodeHint, session.CustomerLabel, session.TechnicianName,
		session.RequestedControl, session.RequestedElevation, session.RequestedClipboard, session.RequestedFileTransfer, session.CreatedAt, session.ExpiresAt)
	return err
}

func (s *Store) expireOld(ctx context.Context) {
	_, _ = s.db.ExecContext(ctx, `UPDATE support_sessions
		SET status='expired', agent_token_hash=NULL, ended_at=COALESCE(ended_at, UTC_TIMESTAMP(6))
		WHERE status IN ('waiting','approved') AND expires_at <= UTC_TIMESTAMP(6)`)
}

func (s *Store) ListSessions(ctx context.Context, limit int) ([]Session, error) {
	s.expireOld(ctx)
	if limit < 1 || limit > 200 {
		limit = 50
	}
	rows, err := s.db.QueryContext(ctx, `SELECT id, code_hint, customer_label, technician_name, status,
		requested_control, requested_elevation, requested_clipboard, requested_file_transfer, terms_accepted, machine_name,
		COALESCE(agent_token_hash,''), created_at, expires_at, redeemed_at, connected_at, ended_at
		FROM support_sessions ORDER BY created_at DESC LIMIT ?`, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []Session
	for rows.Next() {
		var x Session
		if err := scanSession(rows, &x); err != nil {
			return nil, err
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

func (s *Store) GetSession(ctx context.Context, id string) (Session, error) {
	row := s.db.QueryRowContext(ctx, `SELECT id, code_hint, customer_label, technician_name, status,
		requested_control, requested_elevation, requested_clipboard, requested_file_transfer, terms_accepted, machine_name,
		COALESCE(agent_token_hash,''), created_at, expires_at, redeemed_at, connected_at, ended_at
		FROM support_sessions WHERE id=?`, id)
	var x Session
	if err := scanSession(row, &x); err != nil {
		return Session{}, err
	}
	return x, nil
}

type scanner interface{ Scan(dest ...any) error }

func scanSession(row scanner, x *Session) error {
	return row.Scan(&x.ID, &x.CodeHint, &x.CustomerLabel, &x.TechnicianName, &x.Status,
		&x.RequestedControl, &x.RequestedElevation, &x.RequestedClipboard, &x.RequestedFileTransfer, &x.TermsAccepted, &x.MachineName,
		&x.AgentTokenHash, &x.CreatedAt, &x.ExpiresAt, &x.RedeemedAt, &x.ConnectedAt, &x.EndedAt)
}

func (s *Store) LookupByCodeHash(ctx context.Context, codeHash string) (Session, error) {
	row := s.db.QueryRowContext(ctx, `SELECT id, code_hint, customer_label, technician_name, status,
		requested_control, requested_elevation, requested_clipboard, requested_file_transfer, terms_accepted, machine_name,
		COALESCE(agent_token_hash,''), created_at, expires_at, redeemed_at, connected_at, ended_at
		FROM support_sessions WHERE code_hash=? AND status='waiting' AND expires_at > UTC_TIMESTAMP(6)`, codeHash)
	var x Session
	if err := scanSession(row, &x); err != nil {
		return Session{}, err
	}
	return x, nil
}

func (s *Store) RedeemSession(ctx context.Context, codeHash, tokenHash, machineName string, termsAccepted bool, liveTTLArg ...time.Duration) (Session, error) {
	tx, err := s.db.BeginTx(ctx, &sql.TxOptions{Isolation: sql.LevelReadCommitted})
	if err != nil {
		return Session{}, err
	}
	defer tx.Rollback()
	row := tx.QueryRowContext(ctx, `SELECT id, code_hint, customer_label, technician_name, status,
		requested_control, requested_elevation, requested_clipboard, requested_file_transfer, terms_accepted, machine_name,
		COALESCE(agent_token_hash,''), created_at, expires_at, redeemed_at, connected_at, ended_at
		FROM support_sessions WHERE code_hash=? FOR UPDATE`, codeHash)
	var x Session
	if err := scanSession(row, &x); err != nil {
		return Session{}, err
	}
	if x.Status != "waiting" || !x.ExpiresAt.After(time.Now().UTC()) {
		return Session{}, errors.New("session is not available")
	}
	liveTTL := 8 * time.Hour
	if len(liveTTLArg) > 0 {
		liveTTL = liveTTLArg[0]
	}
	if liveTTL <= 0 {
		return Session{}, errors.New("live session TTL must be positive")
	}
	now := time.Now().UTC()
	liveExpires := now.Add(liveTTL)
	_, err = tx.ExecContext(ctx, `UPDATE support_sessions
		SET status='approved', terms_accepted=?, machine_name=?, agent_token_hash=?, redeemed_at=?, expires_at=?
		WHERE id=? AND status='waiting'`, termsAccepted, machineName, tokenHash, now, liveExpires, x.ID)
	if err != nil {
		return Session{}, err
	}
	if err := tx.Commit(); err != nil {
		return Session{}, err
	}
	x.Status = "approved"
	x.TermsAccepted = termsAccepted
	x.MachineName = machineName
	x.AgentTokenHash = tokenHash
	x.RedeemedAt = &now
	x.ExpiresAt = liveExpires
	return x, nil
}

func (s *Store) MarkAgentConnected(ctx context.Context, id string) error {
	_, err := s.db.ExecContext(ctx, `UPDATE support_sessions SET status='connected', connected_at=COALESCE(connected_at, UTC_TIMESTAMP(6)) WHERE id=? AND status IN ('approved','connected')`, id)
	return err
}

func (s *Store) MarkAgentDisconnected(ctx context.Context, id string) error {
	_, err := s.db.ExecContext(ctx, `UPDATE support_sessions SET status='approved' WHERE id=? AND status='connected' AND expires_at > UTC_TIMESTAMP(6)`, id)
	return err
}

func (s *Store) EndSession(ctx context.Context, id string) error {
	res, err := s.db.ExecContext(ctx, `UPDATE support_sessions SET status='ended', ended_at=UTC_TIMESTAMP(6), agent_token_hash=NULL WHERE id=? AND status <> 'ended'`, id)
	if err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return sql.ErrNoRows
	}
	return nil
}

func (s *Store) EndSessionByAgentToken(ctx context.Context, id, tokenHash string) error {
	res, err := s.db.ExecContext(ctx, `UPDATE support_sessions
		SET status='ended', ended_at=UTC_TIMESTAMP(6), agent_token_hash=NULL
		WHERE id=? AND agent_token_hash=? AND status IN ('approved','connected')`, id, tokenHash)
	if err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return sql.ErrNoRows
	}
	return nil
}

func (s *Store) ExpireLiveSession(ctx context.Context, id string) error {
	res, err := s.db.ExecContext(ctx, `UPDATE support_sessions
		SET status='expired', ended_at=COALESCE(ended_at, UTC_TIMESTAMP(6)), agent_token_hash=NULL
		WHERE id=? AND status IN ('approved','connected') AND expires_at <= UTC_TIMESTAMP(6)`, id)
	if err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return sql.ErrNoRows
	}
	return nil
}

func (s *Store) AddEvent(ctx context.Context, id, actor, event, details string) {
	_, _ = s.db.ExecContext(ctx, `INSERT INTO support_events (session_id, actor, event, details, created_at) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(6))`, id, actor, event, details)
}

func (s *Store) ListEvents(ctx context.Context, id string) ([]Event, error) {
	rows, err := s.db.QueryContext(ctx, `SELECT id, session_id, actor, event, details, created_at FROM support_events WHERE session_id=? ORDER BY created_at ASC`, id)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []Event
	for rows.Next() {
		var e Event
		if err := rows.Scan(&e.ID, &e.SessionID, &e.Actor, &e.Event, &e.Details, &e.CreatedAt); err != nil {
			return nil, err
		}
		out = append(out, e)
	}
	return out, rows.Err()
}

func (s *Store) Health(ctx context.Context) error {
	ctx, cancel := context.WithTimeout(ctx, 2*time.Second)
	defer cancel()
	if err := s.db.PingContext(ctx); err != nil {
		return fmt.Errorf("mysql: %w", err)
	}
	return nil
}
