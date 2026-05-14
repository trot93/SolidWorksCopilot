// SPRINT 3: FeedbackDialog — WinForms Form matching the attached redesign mockup.
// Uses WinForms throughout (NOT WPF Window) — safe in SolidWorks ElementHost context.
// Quick-issue chips + free-text textarea + severity selector + Send to AI button.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CopilotUI
{
    public class FeedbackDialog : Form
    {
        // ── Public API ────────────────────────────────────────────────────────
        public string FeedbackText { get; private set; }

        // ── Colors (adapted from design tokens to match SW copilot palette) ──
        private static readonly Color BgPrimary = Color.FromArgb(255, 255, 255);
        private static readonly Color BgSecondary = Color.FromArgb(248, 248, 245);
        private static readonly Color BorderLight = Color.FromArgb(218, 218, 213);
        private static readonly Color BorderMed = Color.FromArgb(200, 198, 192);
        private static readonly Color TextPrimary = Color.FromArgb(25, 25, 25);
        private static readonly Color TextSecond = Color.FromArgb(100, 100, 96);
        private static readonly Color TextTertiary = Color.FromArgb(150, 150, 146);
        private static readonly Color AccentBlue = Color.FromArgb(24, 95, 165);
        private static readonly Color ChipActive = Color.FromArgb(230, 241, 251);
        private static readonly Color ChipActiveTx = Color.FromArgb(12, 68, 124);
        private static readonly Color SevLow = Color.FromArgb(29, 158, 117);
        private static readonly Color SevMid = Color.FromArgb(239, 159, 39);
        private static readonly Color SevHigh = Color.FromArgb(226, 75, 74);

        // ── State ─────────────────────────────────────────────────────────────
        private readonly List<Button> _chips = new List<Button>();
        private readonly HashSet<string> _active = new HashSet<string>();
        private RichTextBox _editor;
        private Label _charCount;
        private Button _sendBtn;
        private string _severity = null;

        // ── Constructor ───────────────────────────────────────────────────────
        public FeedbackDialog(string stepSummary = null)
        {
            Text = "Step Feedback";
            Width = 500;
            Height = 420;
            MinimumSize = new Size(400, 360);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            BackColor = BgPrimary;
            Font = new Font("Segoe UI", 9f);
            DoubleBuffered = true;

            BuildUI(stepSummary);
        }

        private void BuildUI(string stepSummary)
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 6,
                ColumnCount = 1,
                BackColor = BgPrimary,
                Padding = new Padding(0)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // header
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // context chip
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // quick chips
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // textarea
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // char count
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // footer

            // ── Header ───────────────────────────────────────────────────────
            var header = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgPrimary,
                Height = 64,
                Padding = new Padding(20, 18, 20, 0)
            };

            var titleLabel = new Label
            {
                Text = "What went wrong?",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = TextPrimary,
                AutoSize = false,
                Location = new Point(20, 18),
                Size = new Size(380, 22),
                BackColor = Color.Transparent
            };

            var subLabel = new Label
            {
                Text = "Step feedback — describe the issue and the AI will rewrite the step.",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextTertiary,
                AutoSize = false,
                Location = new Point(20, 42),
                Size = new Size(440, 16),
                BackColor = Color.Transparent
            };

            header.Controls.Add(titleLabel);
            header.Controls.Add(subLabel);
            root.Controls.Add(header, 0, 0);

            // ── Context chip ─────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(stepSummary))
            {
                var ctxPanel = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = BgPrimary,
                    Height = 38,
                    Padding = new Padding(20, 4, 20, 0)
                };

                var ctx = new Label
                {
                    Text = $"Step: {stepSummary}",
                    Font = new Font("Segoe UI", 9f),
                    ForeColor = ChipActiveTx,
                    BackColor = ChipActive,
                    AutoSize = false,
                    Size = new Size(440, 26),
                    Location = new Point(20, 4),
                    Padding = new Padding(10, 4, 10, 4),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                ctxPanel.Controls.Add(ctx);
                root.Controls.Add(ctxPanel, 0, 1);
            }
            else
            {
                root.Controls.Add(new Panel { Height = 4, BackColor = BgPrimary }, 0, 1);
            }

            // ── Quick-issue chips ─────────────────────────────────────────────
            var chipLabels = new[] { "Wrong tool", "Wrong order", "Unclear wording", "Missing substep" };
            var chipPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = BgPrimary,
                Height = 40,
                Padding = new Padding(16, 6, 16, 0),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = false
            };

            foreach (var label in chipLabels)
            {
                var chip = new Button
                {
                    Text = label,
                    Font = new Font("Segoe UI", 8.5f),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = BgSecondary,
                    ForeColor = TextSecond,
                    Height = 26,
                    AutoSize = true,
                    Margin = new Padding(0, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    Tag = label
                };
                chip.FlatAppearance.BorderColor = BorderLight;
                chip.FlatAppearance.BorderSize = 1;
                chip.Click += OnChipClick;
                chipPanel.Controls.Add(chip);
                _chips.Add(chip);
            }
            root.Controls.Add(chipPanel, 0, 2);

            // ── Textarea ─────────────────────────────────────────────────────
            var editorPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgPrimary,
                Padding = new Padding(20, 8, 20, 0)
            };

            _editor = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = BgSecondary,
                ForeColor = TextPrimary,
                Font = new Font("Segoe UI", 10f),
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Padding = new Padding(4)
            };
            _editor.TextChanged += OnEditorChanged;

            editorPanel.Controls.Add(_editor);
            root.Controls.Add(editorPanel, 0, 3);

            // ── Char count row ────────────────────────────────────────────────
            var charPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgPrimary,
                Height = 22,
                Padding = new Padding(20, 2, 20, 0)
            };

            _charCount = new Label
            {
                Text = "0 / 600",
                Font = new Font("Segoe UI", 8f),
                ForeColor = TextTertiary,
                AutoSize = false,
                Size = new Size(80, 16),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleRight,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            _charCount.Location = new Point(charPanel.Width - 100, 2);
            _charCount.Anchor = AnchorStyles.Right | AnchorStyles.Top;

            charPanel.Controls.Add(_charCount);
            root.Controls.Add(charPanel, 0, 4);

            // ── Footer: severity + buttons ────────────────────────────────────
            var footer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(241, 239, 232),
                Height = 52,
                Padding = new Padding(20, 10, 20, 10)
            };
            footer.Paint += (s, e) =>
            {
                using (var pen = new Pen(BorderLight, 1f))
                    e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            // Severity dots
            var sevLabel = new Label
            {
                Text = "Severity",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = TextTertiary,
                AutoSize = true,
                Location = new Point(0, 10),
                BackColor = Color.Transparent
            };
            footer.Controls.Add(sevLabel);

            var sevColors = new[] { SevLow, SevMid, SevHigh };
            var sevNames = new[] { "Low", "Medium", "High" };
            for (int i = 0; i < 3; i++)
            {
                int x = 62 + i * 28;
                var dot = new Panel
                {
                    Size = new Size(20, 20),
                    Location = new Point(x, 8),
                    BackColor = Color.FromArgb(200,
                        sevColors[i].R, sevColors[i].G, sevColors[i].B),
                    Cursor = Cursors.Hand,
                    Tag = sevNames[i]
                };
                dot.Region = new Region(RoundedPath(20, 20, 10));
                dot.Click += OnSevClick;
                footer.Controls.Add(dot);
            }

            // Cancel + Send buttons
            _sendBtn = new Button
            {
                Text = "Send to AI →",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(180, 180, 180),
                ForeColor = Color.White,
                Height = 32,
                Width = 110,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Cursor = Cursors.Hand,
                Enabled = false
            };
            _sendBtn.FlatAppearance.BorderSize = 0;
            _sendBtn.Click += OnSendClick;

            var cancelBtn = new Button
            {
                Text = "Cancel",
                Font = new Font("Segoe UI", 9f),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = TextSecond,
                Height = 32,
                Width = 80,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Cursor = Cursors.Hand
            };
            cancelBtn.FlatAppearance.BorderColor = BorderLight;
            cancelBtn.FlatAppearance.BorderSize = 1;
            cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            footer.SizeChanged += (s, e) =>
            {
                _sendBtn.Location = new Point(footer.ClientSize.Width - _sendBtn.Width - 20, 10);
                cancelBtn.Location = new Point(footer.ClientSize.Width - _sendBtn.Width - cancelBtn.Width - 28, 10);
            };

            footer.Controls.Add(_sendBtn);
            footer.Controls.Add(cancelBtn);
            root.Controls.Add(footer, 0, 5);

            Controls.Add(root);
            AcceptButton = _sendBtn;
            CancelButton = cancelBtn;
            Shown += (s, e) => _editor.Focus();
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void OnChipClick(object sender, EventArgs e)
        {
            var chip = (Button)sender;
            var label = (string)chip.Tag;

            if (_active.Contains(label))
            {
                _active.Remove(label);
                chip.BackColor = BgSecondary;
                chip.ForeColor = TextSecond;
                chip.FlatAppearance.BorderColor = BorderLight;
            }
            else
            {
                _active.Add(label);
                chip.BackColor = ChipActive;
                chip.ForeColor = ChipActiveTx;
                chip.FlatAppearance.BorderColor = Color.FromArgb(180, 210, 240);
            }
            UpdateSendState();
        }

        private void OnSevClick(object sender, EventArgs e)
        {
            var dot = (Panel)sender;
            _severity = (string)dot.Tag;

            // Reset all dots, highlight selected
            foreach (Control c in dot.Parent.Controls)
            {
                if (c is Panel p && p.Tag is string)
                {
                    bool sel = (string)p.Tag == _severity;
                    Color base_ = (string)p.Tag == "Low" ? SevLow :
                                  (string)p.Tag == "Medium" ? SevMid : SevHigh;
                    p.BackColor = sel ? base_ : Color.FromArgb(200, base_.R, base_.G, base_.B);
                }
            }
        }

        private void OnEditorChanged(object sender, EventArgs e)
        {
            int len = _editor.Text.Length;
            if (len > 600) { _editor.Text = _editor.Text.Substring(0, 600); _editor.SelectionStart = 600; }
            _charCount.Text = $"{Math.Min(len, 600)} / 600";
            _charCount.ForeColor = len > 500 ? SevMid : TextTertiary;
            _editor.BackColor = BgSecondary;
            UpdateSendState();
        }

        private void UpdateSendState()
        {
            bool hasText = !string.IsNullOrWhiteSpace(_editor.Text);
            bool hasChip = _active.Count > 0;
            _sendBtn.Enabled = hasText || hasChip;
            _sendBtn.BackColor = _sendBtn.Enabled ? AccentBlue : Color.FromArgb(180, 180, 180);
        }

        private void OnSendClick(object sender, EventArgs e)
        {
            var text = _editor.Text.Trim();
            if (string.IsNullOrWhiteSpace(text) && _active.Count == 0)
            {
                _editor.BackColor = Color.FromArgb(255, 240, 240);
                _editor.Focus();
                return;
            }

            var parts = new List<string>();
            if (_active.Count > 0)
                parts.Add(string.Join(", ", _active));
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text);
            if (_severity != null)
                parts.Add($"[severity: {_severity}]");

            FeedbackText = string.Join(" — ", parts);
            DialogResult = DialogResult.OK;
            Close();
        }

        // ── Graphics helper ───────────────────────────────────────────────────
        private static GraphicsPath RoundedPath(int w, int h, int r)
        {
            var path = new GraphicsPath();
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(w - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(w - r * 2, h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}