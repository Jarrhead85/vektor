using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Vektor.Core;

namespace Vektor.Platforms.Android;

/// <summary>
/// Одна сессия записи между Start и Terminal.
/// VAD спайка — RMS-порог на PCM-кадре 16 kHz; настоящий Vosk/VAD подключается на этапе 2.
/// Source неизменяем; Stop single-flight; лимит 10 минут и предупреждение выдаёт state machine.
/// </summary>
internal sealed class VoiceRecordingSession
{
    private const int SampleRate = 16_000;
    private const int FrameSize = 512; // ~32 мс при 16 кГц
    private const double SpeechRmsThreshold = 0.035; // спайк-порог; behaviour VAD этапа 2 заменит

    private readonly Context context;
    private readonly CaptureSource source;
    private readonly VoiceSessionStateMachine machine = new();
    private readonly VoiceSessionStore store = VoiceSessionStore.Instance;
    private readonly CancellationTokenSource cancelSource = new();
    private readonly object gate = new();

    private AudioRecord? recorder;
    private Thread? reader;
    private PowerManager.WakeLock? wakeLock;
    private double secondsRecorded;
    private volatile bool stopRequestedByMachine;
    private bool stopHasAudio = true;
    private bool finished;

    public VoiceRecordingSession(Context context, CaptureSource source)
    {
        this.context = context;
        this.source = source;
    }

    public void Start()
    {
        var now = DateTime.Now;
        store.Clear();
        store.Running = true;
        store.ActiveSource = source;
        store.StartedAt = now;
        store.LastResult = null;

        AcquireWakeLock();
        PostRecordingNotification();
        machine.TryStart(source, now);

        if (!TryOpenRecorder())
        {
            machine.StartFailed();
            Finish(hasAudio: false, result: "Микрофон недоступен — запись не начата");
            return;
        }

        machine.AudioReady(DateTime.Now);

        reader = new Thread(RecordLoop)
        {
            IsBackground = true,
            Name = "vektor-audio-reader",
        };
        reader.Start();
    }

    /// <summary>UserStop/повторный тап виджета/Stop из уведомления. Single-flight внутри машины.</summary>
    public void RequestUserStop()
    {
        if (machine.RequestStop(DateTime.Now))
        {
            stopHasAudio = true;
            stopRequestedByMachine = true;
            cancelSource.Cancel();
        }
    }

    private bool TryOpenRecorder()
    {
        try
        {
            var minBuffer = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
            if (minBuffer <= 0)
            {
                return false;
            }

            recorder = new AudioRecord(AudioSource.Mic, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, minBuffer * 3);
            if (recorder.State == State.Uninitialized)
            {
                return false;
            }

            recorder.StartRecording();
            return recorder.RecordingState == RecordState.Recording;
        }
        catch (Exception)
        {
            ReleaseRecorder();
            return false;
        }
    }

    private void RecordLoop()
    {
        var buffer = new short[FrameSize];
        try
        {
            while (!cancelSource.IsCancellationRequested && recorder is { } rec)
            {
                if (rec.RecordingState != RecordState.Recording)
                {
                    break; // системный отбор микрофона/ошибка → корректное прерывание
                }

                var frames = rec.Read(buffer, 0, buffer.Length);
                if (frames <= 0)
                {
                    break;
                }

                double sumSquares = 0;
                for (var i = 0; i < frames; i++)
                {
                    sumSquares += (double)buffer[i] * buffer[i];
                }

                var rms = Math.Sqrt(sumSquares / frames) / short.MaxValue;
                secondsRecorded += frames / (double)SampleRate;

                var signals = machine.AudioFrame(rms > SpeechRmsThreshold, DateTime.Now);
                foreach (var signal in signals)
                {
                    HandleSignal(signal);
                    if (stopRequestedByMachine)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Микрофон/системный сбой: сохраняем доступный фрагмент, финализируем корректно (спека §5).
        }

        var hasAudio = machine.SpeechSeen && secondsRecorded > 0;
        if (stopRequestedByMachine)
        {
            hasAudio = machine.SpeechSeen && stopHasAudio && secondsRecorded > 0;
        }

        Finish(
            hasAudio,
            hasAudio
                ? $"Записано {secondsRecorded:0.#} с (распознавание — этап 2, файл не сохранён в спайке)"
                : "Тишина без речи — заметка не создана");
    }

    private void HandleSignal(VoiceSignal signal)
    {
        switch (signal.Kind)
        {
            case VoiceSignalKind.LimitWarning:
                Notifications.Post(context, VoiceSessionService.LimitWarningNotificationId,
                    VoiceSessionService.ChannelRecording, "Вектор: остался 1 сегмент",
                    "До лимита 10 минут — 30 секунд. Досня, нажми ■.");
                break;

            case VoiceSignalKind.AutoStopRequested:
            case VoiceSignalKind.UserStopRequested:
            case VoiceSignalKind.LimitReached:
            case VoiceSignalKind.NoSpeechTimeout:
                stopHasAudio = signal.Kind != VoiceSignalKind.NoSpeechTimeout;
                stopRequestedByMachine = true;
                cancelSource.Cancel();
                break;
        }
    }

    private void Finish(bool hasAudio, string result)
    {
        lock (gate)
        {
            if (finished)
            {
                return;
            }
            finished = true;
        }

        ReleaseRecorder();

        machine.FinalizeAudio(DateTime.Now, hasAudio);
        store.Running = false;
        store.ActiveSource = null;
        store.StartedAt = null;
        store.LastResult = result;
        store.Notify();

        if (source == CaptureSource.WidgetQuickCapture)
        {
            // Результат из фона — уведомление с переходом к карточке (Activity не открывается произвольно).
            Notifications.Post(context, 102, VoiceSessionService.ChannelResults, "Вектор: запись завершена", result);
        }

        ReleaseWakeLock();
        VoiceSessionService.SessionFinished();
    }

    private void PostRecordingNotification()
    {
        var builder = new Notification.Builder(context, VoiceSessionService.ChannelRecording)
            .SetSmallIcon(Resource.Drawable.widget_mic)
            .SetContentTitle("Вектор: запись")
            .SetContentText("Нажми ■, чтобы закончить")
            .SetOngoing(true);

        var stopAction = new Notification.Action.Builder(
            Resource.Drawable.widget_mic,
            new Java.Lang.String("Стоп"),
            PendingIntent.GetService(context, 1, VoiceIntents.StopIntent(context),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)).Build();
        builder.AddAction(stopAction);

        var content = PendingIntent.GetActivity(context, 0, new Intent(context, typeof(MainActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        builder.SetContentIntent(content);

        // foregroundServiceType=microphone — обязательный явный тип на Android 14.
        // Сервис уже стартует через StartForegroundService от виджета/UI; здесь только переход в foreground.
        ((VoiceSessionService)context).StartForeground(
            VoiceSessionService.RecordingNotificationId,
            builder.Build(),
            ForegroundService.TypeMicrophone);
    }

    private void AcquireWakeLock()
    {
        var power = (PowerManager)context.GetSystemService(Context.PowerService)!;
        wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "vektor:session");
        wakeLock.SetReferenceCounted(false);
        // Ограниченный жизнью сессии lock: max 10 мин + запас на финализацию; никакой постоянной фоновости.
        wakeLock.Acquire((long)TimeSpan.FromMinutes(11).TotalMilliseconds);
    }

    private void ReleaseWakeLock()
    {
        try
        {
            if (wakeLock is { IsHeld: true })
            {
                wakeLock.Release();
            }
        }
        finally
        {
            wakeLock = null;
        }
    }

    private void ReleaseRecorder()
    {
        try
        {
            if (recorder is { } rec)
            {
                if (rec.RecordingState == RecordState.Recording)
                {
                    rec.Stop();
                }
                rec.Release();
                rec.Dispose();
            }
        }
        catch (Exception)
        {
            //Release уже сделан системой.
        }
        finally
        {
            recorder = null;
        }
    }
}
