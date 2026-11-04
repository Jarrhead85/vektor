using Android.App;
using Android.Content;

namespace Vektor.Platforms.Android;

/// <summary>
/// Exact alarm (ТЗ §8): AlarmManager.SetExactAndAllowWhileIdle при CanScheduleExactAlarms.
/// Без права exact — явный приблизительный режим (окно 15 минут), пользователь предупреждается.
/// WorkManager и таймер процесса не заменяют точный alarm и не используются.
/// Alarm identity не содержит текста задачи: requestCode фиксирован по пункту, version хранится в prefs.
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

    private const string PrefsFile = "vektor_reminders";
    private const string KeyOneOffTrigger = "one_off_trigger_at";
    private const string KeyOneOffVersion = "one_off_version";
    private const long ApproximateWindowMs = 15 * 60 * 1000;
    private const string TestReminderAction = "vektor.intent.TEST_REMINDER";
    private const int OneOffRequestId = 1;

    public static bool CanScheduleExact(Context context) =>
        ((AlarmManager)context.GetSystemService(Context.AlarmService)!).CanScheduleExactAlarms();

    /// <summary>Тестовый разовый напоминатель спайка: +1 минута (интент → AlarmReceiver).</summary>
    public static ReminderScheduleStatus ScheduleOneOff(Context context, TimeSpan delay)
    {
        var triggerAt = Java.Lang.JavaSystem.CurrentTimeMillis() + (long)delay.TotalMilliseconds;

        // Спайк: prefs хранит trigger-at и версию; крипто-этап 4 заменит на защищённую таблицу расписаний.
        var prefs = context.GetSharedPreferences(PrefsFile, FileCreationMode.Private);
        var version = (prefs?.GetInt(KeyOneOffVersion, 0) ?? 0) + 1;
        prefs?.Edit()?.PutLong(KeyOneOffTrigger, triggerAt)?.PutInt(KeyOneOffVersion, version)?.Apply();

        var pending = PendingIntent.GetBroadcast(
            context,
            OneOffRequestId * version,
            NewOneOffIntent(context),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        return Schedule(context, triggerAt, pending);
    }

    /// <summary>BOOT_COMPLETED: перепланирование после reboot (спайк — без ожидания unlock базы).</summary>
    public static void RescheduleAfterReboot(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefsFile, FileCreationMode.Private);
        var triggerAt = prefs?.GetLong(KeyOneOffTrigger, 0L) ?? 0L;
        if (triggerAt <= Java.Lang.JavaSystem.CurrentTimeMillis())
        {
            // Просроченный пункт не таскается на постоянку, сводка просроченных — продуктом этапа 7.
            prefs?.Edit()?.Remove(KeyOneOffTrigger)?.Apply();
            return;
        }

        var version = prefs?.GetInt(KeyOneOffVersion, 1) ?? 1;
        var pending = PendingIntent.GetBroadcast(
            context,
            OneOffRequestId * version,
            NewOneOffIntent(context),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        _ = Schedule(context, triggerAt, pending);
    }

    private static ReminderScheduleStatus Schedule(Context context, long triggerAt, PendingIntent pending)
    {
        var alarmManager = (AlarmManager)context.GetSystemService(Context.AlarmService)!;

        if (alarmManager.CanScheduleExactAlarms())
        {
            alarmManager.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAt, pending);
            return ReminderScheduleStatus.ExactScheduled;
        }

        // Точный тайминг недоступен: подписываем приблизительный режим и НЕ подписываем его как точный.
        alarmManager.SetWindow(AlarmType.RtcWakeup, triggerAt, ApproximateWindowMs, pending);
        return ReminderScheduleStatus.ApproximateScheduled;
    }

    private static Intent NewOneOffIntent(Context context)
    {
        var intent = new Intent(context, typeof(AlarmReceiver));
        intent.SetAction(TestReminderAction);
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
        // Полный текст напоминания разрешён (D07); настройка скрытия — настройки профиль в этапе 5.
        if (context is not null)
        {
            Notifications.Post(context, ReminderScheduler.ReminderNotificationId,
                VoiceSessionService.ChannelResults, "Вектор: напоминание (spike)",
                $"Сработал тестовый alarm в {DateTime.Now:HH:mm:ss}. Exact — приходит до минуты; окно 15 мин — приблизительный.");
        }
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
