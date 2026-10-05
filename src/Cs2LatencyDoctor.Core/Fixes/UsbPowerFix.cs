using Cs2LatencyDoctor.Core.Windows;
using Microsoft.Win32;

namespace Cs2LatencyDoctor.Core.Fixes;

/// <summary>
/// Запрещает Windows отключать USB-порты «для экономии энергии».
/// Иначе мышь и клавиатура выходят из спящего режима с задержкой в первые
/// миллисекунды движения — это чувствуется как «первый флик не туда».
/// </summary>
public sealed class UsbPowerFix : FixBase
{
    private const string UsbSubGroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSuspendSetting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    public override string Id => "power.usb.suspend";
    public override string Title => "Запретить отключение USB-портов для экономии энергии";

    public override FixResult Apply(DiagnosticContext context, UndoJournal journal)
    {
        var state = PowerConfigReader.Read();
        if (state.UsbSelectiveSuspendAc is null && state.UsbSelectiveSuspendDc is null)
            return FixResult.Skipped(Id, Title, "Параметр недоступен в текущей схеме электропитания");

        var changes = new List<JournalEntry>();
        var applied = new List<string>();

        foreach (var (power, oldValue) in new[]
                 {
                     ("AC", state.UsbSelectiveSuspendAc),
                     ("DC", state.UsbSelectiveSuspendDc)
                 })
        {
            if (oldValue is null) continue;

            if (oldValue == 0)
            {
                applied.Add($"{power}: уже запрещено");
                continue;
            }

            var argument = power == "AC" ? "/setacvalueindex" : "/setdcvalueindex";
            if (!RunPowerCfg($"{argument} SCHEME_CURRENT {UsbSubGroup} {UsbSuspendSetting} 0"))
            {
                return FixResult.Failed(Id, Title, $"powercfg не принял настройку {power}");
            }

            changes.Add(new JournalEntry
            {
                FixId = Id,
                Title = $"Отключение USB-портов ({power})",
                Kind = "powercfg",
                Location = $"{UsbSubGroup}/{UsbSuspendSetting}",
                Name = power,
                OldValue = oldValue.Value.ToString(),
                NewValue = "0"
            });

            applied.Add($"{power}: запрещено");
        }

        // Активируем схему, иначе изменения не применятся.
        RunPowerCfg("/setactive SCHEME_CURRENT");

        var check = PowerConfigReader.Read();
        if (check.UsbSelectiveSuspendAc != 0 || check.UsbSelectiveSuspendDc != 0)
        {
            return FixResult.Failed(Id, Title,
                "Настройка записана, но не применилась — проверьте схему электропитания вручную");
        }

        if (changes.Count == 0)
            return FixResult.AlreadyOk(Id, Title, "Отключение USB-портов уже запрещено");

        foreach (var change in changes) journal.Add(change);
        return FixResult.Applied(Id, Title, string.Join(", ", applied), changes);
    }

    public override bool Revert(JournalEntry entry, DiagnosticContext context)
    {
        if (entry.Kind != "powercfg") return false;

        var argument = entry.Name == "AC" ? "/setacvalueindex" : "/setdcvalueindex";
        if (!RunPowerCfg($"{argument} SCHEME_CURRENT {UsbSubGroup} {UsbSuspendSetting} {entry.OldValue}"))
            return false;

        RunPowerCfg("/setactive SCHEME_CURRENT");

        var state = PowerConfigReader.Read();
        var actual = entry.Name == "AC" ? state.UsbSelectiveSuspendAc : state.UsbSelectiveSuspendDc;
        return actual?.ToString() == entry.OldValue;
    }

    private static bool RunPowerCfg(string arguments)
    {
        var output = LatencyProbe
            .RunProcessAsync("powercfg.exe", arguments, CancellationToken.None)
            .GetAwaiter().GetResult();

        // powercfg при ошибке пишет в stderr и возвращает код; пустой вывод = успех.
        return output is not null;
    }
}
