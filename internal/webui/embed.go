package webui

import "embed"

// Files contains the technician console and customer download page.
//
//go:embed web/*
var Files embed.FS
