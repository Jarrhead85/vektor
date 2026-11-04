using System.Diagnostics;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Vektor.Core;

namespace Vektor.Platforms.Android;

/// <summary>
/// Одна сессия записи между Start и Terminal (docs/voice-state-machine.md).
/// VAD спайка — RMS-порог на PCM-кадре 16 kHz; настоящий Vosk/VAD подключается на этапе 2.
/// Source неизменяем; Stop single-flight; машина считает время в монотонном Stopwatch-ms.
/// Finish идемпотентен (Interlocked), причина завершения сохраняется: MicError НЕ маскируется тишиной.
/// </summary>
internal sealed class VoiceRecordingSession
{
    private const int SampleRate = 16_000;
    private const int FrameSize = 512; // ~32 мс при 16 кГц
    private const double SpeechRmsThreshold = 0.035; // спайк-порог; настоящий VAD этапа 2 заменит

    private readonly Context context;
    private readonly CaptureSource source;
    private readonly VoiceSessionStateMachine machine = new();
    private readonly VoiceSessionStore store = VoiceSessionStore.Instance;
    private readonly CancellationTokenSource cancelSource = new();
    private readonly object gate = new();

    private AudioRecord? recorder;
    private PowerManager.WakeLock? wakeLock;
    private double secondsRecorded;
    private volatile bool stopRequestedByMachine;
    private int finished; // 0 = идёт, 1 = финализирована (идемпотентность Finish)
    private Thread readerThread = null!; // задаётся в Start

    public VoiceRecordingSession(Context context, CaptureSource source)
    {
        this.context = context;
        this.source = source;
    }

    /// <summary>Монотонные millisecond-only для машины (Stopwatch); календарное время — отдельно для отображения.</summary>
    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    public void Start()
    {
        var now = DateTime.Now; // только отображение
        store.Clear();
        store.Running = true;
        store.ActiveSource = source;
        store.StartedAt = now;
        store.LastResult = null;

        AcquireWakeLock();
        machine.TryStart(source, NowMs());

        var foregroundOk = PostRecordingNotification();
        if (!foregroundOk)
        {
            // Частично успешный запуск: foreground не удался — запись НЕ продолжается без нового уведомления.
            if (machine.Fail(VoiceStopReason.ServiceStopped, NowMs()))
            {
                Finish(hasAudio: false);
            }
            return;
        }

        if (!TryOpenRecorder())
        {
            // Могли получить RequestStop, пока открывали recorder — не перетираем UserStop.
            if (machine.Fail(VoiceStopReason.MicError, NowMs()))
            {
                Finish(hasAudio: false);
            }
            return;
        }

        machine.AudioReady(NowMs());

        readerThread = new Thread(RecordLoop)
        {
            IsBackground = true,
            Name = "vektor-audio-reader",
        };
        readerThread.Start();
    }

    /// <summary>UserStop/повторный тап виджета/Stop из уведомления. Single-flight внутри машины.</summary>
    public void RequestUserStop()
    {
        if (machine.RequestStop(NowMs()))
        {
            stopRequestedByMachine = true;
            cancelSource.Cancel();
        }
    }

    /// <summary>Системное разрушение сервиса/системный сбой фона: причина ServiceStopped, фрагменты не теряются молча.</summary>
    public void RequestServiceStop()
    {
        var accepted = machine.Fail(VoiceStopReason.ServiceStopped, NowMs());
        stopRequestedByMachine = true;
        cancelSource.Cancel();
        Finish(hasAudio: machine.SpeechSeen && secondsRecorded > 0 && machine.StopReason != VoiceStopReason.NoSpeech);
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
                    // Ошибка чтения микрофона ≠ тишина: фиксируем отдельную причину.
                    machine.Fail(VoiceStopReason.MicError, NowMs());
                    stopRequestedByMachine = true;
                    break;
                }

                double sumSquares = 0;
                for (var i = 0; i < frames; i++)
                {
                    sumSquares += (double)buffer[i] * buffer[i];
                }

                var rms = Math.Sqrt(sumSquares / frames) / short.MaxValue;
                secondsRecorded += frames / (double)SampleRate;

                var signals = machine.AudioFrame(rms > SpeechRmsThreshold, NowMs());
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
            // Микрофон/системный сбой на ПОМИДОР этапе цикла: причина отдельна (MicError), фрагмент сохранится.
            machine.Fail(VoiceStopReason.MicError, NowMs());
            stopRequestedByMachine = true;
        }

        var machineStopReason = machine.StopReason;
        var hasAudio = machineStopReason != VoiceStopReason.NoSpeech && machine.SpeechSeen && secondsRecorded > 0;

        // Если Fail настиг user-stop первым, не перезатираем (machine.Fail вернёт false) — reason уже в машине.
        Finish(hasAudio);
    }

    private void HandleSignal(VoiceSignal signal)
    {
        switch (signal.Kind)
        {
            case VoiceSignalKind.LimitWarning:
                Notifications.Post(context, VoiceSessionService.LimitWarningNotificationId,
                    NotificationChannels.Recording, "Вектор: осталось 30 секунд",
                    "До лимита 10 минут. Нажми ■, чтобы закончить.");
                break;

            case VoiceSignalKind.AutoStopRequested:
            case VoiceSignalKind.UserStopRequested:
            case VoiceSignalKind.LimitReached:
            case VoiceSignalKind.NoSpeechTimeout:
                stopRequestedByMachine = true;
                cancelSource.Cancel();
                break;
        }
    }

    /// <summary>Идемпотентная финализация: Interlocked гарантирует одноразовость из ЛЮБОГО потока.</summary>
    private void Finish(bool hasAudio)
    {
        if (Interlocked.Exchange(ref finished, 1) == 1)
        {
            return;
        }

        ReleaseRecorder();

        var result = BuildResultText(hasAudio);
        machine.FinalizeAudio(NowMs(), hasAudio);

        store.Running = false;
        store.ActiveSource = null;
        store.StartedAt = null;
        store.LastResult = result;
        store.Notify();

        AppStatusStore.Instance.SetInfo(result);
        if (source == CaptureSource.WidgetQuickCapture)
        {
            // Результат из фона — уведомление с переходом к карточке (Activity не открывается произвольно).
            Notifications.Post(context, VoiceSessionService.ResultNotificationId,
                NotificationChannels.Results, "Вектор: запись завершена", result);
        }

        ReleaseWakeLock();
        VoiceSessionService.SessionFinished();
    }

    private string BuildResultText(bool hasAudio)
    {
        var fragment = $"фрагмент {secondsRecorded:0.#} с";
        return machine.StopReason switch
        {
            VoiceStopReason.NoSpeech => "Тишина без речи — заметка не создана",
            VoiceStopReason.MicError => $"Запись прервана (ошибка микрофона), сохранён {fragment}",
            VoiceStopReason.ServiceStopped when hasAudio => $"Сервис остановлен системой, сохранён {fragment}",
            VoiceStopReason.ServiceStopped => "Запись прервана (сервис остановлен)",
            VoiceStopReason.User => $"Стоп по кнопке: записано {secondsRecorded:0.#} с",
            VoiceStopReason.AutoSilence => $"Автостоп (3 с тишины): записано {secondsRecorded:0.#} с",
            VoiceStopReason.Limit => $"Лимит 10 минут: записано {secondsRecorded:0.#} с",
            _ => hasAudio ? $"Записано {secondsRecorded:0.#} с" : "Запись завершена",
        };
    }

    /// <summary>foregroundServiceType=microphone — обязательный явный тип на Android 14; false при сбое перехода.</summary>
    private bool PostRecordingNotification()
    {
        try
        {
            NotificationChannels.Ensure(context);

            var builder = new Notification.Builder(context, NotificationChannels.Recording)
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

            // Сервис уже стартует через StartForegroundService от виджета/UI; здесь только переход в foreground.
            ((VoiceSessionService)context).StartForeground(
                VoiceSessionService.RecordingNotificationId,
                builder.Build(),
                ForegroundService.TypeMicrophone);
            return true;
        }
        catch (Exception)
        {
            // Пропущен call StartForeground в 5с окно/нет типа/системный запрет: не молчим.
            Notifications.Post(context, VoiceSessionService.ResultNotificationId,
                NotificationChannels.Results, "Вектор: запись не началась",
                "Не удалось открыть фоновое уведомление записи (системное ограничение службы).");
            return false;
        }
    }

    private void AcquireWakeLock()
    {
        try
        {
            var power = (PowerManager)context.GetSystemService(Context.PowerService)!;
            wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "vektor:session");
            wakeLock.SetReferenceCounted(false);
            // Ограниченный жизнью сессии lock: max 10 мин + запас на финализацию; фоновость не постоянна.
            wakeLock.Acquire((long)TimeSpan.FromMinutes(11).TotalMilliseconds);
        }
        catch (Exception)
        {
            wakeLock = null; // без wakeLock запись всё равно возможна; честное ограничение фиксируется отчётом
        }
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
        catch (Exception)
        {
            // Уже выпущен системой.
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
            // Release уже сделан системой.
        }
        finally
        {
            recorder = null;
        }
    }
}
