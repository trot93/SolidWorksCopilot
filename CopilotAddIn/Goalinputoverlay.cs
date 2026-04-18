using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CopilotAddIn
{
    /// <summary>
    /// A borderless WinForms Form containing a native TextBox.
    ///
    /// After creation, TaskPaneManager calls SetParent(Handle, container.Handle)
    /// to make this a Win32 child of the task pane container. From that point:
    ///   • Windows shows/hides it automatically with the container — no
    ///     visibility detection code needed at all.
    ///   • Tab switching, click-away, minimize all work for free.
    ///   • The TextBox still receives keyboard messages directly — typing works
    ///     because we are a native WinForms control, not inside ElementHost/WPF.
    ///
    /// Positioning is done by TaskPaneManager.RepositionOverlay() which calls
    /// SetBounds() with coordinates converted to container-client space.
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

        public event EventHandler GoalTextChanged;
        public event EventHandler SubmitRequested;

        // ── Controls ──────────────────────────────────────────────────────────

        private readonly TextBox _textBox;
        private readonly Label _placeholder;

        // ── Colors ────────────────────────────────────────────────────────────
        private static readonly Color ColBackground = Color.White;
        private static readonly Color ColForeground = Color.FromArgb(25, 25, 25);
        private static readonly Color ColBorder = Color.FromArgb(218, 218, 213);
        private static readonly Color ColBorderFocus = Color.FromArgb(24, 95, 165);
        private static readonly Color ColPlaceholder = Color.FromArgb(180, 180, 178);

        public GoalInputOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = ColBackground;
            StartPosition = FormStartPosition.Manual;

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
                Dock = DockStyle.Fill,
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

            Controls.Add(_textBox);
            Controls.Add(_placeholder);
            _placeholder.Click += (s, e) => _textBox.Focus();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            _placeholder.Width = ClientSize.Width - 20;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(_textBox.Focused ? ColBorderFocus : ColBorder, 1f))
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SubmitRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        // No CreateParams override needed — WS_CHILD is set implicitly
        // by SetParent() in TaskPaneManager after both HWNDs exist.
        // Setting it here without a valid parent causes silent HWND failure.
    }
}