using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CopilotUI
{
    /// <summary>
    /// A proper multi-line edit dialog for step modifications.
    /// Pre-populated with the AI's original instruction text.
    /// The engineer edits directly on top of it.
    /// </summary>
    public class ModifyStepDialog : Window
    {
        public string ResultText { get; private set; }

        private readonly TextBox _editor;

        public ModifyStepDialog(string originalText)
        {
            Title = "Modify Step";
            Width = 480;
            Height = 320;
            MinWidth = 360;
            MinHeight = 220;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            Background = new SolidColorBrush(Color.FromRgb(248, 248, 245));
            FontFamily = new FontFamily("Segoe UI");

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Header ────────────────────────────────────────────────────────
            var header = new TextBlock
            {
                Text = "Edit the step instructions — your version will be shown on the card",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 96)),
                Margin = new Thickness(12, 10, 12, 6),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // ── Editor ────────────────────────────────────────────────────────
            var editorBorder = new Border
            {
                Margin = new Thickness(12, 0, 12, 0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(218, 218, 213)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.White)
            };

            _editor = new TextBox
            {
                Text = originalText,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.White),
                Foreground = new SolidColorBrush(Color.FromRgb(25, 25, 25)),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                Padding = new Thickness(10, 8, 10, 8)
            };

            editorBorder.Child = _editor;
            Grid.SetRow(editorBorder, 1);
            root.Children.Add(editorBorder);

            // ── Button strip ──────────────────────────────────────────────────
            var strip = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(241, 239, 232)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(218, 218, 213)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(12, 8, 12, 8)
            };
            var stripPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Colors.White),
                Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 96)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(218, 218, 213)),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };

            var saveBtn = new Button
            {
                Content = "Save modification",
                Height = 30,
                Padding = new Thickness(14, 0, 14, 0),
                Background = new SolidColorBrush(Color.FromRgb(24, 95, 165)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            saveBtn.Click += (s, e) =>
            {
                ResultText = _editor.Text;
                DialogResult = true;
                Close();
            };

            stripPanel.Children.Add(cancelBtn);
            stripPanel.Children.Add(saveBtn);
            strip.Child = stripPanel;
            Grid.SetRow(strip, 2);
            root.Children.Add(strip);

            Content = root;

            // Select all text on open so engineer can replace entirely or edit inline
            Loaded += (s, e) =>
            {
                _editor.Focus();
                _editor.SelectAll();
            };
        }
    }
}