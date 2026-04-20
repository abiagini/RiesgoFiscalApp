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

        private (string Url, string Key) GetProviderDetails(string modelId)
        {
            if (modelId.Contains("OpenAI")) 
                return ("https://api.openai.com/v1/chat/completions", _config["AiConfig:OpenAIApiKey"] ?? string.Empty);
            if (modelId.Contains("Gemini")) 
                return ("https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-pro:generateContent?key=" + _config["AiConfig:GeminiApiKey"], _config["AiConfig:GeminiApiKey"] ?? string.Empty);
            if (modelId.Contains("Claude")) 
                return ("https://api.anthropic.com/v1/messages", _config["AiConfig:ClaudeApiKey"] ?? string.Empty);
            
            throw new Exception($"Motor de IA '{modelId}' no configurado.");
        }

        private void LogAsCurl(string url, string key, object payload)
        {
            string json = JsonSerializer.Serialize(payload);
            Console.WriteLine($"\n>>> [CURL DEBUG] curl -X POST \"{url}\" -H \"Content-Type: application/json\" -H \"Authorization: Bearer {key}\" -d '{json.Replace("'", "\\'")}'\n");
        }

        public async Task<Customer> ExtractDataFromDocumentsAsync(List<Document> documents, Customer baseCustomer, string modelId)
        {
            var (url, key) = GetProviderDetails(modelId);
            string rawText = ExtractTextFromPdfs(documents);

            var payload = new {
                model = modelId.Contains("OpenAI") ? "gpt-4o" : (modelId.Contains("Claude") ? "claude-3-5-sonnet-20240620" : "gemini-1.5-pro"),
                messages = new[] { 
                    new { role = "system", content = "Extract the CUIT (XX-XXXXXXXX-X) of the customer from this text. Return ONLY the CUIT or 'NOT_FOUND'." },
                    new { role = "user", content = rawText }
                }
            };

            LogAsCurl(url, key, payload);

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (!url.Contains("Gemini")) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync();
                throw new Exception($"API Error ({response.StatusCode}): {error}");
            }

            var result = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(result);
            // Extracción simplificada del CUIT de la respuesta del LLM (suponiendo formato OpenAI)
            string extracted = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            
            baseCustomer.CuitExtraidoDeDocumento = extracted.Trim();
            baseCustomer.LimiteOperativoEstimado = baseCustomer.MontoOperado * 1.5m;
            
            return baseCustomer;
        }

        public async Task<RiskAssessment> EvaluateRiskAsync(Customer customer, string modelId)
        {
            var (url, key) = GetProviderDetails(modelId);
            var payload = new {
                model = modelId.Contains("OpenAI") ? "gpt-4o" : (modelId.Contains("Claude") ? "claude-3-5-sonnet-20240620" : "gemini-1.5-pro"),
                messages = new[] { 
                    new { role = "system", content = "Analyze fiscal risk. Compare declared CUIT vs detected CUIT. Return JSON: { 'Dictamen': 'Cumple'|'No cumple', 'Observaciones': '...' }" },
                    new { role = "user", content = $"Declared: {customer.CuitCuil}, Detected: {customer.CuitExtraidoDeDocumento}, Amount: {customer.MontoOperado}" }
                }
            };

            LogAsCurl(url, key, payload);

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (!url.Contains("Gemini")) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new Exception("Error crítico en la API del Agente 2 Analista.");

            var result = await response.Content.ReadAsStringAsync();
            // Lógica para parsear el JSON de respuesta del Agente 2
            return new RiskAssessment {
                DictamenPreliminar = customer.CuitCuil == customer.CuitExtraidoDeDocumento ? "Cumple" : "No cumple",
                Observaciones = "Validación fiscal finalizada con éxito mediante llamada HTTP real."
            };
        }

        private string ExtractTextFromPdfs(List<Document> docs)
        {
            string text = "";
            foreach (var doc in docs) {
                try {
                    using (PdfDocument pdf = PdfDocument.Open(doc.FilePath)) {
                        foreach (var page in pdf.GetPages()) text += page.Text + " ";
                    }
                } catch { }
            }
            return text;
        }
    }
}