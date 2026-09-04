using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace SubtitleTranslator.Services
{
    public class HiggsApiService
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
        private string _baseUrl = "http://127.0.0.1:7077";
        private string _apiKey = "";

        public void Configure(string baseUrl, string apiKey)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _apiKey = apiKey;
            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        public async Task<bool> CheckHealthAsync()
        {
            try
            {
                var r = await _http.GetAsync($"{_baseUrl}/health");
                return r.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> GetStatusAsync()
        {
            try
            {
                var r = await _http.GetAsync($"{_baseUrl}/v1/status");
                r.EnsureSuccessStatusCode();
                return await r.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                return $"Ошибка: {ex.Message}";
            }
        }

        public async Task<Dictionary<string, string>> GetModelsAsync()
        {
            try
            {
                var r = await _http.GetAsync($"{_baseUrl}/v1/models");
                r.EnsureSuccessStatusCode();
                var json = await r.Content.ReadAsStringAsync();
                var jobj = JObject.Parse(json);
                var models = new Dictionary<string, string>();

                if (jobj.TryGetValue("data", out var dataToken) && dataToken is JArray dataArray)
                {
                    foreach (var item in dataArray)
                    {
                        if (item.Type == JTokenType.Object)
                        {
                            var id = item["id"]?.ToString() ?? "";
                            var name = item["name"]?.ToString() ?? item["id"]?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(id))
                                models[id] = name;
                        }
                        else if (item.Type == JTokenType.String)
                        {
                            var itemStr = item.ToString();
                            models[itemStr] = itemStr;
                        }
                    }
                }

                return models;
            }
            catch (Exception ex)
            {
                return new();
            }
        }

        public async Task<Dictionary<string, string>> GetSpeakersAsync()
        {
            var r = await _http.GetAsync($"{_baseUrl}/v1/higgs/speakers");
            r.EnsureSuccessStatusCode();
            var json = await r.Content.ReadAsStringAsync();
            var jobj = JObject.Parse(json);
            var speakers = new Dictionary<string, string>();

            if (jobj.TryGetValue("data", out var dataToken))
            {
                if (dataToken is JArray dataArray)
                {
                    foreach (var item in dataArray)
                    {
                        if (item.Type == JTokenType.String)
                        {
                            var itemStr = item.ToString();
                            speakers[itemStr] = itemStr;
                        }
                        else if (item.Type == JTokenType.Object)
                        {
                            var id = item["id"]?.ToString() ?? item["name"]?.ToString();
                            var name = item["name"]?.ToString() ?? item["id"]?.ToString();
                            if (!string.IsNullOrEmpty(id))
                                speakers[id] = name;
                        }
                    }
                }
            }
            else if (jobj.Type == JTokenType.Array)
            {
                var arr = jobj.ToObject<List<string>>();
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        speakers[item] = item;
                    }
                }
            }

            return speakers;
        }

        public async Task SynthesizeToFileAsync(
            string input,
            string voice,
            string format,
            string outputPath,
            CancellationToken ct)
        {
            var payload = new Dictionary<string, object>
            {
                ["input"] = input,
                ["voice"] = voice,
                ["max_tokens"] = 7000,
                ["response_format"] = format
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/audio/speech");
            req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(outputPath);
            await stream.CopyToAsync(fs, ct);
        }

        public async Task CancelAsync()
        {
            try
            {
                await _http.PostAsync($"{_baseUrl}/v1/higgs/cancel", null);
            }
            catch { }
        }
    }

    public static class TextSplitter
    {
        public static List<string> Split(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return new();
            var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?…])\s+");
            var chunks = new List<string>();
            var sb = new StringBuilder();
            foreach (var s in sentences)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (sb.Length + s.Length + 1 > maxChars && sb.Length > 0)
                {
                    chunks.Add(sb.ToString().Trim());
                    sb.Clear();
                }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(s);
            }
            if (sb.Length > 0) chunks.Add(sb.ToString().Trim());
            return chunks;
        }
    }

public class VoiceItem : INotifyPropertyChanged
    {
        private string _psevdonim = "";
        private string _name = "";
        private string _previousName = "";
        private int _countLinesText = 0;
        private int _countSymText = 0;
        private string _value = "";

        /// <summary> Псевдоним из текста </summary>
        public string Psevdonim
        {
            get => _psevdonim;
            set { _psevdonim = value; OnPropertyChanged(); }
        }

        public string PreviousName => _previousName;

        public string Name
        {
            get => _name;
            set
            {
                _previousName = _name;   // сохраняем старое значение ДО изменения
                _name = value;
                OnPropertyChanged();
            }
        }

        /// <summary> Количество строк текста </summary>
        public int CountLinesText
        {
            get => _countLinesText;
            set { _countLinesText = value; OnPropertyChanged(); }
        }

        /// <summary> Количество символов текста </summary>
        public int CountSymText
        {
            get => _countSymText;
            set { _countSymText = value; OnPropertyChanged(); }
        }

        public string Value
        {
            get => _value;
            set { _value = value; OnPropertyChanged(); }
        }

        public override string ToString() => Name;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}