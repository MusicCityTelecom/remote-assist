package app

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strings"
	"time"
)

type SessionQuery struct {
	Search       string
	Status       string
	TechnicianID int64
	From         *time.Time
	To           *time.Time
	Limit        int
	Offset       int
}

type SessionSearchResult struct {
	Sessions []Session `json:"sessions"`
	Total    int       `json:"total"`
	Limit    int       `json:"limit"`
	Offset   int       `json:"offset"`
}

type DashboardMetrics struct {
	Waiting        int     `json:"waiting"`
	Connected      int     `json:"connected"`
	CreatedToday   int     `json:"created_today"`
	EndedSevenDays int     `json:"ended_seven_days"`
	TotalThirtyDays int    `json:"total_thirty_days"`
	AverageMinutes float64 `json:"average_minutes"`
}

type SessionNote struct {
	ID            int64     `json:"id"`
	SessionID     string    `json:"session_id"`
	AdminID       int64     `json:"admin_id"`
	AdminUsername string    `json:"admin_username"`
	Body          string    `json:"body"`
	CreatedAt     time.Time `json:"created_at"`
}

func (s *Store) SearchSessions(ctx context.Context, q SessionQuery) (SessionSearchResult, error) {
	s.expireOld(ctx)
	if q.Limit < 1 || q.Limit > 500 {
		q.Limit = 100
	}
	if q.Offset < 0 {
		q.Offset = 0
	}
	where := []string{"1=1"}
	args := []any{}
	search := strings.TrimSpace(q.Search)
	if search != "" {
		if len(search) > 160 {
			search = search[:160]
		}
		like := "%" + search + "%"
		where = append(where, "(customer_label LIKE ? OR machine_name LIKE ? OR technician_name LIKE ? OR code_hint LIKE ?)")
		args = append(args, like, like, like, like)
	}
	switch q.Status {
	case "", "all":
	case "active":
		where = append(where, "status IN ('waiting','approved','connected')")
	case "waiting", "approved", "connected", "ended", "expired":
		where = append(where, "status=?")
		args = append(args, q.Status)
	default:
		return SessionSearchResult{}, errors.New("invalid status filter")
	}
	if q.TechnicianID > 0 {
		where = append(where, "technician_id=?")
		args = append(args, q.TechnicianID)
	}
	if q.From != nil {
		where = append(where, "created_at>=?")
		args = append(args, q.From.UTC())
	}
	if q.To != nil {
		where = append(where, "created_at<?")
		args = append(args, q.To.UTC())
	}
	clause := strings.Join(where, " AND ")
	var total int
	if err := s.db.QueryRowContext(ctx, "SELECT COUNT(*) FROM support_sessions WHERE "+clause, args...).Scan(&total); err != nil {
		return SessionSearchResult{}, err
	}
	query := `SELECT id, code_hint, customer_label, technician_name, technician_id, status,
		requested_control, requested_elevation, requested_clipboard, requested_file_transfer, terms_accepted, machine_name,
		COALESCE(agent_token_hash,''), created_at, expires_at, redeemed_at, connected_at, ended_at
		FROM support_sessions WHERE ` + clause + ` ORDER BY created_at DESC LIMIT ? OFFSET ?`
	pageArgs := append(append([]any{}, args...), q.Limit, q.Offset)
	rows, err := s.db.QueryContext(ctx, query, pageArgs...)
	if err != nil {
		return SessionSearchResult{}, err
	}
	defer rows.Close()
	out := make([]Session, 0, q.Limit)
	for rows.Next() {
		var x Session
		if err := rows.Scan(&x.ID, &x.CodeHint, &x.CustomerLabel, &x.TechnicianName, &x.TechnicianID, &x.Status,
			&x.RequestedControl, &x.RequestedElevation, &x.RequestedClipboard, &x.RequestedFileTransfer, &x.TermsAccepted, &x.MachineName,
			&x.AgentTokenHash, &x.CreatedAt, &x.ExpiresAt, &x.RedeemedAt, &x.ConnectedAt, &x.EndedAt); err != nil {
			return SessionSearchResult{}, err
		}
		out = append(out, x)
	}
	return SessionSearchResult{Sessions: out, Total: total, Limit: q.Limit, Offset: q.Offset}, rows.Err()
}

func (s *Store) DashboardMetrics(ctx context.Context) (DashboardMetrics, error) {
	s.expireOld(ctx)
	var m DashboardMetrics
	row := s.db.QueryRowContext(ctx, `SELECT
		COALESCE(SUM(status IN ('waiting','approved')),0),
		COALESCE(SUM(status='connected'),0),
		COALESCE(SUM(created_at >= UTC_DATE()),0),
		COALESCE(SUM(status='ended' AND ended_at >= UTC_TIMESTAMP() - INTERVAL 7 DAY),0),
		COALESCE(SUM(created_at >= UTC_TIMESTAMP() - INTERVAL 30 DAY),0),
		COALESCE(AVG(CASE WHEN ended_at IS NOT NULL AND connected_at IS NOT NULL THEN TIMESTAMPDIFF(SECOND, connected_at, ended_at) END),0)
		FROM support_sessions`)
	var avgSeconds float64
	if err := row.Scan(&m.Waiting, &m.Connected, &m.CreatedToday, &m.EndedSevenDays, &m.TotalThirtyDays, &avgSeconds); err != nil {
		return DashboardMetrics{}, err
	}
	m.AverageMinutes = avgSeconds / 60
	return m, nil
}

func (s *Store) AddSessionNote(ctx context.Context, sessionID string, admin Admin, body string) (SessionNote, error) {
	body = strings.TrimSpace(body)
	if body == "" {
		return SessionNote{}, errors.New("note cannot be empty")
	}
	if len(body) > 4000 {
		return SessionNote{}, errors.New("note must be 4000 characters or fewer")
	}
	if _, err := s.GetSession(ctx, sessionID); err != nil {
		return SessionNote{}, err
	}
	res, err := s.db.ExecContext(ctx, `INSERT INTO support_session_notes
		(session_id, admin_id, admin_username, body, created_at)
		VALUES (?, ?, ?, ?, UTC_TIMESTAMP(6))`, sessionID, admin.ID, admin.Username, body)
	if err != nil {
		return SessionNote{}, err
	}
	id, _ := res.LastInsertId()
	var note SessionNote
	err = s.db.QueryRowContext(ctx, `SELECT id, session_id, admin_id, admin_username, body, created_at
		FROM support_session_notes WHERE id=?`, id).
		Scan(&note.ID, &note.SessionID, &note.AdminID, &note.AdminUsername, &note.Body, &note.CreatedAt)
	return note, err
}

func (s *Store) ListSessionNotes(ctx context.Context, sessionID string) ([]SessionNote, error) {
	rows, err := s.db.QueryContext(ctx, `SELECT id, session_id, admin_id, admin_username, body, created_at
		FROM support_session_notes WHERE session_id=? ORDER BY created_at ASC`, sessionID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []SessionNote
	for rows.Next() {
		var n SessionNote
		if err := rows.Scan(&n.ID, &n.SessionID, &n.AdminID, &n.AdminUsername, &n.Body, &n.CreatedAt); err != nil {
			return nil, err
		}
		out = append(out, n)
	}
	return out, rows.Err()
}

func (s *Store) GetSessionWithTechnician(ctx context.Context, id string) (Session, error) {
	row := s.db.QueryRowContext(ctx, `SELECT id, code_hint, customer_label, technician_name, technician_id, status,
		requested_control, requested_elevation, requested_clipboard, requested_file_transfer, terms_accepted, machine_name,
		COALESCE(agent_token_hash,''), created_at, expires_at, redeemed_at, connected_at, ended_at
		FROM support_sessions WHERE id=?`, id)
	var x Session
	if err := row.Scan(&x.ID, &x.CodeHint, &x.CustomerLabel, &x.TechnicianName, &x.TechnicianID, &x.Status,
		&x.RequestedControl, &x.RequestedElevation, &x.RequestedClipboard, &x.RequestedFileTransfer, &x.TermsAccepted, &x.MachineName,
		&x.AgentTokenHash, &x.CreatedAt, &x.ExpiresAt, &x.RedeemedAt, &x.ConnectedAt, &x.EndedAt); err != nil {
		return Session{}, err
	}
	return x, nil
}

func (s *Store) SessionExists(ctx context.Context, id string) bool {
	var one int
	return s.db.QueryRowContext(ctx, `SELECT 1 FROM support_sessions WHERE id=?`, id).Scan(&one) == nil
}

func (s *Store) AdminSummary(ctx context.Context) (map[string]int, error) {
	rows, err := s.db.QueryContext(ctx, `SELECT role, active, COUNT(*) FROM support_admins GROUP BY role, active`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[string]int{"admins": 0, "technicians": 0, "active": 0, "inactive": 0}
	for rows.Next() {
		var role string
		var active bool
		var count int
		if err := rows.Scan(&role, &active, &count); err != nil {
			return nil, err
		}
		if role == "admin" {
			out["admins"] += count
		} else if role == "technician" {
			out["technicians"] += count
		}
		if active {
			out["active"] += count
		} else {
			out["inactive"] += count
		}
	}
	return out, rows.Err()
}

var _ = sql.ErrNoRows
var _ = fmt.Sprintf
