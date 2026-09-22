package app

import (
	"strings"
	"testing"
)

func TestRandomDigits(t *testing.T) {
	for i := 0; i < 100; i++ {
		v, err := randomDigits(8)
		if err != nil {
			t.Fatal(err)
		}
		if len(v) != 8 {
			t.Fatalf("unexpected length: %q", v)
		}
		if strings.Trim(v, "0123456789") != "" {
			t.Fatalf("non-digit output: %q", v)
		}
	}
}

func TestCodeHMACIsStableAndSecretDependent(t *testing.T) {
	a := []byte(strings.Repeat("a", 32))
	b := []byte(strings.Repeat("b", 32))
	if hmacHex(a, "12345678") != hmacHex(a, "12345678") {
		t.Fatal("HMAC should be stable")
	}
	if hmacHex(a, "12345678") == hmacHex(b, "12345678") {
		t.Fatal("different secrets should produce different HMACs")
	}
}

func TestNormalizeCode(t *testing.T) {
	if got := normalizeCode("1234-5678"); got != "12345678" {
		t.Fatalf("got %q", got)
	}
}

func TestSecureEqual(t *testing.T) {
	if !secureEqual("same", "same") {
		t.Fatal("equal values should match")
	}
	if secureEqual("same", "different") {
		t.Fatal("different values should not match")
	}
}
