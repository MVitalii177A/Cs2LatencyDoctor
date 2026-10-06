using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Потери пакетов отдельной проверкой.
///
/// Почему отдельной. Задержку и потери часто путают, а в игре это разные беды:
/// ровные 60 мс играются нормально, а 2% потерь при 20 мс — это рывки, «телепорты»
/// противника и пропавшие попадания. Раньше потери считались попутно внутри замера
/// задержки, и на них никто не смотрел.
///
/// Считаем честно: подключение раз в секунду, неудачное подключение — потеря.
/// Чаще нельзя: роутеры и серверы воспринимают частые подключения как атаку
/// и сами начинают отвечать рывками, создавая ложную картину.
/// </summary>
public sealed class NetworkLossCheck : IDiagnosticCheck
{
    public string Id => "net.loss";
    public string Title => "Потери пакетов";
    public bool RequiresAdmin => false;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        context.Progress("Проверяю потери пакетов…");

        var target = ProbeTargets.External[0];

        // Число попыток считает общий оценщик: на пяти нельзя отличить случайность от потерь.
        var attempts = LossEvaluator.AttemptsFor(context.ProbeSeconds);

        var failures = 0;
        var errors = new List<string>();

        for (var i = 0; i < attempts && !ct.IsCancellationRequested; i++)
        {
            var (sample, _) = await TcpConnectProbe.TryConnectAsync(target.Host, timeoutMs: 1500, ct);

            if (!sample.Success)
            {
                failures++;
                if (sample.Error is not null && errors.Count < 3) errors.Add(sample.Error);
            }

            context.Progress($"Потери: попытка {i + 1} из {attempts}…");

            if (i < attempts - 1)
            {
                try { await Task.Delay(1000, ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        var outcome = LossEvaluator.Evaluate(attempts, failures);

        // Ни одно подключение не прошло — это не «100% потерь», а «измерить не вышло».
        // Правило проекта: не выдавать отговорку за диагноз.
        if (outcome == LossEvaluator.Outcome.Inconclusive)
        {
            results.Add(CheckResult.Skipped(Id, Title,
                $"Ни одно из {attempts} подключений к {target.Host} не прошло",
                NoHelpReason.BlockedBySystem,
                "Программа не может измерить потери: узел не отвечает вообще. Это не значит " +
                "«100% потерь» — скорее всего соединения блокирует фаервол, антивирус или " +
                "провайдер. Что делать: проверьте, открывается ли сайт в браузере. " +
                "Если интернет работает, а программа не может подключиться — " +
                $"попробуйте временно отключить антивирус или проверьте доступ к {target.Host}."));
            return results;
        }

        var lossPercent = LossEvaluator.Percent(attempts, failures);
        var detail = $"Не дошло {failures} из {attempts} подключений ({lossPercent:0.#}%)";

        var metrics = new Dictionary<string, double>
        {
            ["loss_percent"] = lossPercent,
            ["loss_attempts"] = attempts,
            ["loss_failed"] = failures
        };

        if (outcome == LossEvaluator.Outcome.Clean)
        {
            results.Add(new CheckResult
            {
                Id = Id,
                Title = Title,
                Severity = Severity.Ok,
                Detail = detail,
                Why = "Потери — это рывки и пропавшие попадания, и они мешают сильнее, " +
                      "чем лишние миллисекунды ровной задержки.",
                Metrics = metrics
            });

            return results;
        }

        var errorText = errors.Count > 0
            ? " Первые ошибки: " + string.Join("; ", errors) + "."
            : string.Empty;

        if (outcome == LossEvaluator.Outcome.Lossy)
        {
            results.Add(new CheckResult
            {
                Id = Id,
                Title = Title,
                Severity = Severity.Problem,
                Detail = detail + errorText,
                Why = "Каждый потерянный пакет в игре — это рывок: позиции противника обновляются " +
                      "с опозданием, попадания не засчитываются. Потери в пару процентов " +
                      "ощущаются хуже, чем лишние 20 мс ровной задержки.",
                NoHelpReason = NoHelpReason.OutsideThisPc,
                Recommendation = "Программа не может исправить потери: это участок вне компьютера. " +
                                 "Что делать по порядку: " +
                                 "1) посмотрите проверку «Задержка до роутера»: если потери есть и там, " +
                                 "виноват кабель, порт роутера или сама сетевая карта; " +
                                 "2) проверьте, не занят ли канал — торренты, обновления Windows, " +
                                 "облачные синхронизаторы (кнопка «Фоновые программы»); " +
                                 "3) перезагрузите роутер, вынув его из розетки на 30 секунд; " +
                                 "4) позвоните провайдеру и назовите цифры из этой проверки: " +
                                 $"«{failures} потерь из {attempts} подключений» — с числами разговор идёт иначе.",
                Metrics = metrics,
                Fixes = new[]
                {
                    new FixAction("net.loss.check-cable", "Проверить кабель и порт роутера",
                        FixRisk.ManualOnly, "Требуется физическое действие."),
                    new FixAction("net.loss.call-provider", "Обратиться к провайдеру с цифрами",
                        FixRisk.ManualOnly, "Этот участок вне компьютера.")
                }
            });
        }
        else
        {
            // Меньше процента, но не ноль: бывает разовое совпадение, а бывает начало проблемы.
            results.Add(new CheckResult
            {
                Id = Id,
                Title = Title,
                Severity = Severity.Info,
                Detail = detail + errorText,
                Why = "Единичная потеря обычно случайна: узел мог быть занят или пакет попал " +
                      "в перегрузку. Но если такое повторяется из замера в замер — это начало проблемы.",
                Recommendation = "Пока делать ничего не нужно. Запустите проверку ещё раз через " +
                                 "несколько минут: если потери повторятся, смотрите рекомендации " +
                                 "для этого случая — они появятся автоматически.",
                Metrics = metrics
            });
        }

        return results;
    }
}
