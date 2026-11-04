using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Vektor.Core;

namespace Vektor.Platforms.Android;

/// <summary>
/// Микрофонный foreground service (AndroidManifest: microphone type).
/// Lifecycle записи — сервис; виджет и UI видят общий VoiceSessionStore, PCM им не доставляется.
/// Start/stop идемпотентны: одна сессия в каждый момент.
/// NotSticky: запись не воскрешается системой после kill; OnDestroy финализирует активную сессию.
/// </summary>
[Service(
    Name = "com.vektor.VoiceSessionService",
    Enabled = true,
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeMicrophone)]
public sealed class VoiceSessionService : Service
{
    public const int RecordingNotificationId = 101;
    public const int ResultNotificationId = 102;
    public const int LimitWarningNotificationId = 103;

    private static readonly object Sync = new();
    private static VoiceSessionService? activeService;
    private static VoiceRecordingSession? activeSession;
    private static volatile bool destroyed;

    public override void OnCreate()
    {
        base.OnCreate();
        NotificationChannels.Ensure(this);
        destroyed = false;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // null/unknown intent (restart/спорно предназначенный): без активной сессии сервис не висит.
        var action = intent?.Action;
        if (action != VoiceIntents.ActionStop &&
            action != VoiceIntents.ActionStartAppManual &&
            action != VoiceIntents.ActionStartWidgetQuick &&
            action != VoiceIntents.ActionWidgetToggle)
        {
            if (activeSession is null)
            {
                StopSelf();
            }
            return StartCommandResult.NotSticky;
        }

        switch (action)
        {
            case VoiceIntents.ActionStop:
                lock (Sync)
                {
                    var session = activeSession;
                    if (session is not null)
                    {
                        session.RequestUserStop();
                    }
                    else
                    {
                        // STOP без активной сессии: не маскируем успех, просто не оставляем висящий сервис.
                        StopSelf();
                    }
                }
                break;

            case VoiceIntents.ActionStartAppManual:
            case VoiceIntents.ActionStartWidgetQuick:
                TryStartCapture(widget: action == VoiceIntents.ActionStartWidgetQuick);
                break;

            case VoiceIntents.ActionWidgetToggle:
                lock (Sync)
                {
                    if (activeSession is { } running)
                    {
                        running.RequestUserStop(); // повторное нажатие кнопки виджета = stop
                    }
                    else
                    {
                        TryStartCapture(widget: true);
                    }
                }
                break;
        }

        return StartCommandResult.NotSticky;
    }

    private void TryStartCapture(bool widget)
    {
        if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            // Не открываем Activity из фона: честное уведомление с переходом в приложение.
            Notifications.Post(this, ResultNotificationId, NotificationChannels.Results,
                "Вектор: нужно разрешение",
                "Разреши микрофон в приложении Вектор — запись виджета не началась.");
            StopSelf();
            return;
        }

        lock (Sync)
        {
            if (activeSession is not null || destroyed)
            {
                return; // одна сессия; сервис в разрушении — новые старты не принимаем
            }

            activeService = this;
            activeSession = new VoiceRecordingSession(this,
                widget ? CaptureSource.WidgetQuickCapture : CaptureSource.AppManualCapture);
            activeSession.Start();
        }
    }

    internal static void SessionFinished()
    {
        lock (Sync)
        {
            activeSession = null;
            activeService?.StopSelf();
            activeService = null;
        }
    }

    /// <summary>Системное разрушение сервиса: активная запись финализируется (ServiceStopped), фрагменты не теряются молча.</summary>
    public override void OnDestroy()
    {
        base.OnDestroy();

        VoiceRecordingSession? orphaned;
        lock (Sync)
        {
            destroyed = true;
            orphaned = activeSession;
            activeSession = null;
            activeService = null;
        }

        orphaned?.RequestServiceStop();
    }

    public override IBinder? OnBind(Intent intent) => null;
}
