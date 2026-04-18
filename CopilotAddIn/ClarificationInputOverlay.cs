using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CopilotModels;

namespace CopilotAddIn
{
    /// <summary>
    /// Native WinForms overlay for clarification Q&A — visually matches
    /// MainTaskPane pixel-for-pixel: same colors, typography, spacing, radii.
    /// Parented via SetParent so TextBoxes receive keyboard focus without
    /// SOLIDWORKS stealing it.
    /// </summary>
    public class ClarificationInputOverlay : Form
    {
        public event EventHandler AnswersSubmitted;
        public event EventHandler Skipped;

        private readonly ClarificationQuestion[] _questions;
        private readonly List<TextBox> _answerBoxes = new List<TextBox>();
        private readonly List<Panel> _questionCards = new List<Panel>();
        private Button _submitBtn;

        // ── MainTaskPane color system (exact match) ──────────────────────────
        private static readonly Color ColBackground    = Color.FromArgb(248, 248, 245); // #F8F8F5
        private static readonly Color ColCardBg        = Color.White;
        private static readonly Color ColBorder        = Color.FromArgb(218, 218, 213); // #DADAD5
        private static readonly Color ColTextPri       = Color.FromArgb(25, 25, 25);    // #191919
        private static readonly Color ColTextSec       = Color.FromArgb(150, 150, 146); // #969692
        private static readonly Color ColTextHint      = Color.FromArgb(180, 180, 178); // placeholder
        private static readonly Color ColAccent        = Color.FromArgb(24, 95, 165);   // #185FA5
        private static readonly Color ColAccentHover   = Color.FromArgb(12, 68, 124);   // #0C447C
        private static readonly Color ColAccentBg      = Color.FromArgb(230, 241, 251); // #E6F1FB
        private static readonly Color ColAccentText    = Color.FromArgb(12, 68, 124);   // #0C447C
        private static readonly Color ColSurface       = Color.FromArgb(241, 239, 232); // #F1EFE8 (status bar)
        private static readonly Color ColInputBg       = Color.FromArgb(248, 248, 245); // same as bg
        private static readonly Color ColGhostText     = Color.FromArgb(100, 100, 96);  // #646460
        private static readonly Color ColGhostHover    = Color.FromArgb(235, 235, 232); // #EBEBE8
        private static readonly int    CornerRadius    = 8;
        private static readonly int    CardPad         = 12;

        public ClarificationInputOverlay(ClarificationQuestion[] questions)
        {
            _questions = questions;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = ColBackground;
            StartPosition = FormStartPosition.Manual;
            TabStop = false;
            DoubleBuffered = true;

            Build();
        }

        private void Build()
        {
            // Calculate total height: header(52) + question cards(N * ~100) + submitBtn(34) + padding
            int cardHeight = 0;
            foreach (var q in _questions)
            {
                int h = 28; // question label
                if (!string.IsNullOrEmpty(q.Hint)) h += 16;
                if (q.SuggestedValues != null && q.SuggestedValues.Length > 0) h += 16;
                h += 34; // input box
                h += CardPad * 2 + 6; // card margin
                cardHeight += h;
            }

            int totalH = 52 + cardHeight + 40; // header + cards + submit area

            // Set size — TaskPaneManager will override to match placeholder width
            Size = new Size(300, Math.Min(totalH, 450));
            AutoScroll = totalH > 450;

            // ── Header (white card, matches MainTaskPane header style) ────────
            var header = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(Width, 52),
                BackColor = ColCardBg
            };
            header.Paint += (s, e) =>
            {
                // Bottom border only
                using (var pen = new Pen(ColBorder, 1f))
                    e.Graphics.DrawLine(pen, 0, 51, Width, 51);
            };
            Controls.Add(header);

            // Icon: blue rounded rect with ❓
            var iconBox = new Panel
            {
                Location = new Point(12, 12),
                Size = new Size(28, 28),
                BackColor = ColAccentBg
            };
            iconBox.Region = RoundedRegion(28, 28, 6);
            iconBox.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var brush = new SolidBrush(AccentDark()))
                    e.Graphics.DrawString("?", new Font("Segoe UI", 13f, FontStyle.Bold), brush, 6, 2);
            };
            header.Controls.Add(iconBox);

            // Header text: "AI needs a few details"
            var headerLbl = new Label
            {
                Text = "AI needs a few details",
                Font = new Font("Arial", 11f, FontStyle.Bold),
                ForeColor = ColAccentText,
                Location = new Point(48, 16),
                Size = new Size(200, 20),
                BackColor = Color.Transparent
            };
            header.Controls.Add(headerLbl);

            // Skip link (top-right)
            var skipLink = new LinkLabel
            {
                Text = "Skip →",
                Font = new Font("Segoe UI", 8.5f),
                LinkColor = ColTextSec,
                ActiveLinkColor = ColTextPri,
                VisitedLinkColor = ColTextSec,
                Location = new Point(Width - 56, 18),
                Size = new Size(50, 18),
                TextAlign = ContentAlignment.MiddleRight,
                BackColor = Color.Transparent,
                TabStop = false
            };
            skipLink.LinkClicked += (s, e) => Skipped?.Invoke(this, EventArgs.Empty);
            header.Controls.Add(skipLink);

            // ── Subtitle ──────────────────────────────────────────────────────
            var subLbl = new Label
            {
                Text = "Answer to improve step accuracy:",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = ColGhostText,
                Location = new Point(12, 58),
                Size = new Size(Width - 24, 16),
                BackColor = Color.Transparent
            };
            Controls.Add(subLbl);

            // ── Question cards ────────────────────────────────────────────────
            int y = 78;
            foreach (var q in _questions)
            {
                var card = BuildQuestionCard(q, Width - 24);
                card.Location = new Point(12, y);
                Controls.Add(card);
                y += card.Height + 6;
            }

            // ── Submit button ─────────────────────────────────────────────────
            _submitBtn = new Button
            {
                Text = "Generate Steps with Answers",
                Font = new Font("Arial", 11f, FontStyle.Bold),
                Location = new Point(12, y + 6),
                Size = new Size(Width - 24, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(180, 180, 180), // disabled gray
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Enabled = false,
                TabStop = false
            };
            _submitBtn.FlatAppearance.BorderSize = 0;
            _submitBtn.Region = RoundedRegion(_submitBtn.Width, 34, CornerRadius);
            _submitBtn.Click += (s, e) => AnswersSubmitted?.Invoke(this, EventArgs.Empty);
            _submitBtn.Paint += PaintSubmitBtn;
            Controls.Add(_submitBtn);
        }

        private Panel BuildQuestionCard(ClarificationQuestion question, int cardW)
        {
            int qh = 28; // label height
            if (!string.IsNullOrEmpty(question.Hint)) qh += 16;
            if (question.SuggestedValues != null && question.SuggestedValues.Length > 0) qh += 16;
            qh += 34; // input box
            qh += CardPad * 2;

            var card = new Panel
            {
                Size = new Size(cardW, qh),
                BackColor = ColCardBg,
                TabStop = false
            };

            // Rounded border outline
            card.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, cardW - 1, qh - 1);
                using (var pen = new Pen(ColBorder, 1f))
                    DrawRoundRect(e.Graphics, pen, r, CornerRadius);
            };

            int cy = CardPad;

            // Question label
            var qLabel = new Label
            {
                Text = $"Q{question.Id}. {question.Question}",
                Font = new Font("Arial", 11f, FontStyle.Bold),
                ForeColor = ColTextPri,
                Location = new Point(CardPad, cy),
                Size = new Size(cardW - CardPad * 2, 20),
                BackColor = Color.Transparent,
                TabStop = false
            };
            card.Controls.Add(qLabel);
            cy += 20;

            // Hint
            if (!string.IsNullOrEmpty(question.Hint))
            {
                var hint = new Label
                {
                    Text = question.Hint,
                    Font = new Font("Segoe UI", 8f),
                    ForeColor = ColTextSec,
                    Location = new Point(CardPad, cy),
                    Size = new Size(cardW - CardPad * 2, 14),
                    BackColor = Color.Transparent,
                    TabStop = false
                };
                card.Controls.Add(hint);
                cy += 14;
            }

            // Suggested values
            if (question.SuggestedValues != null && question.SuggestedValues.Length > 0)
            {
                var sugg = new Label
                {
                    Text = "Suggestions: " + string.Join("  ·  ", question.SuggestedValues),
                    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                    ForeColor = ColAccent,
                    Location = new Point(CardPad, cy),
                    Size = new Size(cardW - CardPad * 2, 14),
                    BackColor = Color.Transparent,
                    TabStop = false
                };
                card.Controls.Add(sugg);
                cy += 14;
            }

            // Input box
            var inputContainer = new Panel
            {
                Location = new Point(CardPad, cy),
                Size = new Size(cardW - CardPad * 2, 30),
                BackColor = ColInputBg
            };
            inputContainer.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, inputContainer.Width - 1, inputContainer.Height - 1);
                using (var pen = new Pen(ColBorder, 1f))
                    DrawRoundRect(e.Graphics, pen, r, 6);
            };

            var textBox = new TextBox
            {
                Location = new Point(10, 4),
                Size = new Size(inputContainer.Width - 20, 22),
                Font = new Font("Segoe UI", 9f),
                BackColor = ColInputBg,
                ForeColor = ColTextPri,
                BorderStyle = BorderStyle.None,
                TabStop = true,
                Tag = question.Id
            };
            textBox.TextChanged += (s, e) =>
            {
                bool hasAny = false;
                foreach (var tb in _answerBoxes)
                    if (!string.IsNullOrWhiteSpace(tb.Text.Trim())) { hasAny = true; break; }

                _submitBtn.Enabled = hasAny;
                _submitBtn.BackColor = hasAny ? ColAccent : Color.FromArgb(180, 180, 180);
                _submitBtn.Invalidate();
                inputContainer.Invalidate();
                textBox.Invalidate();
            };
            textBox.GotFocus += (s, e) => inputContainer.Invalidate();
            textBox.LostFocus += (s, e) => inputContainer.Invalidate();
            inputContainer.Controls.Add(textBox);
            _answerBoxes.Add(textBox);

            card.Controls.Add(inputContainer);
            _questionCards.Add(card);

            return card;
        }

        private void PaintSubmitBtn(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var btn = (Button)sender;
            using (var brush = new SolidBrush(btn.BackColor))
            {
                var path = RoundedPath(btn.Width, btn.Height, CornerRadius);
                g.FillPath(brush, path);
            }
            // Text
            TextRenderer.DrawText(g, btn.Text, btn.Font,
                new Rectangle(0, 0, btn.Width, btn.Height),
                btn.ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ── Graphics helpers ──────────────────────────────────────────────────

        private static Region RoundedRegion(int w, int h, int r)
        {
            var path = RoundedPath(w, h, r);
            return new Region(path);
        }

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

        private static void DrawRoundRect(Graphics g, Pen pen, Rectangle r, int radius)
        {
            using (var path = RoundedPath(r.Width, r.Height, radius))
                g.DrawPath(pen, path);
        }

        private static Color AccentDark() => ColAccentHover;

        // ── Public: collect answers ───────────────────────────────────────────

        public Dictionary<int, string> GetAnswers()
        {
            var answers = new Dictionary<int, string>();
            foreach (var tb in _answerBoxes)
            {
                var text = tb.Text.Trim();
                if (!string.IsNullOrEmpty(text) && tb.Tag is int id)
                    answers[id] = text;
            }
            return answers;
        }
    }
}
