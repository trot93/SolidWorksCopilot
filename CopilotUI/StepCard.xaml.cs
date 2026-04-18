using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text;

namespace CopilotUI
{
    public partial class StepCard : UserControl
    {
        // --- Properties ---
        public int StepNumber { get; set; }
        public string Feature { get; set; }
        public string SummaryLine { get; set; } // Concise design rationale
        public string StepRationale { get; set; } // Full technical reasoning
        public Dictionary<string, object> Parameters { get; set; }
        public string Risk { get; set; }
        public string Confidence { get; set; }
        public string[] Instructions { get; set; }

        // --- Callbacks ---
        public Action OnAccept { get; set; }
        public Action OnReject { get; set; }
        public Action<string> OnModify { get; set; }

        public StepCard()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Header Info
            StepNumberBadge.Text = StepNumber.ToString();
            FeatureText.Text = Feature ?? "CAD Step";

            // Rationales
            SummaryText.Text = SummaryLine ?? "Establishing geometry...";
            DetailedRationaleText.Text = StepRationale ?? "No detailed reasoning provided.";

            // Risk Handling
            if (!string.IsNullOrEmpty(Risk))
            {
                RiskPanel.Visibility = Visibility.Visible;
                RiskText.Text = Risk;
            }
            else
            {
                RiskPanel.Visibility = Visibility.Collapsed;
            }

            // Instructions (Technical Steps)
            if (Instructions != null && Instructions.Length > 0)
            {
                InstructionsList.ItemsSource = Instructions;
                InstructionsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                InstructionsPanel.Visibility = Visibility.Collapsed;
            }

            SetConfidenceBadge(Confidence);
        }

        private void SetConfidenceBadge(string confidence)
        {
            switch (confidence?.ToLower())
            {
                case "high":
                    ConfidenceText.Text = "HIGH";
                    ConfidenceBadge.Background = new SolidColorBrush(Color.FromRgb(234, 243, 222));
                    ConfidenceText.Foreground = new SolidColorBrush(Color.FromRgb(39, 80, 10));
                    break;
                case "low":
                    ConfidenceText.Text = "LOW";
                    ConfidenceBadge.Background = new SolidColorBrush(Color.FromRgb(252, 235, 235));
                    ConfidenceText.Foreground = new SolidColorBrush(Color.FromRgb(121, 31, 31));
                    break;
                default:
                    ConfidenceText.Text = "MED";
                    ConfidenceBadge.Background = new SolidColorBrush(Color.FromRgb(250, 238, 218));
                    ConfidenceText.Foreground = new SolidColorBrush(Color.FromRgb(99, 56, 6));
                    break;
            }
        }

        // --- Status Updates ---

        public void MarkCompleted()
        {
            CardBorder.Background = new SolidColorBrush(Color.FromRgb(245, 250, 240));
            CardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(159, 225, 203));
            StatusIndicator.Text = "✓ Done";
            StatusIndicator.Visibility = Visibility.Visible;
            AcceptBtn.IsEnabled = false;
            ModifyBtn.IsEnabled = false;
        }

        public void MarkRejected()
        {
            CardBorder.Background = new SolidColorBrush(Color.FromRgb(241, 239, 232));
            CardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(218, 218, 213));
            StatusIndicator.Text = "— Skipped";
            StatusIndicator.Visibility = Visibility.Visible;
            AcceptBtn.IsEnabled = false;
            ModifyBtn.IsEnabled = false;
        }

        public void MarkModified(string modifiedText)
        {
            CardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(240, 192, 64));
            ModifiedPanel.Visibility = Visibility.Visible;
            ModifiedText.Text = modifiedText;
            StatusIndicator.Text = "✎ Modified";
            StatusIndicator.Visibility = Visibility.Visible;
        }

        // --- Interaction Handlers ---

        private void OnAcceptClick(object sender, RoutedEventArgs e)
        {
            OnAccept?.Invoke();
        }

        private void OnModifyClick(object sender, RoutedEventArgs e)
        {
            var originalContent = BuildOriginalText();
            var dialog = new ModifyStepDialog(originalContent);
            dialog.Owner = Window.GetWindow(this);

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultText))
            {
                var modified = dialog.ResultText.Trim();
                MarkModified(modified);
                OnModify?.Invoke(modified);
            }
        }

        private string BuildOriginalText()
        {
            var sb = new StringBuilder();
            if (Instructions != null)
            {
                foreach (var line in Instructions)
                    sb.AppendLine(line);
            }
            return sb.ToString().TrimEnd();
        }
    }
}