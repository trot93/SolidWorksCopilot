// SPRINT 3 CHANGES:
// - Removed StepRationale property — no longer in model or schema.
// - Added Status (ExecutionStatus) and IsLocked properties.
// - Added OnLock callback — fires when user clicks ✓ Done.
// - Renamed OnModify → OnModify (action, no args) — fires to open FeedbackDialog in parent.
// - Renamed ModifyBtn → FeedbackBtn in handler wiring.
// - Added MarkLocked(), MarkDiscarded(), MarkFailed() status methods.
// - Added UpdateStatusUI() — single source of truth for visual state.
// - OnFeedbackClick replaces OnModifyClick — no longer opens text editor,
//   delegates to parent via OnModify callback (parent owns FeedbackDialog lifecycle).

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CopilotModels;

namespace CopilotUI
{
    public partial class StepCard : UserControl
    {
        // ── Data properties ───────────────────────────────────────────────────

        public int StepNumber { get; set; }
        public string Feature { get; set; }
        public string SummaryLine { get; set; }
        public string Confidence { get; set; }
        public string[] Instructions { get; set; }
        public Dictionary<string, object> Parameters { get; set; }

        // SPRINT 3: Execution tracking state
        public ExecutionStatus Status { get; private set; } = ExecutionStatus.Pending;
        public bool IsLocked { get; private set; } = false;

        // ── Callbacks ─────────────────────────────────────────────────────────

        // SPRINT 3: OnLock fires when user clicks ✓ Done — step was executed in SolidWorks
        public Action OnLock { get; set; }

        // SPRINT 3: OnModify fires to tell the parent to open FeedbackDialog.
        // StepCard no longer owns the dialog — parent (MainTaskPane) handles it
        // so it can wire up regeneration logic directly.
        public Action OnModify { get; set; }

        // ── Constructor ───────────────────────────────────────────────────────

        public StepCard()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            StepNumberBadge.Text = StepNumber.ToString();
            FeatureText.Text = Feature ?? "CAD Step";
            SummaryText.Text = SummaryLine ?? "Establishing geometry...";

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

        // ── Status update methods ─────────────────────────────────────────────

        /// <summary>Step accepted by user without explicit execution confirmation.</summary>
        public void MarkCompleted()
        {
            Status = ExecutionStatus.Completed;
            UpdateStatusUI();
        }

        /// <summary>
        /// SPRINT 3: User clicked ✓ Done — step was physically executed in SolidWorks.
        /// Locks the step so it cannot be changed.
        /// </summary>
        public void MarkLocked()
        {
            Status = ExecutionStatus.Completed;
            IsLocked = true;
            UpdateStatusUI();
        }

        /// <summary>SPRINT 3: User flagged this step as wrong via Feedback.</summary>
        public void MarkFailed()
        {
            Status = ExecutionStatus.Failed;
            UpdateStatusUI();
        }

        /// <summary>
        /// SPRINT 3: Step auto-invalidated because a prior step in the same batch failed.
        /// Shown dimmed — user cannot interact with it.
        /// </summary>
        public void MarkDiscarded()
        {
            Status = ExecutionStatus.Discarded;
            UpdateStatusUI();
        }

        /// <summary>Legacy — kept for compatibility with any existing callers.</summary>
        public void MarkRejected()
        {
            Status = ExecutionStatus.Discarded;
            UpdateStatusUI();
        }

        // ── Private: single source of truth for visual state ─────────────────

        private void UpdateStatusUI()
        {
            switch (Status)
            {
                case ExecutionStatus.Completed:
                    CardBorder.Background = new SolidColorBrush(Color.FromRgb(245, 250, 240));
                    CardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(159, 225, 203));
                    ShowStatusBadge(IsLocked ? "🔒 Locked" : "✓ Done",
                                    Color.FromRgb(27, 94, 32),
                                    Color.FromRgb(200, 240, 200));
                    DisableActionButtons();
                    break;

                case ExecutionStatus.Failed:
                    CardBorder.Background = new SolidColorBrush(Color.FromRgb(255, 245, 245));
                    CardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(240, 149, 149));
                    ShowStatusBadge("✗ Failed — awaiting correction",
                                    Color.FromRgb(163, 45, 45),
                                    Color.FromRgb(255, 228, 228));
                    // Keep Feedback button enabled so user can submit correction
                    LockBtn.IsEnabled = false;
                    break;

                case ExecutionStatus.Discarded:
                    CardBorder.Background = new SolidColorBrush(Color.FromRgb(241, 239, 232));
                    CardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(218, 218, 213));
                    Opacity = 0.5;
                    ShowStatusBadge("— Discarded",
                                    Color.FromRgb(100, 100, 96),
                                    Color.FromRgb(230, 230, 225));
                    DisableActionButtons();
                    break;

                default: // Pending — initial state, no badge shown
                    StatusBadge.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void ShowStatusBadge(string text, Color foreground, Color background)
        {
            StatusText.Text = text;
            StatusText.Foreground = new SolidColorBrush(foreground);
            StatusBadge.Background = new SolidColorBrush(background);
            StatusBadge.Visibility = Visibility.Visible;
            StatusIndicator.Visibility = Visibility.Collapsed; // legacy — hide old inline indicator
        }

        private void DisableActionButtons()
        {
            LockBtn.IsEnabled = false;
            FeedbackBtn.IsEnabled = false;
        }

        // ── Confidence badge ──────────────────────────────────────────────────

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

        // ── Button event handlers ─────────────────────────────────────────────

        // SPRINT 3: ✓ Done — user confirms they physically executed this step in SolidWorks.
        private void OnLockClick(object sender, RoutedEventArgs e)
        {
            OnLock?.Invoke();
        }

        // SPRINT 3: Feedback button replaces Modify.
        // StepCard no longer opens any dialog — delegates entirely to the parent.
        // Parent (MainTaskPane) owns FeedbackDialog so it can wire regeneration directly.
        private void OnFeedbackClick(object sender, RoutedEventArgs e)
        {
            OnModify?.Invoke();
        }
    }
}