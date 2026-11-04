using Android.App;
using Android.Content;
using Vektor.Platforms.Android;

namespace Vektor.Platforms.Android;

internal static class Notifications
{
    private static PendingIntent? ContentActivity(Context context)
    {
        var launch = new Intent(context, typeof(MainActivity));
        launch.SetAction(Intent.ActionMain);
        launch.AddCategory(Intent.CategoryLauncher);
        return PendingIntent.GetActivity(context, 0, launch,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
    }

    public static void Post(Context context, int id, string channelId, string title, string? text = null)
    {
        // POST_NOTIFICATIONS не выдан — уведомление показать нечем; отказ не должен приводить к краху записи.
        if (context.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)
            != global::Android.Content.PM.Permission.Granted)
        {
            return;
        }

        // Канал на чистой установке может отсутствовать (например, reboot-receiver без первого запуска записи).
        NotificationChannels.Ensure(context);

        var builder = new Notification.Builder(context, channelId)
            .SetSmallIcon(Resource.Drawable.widget_mic)
            .SetContentTitle(title)
            .SetContentIntent(ContentActivity(context))
            .SetAutoCancel(true);

        if (!string.IsNullOrEmpty(text))
        {
            builder.SetContentText(text);
        }

        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.Notify(id, builder.Build());
    }
}
