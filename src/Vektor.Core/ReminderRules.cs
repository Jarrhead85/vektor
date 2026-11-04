namespace Vektor.Core;

/// <summary>Идентичность напоминания: версия расписания + абсолютныйTriggerAt (UTC ms). Календарное время.</summary>
public sealed record ReminderIdentity(int Version, long TriggerAtMs);

/// <summary>
/// Чистые правила приёма intent напоминания (unit-тестируемо, без Android):
/// -_version текущего расписания; stale/deleted intent не срабатывает;
/// - точная identity (version + triggerAt) должна совпадать — чужой/подменённый intent отбрасывается;
/// - receiver не должен сработать раньше времени раннее допуска (защита от очень раннего fire окна).
/// </summary>
public static class ReminderRules
{
    /// <summary>Допуск: receiver может прийти чуть раньше exact (норма для deliver-иwindows) максимум на 30 с.</summary>
    public const long EarlyFireToleranceMs = 30_000;

    public static bool IsApplicable(ReminderIdentity incoming, ReminderIdentity? current, long nowMs)
    {
        if (current is null || incoming.Version <= 0)
        {
            return false;
        }
        if (incoming.Version != current.Version || incoming.TriggerAtMs != current.TriggerAtMs)
        {
            return false; // устаревший/чужой alarm: version не актуальна
        }
        return nowMs >= incoming.TriggerAtMs - EarlyFireToleranceMs; // не сработает задолго до срока
    }
}
