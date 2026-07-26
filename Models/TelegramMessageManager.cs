using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SubtitleTranslator.Models
{
    public class TelegramMessage
    {
        [JsonPropertyName("Id")]
        public int Id { get; set; }

        [JsonPropertyName("ParentId")]
        public int? ParentId { get; set; }

        [JsonPropertyName("ChildIds")]
        public List<int> ChildIds { get; set; } = new List<int>();

        /// <summary> Дата и время отправки сообщения </summary>
        [JsonPropertyName("Timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary> Имя отправителя (кто пишет) </summary>
        [JsonPropertyName("Sender")]
        public string Sender { get; set; }

        /// <summary> Имя человека, которому отвечают </summary>
        [JsonPropertyName("ReplyTo")]
        public string ReplyTo { get; set; }

        /// <summary> Текст цитаты (на что отвечают) </summary>
        [JsonPropertyName("QuotedText")]
        public string QuotedText { get; set; }

        /// <summary> Текст самого ответа </summary>
        [JsonPropertyName("MessageText")]
        public string MessageText { get; set; }

        public string getMessagesText(List<TelegramMessage> in_messages)
        {
            if (MessageText.StartsWith("> "))
                Console.WriteLine(" ");

            var ret = $"[{Id}. {Timestamp}] {Sender}: {MessageText}";
            if (!string.IsNullOrWhiteSpace(ReplyTo))
                ret = $"[{Id}^{ParentId}. {Timestamp}] {Sender}: {ReplyTo}, {MessageText}";

            if (ChildIds?.Any() == true)
            {
                var childs = in_messages.Where(xm => ChildIds.Contains(xm.Id)).Select(xm => xm.getMessagesText(in_messages));
                ret = $"{ret}\r\n{string.Join("\r\n", childs)}";
            }

            return ret;
        }
    }

    public class TelegramParser
    {
        private List<TelegramMessage> m_tgMessages = new List<TelegramMessage>();
        public TelegramParser(string in_input)
        {
            parseMessages(in_input);
            setLinkMessages();
        }

        private void parseMessages(string in_input)
        {
            m_tgMessages = new List<TelegramMessage>();
            if (!string.IsNullOrWhiteSpace(in_input))
            {
                // Разделяем текст на блоки по таймстампу в начале строки
                string[] blocks = Regex.Split(in_input, @"(?m)(?=^\[\d{2}\.\d{2}\.\d{4} \d{1,2}:\d{2}\])");
                var errors = new List<string>();
                var errorsBlock = new List<string>();
                var id = 0;
                foreach (string block in blocks)
                {
                    if (!string.IsNullOrWhiteSpace(block))
                    {
                        try
                        {
                            string pattern = @"^\[(\d{2}\.\d{2}\.\d{4} \d{1,2}:\d{1,2})\] (.*?) в ответ (.*?)\:[\r\n]+?\> (.*?)[\r\n]+(.*)$";
                            var match = Regex.Match(block, pattern, RegexOptions.Singleline);

                            if (!match.Success)
                            {
                                pattern = @"^\[(\d{2}\.\d{2}\.\d{4} \d{1,2}:\d{1,2})\] (.*?)\:(.*)$";
                                match = Regex.Match(block, pattern, RegexOptions.Singleline);
                            }

                            if (match.Success)
                            {
                                DateTime timestamp = DateTime.ParseExact(
                                    match.Groups[1].Value,
                                    "dd.MM.yyyy H:mm",
                                    CultureInfo.InvariantCulture
                                );

                                id++;
                                if (match.Groups.Count == 4)
                                    m_tgMessages.Add(new TelegramMessage
                                    {
                                        Id = id,
                                        Timestamp = timestamp,
                                        Sender = match.Groups[2].Value.Trim(),
                                        MessageText = cleanTelegramText(match.Groups[3].Value).Trim()
                                    });
                                else
                                    m_tgMessages.Add(new TelegramMessage
                                    {
                                        Id = id,
                                        Timestamp = timestamp,
                                        Sender = match.Groups[2].Value.Trim(),
                                        ReplyTo = match.Groups[3].Value.Trim(),
                                        QuotedText = cleanTelegramText(match.Groups[4].Value),
                                        MessageText = cleanTelegramText(match.Groups[5].Value).Trim()
                                    });
                            }
                            else
                                errorsBlock.Add(block);
                        }
                        catch (Exception ex)
                        {
                            errors.Add(ex.Message);
                        }
                    }
                }

                m_tgMessages = m_tgMessages.OrderBy(tx => tx.Timestamp).ToList();
            }
        }

        /// <summary> Назначает уникальные ID и выстраивает связи родитель-потомок между сообщениями. </summary>
        public void setLinkMessages()
        {
            // 1. Назначаем уникальные ID по порядку (начиная с 1)
            for (int i = 0; i < m_tgMessages.Count; i++)
            {
                m_tgMessages[i].Id = i + 1;
                m_tgMessages[i].ParentId = null;
                m_tgMessages[i].ChildIds = new List<int>();
            }

            // Создаем словарь для быстрого поиска по ID
            var byId = m_tgMessages.ToDictionary(m => m.Id);

            // 2. Выстраиваем связи
            foreach (var msg in m_tgMessages)
            {
                if (string.IsNullOrWhiteSpace(msg.ReplyTo)) continue;
                if (msg.Id == 52 || msg.Id == 53)
                    Console.WriteLine(" ");

                // Ищем всех потенциальных родителей: совпадение по имени отправителя и время ДО текущего сообщения
                var potentialParents = m_tgMessages
                    .Where(m => m.Sender.Equals(msg.ReplyTo, StringComparison.OrdinalIgnoreCase)
                            && m.Timestamp <= msg.Timestamp && m.Id < msg.Id)
                    .OrderByDescending(m => m.Id) // Сначала самые свежие
                    .ToList();

                if (!potentialParents.Any()) continue;

                // Если есть текст цитаты, попробуем найти точное совпадение текста (это самый надежный способ)
                if (!string.IsNullOrWhiteSpace(msg.QuotedText))
                {
                    var cleanQuoted = msg.QuotedText.Trim();
                    var exactMatch = potentialParents.FirstOrDefault(m =>
                        !string.IsNullOrWhiteSpace(m.MessageText)
                        && m.MessageText.Contains(cleanQuoted, StringComparison.OrdinalIgnoreCase));

                    if (exactMatch != null)
                    {
                        msg.ParentId = exactMatch.Id;
                        if (exactMatch.ParentId != msg.Id)
                            exactMatch.ChildIds.Add(msg.Id);
                        else
                            Console.WriteLine(" ");

                        continue; // Точное совпадение найдено, переходим к следующему сообщению
                    }
                }

                // Fallback: если точного совпадения по тексту нет, берем самое свежее сообщение от этого отправителя
                var bestFallback = potentialParents.First();
                msg.ParentId = bestFallback.Id;
                if (bestFallback.ParentId != msg.Id)
                    bestFallback.ChildIds.Add(msg.Id);
                else
                    Console.WriteLine(" ");
            }
        }

        /// <summary> Преобразует по моему список сообщений в упорядоченный текст, где ответы идут сразу за родительскими сообщениями с отступом. </summary>
        public string getMyFormatText()
        {
            var ret = "";
            var hs = new HashSet<int>();
            foreach (var msg in m_tgMessages)
                if (hs.Add(msg.Id))
                {
                    ret = $"{ret}\r\n{msg.getMessagesText(m_tgMessages)}";
                    if (msg.ChildIds?.Any() == true)
                        foreach (var xid in msg.ChildIds)
                            hs.Add(xid);
                }

            return ret;
        }

        public Dictionary<string, Tuple<int, int>> getPsevdonims()
        {
            var ret = new Dictionary<string, Tuple<int, int>>();
            foreach (var msg in m_tgMessages)
            {
                if (!string.IsNullOrWhiteSpace(msg.MessageText))
                {
                    var countSym = 0;
                    var countBlock = 0;
                    if (ret.ContainsKey(msg.Sender))
                    {
                        var tuple = ret[msg.Sender];
                        countSym = tuple.Item1;
                        countBlock = tuple.Item2;
                    }

                    countSym += msg.MessageText.Count();
                    countBlock += 1;
                    var newTuple = new Tuple<int, int>(countSym, countBlock);
                    ret[msg.Sender] = newTuple;
                }
            }

            return ret;
        }

        /// <summary> Преобразует список сообщений в упорядоченный текст, где ответы идут сразу за родительскими сообщениями с отступом. </summary>
        public string getFormatAsThreadedText()
        {
            var sb = new StringBuilder();

            // Находим все корневые сообщения (у которых нет родителя) и сортируем их по времени/ID
            var roots = m_tgMessages
                .Where(m => m.ParentId == null)
                .OrderBy(m => m.Timestamp)
                .ThenBy(m => m.Id)
                .ToList();

            var byId = m_tgMessages.ToDictionary(m => m.Id);

            // Рекурсивная функция для обхода дерева
            void Traverse(int currentId, int depth)
            {
                if (!byId.TryGetValue(currentId, out var msg)) return;

                string indent = new string(' ', depth * 2); // 2 пробела на каждый уровень вложенности
                string replyMark = !string.IsNullOrEmpty(msg.ReplyTo) ? $" (в ответ {msg.ReplyTo})" : "";

                sb.AppendLine($"{indent}[{msg.Timestamp:dd.MM.yyyy HH:mm}] {msg.Sender}{replyMark}:");

                if (!string.IsNullOrWhiteSpace(msg.QuotedText))
                {
                    // Форматируем цитату с отступом и символом >
                    string quotedIndented = msg.QuotedText.Replace("\n", $"\n{indent}> ");
                    sb.AppendLine($"{indent}> {quotedIndented}");
                }

                sb.AppendLine($"{indent}{msg.MessageText}");
                sb.AppendLine(); // Пустая строка между сообщениями

                // Сортируем детей по времени, чтобы сохранить хронологию внутри ветки
                var children = msg.ChildIds
                    .Select(id => byId[id])
                    .OrderBy(c => c.Timestamp)
                    .ThenBy(c => c.Id)
                    .ToList();

                foreach (var child in children)
                    Traverse(child.Id, depth + 1);
            }

            // Запускаем обход для каждого корневого сообщения
            foreach (var root in roots)
                Traverse(root.Id, 0);

            return sb.ToString().Trim();
        }

        /// <summary> Очищает текст от специфических невидимых символов Telegram и префиксов цитирования </summary>
        private static string cleanTelegramText(string in_text)
        {
            if (string.IsNullOrEmpty(in_text)) return string.Empty;

            string cleaned = Regex.Replace(in_text, @"[\u200E\u2068\u2069]", "");
            cleaned = Regex.Replace(cleaned, @"^> ", "", RegexOptions.Multiline);
            cleaned = cleaned.TrimEnd(".⁩".ToCharArray());
            cleaned = cleaned.TrimStart(" ⁨".ToCharArray());

            return cleaned.Trim();
        }
    }
}