// PHASE 2 STUB — Do not implement yet.
//
// This file will contain the full Mode B error-capture logic:
//   - Hook into SOLIDWORKS FeatureManagerFeatureEditPostNotify / failure events
//   - Capture the error message string from the failed operation
//   - Pass error context to AiClient.ResolveErrorAsync()
//   - Surface the diagnosis and ranked alternatives in the Task Pane error panel
//
// For Phase 1, this file intentionally contains no logic.
// Its presence in the project prevents the "missing file" compile error and
// reserves the namespace/structure for Phase 2 implementation.
//
// DO NOT DELETE — this stub will be filled in during Phase 2 (Months 4-5).

namespace CopilotAddIn
{
    /// <summary>
    /// Phase 2: Captures SOLIDWORKS feature operation failures and routes them
    /// to the AI error resolution pipeline (Mode B).
    /// </summary>
    internal static class ComEventHandlers
    {
        // Phase 2 implementation goes here.
        // Planned public surface:
        //   public static void HookErrorEvents(PartDoc doc, TaskPaneManager pane, AiClient ai, SessionLogger log)
        //   public static void UnhookErrorEvents(PartDoc doc)
    }
}