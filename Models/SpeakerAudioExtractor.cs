using SubtitleTranslator.Models;
using SubtitleTranslator.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SubtitleTranslator.Models
{
    public static class SpeakerAudioExtractor
    {
        private static readonly Regex BracketRegex =
            new Regex(@"\[[^\]]*\]", RegexOptions.Compiled);

        private sealed class SpeechSegment
        {
            public int Speaker { get; set; }
            public double Start { get; set; }
            public double End { get; set; }
        }

        private sealed class AudioSegment
        {
            public double Start { get; set; }
            public double End { get; set; }
            public double Duration => End - Start;
        }

        /// <summary>
        /// Извлекает голос каждого спикера из видео по уже распарсенным субтитрам.
        /// </summary>
        public static async Task<List<string>> extractSpeakerAudioAsync(
            string in_videoPath,
            IReadOnlyList<SubtitleItem> in_subtitles,
            string in_ffmpegPath = "ffmpeg",
            double in_maxSecondsPerSpeaker = 13.0,
            double in_mergeGapSeconds = 0.35,
            int in_preferredSegmentCount = 2,
            int in_absoluteMaxSegmentCount = 3,
            double in_minUsefulDurationSeconds = 8.0,
            bool in_keepFullAudio = false)
        {
            if (string.IsNullOrWhiteSpace(in_videoPath))
                throw new ArgumentException("Не указан путь к видео.", nameof(in_videoPath));

            if (!File.Exists(in_videoPath))
                throw new FileNotFoundException("Видео не найдено.", in_videoPath);

            if (in_maxSecondsPerSpeaker <= 0)
                throw new ArgumentException("Длительность должна быть больше нуля.", nameof(in_maxSecondsPerSpeaker));

            if (in_subtitles == null || in_subtitles.Count == 0)
                return new List<string>();

            in_absoluteMaxSegmentCount = Math.Max(1, in_absoluteMaxSegmentCount);
            in_preferredSegmentCount = Math.Max(1, Math.Min(in_preferredSegmentCount, in_absoluteMaxSegmentCount));
            in_minUsefulDurationSeconds = Math.Max(0, Math.Min(in_minUsefulDurationSeconds, in_maxSecondsPerSpeaker));

            // Превращаем SubtitleItem в рабочие речевые сегменты.
            var speechSegments = new List<SpeechSegment>();

            foreach (var item in in_subtitles)
            {
                var segment = CreateSpeechSegment(item);
                if (segment != null)
                    speechSegments.Add(segment);
            }

            if (speechSegments.Count == 0)
                return new List<string>();

            var videoDir = Path.GetDirectoryName(Path.GetFullPath(in_videoPath)) ?? Directory.GetCurrentDirectory();
            var tempDir = Path.Combine(videoDir, "tempFiles");
            Directory.CreateDirectory(tempDir);

            var fullAudioPath = Path.Combine(
                tempDir,
                Path.GetFileNameWithoutExtension(in_videoPath) + "_full.mp3");

            // Сначала извлекаем всю аудиодорожку в mp3.
            await RunFfmpegAsync(in_ffmpegPath, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", in_videoPath,
                "-map", "0:a:0",
                "-vn",
                "-ac", "2",
                "-ar", "44100",
                "-codec:a", "libmp3lame",
                "-b:a", "192k",
                fullAudioPath
            });

            var resultFiles = new List<string>();

            foreach (var group in speechSegments.GroupBy(s => s.Speaker).OrderBy(g => g.Key))
            {
                var merged = MergeSegmentsForSpeaker(group.Key, speechSegments, in_mergeGapSeconds);

                var selected = SelectBestSegments(
                    merged,
                    in_maxSecondsPerSpeaker,
                    in_preferredSegmentCount,
                    in_absoluteMaxSegmentCount,
                    in_minUsefulDurationSeconds);

                if (selected.Count == 0)
                    continue;

                var outputPath = Path.Combine(tempDir, $"Speaker {group.Key}.mp3");

                if (File.Exists(outputPath))
                    File.Delete(outputPath);

                await ExportSegmentsAsync(
                    in_ffmpegPath,
                    fullAudioPath,
                    selected,
                    outputPath,
                    in_maxSecondsPerSpeaker);

                resultFiles.Add(outputPath);
            }

            if (!in_keepFullAudio)
                tryDeleteFile(fullAudioPath);

            return resultFiles;
        }

        /// <summary>
        /// Удобная перегрузка: принимает путь к JSON и вашу функцию парсинга.
        /// Пример: ExtractSpeakerAudioAsync(videoPath, jsonPath, tryDeserializeJsonSub)
        /// </summary>
        public static async Task<List<string>> ExtractSpeakerAudioAsync(
            string videoPath,
            string subtitlesJsonPath,
            Func<string, List<SubtitleItem>> parseSubtitles,
            string ffmpegPath = "ffmpeg",
            double maxSecondsPerSpeaker = 13.0,
            double mergeGapSeconds = 0.35,
            int preferredSegmentCount = 2,
            int absoluteMaxSegmentCount = 3,
            double minUsefulDurationSeconds = 8.0,
            bool keepFullAudio = false)
        {
            if (parseSubtitles == null)
                throw new ArgumentNullException(nameof(parseSubtitles));

            if (!File.Exists(subtitlesJsonPath))
                throw new FileNotFoundException("JSON не найден.", subtitlesJsonPath);

            var jsonText = await File.ReadAllTextAsync(subtitlesJsonPath);
            var subtitles = parseSubtitles(jsonText) ?? new List<SubtitleItem>();

            return await extractSpeakerAudioAsync(
                videoPath,
                subtitles,
                ffmpegPath,
                maxSecondsPerSpeaker,
                mergeGapSeconds,
                preferredSegmentCount,
                absoluteMaxSegmentCount,
                minUsefulDurationSeconds,
                keepFullAudio);
        }

        public static async Task<bool> createVoiceFile(List<SubtitleItem> in_subs)
        {
            var ret = false;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Выберите видео из которого нужно достать голоса", Filter = "Video Files|*.mp4;*.mkv;*.avi" };
                if (dlg.ShowDialog() == true)
                {
                    var mp4FilePath = dlg.FileName;
                    await extractSpeakerAudioAsync(mp4FilePath, in_subs);
                }
            }
            catch(Exception ex)
            {
                Logger.LogError($"Произошла ошибка во время создания голосов: {ex.Message}");
            }

            return ret;
        }

        private static SpeechSegment CreateSpeechSegment(SubtitleItem item)
        {
            if (item == null)
                return null;

            var (start, end) = getTimeRange(item);

            if (end <= start)
                return null;

            if (string.IsNullOrWhiteSpace(item.Content))
                return null;

            // Убираем служебные теги вида [Environmental Sounds], [Music], [Human Sounds].
            var cleanedContent = BracketRegex.Replace(item.Content, string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(cleanedContent))
                return null;

            return new SpeechSegment
            {
                Speaker = item.Speaker,
                Start = start,
                End = end
            };
        }

        private static (double start, double end) getTimeRange(SubtitleItem in_item)
        {
            // Основной вариант — использовать Start / End.
            double start = in_item.Start;
            double end = in_item.End;

            // Запасной вариант, если вдруг по какой-то причине Start/End пустые,
            // но заполнены StartTime / EndTime.
            if (end <= start && in_item.EndTime > in_item.StartTime)
            {
                start = in_item.StartTime.TotalSeconds;
                end = in_item.EndTime.TotalSeconds;
            }

            return (start, end);
        }

        private static List<AudioSegment> MergeSegmentsForSpeaker(
            int speakerId,
            IReadOnlyList<SpeechSegment> allSpeechSegments,
            double maxGapSeconds)
        {
            const double epsilon = 0.02;

            var speakerSegments = allSpeechSegments
                .Where(s => s.Speaker == speakerId)
                .OrderBy(s => s.Start)
                .ThenBy(s => s.End)
                .ToList();

            var otherSpeech = allSpeechSegments
                .Where(s => s.Speaker != speakerId)
                .ToList();

            var result = new List<AudioSegment>();

            foreach (var segment in speakerSegments)
            {
                if (segment.End <= segment.Start)
                    continue;

                if (result.Count == 0)
                {
                    result.Add(new AudioSegment
                    {
                        Start = segment.Start,
                        End = segment.End
                    });

                    continue;
                }

                var last = result[^1];

                bool canMergeByGap = segment.Start <= last.End + maxGapSeconds;

                // Не склеиваем через паузу, если внутри этой паузы говорит другой спикер.
                bool hasOtherSpeechInGap = HasOverlap(
                    otherSpeech,
                    last.End + epsilon,
                    segment.Start - epsilon);

                if (canMergeByGap && !hasOtherSpeechInGap)
                {
                    if (segment.End > last.End)
                        last.End = segment.End;
                }
                else
                {
                    result.Add(new AudioSegment
                    {
                        Start = segment.Start,
                        End = segment.End
                    });
                }
            }

            return result;
        }

        private static bool HasOverlap(
            IReadOnlyList<SpeechSegment> segments,
            double start,
            double end)
        {
            if (end <= start)
                return false;

            return segments.Any(s => s.Start < end && s.End > start);
        }

        private static List<AudioSegment> SelectBestSegments(
            IReadOnlyList<AudioSegment> mergedSegments,
            double maxDuration,
            int preferredCount,
            int absoluteMaxCount,
            double minUsefulDuration)
        {
            const double minCutSeconds = 0.25;
            const double epsilon = 0.01;

            // Сначала выбираем самые длинные куски.
            // Это даёт приоритет одному/двум большим фрагментам, а не множеству мелких.
            var candidates = mergedSegments
                .Where(s => s.Duration >= minCutSeconds)
                .OrderByDescending(s => Math.Min(s.Duration, maxDuration))
                .ThenBy(s => s.Start)
                .ToList();

            var selected = new List<AudioSegment>();
            double totalDuration = 0;
            int allowedSegments = Math.Max(1, Math.Min(preferredCount, absoluteMaxCount));

            foreach (var candidate in candidates)
            {
                if (selected.Count >= allowedSegments)
                {
                    // Если уже набрали достаточно полезной длительности, дальше не мельчим.
                    if (totalDuration >= minUsefulDuration || selected.Count >= absoluteMaxCount)
                        break;

                    // Если полезной длительности мало, разрешаем добавить ещё один фрагмент.
                    allowedSegments = Math.Min(absoluteMaxCount, selected.Count + 1);
                }

                double remaining = maxDuration - totalDuration;

                if (remaining < minCutSeconds)
                    break;

                double take = Math.Min(candidate.Duration, remaining);

                if (take < minCutSeconds)
                    continue;

                selected.Add(new AudioSegment
                {
                    Start = candidate.Start,
                    End = candidate.Start + take
                });

                totalDuration += take;

                if (totalDuration >= maxDuration - epsilon)
                    break;
            }

            // Для финальной склейки делаем хронологический порядок.
            return selected
                .OrderBy(s => s.Start)
                .ToList();
        }

        private static Task ExportSegmentsAsync(
            string ffmpegPath,
            string inputAudioPath,
            IReadOnlyList<AudioSegment> segments,
            string outputPath,
            double maxDuration)
        {
            var filter = BuildFilter(segments);

            return RunFfmpegAsync(ffmpegPath, new[]
            {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", inputAudioPath,
            "-filter_complex", filter,
            "-map", "[out]",
            "-ac", "2",
            "-ar", "44100",
            "-codec:a", "libmp3lame",
            "-b:a", "192k",
            "-t", FormatSeconds(maxDuration),
            outputPath
        });
        }

        private static string BuildFilter(IReadOnlyList<AudioSegment> segments)
        {
            if (segments.Count == 1)
            {
                var s = segments[0];

                return
                    $"[0:a]atrim=start={FormatSeconds(s.Start)}:end={FormatSeconds(s.End)}," +
                    "asetpts=PTS-STARTPTS[out]";
            }

            var sb = new StringBuilder();

            for (var i = 0; i < segments.Count; i++)
            {
                if (i > 0)
                    sb.Append(';');

                sb.Append(
                    $"[0:a]atrim=start={FormatSeconds(segments[i].Start)}:end={FormatSeconds(segments[i].End)}," +
                    $"asetpts=PTS-STARTPTS[a{i}]");
            }

            sb.Append(';');

            for (var i = 0; i < segments.Count; i++)
                sb.Append($"[a{i}]");

            sb.Append($"concat=n={segments.Count}:v=0:a=1[out]");

            return sb.ToString();
        }

        private static string FormatSeconds(double seconds)
        {
            return seconds.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static async Task RunFfmpegAsync(
            string ffmpegPath,
            IEnumerable<string> arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = new Process
            {
                StartInfo = psi
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Не удалось запустить FFmpeg. Проверьте путь '{ffmpegPath}' и установку FFmpeg.", ex);
            }

            var errorOutputTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var errorOutput = await errorOutputTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"FFmpeg завершился с кодом {process.ExitCode}. Ошибка: {errorOutput}");
            }
        }

        private static void tryDeleteFile(string in_path)
        {
            try
            {
                if (File.Exists(in_path))
                    File.Delete(in_path);
            }
            catch(Exception ex)
            {
                // Не критично, если временный файл не удалился.
                Logger.LogError($"Произошла ошибка во время удаления файла: {ex.Message}\nПуть к файлу: {in_path}");
            }
        }
    }
}