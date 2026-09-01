using SubtitleTranslator.Models;
using SubtitleTranslator.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Text.Encodings.Web; // Обязательно добавьте этот using!

namespace SubtitleTranslator.ViewModels
{
    public class RawJsonViewModel : INotifyPropertyChanged
    {
        private readonly TranslationService _translationService;
        private string _rawJsonText;
        private string _resultText;
        private bool _enableTranslation;
        private bool _includeSpeakers;
        private bool _includeSounds;
        private bool _isNoRus;
        private bool _noMoreThanTwo;
        private bool _noMoreThanTen;
        private bool _isProcessing;
        private bool _isNeedSpeakerVoice;
        private string _statusText;

        public bool IsNoRus
        {
            get => _isNoRus;
            set { _isNoRus = value; OnPropertyChanged(); }
        }

        public bool NoMoreThanTen
        {
            get => _noMoreThanTen;
            set { _noMoreThanTen = value; OnPropertyChanged(); }
        }

        public bool NoMoreThanTwo
        {
            get => _noMoreThanTwo;
            set { _noMoreThanTwo = value; OnPropertyChanged(); }
        }

        public bool IncludeSounds
        {
            get => _includeSounds;
            set { _includeSounds = value; OnPropertyChanged(); }
        }

        public string RawJsonText
        {
            get => _rawJsonText;
            set { _rawJsonText = value; OnPropertyChanged(); }
        }

        public string ResultText
        {
            get => _resultText;
            set { _resultText = value; OnPropertyChanged(); }
        }

        public bool EnableTranslation
        {
            get => _enableTranslation;
            set { _enableTranslation = value; OnPropertyChanged(); }
        }

        public bool IncludeSpeakers
        {
            get => _includeSpeakers;
            set { _includeSpeakers = value; OnPropertyChanged(); }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set { _isProcessing = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public bool IsNeedSpeakerVoice
        {
            get => _isNeedSpeakerVoice;
            set { _isNeedSpeakerVoice = value; OnPropertyChanged(); }
        }

        public ICommand ConvertToSubtitlesCommand { get; }
        public ICommand ConvertToTextCommand { get; }
        public ICommand ClearAllCommand { get; }

        public RawJsonViewModel()
        {
            _translationService = new TranslationService();
            EnableTranslation = true;
            IncludeSpeakers = false;
            IncludeSounds = false;
            IsNoRus = true;
            IsNeedSpeakerVoice = true;
            Logger.setStatus("Готов к работе");

            ConvertToSubtitlesCommand = new RelayCommand(async _ => await ConvertToSubtitlesAsync(), _ => !IsProcessing && !string.IsNullOrWhiteSpace(RawJsonText));
            ConvertToTextCommand = new RelayCommand(async _ => await ConvertToTextAsync(), _ => !IsProcessing && !string.IsNullOrWhiteSpace(RawJsonText));
            ClearAllCommand = new RelayCommandSync(_ => ClearAll());

            Logger.LogInfo("Вкладка 'Работа с текстом' инициализирована");
        }

        private async Task ConvertToSubtitlesAsync()
        {
            var dateStart = DateTime.Now;

            try
            {
                IsProcessing = true;
                Logger.setStatus("Обработка JSON...", 0, true);
                Logger.LogProgress("Конвертация JSON в SRT...");
                var subtitles = await getNormSub(dateStart);
                if (subtitles != null && subtitles.Count > 0)
                {
                    Logger.setStatus($"Загружено {subtitles.Count} субтитров{Logger.getInfoDurationString(dateStart)} из JSON", 0, true);
                    Logger.LogInfo(StatusText);

                    if (IsNeedSpeakerVoice)
                        await SpeakerAudioExtractor.createVoiceFile(subtitles);

                    var srt = new StringBuilder();
                    var num = 0;
                    for (int i = 0; i < subtitles.Count; i++)
                    {
                        var sub = subtitles[i];
                        var textToShow = EnableTranslation && !string.IsNullOrEmpty(sub.TranslatedContent)
                            ? sub.TranslatedContent
                            : sub.Content;

                        if (!(IsNoRus && sub.DetectedLang == "Русский")
                            && !(!IncludeSounds && textToShow.StartsWith("[")))
                        {
                            num++;
                            srt.AppendLine(num.ToString());
                            srt.AppendLine($"{FormatTime(sub.Start)} --> {FormatTime(sub.End)}");
                            srt.AppendLine(textToShow);
                            srt.AppendLine();
                        }
                    }

                    ResultText = srt.ToString();
                    Logger.setStatus($"Готово! Создано {num} субтитров");
                    Logger.LogSuccess($"SRT создан{Logger.getInfoDurationString(dateStart)}: {num} субтитров");
                }
            }
            catch (JsonException ex)
            {
                Logger.LogError($"Ошибка формата JSON{Logger.getInfoDurationString(dateStart)}: {ex.Message}");
                MessageBox.Show($"Ошибка формата JSON{Logger.getInfoDurationString(dateStart)}: {ex.Message}", "Ошибка"
                    , MessageBoxButton.OK, MessageBoxImage.Error);
                Logger.setStatus("Ошибка JSON");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка{Logger.getInfoDurationString(dateStart)}: {ex.Message}");
                MessageBox.Show($"Ошибка{Logger.getInfoDurationString(dateStart)}: {ex.Message}"
                    , "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Logger.setStatus("Ошибка");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private async Task ConvertToTextAsync()
        {
            var dateStart = DateTime.Now;

            try
            {
                IsProcessing = true;
                Logger.setStatus("Обработка JSON...", 0, true);
                Logger.LogProgress("Конвертация JSON в текст...");
                var subtitles = await getNormSub(dateStart);
                if (subtitles != null && subtitles.Count > 0)
                {
                    Logger.setStatus($"Загружено {subtitles.Count} субтитров{Logger.getInfoDurationString(dateStart)} из JSON", 0, true);
                    Logger.LogInfo(StatusText);
                    if (IsNeedSpeakerVoice)
                        await SpeakerAudioExtractor.createVoiceFile(subtitles);

                    var text = new StringBuilder();
                    foreach (var sub in subtitles)
                    {
                        var textToShow = EnableTranslation && !string.IsNullOrEmpty(sub.TranslatedContent)
                            ? sub.TranslatedContent
                            : sub.Content;

                        if (!(IsNoRus && sub.DetectedLang == "Русский")
                            && !(!IncludeSounds && textToShow.StartsWith("[")))
                        {
                            if (IncludeSpeakers)
                            {
                                var numSpeaker = sub.Speaker + 1;
                                if (NoMoreThanTwo)
                                    numSpeaker = trimTheNumber(numSpeaker, 2);
                                else if (NoMoreThanTen)
                                    numSpeaker = trimTheNumber(numSpeaker, 10);

                                text.AppendLine($"Speaker {numSpeaker}: {textToShow}");
                            }
                            else
                                text.AppendLine(textToShow);
                        }
                    }

                    ResultText = text.ToString();
                    Logger.setStatus($"Готово! Создан текст{Logger.getInfoDurationString(dateStart)} из {subtitles.Count} строк");
                    Logger.LogSuccess(StatusText);
                }
                else
                {
                    Logger.setStatus($"Неудалось распарсить json текст");
                    Logger.LogSuccess(StatusText);
                }
            }
            catch (JsonException ex)
            {
                Logger.LogError($"Ошибка формата JSON{Logger.getInfoDurationString(dateStart)}: {ex.Message}");
                MessageBox.Show($"Ошибка формата JSON{Logger.getInfoDurationString(dateStart)}: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Logger.setStatus("Ошибка JSON");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка{Logger.getInfoDurationString(dateStart)}: {ex.Message}");
                MessageBox.Show($"Ошибка{Logger.getInfoDurationString(dateStart)}: {ex.Message}"
                    , "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Logger.setStatus("Ошибка");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task<List<SubtitleItem>> getNormSub(DateTime in_dateStart)
        {
            var ret = getSubs();
            if (ret == null || ret.Count == 0)
            {
                Logger.LogWarning("JSON пуст или не удалось распарсить");
                MessageBox.Show("Не удалось распарсить JSON или список пуст", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                Logger.setStatus("Ошибка парсинга");
                //return false;
            }
            else
            {
                if (EnableTranslation || IsNoRus)
                {
                    Logger.setStatus("Проверка сервера...", 0, true);
                    Logger.LogProgress("Проверка сервера перевода...");

                    if (!await _translationService.CheckHealthAsync())
                    {
                        Logger.LogError($"Сервер перевода недоступен{Logger.getInfoDurationString(in_dateStart)}. Запустите: python server.py");
                        //MessageBox.Show("Сервер перевода недоступен.\nЗапустите: python server.py"
                        //    , "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                        Logger.setStatus("Сервер недоступен");
                    }

                    Logger.setStatus("Перевод субтитров...", 0, true);
                    Logger.LogProgress("Начало перевода субтитров...");

                    var texts = new List<string>();
                    foreach (var sub in ret)
                        if (!(!string.IsNullOrWhiteSpace(sub.TranslatedContent) && !string.IsNullOrWhiteSpace(sub.DetectedLang)))
                            texts.Add(sub.Content);

                    var results = await _translationService.TranslateBatchAsync(texts);
                    for (int i = 0; i < ret.Count; i++)
                    {
                        if (results.Any())
                        {
                            ret[i].TranslatedContent = results[i].Translated;
                            ret[i].DetectedLang = results[i].Lang;
                        }

                        ret[i].Index = i + 1;
                        ret[i].StartTime = new TimeSpan(0, 0, Convert.ToInt32(Math.Round(ret[i].Start)));
                        ret[i].EndTime = new TimeSpan(0, 0, Convert.ToInt32(Math.Round(ret[i].End)));
                    }

                    RawJsonText = trySerializeSubJson(ret);

                    Logger.setStatus("Перевод завершён");
                    Logger.LogSuccess($"Перевод завершён{Logger.getInfoDurationString(in_dateStart)}: {ret.Count} субтитров");
                }
            }

            return ret;
        }

        public static List<SubtitleItem> tryDeserializeJsonSub(string in_jsonText)
        {
            var ret = new List<SubtitleItem>();
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                ret = JsonSerializer.Deserialize<List<SubtitleItem>>(in_jsonText, options);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Неудалось распарсить Json для субтитров: {ex.Message}");
            }

            return ret;
        }

        public static string trySerializeSubJson(List<SubtitleItem> in_sub)
        {
            var ret = "";
            if (in_sub?.Any() == true)
            {
                try
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        // Отключаем экранирование кириллицы и других не-ASCII символов
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        WriteIndented = true // Опционально: делает JSON красивым (с отступами)
                    };

                    // Сериализуем
                    ret = JsonSerializer.Serialize(in_sub, options);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Ошибка при сериализации субтитров: {ex.Message}");
                }
            }

            return ret;
        }

        /// <summary> Обрезать число до максимального </summary>
        private int trimTheNumber(int in_number, int in_maxNumber)
        {
            var ret = in_number;
            while (ret > in_maxNumber)
                ret -= in_maxNumber;

            return ret;
        }

        private List<SubtitleItem>? getSubs()
        {
            var jsonText = RawJsonText;
            var ret = Logger.getSubs(jsonText);
            return ret;
        }

        private void ClearAll()
        {
            RawJsonText = string.Empty;
            ResultText = string.Empty;
            Logger.setStatus("Очищено");
            Logger.LogInfo("Все поля очищены");
        }

        private string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            int hours = (int)ts.TotalHours;
            return $"{hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}