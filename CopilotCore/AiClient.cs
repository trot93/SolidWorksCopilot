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

        // Cheap model for clarification — fast + token-efficient
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

        public async Task<ClarificationResponse> ClarifyGoalAsync(string designGoal)
        {
            try
            {
                var promptJson = PromptBuilder.BuildClarificationPrompt(designGoal);
                var cheapModel = GetClarifyModel();
                var response = await CallLLMAsync(promptJson, "Clarify", cheapModel);

                if (!string.IsNullOrEmpty(response.Error) || string.IsNullOrEmpty(response.RawResponse))
                    return new ClarificationResponse { NeedsClarification = false, SkipReason = "Clarification call failed" };

                // Parse the JSON response into ClarificationResponse
                return ParseClarificationResponse(response.RawResponse);
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

        private ClarificationResponse ParseClarificationResponse(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                return new ClarificationResponse
                {
                    NeedsClarification = root["needs_clarification"]?.Value<bool>() ?? false,
                    Questions = root["questions"]?.ToObject<ClarificationQuestion[]>(),
                    ResolvedContext = root["resolved_context"]?.ToString(),
                    SkipReason = root["skip_reason"]?.ToString()
                };
            }
            catch (Exception ex)
            {
                logger?.LogError("Failed to parse clarification response", ex);
                return new ClarificationResponse { NeedsClarification = false, SkipReason = "Parse error" };
            }
        }

        public async Task<AiResponse> GenerateStepsAsync(WorkspaceContext context, string clarificationAnswers = null, string resolvedContext = null)
        {
            var response = await CallLLMAsync(PromptBuilder.BuildModeAPrompt(context, clarificationAnswers, resolvedContext), "ModeA");

            // If schema fails, retry once with the same prompt
            if (response.Steps != null && !ValidateStepSchema(response))
            {
                logger?.LogError("Schema validation failed on first attempt — retrying...", null);
                response = await CallLLMAsync(PromptBuilder.BuildModeAPrompt(context, clarificationAnswers, resolvedContext), "ModeA_Retry");
            }

            if (response.Steps != null && !ValidateStepSchema(response))
            {
                response.Error = "AI failed to produce a valid CAD sequence after multiple attempts.";
                return response;
            }

            if (response.Steps != null && response.Steps.Length > 0 && string.IsNullOrEmpty(response.Error))
            {
                return await VerifyStepsAsync(response, context);
            }

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
                    logger?.LogError(string.Format("API error {0}: {1}", response.StatusCode, responseString), null);
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
            string systemMsg = (prompt["system"] != null) ? prompt["system"].ToString() : "You are an expert SOLIDWORKS engineer. Return ONLY JSON.";
            string userMsg = (prompt["user"] != null) ? prompt["user"].ToString() : string.Empty;

            string selectedModel = forceModel ?? ((provider == "openrouter") ? OpenRouterModel : "gpt-4o");

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

        private AiResponse ParseResponse(string json)
        {
            try
            {
                var root = JObject.Parse(json);

                // Truncation Detection
                var choices = root["choices"];
                if (choices != null && choices.HasValues)
                {
                    string finishReason = choices[0]["finish_reason"]?.ToString()
                                       ?? choices[0]["native_finish_reason"]?.ToString();

                    if (finishReason == "length" || finishReason == "MAX_TOKENS")
                    {
                        return new AiResponse
                        {
                            Error = "Response cut off — model hit token limit.",
                            RawResponse = json
                        };
                    }
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

                // MAP NEW FIELDS HERE
                return new AiResponse
                {
                    DesignLogic = parsed["design_logic"]?.ToString(),
                    Steps = (parsed["steps"] != null) ? parsed["steps"].ToObject<StepData[]>() : null,
                    ErrorDiagnosis = (parsed["error_diagnosis"] != null) ? parsed["error_diagnosis"].ToString() : null,
                    Alternatives = (parsed["alternatives"] != null) ? parsed["alternatives"].ToObject<AlternativeData[]>() : null,
                    Confidence = (parsed["confidence"] != null) ? parsed["confidence"].ToString() : "medium",
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

            var validFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "Extrude", "Cut-Extrude", "Fillet", "Chamfer", "Shell",
                "Revolve", "Hole", "LinearPattern", "CircularPattern", "Sweep", "Loft"
            };

            foreach (var step in response.Steps)
            {
                // Basic structural validation
                if (string.IsNullOrEmpty(step.Feature) || !validFeatures.Contains(step.Feature))
                    return false;

                if (step.Instructions == null || step.Instructions.Length < 1)
                    return false;

                // Dimension check: Only fail if a value is explicitly negative. 
                // Don't fail if depth_mm is missing (some features don't need it).
                if (step.Parameters != null && step.Parameters.TryGetValue("depth_mm", out var d))
                {
                    try
                    {
                        if (Convert.ToDouble(d) < 0) return false;
                    }
                    catch { /* Ignore parsing errors on parameters here */ }
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
                return errorMsg ?? string.Format("HTTP {0}", statusCode);
            }
            catch { return string.Format("HTTP {0}", statusCode); }
        }

        public void UpdateApiKey(string newKey, string newProvider = null)
        {
            if (!string.IsNullOrEmpty(newProvider)) provider = newProvider.ToLower();
            ConfigureHeaders(newKey);
        }

        public void Dispose() => httpClient?.Dispose();
    }
}