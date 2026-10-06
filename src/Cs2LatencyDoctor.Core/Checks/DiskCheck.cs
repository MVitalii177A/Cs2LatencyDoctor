using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Диск, на котором стоит игра.
///
/// Зачем это в инструменте про задержки. Механический диск и переполненный
/// твердотельный дают не «низкий FPS», а подгрузки: игра останавливается на доли
/// секунды, чтобы дочитать текстуры, и это чувствуется как рывок ровно в бою.
/// К сетевой задержке отношения не имеет, но жалобы звучат одинаково.
/// </summary>
public sealed class DiskCheck : IDiagnosticCheck
{
    public string Id => "disk.game";
    public string Title => "Диск, на котором стоит игра";
    public bool RequiresAdmin => false;

    /// <summary>Ниже этого процента свободного места твердотельный диск начинает тормозить.</summary>
    private const double LowFreeSpacePercent = 10.0;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        context.Progress("Смотрю, на каком диске стоит игра…");

        var install = context.GetCs2Installation();
        var volumes = HardwareReader.ReadVolumes();
        var disks = HardwareReader.ReadDisks();

        if (volumes.Count == 0)
        {
            results.Add(CheckResult.Skipped(Id, Title,
                "Не удалось прочитать список дисков",
                NoHelpReason.BlockedBySystem,
                "Программа не смогла получить сведения о дисках. Обычная причина — " +
                "служба WMI не отвечает или доступ к ней ограничен. Что делать: " +
                "1) откройте «Этот компьютер» и проверьте, что диски видны; " +
                "2) если диск не отображается и там, дело не в программе — " +
                "проверьте подключение и «Управление дисками»; " +
                "3) перезагрузите компьютер: служба WMI часто оживает после перезагрузки."));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        // Диск игры: если игра не найдена, проверяем системный — это тоже полезно.
        var letter = 'C';

        if (install is not null && !string.IsNullOrEmpty(install.GameFolder))
        {
            var root = Path.GetPathRoot(install.GameFolder);
            if (!string.IsNullOrEmpty(root)) letter = root[0];
        }

        var volume = volumes.FirstOrDefault(v => v.Letter == letter);
        if (volume is null)
        {
            volumes = volumes.OrderBy(v => v.Letter).ToList();
            volume = volumes[0];
        }

        var disk = volume.PhysicalModel is null
            ? null
            : disks.FirstOrDefault(d => d.Model == volume.PhysicalModel);

        var diskKind = disk?.KindText ?? "тип определить не удалось";

        context.Progress("Проверяю свободное место…");

        // --- Свободное место ---
        if (volume.TotalBytes > 0)
        {
            var detail = $"Диск {volume.Letter}: — {diskKind}, свободно {volume.FreeText}";

            if (volume.FreePercent < LowFreeSpacePercent)
            {
                results.Add(CheckResult.Warn(Id + ".space", "Свободное место на диске с игрой",
                    detail,
                    "Когда на диске мало места, система не может быстро записать временные файлы " +
                    "и подготовить данные заранее. В игре это подгрузки и рывки при появлении " +
                    "новых объектов на карте.",
                    new[]
                    {
                        new FixAction("disk.free-space", "Освободить место на диске", FixRisk.ManualOnly,
                            "Программа не удаляет файлы пользователя — это небезопасно.")
                    },
                    "Программа не может освободить место за вас: удалять чужие файлы — не её дело. " +
                    "Что делать: 1) в «Параметры → Система → Память» посмотрите, что занимает место " +
                    "(обычно это старые обновления Windows и временные файлы); " +
                    "2) очистите «Загрузки» и «Корзину»; " +
                    "3) перенесите на другой диск игры и фильмы — в Steam это делается через " +
                    "«Свойства → Установленные файлы → Переместить»; " +
                    $"4) цель — держать свободными хотя бы {LowFreeSpacePercent:0}% диска."));
            }
            else
            {
                results.Add(new CheckResult
                {
                    Id = Id + ".space",
                    Title = "Свободное место на диске с игрой",
                    Severity = Severity.Ok,
                    Detail = detail,
                    Why = "Свободного места достаточно: система успевает готовить данные заранее, " +
                          "поэтому подгрузок из-за места не будет."
                });
            }
        }

        // --- Тип диска ---
        if (disk?.IsSolidState is false)
        {
            results.Add(CheckResult.Info(Id + ".kind", "Тип диска с игрой",
                $"Игра стоит на механическом диске ({disk.Model}, {disk.SizeText})",
                "Механический диск читает данные головкой, которой нужно физически " +
                "переместиться. Когда игра запрашивает новые текстуры, это занимает десятки " +
                "миллисекунд — и в бою ощущается как рывок. Твердотельный диск отдаёт данные " +
                "почти мгновенно.",
                new[]
                {
                    new FixAction("disk.move-to-ssd", "Перенести игру на твердотельный диск",
                        FixRisk.ManualOnly, "Требуется второй накопитель и свободное место на нём.")
                },
                "Программа не может перенести игру: нужен твердотельный диск. Что делать: " +
                "1) если в компьютере есть SSD, перенесите игру: в Steam это " +
                "«Свойства → Установленные файлы → Переместить», всё скачается заново только " +
                "частично; 2) если SSD один и на нём мало места, перенесите на него именно CS2 — " +
                "она занимает около 30 ГБ; 3) менять диск не обязательно: механический влияет " +
                "на подгрузки, а не на задержку сети, и играть на нём можно."));
        }
        else if (disk?.IsSolidState is true)
        {
            results.Add(CheckResult.Ok(Id + ".kind", "Тип диска с игрой",
                $"Твердотельный диск ({disk.Model}, {disk.SizeText})",
                "Твердотельный диск отдаёт данные без механических задержек: подгрузок " +
                "из-за накопителя быть не должно."));
        }
        else
        {
            // Тип определить не вышло: честно говорим об этом, а не делаем вид, что всё хорошо.
            results.Add(CheckResult.Skipped(Id + ".kind", "Тип диска с игрой",
                $"Диск {volume.Letter}: — {diskKind}",
                NoHelpReason.HardwareNotSupported,
                "Программа не смогла определить, твердотельный это диск или механический: " +
                "система не сообщила тип накопителя. Это обычная ситуация для внешних и " +
                "некоторых USB-накопителей. Что делать: посмотрите модель диска в " +
                "«Диспетчер задач → Производительность → Диск» — там тип указан словами " +
                "«SSD» или «HDD». Если это HDD и игра стоит на нём, подгрузки возможны."));
        }

        // --- Таймаут остановки диска ---
        var timeout = HardwareReader.ReadDiskTimeoutSeconds();

        if (timeout is > 0 and < 20)
        {
            results.Add(CheckResult.Info(Id + ".timeout", "Таймаут остановки диска",
                $"Диск останавливается через {timeout} с простоя",
                "Слишком короткий таймаут заставляет диск засыпать при простое, а первое " +
                "обращение после этого ждёт раскрутки. В игре это рывок при входе на карту.",
                recommendation: "Программа не меняет этот параметр: он влияет на всё " +
                                "устройство целиком, и риск ошибки выше пользы. Что делать: " +
                                "в «Панель управления → Электропитание → Настройка схемы → " +
                                "Изменить дополнительные параметры → Жёсткий диск → Отключить " +
                                "жёсткий диск через» поставьте 0 (никогда) или 20 минут."));
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }
}
