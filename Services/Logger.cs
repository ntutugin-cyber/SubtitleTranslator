using SubtitleTranslator.Models;
using SubtitleTranslator.ViewModels;
using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace SubtitleTranslator.Services
{
    public static class Logger
    {
        public static event Action<string> LogAdded;
        public static event Action<string, double, bool> StatusChange;

        public static void Log(string in_message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logMessage = $"[{timestamp}] {in_message}";

            // Вызываем событие для обновления UI
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                LogAdded?.Invoke(logMessage);
            });
        }

        public static void setStatus(string in_statusText, double in_progress = 0, bool in_isIndeterminateProgress = false)
        {
            if (StatusChange != null)
                StatusChange(in_statusText, in_progress, in_isIndeterminateProgress);
        }

        public static void LogInfo(string message)
        {
            Log($"ℹ️ {message}");
        }

        public static void LogSuccess(string message)
        {
            Log($"✅ {message}");
        }

        public static void LogWarning(string message)
        {
            Log($"⚠️ {message}");
        }

        public static void LogError(string message)
        {
            Log($"❌ {message}");
        }

        public static void LogProgress(string message)
        {
            Log($"🔄 {message}");
        }

        public static string getInfoDurationString(DateTime in_dateStart)
        {
            var dateEnd = DateTime.Now;
            var duration = dateEnd - in_dateStart;

            // Форматируем продолжительность процесса в hh:mm:ss
            string durationFormatted = $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";

            var ret = $" за {durationFormatted}";

            return ret;
        }

        public static List<SubtitleItem>? getSubs(string in_jsonText)
        {
            // Проверяем наличие метаданных перед JSON
            if (in_jsonText.Contains("--- Результат распознавания ---"))
            {
                Logger.LogInfo("Обнаружены метаданные распознавания");

                // Извлекаем и логируем метаданные
                ParseAndLogMetadata(in_jsonText);

                // Находим начало JSON (первая '[')
                int jsonStartIndex = in_jsonText.IndexOf('[');
                if (jsonStartIndex >= 0)
                {
                    in_jsonText = in_jsonText.Substring(jsonStartIndex);
                    Logger.LogSuccess("Метаданные удалены, извлечён чистый JSON");
                }
                else
                    Logger.LogWarning("Не найдено начало JSON массива");
            }

            var ret = RawJsonViewModel.tryDeserializeJsonSub(in_jsonText);
            if (ret?.Any() != true && !string.IsNullOrWhiteSpace(in_jsonText))
            {
                var lines = Regex.Split(in_jsonText, "[\r\n]").ToList();
                ret = SubtitleManager.parseFromLines(lines);
            }

            return ret;
        }

        private static void ParseAndLogMetadata(string text)
        {
            try
            {
                var lines = text.Split('\n');

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    // Парсим строку "Вход: 4830 токенов (речь: 4771, текст: 59, паддинг: 0)"
                    if (trimmedLine.StartsWith("Вход:") && trimmedLine.Contains("токенов"))
                    {
                        Logger.LogInfo($"📊 {trimmedLine}");
                    }
                    // Парсим строку "Выход: 6896 токенов | Время: 1030.37с"
                    else if (trimmedLine.StartsWith("Выход:") && trimmedLine.Contains("Время:"))
                    {
                        // Извлекаем время в секундах
                        int timeIndex = trimmedLine.IndexOf("Время:");
                        if (timeIndex >= 0)
                        {
                            string timePart = trimmedLine.Substring(timeIndex + 6).Trim();
                            // Убираем "с" в конце
                            timePart = timePart.Replace("с", "").Trim().Replace(".", ",");

                            if (double.TryParse(timePart, out double seconds))
                            {
                                // Форматируем в hh:mm:ss
                                var timeSpan = TimeSpan.FromSeconds(seconds);
                                string formattedTime = $"{(int)timeSpan.TotalHours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";

                                Logger.LogInfo($"⏱️ Время распознавания: {formattedTime} ({seconds:F2}с)");
                            }
                        }

                        // Логируем количество токенов
                        Logger.LogInfo($"📤 {trimmedLine.Split('|')[0].Trim()}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Не удалось распарсить метаданные: {ex.Message}");
            }
        }

        /// <summary> Попытаться удалить все файлы из указанной папки с указанным расширением </summary>
        public static void tryDeleteFiles(string in_targetDirectory, string in_extention)
        {
            // Проверяем, существует ли указанная папка
            if (!Directory.Exists(in_targetDirectory))
            {
                Logger.LogError($"Указанная папка не найдена: {in_targetDirectory}");
                return;
            }

            try
            {
                // Находим все файлы .mp3 только в этой папке
                string[] mp3Files = Directory.GetFiles(in_targetDirectory, in_extention, SearchOption.TopDirectoryOnly);

                foreach (string file in mp3Files)
                {
                    File.Delete(file);
                    //Logger.LogSuccess($"Удален файл: {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка при удалении: {ex.Message}");
            }
        }
    }
}