package app

import (
	"encoding/hex"
	"testing"
)

func TestPBKDF2SHA256Vector(t *testing.T) {
	got := hex.EncodeToString(pbkdf2SHA256([]byte("password"), []byte("salt"), 1, 32))
	const want = "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b"
	if got != want {
		t.Fatalf("PBKDF2 vector mismatch: got %s want %s", got, want)
	}
}

func TestPasswordHashRoundTrip(t *testing.T) {
	const password = "Correct-Horse-2026-Battery!"
	salt, hash, iterations, err := passwordHash(password)
	if err != nil {
		t.Fatal(err)
	}
	if !verifyPassword(password, salt, hash, iterations) {
		t.Fatal("correct password did not verify")
	}
	if verifyPassword("wrong-password-value", salt, hash, iterations) {
		t.Fatal("wrong password verified")
	}
}

func TestAdminInputValidation(t *testing.T) {
	if normalizeUsername("  Tommy.Admin ") != "tommy.admin" {
		t.Fatal("username normalization failed")
	}
	if err := validateUsername("ok-user_1"); err != nil {
		t.Fatalf("valid username rejected: %v", err)
	}
	if err := validateUsername("x"); err == nil {
		t.Fatal("short username accepted")
	}
	if err := validateRole("admin"); err != nil {
		t.Fatal(err)
	}
	if err := validateRole("owner"); err == nil {
		t.Fatal("unknown role accepted")
	}
	if err := validatePassword("too-short"); err == nil {
		t.Fatal("short password accepted")
	}
}
