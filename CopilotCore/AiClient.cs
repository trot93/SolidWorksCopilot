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

        private const string ClarifyModelAnthropic = "claude-3-haiku-20240307";
        private const string ClarifyModelOpenRouter = "openai/gpt-4o-mini";
        private const string ClarifyModelOpenAI = "gpt-4o-mini";

        public AiClient(string apiKey, string provider = "anthropic", SessionLogger logger = null)
        {
            this.logger = logger;
            this.provider = provider.ToLower();
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
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

        /// <summary>
        /// Validates the current API key against the provider.
        /// Returns three values:
        ///   ok          — true if the key is accepted.
        ///   isAuthError — true only on a definitive 401/403 rejection.
        ///                 false for timeouts, DNS failures, or any other
        ///                 network-level problem so callers can distinguish
        ///                 "bad key" from "bad network".
        ///   error       — human-readable error string when ok is false.
        /// </summary>
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
                    string model = provider == "openrouter" ? OpenRouterModel : "gpt-4o-mini";
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

        // ── Clarification (Sprint 2: accepts optional image) ──────────────────

        public async Task<ClarificationResponse> ClarifyGoalAsync(
            string designGoal,
            string imageBase64 = null,
            string imageMediaType = null)
        {
            try
            {
                var promptJson = PromptBuilder.BuildClarificationPrompt(designGoal);
                var cheapModel = GetClarifyModel();

                var rawText = await CallLLMRawAsync(promptJson, cheapModel, imageBase64, imageMediaType);

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
        /// Raw LLM call that returns extracted text content only.
        /// Used for clarification to avoid ParseResponse discarding non-step fields.
        /// Supports optional image injection (Sprint 2).
        /// </summary>
        private async Task<string> CallLLMRawAsync(
            string promptJson,
            string forceModel,
            string imageBase64 = null,
            string imageMediaType = null)
        {
            try
            {
                var requestBody = BuildRequestBody(promptJson, forceModel, imageBase64, imageMediaType);
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
        /// Extracts text from Anthropic or OpenAI/OpenRouter response envelope.
        /// </summary>
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

        // ── Step generation (Sprint 2: image flows via WorkspaceContext) ──────

        public async Task<AiResponse> GenerateStepsAsync(
            WorkspaceContext context,
            string clarificationAnswers = null,
            string resolvedContext = null)
        {
            // Image is carried inside context.ImageBase64 / context.ImageMediaType
            var response = await CallLLMAsync(
                PromptBuilder.BuildModeAPrompt(context, clarificationAnswers, resolvedContext),
                "ModeA",
                imageBase64: context.ImageBase64,
                imageMediaType: context.ImageMediaType);

            if (response.Steps != null && !ValidateStepSchema(response))
            {
                logger?.LogError("Schema validation failed on first attempt — retrying...", null);
                response = await CallLLMAsync(
                    PromptBuilder.BuildModeAPrompt(context, clarificationAnswers, resolvedContext),
                    "ModeA_Retry",
                    imageBase64: context.ImageBase64,
                    imageMediaType: context.ImageMediaType);
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
                ["system"] = "You are a SOLIDWORKS DFM reviewer. Return ONLY JSON. Check for:\n1. Plane existence.\n2. Physical plausibility.\nDo NOT change geometry.",
                ["user"] = JsonConvert.SerializeObject(new
                {
                    steps = draft.Steps,
                    material = context.Material,
                    existing_features = context.Features?.Select(f => f.Name).ToList()
                })
            }.ToString();

            // Verification does not need the image — geometry check only
            var verifyResponse = await CallLLMAsync(verifyPrompt, "Verify", VerifyModel);
            return (verifyResponse.Steps != null && string.IsNullOrEmpty(verifyResponse.Error))
                   ? verifyResponse : draft;
        }

        private async Task<AiResponse> CallLLMAsync(
            string promptJson,
            string mode,
            string forceModel = null,
            string imageBase64 = null,
            string imageMediaType = null)
        {
            try
            {
                var requestBody = BuildRequestBody(promptJson, forceModel, imageBase64, imageMediaType);
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

        /// <summary>
        /// Builds the provider-specific request body.
        /// Sprint 2: when imageBase64 is supplied the user message becomes a
        /// multipart content array [image_block, text_block] instead of a plain string,
        /// using the vision format supported by Anthropic, OpenAI, and OpenRouter.
        /// </summary>
        private string BuildRequestBody(
            string promptJson,
            string forceModel = null,
            string imageBase64 = null,
            string imageMediaType = null)
        {
            var prompt = JObject.Parse(promptJson);
            string sysMsg = prompt["system"]?.ToString()
                              ?? "You are an expert SOLIDWORKS engineer. Return ONLY JSON.";
            string usrMsg = prompt["user"]?.ToString() ?? string.Empty;

            string selectedModel = forceModel
                                   ?? (provider == "openrouter" ? OpenRouterModel :
                                       provider == "anthropic" ? "claude-3-5-haiku-20241022" :
                                                                   "gpt-4o");

            bool hasImage = !string.IsNullOrEmpty(imageBase64) && !string.IsNullOrEmpty(imageMediaType);

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
                else
                {
                    userContent = usrMsg;
                }

                return new JObject
                {
                    ["model"] = selectedModel,
                    ["max_tokens"] = 4096,
                    ["temperature"] = 0,
                    ["system"] = sysMsg,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "user", ["content"] = userContent }
                    }
                }.ToString();
            }
            else
            {
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
                else
                {
                    userContent = usrMsg;
                }

                return new JObject
                {
                    ["model"] = selectedModel,
                    ["max_tokens"] = 4096,
                    ["temperature"] = 0,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "system", ["content"] = sysMsg },
                        new JObject { ["role"] = "user",   ["content"] = userContent }
                    }
                }.ToString();
            }
        }

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
                        return new AiResponse { Error = "Response cut off — model hit token limit.", RawResponse = json };
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