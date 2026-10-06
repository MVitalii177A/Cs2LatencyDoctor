using System.IO;
using Cs2LatencyDoctor.Core;
using Cs2LatencyDoctor.Core.Fixes;
using Cs2LatencyDoctor.Core.Windows;
using Cs2LatencyDoctor.Gui;
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

        RunTest(results, "Ни одна проверка не молчит: «не могу» всегда объяснено", () =>
        {
            // Правило проекта: если программа не может помочь или не может измерить —
            // она обязана сказать, почему и что делать человеку. Молчаливого «пропущено» быть не должно.
            var context = new DiagnosticContext { IsAdministrator = false, ProbeSeconds = 5 };
            var report = DiagnosticRunner.CreateDefault(5).RunAsync(context).GetAwaiter().GetResult();

            var silent = report.Results
                .Where(r => r.Severity == Severity.Skipped && !r.HasRecommendation)
                .ToList();

            if (silent.Count > 0)
                throw new InvalidOperationException(
                    "Проверки без пояснения, что делать: " +
                    string.Join(" | ", silent.Select(r => $"{r.Id} («{r.Title}»): {r.Detail}")));

            var skippedWithoutReason = report.Results
                .Where(r => r.Severity == Severity.Skipped && r.NoHelpReason == NoHelpReason.None)
                .ToList();

            if (skippedWithoutReason.Count > 0)
                throw new InvalidOperationException(
                    "Пропущенные проверки без указания причины: " +
                    string.Join(" | ", skippedWithoutReason.Select(r => $"{r.Id} («{r.Title}»)")));
        });

        RunTest(results, "Проблемы и предупреждения объясняют, что делать", () =>
        {
            var context = new DiagnosticContext { IsAdministrator = false, ProbeSeconds = 5 };
            var report = DiagnosticRunner.CreateDefault(5).RunAsync(context).GetAwaiter().GetResult();

            var unexplained = report.Results
                .Where(r => r.Severity is Severity.Problem or Severity.Warning)
                .Where(r => !r.HasRecommendation && !r.CanFixItself)
                .ToList();

            if (unexplained.Count > 0)
                throw new InvalidOperationException(
                    "Найдены проблемы без совета и без автоисправления: " +
                    string.Join("; ", unexplained.Select(r => r.Title)));
        });

        RunTest(results, "Проверка без прав объясняет, что нужен администратор", () =>
        {
            // Отдельный случай: часть проверок требует прав. Человек должен понимать,
            // что дело в правах, а не в том, что программа сломалась.
            var check = new RequiresAdminTestCheck();
            var resultsList = check.RunAsync(
                new DiagnosticContext { IsAdministrator = false, ProbeSeconds = 3 },
                CancellationToken.None).GetAwaiter().GetResult();

            var runner = new DiagnosticRunner().Add(check);
            var report = runner.RunAsync(
                new DiagnosticContext { IsAdministrator = false, ProbeSeconds = 3 },
                CancellationToken.None).GetAwaiter().GetResult();

            var skipped = report.Results.FirstOrDefault(r => r.Severity == Severity.Skipped);

            if (skipped is null)
                throw new InvalidOperationException("Проверка без прав не помечена как пропущенная");
            if (!skipped.HasRecommendation)
                throw new InvalidOperationException("Нет пояснения, что делать без прав администратора");
            if (skipped.NoHelpReason != NoHelpReason.NeedsAdmin)
                throw new InvalidOperationException($"Причина «{skipped.NoHelpReason}», ожидалась NeedsAdmin");
            if (resultsList.Count == 0)
                throw new InvalidOperationException("Проверка не вернула результатов");
        });

        RunTest(results, "Реквизиты для доната заполнены правильно", () =>
        {
            // Проверяем то, что проверяется надёжно: картинка QR на месте,
            // адрес вписан и его формат соответствует указанной сети.
            //
            // Сам QR автоматически распознать не удалось: картинки от кошельков идут
            // с рамкой, скруглениями и логотипом в центре, для них нужен полноценный
            // детектор. Поэтому QR проверяется один раз глазами: навести телефон
            // и сверить адрес с тем, что вписан в коде.
            foreach (var option in DonationInfo.Options)
            {
                var imagePath = option.QrFullPath;

                if (!File.Exists(imagePath))
                    throw new InvalidOperationException(
                        $"Нет картинки QR для «{option.Title}»: ожидается {imagePath}");

                if (!option.HasAddress)
                    throw new InvalidOperationException(
                        $"У «{option.Title}» есть картинка QR, но не вписан адрес — " +
                        "кнопка копирования будет отключена");

                var address = option.Address;
                var network = option.Network;

                // Ethereum и BSC: 0x и 40 шестнадцатеричных знаков
                var looksLikeEvm = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                   && address.Length == 42
                                   && address.Skip(2).All(Uri.IsHexDigit);

                // Tron: начинается с T, длина 34, только base58
                const string base58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
                var looksLikeTron = address.StartsWith('T')
                                    && address.Length == 34
                                    && address.All(base58.Contains);

                var expectsEvm = network.Contains("ERC", StringComparison.OrdinalIgnoreCase)
                                 || network.Contains("BEP", StringComparison.OrdinalIgnoreCase)
                                 || network.Contains("Ethereum", StringComparison.OrdinalIgnoreCase)
                                 || network.Contains("BSC", StringComparison.OrdinalIgnoreCase);

                var expectsTron = network.Contains("TRC", StringComparison.OrdinalIgnoreCase)
                                  || network.Contains("Tron", StringComparison.OrdinalIgnoreCase);

                if (expectsEvm && !looksLikeEvm)
                    throw new InvalidOperationException(
                        $"Адрес «{option.Title}» не похож на адрес сети {network}: {address}. " +
                        "Для ERC-20 и BEP-20 адрес начинается с 0x и содержит 40 hex-знаков.");

                if (expectsTron && !looksLikeTron)
                    throw new InvalidOperationException(
                        $"Адрес «{option.Title}» не похож на адрес сети {network}: {address}. " +
                        "Для TRC-20 адрес начинается с T и содержит 34 знака.");

                if (!looksLikeEvm && !looksLikeTron)
                    throw new InvalidOperationException(
                        $"Адрес «{option.Title}» не похож ни на ERC-20, ни на TRC-20: {address}");
            }
        });

        RunTest(results, "QR-код пригоден для сканирования", () =>
        {
            // Файл открывается как картинка, размеры достаточные, форма квадратная.
            // Кривой размер или растянутая картинка — частая причина,
            // по которой телефон не может прочитать код.
            foreach (var option in DonationInfo.Options.Where(o => o.QrExists))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(option.QrFullPath);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                if (bitmap.PixelWidth < 150 || bitmap.PixelHeight < 150)
                    throw new InvalidOperationException(
                        $"QR «{option.Title}» слишком мелкий: {bitmap.PixelWidth}x{bitmap.PixelHeight}. " +
                        "Для надёжного сканирования нужно хотя бы 150x150.");

                // Допускаем небольшое расхождение: при обрезке рамки стороны
                // могут отличаться на пару пикселей, это не мешает сканеру.
                var difference = Math.Abs(bitmap.PixelWidth - bitmap.PixelHeight);
                if (difference > bitmap.PixelWidth * 0.03)
                    throw new InvalidOperationException(
                        $"QR «{option.Title}» заметно не квадратный: " +
                        $"{bitmap.PixelWidth}x{bitmap.PixelHeight}. Скорее всего осталась рамка " +
                        "или лишний край — сканер может не прочитать такой код.");
            }
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

/// <summary>Проверка, которой нужны права администратора — для проверки поведения без прав.</summary>
internal sealed class RequiresAdminTestCheck : IDiagnosticCheck
{
    public string Id => "test.requires-admin";
    public string Title => "Проверка, требующая прав";
    public bool RequiresAdmin => true;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CheckResult>>(new[]
        {
            CheckResult.Ok(Id, Title, "выполнено с правами")
        });
}
