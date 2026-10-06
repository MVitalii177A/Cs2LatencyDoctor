namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>Узел для замера: адрес и понятное название.</summary>
/// <param name="Host">Адрес или имя узла.</param>
/// <param name="Title">Как называть его в отчёте.</param>
public sealed record ProbeTarget(string Host, string Title);

/// <summary>
/// Куда программа замеряет задержку.
///
/// Одного адреса мало: маршрут до него ничего не говорит о направлении, в котором
/// находится игровой сервер. Три независимых направления в разных сетях дают картину:
/// если плохо везде — вопрос к своему каналу, если в одном месте — к тому участку.
/// </summary>
public static class ProbeTargets
{
    /// <summary>
    /// Адреса выбраны так, чтобы это были разные сети и разные операторы:
    /// Cloudflare, Google и Quad9 находятся в разных местах, поэтому один общий
    /// участок проблемы виден сразу, а локальная авария у одного из них — нет.
    /// </summary>
    public static readonly IReadOnlyList<ProbeTarget> External = new[]
    {
        new ProbeTarget("1.1.1.1", "Cloudflare"),
        new ProbeTarget("8.8.8.8", "Google"),
        new ProbeTarget("9.9.9.9", "Quad9")
    };
}
