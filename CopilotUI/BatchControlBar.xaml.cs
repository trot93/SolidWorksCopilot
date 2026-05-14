// SPRINT 3: New component — replaces the "Generate Steps" button after the first batch.
// Sits below the current batch of StepCards and drives the rolling window loop.
// MainTaskPane owns this control and wires MarkDoneAndNextRequested / RetryRequested callbacks.

using System;
using System.Windows;
using System.Windows.Controls;

namespace CopilotUI
{
    public partial class BatchControlBar : UserControl
    {
        // ── Callbacks wired by MainTaskPane ───────────────────────────────────
        // Named *Requested to avoid collision with the XAML Click handler methods.

        /// <summary>Fired when user clicks "Mark Both Done &amp; Generate Next Steps".</summary>
        public Action MarkDoneAndNextRequested { get; set; }

        /// <summary>Fired when user clicks Retry after a batch error.</summary>
        public Action RetryRequested { get; set; }

        // ── Constructor ───────────────────────────────────────────────────────

        public BatchControlBar()
        {
            InitializeComponent();
        }

        // ── Public state setters — called by MainTaskPane ─────────────────────

        /// <summary>
        /// Updates the progress bar and label.
        /// Call after each batch is confirmed complete.
        /// </summary>
        /// <param name="currentStep">First step of the NEXT batch (1-based).</param>
        /// <param name="totalSteps">Total steps in the feature_sequence.</param>
        public void SetProgress(int currentStep, int totalSteps)
        {
            if (totalSteps <= 0) return;

            int batchEnd = Math.Min(currentStep + 1, totalSteps);
            ProgressLabel.Text = $"Steps {currentStep}–{batchEnd} of {totalSteps}";

            // Defer width calculation until layout pass so ActualWidth is valid
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var track = ProgressFill.Parent as Border;
                if (track == null) return;
                double pct = Math.Min((double)(currentStep - 1) / totalSteps, 1.0);
                ProgressFill.Width = track.ActualWidth * pct;
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>Shows the "Mark Both Done" button — normal pending state.</summary>
        public void ShowPending()
        {
            MarkDoneAndNextBtn.Visibility = Visibility.Visible;
            MarkDoneAndNextBtn.IsEnabled = true;
            GeneratingPanel.Visibility = Visibility.Collapsed;
            CompletePanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>Collapses button and shows spinner — AI call in flight.</summary>
        public void ShowGenerating()
        {
            MarkDoneAndNextBtn.Visibility = Visibility.Collapsed;
            GeneratingPanel.Visibility = Visibility.Visible;
            CompletePanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>Hides all action UI and shows the completion banner.</summary>
        public void ShowComplete()
        {
            MarkDoneAndNextBtn.Visibility = Visibility.Collapsed;
            GeneratingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            CompletePanel.Visibility = Visibility.Visible;

            ProgressLabel.Text = "All steps complete";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var track = ProgressFill.Parent as Border;
                if (track != null) ProgressFill.Width = track.ActualWidth;
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>Shows error state with message and Retry button.</summary>
        public void ShowError(string message)
        {
            MarkDoneAndNextBtn.Visibility = Visibility.Collapsed;
            GeneratingPanel.Visibility = Visibility.Collapsed;
            CompletePanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = message ?? "Batch generation failed — please retry.";
        }

        // ── XAML Click handlers — delegate to Action callbacks ────────────────

        private void OnMarkDoneAndNextClick(object sender, RoutedEventArgs e)
        {
            ShowGenerating();
            MarkDoneAndNextRequested?.Invoke();
        }

        private void OnRetryClick(object sender, RoutedEventArgs e)
        {
            ShowGenerating();
            RetryRequested?.Invoke();
        }
    }
}