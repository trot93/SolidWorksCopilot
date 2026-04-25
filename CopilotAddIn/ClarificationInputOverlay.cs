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

        // Font used for the question label — defined once so MeasureText is consistent
        private static readonly Font QuestionFont = new Font("Segoe UI", 10f, FontStyle.Bold);

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
                var card = BuildQuestionCard(q, cardW);
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

            // Recalculate preferred height now that we know actual card heights
            _calculatedHeight = y + 6 + 34 + 8; // cards end + btn top margin + btn height + bottom pad
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_scrollPanel != null)
                BuildContent();
        }

        private DoubleBufferedPanel BuildQuestionCard(ClarificationQuestion question, int cardW)
        {
            int innerW = cardW - CardPad * 2;

            // ── Measure how tall the question text actually needs to be ────────
            // TextRenderer.MeasureText respects word-wrap at the given width,
            // so long questions get the height they need instead of being clipped.
            int questionLabelH = TextRenderer.MeasureText(
                $"Q{question.Id}. {question.Question}",
                QuestionFont,
                new Size(innerW, int.MaxValue),          // constrain width, free height
                TextFormatFlags.WordBreak).Height;

            // ── Accumulate total card height based on real content ────────────
            int qh = CardPad;                            // top padding
            qh += questionLabelH;                        // variable-height question text
            qh += 4;                                     // gap after question

            if (!string.IsNullOrEmpty(question.Hint))
                qh += 16;                                // hint row

            if (question.SuggestedValues != null && question.SuggestedValues.Length > 0)
                qh += 16;                                // suggestions row

            qh += 30;                                    // input box
            qh += CardPad;                               // bottom padding

            var card = new DoubleBufferedPanel
            {
                Size = new Size(cardW, qh),
                BackColor = ColCardBg,
                TabStop = false,
            };

            card.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, cardW - 1, qh - 1);
                using (var pen = new Pen(ColBorder, 1f))
                    DrawRoundRect(e.Graphics, pen, r, CornerRadius);
            };

            int cy = CardPad;

            // ── Question label — multi-line, exact measured height ────────────
            var qLabel = new Label
            {
                Text = $"Q{question.Id}. {question.Question}",
                Font = QuestionFont,
                ForeColor = ColTextPri,
                Location = new Point(CardPad, cy),
                Size = new Size(innerW, questionLabelH),
                BackColor = Color.Transparent,
                TabStop = false,
                // AutoSize = false + fixed width + no height clamp = word-wraps freely
            };
            card.Controls.Add(qLabel);
            cy += questionLabelH + 4;

            // ── Hint ──────────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(question.Hint))
            {
                var hint = new Label
                {
                    Text = question.Hint,
                    Font = new Font("Segoe UI", 8f),
                    ForeColor = ColTextSec,
                    Location = new Point(CardPad, cy),
                    Size = new Size(innerW, 14),
                    BackColor = Color.Transparent,
                    TabStop = false,
                };
                card.Controls.Add(hint);
                cy += 16;
            }

            // ── Suggested values ──────────────────────────────────────────────
            if (question.SuggestedValues != null && question.SuggestedValues.Length > 0)
            {
                var sugg = new Label
                {
                    Text = "Suggestions: " + string.Join("  ·  ", question.SuggestedValues),
                    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                    ForeColor = ColAccent,
                    Location = new Point(CardPad, cy),
                    Size = new Size(innerW, 14),
                    BackColor = Color.Transparent,
                    TabStop = false,
                };
                card.Controls.Add(sugg);
                cy += 16;
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