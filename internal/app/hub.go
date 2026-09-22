package app

import (
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

type wsPeer struct {
	conn *websocket.Conn
	mu   sync.Mutex
}

func (p *wsPeer) write(messageType int, data []byte) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	_ = p.conn.SetWriteDeadline(time.Now().Add(10 * time.Second))
	return p.conn.WriteMessage(messageType, data)
}

func (p *wsPeer) close(code int, reason string) {
	p.mu.Lock()
	defer p.mu.Unlock()
	_ = p.conn.SetWriteDeadline(time.Now().Add(2 * time.Second))
	_ = p.conn.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(code, reason), time.Now().Add(2*time.Second))
	_ = p.conn.Close()
}

type room struct {
	agent *wsPeer
	tech  *wsPeer
}

type Hub struct {
	mu    sync.RWMutex
	rooms map[string]*room
}

func NewHub() *Hub { return &Hub{rooms: make(map[string]*room)} }

func (h *Hub) setAgent(id string, p *wsPeer) (old *wsPeer) {
	h.mu.Lock()
	defer h.mu.Unlock()
	r := h.rooms[id]
	if r == nil {
		r = &room{}
		h.rooms[id] = r
	}
	old = r.agent
	r.agent = p
	return old
}

func (h *Hub) setTech(id string, p *wsPeer) (old *wsPeer) {
	h.mu.Lock()
	defer h.mu.Unlock()
	r := h.rooms[id]
	if r == nil {
		r = &room{}
		h.rooms[id] = r
	}
	old = r.tech
	r.tech = p
	return old
}

func (h *Hub) unsetAgent(id string, p *wsPeer) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	if r := h.rooms[id]; r != nil && r.agent == p {
		r.agent = nil
		if r.tech == nil {
			delete(h.rooms, id)
		}
		return true
	}
	return false
}

func (h *Hub) unsetTech(id string, p *wsPeer) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	if r := h.rooms[id]; r != nil && r.tech == p {
		r.tech = nil
		if r.agent == nil {
			delete(h.rooms, id)
		}
		return true
	}
	return false
}

func (h *Hub) hasTech(id string) bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	r := h.rooms[id]
	return r != nil && r.tech != nil
}

func (h *Hub) hasAgent(id string) bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	r := h.rooms[id]
	return r != nil && r.agent != nil
}

func (h *Hub) sendToTech(id string, messageType int, data []byte) error {
	h.mu.RLock()
	var p *wsPeer
	if r := h.rooms[id]; r != nil {
		p = r.tech
	}
	h.mu.RUnlock()
	if p == nil {
		return nil
	}
	return p.write(messageType, data)
}

func (h *Hub) sendToAgent(id string, messageType int, data []byte) error {
	h.mu.RLock()
	var p *wsPeer
	if r := h.rooms[id]; r != nil {
		p = r.agent
	}
	h.mu.RUnlock()
	if p == nil {
		return nil
	}
	return p.write(messageType, data)
}

func (h *Hub) End(id string) {
	h.mu.Lock()
	r := h.rooms[id]
	delete(h.rooms, id)
	h.mu.Unlock()
	if r != nil {
		if r.agent != nil {
			r.agent.close(websocket.CloseNormalClosure, "session ended")
		}
		if r.tech != nil {
			r.tech.close(websocket.CloseNormalClosure, "session ended")
		}
	}
}
