using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Vektor.Core;

namespace Vektor.Platforms.Android;

/// <summary>
/// Микрофонный foreground service (AndroidManifest: microphone type).
/// Lifecycle записи владеет сервис; виджет и UI видят общий VoiceSessionStore, PCM им не доставляется.
/// Start/stop идемпотентны: одна сессия в каждый момент.
/// </summary>
[Service(
    Name = "com.vektor.VoiceSessionService",
    Enabled = true,
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeMicrophone)]
public sealed class VoiceSessionService : Service
{
    public const string ChannelRecording = "vektor_recording";
    public const string ChannelResults = "vektor_results";
    public const int RecordingNotificationId = 101;
    public const int LimitWarningNotificationId = 103;

    private static readonly object Sync = new();
    private static VoiceSessionService? activeService;
    private static VoiceRecordingSession? activeSession;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannels();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case VoiceIntents.ActionStop:
                // Stop из уведомления или из UI приложения: single-flight машины дедуплицирует гонку.
                lock (Sync)
                {
                    activeSession?.RequestUserStop();
                }
                break;

            case VoiceIntents.ActionStartAppManual:
            case VoiceIntents.ActionStartWidgetQuick:
                TryStartCapture(intent.Action == VoiceIntents.ActionStartWidgetQuick);
                break;

            case VoiceIntents.ActionWidgetToggle:
                // Повторное нажатие кнопки виджета останавливает запись.
                lock (Sync)
                {
                    if (activeSession is { } running)
                    {
                        running.RequestUserStop();
                    }
                    else
                    {
                        TryStartCapture(widget: true);
                    }
                }
                break;
        }

        return StartCommandResult.Sticky;
    }

    private void TryStartCapture(bool widget)
    {
        if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            // Не открываем Activity из фона: честное уведомление с переходом в приложение.
            Notifications.Post(this, 102, ChannelResults, "Вектор: нужно разрешение",
                "Разреши микрофон в приложении Вектор — запись виджета не началась.");
            StopSelf();
            return;
        }

        lock (Sync)
        {
            if (activeSession is not null)
            {
                return; // одна сессия
            }

            activeService = this;
            activeSession = new VoiceRecordingSession(this, widget ? CaptureSource.WidgetQuickCapture : CaptureSource.AppManualCapture);
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

    private void CreateNotificationChannels()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(
            ChannelRecording, "Запись Вектор", NotificationImportance.Low)
        {
            Description = "Постоянное уведомление активной записи с кнопкой Stop",
        });
        manager.CreateNotificationChannel(new NotificationChannel(
            ChannelResults, "Результаты Вектор", NotificationImportance.Default)
        {
            Description = "Результаты записи и напоминания",
        });
    }

    public override IBinder? OnBind(Intent intent) => null;
}
