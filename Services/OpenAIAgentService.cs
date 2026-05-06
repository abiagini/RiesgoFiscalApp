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

        private string Normalize(string text) => new string(text?.Where(char.IsLetterOrDigit).ToArray()).ToLower() ?? "";

        private (string Url, string Key, string Provider) GetProviderDetails(string modelId)
        {
            if (modelId.Contains("OpenAI")) return ("https://api.openai.com/v1/chat/completions", _config["AiConfig:OpenAIApiKey"] ?? string.Empty, "OpenAI");
            if (modelId.Contains("Gemini")) return ($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={_config["AiConfig:GeminiApiKey"]}", _config["AiConfig:GeminiApiKey"] ?? string.Empty, "Gemini");
            if (modelId.Contains("Claude")) return ("https://api.anthropic.com/v1/messages", _config["AiConfig:ClaudeApiKey"] ?? string.Empty, "Claude");
            throw new Exception("Motor no configurado.");
        }

        private object BuildPayload(string provider, string systemPrompt, string userPrompt)
        {
            if (provider == "Gemini") return new { contents = new[] { new { parts = new[] { new { text = $"{systemPrompt}\n\nInput: {userPrompt}" } } } } };
            return new { model = "gpt-4o", messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } } };
        }

        private string ExtractResponseText(string jsonResponse, string provider)
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            if (provider == "Gemini") return doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }

        public async Task<Customer> ExtractDataFromDocumentsAsync(List<Document> documents, Customer baseCustomer, string modelId)
        {
            var (url, key, provider) = GetProviderDetails(modelId);
            
            string prompt = "";
            string context = "";

            if (baseCustomer.IsJuridica)
            {
                string tEst = ExtractTextFromPdfs(documents.Where(d => d.Tipo == "Estatuto").ToList());
                string tIva = ExtractTextFromPdfs(documents.Where(d => d.Tipo == "Posición IVA").ToList());
                string tBal = ExtractTextFromPdfs(documents.Where(d => d.Tipo == "Balance").ToList());
                
                context = $"ESTATUTO: {tEst}\nIVA: {tIva}\nBALANCE: {tBal}";
                prompt = "Eres un Auditor Corporativo. Extrae datos de la empresa y responde en JSON: { 'cuit': '...', 'razon_social': '...', 'representante': '...', 'ventas_mensuales': 0.0, 'es_pep': bool }. Busca el representante legal y si es PEP.";
            }
            else
            {
                string tRec = ExtractTextFromPdfs(documents.Where(d => d.Tipo == "Recibo Sueldo").ToList());
                string tDdj = ExtractTextFromPdfs(documents.Where(d => d.Tipo == "DDJJ").ToList());
                
                context = $"RECIBO: {tRec}\nDDJJ: {tDdj}";
                prompt = "Eres un Auditor Bancario. Extrae datos y responde en JSON: { 'cuit': '...', 'nombre': '...', 'sueldo': 0.0, 'es_pep': bool, 'antiguedad_meses': 0 }. Analiza ambos textos.";
            }

            var payload = BuildPayload(provider, prompt, context);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (provider == "OpenAI") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            else request.Headers.Add("x-goog-api-key", key);
            
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _httpClient.SendAsync(request);
            var result = await response.Content.ReadAsStringAsync();
            var aiText = ExtractResponseText(result, provider);

            try {
                var json = aiText.Substring(aiText.IndexOf("{"), aiText.LastIndexOf("}") - aiText.IndexOf("{") + 1);
                var data = JsonSerializer.Deserialize<JsonElement>(json);
                baseCustomer.CuitExtraidoDeDocumento = data.GetProperty("cuit").GetString() ?? "";
                baseCustomer.EsPepSegunDocumento = data.GetProperty("es_pep").GetBoolean();

                if (baseCustomer.IsJuridica)
                {
                    baseCustomer.RazonSocial = data.GetProperty("razon_social").GetString() ?? "";
                    baseCustomer.RepresentanteLegalNombre = data.GetProperty("representante").GetString() ?? "";
                    baseCustomer.FacturacionAnual = data.GetProperty("ventas_mensuales").GetDecimal() * 12; // Estimación
                }
                else
                {
                    baseCustomer.NombreExtraidoRecibo = data.GetProperty("nombre").GetString() ?? "";
                    baseCustomer.SueldoNetoExtraido = data.GetProperty("sueldo").GetDecimal();
                    baseCustomer.MesesAntiguedadLaboral = data.GetProperty("antiguedad_meses").GetInt32();
                }
            } catch { }

            return baseCustomer;
        }

        public async Task<RiskAssessment> EvaluateRiskAsync(Customer customer, string modelId)
        {
            int score = 100;
            var logs = new List<string>();

            // 1. REGLA IDENTIDAD (KILLER)
            string declaredName = customer.IsJuridica ? customer.RazonSocial : customer.Nombre;
            string extractedName = customer.IsJuridica ? customer.RazonSocial : customer.NombreExtraidoRecibo;

            if (Normalize(declaredName) != Normalize(extractedName))
                return new RiskAssessment { ScoreRiesgo = 1, DictamenPreliminar = "No cumple", Observaciones = "FRAUDE: El nombre o razón social no coincide con la documentación." };

            // 2. REGLA PEP (COMPLIANCE)
            if (customer.EsPep != customer.EsPepSegunDocumento)
                return new RiskAssessment { ScoreRiesgo = 1, DictamenPreliminar = "No cumple", Observaciones = "RECHAZO: Declaración de PEP inconsistente con los documentos." };
            if (customer.EsPep) { score -= 60; logs.Add("PEP detectado (-60)."); }

            // 3. CAPACIDAD ECONÓMICA
            decimal income = customer.IsJuridica ? customer.FacturacionAnual / 12 : customer.SueldoNetoExtraido;
            if (income > 0) {
                if (customer.MontoOperado > income * 5) { score -= 70; logs.Add("AML: Monto operativo sospechoso vs ingresos (-70)."); }
            } else {
                score -= 30; logs.Add("ADVERTENCIA: No se pudo determinar capacidad de ingreso (-30).");
            }

            if (score < 1) score = 1;
            string dictamen = score >= 75 ? "Cumple" : (score >= 45 ? "Cumple con observaciones" : "No cumple");

            return new RiskAssessment {
                ScoreRiesgo = score,
                DictamenPreliminar = dictamen,
                Observaciones = logs.Any() ? string.Join(" | ", logs) : "Perfil verificado bajo estándares BCRA."
            };
        }

        private string ExtractTextFromPdfs(List<Document> docs) {
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