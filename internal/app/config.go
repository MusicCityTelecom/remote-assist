package app

import (
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"
)

type Config struct {
	ListenAddr        string
	PublicBaseURL     string
	MySQLDSN          string
	TechUsername      string
	TechPassword      string
	CookieSecret      []byte
	CodeSecret        []byte
	SessionTTL        time.Duration
	LiveSessionTTL    time.Duration
	AgentDownloadPath          string
	TechnicianPortablePath     string
	TechnicianInstallerPath    string
	TrustProxy                 bool
}

func LoadConfig() (Config, error) {
	cfg := Config{
		ListenAddr:        getenv("LISTEN_ADDR", "127.0.0.1:8787"),
		PublicBaseURL:     strings.TrimRight(getenv("PUBLIC_BASE_URL", "https://support.example.com"), "/"),
		MySQLDSN:          os.Getenv("MYSQL_DSN"),
		TechUsername:      getenv("TECH_USERNAME", "admin"),
		TechPassword:      os.Getenv("TECH_PASSWORD"),
		AgentDownloadPath:       getenv("AGENT_DOWNLOAD_PATH", "/opt/remote-assist/downloads/RemoteAssist.exe"),
		TechnicianPortablePath:  getenv("TECHNICIAN_PORTABLE_PATH", "/opt/remote-assist/downloads/Remote-Assist-Technician-Portable.zip"),
		TechnicianInstallerPath: getenv("TECHNICIAN_INSTALLER_PATH", "/opt/remote-assist/downloads/Remote-Assist-Technician-Setup.exe"),
		TrustProxy:              getenvBool("TRUST_PROXY", true),
	}

	if cfg.MySQLDSN == "" {
		return Config{}, errors.New("MYSQL_DSN is required")
	}
	if len(cfg.TechPassword) < 12 {
		return Config{}, errors.New("TECH_PASSWORD must be at least 12 characters")
	}

	var err error
	cfg.CookieSecret, err = decodeSecret("COOKIE_SECRET")
	if err != nil {
		return Config{}, err
	}
	cfg.CodeSecret, err = decodeSecret("CODE_SECRET")
	if err != nil {
		return Config{}, err
	}

	ttlMinutes, err := strconv.Atoi(getenv("SESSION_TTL_MINUTES", "15"))
	if err != nil || ttlMinutes < 5 || ttlMinutes > 120 {
		return Config{}, fmt.Errorf("SESSION_TTL_MINUTES must be an integer from 5 to 120")
	}
	cfg.SessionTTL = time.Duration(ttlMinutes) * time.Minute

	liveTTLMinutes, err := strconv.Atoi(getenv("LIVE_SESSION_TTL_MINUTES", "480"))
	if err != nil || liveTTLMinutes < 30 || liveTTLMinutes > 1440 {
		return Config{}, fmt.Errorf("LIVE_SESSION_TTL_MINUTES must be an integer from 30 to 1440")
	}
	cfg.LiveSessionTTL = time.Duration(liveTTLMinutes) * time.Minute
	return cfg, nil
}

func decodeSecret(name string) ([]byte, error) {
	raw := strings.TrimSpace(os.Getenv(name))
	if raw == "" {
		return nil, fmt.Errorf("%s is required", name)
	}
	b, err := hex.DecodeString(raw)
	if err != nil || len(b) < 32 {
		return nil, fmt.Errorf("%s must be at least 32 random bytes encoded as hex", name)
	}
	return b, nil
}

func getenv(name, fallback string) string {
	if v := os.Getenv(name); v != "" {
		return v
	}
	return fallback
}

func getenvBool(name string, fallback bool) bool {
	v := strings.TrimSpace(strings.ToLower(os.Getenv(name)))
	if v == "" {
		return fallback
	}
	return v == "1" || v == "true" || v == "yes" || v == "on"
}
