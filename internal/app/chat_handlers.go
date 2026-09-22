package app

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"strings"

	"github.com/gorilla/websocket"
)

const maxChatBodyBytes = 16 * 1024

func normalizeChatBody(body string) (string, error) {
	body = strings.TrimSpace(body)
	if body == "" {
		return "", errors.New("chat message is empty")
	}
	if len([]byte(body)) > maxChatBodyBytes {
		return "", errors.New("chat message is too long")
	}
	return body, nil
}

func (s *Server) handleChatHistory(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.PathValue("id"))
	if id == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "session id is required"})
		return
	}
	if _, err := s.store.GetSession(r.Context(), id); err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "session not found"})
		return
	}
	messages, err := s.store.ListChatMessages(r.Context(), id, 200)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not load chat"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"messages": messages})
}

func chatMessageEnvelope(message ChatMessage) []byte {
	payload, _ := json.Marshal(map[string]any{
		"type":    "chat_message",
		"message": message,
	})
	return payload
}

func chatHistoryEnvelope(messages []ChatMessage) []byte {
	payload, _ := json.Marshal(map[string]any{
		"type":     "chat_history",
		"messages": messages,
	})
	return payload
}

func (s *Server) sendChatHistoryToPeer(
	ctx context.Context,
	sessionID string,
	peer *wsPeer,
) {
	messages, err := s.store.ListChatMessages(ctx, sessionID, 200)
	if err != nil {
		return
	}
	_ = peer.write(websocket.TextMessage, chatHistoryEnvelope(messages))
}

func (s *Server) createChatImageMessage(
	ctx context.Context,
	sessionID, senderType, senderName, caption string,
	item FileTransfer,
) (ChatMessage, error) {
	caption = strings.TrimSpace(caption)
	if caption == "" {
		caption = "Shared image: " + item.Name
	}
	body, err := normalizeChatBody(caption)
	if err != nil {
		return ChatMessage{}, err
	}
	return s.store.AddChatMessageAttachment(
		ctx,
		sessionID,
		senderType,
		strings.TrimSpace(senderName),
		body,
		item.ID,
		item.Name,
		item.MimeType,
		item.Size,
	)
}

func (s *Server) createChatMessage(
	ctx context.Context,
	sessionID, senderType, senderName, body string,
) (ChatMessage, error) {
	body, err := normalizeChatBody(body)
	if err != nil {
		return ChatMessage{}, err
	}
	return s.store.AddChatMessage(
		ctx,
		sessionID,
		senderType,
		strings.TrimSpace(senderName),
		body,
	)
}
