using System.Collections.Concurrent;

namespace SubtitleTranslator.Services
{
    /// <summary>
    /// Кэш переводов для избежания повторного перевода одинаковых текстов
    /// </summary>
    public static class TranslationCache
    {
        private static readonly ConcurrentDictionary<string, string> _cache = new();
        private static readonly ConcurrentDictionary<string, string> _cacheLang = new();

        /// <summary>
        /// Проверяет наличие перевода в кэше
        /// </summary>
        public static bool TryGetTranslation(string sourceText, out string translatedText)
        {
            return _cache.TryGetValue(sourceText, out translatedText);
        }

        /// <summary>
        /// Проверяет наличие языка перевода в кэше
        /// </summary>
        public static bool TryGetLangTranslation(string sourceText, out string translatedLang)
        {
            return _cacheLang.TryGetValue(sourceText, out translatedLang);
        }

        /// <summary>
        /// Добавляет перевод в кэш
        /// </summary>
        public static void AddTranslation(string sourceText, string translatedText, string in_lang)
        {
            _cacheLang.TryAdd(sourceText, in_lang);
            _cache.TryAdd(sourceText, translatedText);
        }

        /// <summary>
        /// Получает количество записей в кэше
        /// </summary>
        public static int Count => _cache.Count;

        /// <summary>
        /// Очищает кэш
        /// </summary>
        public static void Clear()
        {
            _cache.Clear();
        }
    }
}