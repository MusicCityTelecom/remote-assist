package app

import "testing"

func TestHubAgentReplacementCleanup(t *testing.T) {
	h := NewHub()
	first := &wsPeer{}
	second := &wsPeer{}

	if old := h.setAgent("session-1", first); old != nil {
		t.Fatal("unexpected existing agent")
	}
	if old := h.setAgent("session-1", second); old != first {
		t.Fatal("replacement did not return first agent")
	}
	if h.unsetAgent("session-1", first) {
		t.Fatal("replaced agent was allowed to clear current room agent")
	}
	if !h.unsetAgent("session-1", second) {
		t.Fatal("current agent cleanup was not recognized")
	}
}

func TestHubTechnicianReplacementCleanup(t *testing.T) {
	h := NewHub()
	first := &wsPeer{}
	second := &wsPeer{}

	if old := h.setTech("session-2", first); old != nil {
		t.Fatal("unexpected existing technician")
	}
	if old := h.setTech("session-2", second); old != first {
		t.Fatal("replacement did not return first technician")
	}
	if h.unsetTech("session-2", first) {
		t.Fatal("replaced technician was allowed to clear current room technician")
	}
	if !h.unsetTech("session-2", second) {
		t.Fatal("current technician cleanup was not recognized")
	}
}
