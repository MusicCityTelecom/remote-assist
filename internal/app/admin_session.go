package app

import (
	"crypto/hmac"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/http"
	"strconv"
	"strings"
	"time"
)

type adminClaims struct {
	AdminID     int64  `json:"id"`
	Username    string `json:"u"`
	Role        string `json:"r"`
	AuthVersion int64  `json:"v"`
	Expires     int64  `json:"exp"`
	Nonce       string `json:"n"`
}

func issueAdminCookie(w http.ResponseWriter, cfg Config, admin Admin) error {
	nonce, err := randomToken(18)
	if err != nil {
		return err
	}
	claims := adminClaims{
		AdminID: admin.ID, Username: admin.Username, Role: admin.Role,
		AuthVersion: admin.AuthVersion, Expires: time.Now().Add(12 * time.Hour).Unix(), Nonce: nonce,
	}
	payload, err := json.Marshal(claims)
	if err != nil {
		return err
	}
	enc := base64.RawURLEncoding.EncodeToString(payload)
	sig := hmacHex(cfg.CookieSecret, enc)
	http.SetCookie(w, &http.Cookie{
		Name: techCookieName, Value: enc + "." + sig, Path: "/",
		MaxAge: 12 * 60 * 60, Expires: time.Now().Add(12 * time.Hour),
		HttpOnly: true, Secure: true, SameSite: http.SameSiteStrictMode,
	})
	return nil
}

func authenticateAdmin(r *http.Request, cfg Config, store *Store) (Admin, error) {
	c, err := r.Cookie(techCookieName)
	if err != nil {
		return Admin{}, err
	}
	parts := strings.Split(c.Value, ".")
	if len(parts) != 2 || !hmac.Equal([]byte(parts[1]), []byte(hmacHex(cfg.CookieSecret, parts[0]))) {
		return Admin{}, errors.New("invalid session cookie")
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[0])
	if err != nil {
		return Admin{}, err
	}
	var claims adminClaims
	if err := json.Unmarshal(payload, &claims); err != nil {
		return Admin{}, err
	}
	if claims.AdminID <= 0 || claims.Expires < time.Now().Unix() {
		return Admin{}, errors.New("expired or invalid session")
	}
	admin, err := store.GetAdminByID(r.Context(), claims.AdminID)
	if err != nil {
		return Admin{}, err
	}
	if !admin.Active || admin.AuthVersion != claims.AuthVersion ||
		admin.Username != claims.Username || admin.Role != claims.Role {
		return Admin{}, errors.New("account changed or disabled")
	}
	return admin, nil
}

func adminIDString(id int64) string { return strconv.FormatInt(id, 10) }
