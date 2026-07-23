using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SubtitleTranslator.Models;

namespace SubtitleTranslator.Services
{
    public class TranslationService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public TranslationService(string baseUrl = "http://localhost:8000")
        {
            _baseUrl = baseUrl;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
        }

        public async Task<bool> CheckHealthAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/health");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> TranslateTextAsync(string text)
        {
            // Проверяем кэш
            if (TranslationCache.TryGetTranslation(text, out var cachedTranslation))
            {
                Logger.LogInfo($"Используем кэшированный перевод для: {text.Substring(0, Math.Min(30, text.Length))}...");
                return cachedTranslation;
            }

            var payload = new { text = text };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_baseUrl}/translate", content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var translatedText = doc.RootElement.GetProperty("translated_text").GetString();

            // Сохраняем в кэш
            TranslationCache.AddTranslation(text, translatedText, "");

            return translatedText;
        }

        public async Task<List<(string Translated, string Lang)>> TranslateBatchAsync(List<string> texts)
        {
            var results = new List<(string Translated, string Lang)>();
            var textsToTranslate = new List<string>();
            var indicesToTranslate = new List<int>();

            // Проверяем кэш для каждого текста
            for (int i = 0; i < texts.Count; i++)
            {
                if (TranslationCache.TryGetTranslation(texts[i], out var cachedTranslation))
                {
                    var lang = "Кэш";
                    TranslationCache.TryGetLangTranslation(texts[i], out lang);                    
                    results.Add((cachedTranslation, lang));
                    Logger.LogInfo($"[{i + 1}/{texts.Count}] Из кэша: {texts[i].Substring(0, Math.Min(30, texts[i].Length))}...");
                }
                else
                {
                    results.Add(("", "")); // Заглушка
                    textsToTranslate.Add(texts[i]);
                    indicesToTranslate.Add(i);
                }
            }

            // Переводим только те, которых нет в кэше
            if (textsToTranslate.Count > 0)
            {
                Logger.LogProgress($"Перевод {textsToTranslate.Count} новых текстов ({texts.Count - textsToTranslate.Count} из кэша)...");

                var payload = new { texts = textsToTranslate };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/translate/batch", content);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var translations = doc.RootElement.GetProperty("translations");

                int translationIndex = 0;
                foreach (var item in translations.EnumerateArray())
                {
                    var translated = item.GetProperty("translated_text").GetString();
                    var lang = item.GetProperty("detected_lang_name").GetString();

                    int originalIndex = indicesToTranslate[translationIndex];
                    results[originalIndex] = (translated, lang);

                    // Сохраняем в кэш
                    TranslationCache.AddTranslation(textsToTranslate[translationIndex], translated, lang);

                    translationIndex++;
                }
            }

            return results;
        }
    }
}