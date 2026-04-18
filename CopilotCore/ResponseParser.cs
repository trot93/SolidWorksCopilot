// PHASE 2 STUB — Do not implement yet.
//
// This file will contain dedicated response parsing logic extracted from AiClient.cs
// once response handling grows complex enough to warrant separation.
// Planned responsibilities:
//   - Validate response JSON against expected schema
//   - Handle partial responses (steps array present but empty)
//   - Parse and normalise confidence values
//   - Detect and strip accidental markdown fences (currently handled inline in AiClient)
//
// For Phase 1, parsing lives in AiClient.ParseResponse().
// This stub exists to keep the project structure honest — the file is listed
// in the solution and will be filled in Phase 2 without any project-file changes.
//
// DO NOT DELETE — stub will be expanded in Phase 2.

namespace CopilotCore
{
    /// <summary>
    /// Phase 2: Dedicated AI response validation and normalisation layer.
    /// </summary>
    internal static class ResponseParser
    {
        // Phase 2 implementation goes here.
        // Planned public surface:
        //   public static AiResponse Parse(string rawJson)
        //   public static bool IsValidStepResponse(string json)
    }
}