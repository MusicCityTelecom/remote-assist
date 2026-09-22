package app

import (
	"strings"
	"testing"
	"time"
)

func TestNormalizeChatBody(t *testing.T) {
	body, err := normalizeChatBody("  hello customer  ")
	if err != nil {
		t.Fatal(err)
	}
	if body != "hello customer" {
		t.Fatalf("unexpected normalized body %q", body)
	}

	if _, err := normalizeChatBody("   "); err == nil {
		t.Fatal("empty chat message was accepted")
	}

	if _, err := normalizeChatBody(strings.Repeat("x", maxChatBodyBytes+1)); err == nil {
		t.Fatal("oversize chat message was accepted")
	}
}

func TestNormalizeNetworkSnapshot(t *testing.T) {
	now := time.Now().UTC().Add(-time.Minute)
	snapshot := normalizeNetworkSnapshot(SessionNetworkSnapshot{
		PublicIP:   " 173.10.222.118 ",
		CapturedAt: now,
		Adapters: []NetworkAdapterSnapshot{
			{
				Name:         " Ethernet ",
				Description:  " Intel Ethernet Adapter ",
				Method:       " Wired ",
				IPv4:         []string{"192.168.1.25", "192.168.1.25", ""},
				Netmasks:     []string{"255.255.255.0"},
				Gateways:     []string{"192.168.1.1"},
				DNSServers:   []string{"1.1.1.1", "8.8.8.8", "1.1.1.1"},
				DefaultRoute: true,
			},
		},
	})

	if snapshot.PublicIP != "173.10.222.118" {
		t.Fatalf("unexpected public ip %q", snapshot.PublicIP)
	}
	if !snapshot.CapturedAt.Equal(now) {
		t.Fatalf("captured timestamp changed: %s", snapshot.CapturedAt)
	}
	if len(snapshot.Adapters) != 1 {
		t.Fatalf("unexpected adapter count %d", len(snapshot.Adapters))
	}

	adapter := snapshot.Adapters[0]
	if adapter.Name != "Ethernet" || adapter.Method != "Wired" {
		t.Fatalf("adapter strings not normalized: %+v", adapter)
	}
	if len(adapter.IPv4) != 1 || adapter.IPv4[0] != "192.168.1.25" {
		t.Fatalf("IPv4 list not normalized: %+v", adapter.IPv4)
	}
	if len(adapter.DNSServers) != 2 {
		t.Fatalf("DNS list not deduplicated: %+v", adapter.DNSServers)
	}
	if !adapter.DefaultRoute {
		t.Fatal("default route flag was lost")
	}
}

func TestNormalizeNetworkSnapshotRejectsInvalidPublicIP(t *testing.T) {
	snapshot := normalizeNetworkSnapshot(SessionNetworkSnapshot{
		PublicIP: "not-an-ip",
	})
	if snapshot.PublicIP != "" {
		t.Fatalf("invalid public IP was retained: %q", snapshot.PublicIP)
	}
}
