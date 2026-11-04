using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;

namespace Vektor.Platforms.Android;

/// <summary>
/// Виджет 1x2: кнопка микрофона → WidgetQuickCapture (источник фиксирует сервис).
/// Повторное нажатие той же кнопки = stop (логика Toggle в сервисе).
/// Из фона Activity не открываем: результат приходит уведомлением.
/// </summary>
[BroadcastReceiver(
    Name = "com.vektor.VektorWidgetProvider",
    Label = "Вектор",
    Enabled = true,
    Exported = true)]
[IntentFilter(new[] { AppWidgetManager.ActionAppwidgetUpdate })]
[MetaData("android.appwidget.provider", Resource = "@xml/widget_vektor_info")]
public sealed class VektorWidgetProvider : AppWidgetProvider
{
    private const int WidgetToggleRequestId = 2;

    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null)
        {
            return;
        }

        var views = new RemoteViews(context.PackageName, Resource.Layout.widget_vektor);

        var pending = PendingIntent.GetForegroundService(
            context,
            WidgetToggleRequestId,
            VoiceIntents.WidgetToggleIntent(context),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        views.SetOnClickPendingIntent(Resource.Id.widget_mic_button, pending);

        appWidgetManager.UpdateAppWidget(appWidgetIds, views);
    }
}
