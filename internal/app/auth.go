package app

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"time"
)

const techCookieName = "remote-assist_tech"

type authClaims struct {
	Username string `json:"u"`
	Expires  int64  `json:"exp"`
	Nonce    string `json:"n"`
}

func secureEqual(a, b string) bool {
	ab, bb := []byte(a), []byte(b)
	if len(ab) != len(bb) {
		return false
	}
	return subtle.ConstantTimeCompare(ab, bb) == 1
}

func issueTechCookie(w http.ResponseWriter, cfg Config) error {
	nonce, err := randomToken(18)
	if err != nil {
		return err
	}
	claims := authClaims{Username: cfg.TechUsername, Expires: time.Now().Add(12 * time.Hour).Unix(), Nonce: nonce}
	payload, err := json.Marshal(claims)
	if err != nil {
		return err
	}
	enc := base64.RawURLEncoding.EncodeToString(payload)
	sig := hmacHex(cfg.CookieSecret, enc)
	http.SetCookie(w, &http.Cookie{
		Name:     techCookieName,
		Value:    enc + "." + sig,
		Path:     "/",
		MaxAge:   12 * 60 * 60,
		Expires:  time.Now().Add(12 * time.Hour),
		HttpOnly: true,
		Secure:   true,
		SameSite: http.SameSiteStrictMode,
	})
	return nil
}

func clearTechCookie(w http.ResponseWriter) {
	http.SetCookie(w, &http.Cookie{Name: techCookieName, Value: "", Path: "/", MaxAge: -1, HttpOnly: true, Secure: true, SameSite: http.SameSiteStrictMode})
}

func authenticateTech(r *http.Request, cfg Config) (string, error) {
	c, err := r.Cookie(techCookieName)
	if err != nil {
		return "", err
	}
	parts := strings.Split(c.Value, ".")
	if len(parts) != 2 || !hmac.Equal([]byte(parts[1]), []byte(hmacHex(cfg.CookieSecret, parts[0]))) {
		return "", errors.New("invalid session cookie")
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[0])
	if err != nil {
		return "", err
	}
	var claims authClaims
	if err := json.Unmarshal(payload, &claims); err != nil {
		return "", err
	}
	if claims.Username != cfg.TechUsername || claims.Expires < time.Now().Unix() {
		return "", errors.New("expired or invalid session")
	}
	return claims.Username, nil
}

func randomToken(n int) (string, error) {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(b), nil
}

func randomDigits(n int) (string, error) {
	if n < 1 || n > 18 {
		return "", fmt.Errorf("invalid digit count")
	}
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	for i := range b {
		b[i] = '0' + (b[i] % 10)
	}
	return string(b), nil
}

func hmacHex(secret []byte, value string) string {
	m := hmac.New(sha256.New, secret)
	_, _ = m.Write([]byte(value))
	return hex.EncodeToString(m.Sum(nil))
}

func sha256Hex(value string) string {
	h := sha256.Sum256([]byte(value))
	return hex.EncodeToString(h[:])
}
