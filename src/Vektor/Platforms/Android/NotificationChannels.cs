using Android.App;
using Android.Content;

namespace Vektor.Platforms.Android;

/// <summary>
/// Единый идемпотентный компонент notification channels.
/// Создание канала уже по API идемпотентно; Ensure вызывается до каждого Post и из MainApplication.OnCreate,
/// чтобы уведомления/AlarmReceiver работали на чистой установке без предварительной записи.
/// </summary>
internal static class NotificationChannels
{
    public const string Recording = "vektor_recording";
    public const string Results = "vektor_results";

    public static void Ensure(Context context)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;

        if (manager.GetNotificationChannel(Recording) is null)
        {
            manager.CreateNotificationChannel(new NotificationChannel(
                Recording, "Запись Вектор", NotificationImportance.Low)
            {
                Description = "Постоянное уведомление активной записи с кнопкой Stop",
            });
        }

        if (manager.GetNotificationChannel(Results) is null)
        {
            manager.CreateNotificationChannel(new NotificationChannel(
                Results, "Результаты Вектор", NotificationImportance.Default)
            {
                Description = "Результаты записи и напоминания",
            });
        }
    }
}
