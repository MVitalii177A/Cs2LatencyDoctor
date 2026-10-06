using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Fixes;

/// <summary>
/// Переключает CS2 в полноэкранный (exclusive) режим.
///
/// Важная тонкость: сочетание fullscreen=1 + nowindowborder=1 — это «полноэкранный оконный»,
/// то есть всё тот же путь через композитор Windows. Настоящий exclusive требует
/// nowindowborder=0. Поэтому правим оба значения, а не одно.
///
/// Вторая тонкость: игра перезаписывает cs2_video.txt при выходе. Если пользователь
/// после правки зайдёт в настройки и выберет режим вручную — значение будет перезаписано
/// на то, что он выбрал. Это не ошибка отката, а поведение игры.
/// </summary>
public sealed class Cs2DisplayModeFix : FixBase
{
    public override string Id => "cs2.display.exclusive";
    public override string Title => "Переключить CS2 в полноэкранный (exclusive) режим";

    public override bool CanApply(DiagnosticContext context)
    {
        if (!base.CanApply(context)) return false;

        var install = Cs2Locator.Find();
        if (install?.VideoSettings is null) return false;

        // Игра должна быть закрыта: иначе перезапишет файл при выходе.
        return !IsGameRunning();
    }

    public override string? SkipReason(DiagnosticContext context)
    {
        if (!context.IsAdministrator) return "Нужны права администратора";
        if (IsGameRunning()) return "CS2 запущена — закройте игру, иначе настройка будет перезаписана";

        var install = Cs2Locator.Find();
        if (install is null) return "CS2 не найдена на этом компьютере";
        if (install.VideoSettings is null) return "Файл настроек видео ещё не создан — запустите игру один раз";

        var video = install.VideoSettings;
        if (video.Fullscreen == 1 && video.NoWindowBorder == 0)
            return "Уже настроено правильно";

        return null;
    }

    public override FixResult Apply(DiagnosticContext context, UndoJournal journal, FixPlan plan)
    {
        var install = Cs2Locator.Find();
        if (install?.VideoSettings is null)
            return FixResult.Skipped(Id, Title, "CS2 не найдена или файл настроек отсутствует");

        if (IsGameRunning())
            return FixResult.Skipped(Id, Title, "CS2 запущена — закройте игру и повторите");

        var video = install.VideoSettings;
        if (video.Fullscreen == 1 && video.NoWindowBorder == 0)
            return FixResult.AlreadyOk(Id, Title, "Уже полноэкранный (exclusive)");

        var changes = new List<JournalEntry>();
        var path = video.FilePath;

        // Резервная копия файла целиком: настройки видео — единый документ,
        // и точечная правка двух строк не даёт полной страховки.
        var backupPath = path + ".cs2latencydoc-backup";
        try
        {
            if (!File.Exists(backupPath)) File.Copy(path, backupPath);
        }
        catch (Exception ex)
        {
            return FixResult.Failed(Id, Title, "Не удалось создать резервную копию: " + ex.Message);
        }

        try
        {
            var content = File.ReadAllText(path);
            var updated = Cs2VideoFileEditor.SetFullscreenExclusive(content);

            if (updated == content)
                return FixResult.AlreadyOk(Id, Title, "Файл уже содержит нужные значения");

            WritePreservingEncoding(path, updated);
        }
        catch (Exception ex)
        {
            return FixResult.Failed(Id, Title, "Не удалось записать настройки: " + ex.Message);
        }

        // Проверяем по факту, а не по факту отсутствия исключения.
        var check = Cs2Locator.ReadVideoSettings(install.SteamRoot, install.SteamUserId ?? string.Empty);
        if (check is null || check.Fullscreen != 1 || check.NoWindowBorder != 0)
            return FixResult.Failed(Id, Title, "Настройки записаны, но не читаются обратно — проверьте вручную");

        changes.Add(new JournalEntry
        {
            FixId = Id,
            Title = "Режим экрана CS2: exclusive fullscreen",
            Kind = "cs2VideoFile",
            Location = path,
            Name = "fullscreen+nowindowborder",
            OldValue = $"fullscreen={video.Fullscreen};nowindowborder={video.NoWindowBorder}",
            NewValue = "fullscreen=1;nowindowborder=0"
        });

        journal.Add(changes[0]);

        var warning = IsStretchedRisk(video)
            ? " Внимание: в этом режиме масштабированием занимается видеокарта. " +
              "Если разрешение не совпадает с разрешением монитора, проверьте в панели NVIDIA: " +
              "режим масштабирования = «Полноэкранный», выполнять на GPU."
            : string.Empty;

        return FixResult.Applied(Id, Title,
            "CS2 переключена в полноэкранный (exclusive) режим. Резервная копия: " +
            Path.GetFileName(backupPath) + "." + warning, changes);
    }

    public override bool Revert(JournalEntry entry, DiagnosticContext context)
    {
        if (entry.Kind != "cs2VideoFile") return false;

        try
        {
            var backupPath = entry.Location + ".cs2latencydoc-backup";
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, entry.Location, overwrite: true);
                return true;
            }

            // Резервной копии нет — правим значения обратно вручную.
            var content = File.ReadAllText(entry.Location);
            var parts = entry.OldValue.Split(';');
            var fullscreen = parts.Length > 0 ? parts[0].Replace("fullscreen=", "") : "0";
            var border = parts.Length > 1 ? parts[1].Replace("nowindowborder=", "") : "1";

            var restored = Cs2VideoFileEditor.SetValues(content, fullscreen, border);
            WritePreservingEncoding(entry.Location, restored);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WritePreservingEncoding(string path, string content)
    {
        // cs2_video.txt — текст без BOM. Пишем так же, чтобы игра не спотыкалась.
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static bool IsGameRunning() =>
        System.Diagnostics.Process.GetProcessesByName("cs2").Length > 0;

    /// <summary>Разрешение не совпадает с типичным 16:9 — значит картинка растягивается или кропится.</summary>
    private static bool IsStretchedRisk(Cs2VideoSettings video)
    {
        if (video.ResolutionWidth <= 0 || video.ResolutionHeight <= 0) return false;

        var aspect = (double)video.ResolutionWidth / video.ResolutionHeight;
        return Math.Abs(aspect - 16.0 / 9.0) > 0.02;
    }
}
