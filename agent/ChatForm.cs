namespace RemoteAssist.Agent;

internal sealed class SupportChatMessage
{
    public long ID { get; init; }
    public string SenderType { get; init; } = "";
    public string SenderName { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public string AttachmentTransferID { get; init; } = "";
    public string AttachmentName { get; init; } = "";
    public string AttachmentMime { get; init; } = "";
    public long AttachmentSize { get; init; }
    public byte[]? ImageBytes { get; set; }

    public bool HasImage =>
        AttachmentTransferID.Length > 0 &&
        AttachmentMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

internal sealed class ChatImageSendEventArgs : EventArgs
{
    public string Path { get; }
    public string Caption { get; }

    public ChatImageSendEventArgs(string path, string caption)
    {
        Path = path;
        Caption = caption;
    }
}

internal sealed class ChatForm : Form
{
    private readonly FlowLayoutPanel _messages = new();
    private readonly TextBox _input = new();
    private readonly Button _picture = new();
    private readonly Button _send = new();

    public event EventHandler<string>? SendRequested;
    public event EventHandler<ChatImageSendEventArgs>? ImageSendRequested;

    public ChatForm()
    {
        Text = "Remote Assist Chat";
        Width = 560;
        Height = 650;
        MinimumSize = new Size(440, 460);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10F);

        _messages.Dock = DockStyle.Fill;
        _messages.FlowDirection = FlowDirection.TopDown;
        _messages.WrapContents = false;
        _messages.AutoScroll = true;
        _messages.Padding = new Padding(10);
        _messages.BackColor = Color.FromArgb(247, 249, 252);

        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 130,
            Padding = new Padding(10)
        };

        _input.Multiline = true;
        _input.AcceptsReturn = true;
        _input.ScrollBars = ScrollBars.Vertical;
        _input.SetBounds(10, 10, 380, 95);
        _input.Anchor = AnchorStyles.Left | AnchorStyles.Top |
                        AnchorStyles.Right | AnchorStyles.Bottom;
        _input.KeyDown += InputKeyDown;

        _picture.Text = "Picture";
        _picture.SetBounds(400, 10, 70, 42);
        _picture.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _picture.Click += (_, _) => ChoosePicture();

        _send.Text = "Send";
        _send.SetBounds(400, 62, 70, 43);
        _send.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _send.Click += (_, _) => Submit();

        bottom.Controls.AddRange([_input, _picture, _send]);
        Controls.Add(_messages);
        Controls.Add(bottom);

        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing)
                return;

            e.Cancel = true;
            Hide();
        };
    }

    public void SetMessages(IEnumerable<SupportChatMessage> messages)
    {
        ClearMessageControls();

        foreach (var message in messages)
            _messages.Controls.Add(BuildMessageControl(message));

        ScrollToBottom();
    }

    public void AppendMessage(SupportChatMessage message)
    {
        _messages.Controls.Add(BuildMessageControl(message));
        ScrollToBottom();
    }

    private Control BuildMessageControl(SupportChatMessage message)
    {
        var own = string.Equals(
            message.SenderType,
            "customer",
            StringComparison.OrdinalIgnoreCase);

        var card = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = Math.Max(340, _messages.ClientSize.Width - 35),
            Padding = new Padding(10),
            Margin = new Padding(
                own ? 45 : 3,
                4,
                own ? 3 : 45,
                4),
            BackColor = own
                ? Color.FromArgb(233, 247, 241)
                : Color.FromArgb(234, 242, 255)
        };

        var who = string.IsNullOrWhiteSpace(message.SenderName)
            ? message.SenderType
            : message.SenderName;

        var meta = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(card.Width - 20, 0),
            Text = $"{who} · {message.CreatedAt.ToLocalTime():g}",
            ForeColor = Color.FromArgb(100, 115, 136),
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            Location = new Point(10, 8)
        };
        card.Controls.Add(meta);

        var body = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(card.Width - 20, 0),
            Text = message.Body,
            ForeColor = Color.FromArgb(25, 45, 70),
            Location = new Point(10, meta.Bottom + 6)
        };
        card.Controls.Add(body);

        var bottom = body.Bottom + 8;

        if (message.HasImage)
        {
            if (message.ImageBytes is { Length: > 0 })
            {
                try
                {
                    using var stream = new MemoryStream(message.ImageBytes);
                    using var source = Image.FromStream(stream);
                    var image = new Bitmap(source);

                    var box = new PictureBox
                    {
                        Image = image,
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Width = Math.Min(390, Math.Max(260, card.Width - 20)),
                        Height = 230,
                        Location = new Point(10, bottom),
                        Cursor = Cursors.Hand
                    };
                    var fileName = message.AttachmentName;
                    box.Click += (_, _) =>
                    {
                        using var preview = new Form
                        {
                            Text = string.IsNullOrWhiteSpace(fileName)
                                ? "Support picture"
                                : fileName,
                            Width = 900,
                            Height = 700,
                            StartPosition = FormStartPosition.CenterParent
                        };
                        var large = new PictureBox
                        {
                            Dock = DockStyle.Fill,
                            Image = new Bitmap(image),
                            SizeMode = PictureBoxSizeMode.Zoom,
                            BackColor = Color.Black
                        };
                        preview.Controls.Add(large);
                        preview.ShowDialog(this);
                        large.Image?.Dispose();
                    };
                    card.Controls.Add(box);
                    bottom = box.Bottom + 8;
                }
                catch
                {
                    AddUnavailableLabel(card, message, bottom);
                }
            }
            else
            {
                AddUnavailableLabel(card, message, bottom);
            }
        }

        card.Height = bottom + 8;
        return card;
    }

    private static void AddUnavailableLabel(
        Control card,
        SupportChatMessage message,
        int top)
    {
        var unavailable = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(card.Width - 20, 0),
            Text = string.IsNullOrWhiteSpace(message.AttachmentName)
                ? "Picture is no longer available."
                : $"Picture unavailable or expired: {message.AttachmentName}",
            ForeColor = Color.FromArgb(100, 115, 136),
            Font = new Font("Segoe UI", 8F, FontStyle.Italic),
            Location = new Point(10, top)
        };
        card.Controls.Add(unavailable);
    }

    private void ChoosePicture()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a picture to send to your technician",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.gif)|*.jpg;*.jpeg;*.png;*.gif",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var info = new FileInfo(dialog.FileName);
        if (info.Length > 10L * 1024 * 1024)
        {
            MessageBox.Show(
                this,
                "Chat pictures are limited to 10 MB.",
                "Remote Assist Chat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var caption = _input.Text.Trim();
        ImageSendRequested?.Invoke(
            this,
            new ChatImageSendEventArgs(dialog.FileName, caption));
        _input.Clear();
    }

    private void InputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || e.Shift)
            return;

        e.SuppressKeyPress = true;
        Submit();
    }

    private void Submit()
    {
        var body = _input.Text.Trim();
        if (body.Length == 0)
            return;

        if (body.Length > 4000)
        {
            MessageBox.Show(
                this,
                "Chat messages are limited to 4,000 characters.",
                "Remote Assist Chat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        SendRequested?.Invoke(this, body);
        _input.Clear();
    }

    private void ScrollToBottom()
    {
        if (_messages.Controls.Count == 0)
            return;

        _messages.ScrollControlIntoView(
            _messages.Controls[_messages.Controls.Count - 1]);
    }

    private void ClearMessageControls()
    {
        foreach (Control control in _messages.Controls)
        {
            foreach (Control child in control.Controls)
            {
                if (child is PictureBox picture)
                    picture.Image?.Dispose();
            }
            control.Dispose();
        }
        _messages.Controls.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            ClearMessageControls();

        base.Dispose(disposing);
    }
}
