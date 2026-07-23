using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SubtitleTranslator.Models;
using SubtitleTranslator.Services;

namespace SubtitleTranslator.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly TranslationService _translationService;
        private ObservableCollection<SubtitleItem> _subtitles;
        private bool _isProcessing;
        private int _progress;
        private string _statusText;
        private string _filePath;

        public ObservableCollection<SubtitleItem> Subtitles
        {
            get => _subtitles;
            set { _subtitles = value; OnPropertyChanged(); }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set { _isProcessing = value; OnPropertyChanged(); }
        }

        public int Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public string FilePath
        {
            get => _filePath;
            set { _filePath = value; OnPropertyChanged(); }
        }

        public ICommand LoadFileCommand { get; }
        public ICommand LoadFromTextCommand { get; }
        public ICommand TranslateCommand { get; }
        public ICommand SaveSrtCommand { get; }

        public MainViewModel()
        {
            var dateStart = DateTime.Now;
            _translationService = new TranslationService();
            Subtitles = new ObservableCollection<SubtitleItem>();
            StatusText = "Готов к работе";

            LoadFileCommand = new RelayCommand(async _ => await LoadFileAsync());
            LoadFromTextCommand = new RelayCommand(async param => await LoadFromTextAsync(param as string), _ => !IsProcessing);
            TranslateCommand = new RelayCommand(async _ => await TranslateAsync(), _ => !IsProcessing && Subtitles.Count > 0);
            SaveSrtCommand = new RelayCommand(async _ => await SaveSrtAsync(), _ => !IsProcessing && Subtitles.Count > 0);

            Logger.LogInfo($"Вкладка 'Работа с файлами' инициализирована{Logger.getInfoDurationString(dateStart)}");
        }

        private async Task LoadFileAsync()
        {
            Logger.LogInfo("Открытие диалога выбора файла...");
            var dialog = new OpenFileDialog
            {
                Filter = "JSON файлы|*.json",
                Title = "Выберите JSON файл с субтитрами"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var dateStart = DateTime.Now;
                    FilePath = dialog.FileName;
                    StatusText = "Загрузка файла...";
                    Logger.LogProgress($"Загрузка файла: {Path.GetFileName(FilePath)}");

                    var json = await File.ReadAllTextAsync(FilePath);
                    await ParseAndLoadJson(json);

                    Logger.LogSuccess($"Файл загружен: {Path.GetFileName(FilePath)}{Logger.getInfoDurationString(dateStart)}");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Ошибка загрузки файла: {ex.Message}");
                    MessageBox.Show($"Ошибка загрузки файла: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText = "Ошибка загрузки";
                }
            }
            else
            {
                Logger.LogInfo("Выбор файла отменён");
            }
        }

        public async Task LoadFromTextAsync(string jsonText)
        {
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                Logger.LogWarning("Попытка загрузить пустой текст");
                MessageBox.Show("Текст JSON пуст", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                FilePath = null;
                StatusText = "Загрузка из текста...";
                Logger.LogProgress("Загрузка JSON из текстового поля");
                await ParseAndLoadJson(jsonText);
                Logger.LogSuccess("JSON успешно загружен из текста");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка загрузки из текста: {ex.Message}");
                MessageBox.Show($"Ошибка загрузки из текста: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText = "Ошибка загрузки";
            }
        }

        private async Task ParseAndLoadJson(string in_json)
        {
            Logger.LogProgress("Парсинг JSON...");
            var items = Logger.getSubs(in_json);
            if (items == null || items.Count == 0)
            {
                Logger.LogWarning("JSON пуст или не удалось распарсить");
                MessageBox.Show("Не удалось распарсить JSON или список пуст", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText = "Ошибка парсинга";
                return;
            }

            Subtitles.Clear();
            foreach (var item in items)
            {
                Subtitles.Add(item);
            }

            StatusText = $"Загружено {Subtitles.Count} субтитров";
            Logger.LogSuccess($"Загружено {Subtitles.Count} субтитров");
            await Task.CompletedTask;
        }

        private async Task TranslateAsync()
        {
            Logger.LogInfo("Начало перевода субтитров");

            if (!await CheckServerAsync())
            {
                Logger.LogError("Сервер перевода недоступен");
                return;
            }

            var dateStart = DateTime.Now;
            IsProcessing = true;
            Progress = 0;
            StatusText = "Перевод субтитров...";

            try
            {
                var texts = new List<string>();
                foreach (var sub in Subtitles)
                    texts.Add(sub.Content);

                Logger.LogProgress($"Отправка {texts.Count} текстов на перевод...");
                var results = await _translationService.TranslateBatchAsync(texts);

                for (int i = 0; i < Subtitles.Count; i++)
                {
                    Subtitles[i].TranslatedContent = results[i].Translated;
                    Subtitles[i].DetectedLang = results[i].Lang;
                    Progress = (int)((i + 1) * 100.0 / Subtitles.Count);
                    StatusText = $"Переведено {i + 1}/{Subtitles.Count}";

                    if ((i + 1) % 10 == 0 || i == Subtitles.Count - 1)
                        Logger.LogProgress($"Переведено {i + 1}/{Subtitles.Count}");
                }

                StatusText = "Перевод завершен!";
                Logger.LogSuccess($"Перевод завершён: {Subtitles.Count} субтитров{Logger.getInfoDurationString(dateStart)}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка перевода: {ex.Message}{Logger.getInfoDurationString(dateStart)}");
                MessageBox.Show($"Ошибка перевода: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText = "Ошибка перевода";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private async Task SaveSrtAsync()
        {
            Logger.LogInfo("Сохранение SRT файла...");
            var dialog = new SaveFileDialog
            {
                Filter = "SRT файлы|*.srt",
                Title = "Сохранить SRT файл",
                FileName = !string.IsNullOrEmpty(FilePath)
                    ? Path.GetFileNameWithoutExtension(FilePath) + "_ru.srt"
                    : "subtitles_ru.srt"
            };

            if (dialog.ShowDialog() == true)
            {
                var dateStart = DateTime.Now;

                try
                {
                    StatusText = "Сохранение SRT...";
                    var srt = new StringBuilder();

                    for (int i = 0; i < Subtitles.Count; i++)
                    {
                        var sub = Subtitles[i];
                        srt.AppendLine((i + 1).ToString());
                        srt.AppendLine($"{FormatTime(sub.Start)} --> {FormatTime(sub.End)}");
                        srt.AppendLine(sub.TranslatedContent ?? sub.Content);
                        srt.AppendLine();
                    }

                    await File.WriteAllTextAsync(dialog.FileName, srt.ToString(), Encoding.UTF8);
                    StatusText = $"Сохранено: {Path.GetFileName(dialog.FileName)}";
                    Logger.LogSuccess($"SRT файл сохранён: {dialog.FileName}{Logger.getInfoDurationString(dateStart)}");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Ошибка сохранения: {ex.Message}{Logger.getInfoDurationString(dateStart)}");
                    MessageBox.Show($"Ошибка сохранения: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText = "Ошибка сохранения";
                }
            }
            else
            {
                Logger.LogInfo("Сохранение отменено");
            }
        }

        private async Task<bool> CheckServerAsync()
        {
            var dateStart = DateTime.Now;
            StatusText = "Проверка сервера...";
            Logger.LogProgress("Проверка доступности сервера перевода...");

            if (await _translationService.CheckHealthAsync())
            {
                Logger.LogSuccess($"Сервер перевода доступен{Logger.getInfoDurationString(dateStart)}");
                return true;
            }
            else
            {
                Logger.LogError($"Сервер перевода недоступен{Logger.getInfoDurationString(dateStart)}");
                MessageBox.Show("Сервер перевода недоступен.\nЗапустите: python server.py", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText = "Сервер недоступен";
                return false;
            }
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

    // Асинхронный RelayCommand
    public class RelayCommand : ICommand
    {
        private readonly Func<object, Task> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Func<object, Task> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;

        public async void Execute(object parameter) => await _execute(parameter);

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }

    // Синхронный RelayCommand
    public class RelayCommandSync : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommandSync(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object parameter) => _execute(parameter);

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}