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
            string key = _config["AiConfig:GeminiApiKey"] ?? string.Empty;
            return ($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={key}", key, "Gemini");
        }

        public async Task<Customer> ExtractDataFromDocumentsAsync(List<Document> documents, Customer baseCustomer, string modelId)
        {
            var (url, key, provider) = GetProviderDetails(modelId);
            string allText = "";
            foreach (var doc in documents) allText += $"--- DOC: {doc.Tipo} ---\n" + ExtractTextFromPdf(doc.FilePath) + "\n";

            var payload = new {
                contents = new[] {
                    new {
                        parts = new[] {
                            new { text = $"Eres un Auditor Fiscal Argentino. Analiza estos documentos y responde ESTRICTAMENTE en JSON: {{ 'cuit': '...', 'nombre': '...', 'ingreso_estimado': 0.0, 'es_pep': bool, 'categoria': '...', 'fecha': 'DD/MM/YYYY' }}. Extrae el ingreso mensual (sueldo neto, o facturación promedio del IVA, o monto de factura C).\n\nDocumentos:\n{allText}" }
                        }
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("x-goog-api-key", key);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            
            var response = await _httpClient.SendAsync(request);
            var result = await response.Content.ReadAsStringAsync();
            
            using var docRes = JsonDocument.Parse(result);
            string aiText = docRes.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";

            try {
                var json = aiText.Substring(aiText.IndexOf("{"), aiText.LastIndexOf("}") - aiText.IndexOf("{") + 1);
                var data = JsonSerializer.Deserialize<JsonElement>(json);
                baseCustomer.CuitExtraido = data.GetProperty("cuit").GetString() ?? "";
                baseCustomer.NombreExtraido = data.GetProperty("nombre").GetString() ?? "";
                baseCustomer.IngresoMensualValidado = data.GetProperty("ingreso_estimado").GetDecimal();
                baseCustomer.EsPepDocumento = data.GetProperty("es_pep").GetBoolean();
                baseCustomer.CategoriaMonotributo = data.GetProperty("categoria").GetString() ?? "";
                baseCustomer.FechaDocumento = data.GetProperty("fecha").GetString() ?? "";
            } catch { }

            return baseCustomer;
        }

        public async Task<RiskAssessment> EvaluateRiskAsync(Customer customer, string modelId)
        {
            int score = 100;
            var logs = new List<string>();

            // 1. Regla Identidad
            if (customer.CuitCuil.Replace("-","") != customer.CuitExtraido.Replace("-",""))
                return new RiskAssessment { ScoreRiesgo = 1, DictamenPreliminar = "No cumple", Observaciones = "Error de Identidad: CUIT no coincide con los documentos." };

            // 2. Regla PEP
            if (customer.EsPep != customer.EsPepDocumento)
                return new RiskAssessment { ScoreRiesgo = 1, DictamenPreliminar = "No cumple", Observaciones = "Inconsistencia PEP: Declaración jurada no coincide con el perfil detectado." };
            if (customer.EsPep) { score -= 40; logs.Add("Perfil PEP: Riesgo Incrementado (-40)."); }

            // 3. Capacidad Económica por Perfil
            if (customer.IngresoMensualValidado > 0) {
                decimal ratio = customer.MontoOperado / customer.IngresoMensualValidado;
                if (ratio > 4) { score -= 60; logs.Add("Alerta AML: El monto operado supera excesivamente el ingreso mensual (-60)."); }
                else if (ratio > 2) { score -= 20; logs.Add("Precaución: Monto operado elevado vs ingresos (-20)."); }
            }

            // 4. Reglas específicas de Argentina
            if (customer.Clasificacion.Contains("Monotributista") && customer.MontoOperado > 3000000) {
                score -= 30; logs.Add("Monotributo: Monto operado cerca de límites de exclusión (-30).");
            }

            if (score < 1) score = 1;
            string dictamen = score >= 70 ? "Cumple" : (score >= 40 ? "Cumple con Observaciones" : "No cumple");

            return new RiskAssessment {
                ScoreRiesgo = score,
                DictamenPreliminar = dictamen,
                Observaciones = logs.Any() ? string.Join(" | ", logs) : "Perfil validado con éxito."
            };
        }

        private string ExtractTextFromPdf(string path) {
            try {
                using (var pdf = PdfDocument.Open(path)) {
                    return string.Join(" ", pdf.GetPages().Select(p => p.Text));
                }
            } catch { return ""; }
        }
    }
}