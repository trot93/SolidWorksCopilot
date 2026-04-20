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

        // ── Mode A: with clarification answers (Sprint 1 + Sprint 2) ─────────

        public static string BuildModeAPrompt(
            WorkspaceContext context,
            string clarificationAnswers,
            string resolvedContext)
        {
            // Note: if image is present the caller (AiClient) injects it as a
            // vision content block — we only need to tell the model to use it.
            bool hasImage = !string.IsNullOrEmpty(context?.ImageBase64);

            var systemPrompt = @"You are an expert, universal SOLIDWORKS CAD Engineer.
Your goal is to translate abstract design intent into a highly efficient, logical SOLIDWORKS feature tree for ANY type of component (micro-mechanical, consumer goods, heavy machinery, or aerospace).
" + (hasImage ? @"
A reference image has been provided alongside this request. Analyse it carefully:
- Identify the overall geometry, proportions, and key features visible in the image.
- Use observed dimensions, hole patterns, wall thicknesses, and any text/annotations as ground truth.
- Prefer image-derived values over your own assumptions wherever possible.
- If the image contradicts a user statement, flag it in design_logic and follow the image.
" : "") + @"
First, establish the physical scale, domain, and material assumptions in the 'design_logic' field. Then, provide the execution sequence.
Return ONLY valid JSON.

Response must follow this schema:
{
  ""design_logic"": ""State the anticipated physical scale. Define thickness and mass assumptions. Note any image-derived values (Sprint 2)."",
  ""steps"": [
    {
      ""feature"": ""Extrude|Cut-Extrude|Fillet|Chamfer|Hole|Shell|Revolve|etc"",
      ""plane"": ""Front Plane|Top Plane|Right Plane|<named plane>"",
      ""parameters"": {
        ""depth_mm"": 10.0,
        ""end_condition"": ""blind"",
        ""is_cut"": false
      },
      ""instructions"": [
        ""Select [plane] as the sketch plane"",
        ""Draw a [shape] — [exact dimensions, starting at origin or relating to existing geometry]"",
        ""Add required constraints (Fix, Coincident, Vertical, etc.)"",
        ""Apply feature with necessary parameters""
      ],
      ""step_rationale"": ""One sentence explaining how this feature serves the overall design goal."",
      ""risk"": ""Known failure mode or null""
    }
  ],
  ""confidence"": ""high|medium|low""
}

UNIVERSAL ENGINEERING GUIDELINES:
1. Dynamic Scaling: Derive geometric scale directly from the prompt and image (if present).
2. Feature Efficiency: Use the fewest robust features possible.
3. Parametric Anchoring: Always tie the first sketch to the Origin.
4. Absolute Values: All CAD parameters must be positive numerical values.
5. Geometric Anchoring: Reference existing features by name when placing new geometry.

Do not wrap the output in markdown code blocks. Output raw JSON only.";

            var bounds = DeriveBounds(context);

            var userContent = JsonConvert.SerializeObject(new
            {
                goal = context.DesignGoal,
                material = context.Material,
                existing_features = context.Features,
                active_selection = context.ActiveSelection,
                older_features_summary = context.OlderFeaturesSummary,
                geometric_context = bounds,
                resolved_context = resolvedContext,
                clarification_answers = clarificationAnswers,
                image_attached = hasImage ? "yes — see vision content block" : "no"
            }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { system = systemPrompt, user = userContent });
        }

        // ── Mode B: error resolution ──────────────────────────────────────────

        public static string BuildModeBPrompt(ErrorContext error)
        {
            var systemPrompt = @"You are an expert SOLIDWORKS API diagnostic engineer.
Diagnose the following rebuild or topological failure and provide generalized CAD alternatives.

Return ONLY valid JSON using this schema:
{
  ""error_diagnosis"": ""Clear explanation of the geometric or topological root cause."",
  ""alternatives"": [
    {
      ""approach"": ""Brief description of alternative method."",
      ""instructions"": [ ""Step 1"", ""Step 2"" ],
      ""reasoning"": ""Why this robustly avoids the previous failure."",
      ""confidence"": ""high|medium|low""
    }
  ]
}

Do not wrap the output in markdown code blocks. Output raw JSON only.";

            var userContent = JsonConvert.SerializeObject(new
            {
                error_message = error.ErrorMessage,
                attempted_step = error.AttemptedStep,
                feature_context = error.FeatureContext,
                material = error.Material
            }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { system = systemPrompt, user = userContent });
        }

        // ── Clarification prompt (Sprint 1 + Sprint 2 image awareness) ────────

        public static string BuildClarificationPrompt(string designGoal, bool hasImage = false)
        {
            string imageNote = hasImage
                ? "\nA reference image has been attached. Use it to resolve geometry, dimensions, " +
                  "and feature layout before deciding what to ask. Only ask questions that the " +
                  "image cannot answer.\n"
                : string.Empty;

            var systemPrompt = @"You are a SOLIDWORKS design intent clarifier.
Analyze the user's goal. Use your own engineering knowledge — do NOT ask about standard specs you already know (NEMA motors, metric bolts, bearing codes, ISO extrusions, etc.).
" + imageNote + @"
Only ask about genuine ambiguities:
- Wall/material thickness if not stated and not visible in image
- Load/force context (static vs dynamic?)
- Mounting method (bolts, welds, adhesive?) if not visible
- Operating environment if critical
- Tolerances if functionally relevant

Also extract any dimensions or specs already stated in the goal and note them as resolved_context.

If the goal + image together give you enough context, set needs_clarification=false and return no questions. Only ask questions when critical information is genuinely missing and cannot be inferred from the image or goal text.
IMPORTANT: When needs_clarification=true you MUST return a JSON array with 2-3 question objects.
Return ONLY raw JSON. No markdown. No code fences.

Schema (always include all three example questions when needs_clarification=true):
{
  ""needs_clarification"": true,
  ""questions"": [
    {
      ""id"": 1,
      ""question"": ""What mounting method do you need?"",
      ""hint"": ""e.g., 4x M4 counterbored holes, or welded flange"",
      ""suggested_values"": [""Bolted"", ""Welded"", ""Adhesive"", ""Press-fit""]
    },
    {
      ""id"": 2,
      ""question"": ""What wall thickness do you need?"",
      ""hint"": ""e.g., 2mm for lightweight, 5mm for structural"",
      ""suggested_values"": [""2mm"", ""3mm"", ""5mm"", ""8mm""]
    },
    {
      ""id"": 3,
      ""question"": ""What is the operating environment?"",
      ""hint"": ""e.g., indoor static load, outdoor vibration, high temperature"",
      ""suggested_values"": [""Indoor static"", ""Outdoor"", ""High vibration"", ""High temperature""]
    }
  ],
  ""resolved_context"": ""Any specs already known from the goal or image"",
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
                note = "Ensure all new features are proportionally logical relative to these bounds."
            };
        }
    }
}