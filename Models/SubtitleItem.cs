using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.IO;

namespace SubtitleTranslator.Models
{
    public class SubtitleItem
    {
        [JsonPropertyName("Index")]
        public int Index { get; set; }

        [JsonPropertyName("StartTime")]
        public TimeSpan StartTime { get; set; }

        [JsonPropertyName("EndTime")]
        public TimeSpan EndTime { get; set; }

        [JsonPropertyName("Start")]
        public double Start { get; set; }

        [JsonPropertyName("End")]
        public double End { get; set; }

        [JsonPropertyName("Speaker")]
        public int Speaker { get; set; }

        [JsonPropertyName("Content")]
        public string Content { get; set; }

        [JsonPropertyName("TranslatedContent")]
        public string TranslatedContent { get; set; }

        [JsonPropertyName("DetectedLang")]
        public string DetectedLang { get; set; }
    }

    internal class SubtitleManager
    {
        static void test(string[] args)
        {
            string filePath = "path/to/your/subtitles.srt";
            var blocks = parseSrt(filePath);

            var cleanedBlocks = CleanAndMerge(blocks);

            foreach (var block in cleanedBlocks)
            {
                Console.WriteLine($"[{block.StartTime.TotalMilliseconds:F0}] - [{block.EndTime.TotalMilliseconds:F0}] -> {block.Content}");
            }
        }

        static List<SubtitleItem> parseSrt(string in_path)
        {
            var lines = File.ReadAllLines(in_path).ToList();
            return parseFromLines(lines);
        }

        private static string compliteTextes(string in_text1, string in_text2)
        {
            var ret = "";
            if (!string.IsNullOrWhiteSpace(in_text1) && !string.IsNullOrWhiteSpace(in_text2))
            {
                var minCount = Math.Min(in_text1.Length, in_text2.Length);
                var index = 0;
                var isContinue = true;
                var podStr = "";
                while (index < minCount && isContinue)
                {
                    index++;
                    if (in_text1.Substring(in_text1.Length - index) == in_text2.Substring(0, index))
                        isContinue = false;
                }

                ret = in_text2.Substring(index, in_text2.Length - index).Trim();
                if (isContinue || string.IsNullOrWhiteSpace(ret))
                    ret = in_text2;

                ret = SubtitleManager.obrabotka2(ret);
            }

            return ret;
        }

        private static string blocksToString(List<SubtitleItem> in_blocks)
        {
            var ret = "";
            var sb = new StringBuilder();
            foreach (var xBlock in in_blocks)
            {
                sb.AppendLine(xBlock.Index.ToString());
                sb.AppendLine($"{parseTimeToStr(xBlock.StartTime)} --> {parseTimeToStr(xBlock.EndTime)}");
                sb.AppendLine(xBlock.Content);
                sb.AppendLine();
            }

            ret = sb.ToString();

            return ret;
        }

        public static string editText(string in_text)
        {
            var ret = in_text;
            var lines = Regex.Split(ret, "[\r\n]").ToList();
            var bloks = parseFromLines(lines);
            var newBlocks = new List<SubtitleItem>();
            var index = 0;
            var isStarted = false;
            var isFinished = false;
            var subBlock = new SubtitleItem();
            foreach (var xBlock in bloks)
            {
                if (!isStarted)
                {
                    index++;
                    subBlock = new SubtitleItem() { Index = index, Start = xBlock.Start, End = xBlock.End, Content = xBlock.Content };
                    isStarted = true;
                }
                else
                {
                    if (xBlock.Content.StartsWith(subBlock.Content))
                    {
                        subBlock.Content = xBlock.Content;
                        subBlock.End = xBlock.End;
                    }
                    else if (subBlock.Content.EndsWith(xBlock.Content))
                        subBlock.End = xBlock.End;
                    else
                    {
                        var newText = compliteTextes(subBlock.Content, xBlock.Content);
                        if ((subBlock.Content.Length > 5 && subBlock.Content.EndsWith('.')) || subBlock.Content.Length > 120)
                        {
                            if (subBlock.Content.Length > 120 && subBlock.Content.Contains("."))
                            {
                                var lastPred = subBlock.Content.Split('.').Last();
                                if (lastPred.Length > 1 && (subBlock.Content.Length - lastPred.Length) > 5)
                                {
                                    newText = SubtitleManager.obrabotka2(lastPred + " " + newText);
                                    subBlock.Content = subBlock.Content.Substring(0, subBlock.Content.Length - lastPred.Length);
                                    subBlock.Content = SubtitleManager.obrabotka2(subBlock.Content);
                                }
                            }

                            newBlocks.Add(subBlock);
                            index++;
                            subBlock = new SubtitleItem() { Index = index, Start = xBlock.Start, End = xBlock.End, Content = newText };
                        }
                        else
                        {
                            subBlock.Content += (" " + newText);
                            subBlock.Content = SubtitleManager.obrabotka2(subBlock.Content);
                            subBlock.End = xBlock.End;
                        }
                    }
                }

                /*
                if ((subBlock.Text.Length > 20 && subBlock.Text.EndsWith('.')) || subBlock.Text.Length > 200)
                {
                    newBlocks.Add(subBlock);
                    isFinished = false;
                }

                isFinished = isFinished || newBlocks.Any();
                if (isFinished)
                {
                    newBlocks.Add(subBlock);
                    isFinished = false;
                }
                else
                {
                    
                }
                */
            }

            newBlocks.Add(subBlock);
            ret = blocksToString(newBlocks);

            return ret;
        }

        public static string[] getSplittedText(string in_text)
        {
            string[] ret = in_text.Split(".?!,".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            if (ret?.Any() == true)
            {
                var newSpis = new List<string>();
                var newElem = "";
                foreach (var xr in ret)
                {
                    var strXr = xr.Trim();
                    if (!string.IsNullOrWhiteSpace(strXr))
                    {
                        if (newElem.Length < 15)
                        {
                            if (string.IsNullOrWhiteSpace(newElem))
                                newElem = strXr;
                            else
                                newElem = $"{newElem}. {xr?.ToString().Trim()}";
                        }
                        else
                        {
                            newSpis.Add(newElem);
                            newElem = xr?.ToString().Trim();
                        }
                    }
                }

                newElem = newElem?.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(newElem))
                    newSpis.Add(newElem);

                ret = newSpis.ToArray();
            }

            return ret;
        }

        public static List<SubtitleItem> parseFromLines(List<string> in_lines)
        {
            var ret = new List<SubtitleItem>();
            var currentBlock = new SubtitleItem();
            for (int i = 0; i < in_lines.Count; i++)
            {
                var line = in_lines[i].Trim();

                if (int.TryParse(line, out _))
                {
                    // Новый блок
                    currentBlock.Content = SubtitleManager.obrabotka2(currentBlock.Content);
                    if (currentBlock.Content != null)
                    {
                        ret.Add(currentBlock);
                        currentBlock = new SubtitleItem();
                    }
                    currentBlock.Index = int.Parse(line);
                }
                else if (line.Contains(" --> "))
                {
                    var times = line.Split(new[] { " --> " }, StringSplitOptions.None);
                    currentBlock.StartTime = parseTime(times[0]);
                    currentBlock.EndTime = parseTime(times[1]);
                }
                else if (!string.IsNullOrEmpty(line))
                {
                    if (currentBlock.Content == null) currentBlock.Content = line.Trim();
                    else currentBlock.Content += " " + line;
                }
            }

            currentBlock.Content = SubtitleManager.obrabotka2(currentBlock.Content);
            if (!string.IsNullOrWhiteSpace(currentBlock.Content))
                ret.Add(currentBlock);

            return ret;
        }

        static TimeSpan parseTime(string in_str)
        {
            var parts = in_str.Split(new char[] { ':', ',' });
            int hours = int.Parse(parts[0]);
            int minutes = int.Parse(parts[1]);
            int seconds = int.Parse(parts[2]);
            int milliseconds = int.Parse(parts[3]);
            return new TimeSpan(0, hours, minutes, seconds, milliseconds);
        }

        static string parseTimeToStr(TimeSpan in_time)
        {
            var ret = $"{in_time.Hours.ToString().PadLeft(2, '0')}:";
            ret += $"{in_time.Minutes.ToString().PadLeft(2, '0')}:";
            ret += $"{in_time.Seconds.ToString().PadLeft(2, '0')},";
            ret += $"{in_time.Milliseconds.ToString().PadRight(3, '0')}";
            return ret;
        }

        static List<SubtitleItem> CleanAndMerge(List<SubtitleItem> blocks)
        {
            var result = new List<SubtitleItem>();
            var mergedText = new StringBuilder();
            TimeSpan start = TimeSpan.Zero;
            bool isMerging = false;

            for (int i = 0; i < blocks.Count; i++)
            {
                var current = blocks[i];
                var next = i + 1 < blocks.Count ? blocks[i + 1] : null;

                if (!isMerging)
                {
                    start = current.StartTime;
                    isMerging = true;
                }

                if (next != null && current.Content.Trim().EndsWith(next.Content.Trim()))
                {
                    // Пропустить дубль
                    continue;
                }

                mergedText.AppendLine(current.Content.Trim());

                if (next == null || !IsContinuation(current.Content, next.Content))
                {
                    result.Add(new SubtitleItem
                    {
                        StartTime = start,
                        End = current.End,
                        Content = mergedText.ToString().Trim()
                    });
                    mergedText.Clear();
                    isMerging = false;
                }
            }

            return result;
        }

        static bool IsContinuation(string in_current, string in_next)
        {
            if (string.IsNullOrWhiteSpace(in_current) || string.IsNullOrWhiteSpace(in_next)) return false;
            return in_current.EndsWith(char.IsLetterOrDigit(in_current[in_current.Length - 1]) ? "" : " ") ||
                   in_next.StartsWith(char.IsLetterOrDigit(in_next[0]) ? "" : "");
        }

        public static string obrabotka(string in_text)
        {
            string ret = in_text;
            ret = ret.Trim().Replace("и т. д.", "и так далее.");
            ret = ret.Trim().Replace("и т.д.", "и так далее.");
            ret = ret.Trim().Replace("ит.д.", "и так далее.");
            ret = ret.Trim().Replace("и т. п.", "и тому подобное.");
            ret = ret.Trim().Replace("и т.п.", "и тому подобное.");
            ret = ret.Trim().Replace("ит.п.", "и тому подобное.");

            ret = Regex.Replace(ret, @"%", " процентов ", RegexOptions.IgnoreCase);
            ret = Regex.Replace(ret, @"[\r\n]+", ". ", RegexOptions.IgnoreCase);
            ret = Regex.Replace(ret, @"[^А-яЁёЬьЙйA-z0-9,.!?\-:<>]+", " ", RegexOptions.IgnoreCase);
            ret = Regex.Replace(ret, @"[^\w\d<>]+([,.!?\-:])", "$1", RegexOptions.IgnoreCase);
            ret = Regex.Replace(ret, @"([,.!?\-:])", "$1 ", RegexOptions.IgnoreCase);
            ret = Regex.Replace(ret, @"\s+([,.!?\-:])", "$1", RegexOptions.IgnoreCase);
            ret = Regex.Replace(ret, @"\s+\-", "-", RegexOptions.IgnoreCase);
            ret = Regex.Replace(ret, @"\-\s+", "-", RegexOptions.IgnoreCase);
            ret = Regex.Replace(ret, @"\:", " ", RegexOptions.IgnoreCase);
            ret = Regex.Replace(ret, @"\s+", " ", RegexOptions.IgnoreCase);
            ret = ret.Trim(" ,.!?".ToCharArray());
            return ret;
        }

        public static string obrabotka2(string in_text)
        {
            string ret = in_text;
            if (!string.IsNullOrWhiteSpace(ret))
            {
                ret = ret.Trim().Replace("и т. д.", "и так далее.");
                ret = ret.Trim().Replace("и т.д.", "и так далее.");
                ret = ret.Trim().Replace("ит.д.", "и так далее.");
                ret = ret.Trim().Replace("и т. п.", "и тому подобное.");
                ret = ret.Trim().Replace("и т.п.", "и тому подобное.");
                ret = ret.Trim().Replace("ит.п.", "и тому подобное.");

                ret = Regex.Replace(ret, @"%", " процентов ", RegexOptions.IgnoreCase);
                ret = Regex.Replace(ret, @"[\r\n]+", ". ", RegexOptions.IgnoreCase);
                ret = Regex.Replace(ret, @"[^А-яЁёЬьЙйA-z0-9,.!?\-\—:<>]+", " ", RegexOptions.IgnoreCase);
                ret = Regex.Replace(ret, @"[^\w\d<>]+([,.!?\-:])", "$1", RegexOptions.IgnoreCase);
                ret = Regex.Replace(ret, @"([,.!?\-:])", "$1 ", RegexOptions.IgnoreCase);
                ret = Regex.Replace(ret, @"\s+([,.!?\-:])", "$1", RegexOptions.IgnoreCase);
                ret = Regex.Replace(ret, @"\s+\-", "-", RegexOptions.IgnoreCase);
                ret = Regex.Replace(ret, @"\-\s+", "-", RegexOptions.IgnoreCase);
                ret = Regex.Replace(ret, @"\s+", " ", RegexOptions.IgnoreCase);
                ret = ret.Trim();
            }

            return ret;
        }
    }
}