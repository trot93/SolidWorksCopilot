// SPRINT 3 CHANGES:
// - Added rolling window state fields.
// - OnGenerateStepsClick: extracts feature_sequence, bootstraps rolling window.
// - Added GenerateNextBatch(), OnMarkDoneAndNextHandler(), OnStepFeedbackSubmitted().
// - Added DisplayBatch(), RefreshBatchDisplay(), and all session helpers.
// - BatchControlBar wired in constructor.
// - StepCard wiring: removed OnAccept/StepRationale, added OnLock.
// - FeedbackDialog owned here — StepCard just fires OnModify callback.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinForms = System.Windows.Forms;
using System.Windows.Media;
using CopilotModels;
using CopilotCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CopilotUI
{
    public partial class MainTaskPane : UserControl
    {
        // ── Core dependencies ─────────────────────────────────────────────────

        private readonly IWorkspaceScanner scanner;
        private readonly AiClient aiClient;
        private readonly SessionLogger logger;
        private string currentGoal;
        private CancellationTokenSource currentCts;

        // ── Clarification state ───────────────────────────────────────────────

        private ClarificationResponse _pendingClarification;
        private string _resolvedContext;
        private bool _clarificationSeen = false;
        private string _lastClarifiedGoal = null;
        private ClarificationResponse _cachedClarification = null;
        private Dictionary<int, string> _snapshotAnswers;

        // ── Image context state ───────────────────────────────────────────────

        private string _imageContext = null;
        private string _lastImageBase64Hash = null;

        // ── Step card cache ───────────────────────────────────────────────────

        private readonly List<StepCard> _stepCards = new List<StepCard>();

        // ── SPRINT 3: Rolling window state ────────────────────────────────────

        private RollingWindowState _rollingWindowState;
        private string[] _remainingSequence;
        // SPRINT 3: Set when rolling window is active. Used as a guard for future batch-mode-only logic.
#pragma warning disable CS0414
        private bool _isIterativeMode = false;
#pragma warning restore CS0414
        private int _expectedFeatureCountAfterBatch = 0;
        private List<StepData> _currentBatchStepData = new List<StepData>();

        // ── Overlay / delegate integration ───────────────────────────────────

        public Func<string> GetGoalText { get; set; }
        public Action RequestOverlayReposition { get; set; }
        public Action RequestHostFocus { get; set; }

        public Action<CopilotModels.ClarificationQuestion[]> DoShowClarificationQuestions { get; set; }
        public Action HideClarificationQuestions { get; set; }
        public Func<Dictionary<int, string>> GetClarificationAnswers { get; set; }

        public Func<string> GetAttachedImageBase64 { get; set; }
        public Func<string> GetAttachedImageMediaType { get; set; }

        // ── Events ────────────────────────────────────────────────────────────

        public event EventHandler ApiKeySetupRequested;
        public event EventHandler NewSessionRequested;

        // ── Constructor ───────────────────────────────────────────────────────

        public MainTaskPane(IWorkspaceScanner scanner, AiClient aiClient, SessionLogger logger)
        {
            InitializeComponent();
            this.scanner = scanner;
            this.aiClient = aiClient;
            this.logger = logger;

            LayoutUpdated += (s, e) => RequestOverlayReposition?.Invoke();
            NewSessionRequested += (s, e) => ResetClarificationState();

            // SPRINT 3: Wire BatchControlBar callbacks
            BatchBar.MarkDoneAndNextRequested = async () => await OnMarkDoneAndNextHandler();
            BatchBar.RetryRequested = async () => await GenerateNextBatch();
        }

        // ── Overlay helpers ───────────────────────────────────────────────────

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

        public void SetImageAttached(bool attached)
        {
            ImageBadge.Visibility = attached ? Visibility.Visible : Visibility.Collapsed;
            if (!attached || _lastClarifiedGoal != null)
            {
                _lastClarifiedGoal = null;
                _cachedClarification = null;
                _clarificationSeen = false;
                _imageContext = null;
                _lastImageBase64Hash = null;
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

        private void SetProgressVisible(bool visible)
        {
            ProgressIndicator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            ProgressIndicator.IsIndeterminate = visible;
        }

        // ── Clear / Reset ─────────────────────────────────────────────────────

        private void OnClearAllClick(object sender, RoutedEventArgs e)
        {
            ClearStepCards();
            ErrorPanel.Visibility = Visibility.Collapsed;
            BatchBar.Visibility = Visibility.Collapsed;

            // SPRINT 3: Reset rolling window state
            _rollingWindowState = null;
            _remainingSequence = null;
            _isIterativeMode = false;
            _expectedFeatureCountAfterBatch = 0;
            _currentBatchStepData = new List<StepData>();

            ResetClarificationState();
            SetStatus("Ready", false);
            NewSessionRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnSetupKeyClick(object sender, RoutedEventArgs e) =>
            ApiKeySetupRequested?.Invoke(this, EventArgs.Empty);

        private void OnDismissErrorClick(object sender, RoutedEventArgs e) =>
            ErrorPanel.Visibility = Visibility.Collapsed;

        // ── Main generation entry point ───────────────────────────────────────

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

            string imageBase64 = GetAttachedImageBase64?.Invoke();
            string imageMediaType = GetAttachedImageMediaType?.Invoke();
            bool hasImage = !string.IsNullOrEmpty(imageBase64);

            // ── STEP 0: Image extraction — runs once per unique image ────────
            if (hasImage)
            {
                string imageHash = imageBase64.Length + "_" +
                    imageBase64.Substring(0, Math.Min(32, imageBase64.Length));

                if (_imageContext == null || _lastImageBase64Hash != imageHash)
                {
                    // Only show status when actually extracting — not on cache hit
                    SetStatus("Analysing image…", false);
                    SetProgressVisible(true);

                    _imageContext = await aiClient.ExtractImageContextAsync(
                        goal, imageBase64, imageMediaType);

                    if (token.IsCancellationRequested) return;
                    _lastImageBase64Hash = imageHash;
                    logger?.LogApiResponse("ImageExtract", _imageContext ?? "no useful info extracted");

                    // Image just extracted — bust clarification cache so it re-runs
                    // with fresh image context (prevents "image missing" questions)
                    _lastClarifiedGoal = null;
                    _cachedClarification = null;
                }
                // else: cache hit — skip silently, no status change
            }
            else
            {
                _imageContext = null;
                _lastImageBase64Hash = null;
            }

            // ── STEP 1: Clarification gate ────────────────────────────────────
            if (!_clarificationSeen)
            {
                SetStatus("Analyzing design goal…", false);
                SetProgressVisible(true);

                ClarificationResponse clarification;

                // Cache is valid only when goal unchanged AND cache exists.
                // Cache is busted by image extraction above when a new image is processed.
                if (_cachedClarification != null &&
                    string.Equals(_lastClarifiedGoal, goal, StringComparison.Ordinal))
                {
                    clarification = _cachedClarification;
                }
                else
                {
                    clarification = await aiClient.ClarifyGoalAsync(
                        goal,
                        imageBase64: null,
                        imageMediaType: null,
                        imageContext: _imageContext);

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

            // ── STEP 2: Workspace scan ────────────────────────────────────────
            SetStatus("Scanning workspace…", false);
            SetProgressVisible(true);

            try
            {
                var context = scanner.ScanWorkspace(currentGoal);
                if (token.IsCancellationRequested) return;

                context.ImageBase64 = imageBase64;
                context.ImageMediaType = imageMediaType;

                string clarificationAnswers = BuildClarificationAnswersString();

                SetStatus("Waiting for AI…", false);

                var response = await aiClient.GenerateStepsAsync(
                    context,
                    clarificationAnswers,
                    _resolvedContext,
                    _imageContext);

                if (token.IsCancellationRequested) return;
                SetProgressVisible(false);

                if (!string.IsNullOrEmpty(response.Error))
                {
                    bool isAuthError = response.Error.Contains("401") ||
                                       response.Error.Contains("Authentication");
                    SetStatus(isAuthError ? "API key error — click Fix key" : "Error generating steps", isAuthError);
                    ShowInlineError(response.Error, response.RawResponse ?? "No details available");
                    EmptyState.Visibility = Visibility.Visible;
                    return;
                }

                // SPRINT 3: Bootstrap rolling window.
                // Use response.GeometryLockJson directly — avoids shared-logger
                // instance ambiguity that caused "No active session" failures.
                string geometryLockJson = response.GeometryLockJson
                                       ?? logger?.CurrentSession?.GeometryLockJson;

                if (!string.IsNullOrEmpty(geometryLockJson))
                {
                    try
                    {
                        var lockObj = JObject.Parse(geometryLockJson);
                        var seqToken = lockObj["feature_sequence"];

                        if (seqToken != null && seqToken.HasValues)
                        {
                            _remainingSequence = seqToken
                                .Select(f => f["brief"]?.ToString() ?? f["feature"]?.ToString() ?? "Step")
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToArray();
                        }
                        else if (response.Steps != null && response.Steps.Length > 0)
                        {
                            _remainingSequence = response.Steps
                                .Select(s => s.SummaryLine ?? s.Feature ?? "Step")
                                .ToArray();
                            logger?.LogError("feature_sequence missing — built from response.Steps", null);
                        }

                        if (_remainingSequence != null && _remainingSequence.Length > 0)
                        {
                            // Build rolling window state directly — no logger dependency.
                            // Logger is optional persistence only; session state lives here.
                            _rollingWindowState = new RollingWindowState
                            {
                                GeometryLockJson = geometryLockJson,
                                CompletedSteps = new System.Collections.Generic.List<StepData>(),
                                CurrentBatchIndex = 0,
                                LastScanResultJson = null,
                                IsComplete = false
                            };
                            _isIterativeMode = true;

                            // Sync to logger for persistence if available
                            if (logger?.CurrentSession == null)
                                logger?.InitialiseSession(geometryLockJson);

                            ClearStepCards();
                            BatchBar.Visibility = Visibility.Visible;
                            await GenerateNextBatch();
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError("Failed to parse feature_sequence — falling back to full display", ex);
                    }
                }

                // Fallback: no geometry lock or empty sequence — display all steps at once
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

        // ── SPRINT 3: Rolling window ──────────────────────────────────────────

        private async Task GenerateNextBatch()
        {
            if (_remainingSequence == null || _remainingSequence.Length == 0)
            {
                ShowCompletionState();
                return;
            }

            var nextTwo = _remainingSequence.Take(2).ToArray();

            SetStatus("Generating next steps…", false);
            SetProgressVisible(true);

            var response = await aiClient.GenerateBatchAsync(_rollingWindowState, nextTwo);

            SetProgressVisible(false);

            if (!string.IsNullOrEmpty(response.Error))
            {
                BatchBar.ShowError(response.Error);
                ShowInlineError("Batch generation failed", response.Error);
                return;
            }

            DisplayBatch(response, nextTwo);
        }

        private async Task OnMarkDoneAndNextHandler()
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            SetStatus("Scanning workspace…", false);
            SetProgressVisible(true);

            var scanResult = scanner.ScanWorkspace(currentGoal);
            string scanJson = SerializeScanResult(scanResult);

            int actualCount = scanner.GetFeatureCount();
            if (actualCount < _expectedFeatureCountAfterBatch)
            {
                BatchBar.ShowPending();
                SetProgressVisible(false);
                ShowInlineError(
                    "Steps may not have been executed",
                    $"Expected at least {_expectedFeatureCountAfterBatch} features, found {actualCount}. " +
                    "Please complete both steps in SolidWorks before continuing.");
                return;
            }

            var completedSteps = GetCurrentBatchSteps();
            foreach (var s in completedSteps)
                s.Status = ExecutionStatus.Completed;

            for (int i = 0; i < _stepCards.Count; i++)
                _stepCards[i].MarkCompleted();

            // Update state directly — logger is optional persistence only
            _rollingWindowState.CompletedSteps.AddRange(completedSteps);
            _rollingWindowState.LastScanResultJson = scanJson;
            _rollingWindowState.CurrentBatchIndex++;
            logger?.ArchiveBatch(completedSteps, scanJson);

            _remainingSequence = _remainingSequence.Skip(completedSteps.Count).ToArray();

            int totalSteps = _rollingWindowState.CompletedSteps.Count + _remainingSequence.Length;
            int currentStart = _rollingWindowState.CompletedSteps.Count + 1;
            BatchBar.SetProgress(currentStart, totalSteps);

            SetProgressVisible(false);
            await GenerateNextBatch();
        }

        private async void OnStepFeedbackSubmitted(int stepIndex, string feedback)
        {
            bool isFirstStep = stepIndex == 0;

            ErrorPanel.Visibility = Visibility.Collapsed;

            // CRASH GUARD: rolling window must be active before feedback can regenerate
            if (_rollingWindowState == null || _remainingSequence == null || _remainingSequence.Length == 0)
            {
                ShowInlineError("Cannot regenerate", "No active session — please generate steps first.");
                return;
            }

            if (isFirstStep)
                InvalidateStep(1);

            var scanResult = scanner.ScanWorkspace(currentGoal);
            string scanJson = SerializeScanResult(scanResult);
            _rollingWindowState.LastScanResultJson = scanJson;
            logger?.UpdateScanResult(scanJson);

            SetStatus("Regenerating…", false);
            SetProgressVisible(true);
            BatchBar.ShowGenerating();

            AiResponse response;

            if (isFirstStep)
            {
                response = await aiClient.GenerateBatchAsync(
                    _rollingWindowState,
                    _remainingSequence.Take(2).ToArray(),
                    userFeedback: feedback,
                    regenerateBothSteps: true);
            }
            else
            {
                response = await aiClient.GenerateBatchAsync(
                    _rollingWindowState,
                    new[] { _remainingSequence.Length > 1 ? _remainingSequence[1] : _remainingSequence[0] },
                    userFeedback: feedback,
                    regenerateBothSteps: false);
            }

            SetProgressVisible(false);

            if (!string.IsNullOrEmpty(response.Error))
            {
                BatchBar.ShowPending();
                ShowInlineError("Regeneration failed", response.Error);
                return;
            }

            RefreshBatchDisplay(response, stepIndex, isFirstStep);
        }

        // ── SPRINT 3: Display helpers ─────────────────────────────────────────

        private void DisplayBatch(AiResponse response, string[] batchSequence)
        {
            ClearStepCards();

            if (response.Steps == null || response.Steps.Length == 0) return;

            _currentBatchStepData = response.Steps.ToList();
            _expectedFeatureCountAfterBatch = scanner.GetFeatureCount() + response.Steps.Length;

            for (int i = 0; i < response.Steps.Length; i++)
            {
                var step = response.Steps[i];
                var idx = i;

                var card = new StepCard
                {
                    StepNumber = (_rollingWindowState?.CompletedSteps?.Count ?? 0) + i + 1,
                    Feature = step.Feature,
                    SummaryLine = step.SummaryLine ?? BuildStepSummaryFallback(step),
                    Instructions = step.Instructions,
                    Confidence = step.Confidence ?? response.Confidence ?? "medium",
                    OnLock = () => OnStepLock(idx, step),
                    OnModify = () => OnStepFeedbackOpen(idx, step)
                };

                _stepCards.Add(card);
                StepListPanel.Children.Add(card);
            }

            int totalSteps = (_rollingWindowState?.CompletedSteps?.Count ?? 0) + _remainingSequence.Length;
            int currentStart = (_rollingWindowState?.CompletedSteps?.Count ?? 0) + 1;
            BatchBar.SetProgress(currentStart, totalSteps);
            BatchBar.ShowPending();
            BatchBar.Visibility = Visibility.Visible;

            SetStatus("Ready", false);
        }

        private void RefreshBatchDisplay(AiResponse response, int stepIndex, bool replaceBoth)
        {
            if (response.Steps == null || response.Steps.Length == 0) return;

            if (replaceBoth)
            {
                DisplayBatch(response, _remainingSequence.Take(2).ToArray());
                return;
            }

            int cardIndex = stepIndex;
            if (cardIndex < 0 || cardIndex >= _stepCards.Count) return;

            var newStep = response.Steps[0];
            var oldCard = _stepCards[cardIndex];

            int panelIndex = GetStepCardPanelIndex(cardIndex);
            StepListPanel.Children.Remove(oldCard);

            var newCard = new StepCard
            {
                StepNumber = oldCard.StepNumber,
                Feature = newStep.Feature,
                SummaryLine = newStep.SummaryLine ?? BuildStepSummaryFallback(newStep),
                Instructions = newStep.Instructions,
                Confidence = "medium",
                OnLock = () => OnStepLock(cardIndex, newStep),
                OnModify = () => OnStepFeedbackOpen(cardIndex, newStep)
            };

            if (panelIndex >= 0)
                StepListPanel.Children.Insert(panelIndex, newCard);
            else
                StepListPanel.Children.Add(newCard);

            _stepCards[cardIndex] = newCard;
            _currentBatchStepData[cardIndex] = newStep;

            BatchBar.ShowPending();
        }

        // ── Step interaction handlers ─────────────────────────────────────────

        private void OnStepLock(int index, StepData step)
        {
            logger?.LogStepDecision(currentGoal, index, step.Feature, "locked", null);
            if (index >= 0 && index < _stepCards.Count)
            {
                _stepCards[index].MarkLocked();
                if (index < _currentBatchStepData.Count)
                    _currentBatchStepData[index].IsLocked = true;
            }
        }

        private void OnStepFeedbackOpen(int index, StepData step)
        {
            // FeedbackDialog is WinForms Form — safe in SolidWorks ElementHost context.
            // Do NOT wrap in Dispatcher.Invoke — async void inside Invoke fires and
            // returns immediately, losing the thread context for _rollingWindowState.
            var dialog = new FeedbackDialog(step.SummaryLine ?? step.Feature);
            var result = dialog.ShowDialog();

            if (result == WinForms.DialogResult.OK &&
                !string.IsNullOrWhiteSpace(dialog.FeedbackText))
            {
                var feedback = dialog.FeedbackText;

                if (index >= 0 && index < _stepCards.Count)
                    _stepCards[index].MarkFailed();

                logger?.LogStepDecision(currentGoal, index, step.Feature, "feedback_submitted", feedback);
                OnStepFeedbackSubmitted(index, feedback);
            }
        }

        // ── Legacy full-display (non-iterative fallback) ──────────────────────

        private void DisplaySteps(AiResponse response)
        {
            ClearStepCards();

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
                    Instructions = step.Instructions,
                    Confidence = i == 0 ? response.Confidence : "medium",
                    OnLock = () => OnStepLock(idx, step),
                    OnModify = () => OnStepFeedbackOpen(idx, step)
                };
                _stepCards.Add(card);
                StepListPanel.Children.Add(card);
            }
        }

        // ── Session helpers ───────────────────────────────────────────────────

        private List<StepData> GetCurrentBatchSteps() =>
            new List<StepData>(_currentBatchStepData);

        private void InvalidateStep(int cardIndex)
        {
            if (cardIndex >= 0 && cardIndex < _stepCards.Count)
                _stepCards[cardIndex].MarkDiscarded();
        }

        private string SerializeScanResult(WorkspaceContext context)
        {
            try
            {
                return logger?.CurrentSession?.LastScanResultJson
                       ?? JsonConvert.SerializeObject(new
                       {
                           feature_count = context.Features?.Count ?? 0,
                           features = context.Features?.Select(f => new { name = f.Name, type = f.Type })
                       });
            }
            catch { return "{\"feature_count\":0,\"features\":[]}"; }
        }

        private void ShowCompletionState()
        {
            BatchBar.ShowComplete();
            SetStatus("Design complete ✓", false);
            if (_rollingWindowState != null)
                _rollingWindowState.IsComplete = true;

            logger?.LogUserAction(currentGoal, "session_complete",
                $"batches={_rollingWindowState?.CurrentBatchIndex}");
        }

        private void ClearStepCards()
        {
            _stepCards.Clear();
            var toRemove = new List<UIElement>();
            foreach (UIElement child in StepListPanel.Children)
                if (child != EmptyState) toRemove.Add(child);
            foreach (var c in toRemove) StepListPanel.Children.Remove(c);
            EmptyState.Visibility = Visibility.Collapsed;
        }

        private int GetStepCardPanelIndex(int cardIndex)
        {
            if (cardIndex < 0 || cardIndex >= _stepCards.Count) return -1;
            var card = _stepCards[cardIndex];
            for (int i = 0; i < StepListPanel.Children.Count; i++)
                if (StepListPanel.Children[i] == card) return i;
            return -1;
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
            ImageBadge.Visibility = Visibility.Collapsed;
            _pendingClarification = null;
            _resolvedContext = null;
            _clarificationSeen = false;
            _lastClarifiedGoal = null;
            _cachedClarification = null;
            _snapshotAnswers = null;
            _imageContext = null;
            _lastImageBase64Hash = null;
        }
    }
}