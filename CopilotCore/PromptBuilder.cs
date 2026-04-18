using System;
using System.Collections.Generic;
using System.Linq;
using CopilotModels;
using Newtonsoft.Json;

namespace CopilotCore
{
    public static class PromptBuilder
    {
        public static string BuildModeAPrompt(WorkspaceContext context)
        {
            var systemPrompt = @"You are an expert, universal SOLIDWORKS CAD Engineer.
Your goal is to translate abstract design intent into a highly efficient, logical SOLIDWORKS feature tree for ANY type of component (micro-mechanical, consumer goods, heavy machinery, or aerospace).

First, establish the physical scale, domain, and material assumptions in the 'design_logic' field. Then, provide the execution sequence.
Return ONLY valid JSON.

Response must follow this schema:
{
  ""design_logic"": ""State the anticipated physical scale (e.g., 'Small component, ~50mm bounding box' vs 'Large structural element, ~2000mm bounding box'). Define thickness and mass assumptions before proceeding."",
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
      ""step_rationale"": ""One sentence explaining how this specific feature serves the overall functional design goal."",
      ""risk"": ""Known failure mode or null""
    }
  ],
  ""confidence"": ""high|medium|low""
}

UNIVERSAL ENGINEERING GUIDELINES:
1. Dynamic Scaling: Derive your geometric scale directly from the prompt. Ensure that sketch dimensions, extrusion depths, and fillet radii are proportional to the object's real-world domain.
2. Feature Efficiency: Use the fewest robust features possible. Consolidate cuts and hole patterns where logical to maintain a clean feature tree.
3. Parametric Anchoring: Always tie the first sketch directly to the Origin. Fully define all sketches using geometric constraints (Equal, Collinear, Midpoint) over hardcoded dimensions whenever possible to ensure predictable updates.
4. Absolute Values: All CAD parameters (depth, radius, diameter) must be positive numerical values.

Do not wrap the output in markdown code blocks. Output raw JSON only.";

            // Ground dimensions with explicit context injection
            var bounds = DeriveBounds(context);

            var userContent = JsonConvert.SerializeObject(new
            {
                goal = context.DesignGoal,
                material = context.Material,
                existing_features = context.Features,
                active_selection = context.ActiveSelection,
                older_features_summary = context.OlderFeaturesSummary,
                geometric_context = bounds
            }, Formatting.Indented);

            return JsonConvert.SerializeObject(new
            {
                system = systemPrompt,
                user = userContent
            });
        }

        public static string BuildModeBPrompt(ErrorContext error)
        {
            var systemPrompt = @"You are an expert SOLIDWORKS API diagnostic engineer.
Diagnose the following rebuild or topological failure and provide generalized CAD alternatives.

Return ONLY valid JSON using this schema:
{
  ""error_diagnosis"": ""Clear explanation of the geometric or topological root cause (e.g., zero-thickness geometry, self-intersecting contour, missing reference)."",
  ""alternatives"": [
    {
      ""approach"": ""Brief description of alternative method."",
      ""instructions"": [
        ""Step 1: specific corrective action"",
        ""Step 2: what to select or change""
      ],
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

            return JsonConvert.SerializeObject(new
            {
                system = systemPrompt,
                user = userContent
            });
        }

        private static object DeriveBounds(WorkspaceContext context)
        {
            if (context.Features == null || context.Features.Count == 0)
                return new { note = "No existing geometry — first feature establishes the base coordinate system and overall physical scale." };

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
                note = "Ensure all newly generated features are proportionally logical relative to these existing bounds."
            };
        }

        // ── Sprint 1: Clarification Prompt ────────────────────────────────────

        public static string BuildClarificationPrompt(string designGoal)
        {
            // Ultra-lean prompt — trusts LLM's own knowledge base
            var systemPrompt = @"You are a SOLIDWORKS design intent clarifier.
Analyze the user's goal. Use your own engineering knowledge — do NOT ask about standard specs you already know (NEMA motors, metric bolts, bearing codes, ISO extrusions, etc.).

Only ask about genuine ambiguities:
- Wall/material thickness if not stated
- Load/force context (static vs dynamic?)
- Mounting method (bolts, welds, adhesive?)
- Operating environment if critical
- Tolerances if functionally relevant

Also extract any dimensions or specs already stated in the goal and note them as resolved_context.

Ask MAX 2 questions. If clear enough, set needs_clarification=false.
Return ONLY JSON. No markdown.

Schema:
{
  ""needs_clarification"": true/false,
  ""questions"": [
    {
      ""id"": 1,
      ""question"": ""What mounting method do you need?"",
      ""hint"": ""e.g., 4x M4 counterbored holes, or welded flange"",
      ""suggested_values"": [""Bolted"", ""Welded"", ""Adhesive"", ""Press-fit""]
    }
  ],
  ""resolved_context"": ""NEMA 17=42.3x42.3mm, user specified 5mm wall thickness, etc."",
  ""skip_reason"": ""Only used when needs_clarification is false""
}";

            var userContent = JsonConvert.SerializeObject(new
            {
                goal = designGoal
            }, Formatting.None);

            return JsonConvert.SerializeObject(new
            {
                system = systemPrompt,
                user = userContent
            });
        }

        // Build enhanced Mode A prompt with clarification answers
        public static string BuildModeAPrompt(WorkspaceContext context, string clarificationAnswers = null, string resolvedContext = null)
        {
            var systemPrompt = @"You are an expert, universal SOLIDWORKS CAD Engineer.
Your goal is to translate abstract design intent into a highly efficient, logical SOLIDWORKS feature tree for ANY type of component (micro-mechanical, consumer goods, heavy machinery, or aerospace).

First, establish the physical scale, domain, and material assumptions in the 'design_logic' field. Then, provide the execution sequence.
Return ONLY valid JSON.

Response must follow this schema:
{
  ""design_logic"": ""State the anticipated physical scale (e.g., 'Small component, ~50mm bounding box' vs 'Large structural element, ~2000mm bounding box'). Define thickness and mass assumptions before proceeding."",
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
      ""step_rationale"": ""One sentence explaining how this specific feature serves the overall functional design goal."",
      ""risk"": ""Known failure mode or null""
    }
  ],
  ""confidence"": ""high|medium|low""
}

UNIVERSAL ENGINEERING GUIDELINES:
1. Dynamic Scaling: Derive your geometric scale directly from the prompt. Ensure that sketch dimensions, extrusion depths, and fillet radii are proportional to the object's real-world domain.
2. Feature Efficiency: Use the fewest robust features possible. Consolidate cuts and hole patterns where logical to maintain a clean feature tree.
3. Parametric Anchoring: Always tie the first sketch directly to the Origin. Fully define all sketches using geometric constraints (Equal, Collinear, Midpoint) over hardcoded dimensions whenever possible to ensure predictable updates.
4. Absolute Values: All CAD parameters (depth, radius, diameter) must be positive numerical values.
5. Geometric Anchoring: You MUST reference existing features by name when placing new geometry. Example: 'Place 4x M6 holes on Top Plane, 25mm from edges of Boss-Extrude1 (which is 50mm wide x 30mm deep).' If no features exist, establish origin at (0,0) and explicitly state the bounding box dimensions you're assuming.

Do not wrap the output in markdown code blocks. Output raw JSON only.";

            // Ground dimensions with explicit context injection
            var bounds = DeriveBounds(context);

            var userContent = JsonConvert.SerializeObject(new
            {
                goal = context.DesignGoal,
                material = context.Material,
                existing_features = context.Features,
                active_selection = context.ActiveSelection,
                older_features_summary = context.OlderFeaturesSummary,
                geometric_context = bounds,
                resolved_context = resolvedContext, // Sprint 1: LLM's own resolved specs (NEMA dims, bolt sizes, etc.)
                clarification_answers = clarificationAnswers // Sprint 1: user's answers to ambiguity questions
            }, Formatting.Indented);

            return JsonConvert.SerializeObject(new
            {
                system = systemPrompt,
                user = userContent
            });
        }
    }
}