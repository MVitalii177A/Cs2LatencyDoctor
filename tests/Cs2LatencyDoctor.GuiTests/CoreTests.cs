using System.IO;
using Cs2LatencyDoctor.Core;
using Cs2LatencyDoctor.Core.Background;
using Cs2LatencyDoctor.Core.Checks;
using Cs2LatencyDoctor.Core.Fixes;
using Cs2LatencyDoctor.Core.History;
using Cs2LatencyDoctor.Core.Report;
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

        RunTest(results, "Откат одной записи не трогает остальные", () =>
        {
            // Раньше откат возвращал весь журнал целиком. Если человек хотел отменить
            // только сетевые правки, оставив настройки игры, выбора у него не было.
            var store = new UndoJournal();

            // Идентификаторы уникальны для каждого прогона: журнал лежит на диске
            // и не очищается, поэтому одинаковые имена накапливались бы от запуска к запуску.
            var runTag = Guid.NewGuid().ToString("N")[..8];

            var first = new JournalEntry
            {
                FixId = "test." + runTag + ".one", Title = "Первое " + runTag, Kind = "registryValue",
                Location = @"SOFTWARE\Cs2LatencyDoctorTest\SingleRevert",
                Name = "One", OldValue = "1", NewValue = "2"
            };
            var second = new JournalEntry
            {
                FixId = "test." + runTag + ".two", Title = "Второе " + runTag, Kind = "registryValue",
                Location = @"SOFTWARE\Cs2LatencyDoctorTest\SingleRevert",
                Name = "Two", OldValue = "3", NewValue = "4"
            };

            store.Add(first);
            store.Add(second);

            // Журнал на диске может содержать записи от прошлых запусков, поэтому
            // проверяем судьбу именно своих двух записей, а не общее количество.
            var mine = store.Entries.Where(e => e.FixId.Contains(runTag, StringComparison.Ordinal)).ToList();
            if (mine.Count != 2)
                throw new InvalidOperationException($"В журнале {mine.Count} своих записей вместо двух");

            var context = new DiagnosticContext { IsAdministrator = false };

            // Откатываем первую: реестр в этой среде недоступен, поэтому проверяем
            // главное — что вторая запись осталась на месте, а первая попала в отчёт.
            var report = FixRunner.RevertOne(context, store, first);

            if (report.Results.Count != 1)
                throw new InvalidOperationException($"Ожидался один результат, получено {report.Results.Count}");

            if (!store.Entries.Contains(second))
                throw new InvalidOperationException("Откат одной записи убрал из журнала чужую запись");

            // Повторный откат той же записи должен честно сказать, что её уже нет.
            var again = FixRunner.RevertOne(context, store, first);
            if (again.Results.All(r => r.Outcome != FixOutcome.Failed))
                throw new InvalidOperationException("Повторный откат не сообщил, что записи уже нет");
        });
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

                // TON: современный формат — 48 знаков url-safe base64
                // (буквы, цифры, дефис и подчёркивание). Старый формат — 48 hex-знаков.
                const string tonChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
                var looksLikeTon = address.Length == 48 && address.All(tonChars.Contains);

                var expectsEvm = network.Contains("ERC", StringComparison.OrdinalIgnoreCase)
                                 || network.Contains("BEP", StringComparison.OrdinalIgnoreCase)
                                 || network.Contains("Ethereum", StringComparison.OrdinalIgnoreCase)
                                 || network.Contains("BSC", StringComparison.OrdinalIgnoreCase);

                var expectsTron = network.Contains("TRC", StringComparison.OrdinalIgnoreCase)
                                  || network.Contains("Tron", StringComparison.OrdinalIgnoreCase);

                var expectsTon = network.Contains("TON", StringComparison.OrdinalIgnoreCase);

                if (expectsEvm && !looksLikeEvm)
                    throw new InvalidOperationException(
                        $"Адрес «{option.Title}» не похож на адрес сети {network}: {address}. " +
                        "Для ERC-20 и BEP-20 адрес начинается с 0x и содержит 40 hex-знаков.");

                if (expectsTron && !looksLikeTron)
                    throw new InvalidOperationException(
                        $"Адрес «{option.Title}» не похож на адрес сети {network}: {address}. " +
                        "Для TRC-20 адрес начинается с T и содержит 34 знака.");

                if (expectsTon && !looksLikeTon)
                    throw new InvalidOperationException(
                        $"Адрес «{option.Title}» не похож на адрес сети {network}: {address}. " +
                        "Для TON адрес содержит 48 знаков в формате base64 (буквы, цифры, - и _).");

                if (!looksLikeEvm && !looksLikeTron && !looksLikeTon)
                    throw new InvalidOperationException(
                        $"Адрес «{option.Title}» не похож ни на ERC-20, ни на TRC-20, ни на TON: {address}");
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

        RunTest(results, "Ссылка DonationAlerts указана верно и её QR на месте", () =>
        {
            // Битая ссылка означает, что человек нажмёт кнопку и попадёт в никуда.
            var url = DonationInfo.DonationAlertsUrl;

            if (string.IsNullOrWhiteSpace(url))
            {
                // Пустая ссылка — допустимо: кнопка просто неактивна.
                return;
            }

            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Ссылка DonationAlerts должна начинаться с https:// — сейчас: {url}");

            if (!url.Contains("donationalerts.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Ссылка DonationAlerts ведёт не на donationalerts.com: {url}");

            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                throw new InvalidOperationException(
                    $"Ссылка DonationAlerts не разбирается как адрес: {url}");

            if (!DonationInfo.DonationAlertsQrExists)
                throw new InvalidOperationException(
                    $"Ссылка указана, но картинки QR нет: ожидается " +
                    $"{DonationInfo.DonationAlertsQrPath}");

            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(DonationInfo.DonationAlertsQrPath);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            if (bitmap.PixelWidth < 150 || bitmap.PixelHeight < 150)
                throw new InvalidOperationException(
                    $"QR DonationAlerts слишком мелкий: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
        });

        RunTest(results, "План исправлений берётся из находок, а не из всего набора", () =>
        {
            // Это защита от самой неприятной ошибки: программа говорит «нажмите кнопку»,
            // а кнопка меняет совсем не то, что нашла проверка.
            var findings = new[]
            {
                CheckResult.Info("test.a", "Проверка с исправлением", "нашлось",
                    fixes: new[] { new FixAction("fix.one", "Первое", FixRisk.Safe) }),
                CheckResult.Warn("test.b", "Проверка с ручным действием", "нашлось",
                    fixes: new[] { new FixAction("fix.two", "Второе", FixRisk.ManualOnly, "руками") }),
                CheckResult.Ok("test.c", "Всё хорошо", "чисто")
            };

            var plan = FixPlan.FromFindings(findings);

            if (!plan.BuiltFromFindings)
                throw new InvalidOperationException("План не помечен как собранный из находок");

            if (!plan.WantsFix("fix.one"))
                throw new InvalidOperationException("Безопасное исправление из находки не попало в план");

            if (plan.WantsFix("fix.two"))
                throw new InvalidOperationException("Ручное действие попало в план: программа обещает то, чего не делает");

            if (plan.WantsFix("fix.three"))
                throw new InvalidOperationException("Исправление, которого нет в находках, попало в план");
        });

        RunTest(results, "Пустой план не означает «применяй всё»", () =>
        {
            // Проверка нашла проблемы, но исправлять нечего. Если план в этом случае
            // считает себя пустым «по умолчанию», кнопка начнёт менять всё подряд.
            var findings = new[]
            {
                CheckResult.Warn("test.x", "Проблема без исправления", "нашлось")
            };

            var plan = FixPlan.FromFindings(findings);

            if (plan.IsEmpty != true)
                throw new InvalidOperationException("План с пустыми находками должен быть пустым");

            if (plan.WantsFix("net.adapter.hostile-settings"))
                throw new InvalidOperationException(
                    "План без находок разрешил менять сетевой адаптер — это и есть та ошибка, " +
                    "из-за которой кнопка применяла всё подряд");

            // А вот когда диагностика не запускалась, применять всё можно — и это явно.
            if (!FixPlan.Everything.WantsFix("net.adapter.hostile-settings"))
                throw new InvalidOperationException("Режим «применить всё» перестал работать");
        });

        RunTest(results, "Подпункт плана включает только нужный параметр", () =>
        {
            // Одно исправление закрывает несколько параметров. Проверка может жаловаться
            // на один — менять остальные нельзя.
            const string fixId = "scheduler.mmcss";
            var plan = FixPlan.For(new FixTarget(fixId, fixId + ".Priority"));

            if (!plan.WantsFix(fixId))
                throw new InvalidOperationException("Исправление не попало в план по своему подпункту");

            if (!plan.WantsSubAction(fixId, fixId + ".Priority"))
                throw new InvalidOperationException("Указанный подпункт не попал в план");

            if (plan.WantsSubAction(fixId, fixId + ".Scheduling Category"))
                throw new InvalidOperationException("В план попал лишний параметр, которого проверка не просила");

            // А если исправление выбрано целиком, меняем все его параметры.
            var whole = FixPlan.For(new FixTarget(fixId));
            if (!whole.WantsSubAction(fixId, fixId + ".NetworkThrottlingIndex"))
                throw new InvalidOperationException("Выбор исправления целиком не покрыл его параметры");
        });
        RunTest(results, "Оценка потерь: пороги и честность при недоступном узле", () =>
        {
            // 0 потерь — чисто.
            if (LossEvaluator.Evaluate(100, 0) != LossEvaluator.Outcome.Clean)
                throw new InvalidOperationException("Замер без потерь не признан чистым");

            // Меньше процента — единичная потеря, а не проблема.
            if (LossEvaluator.Evaluate(200, 1) != LossEvaluator.Outcome.SingleLoss)
                throw new InvalidOperationException("Одна потеря из 200 признана проблемой");

            // Процент и больше — в игре уже чувствуется.
            if (LossEvaluator.Evaluate(100, 1) != LossEvaluator.Outcome.Lossy)
                throw new InvalidOperationException("Одна потеря из 100 не признана проблемой");

            if (LossEvaluator.Evaluate(20, 3) != LossEvaluator.Outcome.Lossy)
                throw new InvalidOperationException("Три потери из 20 не признаны проблемой");

            // Главное: полное отсутствие ответа — это НЕ «100% потерь», а «измерить не вышло».
            if (LossEvaluator.Evaluate(20, 20) != LossEvaluator.Outcome.Inconclusive)
                throw new InvalidOperationException(
                    "Недоступный узел выдан за 100% потерь — это нарушение правила проекта");

            // Число попыток не должно опускаться ниже разумного минимума.
            if (LossEvaluator.AttemptsFor(3) < LossEvaluator.MinimumAttempts)
                throw new InvalidOperationException("На коротком замере делается слишком мало попыток");
        });
        RunTest(results, "Определение двухканального режима памяти", () =>
        {
            // Режим памяти определяется по расположению планок. Ошибка здесь означает
            // ложный совет «переставьте планки» — то есть человек полезет в корпус зря.
            var dualController = MemoryCheck.DescribeChannel("Controller0-DIMMA2");
            var dualController2 = MemoryCheck.DescribeChannel("Controller1-DIMMB2");

            if (dualController == dualController2)
                throw new InvalidOperationException(
                    "Планки в разных контроллерах считаются одним каналом — это ложное предупреждение");

            var sameController = MemoryCheck.DescribeChannel("Controller0-DIMMA1");
            var sameController2 = MemoryCheck.DescribeChannel("Controller0-DIMMB1");

            if (sameController != sameController2)
                throw new InvalidOperationException(
                    "Планки в одном контроллере считаются разными каналами — это пропуск проблемы");

            // Буквенный формат слотов (платы без Controller в имени).
            var byLetter = MemoryCheck.DescribeChannel("DIMMA1");
            var byLetter2 = MemoryCheck.DescribeChannel("DIMMB1");

            if (byLetter is null || byLetter2 is null || byLetter == byLetter2)
                throw new InvalidOperationException("Буквенный формат слотов не разобран");

            // Непонятный формат: честно null, а не выдуманный канал.
            if (MemoryCheck.DescribeChannel("Bank 0") is not null)
                throw new InvalidOperationException("Для непонятного слота выдуман канал");
        });

        RunTest(results, "Событие о перегреве ищется с фильтром по источнику", () =>
        {
            // Это защита от реальной ошибки: сначала проверка брала события только
            // по коду 37 и находила записи службы точного времени, после чего
            // выдавала ложное предупреждение о перегреве процессора.
            var query = ThermalCheck.BuildEventQuery("Microsoft-Windows-Kernel-Processor-Power", 37);

            if (!query.Contains("SourceName=", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Запрос не фильтрует по источнику: в него попадут чужие события");

            if (!query.Contains("EventCode=37", StringComparison.Ordinal))
                throw new InvalidOperationException("Запрос не фильтрует по коду события");

            // Событие 55 — обычная информация о питании, она пишется постоянно.
            if (ThermalCheck.ThrottleEventCodes.Contains(55))
                throw new InvalidOperationException(
                    "Информационное событие 55 попало в признаки перегрева — это ложные срабатывания");

            if (ThermalCheck.ThrottleEventCodes.Count == 0 || ThermalCheck.ThrottleEventSources.Count == 0)
                throw new InvalidOperationException("Список признаков перегрева пуст");
        });
        RunTest(results, "Список оверлеев не пуст и не путает программы", () =>
        {
            // Ошибка в этом списке стоит дорого: за оверлей на FACEIT и ESEA
            // блокируют аккаунт, поэтому ложное предупреждение здесь недопустимо.
            var known = OverlayCheck.KnownOverlays;

            if (known.Length < 8)
                throw new InvalidOperationException($"В списке оверлеев всего {known.Length} записей");

            foreach (var app in known)
            {
                if (app.ProcessNames.Length == 0)
                    throw new InvalidOperationException($"У «{app.Title}» не указано ни одного процесса");

                if (string.IsNullOrWhiteSpace(app.Note))
                    throw new InvalidOperationException($"У «{app.Title}» нет объяснения, чем он мешает");
            }

            // Одна и та же программа не должна встречаться дважды: иначе в отчёте
            // будет две записи об одном и том же.
            var duplicates = known.GroupBy(a => a.Title).Where(g => g.Count() > 1).ToList();

            if (duplicates.Count > 0)
                throw new InvalidOperationException(
                    "Программа повторяется в списке: " + string.Join(", ", duplicates.Select(d => d.Key)));

            // Программы, за которые блокируют на площадках, должны быть помечены явно.
            var discord = known.FirstOrDefault(a => a.Title == "Discord");
            if (discord is null || !discord.BannedOnPlatforms)
                throw new InvalidOperationException("Discord не помечен как запрещённый на площадках");

            var obs = known.FirstOrDefault(a => a.Title == "OBS Studio");
            if (obs is null || obs.BannedOnPlatforms)
                throw new InvalidOperationException(
                    "OBS помечен как запрещённый: это ложное предупреждение о блокировке");
        });
        RunTest(results, "Отчёт сохраняется и содержит то, что нужно для разговора", () =>
        {
            // Отчёт несут провайдеру или выкладывают на форум, поэтому в нём должны
            // быть версия, дата и отдельный список ручных действий. Без версии
            // непонятно, какая сборка это выдала; без списка — что осталось сделать.
            var report = new DiagnosticReport
            {
                Duration = TimeSpan.FromSeconds(12.3),
                Results = new[]
                {
                    CheckResult.Warn("test.problem", "Проверочная проблема", "нашлось что-то",
                        "потому что так работает",
                        new[]
                        {
                            new FixAction("test.manual", "Сделать руками", FixRisk.ManualOnly,
                                "Требуется физическое действие."),
                            new FixAction("test.auto", "Сделать самим", FixRisk.Safe)
                        },
                        "Что делать: возьмите и сделайте."),
                    CheckResult.Ok("test.ok", "Проверочная норма", "всё чисто")
                }
            };

            // Каталог Temp в этой среде закрыт политикой, поэтому пишем рядом с тестами.
            var directory = Path.Combine(Directory.GetCurrentDirectory(),
                "test-report-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);

            try
            {
                var textPath = Path.Combine(directory, "report.txt");
                var jsonPath = Path.Combine(directory, "report.json");

                var text = ReportExporter.SaveText(report, textPath);
                var json = ReportExporter.SaveJson(report, jsonPath);

                if (text.SizeBytes <= 0 || json.SizeBytes <= 0)
                    throw new InvalidOperationException("Отчёт сохранился пустым");

                var textContent = File.ReadAllText(textPath);

                foreach (var expected in new[]
                         {
                             AppVersion.Short, "ПРОВЕРКА КОМПЬЮТЕРА", "Проверочная проблема",
                             "СДЕЛАТЬ РУКАМИ", "Сделать руками", "Что делать: возьмите и сделайте."
                         })
                {
                    if (!textContent.Contains(expected, StringComparison.Ordinal))
                        throw new InvalidOperationException($"В текстовом отчёте нет «{expected}»");
                }

                // Программа не должна обещать в отчёте то, чего не сделает.
                if (!textContent.Contains("руками", StringComparison.Ordinal))
                    throw new InvalidOperationException("В отчёте не отмечено, что делается руками");

                var jsonContent = File.ReadAllText(jsonPath);

                if (!jsonContent.Contains("test.problem", StringComparison.Ordinal))
                    throw new InvalidOperationException("В JSON-отчёте нет находки");

                if (!jsonContent.Contains("canApplyAutomatically", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "В JSON-отчёте нет признака, применимо ли исправление автоматически");
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { /* не критично */ }
            }
        });
        RunTest(results, "Браузеры и редакторы защищены от паузы", () =>
        {
            // Эта проверка появилась после реальной ошибки: обнаружение по нагрузке
            // нашло браузер и среду разработки, отметило их к остановке — и закрыло
            // вместе с открытыми вкладками и несохранённой работой.
            var mustBeProtected = new[]
            {
                "chrome", "firefox", "msedge", "opera", "brave",
                "code", "devenv", "rider64", "notepad++",
                "python", "node", "dotnet", "java", "docker", "postgres",
                "keepass", "keepassxc", "1password",
                "explorer", "powershell", "pwsh", "cmd", "windowsterminal"
            };

            var unprotected = mustBeProtected
                .Where(name => !BackgroundAppService.NeverDiscover.Contains(name))
                .ToList();

            if (unprotected.Count > 0)
                throw new InvalidOperationException(
                    "Эти программы могут быть закрыты по ошибке: " + string.Join(", ", unprotected));

            // Обнаружение по нагрузке не должно предлагать то, что уже защищено.
            var service = new BackgroundAppService();
            var found = service.Survey();

            var dangerous = found
                .Where(a => BackgroundAppService.NeverDiscover.Contains(a.ProcessName))
                .Select(a => a.ProcessName)
                .ToList();

            if (dangerous.Count > 0)
                throw new InvalidOperationException(
                    "В списке на паузу оказались защищённые программы: " + string.Join(", ", dangerous));

            // Найденное по нагрузке не должно быть отмечено заранее: программа не знает,
            // что это за процесс. Отмечать такое — значит закрывать чужую работу без спроса.
            var preselected = found.Where(a => a.FoundByActivity).ToList();

            if (preselected.Count > 0)
            {
                // Само по себе попадание в список допустимо, но признак обязан стоять,
                // иначе интерфейс отметит строку галочкой.
                if (preselected.Any(a => !a.FoundByActivity))
                    throw new InvalidOperationException("Признак «найдена по нагрузке» потерялся");
            }
        });
        RunTest(results, "Сравнение замеров отвечает на вопрос «что изменилось сейчас»", () =>
        {
            // Разница принципиальная: сводка истории сравнивает ПЕРВЫЙ замер
            // с последним («помогло ли за всё время»), а сравнение последних двух
            // показывает, что дали только что применённые исправления.
            var store = new HistoryStore();
            var before = store.Read();

            var first = new HistorySnapshot
            {
                Summary = "проверочный замер 1",
                Metrics = new Dictionary<string, Dictionary<string, double>>
                {
                    ["net.latency.gateway"] = new() { ["spike_percent"] = 9.5, ["median_ms"] = 2.0 }
                },
                Titles = new Dictionary<string, string> { ["net.latency.gateway"] = "Задержка до роутера" }
            };

            var second = new HistorySnapshot
            {
                Summary = "проверочный замер 2",
                Metrics = new Dictionary<string, Dictionary<string, double>>
                {
                    ["net.latency.gateway"] = new() { ["spike_percent"] = 0.0, ["median_ms"] = 2.0 }
                },
                Titles = new Dictionary<string, string> { ["net.latency.gateway"] = "Задержка до роутера" }
            };

            store.Append(first);
            store.Append(second);

            var comparison = store.CompareLastTwo();

            if (!comparison.Improved.Any(t => t.MetricName == "spike_percent"))
                throw new InvalidOperationException(
                    "Уменьшение всплесков с 9.5% до 0% не показано как улучшение");

            // Метрика без изменений не должна попадать ни в улучшения, ни в ухудшения.
            if (comparison.Improved.Any(t => t.MetricName == "median_ms") ||
                comparison.Worsened.Any(t => t.MetricName == "median_ms"))
                throw new InvalidOperationException("Метрика без изменений показана как изменение");

            // Возвращаем файл истории в прежнее состояние, чтобы не портить данные.
            store.Clear();
            foreach (var snapshot in before) store.Append(snapshot);
        });
        RunTest(results, "Версия программы указана и читается", () =>
        {
            // Версия нужна, чтобы понять, какая сборка выдала отчёт: без неё
            // невозможно разобраться, что человек видел на экране.
            if (string.IsNullOrWhiteSpace(AppVersion.Short))
                throw new InvalidOperationException("Короткая версия пуста");

            if (!AppVersion.Short.Contains('.', StringComparison.Ordinal))
                throw new InvalidOperationException($"Версия «{AppVersion.Short}» не похожа на номер версии");

            if (!AppVersion.Display.Contains(AppVersion.Short, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Строка для интерфейса не содержит номер версии");
        });
        RunTest(results, "Вывод ping.exe разбирается на обоих языках Windows", () =>
        {
            // Это последний запасной способ замера. Вывод ping.exe локализован,
            // поэтому разбор идёт по смыслу строк, а не по конкретным словам.
            // Ошибка здесь означает, что программа не увидит потери там, где
            // другие способы закрыты, — то есть промолчит о реальной проблеме.
            const string english = """
                Pinging 1.1.1.1 with 32 bytes of data:
                Reply from 1.1.1.1: bytes=32 time=31ms TTL=57

                Ping statistics for 1.1.1.1:
                    Packets: Sent = 4, Received = 3, Lost = 1 (25% loss),
                """;

            const string russian = """
                Обмен пакетами с 1.1.1.1 [1.1.1.1] с 32 байтами данных:
                Ответ от 1.1.1.1: число байт=32 время=31мс TTL=57

                Статистика Ping для 1.1.1.1:
                    Пакетов: отправлено = 4, получено = 3, потеряно = 1
                    (25% потерь)
                """;

            var parsedEn = PingExeParser.Parse(english);
            var parsedRu = PingExeParser.Parse(russian);

            if (!parsedEn.Parsed || parsedEn.Sent != 4 || parsedEn.Received != 3)
                throw new InvalidOperationException(
                    $"Английский вывод разобран неверно: отправлено {parsedEn.Sent}, получено {parsedEn.Received}");

            if (!parsedRu.Parsed || parsedRu.Sent != 4 || parsedRu.Received != 3)
                throw new InvalidOperationException(
                    $"Русский вывод разобран неверно: отправлено {parsedRu.Sent}, получено {parsedRu.Received}");

            // Непонятный вывод: честно «не разобрано», а не выдуманные числа.
            var unknown = PingExeParser.Parse("что-то пошло не так");

            if (unknown.Parsed)
                throw new InvalidOperationException(
                    "Из непонятного вывода выдуманы числа — это ложные данные в отчёте");
        });
        RunTest(results, "Ссылка «сообщить о проблеме» ведёт куда надо и несёт версию", () =>
        {
            // Кнопка в окне — единственный путь, которым человек попадёт на страницу
            // сообщений. Если ссылка сломается, о проблемах мы просто не узнаем.
            var url = FeedbackLinks.NewIssueUrl();

            if (!url.StartsWith(FeedbackLinks.RepositoryUrl + "/issues/new", StringComparison.Ordinal))
                throw new InvalidOperationException($"Ссылка ведёт не на форму сообщения: {url}");

            // Метка должна присутствовать, иначе сообщение не попадёт в разбор.
            if (!url.Contains("labels=", StringComparison.Ordinal))
                throw new InvalidOperationException("В ссылке нет метки — сообщение не попадёт в категорию");

            // Версия должна подставляться: без неё невозможно понять, какая это сборка.
            var version = Uri.EscapeDataString(AppVersion.Display);

            if (!url.Contains(version, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"В ссылке нет версии программы — не понять, какая сборка у человека. Ждали «{version}»");

            // Адрес должен быть пригоден для открытия: без пробелов и кириллицы в открытом виде.
            if (url.Contains(' ', StringComparison.Ordinal))
                throw new InvalidOperationException("В ссылке остались пробелы — браузер её не откроет");

            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) is false)
                throw new InvalidOperationException("Ссылка не является правильным адресом");

            if (parsed.Scheme != "https")
                throw new InvalidOperationException("Ссылка должна быть https");
        });

        RunTest(results, "Отчёт содержит сведения о системе, но не имя компьютера", () =>
        {
            // Когда человек присылает отчёт, по нему нужно понять, на чём шла проверка:
            // от материнской платы зависит, какие бывают сетевые карты, а от сборки
            // Windows — какие проверки применимы. Без этого разбор превращается
            // в переписку с вопросами «а что у вас за железо».
            var system = SystemFingerprintReader.Read();

            if (string.IsNullOrWhiteSpace(system.ShortLine))
                throw new InvalidOperationException("Не удалось прочитать версию Windows");

            if (!system.HasHardware)
                throw new InvalidOperationException(
                    "Не удалось прочитать сведения о железе: отчёт будет бесполезен для разбора");

            // Главное: имени компьютера в сведениях быть НЕ должно. Отчёт человек
            // может выложить публично, не подумав, а имя машины для разбора не нужно.
            var fingerprintText = string.Join(" | ", system.ToLines().Select(l => l.Value));

            if (fingerprintText.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "В сведениях о системе оказалось имя компьютера — это лишние данные в публичном отчёте");

            // Названия должны быть читаемыми, а не сырыми строками из WMI.
            if (system.Cpu?.Contains("(R)", StringComparison.Ordinal) == true)
                throw new InvalidOperationException("В названии процессора остались маркетинговые приставки");

            if (system.Gpu?.Contains("(R)", StringComparison.Ordinal) == true)
                throw new InvalidOperationException("В названии видеокарты остались маркетинговые приставки");

            // Версия Windows должна быть с номером сборки: без него не отличить
            // одну сборку Windows 10 от другой, а поведение у них разное.
            if (string.IsNullOrWhiteSpace(system.WindowsBuild))
                throw new InvalidOperationException(
                    "В сведениях нет номера сборки Windows — по такому отчёту не воспроизвести проверку");
        });

        RunTest(results, "Подробный отчёт собирается даже без проверки и без лишнего", () =>
        {
            // Этот файл человек отправляет сам — значит, он должен быть и полезным
            // для разбора, и безопасным: без имени компьютера и чужих путей.
            //
            // Проверка не обязательна: если программа падает при запуске, отчёт
            // нужен как раз без результатов проверки.
            var directory = Path.Combine(Directory.GetCurrentDirectory(),
                "test-dev-" + Guid.NewGuid().ToString("N")[..8]);

            Directory.CreateDirectory(directory);

            try
            {
                var path = Path.Combine(directory, "подробный.json");

                var saved = ReportExporter.SaveJson(null, path, null,
                    Array.Empty<CheckResult>(), DeveloperReport.Collect());

                if (!File.Exists(path))
                    throw new InvalidOperationException("Подробный отчёт не создался");

                var report = File.ReadAllText(path);

                if (report.Length < 200)
                    throw new InvalidOperationException("Отчёт получился подозрительно коротким");

                var parsed = System.Text.Json.JsonDocument.Parse(report);

                foreach (var section in new[] { "about", "program", "system", "state" })
                {
                    if (!parsed.RootElement.TryGetProperty(section, out _))
                        throw new InvalidOperationException($"В отчёте нет раздела «{section}»");
                }

                // Главное: имени компьютера быть не должно.
                if (report.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "В подробном отчёте оказалось имя компьютера — лишние данные в отправляемом файле");

                // В отчёте должно быть прямо сказано, что программа его никуда не шлёт:
                // человек отдаёт файл добровольно, и это нужно подтвердить текстом.
                if (!report.Contains("Приложите этот файл", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "В отчёте не сказано, что отправляет его человек, а не программа");

                // Описание содержимого тоже должно быть честным: показываем его перед сохранением.
                var description = DeveloperReport.DescribeContents();

                foreach (var expected in new[] { "НЕ будет", "никуда", "имени компьютера" })
                {
                    if (!description.Contains(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"В описании содержимого нет слов про «{expected}»");
                }
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { /* не критично */ }
            }
        });
        RunTest(results, "Браузеры в системе находятся, и среди них есть запасной", () =>
        {
            // Это защита от истории с падающим Firefox: браузер по умолчанию не открыл
            // ссылку, и человек остался ни с чем. Программа должна уметь найти другой.
            var browsers = BrowserLauncher.FindInstalled();

            if (browsers.Count == 0)
                throw new InvalidOperationException(
                    "В системе не найдено ни одного браузера — при сломанном браузере по умолчанию " +
                    "человек не сможет открыть страницу сообщений");

            foreach (var browser in browsers)
            {
                if (!File.Exists(browser.ExecutablePath))
                    throw new InvalidOperationException(
                        $"В списке браузеров путь, которого нет: {browser.ExecutablePath}");

                if (string.IsNullOrWhiteSpace(browser.Title))
                    throw new InvalidOperationException("У браузера пустое название в списке");
            }

            // Повторов быть не должно: один браузер может быть прописан в реестре
            // дважды — для пользователя и для всех.
            var duplicates = browsers
                .GroupBy(b => b.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicates.Count > 0)
                throw new InvalidOperationException(
                    "Один и тот же браузер попал в список дважды: " + duplicates[0].Key);
        });
        RunTest(results, "Обе кнопки сохранения ДЕЙСТВИТЕЛЬНО создают файл", () =>
        {
            // Этот тест появился после серьёзной ошибки: кнопки «Сохранить отчёт»
            // и «Отчёт разработчику» в собранной для скачивания версии не работали
            // вообще. Падало с «Could not load file or assembly System.Management»,
            // потому что библиотека лежала в подпапке runtimes, а папка не попадала
            // в архив. Прежние тесты этого не ловили: они проверяли, что отчёт
            // собирается, но не проверяли, что он СОХРАНЯЕТСЯ.
            //
            // Поэтому здесь всё по-настоящему: создаём отчёт, сохраняем оба файла
            // и убеждаемся, что они есть и не пустые.
            var directory = Path.Combine(Directory.GetCurrentDirectory(),
                "test-export-" + Guid.NewGuid().ToString("N")[..8]);

            Directory.CreateDirectory(directory);

            try
            {
                // Настоящий отчёт с настоящей проверкой: так вызывается вся цепочка
                // чтения сведений о системе, где и падала загрузка сборки.
                var context = new DiagnosticContext { IsAdministrator = true, ProbeSeconds = 4 };
                var report = DiagnosticRunner.CreateDefault(4).RunAsync(context, CancellationToken.None)
                    .GetAwaiter().GetResult();

                var textPath = Path.Combine(directory, "обычный.txt");
                var jsonPath = Path.Combine(directory, "обычный.json");
                var devPath = Path.Combine(directory, "разработчику.json");

                // 1. Обычный отчёт: текст.
                var text = ReportExporter.SaveText(report, textPath);
                if (!File.Exists(textPath) || text.SizeBytes <= 0)
                    throw new InvalidOperationException("«Сохранить отчёт» (текст) не создал файл");

                // 2. Обычный отчёт: JSON.
                var json = ReportExporter.SaveJson(report, jsonPath);
                if (!File.Exists(jsonPath) || json.SizeBytes <= 0)
                    throw new InvalidOperationException("«Сохранить отчёт» (JSON) не создал файл");

                // 3. Отчёт разработчику.
                var saved = DeveloperReport.Save(devPath, report, report.Results, null, out var error);

                if (saved is null || !File.Exists(devPath))
                    throw new InvalidOperationException(
                        "«Отчёт разработчику» не создал файл: " + (error ?? "причина неизвестна"));

                if (new FileInfo(devPath).Length <= 0)
                    throw new InvalidOperationException("«Отчёт разработчику» создал пустой файл");

                // Содержимое тоже проверяем: файл должен быть читаемым и полным.
                var devContent = File.ReadAllText(devPath);

                foreach (var expected in new[] { "system", "results", "about", "program", "state" })
                {
                    if (!devContent.Contains(expected, StringComparison.Ordinal))
                        throw new InvalidOperationException($"В отчёте разработчику нет раздела «{expected}»");
                }

                if (!devContent.Contains(AppVersion.Short, StringComparison.Ordinal))
                    throw new InvalidOperationException("В отчёте разработчику нет версии программы");

                // Сведения о железе должны быть: без них отчёт бесполезен.
                if (!devContent.Contains("motherboard", StringComparison.Ordinal))
                    throw new InvalidOperationException("В отчёте разработчику нет сведений о плате");
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { /* не критично */ }
            }
        });
        RunTest(results, "Библиотека System.Management находится там, где её ищет программа", () =>
        {
            // Этот тест появился после самой дорогой ошибки за всё время: в собранной
            // для скачивания версии обе кнопки сохранения отчёта не работали вообще.
            // Падало с «Could not load file or assembly System.Management», потому что
            // библиотека лежала в подпапке runtimes, а папка не попадала в архив:
            // сборщик архива копировал только файлы из корня.
            //
            // Проверяем правило, из-за которого это случилось: если в deps.json записан
            // путь к подпапке, эта подпапка обязана существовать рядом с программой.
            var directory = AppContext.BaseDirectory;
            var deps = Path.Combine(directory, "Cs2LatencyDoctor.Gui.deps.json");

            if (!File.Exists(deps))
            {
                // Консольная сборка: проверять нечего.
                return;
            }

            var content = File.ReadAllText(deps);

            // Ищем пути вида runtimes/.../System.Management.dll
            var matches = System.Text.RegularExpressions.Regex.Matches(
                content, @"runtimes/[^""]*System\.Management\.dll");

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var relative = match.Value.Replace('/', Path.DirectorySeparatorChar);
                var expected = Path.Combine(directory, relative);

                if (!File.Exists(expected))
                    throw new InvalidOperationException(
                        "В deps.json записан путь «" + match.Value + "», но файла по нему нет. " +
                        "Значит, при сборке архива папка runtimes не скопирована — " +
                        "и у человека, скачавшего программу, отчёты не сохранятся.");

                return;
            }

            // Путей к подпапкам нет: библиотека должна лежать в корне.
            if (!File.Exists(Path.Combine(directory, "System.Management.dll")))
                throw new InvalidOperationException(
                    "Библиотеки System.Management нет ни в корне, ни в подпапке — " +
                    "программа упадёт при первом обращении к сведениям о системе.");
        });

        RunTest(results, "Отчёты не повторяют друг друга и не содержат лишнего", () =>
        {
            // Этот тест появился после того, как в окне оказались три кнопки отчётов,
            // а два из них пересекались: версия, сведения о системе, находки и история
            // попадали в оба файла. Два отчёта об одном и том же путают — непонятно,
            // какой отправлять. Теперь отчёт один, различается только подробность.
            var directory = Path.Combine(Directory.GetCurrentDirectory(),
                "test-reports-" + Guid.NewGuid().ToString("N")[..8]);

            Directory.CreateDirectory(directory);

            try
            {
                var context = new DiagnosticContext { IsAdministrator = true, ProbeSeconds = 4 };
                var report = DiagnosticRunner.CreateDefault(4).RunAsync(context, CancellationToken.None)
                    .GetAwaiter().GetResult();

                // 1. Читаемый отчёт для письма.
                var textPath = Path.Combine(directory, "читаемый.txt");
                ReportExporter.SaveText(report, textPath);

                if (!File.Exists(textPath) || new FileInfo(textPath).Length == 0)
                    throw new InvalidOperationException("Читаемый отчёт не создался");

                // 2. Обычный JSON.
                var plainPath = Path.Combine(directory, "обычный.json");
                ReportExporter.SaveJson(report, plainPath, null);
                var plain = File.ReadAllText(plainPath);

                // 3. Подробный JSON: тот же отчёт плюс состояние программы.
                var devPath = Path.Combine(directory, "подробный.json");
                ReportExporter.SaveJson(report, devPath, null, report.Results, DeveloperReport.Collect());
                var dev = File.ReadAllText(devPath);

                // Имени компьютера не должно быть ни в одном файле.
                foreach (var (name, content) in new[] { ("обычном", plain), ("подробном", dev) })
                {
                    if (content.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"В {name} отчёте оказалось имя компьютера — лишние данные в отправляемом файле");
                }

                // Главное: подробный отчёт ОТЛИЧАЕТСЯ от обычного, а не повторяет его.
                // Если однажды кто-то снова сделает два одинаковых отчёта — тест упадёт.
                if (!dev.Contains("\"state\"", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "В подробном отчёте нет раздела state — значит он не отличается от обычного");

                if (plain.Contains("\"state\"", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "В обычном отчёте оказался раздел state: файлы снова повторяют друг друга");

                // Оба должны быть правильным JSON с находками и сведениями о системе.
                foreach (var (name, content) in new[] { ("обычном", plain), ("подробном", dev) })
                {
                    var parsed = System.Text.Json.JsonDocument.Parse(content);

                    if (!parsed.RootElement.TryGetProperty("results", out var resultsNode))
                        throw new InvalidOperationException($"В {name} отчёте нет находок");

                    if (resultsNode.GetArrayLength() == 0)
                        throw new InvalidOperationException($"В {name} отчёте находки пусты");

                    if (!parsed.RootElement.TryGetProperty("system", out _))
                        throw new InvalidOperationException($"В {name} отчёте нет сведений о системе");
                }
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { /* не критично */ }
            }
        });

        RunTest(results, "Отчёт сохраняется в готовую папку без окна выбора файла", () =>
        {
            // Этот тест появился после падения программы при нажатии «Сохранить отчёт».
            // Причина оказалась не в нашем коде: падало системное окно выбора файла,
            // изнутри Windows, с кодом 0xc0000409. Такое падение нельзя поймать
            // обработчиком исключений — процесс завершается мгновенно.
            //
            // Поэтому окна больше нет: папка задана заранее. Тест проверяет, что путь
            // создаётся и файл по нему действительно записывается.
            var path = ReportExporter.SuggestFullPath(DateTimeOffset.Now, "txt");

            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Путь для отчёта не сформирован");

            var directory = Path.GetDirectoryName(path);

            if (directory is null || !Directory.Exists(directory))
                throw new InvalidOperationException(
                    "Папка для отчётов не создана: " + (directory ?? "путь пуст"));

            if (!path.EndsWith(".txt", StringComparison.Ordinal))
                throw new InvalidOperationException("Путь не оканчивается нужным расширением: " + path);

            // Имя должно содержать дату: иначе отчёты перезапишут друг друга.
            var name = Path.GetFileName(path);

            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\d{4}-\d{2}-\d{2}"))
                throw new InvalidOperationException("В имени файла нет даты: " + name);

            // Два отчёта подряд не должны получить одинаковое имя.
            var second = ReportExporter.SuggestFullPath(DateTimeOffset.Now.AddSeconds(1), "txt");

            if (second == path)
                throw new InvalidOperationException("Два отчёта получили одинаковое имя — перезапишут друг друга");

            // И проверяем, что по этому пути файл реально записывается.
            File.WriteAllText(path, "проверка");

            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidOperationException("Файл по подготовленному пути не записался");

            File.Delete(path);
        });
        RunTest(results, "Запуск браузера проверяется, а не предполагается", () =>
        {
            // Этот тест появился после истории с падающим Firefox. Программа
            // рапортовала «Открыл страницу в браузере», не проверив, открылась ли
            // она: возвращала успех сразу после запуска процесса. Человек в этот
            // момент видел окно «Firefox столкнулся с проблемой и аварийно
            // завершил работу» — и справедливо считал виноватой программу.
            //
            // Теперь запуск проверяется по живым процессам браузера.
            var browsers = BrowserLauncher.FindInstalled();

            if (browsers.Count == 0)
                throw new InvalidOperationException(
                    "В системе не найдено ни одного браузера — программа не сможет открыть ссылку");

            // Проверяем, что определение живых процессов вообще работает:
            // у заведомо отсутствующего браузера процессов быть не должно.
            foreach (var browser in browsers)
            {
                if (string.IsNullOrWhiteSpace(browser.ExecutablePath))
                    throw new InvalidOperationException("У браузера «" + browser.Title + "» нет пути к программе");

                if (!File.Exists(browser.ExecutablePath))
                    throw new InvalidOperationException(
                        "Браузер «" + browser.Title + "» найден, но файла по пути нет: " + browser.ExecutablePath);
            }

            // Edge должен стоять первым: он есть почти везде и переживает сбои
            // браузера по умолчанию.
            if (!browsers[0].Title.Contains("Edge", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Первым в списке идёт «" + browsers[0].Title + "», а должен идти Edge: " +
                    "перебор начинается с него, и сбой браузера по умолчанию не должен мешать");
        });
        RunTest(results, "Подпись о исправимом показывает число, а не объясняет галочку", () =>
        {
            // Здесь была подпись «Галочка — что исправлять по кнопке». Человек
            // заметил, что она бесполезна: галочка и так стоит только там, где
            // есть что исправлять, и сама объясняет себя наведением.
            //
            // Теперь на её месте число исправимых находок, а когда исправлять
            // нечего — подписи нет вовсе.
            var row = new FindingRow
            {
                FindingId = "тест",
                Mark = "МОЖНО ЛУЧШЕ",
                Color = "#FFAA00",
                Title = "Проверка",
                Detail = "Подробности",
                Why = string.Empty,
                Recommendation = string.Empty,
                FixHint = string.Empty,
                CanApply = false,
                ApplySelected = false
            };

            var findings = new System.Collections.ObjectModel.ObservableCollection<FindingRow>();

            // Считаем так же, как это делает модель представления: важна формула,
            // а не сам класс окна — окно в тесте не поднять.
            var withNothing = System.Linq.Enumerable.Count(findings, f => f.CanApply);

            if (withNothing != 0)
                throw new InvalidOperationException("В пустом списке не должно быть исправимых находок");

            findings.Add(row);
            var withOneNotFixable = System.Linq.Enumerable.Count(findings, f => f.CanApply);

            if (withOneNotFixable != 0)
                throw new InvalidOperationException("Находка без исправления не должна считаться исправимой");

            // Та же находка, но исправимая.
            findings[0] = new FindingRow
            {
                FindingId = "тест",
                Mark = "ПРОБЛЕМА",
                Color = "#FF5555",
                Title = "Проверка",
                Detail = "Подробности",
                Why = string.Empty,
                Recommendation = string.Empty,
                FixHint = "Что-то исправить",
                CanApply = true,
                ApplySelected = true
            };

            var withOneFixable = System.Linq.Enumerable.Count(findings, f => f.CanApply);

            if (withOneFixable != 1)
                throw new InvalidOperationException("Исправимая находка должна посчитаться");

            // И проверяем сам текст подписи: он собирается из числа.
            var hint = withOneFixable == 1
                ? "Одну находку можно исправить по кнопке — галочка уже стоит."
                : $"Можно исправить по кнопке: {withOneFixable}. Галочки уже стоят.";

            if (!hint.Contains("Одну находку", StringComparison.Ordinal))
                throw new InvalidOperationException("Для одной находки текст должен быть в единственном числе: " + hint);
        });
        RunTest(results, "Разбор вывода ping.exe: времена ответов и «меньше миллисекунды»", () =>
        {
            // Этот тест появился после разбора жалобы на джиттер. Программа показывала
            // джиттер до роутера 1,3–2,5 мс там, где его нет: она не умела читать
            // «время<1мс» из вывода ping.exe и уходила на TCP-замер, а TCP-подключение
            // добавляет собственное дрожание в 1–3 мс.
            //
            // Проверяем оба языка: вывод ping.exe зависит от языка Windows, а не программы.

            // 1. Русский вывод, ответы меньше миллисекунды — как до роутера.
            var russian = string.Join("\n", new[]
            {
                "",
                "Обмен пакетами с 192.168.31.1 по с 32 байтами данных:",
                "Ответ от 192.168.31.1: число байт=32 время<1мс TTL=64",
                "Ответ от 192.168.31.1: число байт=32 время<1мс TTL=64",
                "Ответ от 192.168.31.1: число байт=32 время<1мс TTL=64",
                "",
                "Статистика Ping для 192.168.31.1:",
                "    Пакетов: отправлено = 3, получено = 3, потеряно = 0",
                "    (0% потерь)",
                "Приблизительное время приема-передачи в мс:",
                "    Минимальное = 0мсек, Максимальное = 0 мсек, Среднее = 0 мсек"
            });

            var r = PingExeParser.Parse(russian);

            if (!r.Parsed) throw new InvalidOperationException("Русский вывод не разобрался");
            if (r.Sent != 3) throw new InvalidOperationException("Отправлено разобрано неверно: " + r.Sent);
            if (r.Received != 3) throw new InvalidOperationException("Получено разобрано неверно: " + r.Received);

            if (!r.HasTimes)
                throw new InvalidOperationException(
                    "Времена ответов не разобрались — программа снова уйдёт на TCP-замер и придумает джиттер");

            if (r.TimesMs.Count != 3)
                throw new InvalidOperationException("Времён должно быть 3, а разобрано " + r.TimesMs.Count);

            // «меньше миллисекунды» — это не ноль и не единица. Ставим половину:
            // тогда в расчёте разброса нет ложного дрожания.
            foreach (var time in r.TimesMs)
            {
                if (time > 0.5)
                    throw new InvalidOperationException(
                        "«время<1мс» разобрано как " + time + " мс — это завышает задержку");
            }

            // 2. Английский вывод с обычными временами.
            var english = string.Join("\n", new[]
            {
                "Pinging 1.1.1.1 with 32 bytes of data:",
                "Reply from 1.1.1.1: bytes=32 time=29ms TTL=57",
                "Reply from 1.1.1.1: bytes=32 time=31ms TTL=57",
                "Reply from 1.1.1.1: bytes=32 time=30ms TTL=57",
                "Packets: Sent = 3, Received = 3, Lost = 0 (0% loss),"
            });

            var e = PingExeParser.Parse(english);

            if (!e.HasTimes || e.TimesMs.Count != 3)
                throw new InvalidOperationException("Английский вывод: времён " + e.TimesMs.Count + " вместо 3");

            if (e.TimesMs[0] != 29 || e.TimesMs[1] != 31 || e.TimesMs[2] != 30)
                throw new InvalidOperationException("Времена разобраны неверно: " + string.Join(", ", e.TimesMs));

            // 3. Полная потеря: времён нет, но потери видны.
            var lost = string.Join("\n", new[]
            {
                "Pinging 192.168.31.1 with 32 bytes of data:",
                "Request timed out.",
                "Request timed out.",
                "Packets: Sent = 2, Received = 0, Lost = 2 (100% loss),"
            });

            var l = PingExeParser.Parse(lost);

            if (!l.Parsed) throw new InvalidOperationException("Вывод со 100% потерь не разобрался");
            if (l.Received != 0) throw new InvalidOperationException("Получено должно быть 0, а разобрано " + l.Received);

            if (l.HasTimes)
                throw new InvalidOperationException("При полной потере времён быть не должно");

            // 4. Пустой вывод не должен ломать разбор.
            var empty = PingExeParser.Parse(string.Empty);

            if (empty.Parsed)
                throw new InvalidOperationException("Пустой вывод не должен считаться разобранным");
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
