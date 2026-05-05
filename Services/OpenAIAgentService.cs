using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using RiesgoFiscalApp.Models;
using UglyToad.PdfPig;

namespace RiesgoFiscalApp.Services
{
    public class OpenAIAgentService : IAgentService
    {
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly IConfiguration _config;

        public OpenAIAgentService()
        {
            _config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();
        }

        private (string Url, string Key, string Provider) GetProviderDetails(string modelId)
        {
            if (modelId.Contains("OpenAI")) 
                return ("https://api.openai.com/v1/chat/completions", _config["AiConfig:OpenAIApiKey"] ?? string.Empty, "OpenAI");
            
            if (modelId.Contains("Gemini")) 
            {
                // Usando gemini-3-flash-preview según requerimiento
                string key = _config["AiConfig:GeminiApiKey"] ?? string.Empty;
                return ($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={key}", key, "Gemini");
            }

            if (modelId.Contains("Claude")) 
                return ("https://api.anthropic.com/v1/messages", _config["AiConfig:ClaudeApiKey"] ?? string.Empty, "Claude");
            
            throw new Exception($"Motor de IA '{modelId}' no configurado.");
        }

        private object BuildPayload(string provider, string systemPrompt, string userPrompt)
        {
            if (provider == "Gemini")
            {
                return new {
                    contents = new[] {
                        new {
                            parts = new[] {
                                new { text = $"{systemPrompt}\n\nEntrada: {userPrompt}" }
                            }
                        }
                    }
                };
            }
            if (provider == "Claude")
            {
                return new {
                    model = "claude-3-5-sonnet-20240620",
                    max_tokens = 1024,
                    system = systemPrompt,
                    messages = new[] { new { role = "user", content = userPrompt } }
                };
            }
            return new {
                model = "gpt-4o",
                messages = new[] { 
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };
        }

        private string ExtractResponseText(string jsonResponse, string provider)
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            if (provider == "Gemini")
            {
                if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    return candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                }
                throw new Exception("La respuesta de Gemini-3 no contiene candidatos válidos.");
            }
            if (provider == "Claude")
                return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
            
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }

        private void LogAsCurl(string url, string key, object payload, string provider)
        {
            string json = JsonSerializer.Serialize(payload);
            Console.WriteLine($"\n>>> [DOCS-COMPLIANT CURL - {provider}]");
            Console.WriteLine($"curl -X POST \"{url}\" \\");
            Console.WriteLine("  -H \"Content-Type: application/json\" \\");
            if (provider == "Gemini") Console.WriteLine("  -H \"x-goog-api-key: " + key + "\"\n");
            else if (provider == "Claude") Console.WriteLine("  -H \"x-api-key: " + key + "\" \\ -H \"anthropic-version: 2023-06-01\" \\");
            else if (provider == "OpenAI") Console.WriteLine("  -H \"Authorization: Bearer " + key + "\" \\");
            Console.WriteLine($"  -d '{json.Replace("'", "\\'")}'\n");
        }

        public async Task<Customer> ExtractDataFromDocumentsAsync(List<Document> documents, Customer baseCustomer, string modelId)
        {
            var (url, key, provider) = GetProviderDetails(modelId);
            string rawText = ExtractTextFromPdfs(documents);

            if (string.IsNullOrWhiteSpace(rawText))
                throw new Exception("El PDF no tiene texto legible para extraer.");

            var payload = BuildPayload(provider, 
                "Extract the CUIT (format XX-XXXXXXXX-X). Respond ONLY with the CUIT.", 
                rawText);

            LogAsCurl(url, key, payload, provider);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (provider == "Gemini") request.Headers.Add("x-goog-api-key", key);
            if (provider == "OpenAI") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (provider == "Claude") {
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            string result = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"\n<<< [OFFICIAL API RESPONSE - {provider}]");
            Console.WriteLine(result);
            Console.WriteLine("---------------------------------------------------------\n");

            if (!response.IsSuccessStatusCode)
                throw new Exception($"API Gemini Error ({response.StatusCode}): {result}");

            baseCustomer.CuitExtraidoDeDocumento = ExtractResponseText(result, provider).Trim();
            baseCustomer.LimiteOperativoEstimado = baseCustomer.MontoOperado * 1.5m;
            
            return baseCustomer;
        }

        public async Task<RiskAssessment> EvaluateRiskAsync(Customer customer, string modelId)
        {
            var (url, key, provider) = GetProviderDetails(modelId);
            var payload = BuildPayload(provider,
                "Analyze risk. Return JSON { 'Dictamen': 'Cumple'|'No cumple' }.",
                $"CUIT: {customer.CuitExtraidoDeDocumento}, Amount: {customer.MontoOperado}");

            LogAsCurl(url, key, payload, provider);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (provider == "Gemini") request.Headers.Add("x-goog-api-key", key);
            if (provider == "OpenAI") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (provider == "Claude") {
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }

            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            string result = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"\n<<< [OFFICIAL API RESPONSE - {provider}]");
            Console.WriteLine(result);
            Console.WriteLine("---------------------------------------------------------\n");

            if (!response.IsSuccessStatusCode) throw new Exception($"Error en Agente 2 Gemini: {result}");

            string textResult = ExtractResponseText(result, provider);

            return new RiskAssessment {
                DictamenPreliminar = textResult.Contains("Cumple") ? "Cumple" : "No cumple",
                Observaciones = $"Analizado por Gemini-3-Flash."
            };
        }

        private string ExtractTextFromPdfs(List<Document> docs)
        {
            StringBuilder sb = new StringBuilder();
            foreach (var doc in docs) {
                try {
                    using (PdfDocument pdf = PdfDocument.Open(doc.FilePath)) {
                        foreach (var page in pdf.GetPages()) sb.Append(page.Text).Append(" ");
                    }
                } catch { }
            }
            return sb.ToString().Trim();
        }
    }
}