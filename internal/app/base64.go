package app

import "encoding/base64"

func base64RawURLDecode(v string) ([]byte, error) {
	return base64.RawURLEncoding.DecodeString(v)
}
