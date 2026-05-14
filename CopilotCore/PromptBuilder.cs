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
            => BuildModeAPrompt(context, null, null, null);

        // ── Mode A: full signature ────────────────────────────────────────────

        public static string BuildModeAPrompt(
            WorkspaceContext context,
            string clarificationAnswers,
            string resolvedContext,
            string geometryLock = null)
        {
            bool hasLock = !string.IsNullOrEmpty(geometryLock);

            // Image is NO LONGER sent to this call.
            // It was processed by ExtractImageContextAsync() and its structured
            // output lives inside geometryLock. The generation model (DeepSeek)
            // reads facts as text — no vision capability needed here.
            var systemPrompt =
                "You are an expert SOLIDWORKS CAD Engineer. Return ONLY valid JSON — no markdown, no code fences.\n" +
                (hasLock
                    ? @"
GEOMETRY LOCK — IMMUTABLE GROUND TRUTH:
All dimensions, coordinates, origin, and feature sequence below are FIXED.
They were derived from the user's prompt, image analysis, and clarification answers combined.
You MUST NOT invent, change, or recalculate any dimension.
Your only job is to write precise SolidWorks instructions for each step referencing these exact values.
"
                    : @"
STEP 1 — Before writing any steps, complete design_logic:
  - Define origin (0,0,0) explicitly.
  - Calculate ALL hole/slot X/Y centers relative to origin BEFORE writing instructions.
") +
                // SPRINT 3: Removed ""step_rationale"" from schema — saves tokens per batch,
                // reduces UI clutter. The geometry lock and instructions carry the full intent.
                @"
OUTPUT SCHEMA (exact):
{
  ""design_logic"": {
    ""base_body_plan"": ""Primary mass strategy (e.g. 'Extrude L-profile from Right Plane')."",
    ""origin_definition"": ""Where is (0,0,0)? (e.g. 'Bottom-left inner corner of bracket')."",
    ""coordinate_math"": ""Pre-calculated X/Y of every feature relative to origin (or 'See geometry lock').""
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
    }
  ],
  ""confidence"": ""high|medium|low""
}

HARD RULES:
1. NEVER write 'positioned according to drawing' — always give exact mm values.
2. First sketch must reference the defined origin with a Fix or Coincident constraint.
3. All depth/radius/diameter values must be positive numbers.
4. Maximum 8 steps. If the design needs more, complete the primary structure first.
5. Do not invent dimensions outside what is defined in the geometry lock or goal.
6. NEVER use Infinity, -Infinity, NaN, or null for numeric fields — use 0 if a value is unknown.";

            var bounds = DeriveBounds(context);

            var userPayload = hasLock
                ? (object)new
                {
                    geometry_lock = JsonConvert.DeserializeObject(geometryLock),
                    goal = context.DesignGoal,
                    material = context.Material,
                    existing_features = context.Features,
                    active_selection = context.ActiveSelection,
                    older_features_summary = context.OlderFeaturesSummary,
                    geometric_context = bounds,
                    resolved_context = resolvedContext,
                    clarification_answers = clarificationAnswers
                }
                : (object)new
                {
                    goal = context.DesignGoal,
                    material = context.Material,
                    existing_features = context.Features,
                    active_selection = context.ActiveSelection,
                    older_features_summary = context.OlderFeaturesSummary,
                    geometric_context = bounds,
                    resolved_context = resolvedContext,
                    clarification_answers = clarificationAnswers
                };

            var userContent = JsonConvert.SerializeObject(userPayload, Formatting.Indented);
            return JsonConvert.SerializeObject(new { system = systemPrompt, user = userContent });
        }

        // ── Image Extraction: dedicated vision call ───────────────────────────
        //
        // PURPOSE: Extract structured geometric facts from the attached image.
        // This runs FIRST when an image is present, using a cheap vision model.
        // The output is plain structured JSON — no image is passed to any later call.
        // The generation model (DeepSeek) reads these facts as text.
        //
        // THREE-LAYER HIERARCHY:
        //   Layer 1 — Text prompt:    primary design intent. Always the anchor.
        //   Layer 2 — Image:          supporting geometric evidence. Enriches the text.
        //   Layer 3 — Clarification:  fills gaps neither text nor image resolved.
        //
        // Image must ADD to the text. If it contradicts, flag it — never silently pick one.

        public static string BuildImageExtractionPrompt(string designGoal)
        {
            var systemPrompt =
@"You are a technical image analyst for SOLIDWORKS CAD. Return ONLY valid JSON — no markdown, no code fences.

Your job is to extract geometric facts from the attached image to SUPPORT the user's design goal.
The design goal text is the primary intent — the image provides additional geometric detail only.

EXTRACTION RULES:
1. Extract only what is clearly visible — do not infer or guess beyond what you can see.
2. If a dimension is readable in the image, record it with confidence 'read'. If estimated, say 'estimated'.
3. If anything in the image appears to CONTRADICT the design goal text, list it in contradictions[]. Never silently pick one over the other.
4. If the image is a sketch, photo, or drawing that adds no useful engineering info, set adds_useful_info=false.
5. Record only engineering-relevant geometry — ignore decorative or artistic elements.

OUTPUT SCHEMA (exact):
{
  ""overall_shape"": ""Brief description of the primary geometry visible."",
  ""envelope"": {
    ""L_mm"": null,
    ""W_mm"": null,
    ""H_mm"": null,
    ""confidence"": ""read|estimated|unclear""
  },
  ""visible_dimensions"": [
    { ""name"": ""wall_thickness"", ""value_mm"": 5.0, ""confidence"": ""read|estimated"" }
  ],
  ""hole_pattern"": ""Count, arrangement, estimated or read diameter. null if none visible."",
  ""features_visible"": [""fillet"", ""chamfer"", ""rib"", ""boss"", ""slot""],
  ""annotations"": [""Any text visible in image — material callouts, tolerances, notes, dimensions""],
  ""contradictions"": [""Anything the image shows that conflicts with the design goal text""],
  ""image_quality"": ""clear|partial|unclear"",
  ""adds_useful_info"": true
}";

            var userContent = JsonConvert.SerializeObject(new
            {
                design_goal = designGoal,
                instruction = "Extract geometric facts from the attached image that support or clarify this design goal."
            }, Formatting.None);

            return JsonConvert.SerializeObject(new { system = systemPrompt, user = userContent });
        }

        // ── Geometry Lock: Call 2 of the pipeline ────────────────────────────
        //
        // Runs on empty workspace. Receives image facts as structured text (imageContext).
        // No raw image is passed here — DeepSeek does not need vision capability.
        // Priority order for resolving dimensions is enforced in the prompt.

        public static string BuildGeometryLockPrompt(
            WorkspaceContext context,
            string clarificationAnswers,
            string resolvedContext,
            string imageContext = null)
        {
            bool hasImageContext = !string.IsNullOrEmpty(imageContext);

            var systemPrompt =
                "You are a SOLIDWORKS geometry planner. Return ONLY valid JSON — no markdown, no code fences.\n" +
                (hasImageContext
                    ? "Structured image analysis is provided in the user payload. Use it as supporting evidence — it was extracted from the user's reference image by a dedicated vision model.\n"
                    : "") +
                @"
Your job is to define a COMPLETE dimensional framework BEFORE any modelling steps are written.
This output is immutable — no dimension can be changed in the generation step that follows.

DIMENSION SOURCE PRIORITY (highest to lowest):
  1. clarification_answers  — user confirmed these explicitly. Highest confidence.
  2. goal text              — primary design intent. Always the anchor.
  3. image_analysis         — supporting evidence. Use where goal is silent.
  4. Engineering defaults   — only when all above are silent. Always flagged.

RULES:
1. Envelope L x W x H in mm. Record source and reasoning for each dimension.
2. Origin: define (0,0,0) explicitly — which corner, which face, which edge.
3. Pre-calculate EVERY dimension modelling will need: wall thicknesses, hole diameters,
   hole centre X/Y relative to origin, fillet radii, slot widths. Show the arithmetic.
   CRITICAL: Distinguish sketch dimensions (profile size) from extrusion depth.
   e.g. for a bracket leg: sketch_length=50mm (profile), extrusion_depth=3mm (thickness).
   Never assign thickness values to depth_mm unless the extrusion direction IS the thickness.
4. Feature sequence: build order with feature name and plane. No instructions yet.
5. Any dimension from engineering defaults must appear in flags[].
6. If image_analysis listed contradictions, record how each was resolved.
7. feature_sequence is MANDATORY — every feature that will be modelled must appear here in build order. This array drives the step-by-step execution loop. Never omit it.

OUTPUT SCHEMA (exact):
{
  ""envelope_mm"": { ""L"": 0.0, ""W"": 0.0, ""H"": 0.0 },
  ""origin_definition"": ""Explicit statement of where (0,0,0) is."",
  ""dimension_table"": [
    { ""name"": ""base_thickness"",  ""value_mm"": 5.0,  ""source"": ""clarification|goal|image|default"", ""reasoning"": ""..."" },
    { ""name"": ""hole_1_center_x"", ""value_mm"": 20.0, ""source"": ""clarification|goal|image|default"", ""reasoning"": ""..."" },
    { ""name"": ""hole_1_center_y"", ""value_mm"": 15.0, ""source"": ""clarification|goal|image|default"", ""reasoning"": ""..."" }
  ],
  ""feature_sequence"": [
    { ""order"": 1, ""feature"": ""Extrude"",     ""plane"": ""Front Plane"", ""brief"": ""Base body"" },
    { ""order"": 2, ""feature"": ""Cut-Extrude"", ""plane"": ""Top Plane"",   ""brief"": ""Mounting holes"" }
  ],
  ""flags"": [""Dimensions defaulted or needing user confirmation""]
}";

            var userPayload = hasImageContext
                ? (object)new
                {
                    goal = context.DesignGoal,
                    material = context.Material,
                    clarification_answers = clarificationAnswers,
                    resolved_context = resolvedContext,
                    image_analysis = JsonConvert.DeserializeObject(imageContext)
                }
                : (object)new
                {
                    goal = context.DesignGoal,
                    material = context.Material,
                    clarification_answers = clarificationAnswers,
                    resolved_context = resolvedContext
                };

            var userContent = JsonConvert.SerializeObject(userPayload, Formatting.Indented);
            return JsonConvert.SerializeObject(new { system = systemPrompt, user = userContent });
        }

        // ── Mode B: error resolution ──────────────────────────────────────────

        public static string BuildModeBPrompt(ErrorContext error)
        {
            var systemPrompt =
@"You are a SOLIDWORKS diagnostic engineer. Return ONLY valid JSON — no markdown, no code fences.

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
        //
        // Receives imageContext as structured JSON text — no raw image.
        // Clarification model does not need vision capability.
        // Three-layer hierarchy is enforced explicitly in the prompt.

        public static string BuildClarificationPrompt(
            string designGoal,
            bool hasImage = false,
            string imageContext = null)
        {
            bool hasImageContext = !string.IsNullOrEmpty(imageContext);

            var systemPrompt =
                "You are a SOLIDWORKS design intent clarifier. Return ONLY raw JSON — no markdown, no code fences.\n" +
                @"
THREE-LAYER INPUT HIERARCHY — follow strictly in this order:

  LAYER 1 — Design goal text: PRIMARY. Never ask about anything the text already states.
  LAYER 2 — Image analysis:   SUPPORTING. Use it to ELIMINATE questions.
             If image clearly shows a dimension or feature, do NOT ask about it.
  LAYER 3 — Clarification:    GAPS ONLY. Ask only when both text and image leave
             something critical unresolved.

CONTRADICTION RULE:
  If the image analysis and the text disagree on any dimension or feature,
  this IS a clarification question — always ask which takes priority.
  Never silently resolve a contradiction.

DO NOT ASK ABOUT:
- Standard specs you know (NEMA sizes, metric bolt grades, ISO profiles, bearing codes)
- Anything stated in the design goal text
- Anything clearly shown in image_analysis with confidence 'read'
- Aesthetic preferences that do not affect the feature tree

DO ASK ABOUT:
- Critical missing dimensions not in text or image (e.g. wall thickness, tolerances)
- Mounting method if not visible or stated
- Load type if it affects geometry
- Any contradiction between text and image

Extract all confirmed specs from text and image into resolved_context.
If text + image together give sufficient context: needs_clarification=false.
If image is provided and answers the question — do NOT ask it. Image evidence overrides assumptions.
When asking: maximum 2 questions, only for critical gaps neither text nor image resolved.
NEVER claim the image is missing if image_analysis is present in the payload.

SCHEMA:
{
  ""needs_clarification"": true,
  ""questions"": [
    {
      ""id"": 1,
      ""question"": ""<specific question about this design>"",
      ""hint"": ""<example values relevant to this design>"",
      ""suggested_values"": [""<value1>"", ""<value2>"", ""<value3>""],
      ""reason_asked"": ""text_gap|image_gap|contradiction""
    }
  ],
  ""resolved_context"": ""<all specs confirmed from goal text and image analysis>"",
  ""contradictions_found"": [""<list anything where image and text disagree>""],
  ""skip_reason"": """"
}";

            object userPayload = hasImageContext
                ? (object)new
                {
                    goal = designGoal,
                    image_analysis = JsonConvert.DeserializeObject(imageContext)
                }
                : (object)new
                {
                    goal = designGoal,
                    image_provided = hasImage ? "yes — image analysis unavailable" : "no"
                };

            var userContent = JsonConvert.SerializeObject(userPayload, Formatting.None);
            return JsonConvert.SerializeObject(new { system = systemPrompt, user = userContent });
        }

        // ── SPRINT 3: Rolling Window Batch Prompt ─────────────────────────────
        //
        // Called by AiClient.GenerateBatchAsync() for every 2-step generation.
        // Also handles regeneration scenarios:
        //   userFeedback != null + regenerateBothSteps=false → Scenario B (step 2 only)
        //   userFeedback != null + regenerateBothSteps=true  → Scenario C (both steps)

        public static string BuildBatchPrompt(
            RollingWindowState state,
            string[] remainingSequence,
            string userFeedback = null,
            bool regenerateBothSteps = false)
        {
            bool isRegeneration = !string.IsNullOrEmpty(userFeedback);
            int stepsToGenerate = regenerateBothSteps || remainingSequence.Length >= 2 ? 2 : 1;

            // Clamp to what's actually remaining
            stepsToGenerate = Math.Min(stepsToGenerate, remainingSequence.Length);

            // Last 2 completed steps only — token efficiency, scan covers full current state
            var recentCompleted = state.CompletedSteps
                .Skip(Math.Max(0, state.CompletedSteps.Count - 2))
                .Select(s => new { s.Feature, s.Plane, s.SummaryLine })
                .ToArray();

            var taskDescription = isRegeneration
                ? (regenerateBothSteps
                    ? $"Step 1 ({remainingSequence[0]}) failed. Regenerate BOTH Step 1 AND Step 2."
                    : $"Step 2 ({remainingSequence[0]}) failed. Regenerate Step 2 ONLY. Step 1 is locked and correct.")
                : $"Generate the next {stepsToGenerate} step(s): " +
                  string.Join(" and ", remainingSequence.Take(stepsToGenerate));

            var systemPrompt =
                $"You are a SOLIDWORKS CAD engineer generating the next steps in an active design session.\n" +
                $"The user is executing steps in real SolidWorks as you generate them.\n" +
                $"Generate ONLY {stepsToGenerate} step(s). Do not plan beyond what is asked.\n" +
                "Return ONLY valid JSON — no markdown, no code fences.\n\n" +
                // SPRINT 3: Removed step_rationale from batch schema — not needed,
                // saves ~100 tokens per batch call.
                @"OUTPUT SCHEMA (exact):
{
  ""steps"": [
    {
      ""feature"": ""Extrude|Cut-Extrude|Fillet|Chamfer|Hole|Shell|Revolve|LinearPattern|CircularPattern"",
      ""plane"": ""Front Plane|Top Plane|Right Plane|<named plane>"",
      ""parameters"": { ""depth_mm"": 10.0, ""end_condition"": ""blind"", ""is_cut"": false },
      ""instructions"": [
        ""Select [plane] as sketch plane."",
        ""Draw [shape] at [exact X,Y mm] from origin."",
        ""Add constraints: [list each]."",
        ""Apply feature: [exact parameter values].""
      ],
      ""summary_line"": ""One-line description of what this step produces."",
      ""confidence"": ""high|medium|low"",
    }
  ]
}

HARD RULES:
1. Reference ONLY features that exist in ACTUAL CURRENT STATE — never assume a feature exists.
2. All dimensions MUST come from GEOMETRY LOCK — do not invent new values.
3. Use actual SolidWorks feature names from ACTUAL CURRENT STATE when referencing existing geometry.
4. Never exceed the requested step count.
5. NEVER use Infinity, -Infinity, NaN, or null for numeric fields — use 0 if a value is unknown.";

            var userPayload = new
            {
                geometry_lock = JsonConvert.DeserializeObject(state.GeometryLockJson ?? "{}"),
                actual_current_state = JsonConvert.DeserializeObject(state.LastScanResultJson ?? "{\"feature_count\":0,\"features\":[]}"),
                last_completed_steps = recentCompleted,
                remaining_features = remainingSequence,
                user_correction = isRegeneration ? userFeedback : null,
                task = taskDescription
            };

            var userContent = JsonConvert.SerializeObject(userPayload, Formatting.Indented);
            return JsonConvert.SerializeObject(new { system = systemPrompt, user = userContent });
        }

        // ── SPRINT 3: Single Step Regeneration Prompt (Scenario B) ────────────
        //
        // Used when Step 1 is locked/correct and only Step 2 needs regeneration.
        // Passes the locked step explicitly so the model knows exactly what preceded it.

        public static string BuildSingleStepRegenerationPrompt(
            RollingWindowState state,
            StepData lockedStep,
            string failedStepBrief,
            string userFeedback)
        {
            var systemPrompt =
                "You are a SOLIDWORKS CAD engineer correcting a single failed step.\n" +
                "Step 1 is LOCKED and correct — do NOT change it.\n" +
                "Regenerate Step 2 ONLY based on the user correction and real workspace state.\n" +
                "Return ONLY valid JSON — no markdown, no code fences.\n\n" +
                @"OUTPUT SCHEMA (exact):
{
  ""steps"": [
    {
      ""feature"": ""Extrude|Cut-Extrude|Fillet|Chamfer|Hole|Shell|Revolve|LinearPattern|CircularPattern"",
      ""plane"": ""Front Plane|Top Plane|Right Plane|<named plane>"",
      ""parameters"": { ""depth_mm"": 10.0, ""end_condition"": ""blind"", ""is_cut"": false },
      ""instructions"": [
        ""Select [plane] as sketch plane."",
        ""Draw [shape] at [exact X,Y mm] from origin."",
        ""Add constraints: [list each]."",
        ""Apply feature: [exact parameter values].""
      ],
      ""summary_line"": ""One-line description of what this step produces."",
      ""confidence"": ""high|medium|low"",
    }
  ]
}

HARD RULES:
1. Generate exactly 1 step (Step 2 replacement only).
2. Reference ONLY features that exist in ACTUAL CURRENT STATE.
3. All dimensions MUST come from GEOMETRY LOCK.
4. The locked Step 1 provides context — build on it, do not redo it.";

            var userPayload = new
            {
                geometry_lock = JsonConvert.DeserializeObject(state.GeometryLockJson ?? "{}"),
                actual_current_state = JsonConvert.DeserializeObject(
                    state.LastScanResultJson ?? "{\"feature_count\":0,\"features\":[]}"),
                locked_step_1 = new
                {
                    lockedStep.Feature,
                    lockedStep.Plane,
                    lockedStep.SummaryLine
                },
                failed_step_2_brief = failedStepBrief,
                user_correction = userFeedback,
                task = $"Regenerate Step 2 ({failedStepBrief}) only. Step 1 is complete and locked."
            };

            var userContent = JsonConvert.SerializeObject(userPayload, Formatting.Indented);
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