using System.IO;
using Cs2LatencyDoctor.Core;
using Cs2LatencyDoctor.Core.Fixes;
using Cs2LatencyDoctor.Core.Windows;
using Microsoft.Win32;

namespace Cs2LatencyDoctor.GuiTests;

/// <summary>
/// Проверки слоя исправлений и журнала отката.
///
/// Главная из них — откат по данным журнала: если программа обновилась и
/// исправление переименовали, запись из старого журнала всё равно должна
/// вернуться, а не застрять в нём навсегда.
/// </summary>
internal static class CoreTests
{
    private const string TestKeyPath = @"Software\Cs2LatencyDoctor\SelfTest";
    private const string TestValueName = "Probe";

    /// <summary>
    /// Куда писать временные файлы тестов.
    ///
    /// Берём папку рядом с программой, а не системную временную: в ограниченных
    /// средах (защищённые профили, песочницы) запись в TEMP запрещена, и тест
    /// падал бы не из-за ошибки в коде, а из-за прав.
    /// </summary>
    private static string WorkDirectory
    {
        get
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "test-work");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    /// <summary>Можно ли в этой среде писать в реестр. Если нет — реестровые проверки пропускаем.</summary>
    private static bool? _registryWritable;

    private static bool RegistryWritable()
    {
        if (_registryWritable.HasValue) return _registryWritable.Value;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = baseKey.CreateSubKey(TestKeyPath, writable: true);
            if (key is null)
            {
                _registryWritable = false;
                return false;
            }

            key.SetValue(TestValueName, "probe");
            var ok = key.GetValue(TestValueName)?.ToString() == "probe";
            baseKey.DeleteSubKeyTree(TestKeyPath, throwOnMissingSubKey: false);

            _registryWritable = ok;
            return ok;
        }
        catch
        {
            _registryWritable = false;
            return false;
        }
    }

    public static void Run(List<(string Name, bool Passed, string? Error)> results)
    {
        var registryAvailable = RegistryWritable();

        if (registryAvailable)
        {
            RunTest(results, "Откат значения реестра по данным журнала (значение было)", () =>
            {
                // Готовим «изменённое» состояние и запись о том, что было раньше.
                RegistryValueReader.WriteValue(RegistryHive.CurrentUser, TestKeyPath, TestValueName, "999");

                var entry = new JournalEntry
                {
                    FixId = "unknown.fix.from.old.version",
                    Title = "Тестовый параметр",
                    Kind = "registryValue",
                    Location = TestKeyPath,
                    Hive = "CurrentUser",
                    Name = TestValueName,
                    OldValue = "123",
                    NewValue = "999"
                };

                var ok = FixRunner.RevertFromJournalData(entry);
                var actual = RegistryValueReader.ReadRawString(RegistryHive.CurrentUser, TestKeyPath, TestValueName);

                Cleanup();

                if (!ok) throw new InvalidOperationException("Откат вернул false");
                if (actual != "123") throw new InvalidOperationException($"Ожидалось 123, получено «{actual}»");
            });

            RunTest(results, "Откат удаляет параметр, которого до правки не было", () =>
            {
                // Пустое старое значение означает: параметра не существовало,
                // значит его надо удалить, а не затирать пустой строкой.
                RegistryValueReader.WriteValue(RegistryHive.CurrentUser, TestKeyPath, TestValueName, "999");

                var entry = new JournalEntry
                {
                    FixId = "unknown.fix",
                    Title = "Тестовый параметр",
                    Kind = "registryValue",
                    Location = TestKeyPath,
                    Hive = "CurrentUser",
                    Name = TestValueName,
                    OldValue = string.Empty,
                    NewValue = "999"
                };

                var ok = FixRunner.RevertFromJournalData(entry);
                var actual = RegistryValueReader.ReadRawString(RegistryHive.CurrentUser, TestKeyPath, TestValueName);

                Cleanup();

                if (!ok) throw new InvalidOperationException("Откат вернул false");
                if (actual is not null) throw new InvalidOperationException($"Параметр не удалён, значение «{actual}»");
            });

            RunTest(results, "Запись создаёт отсутствующий ключ реестра", () =>
            {
                // Такого ключа заведомо нет. Раньше запись молча возвращала false,
                // и откат не смог бы вернуть настройку, если ключ удалили.
                Cleanup();

                var ok = RegistryValueReader.WriteValue(
                    RegistryHive.CurrentUser, TestKeyPath, TestValueName, "777");
                var actual = RegistryValueReader.ReadRawString(
                    RegistryHive.CurrentUser, TestKeyPath, TestValueName);

                Cleanup();

                if (!ok) throw new InvalidOperationException("Запись в отсутствующий ключ не удалась");
                if (actual != "777") throw new InvalidOperationException($"Прочитано «{actual}», ожидалось 777");
            });

            RunTest(results, "Журнал: откат очищает только возвращённые записи", () =>
            {
                var path = Path.Combine(WorkDirectory, "journal-revert.json");
                if (File.Exists(path)) File.Delete(path);

                var journal = new UndoJournal(path);
                journal.Add(new JournalEntry
                {
                    FixId = "test.fix",
                    Title = "Возвращаемая запись",
                    Kind = "registryValue",
                    Location = TestKeyPath,
                    Hive = "CurrentUser",
                    Name = TestValueName,
                    OldValue = "5",
                    NewValue = "9"
                });

                RegistryValueReader.WriteValue(RegistryHive.CurrentUser, TestKeyPath, TestValueName, "9");

                var report = FixRunner.RevertAll(
                    new DiagnosticContext { IsAdministrator = true }, journal);

                var remaining = new UndoJournal(path).Entries.Count;
                var actual = RegistryValueReader.ReadRawString(RegistryHive.CurrentUser, TestKeyPath, TestValueName);

                Cleanup();
                try { File.Delete(path); } catch { /* не критично */ }

                if (report.FailedCount > 0)
                    throw new InvalidOperationException("Откат сообщил об ошибке: " +
                        string.Join("; ", report.Results.Select(r => r.Message)));

                if (actual != "5") throw new InvalidOperationException($"Значение «{actual}», ожидалось 5");
                if (remaining != 0) throw new InvalidOperationException($"В журнале осталось записей: {remaining}");
            });
        }
        else
        {
            results.Add(("Откат значения реестра по данным журнала", false, null));
            results.Add(("Откат удаляет параметр, которого не было", false, null));
            results.Add(("Запись создаёт отсутствующий ключ", false, null));
            results.Add(("Откат очищает только возвращённые записи", false, null));
        }

        RunTest(results, "Неизвестный вид записи не считается откаченным", () =>
        {
            // Такого вида записей программа не создаёт. Откат обязан честно
            // сказать «не могу», а не соврать, что всё вернул.
            var entry = new JournalEntry
            {
                FixId = "unknown",
                Title = "Непонятная запись",
                Kind = "somethingElse",
                Location = "nowhere",
                Name = "nothing",
                OldValue = "1",
                NewValue = "0"
            };

            if (FixRunner.RevertFromJournalData(entry))
                throw new InvalidOperationException("Откат сообщил об успехе для неизвестного вида записи");
        });

        RunTest(results, "Откат настроек CS2 из резервной копии", () =>
        {
            var target = Path.Combine(WorkDirectory, "cs2_video.txt");
            var backup = target + ".cs2latencydoc-backup";

            // «Испорченный» файл и копия того, что было до правки.
            File.WriteAllText(target, "\"setting.fullscreen\"\t\t\"1\"\n");
            File.WriteAllText(backup, "\"setting.fullscreen\"\t\t\"0\"\n");

            var entry = new JournalEntry
            {
                FixId = "cs2.display.exclusive",
                Title = "Режим экрана CS2",
                Kind = "cs2VideoFile",
                Location = target,
                Name = "fullscreen+nowindowborder",
                OldValue = "fullscreen=0;nowindowborder=1",
                NewValue = "fullscreen=1;nowindowborder=0"
            };

            var ok = FixRunner.RevertFromJournalData(entry);
            var restored = File.ReadAllText(target);

            try { File.Delete(target); File.Delete(backup); } catch { /* не критично */ }

            if (!ok) throw new InvalidOperationException("Откат вернул false");
            if (!restored.Contains("\"0\""))
                throw new InvalidOperationException("Файл не восстановлен из копии: " + restored.Trim());
        });

        RunTest(results, "Откат без резервной копии не врёт об успехе", () =>
        {
            var target = Path.Combine(WorkDirectory, "cs2_video-without-backup.txt");
            File.WriteAllText(target, "test");

            var entry = new JournalEntry
            {
                FixId = "cs2.display.exclusive",
                Title = "Режим экрана CS2",
                Kind = "cs2VideoFile",
                Location = target,
                Name = "fullscreen+nowindowborder",
                OldValue = "fullscreen=0;nowindowborder=1",
                NewValue = "fullscreen=1;nowindowborder=0"
            };

            var ok = FixRunner.RevertFromJournalData(entry);
            try { File.Delete(target); } catch { /* не критично */ }

            if (ok) throw new InvalidOperationException("Откат сообщил об успехе без резервной копии");
        });

        RunTest(results, "Журнал сохраняется и читается обратно", () =>
        {
            var path = Path.Combine(WorkDirectory, "journal-roundtrip.json");
            if (File.Exists(path)) File.Delete(path);

            var journal = new UndoJournal(path);
            journal.Add(new JournalEntry
            {
                FixId = "test.fix",
                Title = "Тестовая запись",
                Kind = "registryValue",
                Location = TestKeyPath,
                Hive = "CurrentUser",
                Name = TestValueName,
                OldValue = "1",
                NewValue = "0"
            });

            var reloaded = new UndoJournal(path);
            var count = reloaded.Entries.Count;
            var firstTitle = count > 0 ? reloaded.Entries[0].Title : string.Empty;
            var firstHive = count > 0 ? reloaded.Entries[0].Hive : string.Empty;

            try { File.Delete(path); } catch { /* не критично */ }

            if (count != 1) throw new InvalidOperationException($"Прочитано записей: {count}, ожидалась 1");
            if (firstTitle != "Тестовая запись")
                throw new InvalidOperationException($"Заголовок записи «{firstTitle}»");
            if (firstHive != "CurrentUser")
                throw new InvalidOperationException($"Ветка реестра не сохранилась: «{firstHive}»");
        });

        RunTest(results, "Пустой журнал: откат не падает и объясняет", () =>
        {
            var path = Path.Combine(WorkDirectory, "journal-empty.json");
            if (File.Exists(path)) File.Delete(path);

            var journal = new UndoJournal(path);
            var report = FixRunner.RevertAll(new DiagnosticContext { IsAdministrator = true }, journal);

            try { File.Delete(path); } catch { /* не критично */ }

            if (report.Results.Count == 0)
                throw new InvalidOperationException("Откат не дал ни одного результата");
            if (!report.Results[0].Message.Contains("ничего не меняла", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Нет понятного сообщения: " + report.Results[0].Message);
        });

        RunTest(results, "Диагностика на этой машине выполняется и заполнена", () =>
        {
            var context = new DiagnosticContext { IsAdministrator = true, ProbeSeconds = 5 };
            var report = DiagnosticRunner.CreateDefault(5).RunAsync(context).GetAwaiter().GetResult();

            if (report.Results.Count < 5)
                throw new InvalidOperationException($"Проверок выполнено: {report.Results.Count}");

            foreach (var result in report.Results)
            {
                if (string.IsNullOrWhiteSpace(result.Title))
                    throw new InvalidOperationException($"У проверки {result.Id} нет заголовка");
                if (string.IsNullOrWhiteSpace(result.Detail))
                    throw new InvalidOperationException($"У проверки «{result.Title}» нет описания");
            }
        });

        if (!registryAvailable)
        {
            results.Add(("Проверки реестра", true, null));
        }
    }

    private static void RunTest(List<(string Name, bool Passed, string? Error)> results, string name, Action body)
    {
        try
        {
            body();
            results.Add((name, true, null));
        }
        catch (Exception ex)
        {
            results.Add((name, false, ex.Message));
        }
    }

    /// <summary>Убрать следы теста из реестра.</summary>
    private static void Cleanup()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            baseKey.DeleteSubKeyTree(TestKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // не критично
        }
    }
}
