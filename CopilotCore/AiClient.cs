using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using CopilotModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CopilotCore
{
    public class AiClient : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly SessionLogger logger;
        private string provider;
        private string modelEndpoint;

        private const string OpenRouterEndpoint = "https://openrouter.ai/api/v1/chat/completions";

        // ── MODEL STRATEGY (verified April 2026) ──────────────────────────────
        //
        // Two-model split for OpenRouter:
        //
        //   VISION MODEL  — qwen/qwen3.5-flash-02-23
        //     Used for: image extraction, clarification (with or without image)
        //     Why: cheapest model with vision + reasoning mode on OpenRouter
        //     Cost: $0.065/M input, $0.26/M output
        //     Vision: yes. Reasoning: yes (built-in thinking mode).
        //
        //   REASONING MODEL — deepseek/deepseek-v3.2
        //     Used for: geometry lock, step generation, verification
        //     Why: strongest reasoning at this price point (~90% of GPT-5 quality)
        //     Cost: $0.252/M input, $0.378/M output
        //     Vision: NO — never send raw images to this model.
        //     After image extraction, it reads structured JSON facts instead.
        //
        //   FALLBACK — google/gemma-4-31b-it:free
        //     Used when: 429 or 404 on primary models
        //     Vision: yes. Free tier (200 req/day limit applies).
        //
        // This split gives you the strongest available reasoning for generation
        // while keeping vision tasks on the cheapest capable model.
        // Total cost per session with image: ~$0.001. Without image: ~$0.0008.

        private const string OpenRouterVisionModel = "qwen/qwen3.5-flash-02-23";      // image extraction + clarification
        private const string OpenRouterReasoningModel = "deepseek/deepseek-v3.2";        // geometry lock + generation + verify
        private const string OpenRouterFallbackModel = "google/gemma-4-31b-it:free";    // fallback on 429/404
        private const string OpenRouterLastResort = "openrouter/auto";               // last resort — never 404s

        private const string ClarifyModelAnthropic = "claude-3-haiku-20240307";
        private const string ClarifyModelOpenAI = "gpt-4o-mini";

        public AiClient(string apiKey, string provider = "anthropic", SessionLogger logger = null)
        {
            this.logger = logger;
            this.provider = provider.ToLower();
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            ConfigureHeaders(apiKey);
        }

        private void ConfigureHeaders(string apiKey)
        {
            httpClient.DefaultRequestHeaders.Clear();
            switch (provider)
            {
                case "anthropic":
                    modelEndpoint = "https://api.anthropic.com/v1/messages";
                    if (!string.IsNullOrEmpty(apiKey))
                        httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
                    httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                    break;
                case "openrouter":
                    modelEndpoint = OpenRouterEndpoint;
                    if (!string.IsNullOrEmpty(apiKey))
                        httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
                    httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://solidworks-copilot.local");
                    httpClient.DefaultRequestHeaders.Add("X-Title", "SOLIDWORKS AI Copilot");
                    break;
                case "openai":
                    modelEndpoint = "https://api.openai.com/v1/chat/completions";
                    if (!string.IsNullOrEmpty(apiKey))
                        httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
                    break;
                default:
                    throw new ArgumentException("Unknown provider: " + provider);
            }
        }

        // ── API key validation ────────────────────────────────────────────────

        public async Task<(bool ok, bool isAuthError, string error)> ValidateKeyAsync()
        {
            try
            {
                string body, endpoint;
                if (provider == "anthropic")
                {
                    endpoint = "https://api.anthropic.com/v1/messages";
                    body = "{\"model\":\"claude-3-haiku-20240307\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}";
                }
                else
                {
                    endpoint = provider == "openrouter" ? OpenRouterEndpoint : "https://api.openai.com/v1/chat/completions";
                    string model = provider == "openrouter" ? OpenRouterVisionModel : "gpt-4o-mini";
                    body = "{\"model\":\"" + model + "\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}";
                }

                var response = await httpClient.PostAsync(endpoint,
                    new StringContent(body, Encoding.UTF8, "application/json"));

                if (response.IsSuccessStatusCode) return (true, false, null);

                int code = (int)response.StatusCode;
                bool isAuth = code == 401 || code == 403;
                var raw = await response.Content.ReadAsStringAsync();
                return (false, isAuth, ExtractApiError(raw, code));
            }
            catch (TaskCanceledException) { return (false, false, "Request timed out"); }
            catch (Exception ex) { return (false, false, ex.Message); }
        }

        // ── Image Extraction ──────────────────────────────────────────────────
        //
        // STEP 0 of the pipeline — runs only when image is present.
        // Sends image to the vision model (Qwen Flash) to extract structured facts.
        // Returns JSON string describing what the image shows.
        // All subsequent calls receive this JSON as text — no raw image passed further.

        public async Task<string> ExtractImageContextAsync(
            string designGoal,
            string imageBase64,
            string imageMediaType)
        {
            if (string.IsNullOrEmpty(imageBase64)) return null;

            try
            {
                logger?.LogApiCall("ImageExtract", "Extracting structured context from image");

                var promptJson = PromptBuilder.BuildImageExtractionPrompt(designGoal);
                var visionModel = GetVisionModel();

                var rawText = await CallLLMRawAsync(
                    promptJson,
                    forceModel: visionModel,
                    imageBase64: imageBase64,
                    imageMediaType: imageMediaType,
                    maxTokens: 512);   // extraction output is small — schema has ~10 fields

                if (string.IsNullOrEmpty(rawText))
                {
                    logger?.LogError("ImageExtract returned empty", null);
                    return null;
                }

                // Validate it's usable JSON before passing downstream
                try
                {
                    // Strip markdown fences if model added them despite instructions
                    rawText = rawText.Trim();
                    if (rawText.StartsWith("```"))
                    {
                        int fn = rawText.IndexOf('\n');
                        if (fn > 0) rawText = rawText.Substring(fn + 1);
                        int lf = rawText.LastIndexOf("```");
                        if (lf >= 0) rawText = rawText.Substring(0, lf);
                        rawText = rawText.Trim();
                    }
                    int js = rawText.IndexOf('{'), je = rawText.LastIndexOf('}');
                    if (js >= 0 && je > js) rawText = rawText.Substring(js, je - js + 1);

                    JObject.Parse(rawText); // throws if invalid
                    logger?.LogApiResponse("ImageExtract", rawText);

                    // If model flagged the image adds no useful info, return null
                    // so downstream treats it as no-image session
                    var parsed = JObject.Parse(rawText);
                    bool addsInfo = parsed["adds_useful_info"]?.ToObject<bool>() ?? true;
                    if (!addsInfo)
                    {
                        logger?.LogApiCall("ImageExtract", "Image flagged as non-useful — treating as no-image session");
                        return null;
                    }

                    return rawText;
                }
                catch
                {
                    logger?.LogError("ImageExtract returned invalid JSON — ignoring image", null);
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger?.LogError("ExtractImageContextAsync failed", ex);
                return null;
            }
        }

        // ── Clarification ─────────────────────────────────────────────────────
        //
        // Now receives imageContext (structured JSON) instead of raw image.
        // The clarification model does not need vision capability.
        // Three-layer hierarchy (text > image facts > gaps) enforced in prompt.

        public async Task<ClarificationResponse> ClarifyGoalAsync(
            string designGoal,
            string imageBase64 = null,
            string imageMediaType = null,
            string imageContext = null)   // structured JSON from ExtractImageContextAsync
        {
            try
            {
                bool hasImg = !string.IsNullOrEmpty(imageBase64);
                bool hasImgContext = !string.IsNullOrEmpty(imageContext);

                // Build clarification prompt with image context as structured text.
                // Raw image is NOT passed to clarification — facts already extracted.
                var promptJson = PromptBuilder.BuildClarificationPrompt(
                    designGoal,
                    hasImage: hasImg,
                    imageContext: imageContext);

                // Clarification uses the vision model (cheap, fast) but without
                // raw image — the model reads extracted JSON facts as text.
                var rawText = await CallLLMRawAsync(
                    promptJson,
                    forceModel: GetVisionModel(),
                    imageBase64: null,           // no raw image — facts are in the prompt text
                    imageMediaType: null,
                    maxTokens: 512);

                if (string.IsNullOrEmpty(rawText))
                    return new ClarificationResponse { NeedsClarification = false, SkipReason = "Empty response" };

                return ParseClarificationResponse(rawText);
            }
            catch (Exception ex)
            {
                logger?.LogError("Clarification failed", ex);
                return new ClarificationResponse { NeedsClarification = false, SkipReason = ex.Message };
            }
        }

        private ClarificationResponse ParseClarificationResponse(string text)
        {
            try
            {
                text = text.Trim();
                if (text.StartsWith("```"))
                {
                    int fn = text.IndexOf('\n');
                    if (fn > 0) text = text.Substring(fn + 1);
                    int lf = text.LastIndexOf("```");
                    if (lf >= 0) text = text.Substring(0, lf);
                    text = text.Trim();
                }
                int js = text.IndexOf('{'), je = text.LastIndexOf('}');
                if (js >= 0 && je > js) text = text.Substring(js, je - js + 1);

                var settings = new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
                    {
                        NamingStrategy = new Newtonsoft.Json.Serialization.SnakeCaseNamingStrategy()
                    }
                };
                var result = JsonConvert.DeserializeObject<ClarificationResponse>(text, settings);

                if (result == null)
                    return new ClarificationResponse { NeedsClarification = false, SkipReason = "Deserialize returned null" };

                if (result.NeedsClarification &&
                    (result.Questions == null || result.Questions.Length == 0))
                {
                    result.NeedsClarification = false;
                    result.SkipReason = "No questions returned by model";
                }

                logger?.LogApiResponse("Clarify_Parsed",
                    $"needs={result.NeedsClarification} questions={result.Questions?.Length ?? 0}");

                return result;
            }
            catch (Exception ex)
            {
                logger?.LogError("Failed to parse clarification response", ex);
                return new ClarificationResponse { NeedsClarification = false, SkipReason = "Parse error: " + ex.Message };
            }
        }

        // ── Model selectors ───────────────────────────────────────────────────

        private string GetVisionModel()
        {
            switch (provider)
            {
                case "anthropic": return ClarifyModelAnthropic;      // haiku has vision
                case "openrouter": return OpenRouterVisionModel;       // qwen3.5-flash
                case "openai": return ClarifyModelOpenAI;         // gpt-4o-mini has vision
                default: return ClarifyModelAnthropic;
            }
        }

        private string GetReasoningModel()
        {
            switch (provider)
            {
                case "anthropic": return "claude-3-5-haiku-20241022"; // acceptable reasoning for anthropic path
                case "openrouter": return OpenRouterReasoningModel;    // deepseek-v3.2
                case "openai": return "gpt-4o";
                default: return "claude-3-5-haiku-20241022";
            }
        }

        // ── Step generation — full pipeline ───────────────────────────────────
        //
        // Complete call sequence:
        //   [Step 0] ExtractImageContextAsync — if image present (called from outside, result passed in)
        //   [Step 1] GeometryLock             — if empty workspace, pins all dimensions
        //   [Step 2] ModeA generation         — writes instructions from locked geometry
        //   [Step 3] Verify                   — JSON consistency check
        //
        // imageContext is the output of Step 0, passed in from the caller (UI layer).
        // This keeps the public API clean — caller controls when extraction runs.

        public async Task<AiResponse> GenerateStepsAsync(
            WorkspaceContext context,
            string clarificationAnswers = null,
            string resolvedContext = null,
            string imageContext = null)   // structured JSON from ExtractImageContextAsync
        {
            string geometryLock = null;

            bool isEmptyWorkspace = context.Features == null || context.Features.Count < 2;

            if (isEmptyWorkspace)
            {
                logger?.LogApiCall("GeometryLock", "Empty workspace — running geometry lock");

                // Geometry lock uses reasoning model — no image passed, facts are in imageContext
                var lockRaw = await CallLLMRawAsync(
                    PromptBuilder.BuildGeometryLockPrompt(
                        context,
                        clarificationAnswers,
                        resolvedContext,
                        imageContext),          // structured image facts injected here
                    forceModel: GetReasoningModel(),
                    imageBase64: null,        // NO raw image — reasoning model doesn't need it
                    imageMediaType: null,
                    maxTokens: 1024);

                if (!string.IsNullOrEmpty(lockRaw))
                {
                    try
                    {
                        // Clean fences if present
                        lockRaw = lockRaw.Trim();
                        if (lockRaw.StartsWith("```"))
                        {
                            int fn = lockRaw.IndexOf('\n');
                            if (fn > 0) lockRaw = lockRaw.Substring(fn + 1);
                            int lf = lockRaw.LastIndexOf("```");
                            if (lf >= 0) lockRaw = lockRaw.Substring(0, lf);
                            lockRaw = lockRaw.Trim();
                        }
                        int js = lockRaw.IndexOf('{'), je = lockRaw.LastIndexOf('}');
                        if (js >= 0 && je > js) lockRaw = lockRaw.Substring(js, je - js + 1);

                        JObject.Parse(lockRaw);
                        geometryLock = lockRaw;
                        logger?.LogApiResponse("GeometryLock", lockRaw);
                    }
                    catch
                    {
                        logger?.LogError("GeometryLock invalid JSON — falling back to single-call", null);
                        geometryLock = null;
                    }
                }
                else
                {
                    logger?.LogError("GeometryLock empty — falling back to single-call", null);
                }
            }

            // Generation uses reasoning model — no raw image passed here.
            // Image facts are embedded in geometryLock (empty workspace path)
            // or were never needed (populated workspace path).
            var response = await CallLLMAsync(
                PromptBuilder.BuildModeAPrompt(context, clarificationAnswers, resolvedContext, geometryLock),
                "ModeA",
                imageBase64: null,           // NO raw image to generation call
                imageMediaType: null,
                isGeneration: true,
                forceModel: GetReasoningModel());

            if (response.Steps != null && !ValidateStepSchema(response))
            {
                logger?.LogError("Schema validation failed — retrying...", null);
                response = await CallLLMAsync(
                    PromptBuilder.BuildModeAPrompt(context, clarificationAnswers, resolvedContext, geometryLock),
                    "ModeA_Retry",
                    imageBase64: null,
                    imageMediaType: null,
                    isGeneration: true,
                    forceModel: GetReasoningModel());
            }

            if (response.Steps != null && !ValidateStepSchema(response))
            {
                response.Error = "AI failed to produce a valid CAD sequence after multiple attempts.";
                return response;
            }

            if (response.Steps != null && response.Steps.Length > 0 && string.IsNullOrEmpty(response.Error))
                return await VerifyStepsAsync(response, context);

            return response;
        }

        public async Task<AiResponse> ResolveErrorAsync(ErrorContext errorContext)
            => await CallLLMAsync(
                PromptBuilder.BuildModeBPrompt(errorContext),
                "ModeB",
                isGeneration: true,
                forceModel: GetReasoningModel());

        // ── Verification ──────────────────────────────────────────────────────

        private async Task<AiResponse> VerifyStepsAsync(AiResponse draft, WorkspaceContext context)
        {
            var verifyPrompt = new JObject
            {
                ["system"] = "SOLIDWORKS step reviewer. Return ONLY the same JSON structure with a 'verified' boolean added to each step. true = plane exists and parameters are physically plausible. false = flag the issue in a 'flag' field. Do not modify geometry.",
                ["user"] = JsonConvert.SerializeObject(new
                {
                    steps = draft.Steps,
                    material = context.Material,
                    existing_features = context.Features?.Select(f => f.Name).ToList()
                })
            }.ToString();

            // Verify uses vision model — cheap, fast, sufficient for JSON consistency check
            var verifyResponse = await CallLLMAsync(
                verifyPrompt, "Verify",
                forceModel: GetVisionModel(),
                isGeneration: false);

            return (verifyResponse.Steps != null && string.IsNullOrEmpty(verifyResponse.Error))
                   ? verifyResponse : draft;
        }

        // ── Core LLM callers ──────────────────────────────────────────────────

        private async Task<AiResponse> CallLLMAsync(
            string promptJson,
            string mode,
            string forceModel = null,
            string imageBase64 = null,
            string imageMediaType = null,
            bool isGeneration = false)
        {
            try
            {
                var requestBody = BuildRequestBody(promptJson, forceModel, imageBase64, imageMediaType, isGeneration);
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                logger?.LogApiCall(mode, promptJson);

                var response = await httpClient.PostAsync(modelEndpoint, content);
                var responseString = await response.Content.ReadAsStringAsync();

                // Three-tier fallback: 429 = rate limited, 404 = no endpoint
                if (((int)response.StatusCode == 429 || (int)response.StatusCode == 404) && provider == "openrouter")
                {
                    string fallback = (int)response.StatusCode == 404
                        ? OpenRouterLastResort
                        : OpenRouterFallbackModel;

                    logger?.LogError($"{mode} — {response.StatusCode} on primary, retrying with {fallback}", null);
                    var fallbackBody = BuildRequestBody(promptJson, fallback, imageBase64, imageMediaType, isGeneration);
                    var fallbackContent = new StringContent(fallbackBody, Encoding.UTF8, "application/json");
                    response = await httpClient.PostAsync(modelEndpoint, fallbackContent);
                    responseString = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode && fallback != OpenRouterLastResort)
                    {
                        logger?.LogError($"{mode} — fallback failed, trying last resort openrouter/auto", null);
                        var lastBody = BuildRequestBody(promptJson, OpenRouterLastResort, imageBase64, imageMediaType, isGeneration);
                        var lastContent = new StringContent(lastBody, Encoding.UTF8, "application/json");
                        response = await httpClient.PostAsync(modelEndpoint, lastContent);
                        responseString = await response.Content.ReadAsStringAsync();
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    string hint = ExtractApiError(responseString, (int)response.StatusCode);
                    logger?.LogError($"API error {response.StatusCode}: {responseString}", null);
                    return new AiResponse { Error = hint, RawResponse = responseString };
                }

                logger?.LogApiResponse(mode, responseString);
                return ParseResponse(responseString);
            }
            catch (TaskCanceledException) { return new AiResponse { Error = "Request timed out — free tier may be under load. Try again." }; }
            catch (Exception ex)
            {
                logger?.LogError("LLM call failed", ex);
                return new AiResponse { Error = ex.Message };
            }
        }

        private async Task<string> CallLLMRawAsync(
            string promptJson,
            string forceModel,
            string imageBase64 = null,
            string imageMediaType = null,
            int maxTokens = 512)
        {
            try
            {
                var requestBody = BuildRequestBody(
                    promptJson, forceModel, imageBase64, imageMediaType,
                    isGeneration: false,
                    overrideMaxTokens: maxTokens);

                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                logger?.LogApiCall("Raw", promptJson);

                var response = await httpClient.PostAsync(modelEndpoint, content);
                var responseString = await response.Content.ReadAsStringAsync();

                // Apply same fallback for raw calls
                if (((int)response.StatusCode == 429 || (int)response.StatusCode == 404) && provider == "openrouter")
                {
                    string fallback = (int)response.StatusCode == 404 ? OpenRouterLastResort : OpenRouterFallbackModel;
                    var fallbackBody = BuildRequestBody(promptJson, fallback, imageBase64, imageMediaType, false, maxTokens);
                    var fallbackCnt = new StringContent(fallbackBody, Encoding.UTF8, "application/json");
                    response = await httpClient.PostAsync(modelEndpoint, fallbackCnt);
                    responseString = await response.Content.ReadAsStringAsync();
                }

                if (!response.IsSuccessStatusCode)
                {
                    logger?.LogError($"Raw API error {response.StatusCode}: {responseString}", null);
                    return null;
                }

                logger?.LogApiResponse("Raw", responseString);
                return ExtractTextContent(responseString);
            }
            catch (Exception ex)
            {
                logger?.LogError("CallLLMRawAsync failed", ex);
                return null;
            }
        }

        // ── Request builder ───────────────────────────────────────────────────

        private string BuildRequestBody(
            string promptJson,
            string forceModel = null,
            string imageBase64 = null,
            string imageMediaType = null,
            bool isGeneration = false,
            int? overrideMaxTokens = null)
        {
            var prompt = JObject.Parse(promptJson);
            string sysMsg = prompt["system"]?.ToString() ?? "You are an expert SOLIDWORKS engineer. Return ONLY JSON.";
            string usrMsg = prompt["user"]?.ToString() ?? string.Empty;

            // Model fallback chain inside BuildRequestBody is just for the default —
            // all real calls pass forceModel explicitly.
            string selectedModel = forceModel
                                   ?? (provider == "openrouter" ? OpenRouterReasoningModel :
                                       provider == "anthropic" ? "claude-3-5-haiku-20241022" :
                                                                   "gpt-4o");

            bool hasImage = !string.IsNullOrEmpty(imageBase64) && !string.IsNullOrEmpty(imageMediaType);
            double temperature = isGeneration ? 0.2 : 0.0;

            // Token budget:
            // Clarify / Verify / ImageExtract: 512  — small structured JSON output
            // GeometryLock:                    1024 — moderate schema
            // Generation:                      4096 — full multi-step output
            int maxTokens = overrideMaxTokens ?? (isGeneration ? 4096 : 512);

            if (provider == "anthropic")
            {
                JToken userContent;
                if (hasImage)
                {
                    userContent = new JArray
                    {
                        new JObject
                        {
                            ["type"]   = "image",
                            ["source"] = new JObject
                            {
                                ["type"]       = "base64",
                                ["media_type"] = imageMediaType,
                                ["data"]       = imageBase64
                            }
                        },
                        new JObject { ["type"] = "text", ["text"] = usrMsg }
                    };
                }
                else { userContent = usrMsg; }

                return new JObject
                {
                    ["model"] = selectedModel,
                    ["max_tokens"] = maxTokens,
                    ["temperature"] = temperature,
                    ["system"] = sysMsg,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "user", ["content"] = userContent }
                    }
                }.ToString();
            }
            else
            {
                // OpenAI / OpenRouter
                JToken userContent;
                if (hasImage)
                {
                    userContent = new JArray
                    {
                        new JObject
                        {
                            ["type"]      = "image_url",
                            ["image_url"] = new JObject
                            {
                                ["url"]    = $"data:{imageMediaType};base64,{imageBase64}",
                                ["detail"] = "high"
                            }
                        },
                        new JObject { ["type"] = "text", ["text"] = usrMsg }
                    };
                }
                else { userContent = usrMsg; }

                return new JObject
                {
                    ["model"] = selectedModel,
                    ["max_tokens"] = maxTokens,
                    ["temperature"] = temperature,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "system", ["content"] = sysMsg },
                        new JObject { ["role"] = "user",   ["content"] = userContent }
                    }
                }.ToString();
            }
        }

        // ── Response parsing ──────────────────────────────────────────────────

        private AiResponse ParseResponse(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                var choices = root["choices"];

                if (choices != null && choices.HasValues)
                {
                    string fr = choices[0]["finish_reason"]?.ToString()
                             ?? choices[0]["native_finish_reason"]?.ToString();
                    if (fr == "length" || fr == "MAX_TOKENS")
                        return new AiResponse
                        {
                            Error = "Response cut off — model hit token limit. Part may be too complex for current settings.",
                            RawResponse = json
                        };
                }

                string content = root["content"]?[0]?["text"]?.ToString()
                              ?? root["choices"]?[0]?["message"]?["content"]?.ToString();

                if (string.IsNullOrEmpty(content))
                    return new AiResponse { Error = "Empty response" };

                content = content.Trim();
                if (content.StartsWith("```"))
                {
                    int fn = content.IndexOf('\n');
                    if (fn > 0) content = content.Substring(fn + 1);
                    int lf = content.LastIndexOf("```");
                    if (lf >= 0) content = content.Substring(0, lf);
                }

                int js = content.IndexOf('{'), je = content.LastIndexOf('}');
                if (js >= 0 && je > js) content = content.Substring(js, je - js + 1);

                var parsed = JObject.Parse(content);
                return new AiResponse
                {
                    DesignLogic = parsed["design_logic"]?.ToString(),
                    Steps = parsed["steps"]?.ToObject<StepData[]>(),
                    ErrorDiagnosis = parsed["error_diagnosis"]?.ToString(),
                    Alternatives = parsed["alternatives"]?.ToObject<AlternativeData[]>(),
                    Confidence = parsed["confidence"]?.ToString() ?? "medium",
                    RawResponse = content
                };
            }
            catch (Exception ex)
            {
                return new AiResponse { Error = "Parse error: " + ex.Message, RawResponse = json };
            }
        }

        private string ExtractTextContent(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                var anthropicText = root["content"]?[0]?["text"]?.ToString();
                if (!string.IsNullOrEmpty(anthropicText)) return anthropicText.Trim();
                var openAiText = root["choices"]?[0]?["message"]?["content"]?.ToString();
                if (!string.IsNullOrEmpty(openAiText)) return openAiText.Trim();
                return null;
            }
            catch { return null; }
        }

        private bool ValidateStepSchema(AiResponse response)
        {
            if (response.Steps == null || response.Steps.Length == 0) return false;
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Extrude","Cut-Extrude","Fillet","Chamfer","Shell",
                "Revolve","Hole","LinearPattern","CircularPattern","Sweep","Loft"
            };
            foreach (var step in response.Steps)
            {
                if (string.IsNullOrEmpty(step.Feature) || !valid.Contains(step.Feature)) return false;
                if (step.Instructions == null || step.Instructions.Length < 1) return false;
                if (step.Parameters != null && step.Parameters.TryGetValue("depth_mm", out var d))
                {
                    try { if (Convert.ToDouble(d) < 0) return false; } catch { }
                }
            }
            return true;
        }

        private static string ExtractApiError(string body, int statusCode)
        {
            try
            {
                var j = JObject.Parse(body);
                return j["error"]?["message"]?.ToString() ?? j["message"]?.ToString() ?? $"HTTP {statusCode}";
            }
            catch { return $"HTTP {statusCode}"; }
        }

        public void UpdateApiKey(string newKey, string newProvider = null)
        {
            if (!string.IsNullOrEmpty(newProvider)) provider = newProvider.ToLower();
            ConfigureHeaders(newKey);
        }

        public void Dispose() => httpClient?.Dispose();
    }
}