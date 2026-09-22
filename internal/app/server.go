package app

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log"
	"mime"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/remote-assist/remote-assist/internal/webui"
	"github.com/gorilla/websocket"
)

const maxClipboardBytes = 256 * 1024

type Server struct {
	cfg          Config
	store        *Store
	hub          *Hub
	transfers    *TransferStore
	mux          *http.ServeMux
	agentLimiter *limiter
	loginLimiter *limiter
	upgrader     websocket.Upgrader
}

func NewServer(cfg Config, store *Store) *Server {
	s := &Server{
		cfg:          cfg,
		store:        store,
		hub:          NewHub(),
		transfers:    NewTransferStore(),
		mux:          http.NewServeMux(),
		agentLimiter: newLimiter(12, 10*time.Minute),
		loginLimiter: newLimiter(10, 15*time.Minute),
	}
	s.upgrader = websocket.Upgrader{
		HandshakeTimeout: 10 * time.Second,
		ReadBufferSize:   64 * 1024,
		WriteBufferSize:  64 * 1024,
		CheckOrigin:      s.checkOrigin,
	}
	s.routes()
	return s
}

func (s *Server) Handler() http.Handler {
	return s.securityHeaders(s.logRequests(s.mux))
}

func (s *Server) routes() {
	s.mux.HandleFunc("GET /api/health", s.handleHealth)
	s.mux.HandleFunc("POST /api/login", s.handleLogin)
	s.mux.HandleFunc("POST /api/logout", s.handleLogout)
	s.mux.HandleFunc("GET /api/me", s.requireTech(s.handleMe))
	s.mux.HandleFunc("POST /api/account/password", s.requireTech(s.handleChangeOwnPassword))
	s.mux.HandleFunc("GET /api/dashboard/metrics", s.requireTech(s.handleDashboardMetrics))
	s.mux.HandleFunc("GET /api/technicians", s.requireTech(s.handleTechnicianDirectory))
	s.mux.HandleFunc("GET /api/sessions", s.requireTech(s.handleListSessions))
	s.mux.HandleFunc("GET /api/sessions/export", s.requireTech(s.handleSessionExport))
	s.mux.HandleFunc("POST /api/sessions", s.requireTech(s.handleCreateSession))
	s.mux.HandleFunc("GET /api/sessions/{id}", s.requireTech(s.handleGetSession))
	s.mux.HandleFunc("GET /api/sessions/{id}/chat", s.requireTech(s.handleChatHistory))
	s.mux.HandleFunc("POST /api/sessions/{id}/end", s.requireTech(s.handleEndSession))
	s.mux.HandleFunc("POST /api/sessions/{id}/notes", s.requireTech(s.handleAddSessionNote))
	s.mux.HandleFunc("POST /api/sessions/{id}/files", s.requireTech(s.handleTechFileUpload))
	s.mux.HandleFunc("GET /api/sessions/{id}/files", s.requireTech(s.handleTechFileList))
	s.mux.HandleFunc("GET /api/sessions/{id}/files/{transfer}", s.requireTech(s.handleTechFileDownload))
	s.mux.HandleFunc("GET /api/admins", s.requireAdminRole(s.handleListAdmins))
	s.mux.HandleFunc("POST /api/admins", s.requireAdminRole(s.handleCreateAdmin))
	s.mux.HandleFunc("PATCH /api/admins/{id}", s.requireAdminRole(s.handleUpdateAdmin))
	s.mux.HandleFunc("POST /api/admins/{id}/password", s.requireAdminRole(s.handleResetAdminPassword))
	s.mux.HandleFunc("GET /api/admin-audit", s.requireAdminRole(s.handleAdminAudit))
	s.mux.HandleFunc("POST /api/agent/lookup", s.handleAgentLookup)
	s.mux.HandleFunc("POST /api/agent/redeem", s.handleAgentRedeem)
	s.mux.HandleFunc("POST /api/agent/end", s.handleAgentEnd)
	s.mux.HandleFunc("POST /api/agent/files", s.handleAgentFileUpload)
	s.mux.HandleFunc("GET /api/agent/files/{transfer}", s.handleAgentFileDownload)
	s.mux.HandleFunc("GET /api/download-status", s.handleDownloadStatus)
	s.mux.HandleFunc("GET /download/windows", s.handleAgentDownload)
	s.mux.HandleFunc("GET /api/technician-downloads", s.requireTech(s.handleTechnicianDownloadStatus))
	s.mux.HandleFunc("GET /download/technician/portable", s.requireTech(s.handleTechnicianPortableDownload))
	s.mux.HandleFunc("GET /download/technician/installer", s.requireTech(s.handleTechnicianInstallerDownload))
	s.mux.HandleFunc("GET /ws/agent", s.handleAgentWS)
	s.mux.HandleFunc("GET /ws/tech", s.requireTech(s.handleTechWS))

	webFS, err := fs.Sub(webui.Files, "web")
	if err != nil {
		panic(err)
	}
	fileServer := http.FileServer(http.FS(webFS))
	s.mux.Handle("GET /", fileServer)
}

func (s *Server) securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Referrer-Policy", "same-origin")
		w.Header().Set("Permissions-Policy", "camera=(), microphone=(), geolocation=()")
		w.Header().Set("Content-Security-Policy", "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; connect-src 'self' wss:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
		next.ServeHTTP(w, r)
	})
}

func (s *Server) logRequests(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		if !strings.HasPrefix(r.URL.Path, "/api/health") {
			log.Printf("%s %s from=%s elapsed=%s", r.Method, r.URL.Path, s.clientIP(r), time.Since(start).Round(time.Millisecond))
		}
	})
}

func (s *Server) clientIP(r *http.Request) string {
	if s.cfg.TrustProxy {
		if x := strings.TrimSpace(strings.Split(r.Header.Get("X-Forwarded-For"), ",")[0]); net.ParseIP(x) != nil {
			return x
		}
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err == nil {
		return host
	}
	return r.RemoteAddr
}

func (s *Server) checkOrigin(r *http.Request) bool {
	origin := r.Header.Get("Origin")
	if origin == "" {
		return true // native customer agent
	}
	u, err := url.Parse(origin)
	if err != nil {
		return false
	}
	return strings.EqualFold(u.Host, r.Host)
}

func (s *Server) checkStateChangingOrigin(r *http.Request) bool {
	origin := r.Header.Get("Origin")
	if origin == "" {
		return true
	}
	u, err := url.Parse(origin)
	return err == nil && strings.EqualFold(u.Host, r.Host)
}

func (s *Server) requireTech(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		admin, err := authenticateAdmin(r, s.cfg, s.store)
		if err != nil {
			clearTechCookie(w)
			writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "authentication required"})
			return
		}
		ctx := context.WithValue(r.Context(), techContextKey{}, admin)
		next(w, r.WithContext(ctx))
	}
}

type techContextKey struct{}

func techName(ctx context.Context) string {
	a, _ := ctx.Value(techContextKey{}).(Admin)
	if strings.TrimSpace(a.DisplayName) != "" {
		return a.DisplayName
	}
	return a.Username
}

func decodeJSON(w http.ResponseWriter, r *http.Request, dst any) bool {
	defer r.Body.Close()
	dec := json.NewDecoder(io.LimitReader(r.Body, 64*1024))
	dec.DisallowUnknownFields()
	if err := dec.Decode(dst); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "invalid JSON request"})
		return false
	}
	return true
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func (s *Server) handleHealth(w http.ResponseWriter, r *http.Request) {
	if err := s.store.Health(r.Context()); err != nil {
		writeJSON(w, http.StatusServiceUnavailable, map[string]any{"status": "degraded", "database": "down"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"status": "ok", "database": "ok", "time": time.Now().UTC()})
}

func (s *Server) handleLogin(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}
	ip := s.clientIP(r)
	if !s.loginLimiter.Allow(ip) {
		writeJSON(w, http.StatusTooManyRequests, map[string]any{"error": "too many login attempts"})
		return
	}
	var req struct {
		Username string `json:"username"`
		Password string `json:"password"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	cred, err := s.store.GetAdminCredentialByUsername(r.Context(), req.Username)
	if err != nil || !cred.Active || !verifyPassword(req.Password, cred.PasswordSalt, cred.PasswordHash, cred.PasswordIterations) {
		time.Sleep(250 * time.Millisecond)
		s.store.AddAdminAudit(r.Context(), nil, normalizeUsername(req.Username), "login_failed", "admin", "", "", ip)
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "invalid username or password"})
		return
	}
	if err := issueAdminCookie(w, s.cfg, cred.Admin); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not create login session"})
		return
	}
	s.store.RecordAdminLogin(r.Context(), cred.ID)
	s.store.AddAdminAudit(r.Context(), &cred.ID, cred.Username, "login_success", "admin", adminIDString(cred.ID), "", ip)
	writeJSON(w, http.StatusOK, map[string]any{
		"ok": true, "id": cred.ID, "username": cred.Username, "display_name": cred.DisplayName, "role": cred.Role,
	})
}

func (s *Server) handleLogout(w http.ResponseWriter, r *http.Request) {
	if admin, err := authenticateAdmin(r, s.cfg, s.store); err == nil {
		s.store.AddAdminAudit(r.Context(), &admin.ID, admin.Username, "logout", "admin", adminIDString(admin.ID), "", s.clientIP(r))
	}
	clearTechCookie(w)
	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
}

func (s *Server) handleMe(w http.ResponseWriter, r *http.Request) {
	a := currentAdmin(r.Context())
	writeJSON(w, http.StatusOK, map[string]any{
		"id": a.ID, "username": a.Username, "display_name": a.DisplayName, "role": a.Role, "active": a.Active,
	})
}

func newUUID() (string, error) {
	raw, err := randomToken(16)
	if err != nil {
		return "", err
	}
	b, err := base64URLDecode(raw)
	if err != nil || len(b) != 16 {
		return "", fmt.Errorf("uuid entropy failure")
	}
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x",
		b[0:4], b[4:6], b[6:8], b[8:10], b[10:16]), nil
}

func base64URLDecode(v string) ([]byte, error) {
	return base64RawURLDecode(v)
}

func (s *Server) handleCreateSession(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}
	var req struct {
		CustomerLabel      string `json:"customer_label"`
		RequestedControl      bool   `json:"requested_control"`
		RequestedElevation    bool   `json:"requested_elevation"`
		RequestedClipboard    bool   `json:"requested_clipboard"`
		RequestedFileTransfer bool   `json:"requested_file_transfer"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	if len(req.CustomerLabel) > 160 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "customer label is too long"})
		return
	}
	id, err := newUUID()
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not create session"})
		return
	}

	var code string
	for i := 0; i < 10; i++ {
		code, err = randomDigits(8)
		if err != nil {
			break
		}
		now := time.Now().UTC()
		admin := currentAdmin(r.Context())
		session := Session{
			ID: id, CodeHint: code[len(code)-4:], CustomerLabel: strings.TrimSpace(req.CustomerLabel),
			TechnicianName: techName(r.Context()), TechnicianID: &admin.ID, RequestedControl: req.RequestedControl,
			RequestedElevation: req.RequestedElevation, RequestedClipboard: req.RequestedClipboard,
			RequestedFileTransfer: req.RequestedFileTransfer, CreatedAt: now, ExpiresAt: now.Add(s.cfg.SessionTTL),
		}
		err = s.store.CreateSession(r.Context(), session, hmacHex(s.cfg.CodeSecret, code))
		if err == nil {
			admin := currentAdmin(r.Context())
			s.store.SetSessionTechnicianID(r.Context(), id, admin.ID)
			s.store.AddEvent(r.Context(), id, admin.Username, "session_created", "")
			s.store.AddAdminAudit(r.Context(), &admin.ID, admin.Username, "session_created", "session", id,
				auditJSON(map[string]any{
					"customer_label": session.CustomerLabel,
					"control": session.RequestedControl,
					"elevation": session.RequestedElevation,
					"clipboard": session.RequestedClipboard,
					"file_transfer": session.RequestedFileTransfer,
				}), s.clientIP(r))
			writeJSON(w, http.StatusCreated, map[string]any{"session": session, "code": code})
			return
		}
		if !strings.Contains(strings.ToLower(err.Error()), "duplicate") {
			break
		}
	}
	log.Printf("create session failed: %v", err)
	writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not create session"})
}

func (s *Server) handleListSessions(w http.ResponseWriter, r *http.Request) {
	q, err := sessionQueryFromRequest(r, 200)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}
	result, err := s.store.SearchSessions(r.Context(), q)
	if err != nil {
		log.Printf("list sessions: %v", err)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not load sessions"})
		return
	}
	writeJSON(w, http.StatusOK, result)
}

func (s *Server) handleGetSession(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	session, err := s.store.GetSessionWithTechnician(r.Context(), id)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			writeJSON(w, http.StatusNotFound, map[string]any{"error": "session not found"})
			return
		}
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not load session"})
		return
	}
	events, _ := s.store.ListEvents(r.Context(), id)
	notes, _ := s.store.ListSessionNotes(r.Context(), id)
	network, _ := s.store.GetSessionNetwork(r.Context(), id)
	writeJSON(w, http.StatusOK, map[string]any{
		"session": session,
		"events": events,
		"notes": notes,
		"network": network,
	})
}

func (s *Server) handleEndSession(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}
	id := r.PathValue("id")
	if err := s.store.EndSession(r.Context(), id); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			writeJSON(w, http.StatusNotFound, map[string]any{"error": "session not found"})
			return
		}
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not end session"})
		return
	}
	admin := currentAdmin(r.Context())
	s.store.AddEvent(r.Context(), id, admin.Username, "session_ended", "")
	s.store.AddAdminAudit(r.Context(), &admin.ID, admin.Username, "session_ended", "session", id, "", s.clientIP(r))
	s.hub.End(id)
	s.transfers.RemoveSession(id)
	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
}

func normalizeCode(code string) string {
	var b strings.Builder
	for _, r := range code {
		if r >= '0' && r <= '9' {
			b.WriteRune(r)
		}
	}
	return b.String()
}

func (s *Server) handleAgentLookup(w http.ResponseWriter, r *http.Request) {
	ip := s.clientIP(r)
	if !s.agentLimiter.Allow(ip) {
		writeJSON(w, http.StatusTooManyRequests, map[string]any{"error": "too many attempts; wait and try again"})
		return
	}
	var req struct {
		Code string `json:"code"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	code := normalizeCode(req.Code)
	if len(code) != 8 {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "support session not found"})
		return
	}
	session, err := s.store.LookupByCodeHash(r.Context(), hmacHex(s.cfg.CodeSecret, code))
	if err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "support session not found or expired"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"session_id": session.ID, "technician_name": session.TechnicianName,
		"customer_label": session.CustomerLabel, "requested_control": session.RequestedControl,
		"requested_elevation": session.RequestedElevation, "requested_clipboard": session.RequestedClipboard,
		"requested_file_transfer": session.RequestedFileTransfer, "expires_at": session.ExpiresAt,
	})
}

func (s *Server) handleAgentRedeem(w http.ResponseWriter, r *http.Request) {
	ip := s.clientIP(r)
	if !s.agentLimiter.Allow(ip) {
		writeJSON(w, http.StatusTooManyRequests, map[string]any{"error": "too many attempts; wait and try again"})
		return
	}
	var req struct {
		Code          string `json:"code"`
		MachineName   string `json:"machine_name"`
		TermsAccepted bool   `json:"terms_accepted"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	if !req.TermsAccepted {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "terms and remote-support consent must be accepted"})
		return
	}
	code := normalizeCode(req.Code)
	if len(code) != 8 || len(req.MachineName) > 255 {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "support session not found"})
		return
	}
	token, err := randomToken(32)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not authorize support"})
		return
	}
	session, err := s.store.RedeemSession(r.Context(), hmacHex(s.cfg.CodeSecret, code), sha256Hex(token), strings.TrimSpace(req.MachineName), true, s.cfg.LiveSessionTTL)
	if err != nil {
		writeJSON(w, http.StatusConflict, map[string]any{"error": "support session was already used, expired, or ended"})
		return
	}
	s.store.AddEvent(r.Context(), session.ID, "customer", "consent_granted", fmt.Sprintf("machine=%q", session.MachineName))
	base, _ := url.Parse(s.cfg.PublicBaseURL)
	if base.Scheme == "https" {
		base.Scheme = "wss"
	} else {
		base.Scheme = "ws"
	}
	base.Path = "/ws/agent"
	q := base.Query()
	q.Set("session", session.ID)
	base.RawQuery = q.Encode()
	writeJSON(w, http.StatusOK, map[string]any{
		"session_id": session.ID,
		"agent_token": token,
		"websocket_url": base.String(),
		"live_expires_at": session.ExpiresAt,
	})
}

func (s *Server) handleAgentEnd(w http.ResponseWriter, r *http.Request) {
	token := bearerToken(r)
	if token == "" {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "missing session credential"})
		return
	}
	var req struct {
		SessionID string `json:"session_id"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	req.SessionID = strings.TrimSpace(req.SessionID)
	if req.SessionID == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "session_id is required"})
		return
	}
	if err := s.store.EndSessionByAgentToken(r.Context(), req.SessionID, sha256Hex(token)); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			writeJSON(w, http.StatusConflict, map[string]any{"error": "support session is already ended, expired, or unavailable"})
			return
		}
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not end support session"})
		return
	}
	s.store.AddEvent(r.Context(), req.SessionID, "customer", "session_ended", "customer ended support session")
	s.hub.End(req.SessionID)
	s.transfers.RemoveSession(req.SessionID)
	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
}

func (s *Server) handleDownloadStatus(w http.ResponseWriter, r *http.Request) {
	st, err := os.Stat(s.cfg.AgentDownloadPath)
	available := err == nil && st.Mode().IsRegular() && st.Size() > 0
	writeJSON(w, http.StatusOK, map[string]any{"available": available})
}

func (s *Server) handleAgentDownload(w http.ResponseWriter, r *http.Request) {
	st, err := os.Stat(s.cfg.AgentDownloadPath)
	if err != nil || !st.Mode().IsRegular() {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "Windows support agent build is not published on this server yet"})
		return
	}
	w.Header().Set("Content-Type", "application/vnd.microsoft.portable-executable")
	w.Header().Set("Content-Disposition", `attachment; filename="Remote-Assist.exe"`)
	w.Header().Set("Cache-Control", "no-store")
	http.ServeFile(w, r, s.cfg.AgentDownloadPath)
}

func downloadableFileStatus(path string) map[string]any {
	st, err := os.Stat(path)
	if err != nil || !st.Mode().IsRegular() {
		return map[string]any{"available": false}
	}
	return map[string]any{
		"available": true,
		"size":      st.Size(),
		"modified_at": st.ModTime().UTC(),
	}
}

func (s *Server) handleTechnicianDownloadStatus(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{
		"portable":  downloadableFileStatus(s.cfg.TechnicianPortablePath),
		"installer": downloadableFileStatus(s.cfg.TechnicianInstallerPath),
	})
}

func serveTechnicianArtifact(
	w http.ResponseWriter,
	r *http.Request,
	path, filename, contentType string,
) {
	st, err := os.Stat(path)
	if err != nil || !st.Mode().IsRegular() || st.Size() <= 0 {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "technician application build is not published on this server yet"})
		return
	}
	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Content-Disposition", fmt.Sprintf(`attachment; filename="%s"`, filename))
	w.Header().Set("Cache-Control", "no-store")
	http.ServeFile(w, r, path)
}

func (s *Server) handleTechnicianPortableDownload(w http.ResponseWriter, r *http.Request) {
	serveTechnicianArtifact(
		w, r,
		s.cfg.TechnicianPortablePath,
		"Remote-Assist-Technician-Portable.zip",
		"application/zip",
	)
}

func (s *Server) handleTechnicianInstallerDownload(w http.ResponseWriter, r *http.Request) {
	serveTechnicianArtifact(
		w, r,
		s.cfg.TechnicianInstallerPath,
		"Remote-Assist-Technician-Setup.exe",
		"application/vnd.microsoft.portable-executable",
	)
}

func bearerToken(r *http.Request) string {
	header := strings.TrimSpace(r.Header.Get("Authorization"))
	if len(header) < 8 || !strings.EqualFold(header[:7], "Bearer ") {
		return ""
	}
	return strings.TrimSpace(header[7:])
}

func (s *Server) handleAgentWS(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("session")
	token := bearerToken(r)
	if id == "" || token == "" {
		http.Error(w, "missing session credentials", http.StatusUnauthorized)
		return
	}
	session, err := s.store.GetSession(r.Context(), id)
	if err != nil || session.Status == "ended" || session.Status == "expired" || !session.ExpiresAt.After(time.Now().UTC()) || !secureEqual(session.AgentTokenHash, sha256Hex(token)) {
		if err == nil && !session.ExpiresAt.After(time.Now().UTC()) {
			_ = s.store.ExpireLiveSession(r.Context(), id)
		}
		http.Error(w, "invalid session credentials", http.StatusUnauthorized)
		return
	}
	conn, err := s.upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	peer := &wsPeer{conn: conn}
	expiryTimer := time.AfterFunc(time.Until(session.ExpiresAt), func() {
		if err := s.store.ExpireLiveSession(context.Background(), id); err == nil {
			s.store.AddEvent(context.Background(), id, "system", "session_expired", "")
			s.hub.End(id)
		}
	})
	defer expiryTimer.Stop()
	if old := s.hub.setAgent(id, peer); old != nil {
		old.close(websocket.ClosePolicyViolation, "replaced by new customer connection")
	}
	viewerConnected := s.hub.hasTech(id)
	_ = peer.write(websocket.TextMessage, []byte(fmt.Sprintf(`{"type":"viewer_status","connected":%t}`, viewerConnected)))
	s.sendChatHistoryToPeer(r.Context(), id, peer)
	defer func() {
		if s.hub.unsetAgent(id, peer) {
			_ = s.store.MarkAgentDisconnected(context.Background(), id)
			s.store.AddEvent(context.Background(), id, "system", "agent_disconnected", "")
			_ = s.hub.sendToTech(id, websocket.TextMessage, []byte(`{"type":"agent_status","status":"disconnected"}`))
		}
		_ = conn.Close()
	}()
	_ = s.store.MarkAgentConnected(r.Context(), id)
	s.store.AddEvent(r.Context(), id, "system", "agent_connected", "")
	_ = s.hub.sendToTech(id, websocket.TextMessage, []byte(`{"type":"agent_status","status":"connected"}`))
	conn.SetReadLimit(8 * 1024 * 1024)
	for {
		mt, data, err := conn.ReadMessage()
		if err != nil {
			return
		}
		if mt == websocket.BinaryMessage {
			if err := s.hub.sendToTech(id, mt, data); err != nil {
				log.Printf("forward agent->tech session=%s: %v", id, err)
			}
			continue
		}
		if mt == websocket.TextMessage {
			var envelope struct {
				Type string `json:"type"`
			}
			if json.Unmarshal(data, &envelope) != nil {
				continue
			}
			if envelope.Type == "hello" {
				var hello struct {
					Network *SessionNetworkSnapshot `json:"network"`
				}
				if json.Unmarshal(data, &hello) == nil && hello.Network != nil {
					if err := s.store.UpsertSessionNetwork(
						r.Context(),
						id,
						*hello.Network,
					); err != nil {
						log.Printf("persist network snapshot session=%s: %v", id, err)
					}
				}
			}
			if envelope.Type == "clipboard_data" {
				if !session.RequestedClipboard {
					continue
				}
				var payload struct {
					Text string `json:"text"`
				}
				if json.Unmarshal(data, &payload) != nil || len(payload.Text) > maxClipboardBytes {
					continue
				}
			}
			if envelope.Type == "clipboard_status" && !session.RequestedClipboard {
				continue
			}
			if envelope.Type == "chat_message" {
				var payload struct {
					Body string `json:"body"`
				}
				if json.Unmarshal(data, &payload) != nil {
					continue
				}
				message, err := s.createChatMessage(
					r.Context(),
					id,
					"customer",
					session.MachineName,
					payload.Body,
				)
				if err != nil {
					continue
				}
				normalized := chatMessageEnvelope(message)
				_ = peer.write(websocket.TextMessage, normalized)
				_ = s.hub.sendToTech(id, websocket.TextMessage, normalized)
				continue
			}
			if envelope.Type == "elevation_status" {
				if len(data) <= 8*1024 {
					var payload struct {
						Status string `json:"status"`
					}
					if json.Unmarshal(data, &payload) == nil {
						status := strings.ToLower(strings.TrimSpace(payload.Status))
						switch status {
						case "requested", "declined", "uac_cancelled", "restarting", "elevated", "failed":
							s.store.AddEvent(
								r.Context(),
								id,
								"customer",
								"elevation_"+status,
								"",
							)
							_ = s.hub.sendToTech(id, websocket.TextMessage, data)
						}
					}
				}
				continue
			}
			if err := s.hub.sendToTech(id, mt, data); err != nil {
				log.Printf("forward agent->tech session=%s: %v", id, err)
			}
		}
	}
}

func (s *Server) handleTechWS(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("session")
	session, err := s.store.GetSession(r.Context(), id)
	if err != nil || session.Status == "ended" || session.Status == "expired" || !session.ExpiresAt.After(time.Now().UTC()) {
		if err == nil && !session.ExpiresAt.After(time.Now().UTC()) {
			_ = s.store.ExpireLiveSession(r.Context(), id)
		}
		http.Error(w, "session unavailable", http.StatusNotFound)
		return
	}
	conn, err := s.upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	peer := &wsPeer{conn: conn}
	if old := s.hub.setTech(id, peer); old != nil {
		old.close(websocket.ClosePolicyViolation, "replaced by new technician connection")
	}
	_ = s.hub.sendToAgent(id, websocket.TextMessage, []byte(`{"type":"viewer_status","connected":true}`))
	defer func() {
		if s.hub.unsetTech(id, peer) {
			_ = s.hub.sendToAgent(id, websocket.TextMessage, []byte(`{"type":"viewer_status","connected":false}`))
		}
		_ = conn.Close()
	}()
	_ = peer.write(websocket.TextMessage, []byte(`{"type":"tech_status","status":"connected"}`))
	s.sendChatHistoryToPeer(r.Context(), id, peer)
	conn.SetReadLimit(1024 * 1024)
	for {
		mt, data, err := conn.ReadMessage()
		if err != nil {
			return
		}
		if mt != websocket.TextMessage {
			continue
		}
		var envelope struct {
			Type string `json:"type"`
		}
		if json.Unmarshal(data, &envelope) != nil {
			continue
		}
		if envelope.Type == "input" && session.RequestedControl {
			if err := s.hub.sendToAgent(id, websocket.TextMessage, data); err != nil {
				log.Printf("forward tech->agent session=%s: %v", id, err)
			}
			continue
		}
		if envelope.Type == "capture_settings" {
			if err := s.hub.sendToAgent(id, websocket.TextMessage, data); err != nil {
				log.Printf("forward capture settings session=%s: %v", id, err)
			}
			continue
		}
		if envelope.Type == "viewer_telemetry" {
			if len(data) <= 16*1024 {
				if err := s.hub.sendToAgent(id, websocket.TextMessage, data); err != nil {
					log.Printf("forward viewer telemetry session=%s: %v", id, err)
				}
			}
			continue
		}
		if envelope.Type == "viewer_capabilities" {
			if len(data) <= 4*1024 {
				if err := s.hub.sendToAgent(id, websocket.TextMessage, data); err != nil {
					log.Printf("forward viewer capabilities session=%s: %v", id, err)
				}
			}
			continue
		}
		if envelope.Type == "network_refresh_request" {
			admin := currentAdmin(r.Context())
			s.store.AddEvent(r.Context(), id, admin.Username, "network_refresh_requested", "")
			if err := s.hub.sendToAgent(id, websocket.TextMessage, []byte(`{"type":"network_refresh_request"}`)); err != nil {
				log.Printf("forward network refresh session=%s: %v", id, err)
			}
			continue
		}
		if envelope.Type == "clipboard_get" && session.RequestedClipboard {
			if err := s.hub.sendToAgent(id, websocket.TextMessage, data); err != nil {
				log.Printf("forward clipboard request session=%s: %v", id, err)
			}
			continue
		}
		if envelope.Type == "clipboard_set" && session.RequestedClipboard {
			var payload struct {
				Text string `json:"text"`
			}
			if json.Unmarshal(data, &payload) != nil || len(payload.Text) > maxClipboardBytes {
				continue
			}
			if err := s.hub.sendToAgent(id, websocket.TextMessage, data); err != nil {
				log.Printf("forward clipboard session=%s: %v", id, err)
			}
			continue
		}
		if envelope.Type == "file_status" && session.RequestedFileTransfer && len(data) <= 16*1024 {
			if err := s.hub.sendToAgent(id, websocket.TextMessage, data); err != nil {
				log.Printf("forward file status session=%s: %v", id, err)
			}
			continue
		}
		if envelope.Type == "chat_message" {
			var payload struct {
				Body string `json:"body"`
			}
			if json.Unmarshal(data, &payload) != nil {
				continue
			}
			admin := currentAdmin(r.Context())
			message, err := s.createChatMessage(
				r.Context(),
				id,
				"technician",
				techName(r.Context()),
				payload.Body,
			)
			if err != nil {
				continue
			}
			normalized := chatMessageEnvelope(message)
			_ = peer.write(websocket.TextMessage, normalized)
			_ = s.hub.sendToAgent(id, websocket.TextMessage, normalized)
			s.store.AddAdminAudit(
				r.Context(),
				&admin.ID,
				admin.Username,
				"chat_message_sent",
				"session",
				id,
				"",
				s.clientIP(r),
			)
			continue
		}
		if envelope.Type == "elevation_request" {
			admin := currentAdmin(r.Context())
			s.store.AddEvent(r.Context(), id, admin.Username, "elevation_requested", "")
			s.store.AddAdminAudit(
				r.Context(),
				&admin.ID,
				admin.Username,
				"elevation_requested",
				"session",
				id,
				"",
				s.clientIP(r),
			)
			if err := s.hub.sendToAgent(id, websocket.TextMessage, []byte(`{"type":"elevation_request"}`)); err != nil {
				log.Printf("forward elevation request session=%s: %v", id, err)
			}
			continue
		}
		if envelope.Type == "recording_status" {
			var payload struct {
				Status string `json:"status"`
			}
			if json.Unmarshal(data, &payload) != nil {
				continue
			}
			status := strings.ToLower(strings.TrimSpace(payload.Status))
			if status != "started" && status != "stopped" {
				continue
			}
			admin := currentAdmin(r.Context())
			s.store.AddEvent(r.Context(), id, admin.Username, "recording_"+status, "technician-side recording")
			_ = s.hub.sendToAgent(id, websocket.TextMessage, data)
			continue
		}
		if envelope.Type == "screenshot_status" {
			var payload struct {
				Status string `json:"status"`
			}
			if json.Unmarshal(data, &payload) != nil {
				continue
			}
			if strings.ToLower(strings.TrimSpace(payload.Status)) != "saved" {
				continue
			}
			admin := currentAdmin(r.Context())
			s.store.AddEvent(
				r.Context(),
				id,
				admin.Username,
				"screenshot_saved",
				"technician saved remote screenshot locally",
			)
			continue
		}
	}
}

func mimeInit() {
	_ = mime.AddExtensionType(".js", "text/javascript; charset=utf-8")
	_ = mime.AddExtensionType(".css", "text/css; charset=utf-8")
	_ = mime.AddExtensionType(".svg", "image/svg+xml")
}

func init() { mimeInit() }

// filepath.Clean reference retained here to make it explicit that download paths are local configuration,
// not request-derived paths. This prevents future refactors from accidentally treating the URL as a filesystem path.
var _ = filepath.Clean
