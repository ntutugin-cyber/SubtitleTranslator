using Microsoft.Win32;
using SubtitleTranslator.Models;
using SubtitleTranslator.Services;
using SubtitleTranslator.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Serialization;
using NAudio.CoreAudioApi;

namespace SubtitleTranslator
{
    public partial class MainWindow : Window
    {
        private static readonly HttpClient _vocalHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromHours(2)
        };

        private const string VocalRemoverEndpoint = "http://localhost:8000/remove-vocal";

        private readonly HiggsApiService _api = new();
        private CancellationTokenSource? _cts;
        private const int MaxTtsChunkLength = 350;
        private readonly Stopwatch _appStopwatch = Stopwatch.StartNew();
        private ObservableCollection<VoiceItem> m_voiceItems = new ObservableCollection<VoiceItem>();
        private readonly ObservableCollection<DubQueueItem> m_dubQueue = new();
        private bool m_isDubQueueRunning;
        private TimeSpan m_totalDubTime = TimeSpan.Zero;
        private int m_totalDubbedVideos;

        /// <summary> Создаем свойство, которое будет хранить нашу ViewModel </summary>
        public RawJsonViewModel m_rawJsonVM { get; set; }
        public MainWindow()
        {
            InitializeComponent();
            // 1. Инициализируем ViewModel
            m_rawJsonVM = new RawJsonViewModel();

            // 2. Устанавливаем DataContext окна на сам MainWindow
            DataContext = this;

            // Подписываемся на изменение свойства Name у каждой добавленной строки
            m_voiceItems.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                    foreach (VoiceItem item in e.NewItems)
                        item.PropertyChanged += onPropertyChangedVoiceItem;
                if (e.OldItems != null)
                    foreach (VoiceItem item in e.OldItems)
                        item.PropertyChanged -= onPropertyChangedVoiceItem;
            };

            // Подписываемся на событие логгера
            Logger.LogAdded += OnLogAdded;
            Logger.StatusChange += setStatus;
            Logger.LogInfo("Приложение запущено");
            tbApiUrl.Text = "http://127.0.0.1:7077";
            tbApiKey.Text = "a29a12ef250e9af9d1ed7e29c0da35fa777ba25535a27715";
            PbProgress.Visibility = Visibility.Collapsed;

            // Привязка ItemsSource напрямую (на случай, если DataContext не подхватит)
            //dgVoices.ItemsSource = m_voiceItems;
            var cm = new MenuItem() { Header = "Установить голос на псевдоним" };
            cm.Click += (s, e) => onClickSetVoice(s, e);
            dgVoices.ContextMenu = new ContextMenu();
            dgVoices.ContextMenu.Items.Add(cm);

            dgDubQueue.ItemsSource = m_dubQueue;
        }

        private void onPropertyChangedVoiceItem(object in_sender, System.ComponentModel.PropertyChangedEventArgs in_)
        {
            if (in_.PropertyName != nameof(VoiceItem.Name)) return;

            var changedItem = in_sender as VoiceItem;
            string newName = changedItem.Name;
            string oldName = changedItem.PreviousName;

            if (string.IsNullOrEmpty(newName)) return;

            // Ищем дубликат
            var duplicate = m_voiceItems.FirstOrDefault(v => v != changedItem && v.Name == newName);
            if (duplicate != null)
            {
                var previousItem = m_voiceAllItems.FirstOrDefault(v => v != changedItem && v.Name == oldName);
                if (previousItem != null)
                {
                    // Меняем местами голоса
                    OnLogAdded($"Обмен: '{oldName}' ↔ '{newName}' между псевдонимами " +
                        $"#{changedItem.Psevdonim} и #{duplicate.Psevdonim}");

                    // Отключаем обработчики на время замены, чтобы не уйти в рекурсию
                    changedItem.PropertyChanged -= onPropertyChangedVoiceItem;
                    duplicate.PropertyChanged -= onPropertyChangedVoiceItem;

                    duplicate.Name = oldName;      // в дубликат кладём то, что было у changedItem
                    duplicate.Value = previousItem.Value;      // в дубликат кладём то, что было у changedItem
                                                               // changedItem.Name уже равен newName — оставляем

                    changedItem.PropertyChanged += onPropertyChangedVoiceItem;
                    duplicate.PropertyChanged += onPropertyChangedVoiceItem;
                }
            }
            else
                OnLogAdded($"Строка с псевдонимом {changedItem.Psevdonim}: голос изменён на '{newName}'");
        }

        private void OnLogAdded(string in_message)
        {
            // Добавляем сообщение в лог
            LogTextBox.AppendText(in_message + "\n");
            // Прокручиваем вниз
            LogTextBox.ScrollToEnd();
        }

        protected override void OnClosed(System.EventArgs e)
        {
            // Отписываемся при закрытии
            Logger.LogAdded -= OnLogAdded;
            Logger.StatusChange -= setStatus;
            base.OnClosed(e);
        }

        private void BrowseVideo_Click(object in_sender, RoutedEventArgs in_e)
        {
            var dlg = new OpenFileDialog { Filter = "Video Files|*.mp4;*.mkv;*.avi" };
            if (dlg.ShowDialog() == true) TxtVideo.Text = dlg.FileName;
        }

        private void BrowseSrt_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Subtitles|*.srt" };
            if (dlg.ShowDialog() == true) TxtSrt.Text = dlg.FileName;
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            // OpenFolderDialog доступен в .NET 5+. Если ошибка, замените на System.Windows.Forms.FolderBrowserDialog
            var dlg = new OpenFolderDialog();
            if (dlg.ShowDialog() == true) TxtMp3Folder.Text = dlg.FolderName;
        }

        private List<SubtitleItem> parseSrt(string in_path)
        {
            var ret = new List<SubtitleItem>();
            var lines = File.ReadAllLines(in_path);
            var timeRegex = new Regex(@"(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})");
            for (int i = 0; i < lines.Length; i++)
            {
                var match = timeRegex.Match(lines[i]);
                if (match.Success)
                {
                    var start = new TimeSpan(0, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value));
                    var end = new TimeSpan(0, int.Parse(match.Groups[5].Value), int.Parse(match.Groups[6].Value), int.Parse(match.Groups[7].Value), int.Parse(match.Groups[8].Value));
                    ret.Add(new SubtitleItem { StartTime = start, EndTime = end });
                }
            }

            return ret;
        }

        private string findFfprobe(string in_ffmpegPath)
        {
            var dir = Path.GetDirectoryName(in_ffmpegPath);
            var ffprobe = Path.Combine(dir ?? ".", "ffprobe.exe");
            return File.Exists(ffprobe) ? ffprobe : "ffprobe.exe";
        }

        private async Task<double> getDurationAsync(string in_file, string in_ffprobe, CancellationToken in_ct)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = in_ffprobe,
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{in_file}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.Start();
            string outStr = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync(in_ct);
            return double.TryParse(outStr.Trim(), CultureInfo.InvariantCulture, out double dur) ? dur : 0;
        }

        private string buildFilterComplex(
            List<SubtitleItem> in_blocks,
            List<double> in_mp3Durations,
            bool in_hasInstrumental,
            double in_videoDuration = 0,
            bool in_allowHardTrim = false)
        {
            const double maxSpeed = 1.5;

            int voiceCount = Math.Min(in_blocks.Count, in_mp3Durations.Count);

            // Если есть инструментал, он занимает вход 1,
            // значит голосовые файлы начинаются со входа 2.
            // Если инструментала нет, голосовые начинаются со входа 1.
            int voiceInputStart = in_hasInstrumental ? 2 : 1;

            var voiceParts = new List<string>();
            var labels = new List<string>();
            var windows = new List<(double Start, double End)>();

            for (int i = 0; i < voiceCount; i++)
            {
                double start = Math.Max(0, in_blocks[i].StartTime.TotalSeconds);
                double srtEnd = Math.Max(start + 0.05, in_blocks[i].EndTime.TotalSeconds);

                double limitEnd = srtEnd;

                if (i + 1 < in_blocks.Count)
                {
                    double nextStart = in_blocks[i + 1].StartTime.TotalSeconds;

                    if (nextStart > start + 0.05)
                    {
                        // Жёсткая граница до начала следующей реплики.
                        // Это защищает от наложения, но может вызывать обрезку,
                        // если текущая озвучка слишком длинная.
                        limitEnd = nextStart;
                    }
                }
                else if (in_videoDuration > start + 0.05)
                {
                    // Для последнего блока можно разрешить звучать до конца видео,
                    // чтобы последняя длинная фраза не резалась о EndTime субтитра.
                    limitEnd = Math.Max(srtEnd, in_videoDuration);
                }

                double slot = Math.Max(0.05, limitEnd - start);
                double mp3Dur = Math.Max(0, in_mp3Durations[i]);

                double speed = 1.0;

                if (mp3Dur > slot)
                {
                    double needed = mp3Dur / slot;

                    speed = Math.Min(maxSpeed, Math.Ceiling(needed * 1000.0) / 1000.0);

                    if (speed < 1.0)
                        speed = 1.0;
                }

                double finalDur = mp3Dur > 0 ? mp3Dur / speed : 0;

                bool tooLong = finalDur > slot + 0.03;
                bool needTrim = tooLong && in_allowHardTrim;

                if (needTrim)
                    finalDur = slot;

                if (finalDur <= 0.01)
                    finalDur = Math.Min(slot, 0.1);

                if (tooLong)
                {
                    Logger.LogInfo(
                        $"⚠️ Блок {i + 1}: MP3 = {mp3Dur:F2}s, окно = {slot:F2}s, " +
                        $"скорость = {speed:F2}x, после ускорения = {finalDur:F2}s. " +
                        (needTrim
                            ? "Обрезка включена: хвост будет срезан."
                            : "Обрезка отключена: возможно наложение на следующую реплику."));
                }

                windows.Add((start, start + finalDur));

                int delayMs = (int)Math.Round(start * 1000.0, MidpointRounding.AwayFromZero);
                string label = $"v{i}";
                labels.Add(label);

                string trimFilter = string.Empty;

                if (needTrim)
                {
                    double fadeStart = Math.Max(0, slot - 0.08);

                    trimFilter =
                        $",atrim=end={slot.ToString("0.000", CultureInfo.InvariantCulture)}" +
                        $",asetpts=PTS-STARTPTS" +
                        $",afade=t=out:st={fadeStart.ToString("0.000", CultureInfo.InvariantCulture)}:d=0.08";
                }

                voiceParts.Add(
                    $"[{voiceInputStart + i}:a]" +
                    $"aformat=channel_layouts=stereo," +
                    $"atempo={speed.ToString("0.000", CultureInfo.InvariantCulture)}" +
                    trimFilter +
                    $",adelay={delayMs}|{delayMs}" +
                    $"[{label}]"
                );
            }

            string timeline = buildTimelineExpression(windows);

            var parts = new List<string>();

            if (in_hasInstrumental)
            {
                // Если есть озвучка, инструментал внутри субтитров делаем потише,
                // чтобы голос был разборчивее.
                // Если озвучки нет, оставляем почти полную громкость.
                double instrumentalVolume = voiceCount > 0 ? 0.85 : 1;

                // Оригинальная дорожка:
                // внутри субтитров молчит, вне субтитров звучит как есть.
                parts.Add($"[0:a]volume=0:enable='{timeline}'[a_orig_part]");

                // Инструментал:
                // вне субтитров молчит, внутри субтитров звучает.
                parts.Add(
                    $"[1:a]volume=0:enable='not({timeline}')," +
                    $"volume={instrumentalVolume.ToString("0.00", CultureInfo.InvariantCulture)}:enable='{timeline}'" +
                    $"[a_inst_part]"
                );

                // Смешиваем оригинал вне субтитров и инструментал внутри субтитров
                parts.Add("[a_orig_part][a_inst_part]amix=inputs=2:duration=longest:normalize=0[a_bg]");
            }
            else
            {
                // Если инструментала нет, ведём себя близко к старой логике:
                // оригинал тихо, если есть озвучка.
                if (voiceCount == 0)
                    parts.Add("[0:a]anull[a_bg]");
                else
                    parts.Add("[0:a]volume=0.03[a_bg]");
            }

            parts.AddRange(voiceParts);

            if (labels.Count > 1)
            {
                string inputs = string.Join("", labels.Select(l => $"[{l}]"));
                parts.Add($"{inputs}amix=inputs={labels.Count}:duration=longest:normalize=0[a_voice]");
                parts.Add("[a_bg][a_voice]amix=inputs=2:duration=longest:normalize=0[a_out]");
            }
            else if (labels.Count == 1)
                parts.Add($"[a_bg][{labels[0]}]amix=inputs=2:duration=longest:normalize=0[a_out]");
            else
                parts.Add("[a_bg]anull[a_out]");

            return string.Join(";", parts);
        }

        private string buildTimelineExpression(List<(double Start, double End)> in_windows)
        {
            if (in_windows == null || in_windows.Count == 0)
                return "0";

            var parts = new List<string>();

            foreach (var w in in_windows)
            {
                double start = Math.Max(0, w.Start);
                double end = Math.Max(start + 0.05, w.End);

                parts.Add(
                    $"between(t,{start.ToString("0.000", CultureInfo.InvariantCulture)}," +
                    $"{end.ToString("0.000", CultureInfo.InvariantCulture)})"
                );
            }

            return string.Join("+", parts);
        }
        
        private string[] getOrderedMp3Files(string in_folder)
        {
            var dir = new DirectoryInfo(in_folder);

            return dir.GetFiles("*.mp3")
                .OrderBy(f => getNumericPrefix(f.Name))
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.FullName)
                .ToArray();
        }

        private int getNumericPrefix(string in_name)
        {
            var match = Regex.Match(in_name, @"^\s*(\d+)");

            if (match.Success && int.TryParse(match.Groups[1].Value, out int n))
                return n;

            return int.MaxValue;
        }

        private List<string> buildFfmpegArgs(
            string in_video,
            string[] in_mp3s,
            string in_filter,
            string in_output,
            string in_instrumentalPath = null)
        {
            var args = new List<string> { "-y" };

            // Вход 0: оригинальное видео
            args.Add("-i");
            args.Add(in_video);

            // Вход 1: аудио без вокала, если есть
            if (!string.IsNullOrWhiteSpace(in_instrumentalPath))
            {
                args.Add("-i");
                args.Add(in_instrumentalPath);
            }

            // Далее идут голосовые MP3
            foreach (var mp3 in in_mp3s)
            {
                args.Add("-i");
                args.Add(mp3);
            }

            args.Add("-filter_complex");
            args.Add(in_filter);

            args.Add("-map");
            args.Add("0:v");

            args.Add("-map");
            args.Add("[a_out]");

            args.Add("-c:v");
            args.Add("copy");

            args.Add("-c:a");
            args.Add("aac");

            args.Add("-b:a");
            args.Add("192k");

            args.Add(in_output);
            return args;
        }

        private async Task runFfmpegAsync(
            string in_ffmpeg,
            List<string> in_args,
            double in_totalDuration,
            CancellationToken in_ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = in_ffmpeg,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in in_args)
                psi.ArgumentList.Add(arg);

            using var p = Process.Start(psi);
            if (p == null)
                throw new Exception("Не удалось запустить ffmpeg.exe");

            // Ограничиваем частоту обновления UI (макс 5 раз в секунду), чтобы интерфейс не лагал
            DateTime lastUpdate = DateTime.MinValue;

            p.ErrorDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;

                // Ищем текущее время обработки
                var timeMatch = Regex.Match(e.Data, @"time=(\d{2}:\d{2}:\d{2}\.\d{2})");
                if (!timeMatch.Success) return;

                // Throttle UI updates
                if ((DateTime.Now - lastUpdate).TotalMilliseconds < 200) return;
                lastUpdate = DateTime.Now;

                double currentTime = TimeSpan.Parse(
                    timeMatch.Groups[1].Value,
                    CultureInfo.InvariantCulture
                ).TotalSeconds;

                double progress = in_totalDuration > 0
                    ? Math.Min(100, currentTime / in_totalDuration * 100)
                    : 0;

                // Ищем скорость кодирования (speed=1.23x)
                double speed = 1.0;
                var speedMatch = Regex.Match(e.Data, @"speed=(\d+\.?\d*)x");

                if (speedMatch.Success &&
                    double.TryParse(
                        speedMatch.Groups[1].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double s1))
                    speed = s1;

                // Расчёт оставшегося времени и времени завершения
                double remainingSec = speed > 0.01 && in_totalDuration > currentTime
                    ? (in_totalDuration - currentTime) / speed
                    : 0;

                var eta = DateTime.Now.AddSeconds(Math.Max(0, remainingSec));

                string etaText = remainingSec > 0 && in_totalDuration > 0
                    ? $"{eta:HH:mm:ss}"
                    : "???";

                string statusText =
                    $"📊 Прогресс: {progress:F1}% | " +
                    $"⚡ Скорость: {speed:F2}x | " +
                    $"⏳ Осталось: {formatTimeSpan(TimeSpan.FromSeconds(remainingSec))} | " +
                    $"🕒 Готово: {etaText}";

                setStatus(statusText, progress);
            };

            p.BeginErrorReadLine();

            using (in_ct.Register(() =>
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(true);
                }
                catch
                {
                    // ignore
                }
            }))
            {
                await Task.Run(() => p.WaitForExit(), in_ct);
            }

            if (p.ExitCode != 0)
                throw new Exception($"ffmpeg завершился с кодом {p.ExitCode}. Проверьте лог выше.");
        }

        public void setStatus(string in_statusText, double in_progress = 0, bool in_isIndeterminateProgress = false)
        {
            // Безопасное обновление UI из фонового потока
            Dispatcher.BeginInvoke(() =>
            {
                PbProgress.Value = in_progress;
                PbProgress.Visibility = !in_isIndeterminateProgress && (in_progress == 0 || in_progress == 100)
                    ? Visibility.Collapsed : Visibility.Visible;
                TxtStatus.Text = in_statusText;
                PbProgress.IsIndeterminate = in_isIndeterminateProgress;
                
                btnInSub.IsEnabled = !in_isIndeterminateProgress;
                btnInText.IsEnabled = !in_isIndeterminateProgress;
            });
        }

        private string formatTimeSpan(TimeSpan in_ts)
        {
            in_ts = TimeSpan.FromSeconds(Math.Max(0, in_ts.TotalSeconds));
            if (in_ts.TotalHours >= 1) return $"{in_ts.Hours:D2}:{in_ts.Minutes:D2}:{in_ts.Seconds:D2}";
            return $"{in_ts.Minutes:D2}:{in_ts.Seconds:D2}";
        }

        public string[] getSplittedText(string in_text)
        {
            string[] ret = in_text.Split(".".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            if (ret?.Any() == true)
            {
                var newSpis = new List<string>();
                var newElem = "";
                foreach (var xr in ret)
                {
                    if (newElem.Length < 15)
                    {
                        if (string.IsNullOrWhiteSpace(newElem))
                            newElem = xr?.ToString().Trim();
                        else
                            newElem = $"{newElem}. {xr?.ToString().Trim()}";
                    }
                    else
                    {
                        newSpis.Add(newElem);
                        newElem = xr?.ToString().Trim();
                    }
                }

                newElem = newElem?.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(newElem))
                    newSpis.Add(newElem);

                ret = newSpis.ToArray();
            }

            return ret;
        }

        /// <summary>
        /// Возвращает список команд для MP4-файлов, у которых ещё нет соответствующего _ru.srt файла.
        /// </summary>
        /// <param name="in_filePath">Путь к папке с видеофайлами</param>
        /// <param name="in_includeSubdirectories">Искать ли файлы в подпапках (по умолчанию только в корневой)</param>
        /// <returns>Список строк в формате: python.exe transcribe_to_srt7.py "полный_путь"</returns>
        public static List<string> getCommandsForPendingMp4s(string in_filePath, bool in_includeSubdirectories = false)
        {
            if (string.IsNullOrWhiteSpace(in_filePath))
                throw new ArgumentException("Путь к папке не может быть пустым.", nameof(in_filePath));

            var folderPath = Path.GetDirectoryName(in_filePath);
            // Нормализуем путь (убираем лишние слэши, точки и т.д.)
            folderPath = Path.GetFullPath(folderPath);

            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException($"Папка не найдена: {folderPath}");

            var searchOption = in_includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var mp4Files = Directory.GetFiles(folderPath, "*.mp4", searchOption);

            var commands = new List<string>(mp4Files.Length);

            foreach (var mp4Path in mp4Files)
            {
                // Папка, где лежит текущий MP4-файл
                string fileDirectory = Path.GetDirectoryName(mp4Path) ?? folderPath;
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(mp4Path);
                string expectedSrtPath = Path.Combine(fileDirectory, $"{fileNameWithoutExt}_ru.srt");

                // Добавляем команду только если .srt файла нет
                if (!File.Exists(expectedSrtPath))
                {
                    // Формируем строку с двойными кавычками вокруг пути
                    commands.Add($@"python.exe transcribe_to_srt7.py ""{mp4Path}""");
                }
            }

            return commands;
        }

        private async void onClickProcess(object sender, RoutedEventArgs e)
        {
            if (!validateInputs())
                return;

            var videoPath = TxtVideo.Text.Trim();
            var srtPath = TxtSrt.Text.Trim();
            var mp3Folder = TxtMp3Folder.Text.Trim();
            var newFileName = $"{Path.GetFileNameWithoutExtension(srtPath)}_newFileWithAudioSpeak.mp4";
            var srtBlocks = parseSrt(srtPath);

            string instrumentalPath = ChkUseInstrumentalOnSubtitles.IsChecked == true
                ? TxtInstrumental.Text.Trim()
                : null;

            await speakVideo(
                mp3Folder,
                videoPath,
                newFileName,
                srtBlocks,
                instrumentalPath
            );
        }

        private const int MaxFfmpegInputsPerCommand = 100;

        private sealed class MixedAudioPart
        {
            public string Path { get; set; }
            public double BaseStartSeconds { get; set; }
        }

        private sealed class VoicePlan
        {
            public int GlobalIndex { get; set; }
            public double Start { get; set; }
            public double Slot { get; set; }
            public double Mp3Duration { get; set; }
            public double Speed { get; set; }
            public double FinalDur { get; set; }
            public bool NeedTrim { get; set; }
        }

        private async Task speakVideo(
            string in_mp3Folder,
            string in_videoPath,
            string in_newFileName,
            List<SubtitleItem> in_srtBlocks,
            string in_instrumentalPath = null)
        {
            var dateStart = DateTime.Now;

            var outputPath = Path.Combine(
                Path.GetDirectoryName(in_videoPath) ?? "",
                in_newFileName
            );

            var ffmpegPath = TxtFfmpeg.Text.Trim();

            _cts = new CancellationTokenSource();
            BtnStart.IsEnabled = false;

            setStatus($"Парсинг SRT и анализ файлов по видео: {in_videoPath}", 0, true);

            string tempDir = null;

            try
            {
                var mp3Files = getOrderedMp3Files(in_mp3Folder);

                // Если файлов озвучки нет — разрешаем сделать только фон.
                // Если файлы есть, их количество должно совпадать с блоками субтитров.
                if (mp3Files.Length > 0 && mp3Files.Length != in_srtBlocks.Count)
                {
                    throw new Exception(
                        $"Количество MP3 ({mp3Files.Length}) не совпадает с блоками SRT ({in_srtBlocks.Count}). " +
                        "Файлы должны идти в порядке следования субтитров."
                    );
                }

                bool hasInstrumental =
                    !string.IsNullOrWhiteSpace(in_instrumentalPath) &&
                    File.Exists(in_instrumentalPath);

                var ffprobePath = findFfprobe(ffmpegPath);

                var mp3Durations = new List<double>();
                foreach (var mp3 in mp3Files)
                    mp3Durations.Add(await getDurationAsync(mp3, ffprobePath, _cts.Token));

                double videoDuration = await getDurationAsync(in_videoPath, ffprobePath, _cts.Token);

                // Проверяем не только количество MP3, но и общие входы ffmpeg:
                // видео + инструментал (если есть) + MP3.
                // Это нужно, чтобы одна команда не превышала лимит входов.
                int serviceInputs = 1 + (hasInstrumental ? 1 : 0);

                if (mp3Files.Length + serviceInputs <= MaxFfmpegInputsPerCommand)
                {
                    // Старый обычный путь, если входов немного.
                    string filterComplex = buildFilterComplex(
                        in_srtBlocks,
                        mp3Durations,
                        hasInstrumental,
                        videoDuration,
                        in_allowHardTrim: false
                    );

                    var arguments = buildFfmpegArgs(
                        in_videoPath,
                        mp3Files,
                        filterComplex,
                        outputPath,
                        hasInstrumental ? in_instrumentalPath : null
                    );

                    setStatus($"Кодирование... (может занять время) по видео: {in_videoPath}", 0, true);
                    await runFfmpegAsync(
                        ffmpegPath,
                        arguments,
                        videoDuration,
                        _cts.Token
                    );
                }
                else
                {
                    // Если входов слишком много, обрабатываем пачками.
                    tempDir = Path.Combine(
                        Path.GetTempPath(),
                        "speak_video_chunks_" + Guid.NewGuid().ToString("N")
                    );

                    Directory.CreateDirectory(tempDir);

                    await speakVideoByChunksAsync(
                        ffmpegPath,
                        in_videoPath,
                        outputPath,
                        in_srtBlocks,
                        mp3Files,
                        mp3Durations,
                        in_instrumentalPath,
                        hasInstrumental,
                        videoDuration,
                        tempDir,
                        _cts.Token
                    );
                }

                clearCache(in_videoPath);

                setStatus($"✅ Готово! Файл сохранён{Logger.getInfoDurationString(dateStart)}:\n{outputPath}\n по видео: {in_videoPath}");
                Logger.LogSuccess($"Обработка успешно завершена{Logger.getInfoDurationString(dateStart)}");
            }
            catch (OperationCanceledException)
            {
                setStatus($"❌ Обработка отменена{Logger.getInfoDurationString(dateStart)}:\n    {outputPath}\n  по видео: {in_videoPath}");
                Logger.LogSuccess($"❌ Обработка отменена{Logger.getInfoDurationString(dateStart)}");
            }
            catch (Exception ex)
            {
                var mess = $"❌ Ошибка: {ex.Message}\n    {Logger.getInfoDurationString(dateStart)}:\n    {outputPath}\n  по видео: {in_videoPath}";
                setStatus(mess);
                Logger.LogSuccess(mess);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempDir))
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch (Exception ex)
                    {
                        // Временные файлы можно игнорировать, если не удаляются сразу.
                        Logger.LogError($"Ошибка при удалении файлов из временной {tempDir} папки {ex.Message}");
                    }
                }

                BtnStart.IsEnabled = true;
                PbProgress.IsIndeterminate = false;
            }
        }

        private async Task speakVideoByChunksAsync(
            string in_ffmpegPath,
            string in_videoPath,
            string in_outputPath,
            List<SubtitleItem> in_srtBlocks,
            string[] in_mp3Files,
            List<double> in_mp3Durations,
            string in_instrumentalPath,
            bool in_hasInstrumental,
            double in_videoDuration,
            string in_tempDir,
            CancellationToken in_ct)
        {
            var plans = buildVoicePlans(
                in_srtBlocks,
                in_mp3Durations,
                in_videoDuration,
                in_allowHardTrim: false
            );

            if (plans.Count == 0)
            {
                // На практике сюда не должны попадать, потому что без озвучки
                // отработает обычный путь, но оставим безопасный fallback.
                string filterComplex = buildFilterComplex(
                    in_srtBlocks,
                    in_mp3Durations,
                    in_hasInstrumental,
                    in_videoDuration,
                    in_allowHardTrim: false
                );

                var arguments = buildFfmpegArgs(
                    in_videoPath,
                    new string[0],
                    filterComplex,
                    in_outputPath,
                    in_hasInstrumental ? in_instrumentalPath : null
                );

                await runFfmpegAsync(
                    in_ffmpegPath,
                    arguments,
                    in_videoDuration,
                    in_ct
                );

                return;
            }

            var voiceParts = new List<MixedAudioPart>();

            int chunkSize = MaxFfmpegInputsPerCommand;
            int chunkIndex = 0;

            // Шаг 1: режем голосовые файлы на пачки максимум по 100 штук
            // и каждую пачку сводим во временную WAV-дорожку.
            for (int offset = 0; offset < plans.Count; offset += chunkSize)
            {
                in_ct.ThrowIfCancellationRequested();

                int count = Math.Min(chunkSize, plans.Count - offset);

                var chunkFiles = in_mp3Files
                    .Skip(offset)
                    .Take(count)
                    .ToArray();

                double baseStart = plans
                    .Skip(offset)
                    .Take(count)
                    .Min(p => p.Start);

                setStatus(
                    $"Кодирование голосовой пачки {chunkIndex + 1} ({count} из {plans.Count})... по видео: {in_videoPath}",
                    0,
                    true
                );

                string filter = buildVoiceChunkFilter(
                    plans,
                    offset,
                    count,
                    baseStart
                );

                string chunkPath = Path.Combine(
                    in_tempDir,
                    $"voice_chunk_{chunkIndex:0000}.wav"
                );

                var arguments = buildAudioOnlyMixArgs(
                    chunkFiles,
                    filter,
                    chunkPath
                );

                await runFfmpegAsync(
                    in_ffmpegPath,
                    arguments,
                    in_videoDuration,
                    in_ct
                );

                voiceParts.Add(new MixedAudioPart
                {
                    Path = chunkPath,
                    BaseStartSeconds = baseStart
                });

                chunkIndex++;
            }

            // Шаг 2: если пачек получилось слишком много для финальной команды,
            // укрупняем их, чтобы финальный ffmpeg тоже не получил больше 100 входов.
            int maxPreparedVoicesInFinal = Math.Max(
                1,
                MaxFfmpegInputsPerCommand - (1 + (in_hasInstrumental ? 1 : 0))
            );

            voiceParts = await reduceVoicePartsAsync(
                voiceParts,
                maxPreparedVoicesInFinal,
                in_tempDir,
                in_ffmpegPath,
                in_videoDuration,
                in_ct
            );

            // Шаг 3: финальная сборка видео + фон + подготовленные голосовые пачки.
            setStatus(
                $"Финальное сведение ({voiceParts.Count} подготовленных дорожек)... по видео: {in_videoPath}",
                0,
                true
            );

            string finalFilter = buildFinalFilterWithPreparedVoices(
                plans,
                voiceParts,
                in_hasInstrumental
            );

            var finalArguments = buildFfmpegArgs(
                in_videoPath,
                voiceParts.Select(p => p.Path).ToArray(),
                finalFilter,
                in_outputPath,
                in_hasInstrumental ? in_instrumentalPath : null
            );

            await runFfmpegAsync(
                in_ffmpegPath,
                finalArguments,
                in_videoDuration,
                in_ct
            );
        }

        private List<VoicePlan> buildVoicePlans(
            List<SubtitleItem> in_blocks,
            List<double> in_mp3Durations,
            double in_videoDuration,
            bool in_allowHardTrim)
        {
            const double maxSpeed = 1.5;

            int voiceCount = Math.Min(in_blocks.Count, in_mp3Durations.Count);
            var result = new List<VoicePlan>(voiceCount);

            for (int i = 0; i < voiceCount; i++)
            {
                double start = Math.Max(0, in_blocks[i].StartTime.TotalSeconds);
                double srtEnd = Math.Max(start + 0.05, in_blocks[i].EndTime.TotalSeconds);

                double limitEnd = srtEnd;

                if (i + 1 < in_blocks.Count)
                {
                    double nextStart = in_blocks[i + 1].StartTime.TotalSeconds;

                    if (nextStart > start + 0.05)
                    {
                        // Жёсткая граница до начала следующей реплики.
                        // Это защищает от наложения, но может вызывать обрезку,
                        // если текущая озвучка слишком длинная.
                        limitEnd = nextStart;
                    }
                }
                else if (in_videoDuration > start + 0.05)
                {
                    // Для последнего блока можно разрешить звучать до конца видео,
                    // чтобы последняя длинная фраза не резалась о EndTime субтитра.
                    limitEnd = Math.Max(srtEnd, in_videoDuration);
                }

                double slot = Math.Max(0.05, limitEnd - start);
                double mp3Dur = Math.Max(0, in_mp3Durations[i]);

                double speed = 1.0;

                if (mp3Dur > slot)
                {
                    double needed = mp3Dur / slot;
                    speed = Math.Min(maxSpeed, Math.Ceiling(needed * 1000.0) / 1000.0);

                    if (speed < 1.0)
                        speed = 1.0;
                }

                double finalDur = mp3Dur > 0 ? mp3Dur / speed : 0;

                bool tooLong = finalDur > slot + 0.03;
                bool needTrim = tooLong && in_allowHardTrim;

                if (needTrim)
                    finalDur = slot;

                if (finalDur <= 0.01)
                    finalDur = Math.Min(slot, 0.1);

                if (tooLong)
                {
                    Logger.LogInfo(
                        $"⚠️ Блок {i + 1}: MP3 = {mp3Dur:F2}s, окно = {slot:F2}s, " +
                        $"скорость = {speed:F2}x, после ускорения = {finalDur:F2}s. " +
                        (needTrim
                            ? "Обрезка включена: хвост будет срезан."
                            : "Обрезка отключена: возможно наложение на следующую реплику."));
                }

                result.Add(new VoicePlan
                {
                    GlobalIndex = i,
                    Start = start,
                    Slot = slot,
                    Mp3Duration = mp3Dur,
                    Speed = speed,
                    FinalDur = finalDur,
                    NeedTrim = needTrim
                });
            }

            return result;
        }

        private string buildVoiceChunkFilter(
            List<VoicePlan> in_plans,
            int in_offset,
            int in_count,
            double in_baseStart)
        {
            var voiceParts = new List<string>();
            var labels = new List<string>();

            for (int local = 0; local < in_count; local++)
            {
                int global = in_offset + local;
                var p = in_plans[global];

                // Чтобы не писать огромные файлы с тишиной от начала видео,
                // внутри пачки делаем задержку относительно базы этой пачки.
                // Финальный фильтр снова вернёт абсолютную задержку.
                double delaySec = Math.Max(0, p.Start - in_baseStart);
                int delayMs = (int)Math.Round(delaySec * 1000.0, MidpointRounding.AwayFromZero);

                string label = $"v{local}";
                labels.Add(label);

                string trimFilter = string.Empty;

                if (p.NeedTrim)
                {
                    double fadeStart = Math.Max(0, p.Slot - 0.08);

                    trimFilter =
                        $",atrim=end={p.Slot.ToString("0.000", CultureInfo.InvariantCulture)}" +
                        $",asetpts=PTS-STARTPTS" +
                        $",afade=t=out:st={fadeStart.ToString("0.000", CultureInfo.InvariantCulture)}:d=0.08";
                }

                voiceParts.Add(
                    $"[{local}:a]" +
                    $"aformat=channel_layouts=stereo," +
                    $"atempo={p.Speed.ToString("0.000", CultureInfo.InvariantCulture)}" +
                    trimFilter +
                    $",adelay={delayMs}|{delayMs}" +
                    $"[{label}]"
                );
            }

            if (labels.Count == 0)
                throw new InvalidOperationException("Пустой блок голосов для пакетной обработки.");

            if (labels.Count == 1)
                return voiceParts[0].Replace($"[{labels[0]}]", "[a_out]");

            string inputs = string.Join("", labels.Select(l => $"[{l}]"));

            voiceParts.Add(
                $"{inputs}amix=inputs={labels.Count}:duration=longest:normalize=0[a_out]"
            );

            return string.Join(";", voiceParts);
        }

        private async Task<List<MixedAudioPart>> reduceVoicePartsAsync(
            List<MixedAudioPart> in_parts,
            int in_maxParts,
            string in_tempDir,
            string in_ffmpegPath,
            double in_videoDuration,
            CancellationToken in_ct)
        {
            int level = 0;

            while (in_parts.Count > in_maxParts)
            {
                in_ct.ThrowIfCancellationRequested();

                setStatus(
                    $"Укрупнение промежуточных дорожек (уровень {level + 1}, дорожек: {in_parts.Count})...",
                    0,
                    true
                );

                var next = new List<MixedAudioPart>();

                if (in_parts.Count <= MaxFfmpegInputsPerCommand)
                {
                    var mixed = await mixAudioPartsAsync(
                        in_parts,
                        in_tempDir,
                        in_ffmpegPath,
                        $"mix_level_{level:0000}",
                        in_videoDuration,
                        in_ct
                    );

                    next.Add(mixed);
                }
                else
                {
                    int step = Math.Max(2, Math.Min(MaxFfmpegInputsPerCommand, in_parts.Count));
                    int batchIndex = 0;

                    for (int i = 0; i < in_parts.Count; i += step)
                    {
                        var batch = in_parts
                            .Skip(i)
                            .Take(step)
                            .ToList();

                        if (batch.Count == 1)
                        {
                            next.Add(batch[0]);
                        }
                        else
                        {
                            var mixed = await mixAudioPartsAsync(
                                batch,
                                in_tempDir,
                                in_ffmpegPath,
                                $"mix_level_{level:0000}_batch_{batchIndex:0000}",
                                in_videoDuration,
                                in_ct
                            );

                            next.Add(mixed);
                        }

                        batchIndex++;
                    }
                }

                if (next.Count == in_parts.Count)
                    throw new Exception("Не удалось уменьшить количество промежуточных аудиофайлов.");

                in_parts = next;
                level++;
            }

            return in_parts;
        }

        private async Task<MixedAudioPart> mixAudioPartsAsync(
            List<MixedAudioPart> in_parts,
            string in_tempDir,
            string in_ffmpegPath,
            string in_name,
            double in_videoDuration,
            CancellationToken in_ct)
        {
            if (in_parts.Count == 0)
                throw new InvalidOperationException("Нет аудиофайлов для промежуточного сведения.");

            double groupBase = in_parts.Min(p => p.BaseStartSeconds);

            string filter = buildMixPartsFilter(in_parts, groupBase);

            string outPath = Path.Combine(
                in_tempDir,
                $"{in_name}.wav"
            );

            var inputFiles = in_parts.Select(p => p.Path).ToArray();

            var arguments = buildAudioOnlyMixArgs(
                inputFiles,
                filter,
                outPath
            );

            await runFfmpegAsync(
                in_ffmpegPath,
                arguments,
                in_videoDuration,
                in_ct
            );

            return new MixedAudioPart
            {
                Path = outPath,
                BaseStartSeconds = groupBase
            };
        }

        private string buildMixPartsFilter(
            List<MixedAudioPart> in_parts,
            double in_groupBase)
        {
            var voiceParts = new List<string>();
            var labels = new List<string>();

            for (int i = 0; i < in_parts.Count; i++)
            {
                double delaySec = Math.Max(0, in_parts[i].BaseStartSeconds - in_groupBase);
                int delayMs = (int)Math.Round(delaySec * 1000.0, MidpointRounding.AwayFromZero);

                string label = $"m{i}";
                labels.Add(label);

                voiceParts.Add(
                    $"[{i}:a]" +
                    $"aformat=channel_layouts=stereo," +
                    $"adelay={delayMs}|{delayMs}" +
                    $"[{label}]"
                );
            }

            if (labels.Count == 0)
                throw new InvalidOperationException("Пустой список промежуточных аудиофайлов.");

            if (labels.Count == 1)
                return voiceParts[0].Replace($"[{labels[0]}]", "[a_out]");

            string inputs = string.Join("", labels.Select(l => $"[{l}]"));

            voiceParts.Add(
                $"{inputs}amix=inputs={labels.Count}:duration=longest:normalize=0[a_out]"
            );

            return string.Join(";", voiceParts);
        }

        private string buildFinalFilterWithPreparedVoices(
            List<VoicePlan> in_plans,
            List<MixedAudioPart> in_voiceParts,
            bool in_hasInstrumental)
        {
            var windows = new List<(double Start, double End)>();

            foreach (var p in in_plans)
                windows.Add((p.Start, p.Start + p.FinalDur));

            string timeline = buildTimelineExpression(windows);

            int voiceInputStart = in_hasInstrumental ? 2 : 1;
            int voiceCount = in_voiceParts.Count;

            var parts = new List<string>();

            if (in_hasInstrumental)
            {
                // Если есть озвучка, инструментал внутри субтитров делаем потише,
                // чтобы голос был разборчивее.
                // Если озвучки нет, оставляем почти полную громкость.
                double instrumentalVolume = voiceCount > 0 ? 0.85 : 1;

                // Оригинальная дорожка:
                // внутри субтитров молчит, вне субтитров звучит как есть.
                parts.Add($"[0:a]volume=0:enable='{timeline}'[a_orig_part]");

                // Инструментал:
                // вне субтитров молчит, внутри субтитров звучит.
                parts.Add(
                    $"[1:a]volume=0:enable='not({timeline}')," +
                    $"volume={instrumentalVolume.ToString("0.00", CultureInfo.InvariantCulture)}:enable='{timeline}'" +
                    $"[a_inst_part]"
                );

                // Смешиваем оригинал вне субтитров и инструментал внутри субтитров.
                parts.Add("[a_orig_part][a_inst_part]amix=inputs=2:duration=longest:normalize=0[a_bg]");
            }
            else
            {
                // Если инструментала нет, ведём себя близко к старой логике:
                // оригинал тихо, если есть озвучка.
                if (voiceCount == 0)
                    parts.Add("[0:a]anull[a_bg]");
                else
                    parts.Add("[0:a]volume=0.03[a_bg]");
            }

            var labels = new List<string>();

            for (int i = 0; i < in_voiceParts.Count; i++)
            {
                string label = $"v{i}";
                labels.Add(label);

                int delayMs = (int)Math.Round(
                    Math.Max(0, in_voiceParts[i].BaseStartSeconds) * 1000.0,
                    MidpointRounding.AwayFromZero
                );

                parts.Add(
                    $"[{voiceInputStart + i}:a]" +
                    $"aformat=channel_layouts=stereo," +
                    $"adelay={delayMs}|{delayMs}" +
                    $"[{label}]"
                );
            }

            if (labels.Count > 1)
            {
                string inputs = string.Join("", labels.Select(l => $"[{l}]"));

                parts.Add(
                    $"{inputs}amix=inputs={labels.Count}:duration=longest:normalize=0[a_voice]"
                );

                parts.Add("[a_bg][a_voice]amix=inputs=2:duration=longest:normalize=0[a_out]");
            }
            else if (labels.Count == 1)
            {
                parts.Add($"[a_bg][{labels[0]}]amix=inputs=2:duration=longest:normalize=0[a_out]");
            }
            else
            {
                parts.Add("[a_bg]anull[a_out]");
            }

            return string.Join(";", parts);
        }

        private List<string> buildAudioOnlyMixArgs(
            string[] in_inputFiles,
            string in_filter,
            string in_output)
        {
            var args = new List<string> { "-y" };

            foreach (var file in in_inputFiles)
            {
                args.Add("-i");
                args.Add(file);
            }

            args.Add("-filter_complex");
            args.Add(in_filter);

            args.Add("-map");
            args.Add("[a_out]");

            args.Add("-vn");

            // Промежуточный несжатый аудиофайл.
            // Если захотите максимальный запас по качеству/громкости,
            // можно заменить pcm_s16le на pcm_f32le.
            args.Add("-c:a");
            args.Add("pcm_s16le");

            args.Add(in_output);

            return args;
        }

        private void clearCache(string in_videoPath)
        {
            try
            {
                setStatus($"Очистка кэша по видео: {in_videoPath}", 0, true);
                var cacheCirections = new List<string>() { "vocal_removed", "tempFiles", "subtitlesCache" };
                foreach (var xDirectionName in cacheCirections)
                {
                    var tempDir = Path.Combine(
                        Path.GetDirectoryName(in_videoPath) ?? Environment.CurrentDirectory,
                        xDirectionName
                    );

                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                var errMess = $"Ошибка при удалении папок кэша: {ex.Message}\n по видео: {in_videoPath}";
                setStatus(errMess, 0, true);
                Logger.LogError(errMess);
            }
        }

        private bool validateInputs()
        {
            if (!File.Exists(TxtVideo.Text)) return ShowErr("Укажите корректный путь к видео.");
            if (!File.Exists(TxtSrt.Text)) return ShowErr("Укажите корректный путь к SRT.");
            if (!Directory.Exists(TxtMp3Folder.Text)) return ShowErr("Укажите корректную папку с MP3.");

            string ffmpegPath = TxtFfmpeg.Text.Trim();
            if (!File.Exists(ffmpegPath))
            {
                // Пробуем найти ffmpeg в системном PATH
                try
                {
                    var psi = new ProcessStartInfo("ffmpeg", "-version") { UseShellExecute = false, CreateNoWindow = true };
                    using var p = Process.Start(psi);
                    p.WaitForExit();
                }
                catch { return ShowErr("ffmpeg.exe не найден. Укажите полный путь к файлу или добавьте его в PATH."); }
            }

            if (!string.IsNullOrWhiteSpace(TxtInstrumental.Text) && !File.Exists(TxtInstrumental.Text.Trim()))
            {
                return ShowErr("Указанный файл аудио без вокала не найден.");
            }

            return true;
        }

        private bool ShowErr(string in_msg)
        {
            Logger.LogError(in_msg);
            MessageBox.Show(in_msg, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); return false;
        }

        private async void onClickCheckServer(object in_sender, RoutedEventArgs in_e)
        {
            var sw = Stopwatch.StartNew();
            Logger.LogInfo("🔍 Начинаю проверку сервера...");

            try
            {
                _api.Configure(tbApiUrl.Text.Trim().TrimEnd('/'), tbApiKey.Text.Trim());

                var health = await _api.CheckHealthAsync();
                Logger.LogInfo($"Health check: {(health ? "✅ OK" : "❌ Недоступен")}");

                var status = await _api.GetStatusAsync();
                Logger.LogInfo($"Status: {status}");

                var models = await _api.GetModelsAsync();
                Logger.LogInfo($"Модели: {(models.Count > 0 ? string.Join(", ", models.Values) : "❌ не обнаружены")}");

                var speakers = await _api.GetSpeakersAsync();
                Logger.LogInfo($"Сохранённые голоса: {speakers.Keys.Count}");

                sw.Stop();
                Logger.LogSuccess($"Проверка завершена за {sw.ElapsedMilliseconds} мс.");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.LogError($"Ошибка проверки: {ex.Message} ({sw.ElapsedMilliseconds} мс)");
                MessageBox.Show("Ошибка проверки: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private ObservableCollection<VoiceItem> m_voiceAllItems = new ObservableCollection<VoiceItem>();
        private async void onClickRefreshVoices(object in_sender, RoutedEventArgs in_e)
        {
            refreshSpisVoice();
        }

        private async Task refreshSpisVoice()
        {
            var sw = Stopwatch.StartNew();
            var mess = "🔄 Загрузка списка голосов...";
            setStatus(mess, 0, true);
            Logger.LogInfo(mess);

            try
            {
                _api.Configure(tbApiUrl.Text.Trim().TrimEnd('/'), tbApiKey.Text.Trim());
                var speakers = await _api.GetSpeakersAsync();
                m_voiceAllItems.Clear();
                m_voiceAllItems.Add(new() { Name = "🎤 Default (стандартный)", Value = "default" });
                foreach (var xid in speakers.Keys)
                {
                    var displayName = speakers[xid];
                    m_voiceAllItems.Add(new() { Name = displayName, Value = $"speaker:{xid}" });
                }

                cmbVoice.ItemsSource = m_voiceAllItems;
                cmbVoice.SelectedIndex = 0;

                sw.Stop();
                Logger.LogSuccess($"Загружено голосов: {speakers.Count} за {sw.Elapsed}.");
                setStatus($"✅ Загружено голосов: {speakers.Count}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.LogError($"Ошибка загрузки голосов: {ex.Message} ({sw.Elapsed})");
                setStatus("❌ Ошибка загрузки голосов");
                MessageBox.Show("Ошибка загрузки голосов: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private List<string> m_exceptWords = new List<string>() { "дата", "время" };
        private string normalizeText(string in_text)
        {
            var ret = in_text;
            ret = Regex.Replace(ret, @"[^\w\d]+", " ", RegexOptions.IgnoreCase);
            ret = Regex.Replace(ret, @"[^\w\d]+", " ", RegexOptions.IgnoreCase);
            foreach (var xWord in m_exceptWords)
                ret = Regex.Replace(ret, xWord, " ", RegexOptions.IgnoreCase);

            ret = Regex.Replace(ret, @" +", " ", RegexOptions.IgnoreCase);
            ret = ret.Trim();

            return ret;
        }

        private TelegramParser m_telegramParser;
        private async void onClickParseTelegram(object in_sender, RoutedEventArgs in_e)
        {
            var text = RawJsonTextBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var sw = Stopwatch.StartNew();

                try
                {
                    text = Regex.Replace(text, @"\(http\S+", "", RegexOptions.IgnoreCase);
                    text = Regex.Replace(text, @"http\S+", "", RegexOptions.IgnoreCase);
                    m_telegramParser = new TelegramParser(text);
                    tbResultText.Text = m_telegramParser.getMyFormatText();
                    var psevdonims = m_telegramParser.getPsevdonims();
                    await fillVoiceItemsRandomly(psevdonims);
                    sw.Stop();
                    Logger.LogSuccess($"Распарсен текст из телеграмма за {sw.Elapsed}.");
                    setStatus($"✅ Распарсен текст из телеграмма");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Logger.LogError($"Ошибка парсинга текста из тг: {ex.Message} ({sw.Elapsed})");
                    setStatus("❌ Ошибка парсинга текста из тг");
                    MessageBox.Show("Ошибка парсинга текста из тг: " + ex.Message, "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task fillVoiceItemsRandomly(Dictionary<string, Tuple<int, int>> in_psevdonims)
        {
            await refreshSpisVoice();
            if (m_voiceAllItems.Count == 0)
            {
                Logger.LogInfo("Ошибка: список доступных голосов пуст.");
                return;
            }

            if (m_voiceItems.Any())
                m_voiceItems.Clear();

            var random = new Random();

            // Копируем список голосов, чтобы случайно выбирать без повторений
            var availableVoices = m_voiceAllItems.ToList();
            var obnul = 0;
            foreach (var psevdonim in in_psevdonims.Keys)
            {
                if (availableVoices.Count == 0)
                {
                    //Logger.LogInfo($"Предупреждение: голосов не хватает для всех псевдонимов. " +
                    //    $"Осталось без голоса: {in_psevdonims.Count - m_voiceItems.Count}");
                    //break;
                    random = new Random();
                    availableVoices.AddRange(m_voiceAllItems);
                    obnul += 1;
                }

                int randomIndex = -1;
                var selectedVoice = availableVoices.FirstOrDefault(xi => xi.Name == psevdonim);
                if (selectedVoice == null)
                {
                    // Выбираем случайный индекс
                    randomIndex = random.Next(availableVoices.Count);
                    selectedVoice = availableVoices[randomIndex];
                }

                // Создаём элемент
                var item = new VoiceItem
                {
                    Psevdonim = psevdonim,
                    Name = selectedVoice.Name,   // случайный голос
                    CountLinesText = in_psevdonims[psevdonim].Item2,
                    CountSymText = in_psevdonims[psevdonim].Item1,
                    Value = selectedVoice.Value
                };

                m_voiceItems.Add(item);

                // Убираем выбранный голос из списка доступных, чтобы не повторять
                if (randomIndex >= 0)
                    availableVoices.RemoveAt(randomIndex);
            }

            var mess = $"Заполнено {m_voiceItems.Count} строк случайными голосами.";
            if (obnul > 0)
                mess += $"Голосов на всех не хватало, поэтому было обнуление {obnul} раз.";

            Dispatcher.BeginInvoke(() =>
            {
                dgVoices.ItemsSource = m_voiceItems;

                // Обновляем интерфейс
                dgVoices.Items.Refresh();
            });

            Logger.LogInfo(mess);
        }

        private Thread m_thSpeaker;
        private void onClickSpeakTgText(object in_sender, RoutedEventArgs in_e)
        {
            var text = tbResultText.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text) && m_telegramParser != null)
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "MP3 файлы (*.mp3)|*.mp3",
                    FileName = "speechText.mp3",
                    Title = "Выберите имя и папку для первого файла (остальные сохранятся рядом)",
                    DefaultExt = ".mp3"
                };

                if (dlg.ShowDialog() != true)
                {
                    Logger.LogInfo("Синтез отменён пользователем (диалог сохранения).");
                    return;
                }

                var firstPath = dlg.FileName;
                m_thSpeaker = new Thread(() => speakMethod(text, firstPath));
                m_thSpeaker.Start();
            }
        }

        private async Task<bool> speakMethod(string in_text, string in_firstPath, bool in_isNeedGlue = true)
        {
            var sw = Stopwatch.StartNew();
            //_api.Configure(tbApiUrl.Text.Trim().TrimEnd('/'), tbApiKey.Text.Trim());
            _cts = new CancellationTokenSource();
            var dir = Path.GetDirectoryName(in_firstPath) ?? ".";
            var baseName = Path.GetFileNameWithoutExtension(in_firstPath);

            try
            {
                var newDir = Path.Combine(dir, baseName);
                if (!in_isNeedGlue)
                {
                    if (!Directory.Exists(newDir))
                        Directory.CreateDirectory(newDir);
                }

                string[] rows = in_text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var lastPsevdonim = "";
                var lastOutPath = "";
                var index = 1;
                var index2 = 1;
                var countAll = rows.Count();
                var elapsedMilliseconds = new List<long>();
                foreach (var xrow in rows)
                {
                    if (!string.IsNullOrWhiteSpace(xrow))
                    {
                        var partSw = Stopwatch.StartNew();
                        var psevdonim = "";
                        var comments = "";
                        var startMessageMatch = Regex.Match(xrow, @"^\[.*\] (.*?): (.*?)$", RegexOptions.Singleline);
                        if (startMessageMatch.Success)
                        {
                            psevdonim = startMessageMatch.Groups[1].Value.Trim();
                            comments = startMessageMatch.Groups[2].Value.Trim();
                        }
                        else
                        {
                            psevdonim = lastPsevdonim;
                            comments = xrow;
                        }

                        if (!string.IsNullOrWhiteSpace(psevdonim))
                        {
                            if (in_isNeedGlue)
                                comments = $"{psevdonim}: {comments}";

                            var voiceItem = m_voiceItems.Where(xi => xi.Psevdonim == psevdonim).FirstOrDefault();
                            if (voiceItem != null)
                            {
                                var outPath = System.IO.Path.Combine(dir, $"{(index2):D3}_{baseName}.mp3");
                                if (index < 2)
                                    outPath = System.IO.Path.Combine(dir, $"{baseName}.mp3");

                                var isSuccess = false;
                                var tryCount = 0;
                                var errors = new HashSet<string>();
                                while (!isSuccess && tryCount < 10)
                                {
                                    try
                                    {
                                        tryCount++;
                                        await _api.SynthesizeToFileAsync(
                                            input: comments,
                                            voice: voiceItem.Value,
                                            format: "mp3",
                                            outputPath: outPath,
                                            ct: _cts.Token);

                                        index += 1;
                                        if (in_isNeedGlue && !string.IsNullOrWhiteSpace(outPath) && !string.IsNullOrWhiteSpace(lastOutPath)
                                            && File.Exists(outPath) && File.Exists(lastOutPath))
                                        {
                                            // Сначала пытаемся склеить
                                            AudioProcessor.processAudioFiles(lastOutPath, outPath);

                                            // Если склеить не удалось (файл 002 всё ещё на месте) 
                                            // И первый файл уже превышает лимит — организуем в папку
                                            if (File.Exists(outPath))
                                            {
                                                //AudioProcessor.OrganizeFilesIfLimitExceeded(lastOutPath);
                                                index2 += 1;
                                                if (index2 == 2)
                                                {
                                                    var tempOutPath = System.IO.Path.Combine(dir, $"{(index2):D3}_{baseName}.mp3");
                                                    File.Move(outPath, tempOutPath);
                                                    File.Move(lastOutPath, outPath);
                                                    outPath = tempOutPath;
                                                    index2 += 1;
                                                }

                                                lastOutPath = outPath;
                                            }
                                            else
                                            {
                                                outPath = lastOutPath;
                                                //index -= 1;
                                            }
                                        }

                                        partSw.Stop();
                                        elapsedMilliseconds.Add(partSw.ElapsedMilliseconds);
                                        if (elapsedMilliseconds.Count > 10)
                                            elapsedMilliseconds.RemoveAt(0);
                                        var avgMill = Convert.ToInt32(elapsedMilliseconds.Sum() / elapsedMilliseconds.Count);
                                        var ostalosMilliseconds = (countAll - index) * avgMill;
                                        var tmpOst = new TimeSpan(0, 0, 0, 0, ostalosMilliseconds);
                                        var fileSize = getStrSizeFile(new FileInfo(outPath).Length);
                                        var duration = AudioProcessor.getAudioDuration(outPath);
                                        var percent = Math.Round(Convert.ToDouble(index) / (Convert.ToDouble(countAll) / 100.0), 3);
                                        var outFileName = System.IO.Path.GetFileNameWithoutExtension(outPath);
                                        setStatus($"Часть {index} из {countAll} готова за {partSw.Elapsed}. Прошло {sw.Elapsed}, примерно осталось {tmpOst}. Прогресс: {percent}% Файл: {outFileName} ({duration} - {fileSize})", percent);
                                        lastOutPath = outPath;
                                        lastPsevdonim = psevdonim;
                                        isSuccess = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        errors.Add(ex.Message);
                                        setStatus($"Неудалось озвучить текст длинной {comments.Count()} с попытки {tryCount} из 10, голосом {voiceItem.Name} из за ошибки: {ex.Message}.");
                                    }

                                    Thread.Sleep(1000);
                                }

                                if (!isSuccess)
                                    Logger.LogError($"Неудалось озвучить текст длинной {comments.Count()} с 10 попыток, голосом {voiceItem.Name} из-за ошибок {string.Join('\n', errors)}");
                            }
                            else
                                Logger.LogError($"Ненайдено чем озвучивать псевдоним {voiceItem}");
                        }
                        else
                            Logger.LogError($"Ненайден псевдоним");
                    }
                }

                sw.Stop();
                Logger.LogSuccess($"Озвучено {countAll} текста за {sw.Elapsed}.");
                setStatus($"✅ Озвучен текст за {sw.Elapsed}.");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.LogError($"Ошибка озвучки текста : {ex.Message} ({sw.Elapsed})");
                setStatus("❌ Ошибка озвучки текста");
                MessageBox.Show("Ошибка озвучки текста: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return true;
        }

        private async Task<bool> speakMethod(string in_firstPath, Dictionary<string, Tuple<int, int>> in_psevdonims, List<SubtitleItem> in_subtitles)
        {
            var sw = Stopwatch.StartNew();
            _cts = new CancellationTokenSource();
            var dir = Path.GetDirectoryName(in_firstPath) ?? ".";
            var baseName = Path.GetFileNameWithoutExtension(in_firstPath);

            try
            {
                dir = Path.Combine(dir, baseName);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var lastPsevdonim = "";
                var lastOutPath = "";
                var index = 1;
                var countAll = in_subtitles.Count();
                var elapsedMilliseconds = new List<long>();
                foreach (var xsub in in_subtitles)
                {
                    var psevdonim = $"Speaker {xsub.Speaker}";
                    var comments = xsub.TranslatedContent;
                    if (!(m_rawJsonVM.IsNoRus && xsub.DetectedLang == "Русский")
                            && !(!m_rawJsonVM.IncludeSounds && xsub.Content.StartsWith("["))
                            && !string.IsNullOrWhiteSpace(psevdonim))
                    {
                        var voiceItem = m_voiceItems.Where(xi => xi.Psevdonim == psevdonim).FirstOrDefault();
                        if (voiceItem != null)
                        {
                            var outPath = Path.Combine(dir, $"{(index):D3}_{baseName}.mp3");
                            var partSw = Stopwatch.StartNew();
                            var isSuccess = false;
                            var tryCount = 0;
                            var errors = new HashSet<string>();
                            while (!isSuccess && tryCount < 10)
                            {
                                try
                                {
                                    tryCount++;
                                    await synthesizeLongTextToFileAsync(
                                        comments,
                                        outPath,
                                        voiceItem.Value,
                                        _cts.Token,
                                        MaxTtsChunkLength);

                                    index += 1;
                                    partSw.Stop();
                                    elapsedMilliseconds.Add(partSw.ElapsedMilliseconds);
                                    if (elapsedMilliseconds.Count > 10)
                                        elapsedMilliseconds.RemoveAt(0);

                                    var avgMill = Convert.ToInt32(elapsedMilliseconds.Sum() / elapsedMilliseconds.Count);
                                    var ostalosMilliseconds = (countAll - index) * avgMill;
                                    var tmpOst = new TimeSpan(0, 0, 0, 0, ostalosMilliseconds);
                                    var fileSize = getStrSizeFile(new FileInfo(outPath).Length);
                                    var duration = AudioProcessor.getAudioDuration(outPath);
                                    var percent = Math.Round(Convert.ToDouble(index) / (Convert.ToDouble(countAll) / 100.0), 3);
                                    var outFileName = Path.GetFileNameWithoutExtension(outPath);
                                    setStatus($"Часть {index} из {countAll} готова за {partSw.Elapsed}. Прошло {sw.Elapsed}, примерно осталось {tmpOst}. Прогресс: {percent}% Файл: {outFileName} ({duration} - {fileSize})", percent);
                                    lastOutPath = outPath;
                                    lastPsevdonim = psevdonim;
                                    isSuccess = true;
                                }
                                catch (Exception ex)
                                {
                                    errors.Add(ex.Message);
                                    setStatus($"Неудалось озвучить текст длинной {comments.Count()} с попытки {tryCount} из 10, голосом {voiceItem.Name} из за ошибки: {ex.Message}.");
                                }

                                Thread.Sleep(1000);
                            }

                            if (!isSuccess)
                                Logger.LogError($"Неудалось озвучить текст длинной {comments.Count()} с 10 попыток, голосом {voiceItem.Name} из-за ошибок {string.Join('\n', errors)}");
                        }
                        else
                            Logger.LogError($"Ненайдено чем озвучивать псевдоним {voiceItem}");
                    }
                    else if (string.IsNullOrWhiteSpace(psevdonim))
                        Logger.LogError($"Ненайден псевдоним");
                }

                sw.Stop();
                Logger.LogSuccess($"Озвучено {countAll} текста за {sw.Elapsed}.");
                setStatus($"✅ Озвучен текст за {sw.Elapsed}.");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.LogError($"Ошибка озвучки текста : {ex.Message} ({sw.Elapsed})");
                setStatus("❌ Ошибка озвучки текста");
                MessageBox.Show("Ошибка озвучки текста: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return true;
        }

        private string getStrSizeFile(long in_sizeFile)
        {
            var ret = $"{in_sizeFile} байт";
            if (in_sizeFile > 1024)
            {
                double sizeFile = Math.Round(in_sizeFile / 1024.0, 3);
                ret = $"{sizeFile} Кб";
                if (sizeFile > 1024)
                {
                    sizeFile = Math.Round(sizeFile / 1024.0, 3);
                    ret = $"{sizeFile} Мб";
                    if (sizeFile > 1024)
                    {
                        sizeFile = Math.Round(sizeFile / 1024.0, 3);
                        ret = $"{sizeFile} Гб";
                    }
                }
            }

            return ret;
        }

        private List<string> splitTextIntoChunks(string in_text, int in_maxLength = 350)
        {
            var result = new List<string>();

            if (string.IsNullOrWhiteSpace(in_text))
                return result;

            in_text = Regex.Replace(in_text, @"\s+", " ").Trim();

            if (in_text.Length <= in_maxLength)
            {
                result.Add(in_text);
                return result;
            }

            var sentences = Regex.Split(in_text, @"(?<=[.!?…])\s+");
            var sb = new StringBuilder();

            foreach (var sentenceRaw in sentences)
            {
                var sentence = sentenceRaw.Trim();

                if (string.IsNullOrWhiteSpace(sentence))
                    continue;

                if (sentence.Length > in_maxLength)
                {
                    if (sb.Length > 0)
                    {
                        result.Add(sb.ToString().Trim());
                        sb.Clear();
                    }

                    result.AddRange(splitLongStringByWords(sentence, in_maxLength));
                }
                else if (sb.Length + sentence.Length + 1 <= in_maxLength)
                {
                    if (sb.Length > 0)
                        sb.Append(' ');

                    sb.Append(sentence);
                }
                else
                {
                    result.Add(sb.ToString().Trim());
                    sb.Clear();
                    sb.Append(sentence);
                }
            }

            if (sb.Length > 0)
                result.Add(sb.ToString().Trim());

            return result
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        private List<string> splitLongStringByWords(string in_text, int in_maxLength)
        {
            var result = new List<string>();
            var sb = new StringBuilder();

            var parts = Regex.Split(in_text, @"(?<=[,;:!?…])\s+");

            foreach (var partRaw in parts)
            {
                var part = partRaw.Trim();

                if (string.IsNullOrWhiteSpace(part))
                    continue;

                if (part.Length > in_maxLength)
                {
                    if (sb.Length > 0)
                    {
                        result.Add(sb.ToString().Trim());
                        sb.Clear();
                    }

                    var words = part.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var word in words)
                    {
                        if (word.Length > in_maxLength)
                        {
                            if (sb.Length > 0)
                            {
                                result.Add(sb.ToString().Trim());
                                sb.Clear();
                            }

                            for (int i = 0; i < word.Length; i += in_maxLength)
                            {
                                result.Add(word.Substring(i, Math.Min(in_maxLength, word.Length - i)));
                            }
                        }
                        else if (sb.Length + word.Length + 1 > in_maxLength)
                        {
                            if (sb.Length > 0)
                                result.Add(sb.ToString().Trim());

                            sb.Clear();
                            sb.Append(word);
                        }
                        else
                        {
                            if (sb.Length > 0)
                                sb.Append(' ');

                            sb.Append(word);
                        }
                    }
                }
                else if (sb.Length + part.Length + 1 <= in_maxLength)
                {
                    if (sb.Length > 0)
                        sb.Append(' ');

                    sb.Append(part);
                }
                else
                {
                    if (sb.Length > 0)
                        result.Add(sb.ToString().Trim());

                    sb.Clear();
                    sb.Append(part);
                }
            }

            if (sb.Length > 0)
                result.Add(sb.ToString().Trim());

            return result;
        }

        private async Task synthesizeLongTextToFileAsync(
            string in_text,
            string in_outputPath,
            string in_voice,
            CancellationToken in_ct,
            int in_maxChunkLength = 350)
        {
            var chunks = splitTextIntoChunks(in_text, in_maxChunkLength);

            if (chunks.Count == 0)
                return;

            if (chunks.Count == 1)
            {
                await synthesizeChunkWithRetryAsync(chunks[0], in_outputPath, in_voice, in_ct);
                return;
            }

            setStatus($"🧩 Длинный текст ({in_text.Length} симв.) разбит на {chunks.Count} частей для озвучки. Файл: {in_outputPath}", 0, true);

            var tempDir = Path.Combine(Path.GetTempPath(), $"tts_chunks_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var chunkFiles = new List<string>();

            try
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    in_ct.ThrowIfCancellationRequested();

                    var chunkPath = Path.Combine(tempDir, $"chunk_{i:D4}.mp3");

                    await synthesizeChunkWithRetryAsync(
                        chunks[i],
                        chunkPath,
                        in_voice,
                        in_ct
                    );

                    chunkFiles.Add(chunkPath);
                    setStatus($"🧩 Длинный текст ({in_text.Length} симв.) разбит на {chunks.Count} частей для озвучки. Файл: {in_outputPath}", 0, true);
                }

                await concatMp3FilesAsync(chunkFiles, in_outputPath, in_ct);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    // ignore
                }
            }
        }

        private async Task synthesizeChunkWithRetryAsync(
            string in_text,
            string in_outputPath,
            string in_voice,
            CancellationToken in_ct,
            int in_maxAttempts = 10)
        {
            for (int attempt = 1; attempt <= in_maxAttempts; attempt++)
            {
                try
                {
                    await _api.SynthesizeToFileAsync(
                        input: in_text,
                        voice: in_voice,
                        format: "mp3",
                        outputPath: in_outputPath,
                        ct: in_ct
                    );

                    if (File.Exists(in_outputPath) && new FileInfo(in_outputPath).Length > 0)
                        return;

                    throw new Exception("TTS вернул пустой файл.");
                }
                catch (Exception ex) when (!in_ct.IsCancellationRequested && attempt < in_maxAttempts)
                {
                    setStatus($"TTS ошибка для куска текста {in_outputPath} голосом {in_voice} (попытка {attempt}/{in_maxAttempts}): {ex.Message}", 0, true);
                    if (attempt == in_maxAttempts)
                    Logger.LogError(
                        $"Неудалось озвучить кусок {in_outputPath} голосом {in_voice} с {attempt} попыток: {ex.Message}"
                    );

                    await Task.Delay(1000, in_ct);
                }
            }
        }

        private async Task concatMp3FilesAsync(
            List<string> in_files,
            string in_outputPath,
            CancellationToken in_ct)
        {
            if (in_files.Count == 0)
                return;

            if (in_files.Count == 1)
            {
                File.Copy(in_files[0], in_outputPath, true);
                return;
            }

            var ffmpegPath = TxtFfmpeg.Text.Trim();

            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                throw new Exception(
                    "Для склейки длинной озвучки нужно указать корректный путь к ffmpeg.exe."
                );
            }

            var listFile = Path.Combine(Path.GetTempPath(), $"ffmpeg_concat_{Guid.NewGuid():N}.txt");

            try
            {
                var lines = in_files.Select(f =>
                {
                    var safePath = f.Replace('\\', '/').Replace("'", @"'\''");
                    return $"file '{safePath}'";
                });

                File.WriteAllLines(listFile, lines);
                var argsCopy = new List<string>
                    {
                        "-y",
                        "-f", "concat",
                        "-safe", "0",
                        "-i", listFile,
                        "-c", "copy",
                        in_outputPath
                    };

                try
                {
                    await runFfmpegAsync(ffmpegPath, argsCopy, 0, in_ct);

                    if (File.Exists(in_outputPath) && new FileInfo(in_outputPath).Length > 0)
                        return;
                }
                catch (Exception ex) when (!in_ct.IsCancellationRequested)
                {
                    Logger.LogInfo(
                        $"Склейка через -c copy не удалась. Пробую перекодировать. Ошибка: {ex.Message}"
                    );
                }

                var argsReencode = new List<string>
                    {
                        "-y",
                        "-f", "concat",
                        "-safe", "0",
                        "-i", listFile,
                        "-c:a", "libmp3lame",
                        "-b:a", "192k",
                        in_outputPath
                    };

                await runFfmpegAsync(ffmpegPath, argsReencode, 0, in_ct);

                if (!File.Exists(in_outputPath) || new FileInfo(in_outputPath).Length == 0)
                {
                    throw new Exception("Не удалось получить итоговый склеенный MP3-файл.");
                }
            }
            finally
            {
                try
                {
                    File.Delete(listFile);
                }
                catch
                {
                    // ignore
                }
            }
        }

        private void onClickSetVoice(object in_sender, RoutedEventArgs in_e)
        {
            // 1. Получаем сам объект, привязанный к выбранной строке (независимо от сортировки)
            var selectedItem = dgVoices.SelectedItem as VoiceItem;
            if (selectedItem == null) return; // Защита от случая, если ничего не выбрано

            var selectedVoice = m_voiceAllItems[cmbVoice.SelectedIndex];

            // 2. Обновляем свойства самого объекта
            selectedItem.Name = selectedVoice.Name;
            selectedItem.Value = selectedVoice.Value;

            // 3. Обновляем интерфейс
            dgVoices.Items.Refresh();
        }

        private async void onClickParseSub(object in_sender, RoutedEventArgs in_e)
        {
            await parseSubtitle();
        }

        private async Task parseSubtitle(string in_firstPath = "", bool in_isSpeak = false, List<SubtitleItem> subtitles = null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                setStatus("Парсинг субтитров", 0, true);
                if (subtitles == null)
                    subtitles = await getSubObjects();

                if (subtitles != null && subtitles.Count > 0)
                {
                    Dictionary<string, Tuple<int, int>> psevdonims = getPsevdonimsFromSubObjects(subtitles);
                    if (in_isSpeak)
                        await speakMethod(in_firstPath, psevdonims, subtitles);
                    else
                        await fillVoiceItemsRandomly(psevdonims);

                    sw.Stop();
                    var mess = $"✅ Распарсен текст субтитров за {sw.Elapsed}.";
                    Logger.LogSuccess(mess);
                    setStatus(mess);
                }
                else
                {
                    var mess = "❌ Неудалось распарсить субтитры";
                    setStatus(mess);
                    Logger.LogSuccess(mess);
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                var mess = $"❌ Ошибка парсинга субтитров ({sw.Elapsed}): {ex.Message}";
                Logger.LogError(mess);
                setStatus(mess);
                MessageBox.Show(mess, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static Dictionary<string, Tuple<int, int>> getPsevdonimsFromSubObjects(List<SubtitleItem> in_subtitles)
        {
            var ret = new Dictionary<string, Tuple<int, int>>();
            foreach (var xsub in in_subtitles)
            {
                if (!string.IsNullOrWhiteSpace(xsub.Content))
                {
                    var countSym = 0;
                    var countBlock = 0;
                    var psevdonim = $"Speaker {xsub.Speaker}";
                    if (ret.ContainsKey(psevdonim))
                    {
                        var tuple = ret[psevdonim];
                        countSym = tuple.Item1;
                        countBlock = tuple.Item2;
                    }

                    countSym += xsub.Content.Count();
                    countBlock += 1;
                    var newTuple = new Tuple<int, int>(countSym, countBlock);
                    ret[psevdonim] = newTuple;
                }
            }

            return ret;
        }

        private async Task<List<SubtitleItem>?> getSubObjects()
        {
            var ret = new List<SubtitleItem>();
            setStatus("Начали обработку субтиров");
            var dateStart = DateTime.Now;
            var subtitles = await m_rawJsonVM.getNormSub(dateStart);
            if (subtitles == null || subtitles.Count == 0)
            {
                var mess = "❌ Неудалось распарсить json текст";
                setStatus(mess);
                Logger.LogSuccess(mess);

                var text = tbResultText.Text.Trim();
                text = string.IsNullOrWhiteSpace(text) ? RawJsonTextBox.Text.Trim() : text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var lines = Regex.Split(text, "[\r\n]").ToList();
                    subtitles = SubtitleManager.parseFromLines(lines);
                }
            }

            SubtitleItem lastSubItem = null;
            if (subtitles?.Any() == true)
            {
                foreach (var xsub in subtitles)
                    if (!(m_rawJsonVM.IsNoRus && xsub.DetectedLang == "Русский")
                                && !(!m_rawJsonVM.IncludeSounds && xsub.Content.StartsWith("[")))
                    {
                        if (lastSubItem == null)
                            lastSubItem = xsub;

                        ret.Add(xsub);
                    }

                var jsonRawText = RawJsonViewModel.trySerializeSubJson(subtitles);
                Dispatcher.BeginInvoke(() =>
                {
                    RawJsonTextBox.Text = jsonRawText;
                });
            }

            return ret;
        }

        private void onClickSpeakSub(object in_sender, RoutedEventArgs in_e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "MP3 файлы (*.mp3)|*.mp3",
                FileName = "speechText.mp3",
                Title = "Выберите имя и папку для первого файла (остальные сохранятся рядом)",
                DefaultExt = ".mp3"
            };

            if (dlg.ShowDialog() != true)
            {
                Logger.LogInfo("Синтез отменён пользователем (диалог сохранения).");
                return;
            }

            var firstPath = dlg.FileName;
            m_thSpeaker = new Thread(() => parseSubtitle(firstPath, true));
            m_thSpeaker.Start();
        }

        private void onClickSpeakVideo(object in_sender, RoutedEventArgs in_e)
        {
            var dlg = new OpenFileDialog { Filter = "Video Files|*.mp4;*.mkv;*.avi" };
            if (dlg.ShowDialog() == true)
            {
                var mp4FilePath =  dlg.FileName;
                speakVideo(mp4FilePath);
            }
        }

        private async Task speakVideo(string in_videoPath)
        {
            var sw = Stopwatch.StartNew();
            setStatus($"Подготовка видео к озвучке: {in_videoPath}", 0, true);

            // Получаем путь к папке, где лежит исполняемый файл
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;

            // Возвращаем путь к родительской папке получаем папку где будут храниться все субтитры
            var subPath = Path.Combine(
                    Path.GetDirectoryName(in_videoPath) ?? Environment.CurrentDirectory,
                    "subtitlesCache"
                );
            
            Logger.tryDeleteFiles(subPath, "*.mp3");
            Logger.tryDeleteFiles(subPath, "*.srt");
            var subtitles = await getSubObjects();
            await parseSubtitle(subPath, true, subtitles);
            await removeVocal(in_videoPath);
            var newFileName = $"{Path.GetFileNameWithoutExtension(in_videoPath)} RusAudio.mp4";
            string instrumentalPath = ChkUseInstrumentalOnSubtitles.IsChecked == true
                    ? TxtInstrumental.Text.Trim()
                    : null;

            await speakVideo(
                subPath,
                in_videoPath,
                newFileName,
                subtitles,
                instrumentalPath
            );

            sw.Stop();
            var mess = $"✅ Озвучено видео {newFileName} за {sw.Elapsed}.";
            Logger.LogSuccess(mess);
            setStatus(mess);
        }

        private void BrowseInstrumental_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Audio Files|*.mp3;*.wav|MP3|*.mp3|WAV|*.wav"
            };

            if (dlg.ShowDialog() == true)
            {
                TxtInstrumental.Text = dlg.FileName;
            }
        }

        private async void RemoveVocal_Click(object sender, RoutedEventArgs e)
        {
            var videoPath = TxtVideo.Text.Trim();
            await removeVocal(videoPath);
        }

        private async Task<bool> removeVocal(string in_videoPath)
        {
            var ret = false;
            var sw = Stopwatch.StartNew();
            if (!File.Exists(in_videoPath))
            {
                sw.Stop();
                ShowErr("Сначала выберите существующий видеофайл *.mp4.");
                return false;
            }

            try
            {
                BtnRemoveVocal.IsEnabled = false;

                var outputDir = Path.Combine(
                    Path.GetDirectoryName(in_videoPath) ?? Environment.CurrentDirectory,
                    "vocal_removed"
                );

                Directory.CreateDirectory(outputDir);

                setStatus($"🎵 Удаление вокала через сервер... Это может занять несколько минут. Видео: {in_videoPath}", 0, true);
                var requireGpu = ChkRequireGpuForVocal.IsChecked == true;

                var mp3Path = await removeVocalViaServerAsync(
                    in_videoPath,
                    outputDir,
                    requireGpu,
                    CancellationToken.None
                );

                TxtInstrumental.Text = mp3Path;
                ChkUseInstrumentalOnSubtitles.IsChecked = true;
                ret = true;

                sw.Stop();

                setStatus($"✅ Аудио без вокала создано за {sw.Elapsed}: {mp3Path}", 0, false);
                Logger.LogSuccess($"Удаление вокала завершено за {sw.Elapsed}: {mp3Path}");
            }
            catch (Exception ex)
            {
                sw.Stop();

                setStatus($"❌ Ошибка удаления вокала за {sw.Elapsed}: {ex.Message}", 0, false);
                Logger.LogError($"Ошибка удаления вокала за {sw.Elapsed}: {ex.Message}");

                MessageBox.Show(
                    ex.Message,
                    $"Ошибка удаления вокала за {sw.Elapsed}",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            finally
            {
                BtnRemoveVocal.IsEnabled = true;
                PbProgress.IsIndeterminate = false;
                PbProgress.Visibility = Visibility.Collapsed;
            }

            return ret;
        }

        private async Task<string> removeVocalViaServerAsync(
            string in_videoPath,
            string in_outputDir,
            bool in_requireGpu,
            CancellationToken in_ct)
        {
            var payload = new
            {
                video_path = Path.GetFullPath(in_videoPath),
                output_dir = Path.GetFullPath(in_outputDir),
                delete_wav_after_mp3 = true,
                require_gpu = in_requireGpu
            };

            string json = JsonSerializer.Serialize(payload);

            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                using (var response = await _vocalHttpClient.PostAsync(VocalRemoverEndpoint, content, in_ct))
                {
                    string text = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        string errorMessage = $"HTTP {(int)response.StatusCode}";

                        try
                        {
                            using (var errorDoc = JsonDocument.Parse(text))
                            {
                                if (errorDoc.RootElement.TryGetProperty("detail", out var detailProp))
                                {
                                    errorMessage += Environment.NewLine + detailProp.ToString();
                                }
                                else
                                {
                                    errorMessage += Environment.NewLine + text;
                                }
                            }
                        }
                        catch
                        {
                            errorMessage += Environment.NewLine + text;
                        }

                        throw new Exception(errorMessage);
                    }

                    using (var doc = JsonDocument.Parse(text))
                    {
                        var root = doc.RootElement;

                        if (!root.TryGetProperty("success", out var successProp))
                        {
                            throw new Exception($"Сервер вернул некорректный ответ: {text}");
                        }

                        bool isSuccess =
                            successProp.ValueKind == JsonValueKind.True ||
                            (successProp.ValueKind == JsonValueKind.String &&
                             successProp.GetString()?.ToLower() == "true");

                        if (!isSuccess)
                        {
                            string detail = text;

                            if (root.TryGetProperty("detail", out var detailProp))
                            {
                                detail = detailProp.ToString();
                            }
                            else if (root.TryGetProperty("message", out var messageProp))
                            {
                                detail = messageProp.ToString();
                            }

                            throw new Exception(detail);
                        }

                        string resultPath = null;

                        // Основной вариант — сервер уже вернул MP3
                        if (root.TryGetProperty("no_vocal_mp3_path", out var mp3Prop))
                        {
                            resultPath = mp3Prop.GetString();
                        }

                        // Запасной вариант — если сервер старый и вернул WAV/путь к инструменталу
                        if (string.IsNullOrWhiteSpace(resultPath) &&
                            root.TryGetProperty("instrumental_path", out var instrumentalProp))
                        {
                            resultPath = instrumentalProp.GetString();
                        }

                        if (string.IsNullOrWhiteSpace(resultPath))
                        {
                            throw new Exception(
                                "Сервер завершил работу, но не вернул путь к аудио без вокала. " +
                                $"Ответ: {text}"
                            );
                        }

                        if (!Path.IsPathRooted(resultPath))
                        {
                            resultPath = Path.GetFullPath(resultPath);
                        }

                        return resultPath;
                    }
                }
            }
        }

        #region Пакетная озвучка нескольких видео с клонированием голосов

        private async void onClickBatchSpeakVideos(object in_sender, RoutedEventArgs in_e)
        {
            var dlgVideos = new OpenFolderDialog
            {
                Title = "Выберите папку с видео и JSON-переводами (имена файлов должны совпадать)"
            };
            if (dlgVideos.ShowDialog() != true)
                return;

            string voicesRoot = null;
            var dlgVoices = new OpenFolderDialog
            {
                Title = "Папка с сохранёнными голосами (Speaker N.mp3 + Speaker N.txt). " +
                        "Нажмите «Отмена», если образцы голосов нужно извлечь из самих видео."
            };
            if (dlgVoices.ShowDialog() == true)
                voicesRoot = dlgVoices.FolderName;

            try
            {
                await batchSpeakVideosAsync(dlgVideos.FolderName, voicesRoot);
            }
            catch (OperationCanceledException)
            {
                setStatus("❌ Пакетная озвучка отменена.");
                Logger.LogInfo("Пакетная озвучка отменена пользователем.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка пакетной озвучки: {ex.Message}");
                setStatus("❌ Ошибка пакетной озвучки");
                MessageBox.Show(ex.Message, "Ошибка пакетной озвучки",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Пакетная озвучка: для каждого видео в папке ищет JSON-перевод с тем же именем,
        /// берёт образцы голосов (Speaker N.mp3 + Speaker N.txt) из папки сохранённых голосов
        /// (или извлекает их из видео через SpeakerAudioExtractor — сразу с txt),
        /// клонирует голос через /v1/higgs/voice-clone для каждой переведённой реплики
        /// и собирает итоговое видео существующим микшером speakVideo(...).
        /// </summary>
        private async Task batchSpeakVideosAsync(string in_videoDir, string in_voicesRoot)
        {
            var sw = Stopwatch.StartNew();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            var videos = Directory.GetFiles(in_videoDir, "*.mp4")
                .Concat(Directory.GetFiles(in_videoDir, "*.mkv"))
                .Concat(Directory.GetFiles(in_videoDir, "*.avi"))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (videos.Count == 0)
            {
                ShowErr($"В папке {in_videoDir} не найдено видеофайлов (*.mp4, *.mkv, *.avi).");
                return;
            }

            var ffmpegPath = TxtFfmpeg.Text.Trim();
            int videoNum = 0;
            foreach (var videoPath in videos)
            {
                ct.ThrowIfCancellationRequested();
                videoNum++;
                var baseName = Path.GetFileNameWithoutExtension(videoPath);
                var jsonPath = findSubtitlesJsonForVideo(videoPath);
                if (jsonPath == null)
                {
                    Logger.LogError($"Видео {baseName}: рядом не найден JSON с переводом. Пропуск.");
                    continue;
                }

                setStatus($"🎬 [{videoNum}/{videos.Count}] {baseName}: чтение JSON {Path.GetFileName(jsonPath)}", 0, true);
                var subtitles = deserializeSubtitlesJson(await File.ReadAllTextAsync(jsonPath, ct));
                var speakList = subtitles
                    .Where(x => !string.IsNullOrWhiteSpace(x.TranslatedContent)
                                && !x.TranslatedContent.Trim().StartsWith("["))
                    .OrderBy(x => x.Start)
                    .ToList();
                if (speakList.Count == 0)
                {
                    Logger.LogError($"Видео {baseName}: в JSON нет реплик с переводом. Пропуск.");
                    continue;
                }

                try
                {
                    // --- 1. Образцы голосов: папка сохранённых голосов или извлечение из видео ---
                    var resolvedVoices = resolveVoicesFolder(in_voicesRoot, baseName);
                    string extractedVoices = null;

                    string getRefAudio(int speaker)
                    {
                        foreach (var folder in new[] { extractedVoices, resolvedVoices })
                        {
                            if (string.IsNullOrWhiteSpace(folder)) continue;
                            var p = Path.Combine(folder, $"Speaker {speaker}.mp3");
                            if (File.Exists(p)) return p;
                        }
                        return null;
                    }
                    string getRefText(int speaker)
                    {
                        foreach (var folder in new[] { extractedVoices, resolvedVoices })
                        {
                            if (string.IsNullOrWhiteSpace(folder)) continue;
                            var p = Path.Combine(folder, $"Speaker {speaker}.txt");
                            if (File.Exists(p)) return File.ReadAllText(p);
                        }
                        return null;
                    }
                    async Task ensureExtractedAsync()
                    {
                        if (extractedVoices != null) return;
                        setStatus($"🎬 [{videoNum}/{videos.Count}] {baseName}: извлекаю образцы голосов из видео...", 0, true);
                        var extracted = await SpeakerAudioExtractor.extractSpeakerAudioAsync(
                            videoPath, subtitles, ffmpegPath);
                        if (extracted.Count > 0)
                            extractedVoices = Path.GetDirectoryName(extracted[0]);
                    }

                    // --- 2. Синтез переведённых реплик клонированным голосом ---
                    var subPath = Path.Combine(in_videoDir, "subtitlesCache");
                    Logger.tryDeleteFiles(subPath, "*.mp3");
                    Directory.CreateDirectory(subPath);
                    var countAll = speakList.Count();
                    var elapsedMilliseconds = new List<long>();
                    int index = 1;
                    foreach (var sub in speakList)
                    {
                        var partSw = Stopwatch.StartNew();
                        ct.ThrowIfCancellationRequested();
                        var outPath = Path.Combine(subPath, $"{index:D3}_subtitlesCache.mp3");
                        var refAudio = getRefAudio(sub.Speaker);
                        if (refAudio == null)
                        {
                            await ensureExtractedAsync();
                            refAudio = getRefAudio(sub.Speaker);
                        }

                        if (refAudio != null)
                        {
                            var refText = getRefText(sub.Speaker);
                            await cloneSpeakWithRetryAsync(sub.TranslatedContent, refAudio, refText, outPath, ct);
                        }
                        else
                        {
                            Logger.LogInfo($"Видео {baseName}: для спикера {sub.Speaker} нет образца голоса — синтезирую голосом default.");
                            await _api.SynthesizeToFileAsync(sub.TranslatedContent, "default", "mp3", outPath, ct);
                        }

                        partSw.Stop();
                        elapsedMilliseconds.Add(partSw.ElapsedMilliseconds);
                        if (elapsedMilliseconds.Count > 10)
                            elapsedMilliseconds.RemoveAt(0);
                        var avgMill = Convert.ToInt32(elapsedMilliseconds.Sum() / elapsedMilliseconds.Count);
                        var ostalosMilliseconds = (countAll - index) * avgMill;
                        var tmpOst = new TimeSpan(0, 0, 0, 0, ostalosMilliseconds);
                        var fileSize = getStrSizeFile(new FileInfo(outPath).Length);
                        var duration = AudioProcessor.getAudioDuration(outPath);
                        var percent = Math.Round(Convert.ToDouble(index) / (Convert.ToDouble(countAll) / 100.0), 3);
                        var outFileName = System.IO.Path.GetFileNameWithoutExtension(outPath);

                        setStatus($"🎬 [{videoNum}/{videos.Count}] {baseName}: реплика {index}/{speakList.Count} (спикер {sub.Speaker}). Прошло {sw.Elapsed}, примерно осталось {tmpOst}. Прогресс: {percent}% Файл: {outFileName} ({duration} - {fileSize})", percent);
                        Math.Round(100.0 * index / speakList.Count, 1);
                        index++;
                    }

                    // --- 3. Инструментал (опционально) и финальная склейка с видео ---
                    string instrumental = null;
                    if (ChkUseInstrumentalOnSubtitles.IsChecked == true)
                    {
                        setStatus($"🎬 [{videoNum}/{videos.Count}] {baseName}: удаление вокала для инструментала...", 0, true);
                        if (await removeVocal(videoPath))
                            instrumental = TxtInstrumental.Text.Trim();
                    }

                    var newFileName = $"{baseName} RusAudio.mp4";
                    await speakVideo(subPath, videoPath, newFileName, speakList, instrumental);
                    Logger.LogSuccess($"🎬 [{videoNum}/{videos.Count}] Видео {baseName} озвучено: {newFileName}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Ошибка одного видео не останавливает весь пакет.
                    Logger.LogError($"Видео {baseName}: ошибка озвучки: {ex.Message}");
                    setStatus($"❌ Видео {baseName}: ошибка озвучки: {ex.Message}");
                }
            }

            sw.Stop();
            setStatus($"✅ Пакетная озвучка завершена за {sw.Elapsed}. Видео в обработке: {videos.Count}.");
            Logger.LogSuccess($"Пакетная озвучка завершена за {sw.Elapsed}.");
        }

        /// <summary>POST /v1/higgs/voice-clone: синтез фразы голосом из reference-аудио.</summary>
        private async Task cloneSpeakToFileAsync(
            string in_input,
            string in_referenceAudioPath,
            string in_referenceText,
            string in_outputPath,
            CancellationToken in_ct)
        {
            var baseUrl = tbApiUrl.Text.Trim().TrimEnd('/');
            var url = $"{baseUrl}/v1/higgs/voice-clone";
            var payload = new Dictionary<string, object>
            {
                ["input"] = in_input,
                ["reference_audio_path"] = Path.GetFullPath(in_referenceAudioPath),
                ["response_format"] = "mp3",
                ["max_tokens"] = 2048
            };
            if (!string.IsNullOrWhiteSpace(in_referenceText))
                payload["reference_text"] = in_referenceText;

            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {tbApiKey.Text.Trim()}");

            using var response = await _vocalHttpClient.SendAsync(request, in_ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(in_ct);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"voice-clone HTTP {(int)response.StatusCode}: {Encoding.UTF8.GetString(bytes)}");
            if (bytes.Length == 0)
                throw new Exception("voice-clone вернул пустой файл.");
            await File.WriteAllBytesAsync(in_outputPath, bytes, in_ct);
        }

        private async Task cloneSpeakWithRetryAsync(
            string in_input,
            string in_referenceAudio,
            string in_referenceText,
            string in_outputPath,
            CancellationToken in_ct,
            int in_maxAttempts = 10)
        {
            var errors = new List<string>();
            for (int attempt = 1; attempt <= in_maxAttempts; attempt++)
            {
                try
                {
                    await cloneSpeakToFileAsync(in_input, in_referenceAudio, in_referenceText, in_outputPath, in_ct);
                    if (File.Exists(in_outputPath) && new FileInfo(in_outputPath).Length > 0)
                        return;
                    throw new Exception("Сервер вернул пустой файл.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                    setStatus($"⚠️ Клонирование голоса, попытка {attempt}/{in_maxAttempts}: {ex.Message}", 0, true);
                    await Task.Delay(1000, in_ct);
                }
            }
            throw new Exception($"Не удалось клонировать реплику за {in_maxAttempts} попыток: {string.Join(" | ", errors)}");
        }

        /// <summary>Ищет JSON-перевод рядом с видео: имя.json, имя_ru.json, *имя*.json, либо единственный json в папке.</summary>
        private static string findSubtitlesJsonForVideo(string in_videoPath)
        {
            var ext = Path.GetExtension(in_videoPath);
            var dir = Path.GetDirectoryName(in_videoPath) ?? ".";
            var baseName = Path.GetFileNameWithoutExtension(in_videoPath);
            string ret = null;
            if (ext == ".mp4")
            {
                var candidates = new[]
                {
                    Path.Combine(dir, baseName + ".json"),
                    Path.Combine(dir, baseName + "_ru.json"),
                    Path.Combine(dir, baseName + ".ru.json"),
                };

                foreach (var c in candidates)
                    if (File.Exists(c))
                        ret = c;

                if (string.IsNullOrWhiteSpace(ret))
                {
                    var byName = Directory.GetFiles(dir, "*.json")
                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                        .Contains(baseName, StringComparison.OrdinalIgnoreCase));
                    if (byName != null)
                        ret = byName;

                    if (string.IsNullOrWhiteSpace(ret))
                    {
                        var allJson = Directory.GetFiles(dir, "*.json");
                        var allVideos = Directory.GetFiles(dir, "*.mp4")
                            .Concat(Directory.GetFiles(dir, "*.mkv"))
                            .Concat(Directory.GetFiles(dir, "*.avi"))
                            .ToArray();

                        if (allJson.Length == 1 && allVideos.Length == 1)
                            ret = allJson[0];
                    }
                }
            }
            else if (ext == ".json")
            {
                var candidates = new[]
                {
                    Path.Combine(dir, baseName + ".mp4"),
                };

                foreach (var c in candidates)
                    if (File.Exists(c))
                        ret = in_videoPath;

                if (string.IsNullOrWhiteSpace(ret))
                {
                    var byName = Directory.GetFiles(dir, "*.mp4")
                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                        .Contains(baseName, StringComparison.OrdinalIgnoreCase));

                    if (byName != null) ret = in_videoPath;
                }
            }

            var resultVideoPath = Path.Combine(dir, $"{baseName} RusAudio.mp4");
            if (File.Exists(resultVideoPath))
            {
                Logger.LogInfo($"Уже есть переведённый файл: {resultVideoPath}");
                ret = null;
            }

            return ret;
        }

        /// <summary>
        /// Определяет папку с образцами голосов для конкретного видео:
        /// voicesRoot/&lt;имя видео&gt;/ → voicesRoot/tempFiles/ → voicesRoot/ (если там лежат Speaker *.mp3).
        /// </summary>
        private static string resolveVoicesFolder(string in_voicesRoot, string in_videoBaseName)
        {
            if (string.IsNullOrWhiteSpace(in_voicesRoot)) return null;
            var perVideo = Path.Combine(in_voicesRoot, in_videoBaseName);
            if (Directory.Exists(perVideo) && Directory.GetFiles(perVideo, "Speaker *.mp3").Length > 0)
                return perVideo;
            var temp = Path.Combine(in_voicesRoot, "tempFiles");
            if (Directory.Exists(temp) && Directory.GetFiles(temp, "Speaker *.mp3").Length > 0)
                return temp;
            if (Directory.GetFiles(in_voicesRoot, "Speaker *.mp3").Length > 0)
                return in_voicesRoot;
            return null;
        }

        /// <summary>
        /// Терпимый парсер JSON субтитров: игнорирует пробелы в именах ключей и значениях
        /// (вид "Index ", "00:00:04 " в пример.json), заполняет Start/End из StartTime/EndTime при нужде.
        /// </summary>
        public static List<SubtitleItem> deserializeSubtitlesJson(string in_json)
        {
            var ret = new List<SubtitleItem>();
            if (string.IsNullOrWhiteSpace(in_json)) return ret;
            using var doc = JsonDocument.Parse(in_json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return ret;

            int autoIndex = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var item = new SubtitleItem { Content = "", TranslatedContent = "", DetectedLang = "" };
                foreach (var prop in el.EnumerateObject())
                {
                    switch (prop.Name.Trim())
                    {
                        case "Index":
                            item.Index = prop.Value.ValueKind == JsonValueKind.Number
                                ? prop.Value.GetInt32()
                                : (int.TryParse(prop.Value.GetString()?.Trim(), out var ii) ? ii : 0);
                            break;
                        case "StartTime":
                            item.StartTime = parseJsonTime(prop.Value.GetString());
                            break;
                        case "EndTime":
                            item.EndTime = parseJsonTime(prop.Value.GetString());
                            break;
                        case "Start":
                            item.Start = prop.Value.GetDouble();
                            break;
                        case "End":
                            item.End = prop.Value.GetDouble();
                            break;
                        case "Speaker":
                            item.Speaker = prop.Value.ValueKind == JsonValueKind.Number
                                ? prop.Value.GetInt32()
                                : (int.TryParse(prop.Value.GetString()?.Trim(), out var ss) ? ss : 0);
                            break;
                        case "Content":
                            item.Content = prop.Value.GetString()?.Trim() ?? "";
                            break;
                        case "TranslatedContent":
                            item.TranslatedContent = prop.Value.GetString()?.Trim() ?? "";
                            break;
                        case "DetectedLang":
                            item.DetectedLang = prop.Value.GetString()?.Trim() ?? "";
                            break;
                    }
                }
                if (item.Index <= 0) item.Index = ++autoIndex; else autoIndex = item.Index;
                if (item.End <= item.Start && item.EndTime > item.StartTime)
                {
                    item.Start = item.StartTime.TotalSeconds;
                    item.End = item.EndTime.TotalSeconds;
                }
                ret.Add(item);
            }
            return ret;
        }

        private static TimeSpan parseJsonTime(string in_value)
        {
            var s = in_value?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return TimeSpan.Zero;
            if (TimeSpan.TryParseExact(s, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var ts))
                return ts;
            return TimeSpan.Parse(s, CultureInfo.InvariantCulture);
        }

        #endregion

        #region Очередь дубляжа переводов

        private void onClickAddVideosToDubQueue(object in_sender, RoutedEventArgs in_e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Video Files|*.mp4;*.json;*.avi",
                Multiselect = true,
                Title = "Добавить видео в очередь дубляжа"
            };

            if (dlg.ShowDialog() == true)
                addVideosToDubQueue(dlg.FileNames);
        }

        private void onClickAddFolderToDubQueue(object in_sender, RoutedEventArgs in_e)
        {
            var dlg = new OpenFolderDialog { Title = "Папка с видео для очереди дубляжа" };
            if (dlg.ShowDialog() != true) return;
            var files = Directory.GetFiles(dlg.FolderName, "*.mp4")
                .Concat(Directory.GetFiles(dlg.FolderName, "*.json"))
                .Concat(Directory.GetFiles(dlg.FolderName, "*.avi"))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            addVideosToDubQueue(files);
        }

        private void addVideosToDubQueue(IEnumerable<string> in_videoPaths)
        {
            int added = 0;
            foreach (var path in in_videoPaths)
            {
                if (m_dubQueue.Any(x => string.Equals(x.VideoPath, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var jsonPath = findSubtitlesJsonForVideo(path);
                if (string.IsNullOrWhiteSpace(jsonPath))
                    Logger.LogError($"Нет JSON по пути: {path}");
                else
                {
                    var videoPath = path;
                    var ext = Path.GetExtension(path);
                    if (ext == ".json")
                        videoPath = $"{path.Substring(0, path.Length - 5)}.mp4";

                    var item = new DubQueueItem
                    {
                        VideoPath = videoPath,
                        JsonPath = jsonPath ?? "",
                        Status = string.IsNullOrWhiteSpace(jsonPath) ? "Нет JSON" : "В очереди"
                    };

                    m_dubQueue.Add(item);
                    added++;
                }
            }

            dgDubQueue.Items.Refresh();
            Logger.LogInfo($"➕ Добавлено в очередь дубляжа: {added}. Всего в очереди: {m_dubQueue.Count}.");
            setStatus($"➕ В очереди дубляжа: {m_dubQueue.Count} видео.");
        }

        private void onClickRemoveSelectedFromDubQueue(object in_sender, RoutedEventArgs in_e)
        {
            if (m_isDubQueueRunning) { ShowErr("Очередь выполняется — изменять её нельзя."); return; }
            var selected = dgDubQueue.SelectedItems.Cast<DubQueueItem>().ToList();
            foreach (var item in selected) m_dubQueue.Remove(item);
            dgDubQueue.Items.Refresh();
        }

        private void onClickClearDubQueue(object in_sender, RoutedEventArgs in_e)
        {
            if (m_isDubQueueRunning) { ShowErr("Очередь выполняется — очищать её нельзя."); return; }
            m_dubQueue.Clear();
            dgDubQueue.Items.Refresh();
        }

        private async void onClickStartDubQueue(object in_sender, RoutedEventArgs in_e)
        {
            if (m_isDubQueueRunning) { Logger.LogInfo("Очередь дубляжа уже выполняется."); return; }
            var pending = m_dubQueue.Where(x => x.Status != "Готово").ToList();
            if (pending.Count == 0) { ShowErr("Очередь пуста или все видео уже озвучены."); return; }

            m_isDubQueueRunning = true;
            _cts = new CancellationTokenSource();
            _api.Configure(tbApiUrl.Text.Trim().TrimEnd('/'), tbApiKey.Text.Trim());
            try
            {
                await processDubQueueAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                setStatus("❌ Очередь дубляжа прервана.");
                Logger.LogInfo("Очередь дубляжа прервана пользователем.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Ошибка очереди дубляжа: {ex.Message}");
                MessageBox.Show(ex.Message, "Ошибка очереди дубляжа", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                m_isDubQueueRunning = false;
            }
        }

        private async Task processDubQueueAsync(CancellationToken in_ct)
        {
            var queueSw = Stopwatch.StartNew();
            m_totalDubTime = TimeSpan.Zero;
            m_totalDubbedVideos = 0;
            int totalVideos = m_dubQueue.Count(x => x.Status != "Готово");
            int videoNum = 0;

            foreach (var item in m_dubQueue.ToList())
            {
                if (item.Status == "Готово") continue;
                in_ct.ThrowIfCancellationRequested();
                videoNum++;
                await processOneDubVideoAsync(item, videoNum, totalVideos, in_ct);
            }

            queueSw.Stop();
            var mess = $"✅ Очередь дубляжа завершена за {queueSw.Elapsed}. " +
                       $"Озвучено видео: {m_totalDubbedVideos}. Суммарное время озвучки: {formatTimeSpan(m_totalDubTime)}.";
            setStatus(mess);
            Logger.LogSuccess(mess);
        }

        /// <summary>
        /// Полный цикл по одному видео очереди:
        /// 1) заново извлекаются образцы голосов (Speaker N.mp3) и тексты-референсы (Speaker N.txt);
        /// 2) каждую переведённую реплику клонируем голосом соответствующего спикера;
        /// 3) склейка с видео; 4) удаление временных файлов (готовое видео остаётся);
        /// 5) лог: время озвучки, соотношение длительности видео и времени озвучки, суммарное время.
        /// </summary>
        private async Task processOneDubVideoAsync(DubQueueItem in_item, int in_videoNum, int in_totalVideos, CancellationToken in_ct)
        {
            var videoPath = in_item.VideoPath;
            var baseName = Path.GetFileNameWithoutExtension(videoPath);
            var videoDir = Path.GetDirectoryName(videoPath) ?? Environment.CurrentDirectory;
            var videoSw = Stopwatch.StartNew();
            in_item.Status = "В работе";
            in_item.Progress = 0;
            in_item.Error = "";
            try
            {
                // --- 0. JSON перевода ---
                var jsonPath = in_item.JsonPath;
                if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
                {
                    jsonPath = findSubtitlesJsonForVideo(videoPath);
                    in_item.JsonPath = jsonPath ?? "";
                }
                if (string.IsNullOrWhiteSpace(jsonPath))
                    throw new Exception("Не найден JSON с переводом рядом с видео.");
                var subtitles = deserializeSubtitlesJson(await File.ReadAllTextAsync(jsonPath, in_ct));
                var speakList = subtitles
                    .Where(x => !string.IsNullOrWhiteSpace(x.TranslatedContent)
                                && !x.TranslatedContent.Trim().StartsWith("["))
                    .OrderBy(x => x.Start)
                    .ToList();
                if (speakList.Count == 0)
                    throw new Exception("В JSON нет реплик с переводом.");

                // --- 1. Заново извлекаем образцы голосов И тексты-референсы для этого видео ---
                var tempDir = Path.Combine(videoDir, "tempFiles");
                Logger.tryDeleteFiles(tempDir, "Speaker *.mp3");
                Logger.tryDeleteFiles(tempDir, "Speaker *.txt");
                in_item.Status = "Извлечение голосов";
                setStatus($"🎬 [{in_videoNum}/{in_totalVideos}] {baseName}: извлекаю образцы голосов и пишу txt-референсы...", 0, true);
                var extracted = await SpeakerAudioExtractor.extractSpeakerAudioAsync(
                    videoPath, subtitles, TxtFfmpeg.Text.Trim());
                var voicesFolder = extracted.Count > 0 ? Path.GetDirectoryName(extracted[0]) : tempDir;

                // --- 2. Клонирование и синтез каждой реплики ---
                var subPath = Path.Combine(videoDir, "subtitlesCache");
                Logger.tryDeleteFiles(subPath, "*.mp3");
                Directory.CreateDirectory(subPath);
                int index = 1;
                var countAll = speakList.Count();
                var elapsedMilliseconds = new List<long>();
                foreach (var sub in speakList)
                {
                    var partSw = Stopwatch.StartNew();
                    in_ct.ThrowIfCancellationRequested();
                    var outPath = Path.Combine(subPath, $"{index:D3}_subtitlesCache.mp3");
                    var refAudio = Path.Combine(voicesFolder, $"Speaker {sub.Speaker}.mp3");
                    var refTextPath = Path.Combine(voicesFolder, $"Speaker {sub.Speaker}.txt");
                    in_item.Status = $"Озвучка {index}/{speakList.Count}";
                    if (File.Exists(refAudio))
                    {
                        var refText = File.Exists(refTextPath) ? await File.ReadAllTextAsync(refTextPath, in_ct) : null;
                        await cloneLongTextWithRetryAsync(sub.TranslatedContent, refAudio, refText, outPath, in_ct, MaxTtsChunkLength);
                    }
                    else
                    {
                        Logger.LogInfo($"{baseName}: для спикера {sub.Speaker} нет образца голоса — синтез голосом default.");
                        await synthesizeLongTextToFileAsync(sub.TranslatedContent, outPath, "default", in_ct, MaxTtsChunkLength);
                    }

                    in_item.Progress = Math.Round(100.0 * index / speakList.Count, 1);

                    partSw.Stop();
                    elapsedMilliseconds.Add(partSw.ElapsedMilliseconds);
                    if (elapsedMilliseconds.Count > 10)
                        elapsedMilliseconds.RemoveAt(0);
                    var avgMill = Convert.ToInt32(elapsedMilliseconds.Sum() / elapsedMilliseconds.Count);
                    var ostalosMilliseconds = (countAll - index) * avgMill;
                    var tmpOst = new TimeSpan(0, 0, 0, 0, ostalosMilliseconds);
                    var fileSize = getStrSizeFile(new FileInfo(outPath).Length);
                    var duration = AudioProcessor.getAudioDuration(outPath);
                    var percent = Math.Round(Convert.ToDouble(index) / (Convert.ToDouble(countAll) / 100.0), 3);
                    var outFileName = System.IO.Path.GetFileNameWithoutExtension(outPath);

                    setStatus($"🎬 [{in_videoNum}/{in_totalVideos}] {baseName}: реплика {index}/{speakList.Count} (спикер {sub.Speaker}). Прошло {videoSw.Elapsed}, примерно осталось {tmpOst}. Прогресс: {percent}% Файл: {outFileName} ({duration} - {fileSize})",
                        in_item.Progress);
                    index++;
                }

                // --- 3. Инструментал (опционально, как в одиночном режиме) ---
                string instrumental = null;
                if (ChkUseInstrumentalOnSubtitles.IsChecked == true)
                {
                    in_item.Status = "Удаление вокала";
                    setStatus($"🎬 [{in_videoNum}/{in_totalVideos}] {baseName}: удаление вокала...", 0, true);
                    if (await removeVocal(videoPath))
                        instrumental = TxtInstrumental.Text.Trim();
                }

                // --- 4. Склейка озвучки с видео ---
                in_item.Status = "Сборка видео";
                in_item.Progress = 0;
                var newFileName = $"{baseName} RusAudio.mp4";
                await speakVideo(subPath, videoPath, newFileName, speakList, instrumental);

                // --- 5. Удаляем временные файлы, готовое видео остаётся ---
                clearCache(videoPath);

                // --- 6. Тайминги в лог и в таблицу ---
                videoSw.Stop();
                double videoDurationSec = 0;
                try
                {
                    videoDurationSec = await getDurationAsync(videoPath, findFfprobe(TxtFfmpeg.Text.Trim()), CancellationToken.None);
                }
                catch { /* длительность не критична для лога */ }

                m_totalDubTime += videoSw.Elapsed;
                m_totalDubbedVideos++;

                in_item.VideoDuration = videoDurationSec > 0 ? formatTimeSpan(TimeSpan.FromSeconds(videoDurationSec)) : "—";
                in_item.DubTime = formatTimeSpan(videoSw.Elapsed);
                double k = videoDurationSec > 0 ? videoSw.Elapsed.TotalSeconds / videoDurationSec : 0;
                in_item.Ratio = k > 0 ? $"1 : {k:F2}" : "—";

                var logMess = $"🎬 [{in_videoNum}/{in_totalVideos}] Видео \"{baseName}\" озвучено за {videoSw.Elapsed} " +
                              $"(длительность видео {in_item.VideoDuration}, соотношение видео:озвучка = {in_item.Ratio}). " +
                              $"Суммарное время озвучки всех видео: {formatTimeSpan(m_totalDubTime)} ({m_totalDubbedVideos} шт.).";
                Logger.LogSuccess(logMess);
                setStatus($"✅ {logMess}");
                in_item.Status = "Готово";
                in_item.Progress = 100;
            }
            catch (OperationCanceledException)
            {
                in_item.Status = "Прервано";
                try { clearCache(videoPath); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                videoSw.Stop();
                in_item.Status = "Ошибка";
                in_item.Error = ex.Message;
                Logger.LogError($"Видео {baseName}: ошибка дубляжа через {videoSw.Elapsed}: {ex.Message}");
                setStatus($"❌ Видео {baseName}: ошибка: {ex.Message}");
                try { clearCache(videoPath); } catch { }
            }
        }

        /// <summary>Клонирование длинного текста по кускам (склеивание MP3 — как в остальном синтезе).</summary>
        private async Task cloneLongTextWithRetryAsync(
            string in_text, string in_refAudio, string in_refText, string in_outputPath,
            CancellationToken in_ct, int in_maxChunk = 350)
        {
            var chunks = splitTextIntoChunks(in_text, in_maxChunk);
            if (chunks.Count == 0) return;
            if (chunks.Count == 1)
            {
                await _api.CloneVoiceWithRetryAsync(
                    chunks[0], in_refAudio, in_refText, "mp3", in_outputPath, in_ct,
                    onRetry: m => setStatus($"⚠️ Клонирование голоса: {m}", 0, true));

                return;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), $"clone_chunks_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var files = new List<string>();
            try
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    in_ct.ThrowIfCancellationRequested();
                    var chunkPath = Path.Combine(tempDir, $"chunk_{i:D4}.mp3");
                    await _api.CloneVoiceWithRetryAsync(
                        chunks[i], in_refAudio, in_refText, "mp3", chunkPath, in_ct,
                        onRetry: m => setStatus($"⚠️ Клонирование голоса (кусок {i + 1}/{chunks.Count}): {m}", 0, true));
                    files.Add(chunkPath);
                }

                await concatMp3FilesAsync(files, in_outputPath, in_ct);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        #endregion
    }
}