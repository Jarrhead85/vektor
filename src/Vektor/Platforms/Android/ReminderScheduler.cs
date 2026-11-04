using Android.App;
using Android.Content;
using Vektor.Core;

namespace Vektor.Platforms.Android;

/// <summary>
/// Exact alarm (ТЗ §8): AlarmManager.SetExactAndAllowWhileIdle при CanScheduleExactAlarms.
/// Без права exact — явный приблизительный режим (окно 15 минут), пользователь предупреждается.
/// Identity: ОДИН стабильный requestCode на пункт; версия и triggerat передаются в intent extras;
/// AlarmReceiver сверяет identity через ReminderRules (stale/чужой intent НЕ срабатывает).
/// Alarm identity не содержит текста задачи.
/// </summary>
public enum ReminderScheduleStatus
{
    ExactScheduled,
    ApproximateScheduled,
    NotAllowed,
}

public static class ReminderScheduler
{
    public const int ReminderNotificationId = 42;
    public const string ExtraVersion = "vektor.reminder.version";
    public const string ExtraTriggerAt = "vektor.reminder.triggerAt";
    public const string TestReminderAction = "vektor.intent.TEST_REMINDER";

    private const string PrefsFile = "vektor_reminders";
    private const string KeyVersion = "one_off_version";
    private const string KeyTriggerAt = "one_off_trigger_at";
    private const long ApproximateWindowMs = 15 * 60 * 1000;
    private const int OneOffRequestId = 1; // стабильный: одна сущность расписания = один requestCode

    public static bool CanScheduleExact(Context context) =>
        ((AlarmManager)context.GetSystemService(Context.AlarmService)!).CanScheduleExactAlarms();

    public static ReminderIdentity? CurrentIdentity(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefsFile, FileCreationMode.Private);
        var version = prefs?.GetInt(KeyVersion, 0) ?? 0;
        if (version <= 0)
        {
            return null;
        }
        return new ReminderIdentity(version, prefs?.GetLong(KeyTriggerAt, 0L) ?? 0L);
    }

    /// <summary>Тестовый разовый напоминатель спайка: +delay. Замена предыдущего alarm той же identity-сущностью.</summary>
    public static ReminderScheduleStatus ScheduleOneOff(Context context, TimeSpan delay)
    {
        var triggerAt = Java.Lang.JavaSystem.CurrentTimeMillis() + (long)delay.TotalMilliseconds;
        var version = (CurrentIdentity(context)?.Version ?? 0) + 1;
        var identity = new ReminderIdentity(version, triggerAt);

        // Спайк: prefs хранит расписание; крипто-этап 4 заменит на защищённое хранилище.
        var prefs = context.GetSharedPreferences(PrefsFile, FileCreationMode.Private);
        prefs?.Edit()?.PutInt(KeyVersion, version)?.PutLong(KeyTriggerAt, triggerAt)?.Apply();

        // UpdateCurrent c тем же стабильным requestCode заменяет предыдущий PendingIntent/alarm.
        AlarmManagerSetWithStatus(context, identity);

        return Status(context, identity);
    }

    /// <summary>Отмена: эффективная — bump version (старый intent stale) + explicit cancel alarm.</summary>
    public static void CancelOneOff(Context context)
    {
        var current = CurrentIdentity(context);
        if (current is null)
        {
            return;
        }

        // Отменить физический alarm: PendingIntent с НАСТОЯЩИМИ (не багажными) extras, UpdateCurrent + Cancel.
        var pending = PendingIntent.GetBroadcast(
            context,
            OneOffRequestId,
            NewIntent(context, current),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent | PendingIntentFlags.NoCreate);
        if (pending is not null)
        {
            var alarmManager = (AlarmManager)context.GetSystemService(Context.AlarmService)!;
            alarmManager.Cancel(pending);
            pending.Cancel();
        }

        // Bump version: любые оставшиеся в системе копии intent становятся stale для receiver.
        var prefs = context.GetSharedPreferences(PrefsFile, FileCreationMode.Private);
        var newVersion = current.Version + 1;
        prefs?.Edit()?.PutInt(KeyVersion, newVersion)?.Remove(KeyTriggerAt)?.Apply();
    }

    /// <summary>BOOT_COMPLETED: перепланирование после reboot (спайк — без ожидания unlock базы).</summary>
    public static void RescheduleAfterReboot(Context context)
    {
        var identity = CurrentIdentity(context);
        if (identity is null)
        {
            return;
        }
        if (identity.TriggerAtMs <= Java.Lang.JavaSystem.CurrentTimeMillis())
        {
            // Просроченный пункт не таскается на постоянку; сводка просроченных — этап 7.
            context.GetSharedPreferences(PrefsFile, FileCreationMode.Private)?
                .Edit()?.PutInt(KeyVersion, identity.Version + 1)?.Remove(KeyTriggerAt)?.Apply();
            return;
        }

        AlarmManagerSetWithStatus(context, identity);
    }

    private static void AlarmManagerSetWithStatus(Context context, ReminderIdentity identity)
    {
        var alarmManager = (AlarmManager)context.GetSystemService(Context.AlarmService)!;
        var pending = PendingIntent.GetBroadcast(
            context,
            OneOffRequestId,
            NewIntent(context, identity),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

        if (alarmManager.CanScheduleExactAlarms())
        {
            alarmManager.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, identity.TriggerAtMs, pending);
            return;
        }

        // Точный тайминг недоступен: подписываем приблизительный режим и НЕ подписываем его как точный.
        alarmManager.SetWindow(AlarmType.RtcWakeup, identity.TriggerAtMs, ApproximateWindowMs, pending);
    }

    private static ReminderScheduleStatus Status(Context context, ReminderIdentity identity)
    {
        if (!CanScheduleExact(context))
        {
            return ReminderScheduleStatus.NotAllowed;
        }
        return identity.Version > 0 ? ReminderScheduleStatus.ExactScheduled : ReminderScheduleStatus.NotAllowed;
    }

    private static Intent NewIntent(Context context, ReminderIdentity identity)
    {
        var intent = new Intent(context, typeof(AlarmReceiver)).SetAction(TestReminderAction);
        intent.PutExtra(ExtraVersion, identity.Version);
        intent.PutExtra(ExtraTriggerAt, identity.TriggerAtMs);
        return intent;
    }
}

[BroadcastReceiver(
    Name = "com.vektor.AlarmReceiver",
    Enabled = true,
    Exported = false)]
public sealed class AlarmReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null)
        {
            return;
        }

        NotificationChannels.Ensure(context);

        var version = intent.GetIntExtra(ReminderScheduler.ExtraVersion, 0);
        var triggerAt = intent.GetLongExtra(ReminderScheduler.ExtraTriggerAt, 0L);
        var incoming = new ReminderIdentity(version, triggerAt);
        var current = ReminderScheduler.CurrentIdentity(context);

        // Проверка актуальности: stale (после отмены/замены/ребут-удаления) и «ранний» intent отбрасываются.
        if (!ReminderRules.IsApplicable(incoming, current, Java.Lang.JavaSystem.CurrentTimeMillis()))
        {
            return; // честный no-op: напоминание устарело, не объявляем «точному сработало»
        }

        // Полный текст напоминания разрешён (D07); настройка скрытия — настройки профиля на этапе 5.
        var mode = ReminderScheduler.CanScheduleExact(context) ? "exact" : "окно ≤15 мин";
        Notifications.Post(context, ReminderScheduler.ReminderNotificationId,
            NotificationChannels.Results, "Вектор: напоминание (spike)",
            $"сработало уведомление-напоминание в {DateTime.Now:HH:mm:ss} ({mode}); identity.version={incoming.Version}");
    }
}

[BroadcastReceiver(
    Name = "com.vektor.BootReceiver",
    Enabled = true,
    Exported = true,
    Permission = "android.permission.RECEIVE_BOOT_COMPLETED")]
[IntentFilter(new[] { Intent.ActionBootCompleted })]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null)
        {
            return;
        }

        if (intent?.Action == Intent.ActionBootCompleted)
        {
            ReminderScheduler.RescheduleAfterReboot(context);
        }
    }
}
