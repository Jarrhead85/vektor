using Android.Content;
using Vektor.Core;

namespace Vektor.Platforms.Android;

/// <summary>Интенты microphone foreground service. Один обработчик для виджета и приложения.</summary>
internal static class VoiceIntents
{
    public const string ActionStartAppManual = "vektor.intent.START_APP_MANUAL";
    public const string ActionStartWidgetQuick = "vektor.intent.START_WIDGET_QUICK";
    public const string ActionStop = "vektor.intent.STOP";
    public const string ActionWidgetToggle = "vektor.intent.WIDGET_TOGGLE";

    public static Intent CaptureIntent(Context context, CaptureSource source)
    {
        var intent = new Intent(context, typeof(VoiceSessionService));
        intent.SetAction(source == CaptureSource.WidgetQuickCapture ? ActionStartWidgetQuick : ActionStartAppManual);
        return intent;
    }

    public static Intent StopIntent(Context context)
    {
        var intent = new Intent(context, typeof(VoiceSessionService));
        intent.SetAction(ActionStop);
        return intent;
    }

    public static Intent WidgetToggleIntent(Context context)
    {
        var intent = new Intent(context, typeof(VoiceSessionService));
        intent.SetAction(ActionWidgetToggle);
        return intent;
    }
}
