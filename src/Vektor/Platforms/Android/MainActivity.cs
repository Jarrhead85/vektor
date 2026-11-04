using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Vektor.Core;
using Vektor.Platforms.Android;

namespace Vektor.Platforms.Android;

[Activity(
    Label = "Вектор",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode)]
public sealed class MainActivity : MauiAppCompatActivity
{
    private const int RecordAudioRequestCode = 1101;
    private const int PostNotificationsRequestCode = 1102;

    public void StartAppCapture()
    {
        // Разрешения раздельные: микрофон — единственный обязательный для записи,
        // уведомления нужны для Stop-кнопки/результатов в фоне (не блокируют старт записи приложения).
        if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            RequestPermissions([global::Android.Manifest.Permission.RecordAudio], RecordAudioRequestCode);
            return;
        }

        if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            // Уведомления НЕ блокирует старт записи: предлагаем выдать, но запись продолжаем без него.
            RequestPermissions([global::Android.Manifest.Permission.PostNotifications], PostNotificationsRequestCode);
            AppStatusStore.Instance.SetInfo("Уведомления не выданы: Stop-кнопка в фоне будет недоступна, запись всё равно идёт.");
        }

        LaunchCapture();
    }

    public void StopCapture()
    {
        StartService(VoiceIntents.StopIntent(this));
    }

    public void ScheduleTestReminder()
    {
        if (!ReminderScheduler.CanScheduleExact(this))
        {
            // Честный статус: нет права exact — ведём в системные настройки, не имитируем точное напоминание.
            AppStatusStore.Instance.SetError("Система не разрешает exact alarms: напоминание будет с окном до 15 минут. Разрешение — в настройках.");
            StartActivity(new Intent(global::Android.Provider.Settings.ActionRequestScheduleExactAlarm));
            return;
        }

        var status = ReminderScheduler.ScheduleOneOff(this, TimeSpan.FromMinutes(1));
        if (status == ReminderScheduleStatus.ExactScheduled)
        {
            AppStatusStore.Instance.SetInfo("Напоминание через 1 минуту (exact alarm).");
        }
        else
        {
            AppStatusStore.Instance.SetError("Напоминание запланировано приблизительно (окно ≤15 мин) — точного ожидания не гарантируем.");
        }
    }

    /// <summary>Отмена тестового напоминания: bump версии + cancel alarm (не маскируем как «точное прошло»).</summary>
    public void CancelTestReminder()
    {
        ReminderScheduler.CancelOneOff(this);
        AppStatusStore.Instance.SetInfo("Тестовое напоминание отменено (identity инвалидирована).");
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (grantResults.Length == 0 || grantResults.All(g => g != Permission.Granted))
        {
            switch (requestCode)
            {
                case RecordAudioRequestCode:
                    // Только экран, не молчание: уведомления может не быть — но приложение открыто.
                    AppStatusStore.Instance.SetError("Микрофон запрещён: запись не началась. Разрешение — в настройках приложения.");
                    break;

                case PostNotificationsRequestCode:
                    Notifications.Post(this, 104, NotificationChannels.Results,
                        "Вектор: нет разрешений на уведомления",
                        "Без них Stop-кнопка в фоне и напоминания не отобразятся.");
                    AppStatusStore.Instance.SetError("Уведомления запрещены: Stop-кнопка в фоне и результат уведомлением не придут.");
                    break;
            }
        }
        else if (requestCode == RecordAudioRequestCode)
        {
            // Выдали микрофон — старт записи продолжим без немедленного resend POST_NOTIFICATIONS.
            LaunchCapture();
        }
    }

    private void LaunchCapture()
    {
        // Одна сессия: если уже идёт запись — второй старт просто игнорируется сервисом.
        if (VoiceSessionStore.Instance.Running)
        {
            return;
        }
        AppStatusStore.Instance.ClearError();
        AppStatusStore.Instance.SetInfo($"Запись начата ({CaptureSource.AppManualCapture}); стоп — вручную.");
        StartForegroundService(VoiceIntents.CaptureIntent(this, CaptureSource.AppManualCapture));
    }
}
