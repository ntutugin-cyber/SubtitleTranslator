using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using NAudio.Wave;

namespace SubtitleTranslator.Services
{
    public class AudioProcessor
    {
        // Лимит: 1 час 40 минут
        public static TimeSpan m_maxDuration = new TimeSpan(1, 40, 0);

        /// <summary>
        /// Проверяет длительность первого файла и, если она <= 1ч 40м, 
        /// склеивает его со вторым и удаляет второй файл.
        /// </summary>
        public static void processAudioFiles(string in_file1Path, string in_file2Path)
        {
            if (!File.Exists(in_file1Path) || !File.Exists(in_file2Path))
            {
                Console.WriteLine("Ошибка: один или оба файла не найдены.");
                return;
            }

            // 1. Проверяем продолжительность первого файла
            TimeSpan duration1 = getAudioDuration(in_file1Path);
            Console.WriteLine($"Длительность {Path.GetFileName(in_file1Path)}: {duration1:hh\\:mm\\:ss}");

            if (duration1 <= m_maxDuration)
            {
                Console.WriteLine("Условие выполнено (<= 1:40:00). Начинаем склеивание...");

                // Создаем временный файл, чтобы избежать потери данных при сбое
                string tempPath = Path.Combine(Path.GetDirectoryName(in_file1Path), "temp_merged.mp3");

                try
                {
                    // 2. Склеиваем файлы
                    MergeMp3Files(in_file1Path, in_file2Path, tempPath);

                    // Заменяем оригинальный первый файл склеенным
                    File.Delete(in_file1Path);
                    File.Move(tempPath, in_file1Path);

                    // 3. Удаляем второй файл
                    File.Delete(in_file2Path);

                    Console.WriteLine("Файлы успешно обработаны: второй файл удален, первый обновлен.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при обработке файлов: {ex.Message}");
                    // Если что-то пошло не так, удаляем временный файл
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
            }
            else
            {
                Console.WriteLine("Длительность больше 1:40:00. Оставляем всё как есть.");
            }
        }

        /// <summary>
        /// Получает точную длительность аудиофайла с помощью NAudio
        /// </summary>
        public static TimeSpan getAudioDuration(string in_filePath)
        {
            using (var reader = new AudioFileReader(in_filePath))
            {
                return reader.TotalTime;
            }
        }

        /// <summary>
        /// Склеивает два MP3 файла на уровне байтов, корректно обрабатывая ID3 теги
        /// </summary>
        private static void MergeMp3Files(string file1, string file2, string outputPath)
        {
            byte[] b1 = File.ReadAllBytes(file1);
            byte[] b2 = File.ReadAllBytes(file2);

            // 1. Обрезаем ID3v1 тег в конце первого файла (если есть). 
            // Он занимает ровно 128 байт и начинается с "TAG".
            // Если его не убрать, плеер может подумать, что аудио закончилось.
            int end1 = b1.Length;
            if (end1 >= 128 && b1[end1 - 128] == 'T' && b1[end1 - 127] == 'A' && b1[end1 - 126] == 'G')
            {
                end1 -= 128;
            }

            // 2. Пропускаем ID3v2 тег в начале второго файла (если есть).
            // Он начинается с "ID3". Если его не пропустить, на стыке будет слышен щелчок/артефакт.
            int start2 = 0;
            if (b2.Length >= 10 && b2[0] == 'I' && b2[1] == 'D' && b2[2] == '3')
            {
                // Размер тега хранится в байтах 6-9 в формате synchsafe integer
                int size = (b2[6] << 21) | (b2[7] << 14) | (b2[8] << 7) | b2[9];
                start2 = size + 10;
            }

            // 3. Записываем результат
            using (var fs = File.Create(outputPath))
            {
                // Пишем первый файл (без его хвостового тега)
                fs.Write(b1, 0, end1);

                // Пишем второй файл (без его головного тега)
                if (start2 < b2.Length)
                {
                    fs.Write(b2, start2, b2.Length - start2);
                }
            }
        }

        /// <summary>
        /// Если в папке есть другие MP3-файлы и текущий файл превышает лимит длительности,
        /// создаёт папку с именем аудиофайла (без нумерации) и переносит туда все MP3-файлы.
        /// </summary>
        public static void OrganizeFilesIfLimitExceeded(string in_currentFilePath)
        {
            if (!File.Exists(in_currentFilePath))
            {
                Console.WriteLine("Ошибка: файл не найден.");
                return;
            }

            string directory = Path.GetDirectoryName(in_currentFilePath);
            string currentFileName = Path.GetFileNameWithoutExtension(in_currentFilePath);

            // 1. Получаем все MP3 файлы в папке
            string[] allMp3Files = Directory.GetFiles(directory, "*.mp3");

            // 2. Проверяем, есть ли другие файлы кроме текущего
            if (allMp3Files.Length <= 1)
            {
                Console.WriteLine("В папке нет других MP3-файлов для организации.");
                return;
            }

            // 3. Проверяем длительность текущего файла
            TimeSpan currentDuration = getAudioDuration(in_currentFilePath);
            Console.WriteLine($"Текущая длительность {Path.GetFileName(in_currentFilePath)}: {currentDuration:hh\\:mm\\:ss}");

            if (currentDuration <= m_maxDuration)
            {
                Console.WriteLine("Длительность не превышает лимит. Организация не требуется.");
                return;
            }

            Console.WriteLine("Длительность превышает лимит. Создаём папку для группировки файлов...");

            // 4. Извлекаем базовое имя без нумерации (например, "001_speechText" -> "speechText")
            string baseName = ExtractBaseName(currentFileName);
            string newFolderPath = Path.Combine(directory, baseName);

            // 5. Создаём папку, если она ещё не существует
            if (!Directory.Exists(newFolderPath))
            {
                Directory.CreateDirectory(newFolderPath);
                Console.WriteLine($"Создана папка: {newFolderPath}");
            }
            else
            {
                Console.WriteLine($"Папка уже существует: {newFolderPath}");
            }

            // 6. Переносим все MP3-файлы в новую папку
            int movedCount = 0;
            foreach (string file in allMp3Files)
            {
                string fileName = Path.GetFileName(file);
                string destPath = Path.Combine(newFolderPath, fileName);

                try
                {
                    // Если файл с таким именем уже есть в папке назначения — пропускаем или перезаписываем
                    if (File.Exists(destPath))
                    {
                        Console.WriteLine($"Файл {fileName} уже существует в папке назначения. Пропускаем.");
                        continue;
                    }

                    File.Move(file, destPath);
                    movedCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при переносе {fileName}: {ex.Message}");
                }
            }

            Console.WriteLine($"Готово! Перенесено файлов: {movedCount} в папку '{baseName}'.");
        }

        /// <summary>
        /// Извлекает базовое имя файла без префикса-нумерации.
        /// Например: "001_speechText" -> "speechText", "02_chapter1" -> "chapter1"
        /// Если подчёркивания нет, возвращает имя как есть.
        /// </summary>
        private static string ExtractBaseName(string in_fileName)
        {
            int underscoreIndex = in_fileName.IndexOf('_');
            if (underscoreIndex >= 0 && underscoreIndex < in_fileName.Length - 1)
            {
                return in_fileName.Substring(underscoreIndex + 1);
            }
            return in_fileName;
        }
    }
}