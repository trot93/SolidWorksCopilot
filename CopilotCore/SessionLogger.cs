// CHANGES FROM ORIGINAL:
// - FIX: Replaced "using CopilotAddIn" with "using CopilotModels".
//   WorkspaceContext and MaterialInfo now live in CopilotModels.
// - All other fixes retained (session ID constructor, AppData path, using statements).
// SPRINT 3:
// - Added CurrentSession property (RollingWindowState) — in-memory rolling window state.
// - Added InitialiseSession() — called once after GeometryLock completes.
// - Added ArchiveBatch()     — called after each 2-step batch is confirmed done.
// - Added UpdateScanResult() — called by WorkspaceScanner after every scan.

using System;
using System.Collections.Generic;
using System.IO;
using System.Data.SQLite;
using CopilotModels;
using Newtonsoft.Json;

namespace CopilotCore
{
    public class SessionLogger : IDisposable
    {
        private readonly SQLiteConnection connection;
        private readonly string sessionId;

        public string SessionId => sessionId;

        // SPRINT 3: In-memory rolling window state for the active session.
        // Null until InitialiseSession() is called (i.e. after GeometryLock succeeds).
        public RollingWindowState CurrentSession { get; private set; }

        public SessionLogger(string sessionId, string dbPath = null)
        {
            this.sessionId = sessionId;

            // FIX: Store the DB in AppData, not the working directory
            if (dbPath == null)
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SolidWorksCopilot");
                Directory.CreateDirectory(dir);
                dbPath = Path.Combine(dir, "copilot_sessions.db");
            }

            connection = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            connection.Open();
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using (var cmd = new SQLiteCommand(@"
                CREATE TABLE IF NOT EXISTS sessions (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id   TEXT NOT NULL,
                    timestamp    TEXT,
                    design_goal  TEXT,
                    material     TEXT,
                    workspace_state    TEXT,
                    ai_recommendation  TEXT,
                    user_action        TEXT,
                    modification       TEXT,
                    api_latency_ms     INTEGER
                );

                CREATE TABLE IF NOT EXISTS api_calls (
                    call_id    INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    mode       TEXT,
                    prompt     TEXT,
                    response   TEXT,
                    timestamp  TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_sessions_session ON sessions(session_id);
                CREATE INDEX IF NOT EXISTS idx_sessions_goal    ON sessions(design_goal);
                CREATE INDEX IF NOT EXISTS idx_api_session      ON api_calls(session_id);
            ", connection))
            {
                cmd.ExecuteNonQuery();
            }
        }

        // ── SPRINT 3: Rolling Window State ────────────────────────────────────

        /// <summary>
        /// Called once after GeometryLock JSON is confirmed valid.
        /// Initialises the in-memory rolling window for the active session.
        /// </summary>
        public void InitialiseSession(string geometryLockJson)
        {
            CurrentSession = new RollingWindowState
            {
                GeometryLockJson = geometryLockJson,
                CompletedSteps = new List<StepData>(),
                CurrentBatchIndex = 0,
                IsComplete = false
            };

            // Persist session start so DB reflects the geometry lock was established
            try
            {
                using (var cmd = new SQLiteCommand(@"
                    INSERT INTO sessions (session_id, timestamp, user_action, modification)
                    VALUES (@sid, @time, 'session_initialised', @lock)", connection))
                {
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("@lock", geometryLockJson);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { /* Logger must never throw */ }
        }

        /// <summary>
        /// Called after each 2-step batch is confirmed complete by the user.
        /// Appends completed steps to the rolling history and advances the batch index.
        /// </summary>
        public void ArchiveBatch(List<StepData> completedSteps, string newScanResultJson)
        {
            if (CurrentSession == null) return;

            CurrentSession.CompletedSteps.AddRange(completedSteps);
            CurrentSession.LastScanResultJson = newScanResultJson;
            CurrentSession.CurrentBatchIndex++;

            // Persist batch archive to DB for post-session analysis
            try
            {
                using (var cmd = new SQLiteCommand(@"
                    INSERT INTO sessions (session_id, timestamp, user_action, modification, workspace_state)
                    VALUES (@sid, @time, 'batch_archived', @steps, @scan)", connection))
                {
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("@steps", JsonConvert.SerializeObject(completedSteps));
                    cmd.Parameters.AddWithValue("@scan", newScanResultJson);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }

        /// <summary>
        /// Called by WorkspaceScanner after every scan.
        /// Keeps LastScanResultJson current so the next batch prompt reflects reality.
        /// </summary>
        public void UpdateScanResult(string scanResultJson)
        {
            if (CurrentSession != null)
                CurrentSession.LastScanResultJson = scanResultJson;
        }

        // ── Existing logging methods (unchanged) ──────────────────────────────

        public void LogScan(WorkspaceContext context)
        {
            try
            {
                using (var cmd = new SQLiteCommand(@"
                    INSERT INTO sessions (session_id, timestamp, design_goal, material, workspace_state)
                    VALUES (@sid, @time, @goal, @mat, @ws)", connection))
                {
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("@goal", context.DesignGoal);
                    cmd.Parameters.AddWithValue("@mat", JsonConvert.SerializeObject(context.Material));
                    cmd.Parameters.AddWithValue("@ws", JsonConvert.SerializeObject(context.Features));
                    cmd.ExecuteNonQuery();
                }
            }
            catch { /* Logger must never throw */ }
        }

        public void LogApiCall(string mode, string prompt)
        {
            try
            {
                using (var cmd = new SQLiteCommand(@"
                    INSERT INTO api_calls (session_id, mode, prompt, timestamp)
                    VALUES (@sid, @mode, @prompt, @time)", connection))
                {
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    cmd.Parameters.AddWithValue("@mode", mode);
                    cmd.Parameters.AddWithValue("@prompt", prompt);
                    cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("O"));
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }

        public void LogApiResponse(string mode, string response)
        {
            try
            {
                using (var cmd = new SQLiteCommand(@"
                    UPDATE api_calls SET response = @response
                    WHERE call_id = (
                        SELECT MAX(call_id) FROM api_calls
                        WHERE session_id = @sid AND mode = @mode
                    )", connection))
                {
                    cmd.Parameters.AddWithValue("@response", response);
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    cmd.Parameters.AddWithValue("@mode", mode);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }

        public void LogStepDecision(string goal, int stepIndex, string feature,
                                    string action, string modification)
        {
            try
            {
                using (var cmd = new SQLiteCommand(@"
                    INSERT INTO sessions (session_id, timestamp, design_goal, user_action, modification)
                    VALUES (@sid, @time, @goal, @action, @mod)", connection))
                {
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("@goal", goal);
                    cmd.Parameters.AddWithValue("@action", $"step_{stepIndex}_{feature}_{action}");
                    cmd.Parameters.AddWithValue("@mod", modification ?? (object)DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }

        public void LogUserAction(string goal, string actionType, string details)
        {
            try
            {
                using (var cmd = new SQLiteCommand(@"
                    INSERT INTO sessions (session_id, timestamp, design_goal, user_action, modification)
                    VALUES (@sid, @time, @goal, @action, @details)", connection))
                {
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("@goal", goal);
                    cmd.Parameters.AddWithValue("@action", actionType);
                    cmd.Parameters.AddWithValue("@details", details ?? (object)DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }

        public void LogError(string message, Exception ex)
        {
            try
            {
                using (var cmd = new SQLiteCommand(@"
                    INSERT INTO api_calls (session_id, mode, prompt, response, timestamp)
                    VALUES (@sid, 'ERROR', @msg, @detail, @time)", connection))
                {
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    cmd.Parameters.AddWithValue("@msg", message);
                    cmd.Parameters.AddWithValue("@detail", ex?.ToString() ?? "no exception");
                    cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("O"));
                    cmd.ExecuteNonQuery();
                }
            }
            catch { /* Logger must never throw */ }
        }

        public void Dispose()
        {
            connection?.Close();
            connection?.Dispose();
        }
    }
}