using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace CopilotModels
{
    // ── Workspace / Scanner models ────────────────────────────────────────────

    public class WorkspaceContext
    {
        [JsonProperty("goal")]
        public string DesignGoal { get; set; }

        [JsonProperty("material")]
        public MaterialInfo Material { get; set; }

        [JsonProperty("existing_features")]
        public List<FeatureData> Features { get; set; } = new List<FeatureData>();

        [JsonProperty("older_features_summary")]
        public string OlderFeaturesSummary { get; set; }

        [JsonProperty("active_selection")]
        public string ActiveSelection { get; set; }

        // ── Sprint 2: image attachment ────────────────────────────────────────

        /// <summary>
        /// Base64-encoded PNG from GoalInputOverlay, or null.
        /// Forwarded into every LLM call that supports vision.
        /// </summary>
        [JsonIgnore]
        public string ImageBase64 { get; set; }

        /// <summary>"image/png" when ImageBase64 is set, null otherwise.</summary>
        [JsonIgnore]
        public string ImageMediaType { get; set; }
    }

    public class MaterialInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("density_kg_m3")]
        public double Density { get; set; }

        [JsonProperty("youngs_modulus_pa")]
        public double YoungsModulus { get; set; }

        [JsonProperty("poisson_ratio")]
        public double PoissonRatio { get; set; }
    }

    public class FeatureData
    {
        [JsonProperty("step")]
        public int Step { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("plane")]
        public string Plane { get; set; }

        [JsonProperty("unresolved_type")]
        public bool UnresolvedType { get; set; }

        [JsonProperty("parameters")]
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        [JsonProperty("error")]
        public string Error { get; set; }
    }

    // ── AI response models ────────────────────────────────────────────────────

    public class AiResponse
    {
        [JsonProperty("design_logic")]
        public string DesignLogic { get; set; }

        [JsonProperty("steps")]
        public StepData[] Steps { get; set; }

        [JsonProperty("error_diagnosis")]
        public string ErrorDiagnosis { get; set; }

        [JsonProperty("alternatives")]
        public AlternativeData[] Alternatives { get; set; }

        [JsonProperty("confidence")]
        public string Confidence { get; set; } = "medium";

        [JsonIgnore]
        public string Error { get; set; }

        [JsonIgnore]
        public string RawResponse { get; set; }

        [JsonIgnore]
        public bool IsPlainText { get; set; }

        /// <summary>
        /// SPRINT 3: Set by AiClient when geometry lock succeeds.
        /// MainTaskPane reads this directly — no shared logger dependency.
        /// </summary>
        [JsonIgnore]
        public string GeometryLockJson { get; set; }
    }

    public class StepData
    {
        [JsonProperty("feature")]
        public string Feature { get; set; }

        [JsonProperty("plane")]
        public string Plane { get; set; }

        [JsonProperty("parameters")]
        public Dictionary<string, object> Parameters { get; set; }

        [JsonProperty("instructions")]
        public string[] Instructions { get; set; }

        [JsonProperty("summary_line")]
        public string SummaryLine { get; set; }

        // SPRINT 3: Removed StepRationale — saves tokens, removed from JSON schema too.
        // Legacy [Obsolete] why_* fields below are sufficient for any backward-compat reads.

        [JsonProperty("confidence")]
        public string Confidence { get; set; }

        // SPRINT 3: Execution tracking fields
        [JsonIgnore]
        public bool IsLocked { get; set; }

        [JsonIgnore]
        public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;

        [JsonIgnore]
        public string UserFeedback { get; set; }

        // Legacy fields retained for backward compatibility
        [Obsolete("Use step instructions instead")]
        [JsonProperty("why_geometric")]
        public string WhyGeometric { get; set; }

        [Obsolete("Use step instructions instead")]
        [JsonProperty("why_functional")]
        public string WhyFunctional { get; set; }

        [Obsolete("Use step instructions instead")]
        [JsonProperty("why_manufacturing")]
        public string WhyManufacturing { get; set; }
    }

    // SPRINT 3: Step execution lifecycle enum
    public enum ExecutionStatus
    {
        Pending,    // not yet executed
        Completed,  // user confirmed done
        Failed,     // user flagged as wrong
        Discarded   // auto-invalidated because a prior step failed
    }

    // SPRINT 3: Rolling window session state — lives here instead of a separate class
    // to keep the model layer self-contained. Replaces the proposed GenerationSession.cs.
    public class RollingWindowState
    {
        /// <summary>Full GeometryLock JSON from Sprint 2 pipeline — immutable for the session.</summary>
        public string GeometryLockJson { get; set; }

        /// <summary>All steps confirmed complete so far. Prompt injection uses last 2 only.</summary>
        public List<StepData> CompletedSteps { get; set; } = new List<StepData>();

        /// <summary>Batch index (0 = steps 1–2, 1 = steps 3–4, …).</summary>
        public int CurrentBatchIndex { get; set; }

        /// <summary>Most recent WorkspaceScanner output JSON — updated after every scan.</summary>
        public string LastScanResultJson { get; set; }

        /// <summary>True when all feature_sequence items have been generated and completed.</summary>
        public bool IsComplete { get; set; }
    }

    public class AlternativeData
    {
        [JsonProperty("approach")] public string Approach { get; set; }
        [JsonProperty("instructions")] public string[] Instructions { get; set; }
        [JsonProperty("reasoning")] public string Reasoning { get; set; }
        [JsonProperty("confidence")] public string Confidence { get; set; }
    }

    // ── Clarification models ──────────────────────────────────────────────────

    public class ClarificationQuestion
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("question")]
        public string Question { get; set; }

        [JsonProperty("hint")]
        public string Hint { get; set; }

        [JsonProperty("suggested_values")]
        public string[] SuggestedValues { get; set; }
    }

    public class ClarificationResponse
    {
        [JsonProperty("needs_clarification")]
        public bool NeedsClarification { get; set; }

        [JsonProperty("questions")]
        public ClarificationQuestion[] Questions { get; set; }

        [JsonProperty("resolved_context")]
        public string ResolvedContext { get; set; }

        [JsonProperty("skip_reason")]
        public string SkipReason { get; set; }
    }

    public class ClarificationExchange
    {
        [JsonProperty("question_id")]
        public int QuestionId { get; set; }

        [JsonProperty("question")]
        public string Question { get; set; }

        [JsonProperty("answer")]
        public string Answer { get; set; }
    }

    // ── Mode B error context ──────────────────────────────────────────────────

    public class ErrorContext
    {
        public string ErrorMessage { get; set; }
        public object AttemptedStep { get; set; }
        public object[] FeatureContext { get; set; }
        public MaterialInfo Material { get; set; }
    }

}