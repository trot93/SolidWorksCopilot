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
        private const string OpenRouterModel = "google/gemini-2.0-flash-001";
        private const string VerifyModel = "google/gemini-2.0-flash-001";

        // Cheap fast models for clarification
        private const string ClarifyModelAnthropic = "claude-3-haiku-20240307";
        private const string ClarifyModelOpenRouter = "openai/gpt-4o-mini";
        private const string ClarifyModelOpenAI = "gpt-4o-mini";

        public AiClient(string apiKey, string provider = "anthropic", SessionLogger logger = null)
        {
            this.logger = logger;
            this.provider = provider.ToLower();

            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(60);

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

        public async Task<(bool ok, string error)> ValidateKeyAsync()
        {
            try
            {
                string body;
                string endpoint;

                if (provider == "anthropic")
                {
                    endpoint = "https://api.anthropic.com/v1/messages";
                    body = "{\"model\":\"claude-3-haiku-20240307\",\"max_tokens\":1," +
                               "\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}";
                }
                else
                {
                    endpoint = (provider == "openrouter") ? OpenRouterEndpoint : "https://api.openai.com/v1/chat/completions";
                    string model = (provider == "openrouter") ? OpenRouterModel : "gpt-4o-mini";
                    body = "{\"model\":\"" + model + "\",\"max_tokens\":1," +
                           "\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}";
                }

                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(endpoint, content);

                if (response.IsSuccessStatusCode) return (true, null);

                var raw = await response.Content.ReadAsStringAsync();
                return (false, ExtractApiError(raw, (int)response.StatusCode));
            }
            catch (TaskCanceledException) { return (false, "Request timed out"); }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ── Clarification ─────────────────────────────────────────────────────

        public async Task<ClarificationResponse> ClarifyGoalAsync(string designGoal)
        {
            try
            {
                var promptJson = PromptBuilder.BuildClarificationPrompt(designGoal);
                var cheapModel = GetClarifyModel();

                // Use raw call — bypasses ParseResponse which only reads steps/confidence
                // and would discard needs_clarification / questions fields entirely
                var rawText = await CallLLMRawAsync(promptJson, cheapModel);

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

        private string GetClarifyModel()
        {
            switch (provider)
            {
                case "anthropic": return ClarifyModelAnthropic;
                case "openrouter": return ClarifyModelOpenRouter;
                case "openai": return ClarifyModelOpenAI;
                default: return ClarifyModelAnthropic;
            }
        }

        /// <summary>
        /// Calls the LLM and returns the raw extracted text content only —
        /// no AiResponse wrapping, no steps parsing.
        /// Used for clarification so the JSON fields are not discarded.
        /// </summary>
        private async Task<string> CallLLMRawAsync(string promptJson, string forceModel)
        {
            try
            {
                var requestBody = BuildRequestBody(promptJson, forceModel);
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                logger?.LogApiCall("Clarify_Raw", promptJson);

                var response = await httpClient.PostAsync(modelEndpoint, content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    logger?.LogError($"Clarify API error {response.StatusCode}: {responseString}", null);
                    return null;
                }

                logger?.LogApiResponse("Clarify_Raw", responseString);
                return ExtractTextContent(responseString);
            }
            catch (Exception ex)
            {
                logger?.LogError("CallLLMRawAsync failed", ex);
                return null;
            }
        }

        /// <summary>
        /// Extracts the text string from either Anthropic or OpenAI/OpenRouter response format.
        /// </summary>
        private string ExtractTextContent(string json)
        {
            try
            {
                var root = JObject.Parse(json);

                // Anthropic format: { "content": [ { "type": "text", "text": "..." } ] }
                var anthropicText = root["content"]?[0]?["text"]?.ToString();
                if (!string.IsNullOrEmpty(anthropicText)) return anthropicText.Trim();

                // OpenAI / OpenRouter format: { "choices": [ { "message": { "content": "..." } } ] }
                var openAiText = root["choices"]?[0]?["message"]?["content"]?.ToString();
                if (!string.IsNullOrEmpty(openAiText)) return openAiText.Trim();

                return null;
            }
            catch { return null; }
        }

        private ClarificationResponse ParseClarificationResponse(string text)
        {
            try
            {
                // Strip markdown fences if model ignored instructions
                text = text.Trim();
                if (text.StartsWith("```"))
                {
                    int firstNewline = text.IndexOf('\n');
                    if (firstNewline > 0) text = text.Substring(firstNewline + 1);
                    int lastFence = text.LastIndexOf("```");
                    if (lastFence >= 0) text = text.Substring(0, lastFence);
                    text = text.Trim();
                }

                // Extract outermost JSON object
                int jsonStart = text.IndexOf('{');
                int jsonEnd = text.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                    text = text.Substring(jsonStart, jsonEnd - jsonStart + 1);

                // Use snake_case deserialization to match LLM output field names
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

                // Safety: treat empty questions array as no clarification needed
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

        // ── Step generation ───────────────────────────────────────────────────

        public async Task<AiResponse> GenerateStepsAsync(WorkspaceContext context,
            string clarificationAnswers = null, string resolvedContext = null)
        {
            var response = await CallLLMAsync(
                PromptBuilder.BuildModeAPrompt(context, clarificationAnswers, resolvedContext), "ModeA");

            // If schema fails, retry once with the same prompt
            if (response.Steps != null && !ValidateStepSchema(response))
            {
                logger?.LogError("Schema validation failed on first attempt — retrying...", null);
                response = await CallLLMAsync(
                    PromptBuilder.BuildModeAPrompt(context, clarificationAnswers, resolvedContext), "ModeA_Retry");
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
            => await CallLLMAsync(PromptBuilder.BuildModeBPrompt(errorContext), "ModeB");

        private async Task<AiResponse> VerifyStepsAsync(AiResponse draft, WorkspaceContext context)
        {
            var verifyPrompt = new JObject
            {
                ["system"] = "You are a SOLIDWORKS DFM reviewer. Return ONLY JSON. Check for:\n" +
                             "1. Plane existence.\n2. Physical plausibility.\n" +
                             "Do NOT change geometry.",
                ["user"] = JsonConvert.SerializeObject(new
                {
                    steps = draft.Steps,
                    material = context.Material,
                    existing_features = context.Features?.Select(f => f.Name).ToList()
                })
            }.ToString();

            var verifyResponse = await CallLLMAsync(verifyPrompt, "Verify", VerifyModel);

            return (verifyResponse.Steps != null && string.IsNullOrEmpty(verifyResponse.Error))
                   ? verifyResponse
                   : draft;
        }

        private async Task<AiResponse> CallLLMAsync(string promptJson, string mode, string forceModel = null)
        {
            try
            {
                var requestBody = BuildRequestBody(promptJson, forceModel);
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                logger?.LogApiCall(mode, promptJson);

                var response = await httpClient.PostAsync(modelEndpoint, content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    string hint = ExtractApiError(responseString, (int)response.StatusCode);
                    logger?.LogError($"API error {response.StatusCode}: {responseString}", null);
                    return new AiResponse { Error = hint, RawResponse = responseString };
                }

                logger?.LogApiResponse(mode, responseString);
                return ParseResponse(responseString);
            }
            catch (TaskCanceledException) { return new AiResponse { Error = "Request timed out" }; }
            catch (Exception ex)
            {
                logger?.LogError("LLM call failed", ex);
                return new AiResponse { Error = ex.Message };
            }
        }

        private string BuildRequestBody(string promptJson, string forceModel = null)
        {
            var prompt = JObject.Parse(promptJson);
            string systemMsg = prompt["system"]?.ToString()
                               ?? "You are an expert SOLIDWORKS engineer. Return ONLY JSON.";
            string userMsg = prompt["user"]?.ToString() ?? string.Empty;

            string selectedModel = forceModel
                                   ?? (provider == "openrouter" ? OpenRouterModel :
                                       provider == "anthropic" ? "claude-3-5-haiku-20241022" :
                                                                   "gpt-4o");

            if (provider == "anthropic")
            {
                // Anthropic requires system at TOP LEVEL — NOT inside the messages array
                return new JObject
                {
                    ["model"] = selectedModel,
                    ["max_tokens"] = 4096,
                    ["temperature"] = 0,
                    ["system"] = systemMsg,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "user", ["content"] = userMsg }
                    }
                }.ToString();
            }
            else
            {
                // OpenAI / OpenRouter format
                return new JObject
                {
                    ["model"] = selectedModel,
                    ["max_tokens"] = 4096,
                    ["temperature"] = 0,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "system", ["content"] = systemMsg },
                        new JObject { ["role"] = "user",   ["content"] = userMsg   }
                    }
                }.ToString();
            }
        }

        private AiResponse ParseResponse(string json)
        {
            try
            {
                var root = JObject.Parse(json);

                // Truncation detection
                var choices = root["choices"];
                if (choices != null && choices.HasValues)
                {
                    string finishReason = choices[0]["finish_reason"]?.ToString()
                                      ?? choices[0]["native_finish_reason"]?.ToString();
                    if (finishReason == "length" || finishReason == "MAX_TOKENS")
                        return new AiResponse { Error = "Response cut off — model hit token limit.", RawResponse = json };
                }

                string content = root["content"]?[0]?["text"]?.ToString()
                              ?? root["choices"]?[0]?["message"]?["content"]?.ToString();

                if (string.IsNullOrEmpty(content))
                    return new AiResponse { Error = "Empty response" };

                // Clean JSON formatting
                content = content.Trim();
                if (content.StartsWith("```"))
                {
                    int firstNewline = content.IndexOf('\n');
                    if (firstNewline > 0) content = content.Substring(firstNewline + 1);
                    int lastFence = content.LastIndexOf("```");
                    if (lastFence >= 0) content = content.Substring(0, lastFence);
                }

                int jsonStart = content.IndexOf('{');
                int jsonEnd = content.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                    content = content.Substring(jsonStart, jsonEnd - jsonStart + 1);

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

        private bool ValidateStepSchema(AiResponse response)
        {
            if (response.Steps == null || response.Steps.Length == 0) return false;

            var validFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Extrude", "Cut-Extrude", "Fillet", "Chamfer", "Shell",
                "Revolve", "Hole", "LinearPattern", "CircularPattern", "Sweep", "Loft"
            };

            foreach (var step in response.Steps)
            {
                if (string.IsNullOrEmpty(step.Feature) || !validFeatures.Contains(step.Feature))
                    return false;

                if (step.Instructions == null || step.Instructions.Length < 1)
                    return false;

                if (step.Parameters != null && step.Parameters.TryGetValue("depth_mm", out var d))
                {
                    try { if (Convert.ToDouble(d) < 0) return false; }
                    catch { /* ignore parse errors on parameters */ }
                }
            }
            return true;
        }

        private static string ExtractApiError(string body, int statusCode)
        {
            try
            {
                var j = JObject.Parse(body);
                var errorMsg = j["error"]?["message"]?.ToString() ?? j["message"]?.ToString();
                return errorMsg ?? $"HTTP {statusCode}";
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