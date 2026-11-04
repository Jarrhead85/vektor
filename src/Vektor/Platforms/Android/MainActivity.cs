using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Vektor.Core;

namespace Vektor.Platforms.Android;

[Activity(
    Label = "Вектор",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode)]
public sealed class MainActivity : MauiAppCompatActivity
{
    private const int PermissionRequestCode = 1001;

    public void StartAppCapture()
    {
        // AppManualCapture: интерактивный запуск, значит activity имеет законный пользовательский запуск записи.
        var required = new[]
        {
            global::Android.Manifest.Permission.RecordAudio,
            global::Android.Manifest.Permission.PostNotifications,
        };

        if (required.All(p => CheckSelfPermission(p) == Permission.Granted))
        {
            LaunchCapture();
            return;
        }

        RequestPermissions(required, PermissionRequestCode);
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
            StartActivity(new Intent(global::Android.Provider.Settings.ActionRequestScheduleExactAlarm));
            return;
        }

        var status = ReminderScheduler.ScheduleOneOff(this, TimeSpan.FromMinutes(1));
        var message = status == ReminderScheduleStatus.ExactScheduled
            ? "Exact alarm установлен на текущее время + 1 минута"
            : $"Приблизительный режим (конец окна ~15 мин): {status}";
        Notifications.Post(this, 105, VoiceSessionService.ChannelResults, "Напоминание запланировано", message);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode != PermissionRequestCode)
        {
            return;
        }

        if (grantResults.Length > 0 && grantResults.All(g => g == Permission.Granted))
        {
            LaunchCapture();
        }
        else
        {
            Notifications.Post(this, 104, VoiceSessionService.ChannelResults, "Разрешения не выданы",
                "Без микрофона запись не начнётся. Разрешение можно выдать в настройках приложения.");
        }
    }

    private void LaunchCapture()
    {
        // Одна сессия: если уже идёт запись — второй старт просто игнорируется сервисом.
        if (!VoiceSessionStore.Instance.Running)
        {
            StartForegroundService(VoiceIntents.CaptureIntent(this, CaptureSource.AppManualCapture));
        }
    }
}
