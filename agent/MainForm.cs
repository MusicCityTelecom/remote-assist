using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace RemoteAssist.Agent;

internal sealed class MainForm : Form
{
    private readonly StartupOptions _options;
    private readonly HttpClient _http = new();
    private readonly TextBox _code = new();
    private readonly CheckBox _terms = new();
    private readonly Button _connect = new();
    private readonly Button _elevate = new();
    private readonly Button _disconnect = new();
    private readonly Button _sendFile = new();
    private readonly Button _chat = new();
    private readonly Label _status = new();
    private readonly Label _detail = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<SupportChatMessage> _chatMessages = new();

    private ClientWebSocket? _ws;
    private ChatForm? _chatForm;
    private CancellationTokenSource? _sessionCts;
    private bool _requestedControl;
    private bool _requestedClipboard;
    private bool _requestedFileTransfer;
    private string? _sessionId;
    private string? _agentToken;
    private string? _webSocketUrl;
    private DateTime _liveExpiresAtUtc;
    private int _monitorIndex = -1;
    private int _jpegQuality = 55;
    private int _scalePercent = 100;
    private int _fps = 6;
    private int _adaptiveFpsEnabled;
    private long _adaptiveFpsLastAdjust;
    private string _captureMode = "auto";
    private H264CapabilityInfo? _h264Capability;
    private int _viewerH264Supported;
    private string _videoTransport = "jpeg";
    private H264MediaFoundationEncoder? _h264Encoder;
    private string? _h264LastError;
    private int _h264EncoderMonitor = -1;
    private int _h264EncoderScale;
    private int _h264EncoderFps;
    private NetworkSnapshot? _networkSnapshot;
    private DateTime _networkSnapshotAtUtc;
    private string _captureBackend = "initializing";
    private byte[]? _lastSentFrame;
    private long _captureWindowSent;
    private long _captureWindowSkipped;
    private long _captureWindowBytes;
    private long _captureWindowElapsedTicks;
    private long _captureWindowSamples;
    private long _captureTelemetryStamp;
    private int _viewerConnected;
    private int _reconnectGate;
    private bool _explicitEndInProgress;
    private bool _restartingForElevation;
    private int _unreadChat;
    private bool _closing;

    public MainForm(StartupOptions options)
    {
        _options = options;
        Text = "Remote Assist";
        Width = 560;
        Height = 520;
        MinimumSize = new Size(520, 490);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 248, 252);
        Font = new Font("Segoe UI", 10F);
        BuildUi();
        _code.Text = NormalizeCode(options.Code ?? "");
        FormClosing += OnFormClosing;
        Shown += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_options.ResumeFile))
                await ResumeElevatedSessionAsync(_options.ResumeFile);
        };
    }

    private void BuildUi()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(36) };
        Controls.Add(panel);

        var brand = new Label
        {
            Text = "remote-assist",
            Font = new Font("Segoe UI", 26F, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 49, 82),
            AutoSize = true,
            Top = 28,
            Left = 36
        };
        var subtitle = new Label
        {
            Text = "REMOTE SUPPORT  ·  REMOTE ASSIST",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = Color.FromArgb(36, 99, 235),
            AutoSize = true,
            Top = 73,
            Left = 39
        };
        var intro = new Label
        {
            Text = "Enter the temporary support code provided by your technician.",
            AutoSize = false,
            Width = 455,
            Height = 42,
            Top = 112,
            Left = 36,
            ForeColor = Color.FromArgb(85, 101, 120)
        };
        var codeLabel = new Label
        {
            Text = "Support code",
            AutoSize = true,
            Top = 162,
            Left = 36,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        _code.SetBounds(36, 186, 455, 43);
        _code.Font = new Font("Consolas", 17F, FontStyle.Bold);
        _code.MaxLength = 12;
        _code.TextAlign = HorizontalAlignment.Center;

        _terms.SetBounds(36, 245, 455, 45);
        _terms.Text = "I agree to the remote-support terms and understand that I must approve the technician's access.";
        _terms.ForeColor = Color.FromArgb(70, 83, 100);

        _connect.SetBounds(36, 302, 220, 45);
        _connect.Text = "Continue";
        StylePrimary(_connect);
        _connect.Click += async (_, _) => await BeginSessionAsync();

        _elevate.SetBounds(271, 302, 220, 45);
        _elevate.Text = Program.IsAdministrator() ? "Running as Administrator" : "Restart as Administrator";
        _elevate.Enabled = !Program.IsAdministrator();
        _elevate.Click += (_, _) => RestartElevated();

        _disconnect.SetBounds(36, 302, 455, 45);
        _disconnect.Text = "End Support Session";
        _disconnect.BackColor = Color.FromArgb(183, 55, 55);
        _disconnect.ForeColor = Color.White;
        _disconnect.FlatStyle = FlatStyle.Flat;
        _disconnect.Visible = false;
        _disconnect.Click += async (_, _) => await EndSessionFromCustomerAsync();

        _sendFile.SetBounds(271, 302, 220, 45);
        _sendFile.Text = "Send File to Technician";
        _sendFile.BackColor = Color.FromArgb(234, 240, 247);
        _sendFile.ForeColor = Color.FromArgb(20, 49, 82);
        _sendFile.FlatStyle = FlatStyle.Flat;
        _sendFile.FlatAppearance.BorderSize = 0;
        _sendFile.Visible = false;
        _sendFile.Click += async (_, _) => await SendFileToTechnicianAsync();

        _chat.SetBounds(36, 355, 455, 42);
        _chat.Text = "Chat with Technician";
        _chat.BackColor = Color.FromArgb(234, 240, 247);
        _chat.ForeColor = Color.FromArgb(20, 49, 82);
        _chat.FlatStyle = FlatStyle.Flat;
        _chat.FlatAppearance.BorderSize = 0;
        _chat.Visible = false;
        _chat.Click += (_, _) => ShowChatWindow();

        _status.SetBounds(36, 410, 455, 22);
        _status.Text = "Not connected";
        _status.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _status.ForeColor = Color.FromArgb(100, 114, 130);

        _detail.SetBounds(36, 434, 455, 44);
        _detail.Text = $"Server: {_options.Server}";
        _detail.ForeColor = Color.FromArgb(120, 132, 145);
        _detail.Font = new Font("Segoe UI", 8F);

        panel.Controls.AddRange([brand, subtitle, intro, codeLabel, _code, _terms, _connect, _elevate, _disconnect, _sendFile, _chat, _status, _detail]);
    }

    private static void StylePrimary(Button b)
    {
        b.BackColor = Color.FromArgb(36, 99, 235);
        b.ForeColor = Color.White;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
    }

    private async Task BeginSessionAsync()
    {
        if (_sessionCts is not null) return;

        var code = NormalizeCode(_code.Text);
        if (code.Length != 8)
        {
            SetStatus("Enter the 8-digit code provided by your technician.", true);
            return;
        }
        if (!_terms.Checked)
        {
            SetStatus("Please accept the remote-support terms before continuing.", true);
            return;
        }

        ToggleEntry(false);
        SetStatus("Checking support code…");

        try
        {
            var lookup = await PostAsync<LookupResponse>(
                "/api/agent/lookup",
                new LookupRequest { Code = code },
                CancellationToken.None);

            var requestedAccess = new List<string> { "view your screen" };
            if (lookup.RequestedControl)
                requestedAccess.Add("control your keyboard and mouse");
            if (lookup.RequestedClipboard)
                requestedAccess.Add("read and set text in your Windows clipboard");
            if (lookup.RequestedFileTransfer)
                requestedAccess.Add("send and receive files you explicitly approve (up to 25 MB each)");
            requestedAccess.Add("share basic network diagnostics (adapter, LAN IP, gateway, DNS, and public IP)");
            var permissions = string.Join(", ", requestedAccess);
            var elevation = lookup.RequestedElevation
                ? "\n\nThe technician indicated that Administrator access may be needed. Windows will still require you to approve any UAC elevation prompt locally."
                : "";
            var label = string.IsNullOrWhiteSpace(lookup.CustomerLabel) ? "this computer" : lookup.CustomerLabel;

            var answer = MessageBox.Show(
                this,
                $"Technician: {lookup.TechnicianName}\nSupport for: {label}\n\nThe technician is requesting permission to {permissions}.{elevation}\n\nAllow this temporary support session?",
                "Approve Remote Support",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                SetStatus("Support request cancelled.");
                ToggleEntry(true);
                return;
            }

            if (lookup.RequestedElevation && !Program.IsAdministrator())
            {
                var elevate = MessageBox.Show(
                    this,
                    "Administrator access was requested. Restart Remote Assist as Administrator now? You will see a normal Windows UAC prompt and must approve it locally.",
                    "Administrator Access",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button2);

                if (elevate == DialogResult.Yes)
                {
                    RestartElevated(code);
                    return;
                }
            }

            var redeem = await PostAsync<RedeemResponse>(
                "/api/agent/redeem",
                new RedeemRequest
                {
                    Code = code,
                    MachineName = Environment.MachineName,
                    TermsAccepted = true
                },
                CancellationToken.None);

            _requestedControl = lookup.RequestedControl;
            _requestedClipboard = lookup.RequestedClipboard;
            _requestedFileTransfer = lookup.RequestedFileTransfer;
            await ConnectWebSocketAsync(
                redeem.WebSocketUrl,
                redeem.AgentToken,
                redeem.SessionId,
                redeem.LiveExpiresAt);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
            ToggleEntry(true);
        }
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(_options.Server + path, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: cancellationToken);
            throw new InvalidOperationException(err?.Error ?? $"Server returned {(int)response.StatusCode}.");
        }

        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken))
            ?? throw new InvalidOperationException("Server returned an empty response.");
    }

    private async Task ConnectWebSocketAsync(string wsUrl, string agentToken, string sessionId, DateTime liveExpiresAt)
    {
        _sessionId = sessionId;
        _agentToken = agentToken;
        _webSocketUrl = wsUrl;
        _liveExpiresAtUtc = liveExpiresAt.ToUniversalTime();
        _monitorIndex = ScreenCapture.NormalizeScreenIndex(-1);
        _jpegQuality = 55;
        _fps = 6;
        _viewerConnected = 0;
        _sessionCts = new CancellationTokenSource();

        SetStatus("Connecting to technician…");
        await OpenSocketAsync(_sessionCts.Token);
        StartTransferLoops(_sessionCts.Token);

        BeginInvoke((Action)(() =>
        {
            _disconnect.Visible = true;
            _sendFile.Visible = _requestedFileTransfer;
            _chat.Visible = true;
            _disconnect.SetBounds(36, 302, _requestedFileTransfer ? 220 : 455, 45);
            _connect.Visible = false;
            _elevate.Visible = false;
            _code.Enabled = false;
            _terms.Enabled = false;
            SetStatus("Connected — technician access is active.");
            UpdateDetail();
        }));
    }

    private async Task OpenSocketAsync(CancellationToken ct)
    {
        var wsUrl = _webSocketUrl ?? throw new InvalidOperationException("Support session WebSocket URL is missing.");
        var token = _agentToken ?? throw new InvalidOperationException("Support session credential is missing.");

        CloseCurrentSocket();
        Volatile.Write(ref _viewerConnected, 0);

        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.SetRequestHeader("Authorization", "Bearer " + token);

        await ws.ConnectAsync(new Uri(wsUrl), ct);
        Interlocked.Exchange(ref _ws, ws);

        await SendHelloAsync(ct);
        await SendCaptureSettingsAckAsync(ct);
    }

    private void StartTransferLoops(CancellationToken ct)
    {
        _ = Task.Run(() => CaptureLoopAsync(ct), ct);
        _ = Task.Run(() => ReceiveLoopAsync(ct), ct);
    }

    private async Task SendHelloAsync(CancellationToken ct)
    {
        var monitors = ScreenCapture.GetMonitors()
            .Select(m => new
            {
                index = m.Index,
                name = m.DeviceName,
                left = m.Left,
                top = m.Top,
                width = m.Width,
                height = m.Height,
                primary = m.Primary
            })
            .ToArray();

        _h264Capability ??= H264Capability.Probe();
        var network = await GetNetworkSnapshotAsync(ct);

        var hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            machine_name = Environment.MachineName,
            agent_version = AgentBuildInfo.Version,
            agent_build = AgentBuildInfo.InformationalVersion,
            elevated = Program.IsAdministrator(),
            control = _requestedControl,
            clipboard = _requestedClipboard,
            file_transfer = _requestedFileTransfer,
            monitors,
            active_monitor = Volatile.Read(ref _monitorIndex),
            jpeg_quality = Volatile.Read(ref _jpegQuality),
            scale_percent = Volatile.Read(ref _scalePercent),
            fps = Volatile.Read(ref _fps),
            adaptive_fps = Volatile.Read(ref _adaptiveFpsEnabled) == 1,
            capture_mode = Volatile.Read(ref _captureMode),
            h264_hardware_available = _h264Capability.HardwareAvailable,
            h264_hardware_encoders = _h264Capability.HardwareEncoders,
            h264_probe_error = _h264Capability.Error,
            h264_last_error = _h264LastError,
            video_transport = Volatile.Read(ref _videoTransport),
            network,
            live_expires_at = _liveExpiresAtUtc
        });

        await SendTextAsync(hello, ct);
    }

    private async Task<NetworkSnapshot> GetNetworkSnapshotAsync(
        CancellationToken ct)
    {
        if (_networkSnapshot is not null &&
            DateTime.UtcNow - _networkSnapshotAtUtc < TimeSpan.FromMinutes(1))
            return _networkSnapshot;

        var snapshot = await NetworkDiagnostics.CaptureAsync(_http, ct);
        _networkSnapshot = snapshot;
        _networkSnapshotAtUtc = DateTime.UtcNow;
        return snapshot;
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ws = _ws;
                if (ws is null || ws.State != WebSocketState.Open) return;

                if (Volatile.Read(ref _viewerConnected) == 0)
                {
                    await Task.Delay(250, ct);
                    continue;
                }

                var monitorIndex = Volatile.Read(ref _monitorIndex);
                var quality = Volatile.Read(ref _jpegQuality);
                var scalePercent = Volatile.Read(ref _scalePercent);
                var fps = Math.Clamp(Volatile.Read(ref _fps), 1, 12);
                var framePeriodMs = Math.Max(1, 1000 / fps);
                var captureMode = Volatile.Read(ref _captureMode);
                var transport = Volatile.Read(ref _videoTransport);

                if (transport == "h264-annexb" &&
                    Volatile.Read(ref _viewerH264Supported) == 1)
                {
                    var started = Stopwatch.GetTimestamp();
                    var hasFrame = ScreenCapture.TryCaptureBgra(
                        monitorIndex,
                        framePeriodMs,
                        scalePercent,
                        captureMode == "gdi",
                        out var bgraFrame,
                        out var backend);

                    if (!hasFrame || bgraFrame is null)
                    {
                        Volatile.Write(ref _captureBackend, backend + " + H.264");
                        Interlocked.Increment(ref _captureWindowSkipped);
                        await MaybeSendCaptureTelemetryAsync(ct);
                        if (backend == "capture unavailable")
                            await Task.Delay(Math.Min(500, framePeriodMs), ct);
                        continue;
                    }

                    try
                    {
                        var encoder = EnsureH264Encoder(
                            bgraFrame,
                            monitorIndex,
                            scalePercent,
                            fps);

                        if (encoder is null)
                        {
                            _h264LastError ??=
                                $"No Media Foundation H.264 encoder accepted {bgraFrame.Width}x{bgraFrame.Height} at {fps} FPS.";
                            SetVideoTransport("jpeg");
                            await SendCaptureSettingsAckAsync(ct);
                            continue;
                        }

                        var timestampUs = (long)(
                            Stopwatch.GetTimestamp() *
                            (1_000_000.0 / Stopwatch.Frequency));

                        var encoded = encoder.Encode(bgraFrame, timestampUs);
                        var elapsedTicks = Stopwatch.GetTimestamp() - started;

                        Volatile.Write(
                            ref _captureBackend,
                            $"{backend} + H.264 ({encoder.Name})");
                        Interlocked.Add(ref _captureWindowElapsedTicks, elapsedTicks);
                        Interlocked.Increment(ref _captureWindowSamples);

                        if (encoded is null)
                        {
                            Interlocked.Increment(ref _captureWindowSkipped);
                            await MaybeSendCaptureTelemetryAsync(ct);
                            continue;
                        }

                        _h264LastError = null;
                        var packet = BuildH264Packet(encoded);
                        await SendBinaryAsync(packet, ct);
                        Interlocked.Increment(ref _captureWindowSent);
                        Interlocked.Add(ref _captureWindowBytes, packet.Length);
                        await MaybeSendCaptureTelemetryAsync(ct);

                        var elapsedMs =
                            elapsedTicks * 1000.0 / Stopwatch.Frequency;
                        var remainingMs =
                            framePeriodMs - (int)Math.Ceiling(elapsedMs);
                        if (remainingMs > 0)
                            await Task.Delay(remainingMs, ct);

                        continue;
                    }
                    catch (Exception ex)
                    {
                        _h264LastError = DescribeH264Failure(ex);
                        ResetH264Encoder();
                        SetVideoTransport("jpeg");
                        await SendCaptureSettingsAckAsync(ct);
                        continue;
                    }
                }

                var jpegStarted = Stopwatch.GetTimestamp();
                var jpegHasFrame = ScreenCapture.TryCaptureJpeg(
                    monitorIndex,
                    quality,
                    framePeriodMs,
                    scalePercent,
                    captureMode == "gdi",
                    out var frame,
                    out var jpegBackend);
                var jpegElapsedTicks = Stopwatch.GetTimestamp() - jpegStarted;

                Volatile.Write(ref _captureBackend, jpegBackend);

                if (!jpegHasFrame || frame is null)
                {
                    Interlocked.Increment(ref _captureWindowSkipped);
                    await MaybeSendCaptureTelemetryAsync(ct);
                    if (jpegBackend == "capture unavailable")
                        await Task.Delay(Math.Min(500, framePeriodMs), ct);
                    continue;
                }

                Interlocked.Add(
                    ref _captureWindowElapsedTicks,
                    jpegElapsedTicks);
                Interlocked.Increment(ref _captureWindowSamples);

                var previousFrame = _lastSentFrame;
                if (previousFrame is not null &&
                    previousFrame.AsSpan().SequenceEqual(frame))
                {
                    Interlocked.Increment(ref _captureWindowSkipped);
                    await MaybeSendCaptureTelemetryAsync(ct);

                    var duplicateElapsedMs =
                        jpegElapsedTicks * 1000.0 / Stopwatch.Frequency;
                    var duplicateDelayMs =
                        framePeriodMs - (int)Math.Ceiling(duplicateElapsedMs);
                    if (duplicateDelayMs > 0)
                        await Task.Delay(duplicateDelayMs, ct);
                    continue;
                }

                _lastSentFrame = frame;
                await SendBinaryAsync(frame, ct);
                Interlocked.Increment(ref _captureWindowSent);
                Interlocked.Add(ref _captureWindowBytes, frame.Length);
                await MaybeSendCaptureTelemetryAsync(ct);

                var jpegElapsedMs =
                    jpegElapsedTicks * 1000.0 / Stopwatch.Frequency;
                var jpegRemainingMs =
                    framePeriodMs - (int)Math.Ceiling(jpegElapsedMs);
                if (jpegRemainingMs > 0)
                    await Task.Delay(jpegRemainingMs, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            ResetH264Encoder();
            ScreenCapture.ResetAcceleratedCapture();
            if (!ct.IsCancellationRequested && !_explicitEndInProgress)
                _ = ScheduleReconnectAsync(
                    "Screen stream interrupted.",
                    ct);
        }
    }

    private H264MediaFoundationEncoder? EnsureH264Encoder(
        CapturedBgraFrame frame,
        int monitorIndex,
        int scalePercent,
        int fps)
    {
        if (_h264Encoder is not null &&
            _h264Encoder.Width == frame.Width &&
            _h264Encoder.Height == frame.Height &&
            _h264Encoder.Fps == fps &&
            _h264EncoderMonitor == monitorIndex &&
            _h264EncoderScale == scalePercent &&
            _h264EncoderFps == fps)
            return _h264Encoder;

        ResetH264Encoder();

        var bitrate = CalculateH264Bitrate(
            frame.Width,
            frame.Height,
            fps);

        _h264Encoder = H264MediaFoundationEncoder.TryCreate(
            frame.Width,
            frame.Height,
            fps,
            bitrate,
            out var createError);

        if (_h264Encoder is null)
        {
            _h264LastError = createError ??
                $"No Media Foundation H.264 encoder accepted {frame.Width}x{frame.Height} at {fps} FPS.";
            return null;
        }

        _h264LastError = null;

        _h264EncoderMonitor = monitorIndex;
        _h264EncoderScale = scalePercent;
        _h264EncoderFps = fps;
        return _h264Encoder;
    }

    private static int CalculateH264Bitrate(
        int width,
        int height,
        int fps)
    {
        var estimate = (long)Math.Round(
            width * (double)height * fps * 0.20);

        return (int)Math.Clamp(
            estimate,
            500_000L,
            5_000_000L);
    }

    private static string DescribeH264Failure(Exception ex)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message)
            ? ex.GetType().Name
            : $"{ex.GetType().Name}: {ex.Message}";
        return message.Length <= 300 ? message : message[..300];
    }

    private void SetVideoTransport(string transport)
    {
        var normalized = string.Equals(
            transport,
            "h264-annexb",
            StringComparison.OrdinalIgnoreCase)
            ? "h264-annexb"
            : "jpeg";

        var previous = Volatile.Read(ref _videoTransport);
        if (string.Equals(
                previous,
                normalized,
                StringComparison.Ordinal))
            return;

        Volatile.Write(ref _videoTransport, normalized);
        _lastSentFrame = null;
        ResetH264Encoder();
    }

    private void ResetH264Encoder()
    {
        var encoder = Interlocked.Exchange(
            ref _h264Encoder,
            null);

        try { encoder?.Dispose(); } catch { }

        _h264EncoderMonitor = -1;
        _h264EncoderScale = 0;
        _h264EncoderFps = 0;
    }

    private static byte[] BuildH264Packet(
        H264EncodedFrame frame)
    {
        const int headerSize = 16;
        var packet = new byte[
            checked(headerSize + frame.Data.Length)];

        packet[0] = (byte)'I';
        packet[1] = (byte)'A';
        packet[2] = (byte)'H';
        packet[3] = (byte)'1';
        packet[4] = frame.KeyFrame ? (byte)1 : (byte)0;

        BinaryPrimitives.WriteInt64BigEndian(
            packet.AsSpan(8, 8),
            frame.TimestampMicroseconds);

        frame.Data.CopyTo(
            packet.AsSpan(headerSize));

        return packet;
    }

    private async Task MaybeSendCaptureTelemetryAsync(CancellationToken ct)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _captureTelemetryStamp);
        if (previous != 0 && (now - previous) < Stopwatch.Frequency)
            return;
        if (Interlocked.CompareExchange(ref _captureTelemetryStamp, now, previous) != previous)
            return;

        var sent = Interlocked.Exchange(ref _captureWindowSent, 0);
        var skipped = Interlocked.Exchange(ref _captureWindowSkipped, 0);
        var bytes = Interlocked.Exchange(ref _captureWindowBytes, 0);
        var elapsedTicks = Interlocked.Exchange(ref _captureWindowElapsedTicks, 0);
        var samples = Interlocked.Exchange(ref _captureWindowSamples, 0);
        var avgMs = samples > 0
            ? elapsedTicks * 1000.0 / Stopwatch.Frequency / samples
            : 0.0;

        var transport = Volatile.Read(ref _videoTransport);
        var message = JsonSerializer.Serialize(new
        {
            type = "capture_telemetry",
            backend = Volatile.Read(ref _captureBackend),
            transport,
            frames_sent = sent,
            frames_skipped = skipped,
            encoded_bytes = bytes,
            jpeg_bytes = transport == "jpeg" ? bytes : 0,
            average_capture_ms = Math.Round(avgMs, 2)
        });

        await SendTextAsync(message, ct);

        if (!IsDisposed && IsHandleCreated)
            BeginInvoke((Action)UpdateDetail);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ws = _ws;
                if (ws is null || ws.State != WebSocketState.Open) return;

                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                var segment = new ArraySegment<byte>(buffer);

                do
                {
                    result = await ws.ReceiveAsync(segment, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        var reason = result.CloseStatusDescription ?? "";
                        if (result.CloseStatus == WebSocketCloseStatus.NormalClosure &&
                            reason.Contains("session ended", StringComparison.OrdinalIgnoreCase))
                        {
                            SafeServerEnded("Support session ended by the technician or server.");
                        }
                        else if (!ct.IsCancellationRequested && !_explicitEndInProgress)
                        {
                            _ = ScheduleReconnectAsync("Connection interrupted.", ct);
                        }
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;

                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeElement)) continue;

                var type = typeElement.GetString();

                if (type == "input" && _requestedControl && root.TryGetProperty("input", out var input))
                {
                    InputInjector.Apply(input, Volatile.Read(ref _monitorIndex));
                }
                else if (type == "capture_settings")
                {
                    ApplyCaptureSettings(root);
                    await SendCaptureSettingsAckAsync(ct);
                }
                else if (type == "viewer_status" &&
                         root.TryGetProperty("connected", out var connectedElement) &&
                         (connectedElement.ValueKind == JsonValueKind.True || connectedElement.ValueKind == JsonValueKind.False))
                {
                    var connected = connectedElement.GetBoolean();
                    ApplyViewerStatus(connected);
                    if (connected)
                    {
                        await SendHelloAsync(ct);
                        await SendCaptureSettingsAckAsync(ct);
                    }
                }
                else if (type == "viewer_capabilities")
                {
                    var supportsH264 =
                        root.TryGetProperty("h264_webcodecs", out var h264Element) &&
                        h264Element.ValueKind == JsonValueKind.True;
                    Volatile.Write(ref _viewerH264Supported, supportsH264 ? 1 : 0);
                    if (!supportsH264 && Volatile.Read(ref _videoTransport) == "h264-annexb")
                    {
                        SetVideoTransport("jpeg");
                        await SendCaptureSettingsAckAsync(ct);
                    }
                }
                else if (type == "viewer_telemetry")
                {
                    if (ApplyViewerTelemetry(root))
                        await SendCaptureSettingsAckAsync(ct);
                }
                else if (type == "clipboard_set" && _requestedClipboard &&
                         root.TryGetProperty("text", out var clipboardTextElement))
                {
                    var text = clipboardTextElement.GetString() ?? "";
                    if (Encoding.UTF8.GetByteCount(text) <= 262144)
                    {
                        await SetClipboardTextAsync(text);
                        await SendTextAsync(JsonSerializer.Serialize(new
                        {
                            type = "clipboard_status",
                            action = "set",
                            ok = true,
                            length = text.Length
                        }), ct);
                    }
                }
                else if (type == "clipboard_get" && _requestedClipboard)
                {
                    var text = ClampUtf8Text(await GetClipboardTextAsync(), 262144);
                    await SendTextAsync(JsonSerializer.Serialize(new
                    {
                        type = "clipboard_data",
                        text,
                        length = text.Length
                    }), ct);
                }
                else if (type == "file_offer" && _requestedFileTransfer)
                {
                    await HandleIncomingFileOfferAsync(root, ct);
                }
                else if (type == "file_status" && _requestedFileTransfer)
                {
                    var name = root.TryGetProperty("name", out var fileNameElement)
                        ? Path.GetFileName(fileNameElement.GetString() ?? "file")
                        : "file";
                    var status = root.TryGetProperty("status", out var fileStatusElement)
                        ? fileStatusElement.GetString() ?? "updated"
                        : "updated";
                    SetStatus(status switch
                    {
                        "available_to_technician" => $"Technician received the offer for {name}.",
                        "technician_download_started" => $"Technician started downloading {name}.",
                        _ => $"{name}: {status}"
                    });
                }
                else if (type == "chat_history" &&
                         root.TryGetProperty("messages", out var historyElement) &&
                         historyElement.ValueKind == JsonValueKind.Array)
                {
                    await ApplyChatHistoryAsync(historyElement, ct);
                }
                else if (type == "chat_message" &&
                         root.TryGetProperty("message", out var messageElement))
                {
                    var message = ParseChatMessage(messageElement);
                    if (message is not null)
                    {
                        await HydrateChatImageAsync(message, ct);
                        AddChatMessage(message);
                    }
                }
                else if (type == "network_refresh_request")
                {
                    _networkSnapshot = null;
                    _networkSnapshotAtUtc = DateTime.MinValue;
                    await SendHelloAsync(ct);
                }
                else if (type == "elevation_request")
                {
                    await HandleElevationRequestAsync(ct);
                }
                else if (type == "recording_status")
                {
                    var status = root.TryGetProperty("status", out var recordingElement)
                        ? recordingElement.GetString() ?? ""
                        : "";
                    if (string.Equals(status, "started", StringComparison.OrdinalIgnoreCase))
                        SetStatus("Technician is recording this support session.");
                    else if (string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase))
                        SetStatus("Technician stopped session recording.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested && !_explicitEndInProgress)
                _ = ScheduleReconnectAsync("Connection interrupted.", ct);
        }
    }

    private void ShowChatWindow()
    {
        if (_chatForm is null || _chatForm.IsDisposed)
        {
            _chatForm = new ChatForm();
            _chatForm.SendRequested += async (_, body) =>
            {
                try
                {
                    var ct = _sessionCts?.Token ?? CancellationToken.None;
                    await SendTextAsync(JsonSerializer.Serialize(new
                    {
                        type = "chat_message",
                        body
                    }), ct);
                }
                catch (Exception ex)
                {
                    SetStatus("Could not send chat message: " + ex.Message, true);
                }
            };
            _chatForm.ImageSendRequested += async (_, args) =>
            {
                try
                {
                    var ct = _sessionCts?.Token ?? CancellationToken.None;
                    await SendChatImageToTechnicianAsync(
                        args.Path,
                        args.Caption,
                        ct);
                }
                catch (Exception ex)
                {
                    SetStatus("Could not send chat picture: " + ex.Message, true);
                }
            };
        }

        _chatForm.SetMessages(_chatMessages);
        _unreadChat = 0;
        UpdateChatButton();

        if (!_chatForm.Visible)
            _chatForm.Show(this);

        _chatForm.BringToFront();
        _chatForm.Activate();
    }

    private async Task ApplyChatHistoryAsync(
        JsonElement messages,
        CancellationToken ct)
    {
        var items = new List<SupportChatMessage>();
        foreach (var element in messages.EnumerateArray())
        {
            var parsed = ParseChatMessage(element);
            if (parsed is not null)
                items.Add(parsed);
        }

        const int maxHistoryImages = 20;
        const long maxHistoryImageBytes = 50L * 1024 * 1024;
        var hydratedImages = 0;
        long hydratedBytes = 0;

        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (ct.IsCancellationRequested)
                break;

            var item = items[i];
            if (!item.HasImage)
                continue;
            if (hydratedImages >= maxHistoryImages)
                break;
            if (item.AttachmentSize <= 0 ||
                item.AttachmentSize > 10L * 1024 * 1024 ||
                hydratedBytes + item.AttachmentSize > maxHistoryImageBytes)
                continue;

            await HydrateChatImageAsync(item, ct);
            if (item.ImageBytes is { Length: > 0 })
            {
                hydratedImages++;
                hydratedBytes += item.ImageBytes.Length;
            }
        }

        _chatMessages.Clear();
        _chatMessages.AddRange(items.OrderBy(x => x.ID));

        if (_chatForm is not null && !_chatForm.IsDisposed)
            BeginInvoke((Action)(() => _chatForm.SetMessages(_chatMessages)));

        UpdateChatButton();
    }

    private void AddChatMessage(SupportChatMessage message)
    {
        if (message.ID > 0 && _chatMessages.Any(x => x.ID == message.ID))
            return;

        _chatMessages.Add(message);

        var visible = _chatForm is not null &&
                      !_chatForm.IsDisposed &&
                      _chatForm.Visible;

        if (visible)
        {
            BeginInvoke((Action)(() => _chatForm?.AppendMessage(message)));
        }
        else if (string.Equals(
                     message.SenderType,
                     "technician",
                     StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _unreadChat);
        }

        UpdateChatButton();
    }

    private void UpdateChatButton()
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        if (InvokeRequired)
        {
            BeginInvoke((Action)UpdateChatButton);
            return;
        }

        var unread = Volatile.Read(ref _unreadChat);
        _chat.Text = unread > 0
            ? $"Chat with Technician ({unread})"
            : "Chat with Technician";
    }

    private static SupportChatMessage? ParseChatMessage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var id = element.TryGetProperty("id", out var idElement) &&
                 idElement.TryGetInt64(out var parsedId)
            ? parsedId
            : 0L;

        var senderType = element.TryGetProperty("sender_type", out var typeElement)
            ? typeElement.GetString() ?? ""
            : "";

        var senderName = element.TryGetProperty("sender_name", out var nameElement)
            ? nameElement.GetString() ?? ""
            : "";

        var body = element.TryGetProperty("body", out var bodyElement)
            ? bodyElement.GetString() ?? ""
            : "";

        var attachmentTransferId =
            element.TryGetProperty("attachment_transfer_id", out var transferElement)
                ? transferElement.GetString() ?? ""
                : "";
        var attachmentName =
            element.TryGetProperty("attachment_name", out var attachmentNameElement)
                ? attachmentNameElement.GetString() ?? ""
                : "";
        var attachmentMime =
            element.TryGetProperty("attachment_mime", out var attachmentMimeElement)
                ? attachmentMimeElement.GetString() ?? ""
                : "";
        var attachmentSize =
            element.TryGetProperty("attachment_size", out var attachmentSizeElement) &&
            attachmentSizeElement.TryGetInt64(out var parsedAttachmentSize)
                ? parsedAttachmentSize
                : 0L;

        var createdAt = DateTime.UtcNow;
        if (element.TryGetProperty("created_at", out var createdElement) &&
            createdElement.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(
                createdElement.GetString(),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsedCreated))
        {
            createdAt = parsedCreated;
        }

        if (body.Length == 0 && attachmentTransferId.Length == 0)
            return null;

        return new SupportChatMessage
        {
            ID = id,
            SenderType = senderType,
            SenderName = senderName,
            Body = body,
            CreatedAt = createdAt,
            AttachmentTransferID = attachmentTransferId,
            AttachmentName = attachmentName,
            AttachmentMime = attachmentMime,
            AttachmentSize = attachmentSize
        };
    }

    private async Task SendChatImageToTechnicianAsync(
        string path,
        string caption,
        CancellationToken ct)
    {
        if (!_requestedFileTransfer)
            throw new InvalidOperationException(
                "Picture chat requires file-transfer permission.");

        var sessionId = _sessionId ??
            throw new InvalidOperationException("Session is unavailable.");
        var token = _agentToken ??
            throw new InvalidOperationException("Session credential is unavailable.");

        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Picture file does not exist.", path);
        if (info.Length > 10L * 1024 * 1024)
            throw new InvalidOperationException("Chat pictures are limited to 10 MB.");

        var ext = info.Extension.ToLowerInvariant();
        var mime = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            _ => throw new InvalidOperationException(
                "Chat pictures must be JPEG, PNG, or GIF.")
        };

        SetStatus($"Sending picture {info.Name}…");

        using var file = File.OpenRead(info.FullName);
        using var content = new MultipartFormDataContent();
        using var stream = new StreamContent(file);
        stream.Headers.ContentType = new MediaTypeHeaderValue(mime);
        content.Add(stream, "file", info.Name);
        content.Add(new StringContent("chat_image"), "purpose");
        if (!string.IsNullOrWhiteSpace(caption))
            content.Add(new StringContent(caption.Trim()), "caption");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            _options.Server + "/api/agent/files?session=" +
            Uri.EscapeDataString(sessionId));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Content = content;

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadFromJsonAsync<ErrorResponse>(
                cancellationToken: ct);
            throw new InvalidOperationException(
                err?.Error ?? $"Picture upload failed ({(int)response.StatusCode}).");
        }

        SetStatus($"Sent picture {info.Name} to technician.");
    }

    private async Task HydrateChatImageAsync(
        SupportChatMessage message,
        CancellationToken ct)
    {
        if (!message.HasImage ||
            message.AttachmentSize <= 0 ||
            message.AttachmentSize > 10L * 1024 * 1024)
            return;

        var sessionId = _sessionId;
        var token = _agentToken;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(token))
            return;

        try
        {
            var url =
                _options.Server + "/api/agent/files/" +
                Uri.EscapeDataString(message.AttachmentTransferID) +
                "?session=" + Uri.EscapeDataString(sessionId);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (!response.IsSuccessStatusCode)
                return;

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 10L * 1024 * 1024)
                return;

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream(
                contentLength is > 0 and <= int.MaxValue
                    ? (int)contentLength.Value
                    : 0);

            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct);
                if (read <= 0)
                    break;

                total += read;
                if (total > 10L * 1024 * 1024)
                    return;

                output.Write(buffer, 0, read);
            }

            message.ImageBytes = output.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The transcript remains useful even when temporary media expired.
        }
    }

    private async Task ResumeElevatedSessionAsync(string resumeFile)
    {
        try
        {
            var handoff = ElevationHandoff.ReadAndDelete(resumeFile);
            if (handoff.LiveExpiresAtUtc <= DateTime.UtcNow)
                throw new InvalidOperationException("The support session has expired.");

            _requestedControl = handoff.RequestedControl;
            _requestedClipboard = handoff.RequestedClipboard;
            _requestedFileTransfer = handoff.RequestedFileTransfer;

            ToggleEntry(false);
            SetStatus("Resuming elevated support session…");

            await ConnectWebSocketAsync(
                handoff.WebSocketUrl,
                handoff.AgentToken,
                handoff.SessionId,
                handoff.LiveExpiresAtUtc);

            var ct = _sessionCts?.Token ?? CancellationToken.None;
            await SendElevationStatusAsync("elevated", ct);
            SetStatus("Connected as Administrator — technician access is active.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not resume elevated support session: " + ex.Message, true);
            ToggleEntry(true);
        }
    }

    private async Task HandleElevationRequestAsync(CancellationToken ct)
    {
        if (Program.IsAdministrator())
        {
            await SendElevationStatusAsync("elevated", ct);
            return;
        }

        await SendElevationStatusAsync("requested", ct);

        var approved = await PromptElevationApprovalAsync();
        if (!approved)
        {
            await SendElevationStatusAsync("declined", ct);
            SetStatus("Administrator elevation request declined.");
            return;
        }

        string? handoffPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_sessionId) ||
                string.IsNullOrWhiteSpace(_agentToken) ||
                string.IsNullOrWhiteSpace(_webSocketUrl))
                throw new InvalidOperationException("Live session information is unavailable.");

            handoffPath = ElevationHandoff.Write(new ElevationHandoff(
                _options.Server,
                _sessionId,
                _agentToken,
                _webSocketUrl,
                _liveExpiresAtUtc,
                _requestedControl,
                _requestedClipboard,
                _requestedFileTransfer));

            await SendElevationStatusAsync("restarting", ct);

            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var args =
                $"--server \"{_options.Server}\" --resume-file \"{handoffPath}\"";

            Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                Verb = "runas"
            });

            _restartingForElevation = true;
            BeginInvoke((Action)Close);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (handoffPath is not null)
            {
                try { File.Delete(handoffPath); } catch { }
            }

            await SendElevationStatusAsync("uac_cancelled", ct);
            SetStatus("Windows UAC elevation was cancelled.", true);
        }
        catch (Exception ex)
        {
            if (handoffPath is not null)
            {
                try { File.Delete(handoffPath); } catch { }
            }

            await SendElevationStatusAsync("failed", ct);
            SetStatus("Could not elevate support session: " + ex.Message, true);
        }
    }

    private Task<bool> PromptElevationApprovalAsync()
    {
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        BeginInvoke((Action)(() =>
        {
            var answer = MessageBox.Show(
                this,
                "Your technician is requesting Administrator access for this active support session.\n\nIf you approve, Windows will show its normal UAC prompt. You must approve that Windows prompt locally.\n\nAllow the technician to request Administrator elevation?",
                "Administrator Access Requested",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            tcs.TrySetResult(answer == DialogResult.Yes);
        }));

        return tcs.Task;
    }

    private Task SendElevationStatusAsync(
        string status,
        CancellationToken ct) =>
        SendTextAsync(
            JsonSerializer.Serialize(new
            {
                type = "elevation_status",
                status,
                elevated = Program.IsAdministrator()
            }),
            ct);

    private static string ClampUtf8Text(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            if (Encoding.UTF8.GetByteCount(text.AsSpan(0, mid)) <= maxBytes)
                low = mid;
            else
                high = mid - 1;
        }

        if (low > 0 && low < text.Length &&
            char.IsHighSurrogate(text[low - 1]) && char.IsLowSurrogate(text[low]))
            low--;

        return text[..low];
    }

    private async Task SendFileToTechnicianAsync()
    {
        if (!_requestedFileTransfer || string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_agentToken))
            return;

        using var dialog = new OpenFileDialog
        {
            Title = "Choose a file to send to your technician",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var info = new FileInfo(dialog.FileName);
        if (info.Length > 25L * 1024 * 1024)
        {
            SetStatus("That file exceeds the 25 MB transfer limit.", true);
            return;
        }

        _sendFile.Enabled = false;
        SetStatus($"Uploading {info.Name} to technician…");

        try
        {
            using var file = File.OpenRead(info.FullName);
            using var content = new MultipartFormDataContent();
            using var stream = new StreamContent(file);
            stream.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(stream, "file", info.Name);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                _options.Server + "/api/agent/files?session=" + Uri.EscapeDataString(_sessionId));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _agentToken);
            request.Content = content;

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                throw new InvalidOperationException(err?.Error ?? $"Upload failed ({(int)response.StatusCode}).");
            }

            var responseText = await response.Content.ReadAsStringAsync();
            var viewerNotified = false;
            if (!string.IsNullOrWhiteSpace(responseText))
            {
                try
                {
                    using var responseJson = JsonDocument.Parse(responseText);
                    viewerNotified =
                        responseJson.RootElement.TryGetProperty("viewer_notified", out var notifiedElement) &&
                        notifiedElement.ValueKind == JsonValueKind.True;
                }
                catch { }
            }

            SetStatus(viewerNotified
                ? $"Sent {info.Name} to technician."
                : $"Uploaded {info.Name}. It will appear when the technician viewer is connected.");
        }
        catch (Exception ex)
        {
            SetStatus("File transfer failed: " + ex.Message, true);
        }
        finally
        {
            _sendFile.Enabled = true;
        }
    }

    private async Task HandleIncomingFileOfferAsync(JsonElement root, CancellationToken ct)
    {
        var purpose = root.TryGetProperty("purpose", out var purposeElement)
            ? purposeElement.GetString() ?? ""
            : "";
        if (string.Equals(purpose, "chat_image", StringComparison.OrdinalIgnoreCase))
            return;

        if (!root.TryGetProperty("transfer_id", out var idElement)) return;
        var transferId = idElement.GetString() ?? "";
        if (transferId == "") return;

        var name = root.TryGetProperty("name", out var nameElement)
            ? Path.GetFileName(nameElement.GetString() ?? "support-file.bin")
            : "support-file.bin";
        var size = root.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
            ? parsedSize
            : 0L;

        var savePath = await PromptForIncomingFileAsync(name, size);
        if (savePath is null)
        {
            await SendFileStatusAsync(transferId, name, "declined", ct);
            return;
        }

        try
        {
            SetStatus($"Downloading {name}…");

            var sessionId = _sessionId ?? throw new InvalidOperationException("Session is unavailable.");
            var token = _agentToken ?? throw new InvalidOperationException("Session credential is unavailable.");
            var url = _options.Server + "/api/agent/files/" + Uri.EscapeDataString(transferId) +
                      "?session=" + Uri.EscapeDataString(sessionId);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var tempPath = savePath + ".remote-assist-part-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                {
                    await input.CopyToAsync(output, ct);
                }

                File.Move(tempPath, savePath, true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }

            await SendFileStatusAsync(transferId, name, "saved", ct);
            SetStatus($"Saved {name}.");
        }
        catch (Exception ex)
        {
            try { await SendFileStatusAsync(transferId, name, "failed", ct); } catch { }
            SetStatus("File download failed: " + ex.Message, true);
        }
    }

    private Task<string?> PromptForIncomingFileAsync(string name, long size)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        BeginInvoke((Action)(() =>
        {
            var sizeText = size >= 1024 * 1024
                ? $"{size / (1024d * 1024d):0.00} MB"
                : $"{Math.Max(0, size) / 1024d:0.0} KB";

            var answer = MessageBox.Show(
                this,
                $"Your technician wants to send this file:\n\n{name}\n{sizeText}\n\nChoose Yes to select where to save it.",
                "Incoming Support File",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                tcs.SetResult(null);
                return;
            }

            using var save = new SaveFileDialog
            {
                Title = "Save support file",
                FileName = name,
                OverwritePrompt = true
            };
            tcs.SetResult(save.ShowDialog(this) == DialogResult.OK ? save.FileName : null);
        }));

        return tcs.Task;
    }

    private Task SendFileStatusAsync(string transferId, string name, string status, CancellationToken ct)
    {
        return SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "file_status",
            transfer_id = transferId,
            name,
            status
        }), ct);
    }

    private Task<string> GetClipboardTextAsync()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated)
        {
            tcs.SetResult("");
            return tcs.Task;
        }

        BeginInvoke((Action)(() =>
        {
            try
            {
                var text = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : "";
                tcs.SetResult(text);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }));

        return tcs.Task;
    }

    private Task SetClipboardTextAsync(string text)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated)
        {
            tcs.SetResult(true);
            return tcs.Task;
        }

        BeginInvoke((Action)(() =>
        {
            try
            {
                Clipboard.SetDataObject(text, true, 5, 100);
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }));

        return tcs.Task;
    }

    private void ApplyViewerStatus(bool connected)
    {
        Volatile.Write(ref _viewerConnected, connected ? 1 : 0);
        _lastSentFrame = null;

        if (!connected)
        {
            Volatile.Write(ref _viewerH264Supported, 0);
            SetVideoTransport("jpeg");
        }

        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke((Action)(() =>
        {
            if (_sessionCts is null) return;
            SetStatus(
                connected
                    ? "Connected — technician viewer is active."
                    : "Connected — waiting for technician viewer.");
            UpdateDetail();
        }));
    }

    private void ApplyCaptureSettings(JsonElement root)
    {
        if (root.TryGetProperty("monitor", out var monitorElement) && monitorElement.TryGetInt32(out var monitor))
        {
            var normalized = ScreenCapture.NormalizeScreenIndex(monitor);
            var previous = Volatile.Read(ref _monitorIndex);
            Volatile.Write(ref _monitorIndex, normalized);
            if (normalized != previous)
            {
                _lastSentFrame = null;
                ScreenCapture.ResetAcceleratedCapture();
            }
        }

        if (root.TryGetProperty("jpeg_quality", out var qualityElement) && qualityElement.TryGetInt32(out var quality))
        {
            var nextQuality = Math.Clamp(quality, 25, 85);
            if (nextQuality != Volatile.Read(ref _jpegQuality))
                _lastSentFrame = null;
            Volatile.Write(ref _jpegQuality, nextQuality);
        }

        if (root.TryGetProperty("scale_percent", out var scaleElement) && scaleElement.TryGetInt32(out var scalePercent))
        {
            var nextScale = scalePercent switch
            {
                <= 50 => 50,
                <= 75 => 75,
                _ => 100
            };
            if (nextScale != Volatile.Read(ref _scalePercent))
                _lastSentFrame = null;
            Volatile.Write(ref _scalePercent, nextScale);
        }

        var adaptive = root.TryGetProperty("adaptive_fps", out var adaptiveElement) &&
                       adaptiveElement.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("fps", out var fpsElement) && fpsElement.TryGetInt32(out var fps))
        {
            if (adaptive || fps == 0)
            {
                Volatile.Write(ref _adaptiveFpsEnabled, 1);
                var current = Volatile.Read(ref _fps);
                if (current < 2 || current > 12)
                    Volatile.Write(ref _fps, 8);
                Interlocked.Exchange(ref _adaptiveFpsLastAdjust, 0);
            }
            else
            {
                Volatile.Write(ref _adaptiveFpsEnabled, 0);
                Volatile.Write(ref _fps, Math.Clamp(fps, 1, 12));
            }
        }

        if (root.TryGetProperty("capture_mode", out var modeElement))
        {
            var nextMode = string.Equals(modeElement.GetString(), "gdi", StringComparison.OrdinalIgnoreCase)
                ? "gdi"
                : "auto";
            if (!string.Equals(nextMode, Volatile.Read(ref _captureMode), StringComparison.Ordinal))
            {
                Volatile.Write(ref _captureMode, nextMode);
                _lastSentFrame = null;
                ScreenCapture.ResetAcceleratedCapture();
            }
        }

        if (root.TryGetProperty("video_transport", out var transportElement))
        {
            var requested = string.Equals(
                transportElement.GetString(),
                "h264-annexb",
                StringComparison.OrdinalIgnoreCase)
                ? "h264-annexb"
                : "jpeg";

            if (requested == "h264-annexb" &&
                Volatile.Read(ref _viewerH264Supported) != 1)
                requested = "jpeg";

            SetVideoTransport(requested);
        }

        if (!IsDisposed && IsHandleCreated)
            BeginInvoke((Action)UpdateDetail);
    }

    private bool ApplyViewerTelemetry(JsonElement root)
    {
        if (Volatile.Read(ref _adaptiveFpsEnabled) != 1)
            return false;

        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _adaptiveFpsLastAdjust);
        if (last != 0 && now - last < Stopwatch.Frequency * 2)
            return false;

        var dropped = root.TryGetProperty("dropped", out var droppedElement) &&
                      droppedElement.TryGetInt32(out var droppedValue)
            ? Math.Max(0, droppedValue)
            : 0;
        var renderedFps = root.TryGetProperty("rendered_fps", out var renderedElement) &&
                          renderedElement.TryGetDouble(out var renderedValue)
            ? Math.Max(0, renderedValue)
            : 0;
        var averageDecodeMs = root.TryGetProperty("average_decode_ms", out var decodeElement) &&
                              decodeElement.TryGetDouble(out var decodeValue)
            ? Math.Max(0, decodeValue)
            : 0;

        var current = Math.Clamp(Volatile.Read(ref _fps), 2, 12);
        var next = current;
        var budgetMs = 1000.0 / current;

        if (dropped >= 2 || averageDecodeMs > budgetMs * 0.80)
        {
            next = Math.Max(2, current - 2);
        }
        else if (dropped == 0 &&
                 renderedFps >= current * 0.80 &&
                 averageDecodeMs > 0 &&
                 averageDecodeMs < budgetMs * 0.45)
        {
            next = Math.Min(12, current + 1);
        }

        if (next == current)
            return false;

        Volatile.Write(ref _fps, next);
        Interlocked.Exchange(ref _adaptiveFpsLastAdjust, now);
        return true;
    }

    private async Task SendCaptureSettingsAckAsync(CancellationToken ct)
    {
        var message = JsonSerializer.Serialize(new
        {
            type = "capture_settings",
            active_monitor = Volatile.Read(ref _monitorIndex),
            jpeg_quality = Volatile.Read(ref _jpegQuality),
            scale_percent = Volatile.Read(ref _scalePercent),
            fps = Volatile.Read(ref _fps),
            adaptive_fps = Volatile.Read(ref _adaptiveFpsEnabled) == 1,
            capture_mode = Volatile.Read(ref _captureMode),
            video_transport = Volatile.Read(ref _videoTransport),
            h264_encoder = _h264Encoder?.Name,
            h264_error = _h264LastError
        });
        await SendTextAsync(message, ct);
    }

    private async Task SendTextAsync(string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var ws = _ws;
            if (ws is null || ws.State != WebSocketState.Open)
                throw new WebSocketException("WebSocket is not open.");

            await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendBinaryAsync(byte[] data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var ws = _ws;
            if (ws is null || ws.State != WebSocketState.Open)
                throw new WebSocketException("WebSocket is not open.");

            await ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ScheduleReconnectAsync(string reason, CancellationToken sessionToken)
    {
        if (sessionToken.IsCancellationRequested || _explicitEndInProgress) return;
        if (Interlocked.CompareExchange(ref _reconnectGate, 1, 0) != 0) return;

        try
        {
            CloseCurrentSocket();

            for (var attempt = 1; attempt <= 5 && !sessionToken.IsCancellationRequested; attempt++)
            {
                if (_liveExpiresAtUtc != default && DateTime.UtcNow >= _liveExpiresAtUtc)
                {
                    SafeServerEnded("Support session expired.");
                    return;
                }

                SetStatus($"{reason} Reconnecting ({attempt}/5)…", true);

                var delaySeconds = attempt switch
                {
                    1 => 1,
                    2 => 2,
                    3 => 4,
                    4 => 8,
                    _ => 10
                };

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), sessionToken);
                    await OpenSocketAsync(sessionToken);
                    StartTransferLoops(sessionToken);
                    SetStatus("Connected — technician access is active.");
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    CloseCurrentSocket();
                }
            }

            SafeServerEnded("Connection lost. The support session could not be reconnected.", true);
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectGate, 0);
        }
    }

    private async Task EndSessionFromCustomerAsync()
    {
        if (_explicitEndInProgress) return;
        _explicitEndInProgress = true;
        _disconnect.Enabled = false;
        SetStatus("Ending support session…");

        var revoked = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            revoked = await NotifyServerEndAsync(cts.Token);
        }
        catch { }

        StopLocalSession();

        if (!IsDisposed)
        {
            _disconnect.Enabled = true;
            SetStatus(
                revoked ? "Support session ended." : "Disconnected locally — server revocation could not be confirmed.",
                !revoked);
        }

        _explicitEndInProgress = false;
    }

    private async Task<bool> NotifyServerEndAsync(CancellationToken ct)
    {
        var sessionId = _sessionId;
        var token = _agentToken;

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(token))
            return true;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Server + "/api/agent/end");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new EndSessionRequest { SessionId = sessionId });

        using var response = await _http.SendAsync(request, ct);
        return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Conflict;
    }

    private void TryNotifyEndSync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_agentToken)) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            _ = NotifyServerEndAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch { }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        _closing = true;

        if (!_explicitEndInProgress && !_restartingForElevation)
            TryNotifyEndSync();

        StopLocalSession(updateUi: false);
    }

    private void StopLocalSession(bool updateUi = true)
    {
        var cts = Interlocked.Exchange(ref _sessionCts, null);
        cts?.Cancel();
        cts?.Dispose();

        CloseCurrentSocket();

        _sessionId = null;
        _agentToken = null;
        _webSocketUrl = null;
        _liveExpiresAtUtc = default;
        _networkSnapshot = null;
        _networkSnapshotAtUtc = default;
        _lastSentFrame = null;
        SetVideoTransport("jpeg");
        Volatile.Write(ref _viewerH264Supported, 0);
        ScreenCapture.ResetAcceleratedCapture();
        Volatile.Write(ref _scalePercent, 100);
        Volatile.Write(ref _adaptiveFpsEnabled, 0);
        Interlocked.Exchange(ref _adaptiveFpsLastAdjust, 0);
        Volatile.Write(ref _captureMode, "auto");
        Volatile.Write(ref _captureBackend, "initializing");
        Interlocked.Exchange(ref _captureTelemetryStamp, 0);
        Interlocked.Exchange(ref _captureWindowSent, 0);
        Interlocked.Exchange(ref _captureWindowSkipped, 0);
        Interlocked.Exchange(ref _captureWindowBytes, 0);
        Interlocked.Exchange(ref _captureWindowElapsedTicks, 0);
        Interlocked.Exchange(ref _captureWindowSamples, 0);
        Volatile.Write(ref _viewerConnected, 0);
        Interlocked.Exchange(ref _reconnectGate, 0);

        if (updateUi && !IsDisposed && IsHandleCreated)
        {
            BeginInvoke((Action)(() =>
            {
                _disconnect.Visible = false;
                _sendFile.Visible = false;
                _chat.Visible = false;
                _unreadChat = 0;
                _chatMessages.Clear();
                if (_chatForm is not null && !_chatForm.IsDisposed)
                    _chatForm.Hide();
                UpdateChatButton();
                _disconnect.SetBounds(36, 302, 455, 45);
                _connect.Visible = true;
                _elevate.Visible = true;
                _code.Enabled = true;
                _terms.Enabled = true;
                ToggleEntry(true);
                _detail.Text = $"Server: {_options.Server}";
            }));
        }
    }

    private void CloseCurrentSocket()
    {
        var ws = Interlocked.Exchange(ref _ws, null);
        if (ws is null) return;

        try { ws.Abort(); } catch { }
        try { ws.Dispose(); } catch { }
    }

    private void SafeServerEnded(string message, bool error = false)
    {
        if (IsDisposed || !IsHandleCreated) return;

        BeginInvoke((Action)(() =>
        {
            StopLocalSession();
            SetStatus(message, error);
        }));
    }

    private void UpdateDetail()
    {
        var expires = _liveExpiresAtUtc == default
            ? ""
            : $" · Session expires {_liveExpiresAtUtc.ToLocalTime():g}";
        var viewer = Volatile.Read(ref _viewerConnected) == 1 ? "Viewer attached" : "Waiting for viewer";
        var backend = Volatile.Read(ref _captureBackend);
        var transport = Volatile.Read(ref _videoTransport) == "h264-annexb" ? "H.264" : "JPEG";
        var monitorIndex = Volatile.Read(ref _monitorIndex);
        var monitorLabel = monitorIndex == ScreenCapture.AllMonitorsIndex
            ? "All monitors"
            : $"Monitor {monitorIndex + 1}";
        _detail.Text =
            $"Server: {_options.Server} · {backend} · {transport} · {monitorLabel} · {Volatile.Read(ref _scalePercent)}% · {Volatile.Read(ref _fps)} FPS · {viewer}{expires}";
    }

    private void ToggleEntry(bool enabled)
    {
        _code.Enabled = enabled;
        _terms.Enabled = enabled;
        _connect.Enabled = enabled;
        _elevate.Enabled = enabled && !Program.IsAdministrator();
    }

    private void SetStatus(string message, bool error = false)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => SetStatus(message, error)));
            return;
        }

        _status.Text = message;
        _status.ForeColor = error ? Color.FromArgb(183, 55, 55) : Color.FromArgb(43, 111, 78);
    }

    private void RestartElevated(string? code = null)
    {
        if (Program.IsAdministrator()) return;

        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var effectiveCode = NormalizeCode(code ?? _code.Text);
            var args = $"--server \"{_options.Server}\"" + (effectiveCode.Length == 8 ? $" --code {effectiveCode}" : "");
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            SetStatus("Administrator restart was cancelled.", true);
            ToggleEntry(true);
        }
        catch (Exception ex)
        {
            SetStatus("Could not restart as Administrator: " + ex.Message, true);
            ToggleEntry(true);
        }
    }

    private static string NormalizeCode(string value) => new(value.Where(char.IsDigit).Take(8).ToArray());
}
