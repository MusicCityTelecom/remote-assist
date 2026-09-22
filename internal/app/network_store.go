package app

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"net"
	"strings"
	"time"
)

type NetworkAdapterSnapshot struct {
	Name         string   `json:"name"`
	Description  string   `json:"description"`
	Method       string   `json:"method"`
	IPv4         []string `json:"ipv4"`
	Netmasks     []string `json:"netmasks"`
	Gateways     []string `json:"gateways"`
	DNSServers   []string `json:"dns_servers"`
	DefaultRoute bool     `json:"default_route"`
}

type SessionNetworkSnapshot struct {
	PublicIP   string                   `json:"public_ip"`
	Adapters   []NetworkAdapterSnapshot `json:"adapters"`
	CapturedAt time.Time                `json:"captured_at"`
	UpdatedAt  time.Time                `json:"updated_at,omitempty"`
}

func (s *Store) migrateNetwork(ctx context.Context) error {
	_, err := s.db.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS support_session_network (
		session_id VARCHAR(36) NOT NULL PRIMARY KEY,
		public_ip VARCHAR(45) NOT NULL DEFAULT '',
		snapshot_json LONGTEXT NOT NULL,
		captured_at DATETIME(6) NOT NULL,
		updated_at DATETIME(6) NOT NULL,
		CONSTRAINT fk_support_session_network_session
			FOREIGN KEY (session_id) REFERENCES support_sessions(id)
			ON DELETE CASCADE
	) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci`)
	return err
}

func normalizeNetworkSnapshot(snapshot SessionNetworkSnapshot) SessionNetworkSnapshot {
	if ip := net.ParseIP(strings.TrimSpace(snapshot.PublicIP)); ip != nil {
		snapshot.PublicIP = ip.String()
	} else {
		snapshot.PublicIP = ""
	}

	if snapshot.CapturedAt.IsZero() {
		snapshot.CapturedAt = time.Now().UTC()
	} else {
		snapshot.CapturedAt = snapshot.CapturedAt.UTC()
	}

	if len(snapshot.Adapters) > 32 {
		snapshot.Adapters = snapshot.Adapters[:32]
	}

	for i := range snapshot.Adapters {
		a := &snapshot.Adapters[i]
		a.Name = truncateString(strings.TrimSpace(a.Name), 160)
		a.Description = truncateString(strings.TrimSpace(a.Description), 255)
		a.Method = truncateString(strings.TrimSpace(a.Method), 64)
		a.IPv4 = normalizeStringList(a.IPv4, 16, 64)
		a.Netmasks = normalizeStringList(a.Netmasks, 16, 64)
		a.Gateways = normalizeStringList(a.Gateways, 16, 64)
		a.DNSServers = normalizeStringList(a.DNSServers, 16, 64)
	}

	return snapshot
}

func normalizeStringList(values []string, maxItems, maxLength int) []string {
	if len(values) > maxItems {
		values = values[:maxItems]
	}
	out := make([]string, 0, len(values))
	seen := make(map[string]struct{}, len(values))
	for _, value := range values {
		value = truncateString(strings.TrimSpace(value), maxLength)
		if value == "" {
			continue
		}
		if _, ok := seen[value]; ok {
			continue
		}
		seen[value] = struct{}{}
		out = append(out, value)
	}
	return out
}

func truncateString(value string, max int) string {
	if len(value) <= max {
		return value
	}
	return value[:max]
}

func (s *Store) UpsertSessionNetwork(
	ctx context.Context,
	sessionID string,
	snapshot SessionNetworkSnapshot,
) error {
	snapshot = normalizeNetworkSnapshot(snapshot)
	raw, err := json.Marshal(snapshot)
	if err != nil {
		return err
	}
	now := time.Now().UTC()
	_, err = s.db.ExecContext(ctx, `INSERT INTO support_session_network
		(session_id, public_ip, snapshot_json, captured_at, updated_at)
		VALUES (?, ?, ?, ?, ?)
		ON DUPLICATE KEY UPDATE
			public_ip=VALUES(public_ip),
			snapshot_json=VALUES(snapshot_json),
			captured_at=VALUES(captured_at),
			updated_at=VALUES(updated_at)`,
		sessionID,
		snapshot.PublicIP,
		string(raw),
		snapshot.CapturedAt,
		now)
	return err
}

func (s *Store) GetSessionNetwork(
	ctx context.Context,
	sessionID string,
) (*SessionNetworkSnapshot, error) {
	var raw string
	var publicIP string
	var capturedAt, updatedAt time.Time
	err := s.db.QueryRowContext(ctx, `SELECT
		public_ip, snapshot_json, captured_at, updated_at
		FROM support_session_network
		WHERE session_id=?`, sessionID).Scan(
		&publicIP,
		&raw,
		&capturedAt,
		&updatedAt,
	)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}

	var snapshot SessionNetworkSnapshot
	if err := json.Unmarshal([]byte(raw), &snapshot); err != nil {
		return nil, err
	}
	snapshot.PublicIP = publicIP
	snapshot.CapturedAt = capturedAt.UTC()
	snapshot.UpdatedAt = updatedAt.UTC()
	return &snapshot, nil
}
