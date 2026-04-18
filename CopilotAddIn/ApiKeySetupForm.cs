using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CopilotAddIn
{
    /// <summary>
    /// API key setup / reconfiguration form.
    /// Manages layout and verification of API credentials.
    /// </summary>
    public class ApiKeySetupForm : Form
    {
        public string ApiKey { get; private set; }
        public string SelectedProvider { get; private set; } = "anthropic";

        private Panel anthropicCard;
        private Panel openRouterCard;
        private TextBox keyInput;
        private Button eyeButton;
        private Label errorLabel;
        private Label validatingLabel;
        private LinkLabel getKeyLink;
        private Button okButton;
        private Button cancelButton;
        private bool keyVisible = false;

        private readonly string _existingKey;
        private readonly string _existingProvider;

        private readonly Color Blue = Color.FromArgb(24, 95, 165);
        private readonly Color BlueBg = Color.FromArgb(230, 241, 251);
        private readonly Color BlueText = Color.FromArgb(12, 68, 124);
        private readonly Color TextPri = Color.FromArgb(25, 25, 25);
        private readonly Color TextSec = Color.FromArgb(100, 100, 100);
        private readonly Color TextTer = Color.FromArgb(155, 155, 155);
        private readonly Color Border = Color.FromArgb(218, 218, 213);
        private readonly Color Surface = Color.FromArgb(248, 248, 245);

        public ApiKeySetupForm(string existingKey = "", string existingProvider = "anthropic")
        {
            _existingKey = existingKey ?? string.Empty;
            _existingProvider = existingProvider ?? "anthropic";
            Build();
            SelectProvider(_existingProvider);

            if (!string.IsNullOrEmpty(_existingKey))
            {
                keyInput.Text = _existingKey;
                okButton.Enabled = true;
                UpdateOkButtonStyle();
            }
        }

        private void Build()
        {
            Text = "AI Copilot — API Setup";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 430);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            // ── Header ────────────────────────────────────────────────────────
            var iconBox = new Panel { Location = new Point(24, 20), Size = new Size(44, 44), BackColor = BlueBg };
            iconBox.Paint += PaintLockIcon;
            iconBox.Region = RoundedRegion(new Size(44, 44), 10);
            Controls.Add(iconBox);

            bool isReconfigure = !string.IsNullOrEmpty(_existingKey);
            Lbl(80, 20, 420, 26, isReconfigure ? "Update API Key" : "AI Copilot Setup", new Font("Segoe UI", 12f, FontStyle.Bold), TextPri);

            // Subtitle - Expanded height to 28
            Lbl(80, 48, 450, 28,
                isReconfigure ? "Change your API key or switch provider" : "One-time configuration — takes 30 seconds",
                new Font("Segoe UI", 8.5f), TextSec);

            // Divider 1 - Moved to 82
            Divider(82);

            // ── Provider Section ──────────────────────────────────────────────
            // Title - Moved to 98
            Lbl(24, 98, 200, 18, "API Provider", new Font("Segoe UI", 9f, FontStyle.Bold), TextPri);

            // Subtext - Moved to 118
            Lbl(24, 118, 450, 16, "Choose where your AI requests are sent", new Font("Segoe UI", 8f), TextSec);

            // Cards - Moved to 142 (Height is 68, so they end at 210)
            anthropicCard = ProviderCard(new Point(24, 142), "Anthropic", "claude-sonnet-4", true);
            openRouterCard = ProviderCard(new Point(292, 142), "OpenRouter", "Any model via API", false);

            anthropicCard.Click += (s, e) => SelectProvider("anthropic");
            openRouterCard.Click += (s, e) => SelectProvider("openrouter");
            foreach (Control c in anthropicCard.Controls) c.Click += (s, e) => SelectProvider("anthropic");
            foreach (Control c in openRouterCard.Controls) c.Click += (s, e) => SelectProvider("openrouter");

            // Divider 2 - Moved to 230 to clear the cards
            Divider(230);

            // ── API Key Section ───────────────────────────────────────────────
            // Title - Moved to 245
            Lbl(24, 245, 200, 18, "API Key", new Font("Segoe UI", 9f, FontStyle.Bold), TextPri);

            getKeyLink = new LinkLabel
            {
                Text = "Get a key →",
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(376, 245),
                Size = new Size(160, 18),
                TextAlign = ContentAlignment.MiddleRight
            };
            getKeyLink.LinkColor = getKeyLink.ActiveLinkColor = getKeyLink.VisitedLinkColor = Blue;
            getKeyLink.LinkClicked += OnGetKeyLink;
            Controls.Add(getKeyLink);

            // Input - Moved to 270 (Ends at 308)
            var inputContainer = new Panel { Location = new Point(24, 270), Size = new Size(512, 38), BackColor = Surface };
            inputContainer.Paint += (s, e) => {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = new Pen(Border, 1f))
                    DrawRoundRect(e.Graphics, p, new Rectangle(0, 0, inputContainer.Width - 1, inputContainer.Height - 1), 8);
            };

            keyInput = new TextBox
            {
                Location = new Point(10, 7),
                Size = new Size(452, 24),
                Font = new Font("Consolas", 9.5f),
                BackColor = Surface,
                ForeColor = TextPri,
                BorderStyle = BorderStyle.None,
                UseSystemPasswordChar = true
            };
            keyInput.TextChanged += (s, e) => {
                errorLabel.Visible = false;
                validatingLabel.Visible = false;
                okButton.Enabled = keyInput.Text.Trim().Length > 10;
                UpdateOkButtonStyle();
            };

            eyeButton = new Button { Location = new Point(464, 7), Size = new Size(40, 24), FlatStyle = FlatStyle.Flat, BackColor = Surface, Cursor = Cursors.Hand, TabStop = false };
            eyeButton.FlatAppearance.BorderSize = 0;
            eyeButton.Paint += PaintEyeIcon;
            eyeButton.Click += (s, e) => {
                keyVisible = !keyVisible;
                keyInput.UseSystemPasswordChar = !keyVisible;
                eyeButton.Invalidate();
                keyInput.Focus();
            };

            inputContainer.Controls.Add(keyInput);
            inputContainer.Controls.Add(eyeButton);
            Controls.Add(inputContainer);

            // Privacy Text - Moved to 312
            Lbl(24, 312, 512, 16, "Stored locally in %APPDATA%\\SolidWorksCopilot\\config.json — never transmitted", new Font("Segoe UI", 7.5f), TextTer);

            // ── Status/Error Row ──────────────────────────────────────────────
            // Moved to 338
            errorLabel = new Label
            {
                ForeColor = Color.FromArgb(180, 30, 30),
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(24, 338),
                Size = new Size(512, 20),
                Visible = false
            };
            Controls.Add(errorLabel);

            validatingLabel = new Label
            {
                Text = "Verifying key with provider…",
                ForeColor = TextSec,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                Location = new Point(24, 338),
                Size = new Size(512, 20),
                Visible = false
            };
            Controls.Add(validatingLabel);

            // ── Button Strip ──────────────────────────────────────────────────
            var strip = new Panel { Location = new Point(0, 378), Size = new Size(560, 52), BackColor = Surface };
            strip.Paint += (s, e) => {
                using (var p = new Pen(Border, 1f)) e.Graphics.DrawLine(p, 0, 0, strip.Width, 0);
            };

            cancelButton = new Button
            {
                Text = "Skip for now",
                Size = new Size(130, 34),
                Location = new Point(238, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = TextSec,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand,
                DialogResult = DialogResult.Cancel
            };
            cancelButton.FlatAppearance.BorderColor = Border;

            okButton = new Button
            {
                Text = "Save and verify",
                Size = new Size(156, 34),
                Location = new Point(380, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = Blue,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Enabled = false
            };
            okButton.FlatAppearance.BorderSize = 0;
            okButton.Click += OnOkClicked;

            strip.Controls.AddRange(new Control[] { cancelButton, okButton });
            Controls.Add(strip);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private void SelectProvider(string p)
        {
            SelectedProvider = p;
            bool anth = p == "anthropic";
            SetCardSelected(anthropicCard, anth);
            SetCardSelected(openRouterCard, !anth);
            getKeyLink.Text = anth ? "Get Anthropic key →" : "Get OpenRouter key →";
            errorLabel.Visible = false;
            validatingLabel.Visible = false;
        }

        private void SetCardSelected(Panel card, bool selected)
        {
            card.BackColor = selected ? BlueBg : Color.White;
            card.Tag = selected ? "on" : "off";
            card.Invalidate();
            foreach (Control c in card.Controls)
            {
                c.BackColor = selected ? BlueBg : Color.White;
                if (c is Label lbl && lbl.Tag?.ToString() == "title") lbl.ForeColor = selected ? BlueText : TextPri;
                if (c is Label sub && sub.Tag?.ToString() == "sub") sub.ForeColor = selected ? Blue : TextSec;
            }
        }

        private void UpdateOkButtonStyle()
        {
            okButton.BackColor = okButton.Enabled ? Blue : Color.FromArgb(180, 180, 180);
        }

        private async void OnOkClicked(object sender, EventArgs e)
        {
            var key = keyInput.Text.Trim();
            if (key.Length < 10)
            {
                errorLabel.Text = "Please enter a valid API key.";
                errorLabel.Visible = true;
                return;
            }

            SetFormEnabled(false);
            validatingLabel.Visible = true;
            errorLabel.Visible = false;

            try
            {
                (bool valid, string errorMsg) = await TestKeyAsync(key, SelectedProvider);
                if (valid)
                {
                    ApiKey = key;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else
                {
                    validatingLabel.Visible = false;
                    errorLabel.Text = $"Key rejected: {errorMsg}";
                    errorLabel.Visible = true;
                    SetFormEnabled(true);
                }
            }
            catch (Exception ex)
            {
                errorLabel.Text = $"Error: {ex.Message}";
                errorLabel.Visible = true;
                SetFormEnabled(true);
            }
        }

        private async Task<(bool ok, string error)> TestKeyAsync(string key, string provider)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)3072; // Tls12 + Tls13
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(15);
                    System.Net.Http.HttpResponseMessage response;

                    if (provider == "anthropic")
                    {
                        http.DefaultRequestHeaders.Add("x-api-key", key);
                        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                        var body = new System.Net.Http.StringContent("{\"model\":\"claude-3-haiku-20240307\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}", System.Text.Encoding.UTF8, "application/json");
                        response = await http.PostAsync("https://api.anthropic.com/v1/messages", body);
                    }
                    else
                    {
                        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
                        var payload = "{\"model\":\"openai/gpt-3.5-turbo\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}";
                        var body = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                        response = await http.PostAsync("https://openrouter.ai/api/v1/chat/completions", body);
                    }

                    if (response.IsSuccessStatusCode) return (true, null);
                    return (false, $"HTTP {response.StatusCode}");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private void SetFormEnabled(bool enabled)
        {
            keyInput.Enabled = okButton.Enabled = cancelButton.Enabled = anthropicCard.Enabled = openRouterCard.Enabled = enabled;
        }

        private void OnGetKeyLink(object sender, LinkLabelLinkClickedEventArgs e)
        {
            string url = SelectedProvider == "anthropic" ? "https://console.anthropic.com/api-keys" : "https://openrouter.ai/keys";
            System.Diagnostics.Process.Start(url);
        }

        // ── Drawing Helpers ──────────────────────────────────────────────────
        private void PaintLockIcon(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Blue, 1.8f))
            {
                g.DrawRectangle(p, 9, 20, 26, 18);
                g.DrawArc(p, 11, 10, 22, 22, 180, 180);
            }
            g.FillEllipse(new SolidBrush(Blue), 18, 26, 8, 8);
        }

        private void PaintEyeIcon(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var col = keyVisible ? Blue : TextSec;
            using (var p = new Pen(col, 1.5f))
            {
                g.DrawArc(p, 6, 6, 28, 12, 200, 140);
                g.DrawArc(p, 6, 6, 28, 12, 20, 140);
                if (!keyVisible) g.DrawLine(p, 8, 18, 32, 6);
            }
        }

        private Panel ProviderCard(Point loc, string title, string sub, bool recommended)
        {
            var card = new Panel { Location = loc, Size = new Size(244, 68), BackColor = Color.White, Cursor = Cursors.Hand };
            card.Paint += (s, e) => {
                bool sel = card.Tag?.ToString() == "on";
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = new Pen(sel ? Blue : Border, sel ? 2f : 1f))
                    DrawRoundRect(e.Graphics, p, new Rectangle(0, 0, card.Width - 1, card.Height - 1), 8);
            };

            var t = new Label { Text = title, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = TextPri, Location = new Point(12, 10), Size = new Size(220, 20), BackColor = Color.Transparent, Tag = "title" };
            var s2 = new Label { Text = sub, Font = new Font("Segoe UI", 7.5f), ForeColor = TextSec, Location = new Point(12, 30), Size = new Size(220, 14), BackColor = Color.Transparent, Tag = "sub" };
            card.Controls.AddRange(new Control[] { t, s2 });

            if (recommended)
            {
                var badge = new Label { Text = "✓ Recommended", Font = new Font("Segoe UI", 7f, FontStyle.Bold), ForeColor = BlueText, BackColor = Color.Transparent, Location = new Point(12, 48), Size = new Size(110, 14), Tag = "badge" };
                card.Controls.Add(badge);
            }
            Controls.Add(card);
            return card;
        }

        private void Lbl(int x, int y, int w, int h, string text, Font font, Color color)
        {
            Controls.Add(new Label { Text = text, Font = font, ForeColor = color, Location = new Point(x, y), Size = new Size(w, h), AutoSize = false });
        }

        private void Divider(int y)
        {
            Controls.Add(new Panel { Location = new Point(0, y), Size = new Size(560, 1), BackColor = Border });
        }

        private static void DrawRoundRect(Graphics g, Pen pen, Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
            path.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
            path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure(); g.DrawPath(pen, path);
        }

        private static Region RoundedRegion(Size size, int r)
        {
            var path = new GraphicsPath();
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(size.Width - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(size.Width - r * 2, size.Height - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, size.Height - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure(); return new Region(path);
        }
    }
}