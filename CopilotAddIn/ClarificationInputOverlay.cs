using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CopilotModels;

namespace CopilotAddIn
{
    public class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint |
                          ControlStyles.OptimizedDoubleBuffer |
                          ControlStyles.UserPaint, true);
        }
    }

    public class ClarificationInputOverlay : Form
    {
        public event EventHandler AnswersSubmitted;
        public event EventHandler Skipped;

        private readonly ClarificationQuestion[] _questions;
        private readonly List<TextBox> _answerBoxes = new List<TextBox>();
        private Button _submitBtn;
        private Panel _scrollPanel;
        private int _calculatedHeight;

        public int PreferredHeight => _calculatedHeight;

        // ── Colors ────────────────────────────────────────────────────────────
        private static readonly Color ColBackground = Color.FromArgb(248, 248, 245);
        private static readonly Color ColCardBg = Color.White;
        private static readonly Color ColBorder = Color.FromArgb(218, 218, 213);
        private static readonly Color ColTextPri = Color.FromArgb(25, 25, 25);
        private static readonly Color ColTextSec = Color.FromArgb(150, 150, 146);
        private static readonly Color ColAccent = Color.FromArgb(24, 95, 165);
        private static readonly Color ColAccentHover = Color.FromArgb(12, 68, 124);
        private static readonly Color ColAccentBg = Color.FromArgb(230, 241, 251);
        private static readonly Color ColAccentText = Color.FromArgb(12, 68, 124);
        private static readonly Color ColInputBg = Color.FromArgb(248, 248, 245);
        private static readonly Color ColGhostText = Color.FromArgb(100, 100, 96);
        private static readonly int CornerRadius = 8;
        private static readonly int CardPad = 12;
        private static readonly int SidePad = 12;

        // ── Fonts defined once so MeasureText and rendering always match ──────
        private static readonly Font QuestionFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        private static readonly Font HintFont = new Font("Segoe UI", 8f);
        private static readonly Font SuggFont = new Font("Segoe UI", 8f, FontStyle.Bold);

        public ClarificationInputOverlay(ClarificationQuestion[] questions)
        {
            _questions = questions;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = ColBackground;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;

            Build();
        }

        private void Build()
        {
            _scrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = false,
                BackColor = ColBackground,
            };
            Controls.Add(_scrollPanel);

            BuildContent();
        }

        private int _lastBuiltWidth = -1;

        private void BuildContent()
        {
            int w = _scrollPanel.Width > 0 ? _scrollPanel.Width : 300;
            if (w == _lastBuiltWidth) return;
            _lastBuiltWidth = w;

            _scrollPanel.Controls.Clear();
            _answerBoxes.Clear();

            int cardW = w - SidePad * 2;
            int innerW = cardW - CardPad * 2;

            // ── Header ────────────────────────────────────────────────────────
            var header = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(w, 52),
                BackColor = ColCardBg,
            };
            header.Paint += (s, e) =>
            {
                using (var pen = new Pen(ColBorder, 1f))
                    e.Graphics.DrawLine(pen, 0, 51, w, 51);
            };

            var iconBox = new Panel
            {
                Location = new Point(12, 12),
                Size = new Size(28, 28),
                BackColor = ColAccentBg,
            };
            iconBox.Region = RoundedRegion(28, 28, 6);
            iconBox.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(ColAccentHover))
                    e.Graphics.DrawString("?",
                        new Font("Segoe UI", 13f, FontStyle.Bold), brush, 6, 2);
            };
            header.Controls.Add(iconBox);

            var headerLbl = new Label
            {
                Text = "AI needs a few details",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = ColAccentText,
                Location = new Point(48, 16),
                Size = new Size(w - 48 - 60, 20),
                BackColor = Color.Transparent,
            };
            header.Controls.Add(headerLbl);

            var skipLink = new LinkLabel
            {
                Text = "Skip →",
                Font = new Font("Segoe UI", 8.5f),
                LinkColor = ColTextSec,
                ActiveLinkColor = ColTextPri,
                VisitedLinkColor = ColTextSec,
                Location = new Point(w - 56, 18),
                Size = new Size(50, 18),
                TextAlign = ContentAlignment.MiddleRight,
                BackColor = Color.Transparent,
                TabStop = false,
            };
            skipLink.LinkClicked += (s, e) => Skipped?.Invoke(this, EventArgs.Empty);
            header.Controls.Add(skipLink);

            _scrollPanel.Controls.Add(header);

            // ── Subtitle ──────────────────────────────────────────────────────
            var subLbl = new Label
            {
                Text = "Answer to improve step accuracy:",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = ColGhostText,
                Location = new Point(SidePad, 58),
                Size = new Size(w - SidePad * 2, 16),
                BackColor = Color.Transparent,
            };
            _scrollPanel.Controls.Add(subLbl);

            // ── Question cards ────────────────────────────────────────────────
            int y = 78;
            foreach (var q in _questions)
            {
                var card = BuildQuestionCard(q, cardW, innerW);
                card.Location = new Point(SidePad, y);
                _scrollPanel.Controls.Add(card);
                y += card.Height + 8;
            }

            // ── Submit button ─────────────────────────────────────────────────
            bool hasAny = false;
            foreach (var tb in _answerBoxes)
                if (!string.IsNullOrWhiteSpace(tb.Text)) { hasAny = true; break; }

            _submitBtn = new Button
            {
                Text = "Generate Steps with Answers",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Location = new Point(SidePad, y + 6),
                Size = new Size(cardW, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = hasAny ? ColAccent : Color.FromArgb(180, 180, 180),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Enabled = hasAny,
                TabStop = false,
            };
            _submitBtn.FlatAppearance.BorderSize = 0;
            _submitBtn.Region = RoundedRegion(_submitBtn.Width, 34, CornerRadius);
            _submitBtn.Click += (s, e) => AnswersSubmitted?.Invoke(this, EventArgs.Empty);
            _submitBtn.Paint += PaintSubmitBtn;
            _scrollPanel.Controls.Add(_submitBtn);

            // Always accurate — computed after all cards are built with real heights
            _calculatedHeight = y + 6 + 34 + 8;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_scrollPanel != null)
                BuildContent();
        }

        /// <summary>
        /// Measures text height at a fixed width using TextRenderer (matches GDI rendering).
        /// Returns the measured height plus an optional vertical gap beneath it.
        /// Returns 0 for null/empty strings so callers can skip absent rows cleanly.
        /// </summary>
        private static int MeasureH(string text, Font font, int maxWidth, int gap = 4)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(
                text,
                font,
                new Size(maxWidth, int.MaxValue),
                TextFormatFlags.WordBreak).Height + gap;
        }

        private DoubleBufferedPanel BuildQuestionCard(
            ClarificationQuestion question, int cardW, int innerW)
        {
            // ── Measure every text row at the real inner width ────────────────
            int questionH = MeasureH(
                $"Q{question.Id}. {question.Question}", QuestionFont, innerW);

            int hintH = MeasureH(question.Hint, HintFont, innerW);

            string suggText = (question.SuggestedValues != null &&
                               question.SuggestedValues.Length > 0)
                              ? "Suggestions: " + string.Join("  ·  ", question.SuggestedValues)
                              : null;
            int suggH = MeasureH(suggText, SuggFont, innerW);

            // ── Total card height: padding + measured rows + input ────────────
            int qh = CardPad      // top padding
                   + questionH    // question text  (1..n lines)
                   + hintH        // hint row       (0 if absent)
                   + suggH        // suggestions    (0 if absent)
                   + 30           // answer input box
                   + CardPad;     // bottom padding

            var card = new DoubleBufferedPanel
            {
                Size = new Size(cardW, qh),
                BackColor = ColCardBg,
                TabStop = false,
            };

            card.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(ColBorder, 1f))
                    DrawRoundRect(e.Graphics, pen,
                        new Rectangle(0, 0, cardW - 1, qh - 1), CornerRadius);
            };

            int cy = CardPad;

            // ── Question label ────────────────────────────────────────────────
            card.Controls.Add(new Label
            {
                Text = $"Q{question.Id}. {question.Question}",
                Font = QuestionFont,
                ForeColor = ColTextPri,
                Location = new Point(CardPad, cy),
                Size = new Size(innerW, questionH),
                BackColor = Color.Transparent,
                TabStop = false,
            });
            cy += questionH;

            // ── Hint ──────────────────────────────────────────────────────────
            if (hintH > 0)
            {
                card.Controls.Add(new Label
                {
                    Text = question.Hint,
                    Font = HintFont,
                    ForeColor = ColTextSec,
                    Location = new Point(CardPad, cy),
                    Size = new Size(innerW, hintH),
                    BackColor = Color.Transparent,
                    TabStop = false,
                });
                cy += hintH;
            }

            // ── Suggested values ──────────────────────────────────────────────
            if (suggH > 0)
            {
                card.Controls.Add(new Label
                {
                    Text = suggText,
                    Font = SuggFont,
                    ForeColor = ColAccent,
                    Location = new Point(CardPad, cy),
                    Size = new Size(innerW, suggH),
                    BackColor = Color.Transparent,
                    TabStop = false,
                });
                cy += suggH;
            }

            // ── Answer input ──────────────────────────────────────────────────
            var inputContainer = new Panel
            {
                Location = new Point(CardPad, cy),
                Size = new Size(innerW, 30),
                BackColor = ColInputBg,
            };
            inputContainer.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(ColBorder, 1f))
                    DrawRoundRect(e.Graphics, pen,
                        new Rectangle(0, 0,
                            inputContainer.Width - 1,
                            inputContainer.Height - 1), 6);
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
                Tag = question.Id,
            };

            textBox.TextChanged += (s, e) =>
            {
                bool hasAny = false;
                foreach (var tb in _answerBoxes)
                    if (!string.IsNullOrWhiteSpace(tb.Text)) { hasAny = true; break; }

                if (_submitBtn != null)
                {
                    _submitBtn.Enabled = hasAny;
                    _submitBtn.BackColor = hasAny ? ColAccent : Color.FromArgb(180, 180, 180);
                    _submitBtn.Invalidate();
                }
                inputContainer.Invalidate();
            };
            textBox.GotFocus += (s, e) => inputContainer.Invalidate();
            textBox.LostFocus += (s, e) => inputContainer.Invalidate();

            inputContainer.Controls.Add(textBox);
            _answerBoxes.Add(textBox);
            card.Controls.Add(inputContainer);

            return card;
        }

        private void PaintSubmitBtn(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var btn = (Button)sender;
            using (var brush = new SolidBrush(btn.BackColor))
                g.FillPath(brush, RoundedPath(btn.Width, btn.Height, CornerRadius));

            TextRenderer.DrawText(g, btn.Text, btn.Font,
                new Rectangle(0, 0, btn.Width, btn.Height),
                btn.ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ── Graphics helpers ──────────────────────────────────────────────────

        private static Region RoundedRegion(int w, int h, int r)
            => new Region(RoundedPath(w, h, r));

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