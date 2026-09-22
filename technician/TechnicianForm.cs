using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RemoteAssist.Technician;

internal sealed class TechnicianForm : Form
{
    private readonly string _server;
    private readonly string _startUrl;
    private readonly WebView2 _web = new();

    private readonly TableLayoutPanel _root = new();
    private readonly TableLayoutPanel _main = new();
    private readonly Panel _nav = new();
    private readonly Panel _header = new();
    private readonly ToolStrip _viewerTools = new();
    private readonly StatusStrip _status = new();

    private readonly Label _pageTitle = new();
    private readonly Label _pageSubtitle = new();
    private readonly Label _userLabel = new();

    private readonly Button _sessionsNav;
    private readonly Button _historyNav;
    private readonly Button _teamNav;
    private readonly Button _auditNav;
    private readonly Button _accountNav;
    private readonly Button _logoutNav;
    private readonly Button _newSessionButton;
    private readonly Button _refreshButton;
    private readonly Button _backButton;
    private readonly Button _endSessionButton;

    private readonly ToolStripStatusLabel _statusText = new();
    private readonly ToolStripStatusLabel _telemetryText = new();
    private readonly ToolStripComboBox _monitor = new();
    private readonly ToolStripComboBox _capture = new();
    private readonly ToolStripComboBox _video = new();
    private readonly ToolStripComboBox _resolution = new();
    private readonly ToolStripComboBox _quality = new();
    private readonly ToolStripComboBox _fps = new();

    private readonly System.Windows.Forms.Timer _syncTimer = new();

    private bool _authenticated;
    private bool _viewerActive;
    private string _role = "";
    private string _activeSection = "sessions";
    private FormBorderStyle _savedBorderStyle;
    private FormWindowState _savedWindowState;
    private bool _nativeFullscreen;

    private static readonly Color Navy = Color.FromArgb(18, 36, 61);
    private static readonly Color Navy2 = Color.FromArgb(27, 61, 99);
    private static readonly Color Blue = Color.FromArgb(36, 99, 235);
    private static readonly Color Paper = Color.FromArgb(244, 247, 251);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private static readonly Color Border = Color.FromArgb(220, 229, 239);

    public TechnicianForm(string server, string? startUrl = null)
    {
        _server = server.TrimEnd('/');
        _startUrl = string.IsNullOrWhiteSpace(startUrl)
            ? _server + "/?native=1"
            : startUrl;

        Text = "Remote Assist Technician";
        Width = 1560;
        Height = 980;
        MinimumSize = new Size(1040, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Paper;
        Font = new Font("Segoe UI", 9F);
        KeyPreview = true;

        _sessionsNav = CreateNavButton("Live sessions");
        _historyNav = CreateNavButton("History");
        _teamNav = CreateNavButton("Team");
        _auditNav = CreateNavButton("Audit");
        _accountNav = CreateNavButton("My account");
        _logoutNav = CreateNavButton("Sign out");

        _newSessionButton = CreateHeaderButton("+  New session", primary: true);
        _refreshButton = CreateHeaderButton("Refresh");
        _backButton = CreateHeaderButton("Back to sessions");
        _endSessionButton = CreateHeaderButton("End session", danger: true);

        BuildShell();
        BuildViewerToolbar();
        WireNativeActions();
        SetAuthenticated(false, "", "");

        _syncTimer.Interval = 2000;
        _syncTimer.Tick += async (_, _) =>
        {
            if (_viewerActive)
                await SyncViewerToolbarAsync();
        };

        Shown += async (_, _) => await InitializeBrowserAsync();
        KeyDown += HandleShortcut;
    }

    private void BuildShell()
    {
        _root.Dock = DockStyle.Fill;
        _root.Margin = Padding.Empty;
        _root.Padding = Padding.Empty;
        _root.ColumnCount = 2;
        _root.RowCount = 1;
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(_root);

        BuildNavigation();

        _main.Dock = DockStyle.Fill;
        _main.Margin = Padding.Empty;
        _main.Padding = Padding.Empty;
        _main.ColumnCount = 1;
        _main.RowCount = 4;
        _main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _main.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        _main.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        BuildHeader();
        BuildStatusBar();

        _web.Dock = DockStyle.Fill;
        _web.Margin = Padding.Empty;
        _web.BackColor = Paper;

        _main.Controls.Add(_header, 0, 0);
        _main.Controls.Add(_viewerTools, 0, 1);
        _main.Controls.Add(_web, 0, 2);
        _main.Controls.Add(_status, 0, 3);

        _root.Controls.Add(_nav, 0, 0);
        _root.Controls.Add(_main, 1, 0);
    }

    private void BuildNavigation()
    {
        _nav.Dock = DockStyle.Fill;
        _nav.BackColor = Navy;
        _nav.Margin = Padding.Empty;

        var brand = new Panel
        {
            Dock = DockStyle.Top,
            Height = 112,
            BackColor = Navy
        };
        var brandName = new Label
        {
            Text = "remote-assist",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 21F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(22, 25)
        };
        var brandSub = new Label
        {
            Text = "SUPPORT TECHNICIAN",
            ForeColor = Color.FromArgb(143, 183, 255),
            Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 66)
        };
        brand.Controls.Add(brandName);
        brand.Controls.Add(brandSub);

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(10, 8, 10, 0),
            BackColor = Navy
        };
        foreach (var button in new[] { _sessionsNav, _historyNav, _teamNav, _auditNav })
            stack.Controls.Add(button);

        var accountPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 150,
            Padding = new Padding(10, 8, 10, 10),
            BackColor = Navy
        };
        _userLabel.SetBounds(16, 6, 168, 42);
        _userLabel.ForeColor = Color.FromArgb(196, 211, 230);
        _userLabel.Font = new Font("Segoe UI", 8.5F);
        _userLabel.AutoEllipsis = true;
        _accountNav.SetBounds(0, 52, 185, 38);
        _logoutNav.SetBounds(0, 94, 185, 38);
        accountPanel.Controls.Add(_userLabel);
        accountPanel.Controls.Add(_accountNav);
        accountPanel.Controls.Add(_logoutNav);

        _nav.Controls.Add(stack);
        _nav.Controls.Add(accountPanel);
        _nav.Controls.Add(brand);
    }

    private void BuildHeader()
    {
        _header.Dock = DockStyle.Fill;
        _header.BackColor = Color.White;
        _header.Margin = Padding.Empty;
        _header.Padding = new Padding(20, 8, 14, 8);
        _header.Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(
                pen,
                0,
                _header.Height - 1,
                _header.Width,
                _header.Height - 1);
        };

        _pageTitle.Text = "Remote Assist";
        _pageTitle.ForeColor = Navy;
        _pageTitle.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
        _pageTitle.AutoSize = true;
        _pageTitle.Location = new Point(20, 12);

        _pageSubtitle.Text = "Sign in with your support account";
        _pageSubtitle.ForeColor = Muted;
        _pageSubtitle.Font = new Font("Segoe UI", 8.5F);
        _pageSubtitle.AutoSize = true;
        _pageSubtitle.Location = new Point(22, 43);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 520,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 9, 4, 0),
            BackColor = Color.White
        };

        actions.Controls.Add(_endSessionButton);
        actions.Controls.Add(_newSessionButton);
        actions.Controls.Add(_refreshButton);
        actions.Controls.Add(_backButton);

        _header.Controls.Add(actions);
        _header.Controls.Add(_pageTitle);
        _header.Controls.Add(_pageSubtitle);

        _backButton.Visible = false;
        _endSessionButton.Visible = false;
    }

    private void BuildStatusBar()
    {
        _status.Dock = DockStyle.Fill;
        _status.SizingGrip = false;
        _status.BackColor = Color.White;
        _status.RenderMode = ToolStripRenderMode.System;

        _statusText.Text = "Starting technician application…";
        _statusText.ForeColor = Muted;

        _telemetryText.Spring = true;
        _telemetryText.TextAlign = ContentAlignment.MiddleRight;
        _telemetryText.ForeColor = Muted;

        _status.Items.Add(_statusText);
        _status.Items.Add(_telemetryText);
    }

    private Button CreateNavButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Width = 180,
            Height = 44,
            Margin = new Padding(0, 2, 0, 2),
            FlatStyle = FlatStyle.Flat,
            BackColor = Navy,
            ForeColor = Color.FromArgb(204, 216, 231),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0),
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Enabled = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Navy2;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(33, 74, 120);
        return button;
    }

    private Button CreateHeaderButton(
        string text,
        bool primary = false,
        bool danger = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 38,
            Margin = new Padding(6, 0, 0, 0),
            Padding = new Padding(12, 0, 12, 0),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        button.FlatAppearance.BorderSize = 0;

        if (danger)
        {
            button.BackColor = Color.FromArgb(253, 236, 236);
            button.ForeColor = Color.FromArgb(183, 55, 55);
        }
        else if (primary)
        {
            button.BackColor = Blue;
            button.ForeColor = Color.White;
        }
        else
        {
            button.BackColor = Color.FromArgb(234, 240, 247);
            button.ForeColor = Navy;
        }

        return button;
    }

    private void BuildViewerToolbar()
    {
        _viewerTools.GripStyle = ToolStripGripStyle.Hidden;
        _viewerTools.Dock = DockStyle.Fill;
        _viewerTools.BackColor = Color.White;
        _viewerTools.ForeColor = Navy;
        _viewerTools.Padding = new Padding(10, 7, 10, 6);
        _viewerTools.RenderMode = ToolStripRenderMode.System;
        _viewerTools.Visible = false;

        AddToolLabel("Monitor");
        ConfigureMonitorSelector();

        AddToolLabel("Capture");
        ConfigureSelector(
            _capture,
            "captureModeSelect",
            [new("Auto", "auto"), new("GDI", "gdi")],
            "auto",
            72);

        AddToolLabel("Video");
        ConfigureSelector(
            _video,
            "videoTransportSelect",
            [new("JPEG", "jpeg"), new("H.264", "h264-annexb")],
            "jpeg",
            70);

        AddToolLabel("Size");
        ConfigureSelector(
            _resolution,
            "scaleSelect",
            [new("100%", "100"), new("75%", "75"), new("50%", "50")],
            "100",
            62);

        AddToolLabel("Quality");
        ConfigureSelector(
            _quality,
            "qualitySelect",
            [
                new("Low", "35"),
                new("Balanced", "55"),
                new("High", "70"),
                new("Very high", "85")
            ],
            "55",
            84);

        AddToolLabel("FPS");
        ConfigureSelector(
            _fps,
            "fpsSelect",
            [
                new("Adaptive", "0"),
                new("2", "2"),
                new("4", "4"),
                new("6", "6"),
                new("8", "8"),
                new("10", "10"),
                new("12", "12")
            ],
            "6",
            74);

        _viewerTools.Items.Add(new ToolStripSeparator());

        AddToolButton("Fit", async (_, _) => await ClickWebButtonAsync("fitViewButton"));
        AddToolButton("1:1", async (_, _) => await ClickWebButtonAsync("actualViewButton"));
        AddToolButton("Screenshot", async (_, _) => await ClickWebButtonAsync("screenshotViewerButton"));
        AddToolButton("Record", async (_, _) => await ClickWebButtonAsync("recordViewerButton"));
        AddToolButton("Tools", async (_, _) => await ClickWebButtonAsync("toggleSidebarButton"));
        AddToolButton("Chat", async (_, _) =>
        {
            await EnsureViewerSidebarVisibleAsync();
            await FocusWebElementAsync("chatBody");
        });
        AddToolButton("Files", async (_, _) =>
        {
            await EnsureViewerSidebarVisibleAsync();
            await ScrollWebElementAsync("fileTransferCard");
        });
        AddToolButton("Clipboard", async (_, _) =>
        {
            await EnsureViewerSidebarVisibleAsync();
            await ScrollWebElementAsync("clipboardCard");
        });
        AddToolButton("Network", async (_, _) =>
        {
            await EnsureViewerSidebarVisibleAsync();
            await ScrollWebElementAsync("networkCard");
        });
        AddToolButton("Refresh net", async (_, _) => await ClickWebButtonAsync("refreshNetworkButton"));
        AddToolButton("Elevate", async (_, _) => await ClickWebButtonAsync("requestElevationButton"));

        _viewerTools.Items.Add(new ToolStripSeparator());

        AddToolButton("Fullscreen", (_, _) => ToggleNativeFullscreen());

        var topMost = new ToolStripButton("Always on top")
        {
            CheckOnClick = true,
            DisplayStyle = ToolStripItemDisplayStyle.Text
        };
        topMost.CheckedChanged += (_, _) => TopMost = topMost.Checked;
        _viewerTools.Items.Add(topMost);
    }

    private sealed record SelectorChoice(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    private sealed record BrowserSelectOption(
        string Label,
        string Value,
        bool Selected);

    private void AddToolLabel(string text)
    {
        _viewerTools.Items.Add(new ToolStripLabel(text)
        {
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8F, FontStyle.Bold)
        });
    }

    private void AddToolButton(string text, EventHandler onClick)
    {
        var button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = true
        };
        button.Click += onClick;
        _viewerTools.Items.Add(button);
    }

    private void ConfigureMonitorSelector()
    {
        _monitor.DropDownStyle = ComboBoxStyle.DropDownList;
        _monitor.AutoSize = false;
        _monitor.Width = 132;
        _monitor.Items.Add(new SelectorChoice("Monitor 1", "0"));
        _monitor.SelectedIndex = 0;
        _monitor.ComboBox.DropDown += async (_, _) => await SyncMonitorSelectorAsync();
        _monitor.ComboBox.SelectionChangeCommitted += async (_, _) =>
        {
            if (_monitor.SelectedItem is SelectorChoice selected)
                await SetWebSelectValueAsync("monitorSelect", selected.Value);
        };
        _viewerTools.Items.Add(_monitor);
    }

    private void ConfigureSelector(
        ToolStripComboBox combo,
        string webElementId,
        SelectorChoice[] choices,
        string defaultValue,
        int width)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.AutoSize = false;
        combo.Width = width;

        foreach (var choice in choices)
            combo.Items.Add(choice);

        combo.SelectedItem = choices.First(x => x.Value == defaultValue);
        combo.ComboBox.SelectionChangeCommitted += async (_, _) =>
        {
            if (combo.SelectedItem is SelectorChoice selected)
                await SetWebSelectValueAsync(webElementId, selected.Value);
        };

        _viewerTools.Items.Add(combo);
    }

    private void WireNativeActions()
    {
        _sessionsNav.Click += async (_, _) => await NavigateSectionAsync("sessions");
        _historyNav.Click += async (_, _) => await NavigateSectionAsync("history");
        _teamNav.Click += async (_, _) => await NavigateSectionAsync("team");
        _auditNav.Click += async (_, _) => await NavigateSectionAsync("audit");

        _accountNav.Click += async (_, _) => await ClickWebButtonAsync("accountButton");
        _logoutNav.Click += async (_, _) => await ClickWebButtonAsync("logoutButton");

        _newSessionButton.Click += async (_, _) => await ClickWebButtonAsync("newSessionButton");
        _refreshButton.Click += async (_, _) =>
        {
            await ExecuteScriptAsync(
                "(()=>{if(window.state?.session)return false;" +
                "if(typeof switchTab==='function')switchTab(state.activeTab||'sessions');" +
                "return true;})();");
        };
        _backButton.Click += async (_, _) => await ClickWebButtonAsync("backButton");
        _endSessionButton.Click += async (_, _) => await ClickWebButtonAsync("endSessionButton");
    }

    private void HandleShortcut(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11)
        {
            e.SuppressKeyPress = true;
            ToggleNativeFullscreen();
            return;
        }

        if (e.Control && e.KeyCode == Keys.N && _authenticated && !_viewerActive)
        {
            e.SuppressKeyPress = true;
            _ = ClickWebButtonAsync("newSessionButton");
            return;
        }

        if (e.Control && e.Shift && e.KeyCode == Keys.S && _viewerActive)
        {
            e.SuppressKeyPress = true;
            _ = ClickWebButtonAsync("screenshotViewerButton");
        }
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var environment = await BrowserRuntime.GetAsync();
            await _web.EnsureCoreWebView2Async(environment);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            core.NavigationStarting += NavigationStarting;
            core.NavigationCompleted += async (_, e) =>
            {
                _statusText.Text = e.IsSuccess
                    ? "Connected to Remote Assist"
                    : $"Navigation error: {e.WebErrorStatus}";

                if (e.IsSuccess)
                {
                    await Task.Delay(150);
                    await SyncViewerToolbarAsync();
                }
            };
            core.SourceChanged += async (_, _) =>
            {
                await Task.Delay(100);
                if (_viewerActive)
                    await SyncViewerToolbarAsync();
            };
            core.DocumentTitleChanged += (_, _) =>
            {
                Text = "Remote Assist Technician";
            };
            core.WebMessageReceived += WebMessageReceived;
            core.NewWindowRequested += NewWindowRequested;
            core.DownloadStarting += DownloadStarting;

            Navigate(_startUrl);
            _syncTimer.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                this,
                "Microsoft Edge WebView2 Runtime is required for Remote Assist Technician. Install the WebView2 Evergreen Runtime and start the application again.",
                "WebView2 Runtime Required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "Could not start the technician application:\n\n" + ex.Message,
                "Remote Assist Technician",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement.Clone();
            if (InvokeRequired)
                BeginInvoke((Action)(() => ApplyNativeMessage(root)));
            else
                ApplyNativeMessage(root);
        }
        catch
        {
            // Ignore malformed/unknown web messages. The normal web UI remains authoritative.
        }
    }

    private void ApplyNativeMessage(JsonElement root)
    {
        var type = GetString(root, "type");

        switch (type)
        {
            case "auth":
                SetAuthenticated(
                    GetBool(root, "logged_in"),
                    GetString(root, "display_name"),
                    GetString(root, "role"));
                break;

            case "route":
                if (!_viewerActive)
                    SetSection(GetString(root, "tab"));
                break;

            case "viewer_state":
            {
                var active = GetBool(root, "active");
                SetViewerActive(
                    active,
                    GetString(root, "label"),
                    GetString(root, "machine"),
                    GetString(root, "status"));

                var telemetry = GetString(root, "telemetry");
                var capture = GetString(root, "capture");
                var warning = GetString(root, "warning");

                if (!string.IsNullOrWhiteSpace(telemetry))
                    _telemetryText.Text = telemetry;
                if (!string.IsNullOrWhiteSpace(warning))
                    _statusText.Text = "H.264 fallback: " + warning;
                else if (active && !string.IsNullOrWhiteSpace(capture))
                    _statusText.Text = capture;

                if (active)
                    _ = SyncViewerToolbarAsync();
                break;
            }
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private void SetAuthenticated(bool loggedIn, string displayName, string role)
    {
        _authenticated = loggedIn;
        _role = role ?? "";

        foreach (var button in new[] { _sessionsNav, _historyNav, _accountNav, _logoutNav })
            button.Enabled = loggedIn;

        _teamNav.Visible = loggedIn && string.Equals(_role, "admin", StringComparison.OrdinalIgnoreCase);
        _auditNav.Visible = _teamNav.Visible;
        _teamNav.Enabled = _teamNav.Visible;
        _auditNav.Enabled = _auditNav.Visible;

        _newSessionButton.Enabled = loggedIn;

        if (loggedIn)
        {
            _userLabel.Text =
                string.IsNullOrWhiteSpace(displayName)
                    ? role
                    : $"{displayName}\n{role}";
            _pageTitle.Text = "Live sessions";
            _pageSubtitle.Text = "Attended remote assistance";
            SetSection("sessions");
        }
        else
        {
            _userLabel.Text = "Not signed in";
            _pageTitle.Text = "Technician sign in";
            _pageSubtitle.Text = "Use your Remote Assist account";
            _viewerTools.Visible = false;
            _main.RowStyles[1].Height = 0;
            _newSessionButton.Visible = false;
            _refreshButton.Visible = false;
            _backButton.Visible = false;
            _endSessionButton.Visible = false;
            _telemetryText.Text = "";
            SetNavHighlight(null);
        }
    }

    private void SetSection(string section)
    {
        if (string.IsNullOrWhiteSpace(section))
            section = "sessions";

        _activeSection = section;
        _viewerActive = false;
        _viewerTools.Visible = false;
        _main.RowStyles[1].Height = 0;
        _backButton.Visible = false;
        _endSessionButton.Visible = false;
        _newSessionButton.Visible = _authenticated;
        _refreshButton.Visible = _authenticated;
        _telemetryText.Text = "";

        (_pageTitle.Text, _pageSubtitle.Text) = section switch
        {
            "history" => ("Session history", "Search, review and export completed support work"),
            "team" => ("Support team", "Technician accounts and access control"),
            "audit" => ("Administrative audit", "Authentication and administrative activity"),
            _ => ("Live sessions", "Waiting, approved and connected customer sessions")
        };

        SetNavHighlight(section);
    }

    private void SetViewerActive(
        bool active,
        string label,
        string machine,
        string status)
    {
        _viewerActive = active;

        if (!active)
        {
            SetSection(_activeSection);
            return;
        }

        _main.RowStyles[1].Height = 54;
        _viewerTools.Visible = true;
        _newSessionButton.Visible = false;
        _refreshButton.Visible = false;
        _backButton.Visible = true;
        _endSessionButton.Visible =
            status is "waiting" or "approved" or "connected";

        _pageTitle.Text = string.IsNullOrWhiteSpace(label) ? "Remote session" : label;
        _pageSubtitle.Text =
            string.IsNullOrWhiteSpace(machine)
                ? status
                : $"{machine}  ·  {status}";

        SetNavHighlight(null);
    }

    private void SetNavHighlight(string? section)
    {
        foreach (var pair in new[]
        {
            (_sessionsNav, "sessions"),
            (_historyNav, "history"),
            (_teamNav, "team"),
            (_auditNav, "audit")
        })
        {
            var active = string.Equals(pair.Item2, section, StringComparison.OrdinalIgnoreCase);
            pair.Item1.BackColor = active ? Navy2 : Navy;
            pair.Item1.ForeColor = active ? Color.White : Color.FromArgb(204, 216, 231);
            pair.Item1.Font = new Font(
                "Segoe UI",
                9.5F,
                active ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    private async Task NavigateSectionAsync(string section)
    {
        if (!_authenticated)
            return;

        if (_viewerActive)
        {
            await ClickWebButtonAsync("backButton");
            await Task.Delay(100);
        }

        await ExecuteScriptAsync(
            $"typeof switchTab==='function' && switchTab({JsonString(section)});");
        SetSection(section);
    }

    private async Task SetWebSelectValueAsync(string id, string value)
    {
        await ExecuteScriptAsync(
            $"(()=>{{const e=document.getElementById({JsonString(id)});if(!e)return false;e.value={JsonString(value)};e.dispatchEvent(new Event('change',{{bubbles:true}}));return true;}})();");
        await Task.Delay(75);
        await SyncViewerToolbarAsync();
    }

    private async Task SyncViewerToolbarAsync()
    {
        if (!_viewerActive || _web.CoreWebView2 is null)
            return;

        await SyncMonitorSelectorAsync();
        await SyncSelectorAsync(_capture, "captureModeSelect");
        await SyncSelectorAsync(_video, "videoTransportSelect");
        await SyncSelectorAsync(_resolution, "scaleSelect");
        await SyncSelectorAsync(_quality, "qualitySelect");
        await SyncSelectorAsync(_fps, "fpsSelect");
    }

    private async Task SyncMonitorSelectorAsync()
    {
        if (_web.CoreWebView2 is null)
            return;

        try
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(
                "(()=>{const e=document.getElementById('monitorSelect');" +
                "if(!e)return '';" +
                "return JSON.stringify(Array.from(e.options).map(o=>({" +
                "Label:o.textContent||o.text||o.value," +
                "Value:o.value," +
                "Selected:o.selected" +
                "})));})();");

            var json = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var options = JsonSerializer.Deserialize<BrowserSelectOption[]>(json);
            if (options is null || options.Length == 0)
                return;

            var current =
                options.FirstOrDefault(x => x.Selected)?.Value
                ?? options[0].Value;

            _monitor.ComboBox.BeginUpdate();
            try
            {
                _monitor.Items.Clear();
                foreach (var option in options)
                    _monitor.Items.Add(new SelectorChoice(option.Label, option.Value));

                foreach (var item in _monitor.Items)
                {
                    if (item is SelectorChoice choice &&
                        string.Equals(choice.Value, current, StringComparison.OrdinalIgnoreCase))
                    {
                        _monitor.SelectedItem = choice;
                        break;
                    }
                }

                if (_monitor.SelectedIndex < 0 && _monitor.Items.Count > 0)
                    _monitor.SelectedIndex = 0;
            }
            finally
            {
                _monitor.ComboBox.EndUpdate();
            }
        }
        catch
        {
            // Login and operations pages do not expose monitor controls.
        }
    }

    private async Task SyncSelectorAsync(
        ToolStripComboBox combo,
        string webElementId)
    {
        if (_web.CoreWebView2 is null)
            return;

        try
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(
                $"document.getElementById({JsonString(webElementId)})?.value ?? '';");
            var value = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(value))
                return;

            foreach (var item in combo.Items)
            {
                if (item is SelectorChoice choice &&
                    string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = choice;
                    break;
                }
            }
        }
        catch
        {
            // Not currently on a live viewer.
        }
    }

    private void Navigate(string uri)
    {
        if (_web.CoreWebView2 is null)
            return;

        _web.CoreWebView2.Navigate(uri);
    }

    private void NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAllowedUri(e.Uri))
            return;

        e.Cancel = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            _statusText.Text = "Blocked external navigation";
        }
    }

    private bool IsAllowedUri(string value)
    {
        if (string.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !Uri.TryCreate(_server, UriKind.Absolute, out var server))
            return false;

        return string.Equals(uri.Scheme, server.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, server.Host, StringComparison.OrdinalIgnoreCase) &&
               uri.Port == server.Port;
    }

    private void NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (!IsAllowedUri(e.Uri))
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri)
                {
                    UseShellExecute = true
                });
            }
            catch { }
            return;
        }

        BeginInvoke((Action)(() =>
        {
            var childUri = AddNativeParameter(e.Uri);
            var child = new TechnicianForm(_server, childUri)
            {
                StartPosition = FormStartPosition.CenterParent
            };
            child.Show(this);
        }));
    }

    private void DownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        var suggested = Path.GetFileName(e.ResultFilePath);
        using var dialog = new SaveFileDialog
        {
            FileName = string.IsNullOrWhiteSpace(suggested)
                ? "Remote Assist-Download"
                : suggested,
            OverwritePrompt = true,
            Title = "Save Remote Assist support file"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            e.ResultFilePath = dialog.FileName;
            e.Handled = true;
            _statusText.Text = "Downloading " + Path.GetFileName(dialog.FileName);
        }
        else
        {
            e.Cancel = true;
            e.Handled = true;
        }
    }

    private async Task EnsureViewerSidebarVisibleAsync()
    {
        await ExecuteScriptAsync(
            "(()=>{const v=document.getElementById('viewerView');" +
            "if(!v||!v.classList.contains('sidebar-hidden'))return true;" +
            "document.getElementById('toggleSidebarButton')?.click();" +
            "return true;})();");
        await Task.Delay(50);
    }

    private Task ClickWebButtonAsync(string id) =>
        ExecuteScriptAsync(
            $"document.getElementById({JsonString(id)})?.click();");

    private Task FocusWebElementAsync(string id) =>
        ExecuteScriptAsync(
            $"(()=>{{const e=document.getElementById({JsonString(id)});if(e){{e.scrollIntoView({{behavior:'smooth',block:'center'}});e.focus();}}}})();");

    private Task ScrollWebElementAsync(string id) =>
        ExecuteScriptAsync(
            $"document.getElementById({JsonString(id)})?.scrollIntoView({{behavior:'smooth',block:'center'}});");

    private async Task ExecuteScriptAsync(string script)
    {
        if (_web.CoreWebView2 is null)
            return;

        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            _statusText.Text = "Action failed: " + ex.Message;
        }
    }

    private static string JsonString(string value) =>
        JsonSerializer.Serialize(value);

    private static string AddNativeParameter(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return uri;

        var builder = new UriBuilder(parsed);
        var query = builder.Query.TrimStart('?');
        if (!query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Any(x => x.StartsWith("native=", StringComparison.OrdinalIgnoreCase)))
        {
            builder.Query = string.IsNullOrEmpty(query)
                ? "native=1"
                : query + "&native=1";
        }

        return builder.Uri.ToString();
    }

    private void ToggleNativeFullscreen()
    {
        if (!_nativeFullscreen)
        {
            _savedBorderStyle = FormBorderStyle;
            _savedWindowState = WindowState;

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            _nav.Visible = false;
            _root.ColumnStyles[0].Width = 0;
            _header.Visible = false;
            _status.Visible = false;
            _main.RowStyles[0].Height = 0;
            _main.RowStyles[3].Height = 0;
            _nativeFullscreen = true;
        }
        else
        {
            _root.ColumnStyles[0].Width = 205;
            _nav.Visible = true;
            _header.Visible = true;
            _status.Visible = true;
            _main.RowStyles[0].Height = 72;
            _main.RowStyles[3].Height = 28;
            FormBorderStyle = _savedBorderStyle;
            WindowState = _savedWindowState;
            _nativeFullscreen = false;
        }
    }
}
