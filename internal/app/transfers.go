package app

import (
	"errors"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

const maxTransferBytes int64 = 25 * 1024 * 1024
const maxChatImageBytes int64 = 10 * 1024 * 1024
const transferTTL = 30 * time.Minute

type FileTransfer struct {
	ID        string    `json:"id"`
	SessionID string    `json:"session_id"`
	Direction string    `json:"direction"`
	Purpose   string    `json:"purpose"`
	MimeType  string    `json:"mime_type"`
	Name      string    `json:"name"`
	Size      int64     `json:"size"`
	Path      string    `json:"-"`
	CreatedAt time.Time `json:"created_at"`
	ExpiresAt time.Time `json:"expires_at"`
}

type TransferStore struct {
	mu    sync.Mutex
	dir   string
	items map[string]FileTransfer
}

func NewTransferStore() *TransferStore {
	return &TransferStore{
		dir:   filepath.Join(os.TempDir(), "remote-assist-transfers"),
		items: make(map[string]FileTransfer),
	}
}

func (s *TransferStore) Put(sessionID, direction, name string, src io.Reader) (FileTransfer, error) {
	return s.PutWithMetadata(
		sessionID,
		direction,
		"file",
		"application/octet-stream",
		name,
		src,
		maxTransferBytes,
	)
}

func (s *TransferStore) PutWithMetadata(
	sessionID, direction, purpose, mimeType, name string,
	src io.Reader,
	maxBytes int64,
) (FileTransfer, error) {
	if direction != "to_agent" && direction != "to_tech" {
		return FileTransfer{}, errors.New("invalid transfer direction")
	}
	purpose = strings.TrimSpace(strings.ToLower(purpose))
	if purpose == "" {
		purpose = "file"
	}
	if purpose != "file" && purpose != "chat_image" {
		return FileTransfer{}, errors.New("invalid transfer purpose")
	}
	mimeType = strings.TrimSpace(strings.ToLower(mimeType))
	if mimeType == "" {
		mimeType = "application/octet-stream"
	}
	if maxBytes < 1 || maxBytes > maxTransferBytes {
		maxBytes = maxTransferBytes
	}
	if err := os.MkdirAll(s.dir, 0o700); err != nil {
		return FileTransfer{}, err
	}

	id, err := randomToken(18)
	if err != nil {
		return FileTransfer{}, err
	}

	name = filepath.Base(strings.TrimSpace(name))
	if name == "" || name == "." || name == string(filepath.Separator) {
		name = "support-file.bin"
	}
	if len(name) > 180 {
		name = name[:180]
	}

	path := filepath.Join(s.dir, id+".bin")
	f, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
	if err != nil {
		return FileTransfer{}, err
	}

	n, copyErr := io.Copy(f, io.LimitReader(src, maxBytes+1))
	closeErr := f.Close()
	if copyErr != nil {
		_ = os.Remove(path)
		return FileTransfer{}, copyErr
	}
	if closeErr != nil {
		_ = os.Remove(path)
		return FileTransfer{}, closeErr
	}
	if n > maxBytes {
		_ = os.Remove(path)
		return FileTransfer{}, errors.New("file exceeds transfer size limit")
	}

	now := time.Now().UTC()
	item := FileTransfer{
		ID: id, SessionID: sessionID, Direction: direction,
		Purpose: purpose, MimeType: mimeType,
		Name: name, Size: n, Path: path,
		CreatedAt: now, ExpiresAt: now.Add(transferTTL),
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	s.items[id] = item
	return item, nil
}

func (s *TransferStore) Get(id string) (FileTransfer, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(time.Now().UTC())
	item, ok := s.items[id]
	return item, ok
}

func (s *TransferStore) ListSession(sessionID, direction string) []FileTransfer {
	return s.ListSessionPurpose(sessionID, direction, "")
}

func (s *TransferStore) ListSessionPurpose(
	sessionID, direction, purpose string,
) []FileTransfer {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(time.Now().UTC())

	out := make([]FileTransfer, 0)
	for _, item := range s.items {
		if item.SessionID != sessionID {
			continue
		}
		if direction != "" && item.Direction != direction {
			continue
		}
		if purpose != "" && item.Purpose != purpose {
			continue
		}
		out = append(out, item)
	}

	sort.Slice(out, func(i, j int) bool {
		return out[i].CreatedAt.Before(out[j].CreatedAt)
	})
	return out
}

func (s *TransferStore) Remove(id string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if item, ok := s.items[id]; ok {
		_ = os.Remove(item.Path)
		delete(s.items, id)
	}
}

func (s *TransferStore) RemoveSession(sessionID string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for id, item := range s.items {
		if item.SessionID == sessionID {
			_ = os.Remove(item.Path)
			delete(s.items, id)
		}
	}
}

func (s *TransferStore) cleanupLocked(now time.Time) {
	for id, item := range s.items {
		if !item.ExpiresAt.After(now) {
			_ = os.Remove(item.Path)
			delete(s.items, id)
		}
	}
}
