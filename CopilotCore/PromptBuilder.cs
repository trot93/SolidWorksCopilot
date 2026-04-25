using System;
using System.Collections.Generic;
using System.Linq;
using CopilotModels;
using Newtonsoft.Json;

namespace CopilotCore
{
    public static class PromptBuilder
    {
        // ── Mode A: no clarification answers ─────────────────────────────────

        public static string BuildModeAPrompt(WorkspaceContext context)
            => BuildModeAPrompt(context, null, null);

        // ── Mode A: with clarification answers ────────────────────────────────

        public static string BuildModeAPrompt(
            WorkspaceContext context,
            string clarificationAnswers,
            string resolvedContext)
        {
            bool hasImage = !string.IsNullOrEmpty(context?.ImageBase64);

            // CHANGE 1: System prompt restructured for token efficiency without losing instruction quality.
            //
            // What was removed / condensed:
            //   - Removed the verbose preamble paragraph ("Your goal is to translate abstract...")
            //     — the role line already implies this.
            //   - Collapsed the image block from 4 bullet lines to 2 tight lines.
            //   - Removed the "UNIVERSAL ENGINEERING GUIDELINES & HARD CONSTRAINTS" header and
            //     reformatted the 4 rules as a compact block. Same semantic content, ~40% fewer tokens.
            //   - Removed the redundant "Do not wrap the output in markdown code blocks" line —
            //     "Return ONLY valid JSON" already implies this.
            //
            // What was kept unchanged:
            //   - The full JSON schema (cannot be shortened without losing output structure).
            //   - The design_logic scratchpad instruction (critical for coordinate reasoning).
            //   - The 'No Laziness' rule (directly addresses the hallucination symptom you described).
            //   - The parametric anchoring rule.
            //   - All field descriptions in the schema.

            var systemPrompt =
                "You are an expert SOLIDWORKS CAD Engineer. Return ONLY valid JSON — no markdown, no code fences.\n" +
                (hasImage
                    ? "A reference image is attached. Extract geometry, dimensions, hole patterns, and text from it as ground truth. Only ask what the image cannot answer.\n"
                    : "") +
                @"
STEP 1 — Before writing any steps, complete design_logic:
  - Define origin (0,0,0) explicitly.
  - Calculate ALL hole/slot X/Y centers relative to origin BEFORE writing instructions.

OUTPUT SCHEMA (exact):
{
  ""design_logic"": {
    ""base_body_plan"": ""Primary mass strategy (e.g. 'Extrude L-profile from Right Plane')."",
    ""origin_definition"": ""Where is (0,0,0)? (e.g. 'Bottom-left inner corner of bracket')."",
    ""coordinate_math"": ""Pre-calculated X/Y of every hole/slot relative to origin.""
  },
  ""steps"": [
    {
      ""feature"": ""Extrude|Cut-Extrude|Fillet|Chamfer|Hole|Shell|Revolve|LinearPattern|CircularPattern|Sweep|Loft"",
      ""plane"": ""Front Plane|Top Plane|Right Plane|<named plane>"",
      ""parameters"": {
        ""depth_mm"": 10.0,
        ""end_condition"": ""blind"",
        ""is_cut"": false
      },
      ""instructions"": [
        ""Select [plane] as sketch plane."",
        ""Draw [shape] with corners/center at [exact X,Y mm] relative to origin."",
        ""Add constraints: [list each one]."",
        ""Apply feature: [exact parameter values].""
      ],
      ""step_rationale"": ""One sentence: how this feature serves the design goal."",
      ""risk"": ""Known failure mode, or null.""
    }
  ],
  ""confidence"": ""high|medium|low""
}

HARD RULES:
1. NEVER write 'positioned according to drawing' or any vague reference — always give exact mm values.
2. First sketch must reference the defined origin with a Fix or Coincident constraint.
3. All depth/radius/diameter values must be positive numbers.
4. Scale all geometry from the goal/image — do not invent dimensions.";

            var bounds = DeriveBounds(context);

            // CHANGE 2: User content object trimmed.
            //   - Removed 'image_attached' field — the model can see or not see the image from the
            //     vision block itself. The string "yes — see vision content block" was redundant tokens.
            //   - Kept all data fields that carry real information.
            var userContent = JsonConvert.SerializeObject(new
            {
                goal = context.DesignGoal,
                material = context.Material,
                existing_features = context.Features,
                active_selection = context.ActiveSelection,
                older_features_summary = context.OlderFeaturesSummary,
                geometric_context = bounds,
                resolved_context = resolvedContext,
                clarification_answers = clarificationAnswers
            }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { system = systemPrompt, user = userContent });
        }

        // ── Mode B: error resolution ──────────────────────────────────────────

        public static string BuildModeBPrompt(ErrorContext error)
        {
            // CHANGE 3: Mode B system prompt condensed.
            //   Removed verbose preamble, collapsed to essential instruction.
            //   Same output schema, ~30% fewer tokens.
            var systemPrompt =
@"You are a SOLIDWORKS diagnostic engineer. Return ONLY valid JSON.

SCHEMA:
{
  ""error_diagnosis"": ""Root cause of the rebuild or topological failure."",
  ""alternatives"": [
    {
      ""approach"": ""Alternative method name."",
      ""instructions"": [""Step 1"", ""Step 2""],
      ""reasoning"": ""Why this avoids the failure."",
      ""confidence"": ""high|medium|low""
    }
  ]
}";

            var userContent = JsonConvert.SerializeObject(new
            {
                error_message = error.ErrorMessage,
                attempted_step = error.AttemptedStep,
                feature_context = error.FeatureContext,
                material = error.Material
            }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { system = systemPrompt, user = userContent });
        }

        // ── Clarification ─────────────────────────────────────────────────────

        public static string BuildClarificationPrompt(string designGoal, bool hasImage = false)
        {
            // CHANGE 4: Clarification system prompt significantly trimmed.
            //
            //   What was removed:
            //   - The list of "only ask about" examples (wall thickness, load, mounting...) was
            //     7 bullet lines. Condensed to 1 line. The model knows what ambiguity means.
            //   - The example JSON with 3 hardcoded example questions was ~120 tokens of tokens
            //     that the model ignores as content and reads as schema. Replaced with a compact
            //     schema description.
            //   - The "Schema (always include all three example questions when...)" instruction
            //     was confusing — it told the model to always include those 3 specific questions,
            //     which caused generic questions unrelated to the actual design goal.
            //
            //   What was kept:
            //   - The "do not ask about standard specs" rule — critical for question quality.
            //   - The resolved_context extraction instruction.
            //   - The needs_clarification=false path.
            //   - The image awareness note.
            //   - The 2–3 question limit.

            string imageNote = hasImage
                ? "An image is attached — extract geometry, dimensions, and features from it first. Only ask what the image cannot answer.\n"
                : string.Empty;

            var systemPrompt =
                "You are a SOLIDWORKS design intent clarifier. Return ONLY raw JSON — no markdown.\n" +
                imageNote +
                @"
RULES:
- Do NOT ask about standard specs you already know (NEMA sizes, metric bolt grades, ISO profiles, bearing codes, etc.).
- Only ask when information is genuinely missing AND cannot be inferred: e.g. wall thickness, load type, mounting method, tolerances.
- Extract any dimensions/specs already stated in the goal into resolved_context.
- If the goal + image give sufficient context, set needs_clarification=false with empty questions array.
- When needs_clarification=true, return exactly 2–3 targeted questions specific to this design goal.

SCHEMA:
{
  ""needs_clarification"": true,
  ""questions"": [
    {
      ""id"": 1,
      ""question"": ""<specific question>"",
      ""hint"": ""<example values>"",
      ""suggested_values"": [""<value1>"", ""<value2>"", ""<value3>""]
    }
  ],
  ""resolved_context"": ""<specs already known from goal or image>"",
  ""skip_reason"": """"
}";

            var userContent = JsonConvert.SerializeObject(new { goal = designGoal }, Formatting.None);
            return JsonConvert.SerializeObject(new { system = systemPrompt, user = userContent });
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static object DeriveBounds(WorkspaceContext context)
        {
            if (context.Features == null || context.Features.Count == 0)
                return new { note = "No existing geometry — first feature establishes the base coordinate system." };

            var depths = new List<double>();
            foreach (var f in context.Features)
            {
                if (f.Parameters != null && f.Parameters.TryGetValue("depth_mm", out var d))
                {
                    if (d is double dv) depths.Add(dv);
                    else if (d is long lv) depths.Add((double)lv);
                    else if (double.TryParse(d.ToString(), out double pv)) depths.Add(pv);
                }
            }

            if (depths.Count == 0)
                return new { note = "Existing features present but no extractable dimensions to establish scale." };

            return new
            {
                largest_dimension_mm = depths.Max(),
                smallest_dimension_mm = depths.Min(),
                note = "All new features must be proportionally consistent with these bounds."
            };
        }
    }
}