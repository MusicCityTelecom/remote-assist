package app

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"
)

type Admin struct {
	ID          int64      `json:"id"`
	Username    string     `json:"username"`
	DisplayName string     `json:"display_name"`
	Role        string     `json:"role"`
	Active      bool       `json:"active"`
	AuthVersion int64      `json:"-"`
	CreatedAt   time.Time  `json:"created_at"`
	UpdatedAt   time.Time  `json:"updated_at"`
	LastLoginAt *time.Time `json:"last_login_at,omitempty"`
}

type adminCredential struct {
	Admin
	PasswordSalt       string
	PasswordHash       string
	PasswordIterations int
}

type TechnicianDirectoryEntry struct {
	ID          int64  `json:"id"`
	Username    string `json:"username"`
	DisplayName string `json:"display_name"`
	Active      bool   `json:"active"`
}

type AdminAudit struct {
	ID            int64     `json:"id"`
	ActorAdminID  *int64    `json:"actor_admin_id,omitempty"`
	ActorUsername string    `json:"actor_username"`
	Event         string    `json:"event"`
	TargetType    string    `json:"target_type"`
	TargetID      string    `json:"target_id"`
	Details       string    `json:"details"`
	IPAddress     string    `json:"ip_address"`
	CreatedAt     time.Time `json:"created_at"`
}

func (s *Store) migrateOperations(ctx context.Context) error {
	stmts := []string{
		`CREATE TABLE IF NOT EXISTS support_admins (
			id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
			username VARCHAR(48) NOT NULL UNIQUE,
			display_name VARCHAR(120) NOT NULL,
			role VARCHAR(24) NOT NULL DEFAULT 'technician',
			active BOOLEAN NOT NULL DEFAULT TRUE,
			password_salt CHAR(48) NOT NULL,
			password_hash CHAR(64) NOT NULL,
			password_iterations INT NOT NULL,
			auth_version BIGINT NOT NULL DEFAULT 1,
			created_at DATETIME(6) NOT NULL,
			updated_at DATETIME(6) NOT NULL,
			last_login_at DATETIME(6) NULL,
			INDEX idx_support_admins_active_role (active, role)
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci`,
		`CREATE TABLE IF NOT EXISTS support_admin_audit (
			id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
			actor_admin_id BIGINT UNSIGNED NULL,
			actor_username VARCHAR(48) NOT NULL DEFAULT '',
			event VARCHAR(80) NOT NULL,
			target_type VARCHAR(48) NOT NULL DEFAULT '',
			target_id VARCHAR(80) NOT NULL DEFAULT '',
			details TEXT NOT NULL,
			ip_address VARCHAR(64) NOT NULL DEFAULT '',
			created_at DATETIME(6) NOT NULL,
			INDEX idx_support_admin_audit_created (created_at),
			INDEX idx_support_admin_audit_actor (actor_admin_id, created_at)
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci`,
		`CREATE TABLE IF NOT EXISTS support_session_notes (
			id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
			session_id VARCHAR(36) NOT NULL,
			admin_id BIGINT UNSIGNED NOT NULL,
			admin_username VARCHAR(48) NOT NULL,
			body TEXT NOT NULL,
			created_at DATETIME(6) NOT NULL,
			INDEX idx_support_session_notes_session (session_id, created_at),
			CONSTRAINT fk_support_notes_session FOREIGN KEY (session_id) REFERENCES support_sessions(id) ON DELETE CASCADE
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci`,
	}
	for _, stmt := range stmts {
		if _, err := s.db.ExecContext(ctx, stmt); err != nil {
			return err
		}
	}
	if err := s.ensureColumn(ctx, "support_sessions", "technician_id", "BIGINT UNSIGNED NULL AFTER technician_name"); err != nil {
		return err
	}
	if err := s.ensureColumn(ctx, "support_sessions", "requested_clipboard", "BOOLEAN NOT NULL DEFAULT FALSE AFTER requested_elevation"); err != nil {
		return err
	}
	if err := s.ensureColumn(ctx, "support_sessions", "requested_file_transfer", "BOOLEAN NOT NULL DEFAULT FALSE AFTER requested_clipboard"); err != nil {
		return err
	}
	return nil
}

func (s *Store) ensureColumn(ctx context.Context, table, column, ddl string) error {
	var n int
	err := s.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM information_schema.COLUMNS
		WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=? AND COLUMN_NAME=?`, table, column).Scan(&n)
	if err != nil {
		return err
	}
	if n > 0 {
		return nil
	}
	allowed := map[string]bool{
		"support_sessions.technician_id": true,
		"support_sessions.requested_clipboard": true,
		"support_sessions.requested_file_transfer": true,
		"support_chat_messages.attachment_transfer_id": true,
		"support_chat_messages.attachment_name": true,
		"support_chat_messages.attachment_mime": true,
		"support_chat_messages.attachment_size": true,
	}
	if !allowed[table+"."+column] {
		return fmt.Errorf("migration attempted unexpected column %s.%s", table, column)
	}
	_, err = s.db.ExecContext(ctx, fmt.Sprintf("ALTER TABLE %s ADD COLUMN %s %s", table, column, ddl))
	return err
}

func (s *Store) EnsureBootstrapAdmin(ctx context.Context, username, password string) (bool, error) {
	var count int
	if err := s.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM support_admins`).Scan(&count); err != nil {
		return false, err
	}
	if count > 0 {
		return false, nil
	}
	username = normalizeUsername(username)
	if err := validateUsername(username); err != nil {
		return false, err
	}
	salt, hash, iterations, err := passwordHash(password)
	if err != nil {
		return false, err
	}
	now := time.Now().UTC()
	res, err := s.db.ExecContext(ctx, `INSERT INTO support_admins
		(username, display_name, role, active, password_salt, password_hash, password_iterations, auth_version, created_at, updated_at)
		VALUES (?, 'Administrator', 'admin', TRUE, ?, ?, ?, 1, ?, ?)`,
		username, salt, hash, iterations, now, now)
	if err != nil {
		return false, err
	}
	id, _ := res.LastInsertId()
	s.AddAdminAudit(ctx, &id, username, "bootstrap_admin_created", "admin", fmt.Sprintf("%d", id), "initial administrator created from environment bootstrap credential", "")
	return true, nil
}

func scanAdmin(row scanner, a *Admin) error {
	return row.Scan(&a.ID, &a.Username, &a.DisplayName, &a.Role, &a.Active, &a.AuthVersion, &a.CreatedAt, &a.UpdatedAt, &a.LastLoginAt)
}

func (s *Store) GetAdminByID(ctx context.Context, id int64) (Admin, error) {
	row := s.db.QueryRowContext(ctx, `SELECT id, username, display_name, role, active, auth_version, created_at, updated_at, last_login_at
		FROM support_admins WHERE id=?`, id)
	var a Admin
	if err := scanAdmin(row, &a); err != nil {
		return Admin{}, err
	}
	return a, nil
}

func (s *Store) GetAdminCredentialByUsername(ctx context.Context, username string) (adminCredential, error) {
	row := s.db.QueryRowContext(ctx, `SELECT id, username, display_name, role, active, auth_version, created_at, updated_at, last_login_at,
		password_salt, password_hash, password_iterations FROM support_admins WHERE username=?`, normalizeUsername(username))
	var a adminCredential
	if err := row.Scan(&a.ID, &a.Username, &a.DisplayName, &a.Role, &a.Active, &a.AuthVersion, &a.CreatedAt, &a.UpdatedAt, &a.LastLoginAt,
		&a.PasswordSalt, &a.PasswordHash, &a.PasswordIterations); err != nil {
		return adminCredential{}, err
	}
	return a, nil
}

func (s *Store) ListTechnicianDirectory(ctx context.Context) ([]TechnicianDirectoryEntry, error) {
	rows, err := s.db.QueryContext(ctx, `SELECT id, username, display_name, active
		FROM support_admins ORDER BY active DESC, display_name ASC, username ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []TechnicianDirectoryEntry
	for rows.Next() {
		var x TechnicianDirectoryEntry
		if err := rows.Scan(&x.ID, &x.Username, &x.DisplayName, &x.Active); err != nil {
			return nil, err
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

func (s *Store) ListAdmins(ctx context.Context) ([]Admin, error) {
	rows, err := s.db.QueryContext(ctx, `SELECT id, username, display_name, role, active, auth_version, created_at, updated_at, last_login_at
		FROM support_admins ORDER BY active DESC, display_name ASC, username ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []Admin
	for rows.Next() {
		var a Admin
		if err := scanAdmin(rows, &a); err != nil {
			return nil, err
		}
		out = append(out, a)
	}
	return out, rows.Err()
}

func (s *Store) CreateAdmin(ctx context.Context, username, displayName, role, password string) (Admin, error) {
	username = normalizeUsername(username)
	displayName = strings.TrimSpace(displayName)
	if err := validateUsername(username); err != nil {
		return Admin{}, err
	}
	if displayName == "" || len(displayName) > 120 {
		return Admin{}, errors.New("display name is required and must be at most 120 characters")
	}
	if err := validateRole(role); err != nil {
		return Admin{}, err
	}
	salt, hash, iterations, err := passwordHash(password)
	if err != nil {
		return Admin{}, err
	}
	now := time.Now().UTC()
	res, err := s.db.ExecContext(ctx, `INSERT INTO support_admins
		(username, display_name, role, active, password_salt, password_hash, password_iterations, auth_version, created_at, updated_at)
		VALUES (?, ?, ?, TRUE, ?, ?, ?, 1, ?, ?)`,
		username, displayName, role, salt, hash, iterations, now, now)
	if err != nil {
		return Admin{}, err
	}
	id, _ := res.LastInsertId()
	return s.GetAdminByID(ctx, id)
}

func (s *Store) UpdateAdmin(ctx context.Context, id int64, displayName, role string, active bool) (Admin, error) {
	displayName = strings.TrimSpace(displayName)
	if displayName == "" || len(displayName) > 120 {
		return Admin{}, errors.New("display name is required and must be at most 120 characters")
	}
	if err := validateRole(role); err != nil {
		return Admin{}, err
	}
	current, err := s.GetAdminByID(ctx, id)
	if err != nil {
		return Admin{}, err
	}
	if current.Role == "admin" && current.Active && (role != "admin" || !active) {
		var activeAdmins int
		if err := s.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM support_admins WHERE active=TRUE AND role='admin'`).Scan(&activeAdmins); err != nil {
			return Admin{}, err
		}
		if activeAdmins <= 1 {
			return Admin{}, errors.New("cannot deactivate or demote the last active administrator")
		}
	}
	authBump := current.Role != role || current.Active != active
	query := `UPDATE support_admins SET display_name=?, role=?, active=?, updated_at=UTC_TIMESTAMP(6)`
	args := []any{displayName, role, active}
	if authBump {
		query += `, auth_version=auth_version+1`
	}
	query += ` WHERE id=?`
	args = append(args, id)
	if _, err := s.db.ExecContext(ctx, query, args...); err != nil {
		return Admin{}, err
	}
	return s.GetAdminByID(ctx, id)
}

func (s *Store) ChangeAdminPassword(ctx context.Context, id int64, password string) error {
	salt, hash, iterations, err := passwordHash(password)
	if err != nil {
		return err
	}
	res, err := s.db.ExecContext(ctx, `UPDATE support_admins SET password_salt=?, password_hash=?, password_iterations=?,
		auth_version=auth_version+1, updated_at=UTC_TIMESTAMP(6) WHERE id=?`, salt, hash, iterations, id)
	if err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return sql.ErrNoRows
	}
	return nil
}

func (s *Store) RecordAdminLogin(ctx context.Context, id int64) {
	_, _ = s.db.ExecContext(ctx, `UPDATE support_admins SET last_login_at=UTC_TIMESTAMP(6) WHERE id=?`, id)
}

func (s *Store) SetSessionTechnicianID(ctx context.Context, sessionID string, adminID int64) {
	_, _ = s.db.ExecContext(ctx, `UPDATE support_sessions SET technician_id=? WHERE id=?`, adminID, sessionID)
}

func (s *Store) AddAdminAudit(ctx context.Context, actorID *int64, actorUsername, event, targetType, targetID, details, ip string) {
	if len(details) > 4000 {
		details = details[:4000]
	}
	_, _ = s.db.ExecContext(ctx, `INSERT INTO support_admin_audit
		(actor_admin_id, actor_username, event, target_type, target_id, details, ip_address, created_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(6))`,
		actorID, actorUsername, event, targetType, targetID, details, ip)
}

func (s *Store) ListAdminAudit(ctx context.Context, limit int) ([]AdminAudit, error) {
	if limit < 1 || limit > 500 {
		limit = 200
	}
	rows, err := s.db.QueryContext(ctx, `SELECT id, actor_admin_id, actor_username, event, target_type, target_id, details, ip_address, created_at
		FROM support_admin_audit ORDER BY created_at DESC LIMIT ?`, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []AdminAudit
	for rows.Next() {
		var x AdminAudit
		if err := rows.Scan(&x.ID, &x.ActorAdminID, &x.ActorUsername, &x.Event, &x.TargetType, &x.TargetID, &x.Details, &x.IPAddress, &x.CreatedAt); err != nil {
			return nil, err
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

func auditJSON(v any) string {
	b, err := json.Marshal(v)
	if err != nil {
		return "{}"
	}
	return string(b)
}
