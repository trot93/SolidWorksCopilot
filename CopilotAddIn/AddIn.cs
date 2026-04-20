using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;
using SolidWorks.Interop.swconst;
using SolidWorksTools;
using CopilotModels;
using CopilotCore;

using SysEnv = System.Environment;

namespace CopilotAddIn
{
    [ComVisible(true)]
    [Guid("6E8572C5-9048-4B5C-8D75-3D575119A560")]
    [ProgId("SolidWorksCopilot.AddIn")]
    public class AddIn : ISwAddin
    {
        static AddIn()
        {
            // Force TLS 1.2/1.3 — required for Anthropic/OpenAI APIs on .NET 4.8
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 |
                System.Net.SecurityProtocolType.Tls13;

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string name = new System.Reflection.AssemblyName(args.Name).Name;
                string dir = Path.GetDirectoryName(
                                  System.Reflection.Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, name + ".dll");
                return File.Exists(path) ? System.Reflection.Assembly.LoadFrom(path) : null;
            };
        }

        private SldWorks swApp;
        private int addInId;
        private TaskPaneManager taskPaneManager;
        private WorkspaceScanner scanner;
        private SessionLogger logger;
        private AiClient aiClient;
        private PartDoc activePartDoc;
        private string logDir;

        public AddIn()
        {
            try
            {
                logDir = Path.Combine(Path.GetTempPath(), "SW_Copilot_Logs");
                Directory.CreateDirectory(logDir);
                WriteLog("Constructor initialized.");
            }
            catch { }
        }

        private void WriteLog(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(logDir, "lifecycle.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] {message}{SysEnv.NewLine}");
            }
            catch { }
        }

        [ComRegisterFunction]
        public static void Register(Type t)
        {
            try
            {
                var keyPath = $@"SOFTWARE\SolidWorks\AddIns\{t.GUID:B}";
                using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath))
                {
                    key.SetValue(null, 1);
                    key.SetValue("Title", "AI Copilot");
                    key.SetValue("Description", "AI-powered design assistant");
                }
            }
            catch (Exception ex) { MessageBox.Show("Registry Error: " + ex.Message); }
        }

        [ComUnregisterFunction]
        public static void Unregister(Type t)
        {
            try
            {
                Microsoft.Win32.Registry.LocalMachine
                    .DeleteSubKey($@"SOFTWARE\SolidWorks\AddIns\{t.GUID:B}", false);
            }
            catch { }
        }

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                WriteLog("ConnectToSW started.");
                swApp = (SldWorks)ThisSW;
                addInId = Cookie;

                swApp.SetAddinCallbackInfo(0, this, addInId);
                WriteLog("Callback registered.");

                var sessionId = Guid.NewGuid().ToString();
                logger = new SessionLogger(sessionId);
                scanner = new WorkspaceScanner(swApp, logger);
                WriteLog("Core services initialized.");

                string apiKey = LoadApiKeyFromConfig();
                string prov = LoadProviderFromConfig();

                // Pass apiKey as-is (null when missing).
                // Using ?? string.Empty would coerce null → "" which passes the
                // IsNullOrEmpty guard here but then fails the !IsNullOrEmpty check
                // inside ConfigureHeaders, so the auth header would never be set.
                aiClient = new AiClient(apiKey, prov, logger);

                WriteLog("Initializing TaskPane...");
                taskPaneManager = new TaskPaneManager(swApp, addInId, scanner, aiClient, logger);
                taskPaneManager.OnOpenSettings = () => OpenSetupDialog();
                taskPaneManager.CreateTaskPane();

                // Wire the Win32 focus bridge into the WPF pane
                // (must happen after CreateTaskPane so wpfPane exists)
                taskPaneManager.WireHostFocusToPane();

                WriteLog("TaskPane created.");

                if (string.IsNullOrEmpty(apiKey))
                {
                    // No key at all → show banner after SW finishes loading,
                    // then auto-open the dialog once (true first-run experience).
                    WriteLog("No API key — scheduling first-run prompt.");
                    DeferFirstRunPrompt();
                }
                else
                {
                    // Key exists → validate silently after SW finishes loading.
                    // On a definitive auth failure (401/403) show the banner.
                    // On a network/timeout failure assume the key is fine —
                    // a slow network at startup must not force the user through setup.
                    WriteLog("API key found — scheduling background validation.");
                    DeferSilentValidation();
                }

                HookDocumentEvents();
                WriteLog("ConnectToSW completed successfully.");
                return true;
            }
            catch (Exception ex)
            {
                string msg = $"CRASH in ConnectToSW: {ex.Message}{SysEnv.NewLine}{ex.StackTrace}";
                try { File.WriteAllText(Path.Combine(logDir, "last_crash.txt"), msg); } catch { }
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            try
            {
                WriteLog("Disconnecting...");
                UnhookCurrentDocEvents();
                taskPaneManager?.DestroyTaskPane();
                logger?.Dispose();
                aiClient?.Dispose();
                WriteLog("Disconnected.");
            }
            catch { }
            return true;
        }

        // ── Config ────────────────────────────────────────────────────────────

        private string GetConfigPath() =>
            Path.Combine(
                SysEnv.GetFolderPath(SysEnv.SpecialFolder.ApplicationData),
                "SolidWorksCopilot", "config.json");

        private string LoadApiKeyFromConfig()
        {
            try
            {
                var p = GetConfigPath();
                if (!File.Exists(p)) return null;
                var key = JObject.Parse(File.ReadAllText(p))["api_key"]?.ToString();
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
            catch { return null; }
        }

        private string LoadProviderFromConfig()
        {
            try
            {
                var p = GetConfigPath();
                if (!File.Exists(p)) return "anthropic";
                var prov = JObject.Parse(File.ReadAllText(p))["provider"]?.ToString();
                return string.IsNullOrWhiteSpace(prov) ? "anthropic" : prov;
            }
            catch { return "anthropic"; }
        }

        private void SaveConfig(string key, string provider)
        {
            try
            {
                var p = GetConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, new JObject
                {
                    ["api_key"] = key,
                    ["provider"] = provider
                }.ToString());
            }
            catch { }
        }

        // ── Startup sequences ─────────────────────────────────────────────────

        /// <summary>
        /// True first-run: no key exists. Wait for SW to finish loading, show
        /// the missing-key banner, then open the setup dialog automatically once.
        /// </summary>
        private void DeferFirstRunPrompt()
        {
            var timer = new System.Windows.Forms.Timer { Interval = 2500 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                taskPaneManager?.ShowApiKeyMissingState();
                OpenSetupDialog();  // only auto-opened when there is genuinely no key
            };
            timer.Start();
        }

        /// <summary>
        /// Key exists on disk. Validate it silently after SW finishes loading.
        ///
        /// Three outcomes:
        ///   1. ok = true               → NotifyApiKeyReady (normal path).
        ///   2. ok = false, isAuthError  → ShowApiKeyInvalidState — the provider
        ///      definitively rejected the key (401/403); the user must fix it.
        ///   3. ok = false, !isAuthError → NotifyApiKeyReady anyway — this was a
        ///      network hiccup, timeout, or DNS failure at startup. The key is
        ///      probably fine; the first real Generate Steps call will surface any
        ///      genuine problem without forcing the user through setup every time
        ///      they open SW on a slow network.
        /// </summary>
        private void DeferSilentValidation()
        {
            // 5 s gives the network stack more time to settle after SW starts.
            var timer = new System.Windows.Forms.Timer { Interval = 5000 };
            timer.Tick += async (s, e) =>
            {
                timer.Stop();
                timer.Dispose();

                // Defensive guard — key should always be present here because
                // ConnectToSW branched on it, but re-read to be safe.
                string apiKey = LoadApiKeyFromConfig();
                if (string.IsNullOrEmpty(apiKey))
                {
                    WriteLog("DeferSilentValidation: key missing at validation time — showing banner.");
                    taskPaneManager?.ShowApiKeyMissingState();
                    return;
                }

                taskPaneManager?.SetStatus("Verifying API key…", false);
                (bool ok, bool isAuthError, string errorMsg) = await aiClient.ValidateKeyAsync();

                if (ok)
                {
                    WriteLog("API key validated successfully.");
                    taskPaneManager?.NotifyApiKeyReady();
                }
                else if (isAuthError)
                {
                    // Provider returned 401/403 — key is definitively rejected.
                    WriteLog($"API key rejected by provider (auth error): {errorMsg}");
                    taskPaneManager?.ShowApiKeyInvalidState(errorMsg);
                }
                else
                {
                    // Timeout, DNS failure, or other transient network error.
                    // Treat the key as valid and let the first real call catch it.
                    WriteLog($"API key validation inconclusive (network error, assuming valid): {errorMsg}");
                    taskPaneManager?.NotifyApiKeyReady();
                }
            };
            timer.Start();
        }

        // ── Central setup dialog ──────────────────────────────────────────────

        /// <summary>
        /// Opens the ApiKeySetupForm. Entry point for all scenarios:
        ///   • Auto-opened on true first run (no key)
        ///   • Gear icon click
        ///   • "Setup" / "Fix key" banner click
        ///   • After a failed Generate Steps call
        /// </summary>
        public void OpenSetupDialog()
        {
            try
            {
                string existingKey = LoadApiKeyFromConfig() ?? string.Empty;
                string existingProvider = LoadProviderFromConfig();

                using (var form = new ApiKeySetupForm(existingKey, existingProvider))
                {
                    if (form.ShowDialog() == DialogResult.OK &&
                        !string.IsNullOrWhiteSpace(form.ApiKey))
                    {
                        SaveConfig(form.ApiKey, form.SelectedProvider);
                        aiClient.UpdateApiKey(form.ApiKey, form.SelectedProvider);
                        WriteLog($"API key updated. Provider: {form.SelectedProvider}");

                        // Key was verified inside the form before OK was returned,
                        // so we can immediately show Ready without re-validating.
                        taskPaneManager?.NotifyApiKeyReady();
                    }
                    else
                    {
                        WriteLog("Setup dialog dismissed without saving.");
                        // If still no valid key, keep the banner visible.
                        if (string.IsNullOrEmpty(LoadApiKeyFromConfig()))
                            taskPaneManager?.ShowApiKeyMissingState();
                        // If a key exists (user hit Skip on reconfigure), leave status as-is.
                    }
                }
            }
            catch (Exception ex) { WriteLog($"Setup dialog error: {ex.Message}"); }
        }

        // ── Document events ───────────────────────────────────────────────────

        private void HookDocumentEvents()
        {
            swApp.ActiveDocChangeNotify += OnActiveDocChanged;
            swApp.DocumentLoadNotify2 += OnDocumentLoaded;
        }

        private int OnActiveDocChanged()
        {
            UnhookCurrentDocEvents();
            HookCurrentDocEvents();
            return 0;
        }

        private int OnDocumentLoaded(string docTitle, string docPath)
        {
            HookCurrentDocEvents();
            return 0;
        }

        private void HookCurrentDocEvents()
        {
            if (swApp.ActiveDoc is PartDoc part)
            {
                activePartDoc = part;
                ((DPartDocEvents_Event)activePartDoc).AddItemNotify += OnFeatureAdded;
            }
        }

        private void UnhookCurrentDocEvents()
        {
            if (activePartDoc != null)
            {
                try { ((DPartDocEvents_Event)activePartDoc).AddItemNotify -= OnFeatureAdded; }
                catch { }
                activePartDoc = null;
            }
        }

        private int OnFeatureAdded(int EntityType, string itemName)
        {
            logger?.LogUserAction("system", "feature_added", itemName);
            return 0;
        }
    }
}