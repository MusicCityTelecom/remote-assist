package app

import (
	"bytes"
	"io"
	"os"
	"path/filepath"
	"testing"
	"time"
)

type zeroReader struct{}

func (zeroReader) Read(p []byte) (int, error) {
	for i := range p {
		p[i] = 0
	}
	return len(p), nil
}

func TestTransferStorePutGetAndRemoveSession(t *testing.T) {
	store := NewTransferStore()
	store.dir = t.TempDir()

	item, err := store.Put("session-a", "to_agent", "../example.txt", bytes.NewBufferString("hello"))
	if err != nil {
		t.Fatal(err)
	}
	if item.Name != "example.txt" {
		t.Fatalf("filename was not sanitized: %q", item.Name)
	}
	if item.Size != 5 {
		t.Fatalf("unexpected size: %d", item.Size)
	}
	if filepath.Dir(item.Path) != store.dir {
		t.Fatalf("transfer escaped temp directory: %s", item.Path)
	}

	got, ok := store.Get(item.ID)
	if !ok || got.ID != item.ID {
		t.Fatal("stored transfer was not retrievable")
	}
	data, err := os.ReadFile(item.Path)
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != "hello" {
		t.Fatalf("unexpected transfer content: %q", data)
	}

	store.RemoveSession("session-a")
	if _, ok := store.Get(item.ID); ok {
		t.Fatal("session transfer remained after RemoveSession")
	}
	if _, err := os.Stat(item.Path); !os.IsNotExist(err) {
		t.Fatalf("transfer file still exists after RemoveSession: %v", err)
	}
}

func TestTransferStoreRejectsDirectionAndOversize(t *testing.T) {
	store := NewTransferStore()
	store.dir = t.TempDir()

	if _, err := store.Put("session-a", "sideways", "x.txt", bytes.NewBufferString("x")); err == nil {
		t.Fatal("invalid direction was accepted")
	}

	reader := io.LimitReader(zeroReader{}, maxTransferBytes+1)
	if _, err := store.Put("session-a", "to_tech", "too-large.bin", reader); err == nil {
		t.Fatal("oversize transfer was accepted")
	}

	entries, err := os.ReadDir(store.dir)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 0 {
		t.Fatalf("oversize transfer left temporary files behind: %d", len(entries))
	}
}

func TestTransferStoreListSessionFiltersAndOrders(t *testing.T) {
	store := NewTransferStore()
	store.dir = t.TempDir()

	first, err := store.Put("session-a", "to_tech", "first.txt", bytes.NewBufferString("one"))
	if err != nil {
		t.Fatal(err)
	}
	time.Sleep(time.Millisecond)
	second, err := store.Put("session-a", "to_tech", "second.txt", bytes.NewBufferString("two"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.Put("session-a", "to_agent", "customer.txt", bytes.NewBufferString("x")); err != nil {
		t.Fatal(err)
	}
	if _, err := store.Put("session-b", "to_tech", "other.txt", bytes.NewBufferString("y")); err != nil {
		t.Fatal(err)
	}

	items := store.ListSession("session-a", "to_tech")
	if len(items) != 2 {
		t.Fatalf("expected 2 pending technician files, got %d", len(items))
	}
	if items[0].ID != first.ID || items[1].ID != second.ID {
		t.Fatalf("pending transfer order changed: %+v", items)
	}

	all := store.ListSession("session-a", "")
	if len(all) != 3 {
		t.Fatalf("expected all 3 session-a transfers, got %d", len(all))
	}
}

func TestTransferStoreChatImageMetadataAndPurposeFilter(t *testing.T) {
	store := NewTransferStore()
	store.dir = t.TempDir()

	image, err := store.PutWithMetadata(
		"session-a",
		"to_tech",
		"chat_image",
		"image/png",
		"screen.png",
		bytes.NewBufferString("png-bytes"),
		maxChatImageBytes,
	)
	if err != nil {
		t.Fatal(err)
	}
	if image.Purpose != "chat_image" || image.MimeType != "image/png" {
		t.Fatalf("chat image metadata was lost: %+v", image)
	}

	if _, err := store.Put(
		"session-a",
		"to_tech",
		"normal.txt",
		bytes.NewBufferString("text"),
	); err != nil {
		t.Fatal(err)
	}

	images := store.ListSessionPurpose("session-a", "to_tech", "chat_image")
	if len(images) != 1 || images[0].ID != image.ID {
		t.Fatalf("unexpected chat image filter result: %+v", images)
	}

	files := store.ListSessionPurpose("session-a", "to_tech", "file")
	if len(files) != 1 || files[0].Purpose != "file" {
		t.Fatalf("normal file filter included wrong transfers: %+v", files)
	}
}

func TestChatImageHelpers(t *testing.T) {
	if normalizeTransferPurpose(" CHAT_IMAGE ") != "chat_image" {
		t.Fatal("chat image purpose was not normalized")
	}
	if normalizeTransferPurpose("anything-else") != "file" {
		t.Fatal("unknown transfer purpose was not reduced to file")
	}

	for _, value := range []string{"image/jpeg", "image/png", "image/gif"} {
		if !allowedChatImageMime(value) {
			t.Fatalf("expected image MIME to be allowed: %s", value)
		}
	}
	for _, value := range []string{"image/svg+xml", "text/html", "application/octet-stream"} {
		if allowedChatImageMime(value) {
			t.Fatalf("unexpected image MIME allowed: %s", value)
		}
	}
}
