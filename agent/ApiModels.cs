using System.Text.Json.Serialization;

namespace RemoteAssist.Agent;

internal sealed class LookupRequest
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
}

internal sealed class LookupResponse
{
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
    [JsonPropertyName("technician_name")] public string TechnicianName { get; set; } = "";
    [JsonPropertyName("customer_label")] public string CustomerLabel { get; set; } = "";
    [JsonPropertyName("requested_control")] public bool RequestedControl { get; set; }
    [JsonPropertyName("requested_elevation")] public bool RequestedElevation { get; set; }
    [JsonPropertyName("requested_clipboard")] public bool RequestedClipboard { get; set; }
    [JsonPropertyName("requested_file_transfer")] public bool RequestedFileTransfer { get; set; }
    [JsonPropertyName("expires_at")] public DateTime ExpiresAt { get; set; }
}

internal sealed class RedeemRequest
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("machine_name")] public string MachineName { get; set; } = Environment.MachineName;
    [JsonPropertyName("terms_accepted")] public bool TermsAccepted { get; set; }
}

internal sealed class RedeemResponse
{
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
    [JsonPropertyName("agent_token")] public string AgentToken { get; set; } = "";
    [JsonPropertyName("websocket_url")] public string WebSocketUrl { get; set; } = "";
    [JsonPropertyName("live_expires_at")] public DateTime LiveExpiresAt { get; set; }
}

internal sealed class EndSessionRequest
{
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
}

internal sealed class ErrorResponse
{
    [JsonPropertyName("error")] public string Error { get; set; } = "Request failed.";
}
