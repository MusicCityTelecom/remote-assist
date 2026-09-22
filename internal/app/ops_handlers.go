package app

import (
	"database/sql"
	"encoding/csv"
	"errors"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"
)

func currentAdmin(ctx anyContext) Admin {
	a, _ := ctx.Value(techContextKey{}).(Admin)
	return a
}

type anyContext interface {
	Value(any) any
}

func (s *Server) requireAdminRole(next http.HandlerFunc) http.HandlerFunc {
	return s.requireTech(func(w http.ResponseWriter, r *http.Request) {
		admin := currentAdmin(r.Context())
		if admin.Role != "admin" {
			writeJSON(w, http.StatusForbidden, map[string]any{"error": "administrator role required"})
			return
		}
		next(w, r)
	})
}

func parseIntParam(r *http.Request, name string, fallback int) int {
	v := strings.TrimSpace(r.URL.Query().Get(name))
	if v == "" {
		return fallback
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		return fallback
	}
	return n
}

func parseDateParam(v string, end bool) (*time.Time, error) {
	v = strings.TrimSpace(v)
	if v == "" {
		return nil, nil
	}
	t, err := time.Parse("2006-01-02", v)
	if err != nil {
		return nil, errors.New("dates must use YYYY-MM-DD")
	}
	if end {
		t = t.AddDate(0, 0, 1)
	}
	return &t, nil
}

func sessionQueryFromRequest(r *http.Request, maxLimit int) (SessionQuery, error) {
	from, err := parseDateParam(r.URL.Query().Get("from"), false)
	if err != nil {
		return SessionQuery{}, err
	}
	to, err := parseDateParam(r.URL.Query().Get("to"), true)
	if err != nil {
		return SessionQuery{}, err
	}
	techID, _ := strconv.ParseInt(r.URL.Query().Get("technician_id"), 10, 64)
	limit := parseIntParam(r, "limit", 100)
	if maxLimit > 0 && limit > maxLimit {
		limit = maxLimit
	}
	return SessionQuery{
		Search: r.URL.Query().Get("q"), Status: r.URL.Query().Get("status"),
		TechnicianID: techID, From: from, To: to,
		Limit: limit, Offset: parseIntParam(r, "offset", 0),
	}, nil
}

func (s *Server) handleTechnicianDirectory(w http.ResponseWriter, r *http.Request) {
	items, err := s.store.ListTechnicianDirectory(r.Context())
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not load technician directory"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"technicians": items})
}

func (s *Server) handleDashboardMetrics(w http.ResponseWriter, r *http.Request) {
	m, err := s.store.DashboardMetrics(r.Context())
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not load dashboard metrics"})
		return
	}
	writeJSON(w, http.StatusOK, m)
}

func (s *Server) handleSessionExport(w http.ResponseWriter, r *http.Request) {
	q, err := sessionQueryFromRequest(r, 500)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}
	q.Limit = 500
	q.Offset = 0
	result, err := s.store.SearchSessions(r.Context(), q)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}
	w.Header().Set("Content-Type", "text/csv; charset=utf-8")
	w.Header().Set("Content-Disposition", `attachment; filename="remote-assist-history.csv"`)
	w.Header().Set("Cache-Control", "no-store")
	cw := csv.NewWriter(w)
	_ = cw.Write([]string{"session_id", "customer", "machine", "technician", "status", "created_utc", "connected_utc", "ended_utc", "control", "clipboard", "file_transfer", "elevation"})
	for _, x := range result.Sessions {
		connected, ended := "", ""
		if x.ConnectedAt != nil {
			connected = x.ConnectedAt.UTC().Format(time.RFC3339)
		}
		if x.EndedAt != nil {
			ended = x.EndedAt.UTC().Format(time.RFC3339)
		}
		_ = cw.Write([]string{x.ID, x.CustomerLabel, x.MachineName, x.TechnicianName, x.Status,
			x.CreatedAt.UTC().Format(time.RFC3339), connected, ended,
			strconv.FormatBool(x.RequestedControl), strconv.FormatBool(x.RequestedClipboard), strconv.FormatBool(x.RequestedFileTransfer), strconv.FormatBool(x.RequestedElevation)})
	}
	cw.Flush()
	admin := currentAdmin(r.Context())
	s.store.AddAdminAudit(r.Context(), &admin.ID, admin.Username, "session_history_exported", "session", "", fmt.Sprintf("rows=%d", len(result.Sessions)), s.clientIP(r))
}

func (s *Server) handleAddSessionNote(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}
	var req struct {
		Body string `json:"body"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	admin := currentAdmin(r.Context())
	note, err := s.store.AddSessionNote(r.Context(), r.PathValue("id"), admin, req.Body)
	if err != nil {
		status := http.StatusBadRequest
		if errors.Is(err, sql.ErrNoRows) {
			status = http.StatusNotFound
		}
		writeJSON(w, status, map[string]any{"error": err.Error()})
		return
	}
	s.store.AddEvent(r.Context(), note.SessionID, admin.Username, "note_added", "")
	s.store.AddAdminAudit(r.Context(), &admin.ID, admin.Username, "session_note_added", "session", note.SessionID, fmt.Sprintf("note_id=%d", note.ID), s.clientIP(r))
	writeJSON(w, http.StatusCreated, map[string]any{"note": note})
}

func (s *Server) handleListAdmins(w http.ResponseWriter, r *http.Request) {
	admins, err := s.store.ListAdmins(r.Context())
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not load administrators"})
		return
	}
	summary, _ := s.store.AdminSummary(r.Context())
	writeJSON(w, http.StatusOK, map[string]any{"admins": admins, "summary": summary})
}

func (s *Server) handleCreateAdmin(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}
	var req struct {
		Username    string `json:"username"`
		DisplayName string `json:"display_name"`
		Role        string `json:"role"`
		Password    string `json:"password"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	created, err := s.store.CreateAdmin(r.Context(), req.Username, req.DisplayName, req.Role, req.Password)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}
	actor := currentAdmin(r.Context())
	s.store.AddAdminAudit(r.Context(), &actor.ID, actor.Username, "admin_created", "admin", adminIDString(created.ID),
		auditJSON(map[string]any{"username": created.Username, "display_name": created.DisplayName, "role": created.Role}), s.clientIP(r))
	writeJSON(w, http.StatusCreated, map[string]any{"admin": created})
}

func (s *Server) handleUpdateAdmin(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}
	id, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil || id <= 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "invalid administrator id"})
		return
	}
	var req struct {
		DisplayName string `json:"display_name"`
		Role        string `json:"role"`
		Active      bool   `json:"active"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	actor := currentAdmin(r.Context())
	if actor.ID == id && (!req.Active || req.Role != "admin") {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "you cannot deactivate or demote your own administrator account"})
		return
	}
	updated, err := s.store.UpdateAdmin(r.Context(), id, req.DisplayName, req.Role, req.Active)
	if err != nil {
		status := http.StatusBadRequest
		if errors.Is(err, sql.ErrNoRows) {
			status = http.StatusNotFound
		}
		writeJSON(w, status, map[string]any{"error": err.Error()})
		return
	}
	s.store.AddAdminAudit(r.Context(), &actor.ID, actor.Username, "admin_updated", "admin", adminIDString(id),
		auditJSON(map[string]any{"display_name": updated.DisplayName, "role": updated.Role, "active": updated.Active}), s.clientIP(r))
	writeJSON(w, http.StatusOK, map[string]any{"admin": updated})
}

func (s *Server) handleResetAdminPassword(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}
	id, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil || id <= 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "invalid administrator id"})
		return
	}
	var req struct {
		Password string `json:"password"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	target, err := s.store.GetAdminByID(r.Context(), id)
	if err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "administrator not found"})
		return
	}
	if err := s.store.ChangeAdminPassword(r.Context(), id, req.Password); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}
	actor := currentAdmin(r.Context())
	s.store.AddAdminAudit(r.Context(), &actor.ID, actor.Username, "admin_password_reset", "admin", adminIDString(id),
		auditJSON(map[string]any{"username": target.Username}), s.clientIP(r))
	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
}

func (s *Server) handleChangeOwnPassword(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}
	var req struct {
		CurrentPassword string `json:"current_password"`
		NewPassword     string `json:"new_password"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	admin := currentAdmin(r.Context())
	cred, err := s.store.GetAdminCredentialByUsername(r.Context(), admin.Username)
	if err != nil || !verifyPassword(req.CurrentPassword, cred.PasswordSalt, cred.PasswordHash, cred.PasswordIterations) {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "current password is incorrect"})
		return
	}
	if err := s.store.ChangeAdminPassword(r.Context(), admin.ID, req.NewPassword); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}
	s.store.AddAdminAudit(r.Context(), &admin.ID, admin.Username, "own_password_changed", "admin", adminIDString(admin.ID), "", s.clientIP(r))
	clearTechCookie(w)
	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "reauthenticate": true})
}

func (s *Server) handleAdminAudit(w http.ResponseWriter, r *http.Request) {
	items, err := s.store.ListAdminAudit(r.Context(), parseIntParam(r, "limit", 200))
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not load audit log"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"events": items})
}
