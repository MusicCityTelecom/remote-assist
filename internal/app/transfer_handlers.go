package app

import (
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"mime"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/gorilla/websocket"
)

func (s *Server) handleTechFileUpload(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}

	id := strings.TrimSpace(r.PathValue("id"))
	session, err := s.store.GetSession(r.Context(), id)
	if err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "session not found"})
		return
	}
	if !session.RequestedFileTransfer {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "file transfer was not approved for this session"})
		return
	}
	if session.Status != "approved" && session.Status != "connected" {
		writeJSON(w, http.StatusConflict, map[string]any{"error": "customer session is not active"})
		return
	}
	if !s.hub.hasAgent(id) {
		writeJSON(w, http.StatusConflict, map[string]any{"error": "customer support app is not connected"})
		return
	}

	file, name, mimeType, err := readTransferUpload(w, r)
	if r.MultipartForm != nil {
		defer r.MultipartForm.RemoveAll()
	}
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}
	defer file.Close()

	purpose := normalizeTransferPurpose(r.FormValue("purpose"))
	maxBytes := maxTransferBytes
	if purpose == "chat_image" {
		if !allowedChatImageMime(mimeType) {
			writeJSON(w, http.StatusBadRequest, map[string]any{"error": "chat pictures must be JPEG, PNG, or GIF"})
			return
		}
		maxBytes = maxChatImageBytes
	}
	item, err := s.transfers.PutWithMetadata(id, "to_agent", purpose, mimeType, name, file, maxBytes)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}

	var chatMessage *ChatMessage
	if item.Purpose == "chat_image" {
		message, msgErr := s.createChatImageMessage(
			r.Context(),
			id,
			"technician",
			techName(r.Context()),
			r.FormValue("caption"),
			item,
		)
		if msgErr != nil {
			s.transfers.Remove(item.ID)
			writeJSON(w, http.StatusBadRequest, map[string]any{"error": msgErr.Error()})
			return
		}
		chatMessage = &message
		envelope := chatMessageEnvelope(message)
		_ = s.hub.sendToTech(id, websocket.TextMessage, envelope)
		if err := s.hub.sendToAgent(id, websocket.TextMessage, envelope); err != nil {
			s.transfers.Remove(item.ID)
			writeJSON(w, http.StatusConflict, map[string]any{"error": "customer support app disconnected before image delivery"})
			return
		}
	} else {
		offer, _ := json.Marshal(map[string]any{
			"type":        "file_offer",
			"transfer_id": item.ID,
			"direction":   item.Direction,
			"purpose":     item.Purpose,
			"mime_type":   item.MimeType,
			"name":        item.Name,
			"size":        item.Size,
			"expires_at":  item.ExpiresAt,
		})
		if err := s.hub.sendToAgent(id, websocket.TextMessage, offer); err != nil {
			s.transfers.Remove(item.ID)
			writeJSON(w, http.StatusConflict, map[string]any{"error": "customer support app disconnected before transfer offer"})
			return
		}
	}

	admin := currentAdmin(r.Context())
	eventName := "file_offered_to_customer"
	if item.Purpose == "chat_image" {
		eventName = "chat_image_sent_to_customer"
	}
	s.store.AddEvent(r.Context(), id, admin.Username, eventName,
		fmt.Sprintf("name=%q size=%d", item.Name, item.Size))
	s.store.AddAdminAudit(r.Context(), &admin.ID, admin.Username, eventName,
		"session", id, auditJSON(map[string]any{
			"name": item.Name, "size": item.Size, "purpose": item.Purpose,
		}), s.clientIP(r))

	response := map[string]any{"transfer": item}
	if chatMessage != nil {
		response["message"] = *chatMessage
	}
	writeJSON(w, http.StatusCreated, response)
}

func (s *Server) handleTechFileList(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.PathValue("id"))
	session, err := s.store.GetSession(r.Context(), id)
	if err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "session not found"})
		return
	}
	if !session.RequestedFileTransfer {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "file transfer was not approved for this session"})
		return
	}

	writeJSON(w, http.StatusOK, map[string]any{
		"transfers": s.transfers.ListSessionPurpose(id, "to_tech", "file"),
	})
}

func (s *Server) handleTechFileDownload(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.PathValue("id"))
	transferID := strings.TrimSpace(r.PathValue("transfer"))

	item, ok := s.transfers.Get(transferID)
	if !ok || item.SessionID != id || (item.Direction != "to_tech" && item.Purpose != "chat_image") {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "file transfer not found or expired"})
		return
	}

	session, err := s.store.GetSession(r.Context(), id)
	if err != nil || !session.RequestedFileTransfer {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "file transfer unavailable"})
		return
	}

	if err := serveTransferFile(w, r, item); err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "file transfer unavailable"})
		return
	}

	if item.Purpose != "chat_image" {
		admin := currentAdmin(r.Context())
		s.store.AddEvent(r.Context(), id, admin.Username, "file_downloaded_by_technician",
			fmt.Sprintf("name=%q size=%d", item.Name, item.Size))
	}
}

func (s *Server) handleAgentFileUpload(w http.ResponseWriter, r *http.Request) {
	session, err := s.authenticateLiveAgentRequest(r)
	if err != nil {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "invalid session credentials"})
		return
	}
	if !session.RequestedFileTransfer {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "file transfer was not approved for this session"})
		return
	}
	file, name, mimeType, err := readTransferUpload(w, r)
	if r.MultipartForm != nil {
		defer r.MultipartForm.RemoveAll()
	}
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}
	defer file.Close()

	purpose := normalizeTransferPurpose(r.FormValue("purpose"))
	maxBytes := maxTransferBytes
	if purpose == "chat_image" {
		if !allowedChatImageMime(mimeType) {
			writeJSON(w, http.StatusBadRequest, map[string]any{"error": "chat pictures must be JPEG, PNG, or GIF"})
			return
		}
		maxBytes = maxChatImageBytes
	}
	item, err := s.transfers.PutWithMetadata(
		session.ID, "to_tech", purpose, mimeType, name, file, maxBytes)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}

	delivered := false
	var chatMessage *ChatMessage
	if item.Purpose == "chat_image" {
		message, msgErr := s.createChatImageMessage(
			r.Context(),
			session.ID,
			"customer",
			session.MachineName,
			r.FormValue("caption"),
			item,
		)
		if msgErr != nil {
			s.transfers.Remove(item.ID)
			writeJSON(w, http.StatusBadRequest, map[string]any{"error": msgErr.Error()})
			return
		}
		chatMessage = &message
		envelope := chatMessageEnvelope(message)
		_ = s.hub.sendToAgent(session.ID, websocket.TextMessage, envelope)
		if s.hub.hasTech(session.ID) {
			if err := s.hub.sendToTech(session.ID, websocket.TextMessage, envelope); err == nil {
				delivered = true
			}
		}
	} else {
		offer, _ := json.Marshal(map[string]any{
			"type":        "file_offer",
			"transfer_id": item.ID,
			"direction":   item.Direction,
			"purpose":     item.Purpose,
			"mime_type":   item.MimeType,
			"name":        item.Name,
			"size":        item.Size,
			"expires_at":  item.ExpiresAt,
		})
		if s.hub.hasTech(session.ID) {
			if err := s.hub.sendToTech(session.ID, websocket.TextMessage, offer); err == nil {
				delivered = true
			}
		}
	}

	eventName := "file_offered_to_technician"
	if item.Purpose == "chat_image" {
		eventName = "chat_image_sent_to_technician"
	}
	s.store.AddEvent(r.Context(), session.ID, "customer", eventName,
		fmt.Sprintf("name=%q size=%d delivered=%t", item.Name, item.Size, delivered))
	response := map[string]any{
		"transfer": item,
		"viewer_notified": delivered,
	}
	if chatMessage != nil {
		response["message"] = *chatMessage
	}
	writeJSON(w, http.StatusCreated, response)
}

func (s *Server) handleAgentFileDownload(w http.ResponseWriter, r *http.Request) {
	session, err := s.authenticateLiveAgentRequest(r)
	if err != nil {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "invalid session credentials"})
		return
	}
	if !session.RequestedFileTransfer {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "file transfer was not approved for this session"})
		return
	}

	item, ok := s.transfers.Get(strings.TrimSpace(r.PathValue("transfer")))
	if !ok || item.SessionID != session.ID || (item.Direction != "to_agent" && item.Purpose != "chat_image") {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "file transfer not found or expired"})
		return
	}

	if err := serveTransferFile(w, r, item); err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "file transfer unavailable"})
		return
	}

	if item.Purpose != "chat_image" {
		s.store.AddEvent(r.Context(), session.ID, "customer", "file_downloaded_by_customer",
			fmt.Sprintf("name=%q size=%d", item.Name, item.Size))
	}
}

func (s *Server) authenticateLiveAgentRequest(r *http.Request) (Session, error) {
	id := strings.TrimSpace(r.URL.Query().Get("session"))
	token := bearerToken(r)
	if id == "" || token == "" {
		return Session{}, errors.New("missing session credentials")
	}

	session, err := s.store.GetSession(r.Context(), id)
	if err != nil {
		return Session{}, err
	}
	if session.Status == "ended" || session.Status == "expired" ||
		!session.ExpiresAt.After(time.Now().UTC()) ||
		!secureEqual(session.AgentTokenHash, sha256Hex(token)) {
		return Session{}, sql.ErrNoRows
	}
	return session, nil
}

func readTransferUpload(w http.ResponseWriter, r *http.Request) (multipartFile, string, string, error) {
	r.Body = http.MaxBytesReader(w, r.Body, maxTransferBytes+1024*1024)
	if err := r.ParseMultipartForm(1 * 1024 * 1024); err != nil {
		return nil, "", "", errors.New("invalid upload or file exceeds 25 MB limit")
	}

	file, header, err := r.FormFile("file")
	if err != nil {
		return nil, "", "", errors.New("file is required")
	}
	if header.Size > maxTransferBytes {
		file.Close()
		return nil, "", "", errors.New("file exceeds 25 MB transfer limit")
	}
	mimeType := strings.ToLower(strings.TrimSpace(header.Header.Get("Content-Type")))
	if mimeType == "" {
		mimeType = "application/octet-stream"
	}
	return file, header.Filename, mimeType, nil
}

func normalizeTransferPurpose(value string) string {
	if strings.EqualFold(strings.TrimSpace(value), "chat_image") {
		return "chat_image"
	}
	return "file"
}

func allowedChatImageMime(value string) bool {
	switch strings.ToLower(strings.TrimSpace(value)) {
	case "image/jpeg", "image/png", "image/gif":
		return true
	default:
		return false
	}
}

type multipartFile interface {
	Read([]byte) (int, error)
	Close() error
}

func serveTransferFile(w http.ResponseWriter, r *http.Request, item FileTransfer) error {
	f, err := os.Open(item.Path)
	if err != nil {
		return err
	}
	defer f.Close()

	st, err := f.Stat()
	if err != nil || !st.Mode().IsRegular() {
		return errors.New("transfer file unavailable")
	}

	dispositionType := "attachment"
	contentType := "application/octet-stream"
	if item.Purpose == "chat_image" && allowedChatImageMime(item.MimeType) {
		dispositionType = "inline"
		contentType = item.MimeType
	}
	disposition := mime.FormatMediaType(dispositionType, map[string]string{"filename": item.Name})
	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Content-Disposition", disposition)
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Content-Length", fmt.Sprintf("%d", item.Size))
	http.ServeContent(w, r, item.Name, item.CreatedAt, f)
	return nil
}
