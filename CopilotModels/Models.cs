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

        /// <summary>
        /// A concise, one-line rationale displayed on the card front.
        /// </summary>
        [JsonProperty("summary_line")]
        public string SummaryLine { get; set; }

        /// <summary>
        /// The full, deep design reasoning displayed in the expanded view.
        /// </summary>
        [JsonProperty("step_rationale")]
        public string StepRationale { get; set; }

        [JsonProperty("risk")]
        public string Risk { get; set; }

        // --- Legacy Fields (Retained for backward compatibility) ---

        [Obsolete("Use StepRationale instead")]
        [JsonProperty("why_geometric")]
        public string WhyGeometric { get; set; }

        [Obsolete("Use StepRationale instead")]
        [JsonProperty("why_functional")]
        public string WhyFunctional { get; set; }

        [Obsolete("Use StepRationale instead")]
        [JsonProperty("why_manufacturing")]
        public string WhyManufacturing { get; set; }
    }

    public class AlternativeData
    {
        [JsonProperty("approach")] public string Approach { get; set; }
        [JsonProperty("instructions")] public string[] Instructions { get; set; }
        [JsonProperty("reasoning")] public string Reasoning { get; set; }
        [JsonProperty("confidence")] public string Confidence { get; set; }
    }

    // ── Clarification models (Sprint 1) ──────────────────────────────────────

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