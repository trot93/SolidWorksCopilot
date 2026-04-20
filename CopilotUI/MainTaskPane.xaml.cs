using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CopilotModels;
using CopilotCore;

namespace CopilotUI
{
    public partial class MainTaskPane : UserControl
    {
        private readonly IWorkspaceScanner scanner;
        private readonly AiClient aiClient;
        private readonly SessionLogger logger;
        private string currentGoal;
        private CancellationTokenSource currentCts;

        // Clarification state
        private ClarificationResponse _pendingClarification;
        private string _resolvedContext;
        private bool _clarificationSeen = false;
        private string _lastClarifiedGoal = null;
        private ClarificationResponse _cachedClarification = null;
        private Dictionary<int, string> _snapshotAnswers;

        // Step cards cache
        private readonly List<StepCard> _stepCards = new List<StepCard>();

        // ── Overlay / delegate integration ───────────────────────────────────

        public Func<string> GetGoalText { get; set; }
        public Action RequestOverlayReposition { get; set; }
        public Action RequestHostFocus { get; set; }

        // Clarification delegates
        public Action<CopilotModels.ClarificationQuestion[]> DoShowClarificationQuestions { get; set; }
        public Action HideClarificationQuestions { get; set; }
        public Func<Dictionary<int, string>> GetClarificationAnswers { get; set; }

        // Sprint 2: image delegates — set by TaskPaneManager
        public Func<string> GetAttachedImageBase64 { get; set; }
        public Func<string> GetAttachedImageMediaType { get; set; }

        // ── Events ────────────────────────────────────────────────────────────

        public event EventHandler ApiKeySetupRequested;
        public event EventHandler NewSessionRequested;

        public MainTaskPane(IWorkspaceScanner scanner, AiClient aiClient, SessionLogger logger)
        {
            InitializeComponent();
            this.scanner = scanner;
            this.aiClient = aiClient;
            this.logger = logger;

            LayoutUpdated += (s, e) => RequestOverlayReposition?.Invoke();
            NewSessionRequested += (s, e) => ResetClarificationState();
        }

        // ── Overlay position helpers ──────────────────────────────────────────

        public Rect? GetGoalInputScreenRect()
        {
            try
            {
                if (!GoalInputPlaceholder.IsVisible) return null;
                var topLeft = GoalInputPlaceholder.PointToScreen(new Point(0, 0));
                var bottomRight = GoalInputPlaceholder.PointToScreen(
                    new Point(GoalInputPlaceholder.ActualWidth, GoalInputPlaceholder.ActualHeight));
                double w = bottomRight.X - topLeft.X, h = bottomRight.Y - topLeft.Y;
                if (w < 1 || h < 1) return null;
                return new Rect(topLeft.X, topLeft.Y, w, h);
            }
            catch { return null; }
        }

        public void SetClarificationPlaceholderHeight(int height)
        {
            ClarificationPlaceholder.Height = height > 0 ? height : 0;
            UpdateLayout();
            RequestOverlayReposition?.Invoke();
        }

        public void SetGenerateButtonEnabled(bool enabled) => SubmitGoalBtn.IsEnabled = enabled;

        /// <summary>
        /// Sprint 2: shows/hides the image badge below the goal input box.
        /// Called by TaskPaneManager.OnImageAttachmentChanged.
        /// </summary>
        public void SetImageAttached(bool attached)
        {
            ImageBadge.Visibility = attached ? Visibility.Visible : Visibility.Collapsed;
            // Bust clarification cache when image changes — new context = new questions
            if (!attached || _lastClarifiedGoal != null)
            {
                _lastClarifiedGoal = null;
                _cachedClarification = null;
                _clarificationSeen = false;
            }
        }

        public void TriggerGenerateSteps() => OnGenerateStepsClick(this, new RoutedEventArgs());

        private void OnPanePreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            RequestHostFocus?.Invoke();
            if (e.OriginalSource is IInputElement el && el.Focusable)
                Keyboard.Focus(el);
        }

        public void SetStatus(string text, bool showActionButton = false)
        {
            StatusText.Text = text;
            ProgressIndicator.Visibility = Visibility.Collapsed;
            ProgressIndicator.IsIndeterminate = false;
            SetupKeyBtn.Visibility = showActionButton ? Visibility.Visible : Visibility.Collapsed;
            FixKeyBtn.Visibility = showActionButton ? Visibility.Visible : Visibility.Collapsed;

            bool isError = showActionButton || text.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
            StatusDot.Fill = isError
                ? new SolidColorBrush(Color.FromRgb(186, 117, 23))
                : new SolidColorBrush(Color.FromRgb(29, 158, 117));
        }

        private void OnClearAllClick(object sender, RoutedEventArgs e)
        {
            _stepCards.Clear();
            var toRemove = new List<UIElement>();
            foreach (UIElement child in StepListPanel.Children)
                if (child != EmptyState) toRemove.Add(child);
            foreach (var c in toRemove) StepListPanel.Children.Remove(c);

            EmptyState.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Collapsed;
            ResetClarificationState();
            SetStatus("Ready", false);
            NewSessionRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnSetupKeyClick(object sender, RoutedEventArgs e) =>
            ApiKeySetupRequested?.Invoke(this, EventArgs.Empty);

        private void OnDismissErrorClick(object sender, RoutedEventArgs e) =>
            ErrorPanel.Visibility = Visibility.Collapsed;

        private async void OnGenerateStepsClick(object sender, RoutedEventArgs e)
        {
            var goal = GetGoalText?.Invoke()?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(goal)) return;

            currentCts?.Cancel();
            currentCts = new CancellationTokenSource();
            var token = currentCts.Token;

            currentGoal = goal;
            ErrorPanel.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;

            // Sprint 2: collect image once per generation cycle
            string imageBase64 = GetAttachedImageBase64?.Invoke();
            string imageMediaType = GetAttachedImageMediaType?.Invoke();

            // ── Clarification gate ────────────────────────────────────────────
            if (!_clarificationSeen)
            {
                SetStatus("Analyzing design goal…", false);
                SetProgressVisible(true);

                ClarificationResponse clarification;
                if (_cachedClarification != null &&
                    string.Equals(_lastClarifiedGoal, goal, StringComparison.Ordinal))
                {
                    clarification = _cachedClarification;
                }
                else
                {
                    // Pass image into clarification so questions can reference it
                    clarification = await aiClient.ClarifyGoalAsync(goal, imageBase64, imageMediaType);
                    if (token.IsCancellationRequested) return;
                    _lastClarifiedGoal = goal;
                    _cachedClarification = clarification;
                }

                if (clarification != null &&
                    clarification.NeedsClarification &&
                    clarification.Questions != null &&
                    clarification.Questions.Length > 0)
                {
                    _resolvedContext = clarification.ResolvedContext;
                    SetProgressVisible(false);
                    ShowClarificationQuestions(clarification);
                    return;
                }

                _clarificationSeen = true;
            }
            // ── End clarification gate ────────────────────────────────────────

            SetStatus("Scanning workspace…", false);
            SetProgressVisible(true);

            try
            {
                var context = scanner.ScanWorkspace(currentGoal);
                if (token.IsCancellationRequested) return;

                // Sprint 2: attach image to context so GenerateStepsAsync can send it
                context.ImageBase64 = imageBase64;
                context.ImageMediaType = imageMediaType;

                string clarificationAnswers = BuildClarificationAnswersString();

                SetStatus("Waiting for AI…", false);
                var response = await aiClient.GenerateStepsAsync(context, clarificationAnswers, _resolvedContext);
                if (token.IsCancellationRequested) return;

                SetProgressVisible(false);

                if (!string.IsNullOrEmpty(response.Error))
                {
                    bool isAuthError = response.Error.Contains("401") || response.Error.Contains("Authentication");
                    SetStatus(isAuthError ? "API key error — click Fix key" : "Error generating steps", isAuthError);
                    ShowInlineError(response.Error, response.RawResponse ?? "No details available");
                    EmptyState.Visibility = Visibility.Visible;
                    return;
                }

                SetStatus("Ready", false);
                DisplaySteps(response);
                logger?.LogUserAction(currentGoal, "steps_generated", response.Confidence);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetProgressVisible(false);
                SetStatus("Error", false);
                ShowInlineError("Failed to generate steps", ex.Message);
                logger?.LogError("Step generation failed", ex);
            }
        }

        private void SetProgressVisible(bool visible)
        {
            ProgressIndicator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            ProgressIndicator.IsIndeterminate = visible;
        }

        private void DisplaySteps(AiResponse response)
        {
            _stepCards.Clear();
            var toRemove = new List<UIElement>();
            foreach (UIElement child in StepListPanel.Children)
                if (child != EmptyState) toRemove.Add(child);
            foreach (var c in toRemove) StepListPanel.Children.Remove(c);

            EmptyState.Visibility = Visibility.Collapsed;

            if (response.Steps == null || response.Steps.Length == 0)
            { EmptyState.Visibility = Visibility.Visible; return; }

            for (int i = 0; i < response.Steps.Length; i++)
            {
                var step = response.Steps[i];
                var idx = i;
                var card = new StepCard
                {
                    StepNumber = i + 1,
                    Feature = step.Feature,
                    SummaryLine = step.SummaryLine ?? BuildStepSummaryFallback(step),
                    StepRationale = step.StepRationale,
                    Instructions = step.Instructions,
                    Risk = step.Risk,
                    Confidence = i == 0 ? response.Confidence : "medium",
                    OnAccept = () => OnStepAccept(idx, step),
                    OnReject = () => OnStepReject(idx, step),
                    OnModify = mod => OnStepModify(idx, step, mod)
                };
                _stepCards.Add(card);
                StepListPanel.Children.Add(card);
            }
        }

        private string BuildStepSummaryFallback(StepData step)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(step.Plane)) parts.Add($"On {step.Plane}");
            if (step.Parameters != null)
            {
                if (step.Parameters.TryGetValue("depth_mm", out var d)) parts.Add($"{d}mm");
                else if (step.Parameters.TryGetValue("radius_mm", out var r)) parts.Add($"R{r}mm");
                else if (step.Parameters.TryGetValue("diameter_mm", out var dia)) parts.Add($"⌀{dia}mm");
                if (step.Parameters.TryGetValue("end_condition", out var ec)) parts.Add(ec?.ToString());
            }
            return string.Join(" — ", parts);
        }

        private void OnStepAccept(int index, StepData step)
        {
            logger?.LogStepDecision(currentGoal, index, step.Feature, "accepted", null);
            if (index >= 0 && index < _stepCards.Count) _stepCards[index].MarkCompleted();
        }

        private void OnStepReject(int index, StepData step)
        {
            logger?.LogStepDecision(currentGoal, index, step.Feature, "rejected", null);
            if (index >= 0 && index < _stepCards.Count) _stepCards[index].MarkRejected();
        }

        private void OnStepModify(int index, StepData step, string modification) =>
            logger?.LogStepDecision(currentGoal, index, step.Feature, "modified", modification);

        public void ShowErrorMode(string errorMessage, ErrorContext context, AiResponse response)
        {
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorMessageText.Text = errorMessage;
            ErrorDiagnosisText.Text = response?.ErrorDiagnosis ?? "No diagnosis available";
            AlternativesList.ItemsSource = response?.Alternatives;
        }

        private void ShowInlineError(string title, string details)
        {
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorMessageText.Text = title;
            ErrorDiagnosisText.Text = details;
        }

        // ── Clarification UI ──────────────────────────────────────────────────

        private void ShowClarificationQuestions(ClarificationResponse clarification)
        {
            _pendingClarification = clarification;
            ClarificationPanel.Visibility = Visibility.Visible;
            DoShowClarificationQuestions?.Invoke(clarification.Questions);
        }

        public void OnSubmitClarifyClicked()
        {
            _clarificationSeen = true;
            _lastClarifiedGoal = null;
            _cachedClarification = null;
            ClarificationPanel.Visibility = Visibility.Collapsed;
        }

        public void OnSkipClarifyClicked()
        {
            _clarificationSeen = true;
            _pendingClarification = new ClarificationResponse
            { NeedsClarification = false, SkipReason = "User skipped" };
            HideClarificationQuestions?.Invoke();
            ClarificationPanel.Visibility = Visibility.Collapsed;
            TriggerGenerateSteps();
        }

        public void SetAnswerSnapshot(Dictionary<int, string> answers) => _snapshotAnswers = answers;

        private string BuildClarificationAnswersString()
        {
            var answers = _snapshotAnswers ?? GetClarificationAnswers?.Invoke();
            _snapshotAnswers = null;
            if (answers == null || answers.Count == 0) return null;

            var parts = new List<string>();
            foreach (var kvp in answers)
            {
                var question = _pendingClarification?.Questions?.FirstOrDefault(q => q.Id == kvp.Key);
                parts.Add($"{question?.Question ?? $"Q{kvp.Key}"}: {kvp.Value}");
            }
            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }

        public Rect? GetClarificationScreenRect()
        {
            try
            {
                if (!ClarificationPlaceholder.IsVisible) return null;
                var topLeft = ClarificationPlaceholder.PointToScreen(new Point(0, 0));
                var bottomRight = ClarificationPlaceholder.PointToScreen(
                    new Point(ClarificationPlaceholder.ActualWidth, ClarificationPlaceholder.ActualHeight));
                double w = bottomRight.X - topLeft.X, h = bottomRight.Y - topLeft.Y;
                if (w < 1 || h < 1) return null;
                return new Rect(topLeft.X, topLeft.Y, w, h);
            }
            catch { return null; }
        }

        private void ResetClarificationState()
        {
            ClarificationPanel.Visibility = Visibility.Collapsed;
            ImageBadge.Visibility = Visibility.Collapsed; // Sprint 2
            _pendingClarification = null;
            _resolvedContext = null;
            _clarificationSeen = false;
            _lastClarifiedGoal = null;
            _cachedClarification = null;
            _snapshotAnswers = null;
        }
    }
}