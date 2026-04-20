using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using SolidWorks.Interop.sldworks;
using SolidWorksTools;
using CopilotModels;
using CopilotCore;

namespace CopilotAddIn
{
    public class TaskPaneManager
    {
        private ITaskpaneView taskPaneView;
        private readonly ISldWorks swApp;
        private readonly int addInId;
        private readonly IWorkspaceScanner scanner;
        private readonly AiClient aiClient;
        private readonly SessionLogger logger;
        private CopilotUI.MainTaskPane wpfPane;
        private System.Windows.Forms.UserControl container;
        private ElementHost host;
        private PaneSizePoller poller;
        private GoalInputOverlay goalOverlay;
        private ClarificationInputOverlay clarifyOverlay;

        public Action OnOpenSettings { get; set; }

        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "SW_Copilot_overlay.log");
        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + "\n"); }
            catch { }
        }

        public TaskPaneManager(ISldWorks app, int id,
                               IWorkspaceScanner scan, AiClient client, SessionLogger log)
        {
            swApp = app; addInId = id; scanner = scan; aiClient = client; logger = log;
        }

        public void CreateTaskPane()
        {
            wpfPane = new CopilotUI.MainTaskPane(scanner, aiClient, logger);
            wpfPane.ApiKeySetupRequested += (s, e) => OnOpenSettings?.Invoke();
            wpfPane.NewSessionRequested += (s, e) =>
            {
                if (goalOverlay != null) goalOverlay.GoalText = string.Empty;
            };

            host = new ElementHost { Dock = DockStyle.Fill, Child = wpfPane };
            container = new System.Windows.Forms.UserControl();
            container.Controls.Add(host);
            var _hnd = container.Handle;

            string addinDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string icon16 = Path.Combine(addinDir, "copilot_icon.bmp");

            taskPaneView = (ITaskpaneView)swApp.CreateTaskpaneView2(
                File.Exists(icon16) ? icon16 : null, "AI Copilot");
            taskPaneView.DisplayWindowFromHandle(container.Handle.ToInt32());

            goalOverlay = new GoalInputOverlay();
            goalOverlay.GoalTextChanged += OnOverlayTextChanged;
            goalOverlay.SubmitRequested += OnOverlaySubmitRequested;
            goalOverlay.ImageAttachmentChanged += OnImageAttachmentChanged; // Sprint 2

            wpfPane.GetGoalText = () => goalOverlay.GoalText;
            wpfPane.RequestOverlayReposition = RepositionOverlay;

            // Clarification delegates
            wpfPane.DoShowClarificationQuestions = ShowClarificationOverlay;
            wpfPane.HideClarificationQuestions = HideClarificationOverlay;
            wpfPane.GetClarificationAnswers = GetClarificationAnswers;

            // Sprint 2: image delegates
            wpfPane.GetAttachedImageBase64 = () => goalOverlay?.AttachedImageBase64;
            wpfPane.GetAttachedImageMediaType = () => goalOverlay?.AttachedImageMediaType;

            poller = new PaneSizePoller(container, () =>
            {
                wpfPane?.Dispatcher.Invoke(
                    () => { wpfPane.InvalidateMeasure(); wpfPane.UpdateLayout(); },
                    System.Windows.Threading.DispatcherPriority.Render);
                wpfPane?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RepositionOverlay();
                    RepositionClarifyOverlay();
                }), System.Windows.Threading.DispatcherPriority.Background);
            });
            poller.Start();

            var attachTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            attachTimer.Tick += (ts, te) => { attachTimer.Stop(); attachTimer.Dispose(); AttachOverlayToContainer(); };
            attachTimer.Start();
        }

        private void AttachOverlayToContainer()
        {
            try
            {
                Log("AttachOverlayToContainer: start");
                goalOverlay.Show();
                SetParent(goalOverlay.Handle, container.Handle);
                RepositionOverlay();
                Log("  done. bounds=" + goalOverlay.Left + "," + goalOverlay.Top + " " + goalOverlay.Width + "x" + goalOverlay.Height);
            }
            catch (Exception ex) { Log("  EXCEPTION: " + ex.Message); }
        }

        private void RepositionOverlay()
        {
            try
            {
                if (wpfPane == null || goalOverlay == null || !container.IsHandleCreated) return;

                System.Windows.Rect? screenRect = null;
                wpfPane.Dispatcher.Invoke(() => screenRect = wpfPane.GetGoalInputScreenRect(),
                    System.Windows.Threading.DispatcherPriority.Render);

                if (screenRect == null || screenRect.Value.IsEmpty || screenRect.Value.Width < 10) return;

                var r = screenRect.Value;
                var topLeft = new POINT { X = (int)r.Left, Y = (int)r.Top };
                ScreenToClient(container.Handle, ref topLeft);
                int w = (int)r.Width, h = (int)r.Height;

                if (container.InvokeRequired)
                    container.Invoke(new Action(() => goalOverlay.SetBounds(topLeft.X, topLeft.Y, w, h)));
                else
                    goalOverlay.SetBounds(topLeft.X, topLeft.Y, w, h);
            }
            catch (Exception ex) { Log("  RepositionOverlay EXCEPTION: " + ex.Message); }
        }

        private void OnOverlayTextChanged(object sender, EventArgs e)
        {
            bool hasText = !string.IsNullOrWhiteSpace(goalOverlay.GoalText);
            wpfPane?.Dispatcher.BeginInvoke(new Action(() => wpfPane.SetGenerateButtonEnabled(hasText)),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnOverlaySubmitRequested(object sender, EventArgs e)
        {
            wpfPane?.Dispatcher.BeginInvoke(new Action(() => wpfPane.TriggerGenerateSteps()),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        // Sprint 2: notify WPF badge when image attached/cleared
        private void OnImageAttachmentChanged(object sender, EventArgs e)
        {
            bool hasImage = !string.IsNullOrEmpty(goalOverlay?.AttachedImageBase64);
            wpfPane?.Dispatcher.BeginInvoke(new Action(() => wpfPane.SetImageAttached(hasImage)),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        public void WireHostFocusToPane() { }

        // ── Clarification overlay ─────────────────────────────────────────────

        public void ShowClarificationOverlay(ClarificationQuestion[] questions)
        {
            try
            {
                if (clarifyOverlay != null && !clarifyOverlay.IsDisposed) clarifyOverlay.Dispose();

                clarifyOverlay = new ClarificationInputOverlay(questions);
                clarifyOverlay.AnswersSubmitted += OnClarifyAnswersSubmitted;
                clarifyOverlay.Skipped += OnClarifySkipped;

                int preferredH = clarifyOverlay.PreferredHeight;
                int maxH = Math.Max(container.Height - 150, 200);
                int h = Math.Min(preferredH, maxH);

                clarifyOverlay.Size = new System.Drawing.Size(container.Width, h);
                clarifyOverlay.AutoScroll = preferredH > h;

                wpfPane?.Dispatcher.Invoke(() => wpfPane.SetClarificationPlaceholderHeight(h),
                    System.Windows.Threading.DispatcherPriority.Render);

                clarifyOverlay.Show();
                SetParent(clarifyOverlay.Handle, container.Handle);
                clarifyOverlay.BringToFront();
                RepositionClarifyOverlay();

                Log("  Clarification overlay shown q=" + questions.Length + " h=" + h);
            }
            catch (Exception ex) { Log("  ShowClarificationOverlay EXCEPTION: " + ex.Message); }
        }

        public void HideClarificationOverlay()
        {
            try
            {
                if (clarifyOverlay != null && !clarifyOverlay.IsDisposed)
                {
                    clarifyOverlay.Hide(); clarifyOverlay.Dispose(); clarifyOverlay = null;
                    Log("  Clarification overlay hidden");
                }
            }
            catch (Exception ex) { Log("  HideClarificationOverlay EXCEPTION: " + ex.Message); }
        }

        private void RepositionClarifyOverlay()
        {
            try
            {
                if (wpfPane == null || clarifyOverlay == null || !container.IsHandleCreated) return;
                if (clarifyOverlay.IsDisposed) return;

                System.Windows.Rect? screenRect = null;
                wpfPane.Dispatcher.Invoke(() => screenRect = wpfPane.GetClarificationScreenRect(),
                    System.Windows.Threading.DispatcherPriority.Render);

                if (screenRect == null || screenRect.Value.IsEmpty || screenRect.Value.Width < 10) return;

                var r = screenRect.Value;
                var topLeft = new POINT { X = (int)r.Left, Y = (int)r.Top };
                ScreenToClient(container.Handle, ref topLeft);
                int w = container.Width, h = clarifyOverlay.Height;

                if (container.InvokeRequired)
                    container.Invoke(new Action(() => { clarifyOverlay.SetBounds(0, topLeft.Y, w, h); clarifyOverlay.BringToFront(); }));
                else
                { clarifyOverlay.SetBounds(0, topLeft.Y, w, h); clarifyOverlay.BringToFront(); }
            }
            catch (Exception ex) { Log("  RepositionClarifyOverlay EXCEPTION: " + ex.Message); }
        }

        public Dictionary<int, string> GetClarificationAnswers()
        {
            try { return clarifyOverlay?.GetAnswers() ?? new Dictionary<int, string>(); }
            catch { return new Dictionary<int, string>(); }
        }

        private void OnClarifyAnswersSubmitted(object sender, EventArgs e)
        {
            var answers = clarifyOverlay?.GetAnswers() ?? new Dictionary<int, string>();
            wpfPane?.Dispatcher.BeginInvoke(new Action(() =>
            {
                wpfPane.SetAnswerSnapshot(answers);
                wpfPane.OnSubmitClarifyClicked();
                HideClarificationOverlay();
                wpfPane.SetClarificationPlaceholderHeight(0);
                wpfPane.TriggerGenerateSteps();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnClarifySkipped(object sender, EventArgs e)
        {
            wpfPane?.Dispatcher.BeginInvoke(new Action(() => wpfPane.OnSkipClarifyClicked()),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        public void SetStatus(string text, bool showActionButton)
            => wpfPane?.Dispatcher.Invoke(() => wpfPane.SetStatus(text, showActionButton));

        public void NotifyApiKeyReady()
            => wpfPane?.Dispatcher.Invoke(() => wpfPane.SetStatus("Ready — enter a design goal to begin.", false));

        public void ShowApiKeyMissingState()
            => wpfPane?.Dispatcher.Invoke(() => wpfPane.SetStatus("API key not set — click ⚙ to configure.", true));

        public void ShowApiKeyInvalidState(string reason)
            => wpfPane?.Dispatcher.Invoke(() => wpfPane.SetStatus("API key invalid — click ⚙ to fix. (" + reason + ")", true));

        public void DestroyTaskPane()
        {
            poller?.Stop();
            if (goalOverlay != null && !goalOverlay.IsDisposed)
            {
                if (goalOverlay.InvokeRequired) goalOverlay.Invoke(new Action(() => goalOverlay.Dispose()));
                else goalOverlay.Dispose();
                goalOverlay = null;
            }
            HideClarificationOverlay();
            taskPaneView?.DeleteView();
        }

        private class PaneSizePoller
        {
            [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hWnd);
            [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT r);
            [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int W, int H, bool repaint);
            [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

            private readonly System.Windows.Forms.UserControl container;
            private readonly Action onResized;
            private readonly System.Windows.Forms.Timer timer;
            private int lastW = -1, lastH = -1;

            public PaneSizePoller(System.Windows.Forms.UserControl ctrl, Action cb)
            { container = ctrl; onResized = cb; timer = new System.Windows.Forms.Timer { Interval = 200 }; timer.Tick += OnTick; }

            public void Start() => timer.Start();
            public void Stop() => timer.Stop();

            private void OnTick(object sender, EventArgs e)
            {
                try
                {
                    IntPtr hwnd = container.Handle, parent = GetParent(hwnd);
                    if (parent == IntPtr.Zero) return;
                    GetClientRect(parent, out RECT r);
                    int w = r.Right - r.Left, h = r.Bottom - r.Top;
                    if (w < 10 || h < 10 || (w == lastW && h == lastH)) return;
                    lastW = w; lastH = h;
                    MoveWindow(hwnd, 0, 0, w, h, true);
                    container.Size = new System.Drawing.Size(w, h);
                    onResized?.Invoke();
                }
                catch { }
            }
        }
    }
}