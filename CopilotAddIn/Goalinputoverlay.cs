using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace CopilotAddIn
{
    /// <summary>
    /// Borderless WinForms overlay containing the goal TextBox plus an optional
    /// image attachment (Sprint 2). The image is stored as base64 PNG and exposed
    /// via AttachedImageBase64 / AttachedImageMediaType so TaskPaneManager can
    /// forward it straight to AiClient without any extra I/O.
    ///
    /// Layout: TextBox fills the top area; a thin toolbar (28 px) at the bottom
    /// holds the attach button, thumbnail, filename label, and clear button.
    /// </summary>
    public class GoalInputOverlay : Form
    {
        // ── Public API ────────────────────────────────────────────────────────

        public string GoalText
        {
            get => _textBox.Text;
            set
            {
                _textBox.Text = value;
                _placeholder.Visible = string.IsNullOrEmpty(value);
            }
        }

        /// <summary>Base64-encoded PNG, or null when no image is attached.</summary>
        public string AttachedImageBase64 { get; private set; }

        /// <summary>"image/png" when set, null otherwise.</summary>
        public string AttachedImageMediaType { get; private set; }

        public event EventHandler GoalTextChanged;
        public event EventHandler SubmitRequested;
        /// <summary>Fired whenever an image is attached or cleared.</summary>
        public event EventHandler ImageAttachmentChanged;

        // ── Layout constants ──────────────────────────────────────────────────
        private const int ToolbarH = 28;
        private const int ThumbSize = 20;
        private const int ThumbMargin = 4;

        // ── Controls ──────────────────────────────────────────────────────────
        private readonly TextBox _textBox;
        private readonly Label _placeholder;
        private readonly Panel _toolbar;
        private readonly PictureBox _thumb;
        private readonly Label _thumbLabel;
        private readonly Button _attachBtn;
        private readonly Button _clearImgBtn;

        // ── Colors ────────────────────────────────────────────────────────────
        private static readonly Color ColBackground = Color.White;
        private static readonly Color ColForeground = Color.FromArgb(25, 25, 25);
        private static readonly Color ColBorder = Color.FromArgb(218, 218, 213);
        private static readonly Color ColBorderFocus = Color.FromArgb(24, 95, 165);
        private static readonly Color ColPlaceholder = Color.FromArgb(180, 180, 178);
        private static readonly Color ColToolbar = Color.FromArgb(241, 239, 232);
        private static readonly Color ColAccent = Color.FromArgb(24, 95, 165);
        private static readonly Color ColAccentBg = Color.FromArgb(230, 241, 251);
        private static readonly Color ColTextSec = Color.FromArgb(150, 150, 146);

        public GoalInputOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = ColBackground;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;

            // ── TextBox ───────────────────────────────────────────────────────
            _textBox = new TextBox
            {
                Multiline = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = ColBackground,
                ForeColor = ColForeground,
                Font = new Font("Segoe UI", 10.5f),
                AcceptsReturn = true,
                TabStop = true,
            };
            _textBox.TextChanged += (s, e) =>
            {
                _placeholder.Visible = string.IsNullOrEmpty(_textBox.Text);
                GoalTextChanged?.Invoke(this, e);
                Invalidate();
            };
            _textBox.KeyDown += OnKeyDown;
            _textBox.GotFocus += (s, e) => Invalidate();
            _textBox.LostFocus += (s, e) => Invalidate();

            // ── Placeholder ───────────────────────────────────────────────────
            _placeholder = new Label
            {
                Text = "Describe what you want to design...",
                ForeColor = ColPlaceholder,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Italic),
                BackColor = Color.Transparent,
                AutoSize = false,
                Left = 10,
                Top = 7,
                Height = 20,
                Enabled = false,
            };
            _placeholder.Click += (s, e) => _textBox.Focus();

            // ── Bottom toolbar ────────────────────────────────────────────────
            _toolbar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = ToolbarH,
                BackColor = ColToolbar,
            };
            _toolbar.Paint += (s, e) =>
            {
                using (var pen = new Pen(ColBorder, 1f))
                    e.Graphics.DrawLine(pen, 0, 0, _toolbar.Width, 0);
            };

            // ── Attach button — drawn paperclip, no emoji ─────────────────────
            _attachBtn = new Button
            {
                Text = string.Empty,   // no text — icon is drawn in Paint
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Size = new Size(26, 22),
                Location = new Point(4, 3),
                TabStop = false,
            };
            _attachBtn.FlatAppearance.BorderSize = 0;
            _attachBtn.FlatAppearance.MouseOverBackColor = ColAccentBg;
            _attachBtn.FlatAppearance.MouseDownBackColor = ColAccentBg;
            _attachBtn.Paint += PaintPaperclip;
            _attachBtn.Click += OnAttachImage;
            new ToolTip().SetToolTip(_attachBtn, "Attach reference image (PNG, JPG, WEBP)");

            // Thumbnail (click to replace)
            _thumb = new PictureBox
            {
                Size = new Size(ThumbSize, ThumbSize),
                Location = new Point(34, (ToolbarH - ThumbSize) / 2),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Visible = false,
                Cursor = Cursors.Hand,
                BorderStyle = BorderStyle.None,
            };
            _thumb.Click += OnAttachImage;

            // Filename label
            _thumbLabel = new Label
            {
                Text = string.Empty,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = ColTextSec,
                BackColor = Color.Transparent,
                AutoSize = false,
                Height = ToolbarH,
                Location = new Point(34 + ThumbSize + ThumbMargin, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                Visible = false,
            };

            // Clear button
            _clearImgBtn = new Button
            {
                Text = string.Empty,   // drawn as ✕ in Paint
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Size = new Size(18, 18),
                TabStop = false,
                Visible = false,
            };
            _clearImgBtn.FlatAppearance.BorderSize = 0;
            _clearImgBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 235, 232);
            _clearImgBtn.Paint += PaintClearIcon;
            _clearImgBtn.Click += OnClearImage;

            _toolbar.Controls.AddRange(new Control[]
                { _attachBtn, _thumb, _thumbLabel, _clearImgBtn });

            Controls.Add(_textBox);
            Controls.Add(_placeholder);
            Controls.Add(_toolbar);
        }

        // ── Icon painters ─────────────────────────────────────────────────────

        /// <summary>
        /// Draws a simple paperclip shape using GDI+ so it renders crisp on
        /// every Windows version regardless of emoji / font support.
        ///
        ///  Shape: a tall rounded rectangle (the clip body) with a shorter inner
        ///  rounded rectangle cut away, rotated ~40° to give the classic tilt.
        /// </summary>
        private void PaintPaperclip(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var btn = (Button)sender;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Centre of the button
            float cx = btn.Width / 2f;
            float cy = btn.Height / 2f;

            using (var pen = new Pen(ColAccent, 1.6f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // Save the graphics state so we can rotate around the centre
                var state = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(-38f);   // classic paperclip tilt
                g.TranslateTransform(-cx, -cy);

                // Outer clip arc — tall oval
                float ow = 7f, oh = 13f;
                float ox = cx - ow / 2f, oy = cy - oh / 2f;
                g.DrawArc(pen, ox, oy, ow, oh, 180, 180);           // left half-arc (top)
                g.DrawLine(pen, ox, oy + oh / 2f, ox, oy + oh);  // left straight
                g.DrawArc(pen, ox, oy + oh / 2f, ow, oh / 2f, 180, -180);     // bottom U

                // Inner shorter arc (the fold-back)
                float iw = 3.5f, ih = 8f;
                float ix = cx - iw / 2f, iy = oy + 1.5f;
                g.DrawArc(pen, ix, iy, iw, ih, 0, 180);             // right half-arc
                g.DrawLine(pen, ix + iw, iy + ih / 2f, ix + iw, iy + ih);

                g.Restore(state);
            }
        }

        /// <summary>
        /// Draws a small × cross for the clear-image button.
        /// Matches the style of the eye button in ApiKeySetupForm.
        /// </summary>
        private void PaintClearIcon(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var btn = (Button)sender;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float m = 5f;   // margin from edge
            float r = btn.Width - m;
            float b = btn.Height - m;

            using (var pen = new Pen(ColTextSec, 1.5f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, m, m, r, b);
                g.DrawLine(pen, r, m, m, b);
            }
        }

        // ── Layout ────────────────────────────────────────────────────────────

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);

            int tw = ClientSize.Width;
            int th = ClientSize.Height;
            int textH = Math.Max(0, th - ToolbarH);

            _textBox.SetBounds(0, 0, tw, textH);
            _placeholder.Width = Math.Max(0, tw - 20);
            _toolbar.Width = tw;

            int labelX = 34 + (AttachedImageBase64 != null ? ThumbSize + ThumbMargin : 0);
            int clearW = _clearImgBtn.Visible ? _clearImgBtn.Width + 4 : 0;
            _thumbLabel.Left = labelX;
            _thumbLabel.Width = Math.Max(0, tw - labelX - clearW - 6);
            _clearImgBtn.Left = tw - _clearImgBtn.Width - 4;
            _clearImgBtn.Top = (ToolbarH - _clearImgBtn.Height) / 2;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(_textBox.Focused ? ColBorderFocus : ColBorder, 1f))
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        // ── Image attachment ──────────────────────────────────────────────────

        private void OnAttachImage(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Attach reference image",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp",
                Multiselect = false,
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                LoadImage(dlg.FileName);
            }
        }

        private void LoadImage(string path)
        {
            try
            {
                using (var original = Image.FromFile(path))
                {
                    // Resize to max 1024px longest side — keeps token cost low
                    var resized = ResizeIfNeeded(original, 1024);

                    using (var ms = new MemoryStream())
                    {
                        resized.Save(ms, ImageFormat.Png);
                        AttachedImageBase64 = Convert.ToBase64String(ms.ToArray());
                        AttachedImageMediaType = "image/png";
                    }

                    if (_thumb.Image != null) _thumb.Image.Dispose();
                    _thumb.Image = resized.GetThumbnailImage(ThumbSize, ThumbSize, null, IntPtr.Zero);
                    _thumb.Visible = true;

                    if (!ReferenceEquals(resized, original)) resized.Dispose();
                }

                string fname = Path.GetFileName(path);
                _thumbLabel.Text = fname.Length > 22 ? fname.Substring(0, 20) + "…" : fname;
                _thumbLabel.Visible = true;
                _clearImgBtn.Visible = true;

                PerformLayout();
                Invalidate();
                ImageAttachmentChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not load image:\n" + ex.Message,
                    "Image Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnClearImage(object sender, EventArgs e)
        {
            AttachedImageBase64 = null;
            AttachedImageMediaType = null;

            if (_thumb.Image != null) { _thumb.Image.Dispose(); _thumb.Image = null; }
            _thumb.Visible = false;
            _thumbLabel.Text = string.Empty;
            _thumbLabel.Visible = false;
            _clearImgBtn.Visible = false;

            PerformLayout();
            Invalidate();
            ImageAttachmentChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Image ResizeIfNeeded(Image src, int maxSide)
        {
            if (src.Width <= maxSide && src.Height <= maxSide) return src;

            double ratio = Math.Min((double)maxSide / src.Width, (double)maxSide / src.Height);
            int nw = Math.Max(1, (int)(src.Width * ratio));
            int nh = Math.Max(1, (int)(src.Height * ratio));

            var bmp = new Bitmap(nw, nh);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, nw, nh);
            }
            return bmp;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SubmitRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}