package app

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"fmt"
	"regexp"
	"strings"
)

const passwordIterations = 310000

var usernamePattern = regexp.MustCompile(`^[a-z0-9][a-z0-9._-]{2,47}$`)

func normalizeUsername(v string) string {
	return strings.ToLower(strings.TrimSpace(v))
}

func validateUsername(v string) error {
	if !usernamePattern.MatchString(normalizeUsername(v)) {
		return errors.New("username must be 3-48 characters using letters, numbers, dot, underscore, or dash")
	}
	return nil
}

func validateRole(v string) error {
	if v != "admin" && v != "technician" {
		return errors.New("role must be admin or technician")
	}
	return nil
}

func validatePassword(v string) error {
	if len(v) < 14 {
		return errors.New("password must be at least 14 characters")
	}
	if len(v) > 256 {
		return errors.New("password is too long")
	}
	return nil
}

func passwordHash(password string) (saltHex, hashHex string, iterations int, err error) {
	if err = validatePassword(password); err != nil {
		return "", "", 0, err
	}
	salt := make([]byte, 24)
	if _, err = rand.Read(salt); err != nil {
		return "", "", 0, err
	}
	hash := pbkdf2SHA256([]byte(password), salt, passwordIterations, 32)
	return hex.EncodeToString(salt), hex.EncodeToString(hash), passwordIterations, nil
}

func verifyPassword(password, saltHex, hashHex string, iterations int) bool {
	if iterations < 100000 || iterations > 2000000 {
		return false
	}
	salt, err := hex.DecodeString(saltHex)
	if err != nil || len(salt) < 16 {
		return false
	}
	expected, err := hex.DecodeString(hashHex)
	if err != nil || len(expected) != 32 {
		return false
	}
	actual := pbkdf2SHA256([]byte(password), salt, iterations, len(expected))
	return subtle.ConstantTimeCompare(actual, expected) == 1
}

func pbkdf2SHA256(password, salt []byte, iterations, keyLen int) []byte {
	if iterations <= 0 || keyLen <= 0 {
		return nil
	}
	hLen := 32
	blocks := (keyLen + hLen - 1) / hLen
	out := make([]byte, 0, blocks*hLen)
	for block := 1; block <= blocks; block++ {
		mac := hmac.New(sha256.New, password)
		_, _ = mac.Write(salt)
		var counter [4]byte
		binary.BigEndian.PutUint32(counter[:], uint32(block))
		_, _ = mac.Write(counter[:])
		u := mac.Sum(nil)
		t := append([]byte(nil), u...)
		for i := 1; i < iterations; i++ {
			mac = hmac.New(sha256.New, password)
			_, _ = mac.Write(u)
			u = mac.Sum(nil)
			for j := range t {
				t[j] ^= u[j]
			}
		}
		out = append(out, t...)
	}
	return out[:keyLen]
}

func passwordFingerprint(saltHex, hashHex string, iterations int) string {
	sum := sha256.Sum256([]byte(fmt.Sprintf("%s:%s:%d", saltHex, hashHex, iterations)))
	return hex.EncodeToString(sum[:8])
}
