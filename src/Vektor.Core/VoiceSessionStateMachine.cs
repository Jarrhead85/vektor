namespace Vektor.Core;

/// <summary>Источник записи. Неизменяем после создания сессии (docs/voice-state-machine.md).</summary>
public enum CaptureSource
{
    WidgetQuickCapture,
    AppManualCapture,
}

public enum VoicePhase
{
    Idle,
    Starting,
    Recording,
    Finalizing,
    Completed,
    Cancelled,
    Failed,
}

public enum VoiceSignalKind
{
    /// <summary>Виджет: ≥3 с непрерывной тишины ПОСЛЕ первой речи, при любой длительности. Без порога длительности.</summary>
    AutoStopRequested,

    /// <summary>Кнопка stop / повторное нажатие на виджете.</summary>
    UserStopRequested,

    /// <summary>09:30 — осталось 30 секунд до лимита.</summary>
    LimitWarning,

    /// <summary>10:00 — корректное завершение по лимиту, для любого источника.</summary>
    LimitReached,

    /// <summary>Нет первой речи на старте (15 с): пустую заметку не создаём.</summary>
    NoSpeechTimeout,
}

/// <summary>Итоговые причины завершения записи. Никогда не маскируем MicError под тишину.</summary>
public enum VoiceStopReason
{
    None,
    User,
    AutoSilence,
    Limit,
    NoSpeech,
    MicError,
    ServiceStopped,
}

public sealed record VoiceSignal(VoiceSignalKind Kind, double AtSeconds, string Note);

public sealed class VoiceSessionSettings
{
    /// <summary>Автостоп виджета: тишина ПОСЛЕ первой речи. D06. Не переключатель: правило фиксировано источником.</summary>
    public TimeSpan WidgetAutoSilence { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Лимит 10 минут для любого источника.</summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Предупреждение за 30 с до лимита (09:30).</summary>
    public TimeSpan LimitWarnLead { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Технический таймаут отсутствия первой речи (предложение 15 с, подтвердить на тестах).</summary>
    public TimeSpan InitialNoSpeechTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Потокобезопасный срез состояния активной сессии (для виджета/UI/тестов).
/// ElapsedSeconds замораживается при входе в Finalizing.
/// </summary>
public sealed record VoiceSessionSnapshot(
    VoicePhase Phase,
    CaptureSource? Source,
    bool SpeechSeen,
    double ElapsedSeconds,
    VoiceStopReason StopReason);

/// <summary>
/// Чистая машина состояний записи (docs/voice-state-machine.md).
/// Не содержит Android/таймеров: время передаётся снаружи как МОНОТОННЫЕ elapsedMs
/// (Android: Stopwatch). Календарное время используется только для отображения/дат.
/// Кадры с убывающим nowMs игнорируются (нарушение монотонности не меняет состояние).
/// Stop single-flight: RequestStop/сигналы дедуплицируются; повторные стопы в Finalizing — no-op.
/// </summary>
public sealed class VoiceSessionStateMachine
{
    private readonly VoiceSessionSettings settings;
    private readonly object gate = new();

    public VoicePhase Phase { get; private set; } = VoicePhase.Idle;
    public CaptureSource Source { get; private set; }
    public bool SpeechSeen { get; private set; }
    public VoiceStopReason StopReason { get; private set; } = VoiceStopReason.None;

    private double startedMs;
    private double lastSpeechMs;
    private double? silenceSinceMs;
    private double? frozenElapsedMs;
    private bool limitWarned;

    public VoiceSessionStateMachine(VoiceSessionSettings? settings = null)
    {
        this.settings = settings ?? new VoiceSessionSettings();
    }

    /// <summary>Idle → Starting. Повторный Start игнорируется (одна сессия в каждый момент). nowMs — монотонный.</summary>
    public bool TryStart(CaptureSource source, double nowMs)
    {
        lock (gate)
        {
            if (Phase != VoicePhase.Idle)
            {
                return false;
            }

            Phase = VoicePhase.Starting;
            Source = source; // источник после этого не меняем
            SpeechSeen = false;
            StopReason = VoiceStopReason.None;
            startedMs = nowMs;
            lastSpeechMs = nowMs;
            silenceSinceMs = null;
            frozenElapsedMs = null;
            limitWarned = false;
            return true;
        }
    }

    /// <summary>Starting → Recording: точка монотонного старта таймера записи.</summary>
    public void AudioReady(double nowMs)
    {
        lock (gate)
        {
            if (Phase != VoicePhase.Starting)
            {
                return;
            }

            Phase = VoicePhase.Recording;
            startedMs = nowMs;
        }
    }

    public void StartFailed(double nowMs)
    {
        lock (gate)
        {
            if (Phase == VoicePhase.Starting)
            {
                Phase = VoicePhase.Failed;
                StopReason = VoiceStopReason.MicError;
                frozenElapsedMs = 0;
            }
        }
    }

    /// <summary>Feed VAD-кадра. speech=true сбрасывает непрерывную тишину и фиксирует firstSpeech.</summary>
    public IReadOnlyList<VoiceSignal> AudioFrame(bool speech, double nowMs)
    {
        lock (gate)
        {
            if (Phase != VoicePhase.Recording || nowMs < lastKnownMs)
            {
                return []; // нарушение монотонности — кадр не применим
            }
            lastKnownMs = nowMs;

            if (speech)
            {
                SpeechSeen = true;
                lastSpeechMs = nowMs;
                silenceSinceMs = null;
            }
            else if (SpeechSeen)
            {
                // Непрерывная тишина считается от последнего речевого кадра.
                silenceSinceMs ??= lastSpeechMs;
            }

            return CheckUpkeep(nowMs);
        }
    }

    /// <summary>Периодический апдит независимо от кадров.</summary>
    public IReadOnlyList<VoiceSignal> Upkeep(double nowMs)
    {
        lock (gate)
        {
            return CheckUpkeep(nowMs);
        }
    }

    private double lastKnownMs;

    private List<VoiceSignal> CheckUpkeep(double nowMs)
    {
        var signals = new List<VoiceSignal>();
        if (Phase != VoicePhase.Recording || StopReason != VoiceStopReason.None)
        {
            return signals;
        }

        var durationMs = nowMs - startedMs;

        // Лимит 10 минут — для любого источника.
        if (durationMs >= settings.MaxDuration.TotalMilliseconds)
        {
            return EnterFinalizing(signals, VoiceSignalKind.LimitReached, nowMs, durationMs, "лимит 10 минут");
        }

        // Предупреждение в 09:30.
        if (!limitWarned && durationMs >= settings.MaxDuration.TotalMilliseconds - settings.LimitWarnLead.TotalMilliseconds)
        {
            limitWarned = true;
            signals.Add(new VoiceSignal(VoiceSignalKind.LimitWarning, durationMs / 1000.0, "осталось 30 секунд"));
        }

        // Автостоп только у виджета: тишина ≥3 с после первой речи. App-паузу НЕ заканчиваем.
        if (Source == CaptureSource.WidgetQuickCapture && SpeechSeen && silenceSinceMs is { } since)
        {
            var silenceMs = nowMs - since;
            if (silenceMs >= settings.WidgetAutoSilence.TotalMilliseconds)
            {
                return EnterFinalizing(signals, VoiceSignalKind.AutoStopRequested, nowMs, durationMs,
                    $"тишина {silenceMs / 1000.0:0.#}с после речи");
            }
        }

        // Начальная тишина ≠ пустая заметка.
        if (!SpeechSeen && durationMs >= settings.InitialNoSpeechTimeout.TotalMilliseconds)
        {
            return EnterFinalizing(signals, VoiceSignalKind.NoSpeechTimeout, nowMs, durationMs, "нет первой речи на старте");
        }

        return signals;
    }

    private List<VoiceSignal> EnterFinalizing(
        List<VoiceSignal> signals, VoiceSignalKind kind, double nowMs, double durationMs, string note)
    {
        frozenElapsedMs = durationMs / 1000.0;
        StopReason = kind switch
        {
            VoiceSignalKind.LimitReached => VoiceStopReason.Limit,
            VoiceSignalKind.AutoStopRequested => VoiceStopReason.AutoSilence,
            VoiceSignalKind.NoSpeechTimeout => VoiceStopReason.NoSpeech,
            VoiceSignalKind.UserStopRequested => VoiceStopReason.User,
            _ => VoiceStopReason.MicError,
        };
        Phase = VoicePhase.Finalizing;
        signals.Add(new VoiceSignal(kind, durationMs / 1000.0, note));
        return signals;
    }

    /// <summary>
    /// Ручной Stop. Single-flight: true только однажды (таблица/повторный тап/уведомление).
    /// Recording/Starting → Finalizing; в Finalizing — no-op.
    /// </summary>
    public bool RequestStop(double nowMs)
    {
        lock (gate)
        {
            if (StopReason != VoiceStopReason.None ||
                (Phase != VoicePhase.Recording && Phase != VoicePhase.Starting))
            {
                return false;
            }

            frozenElapsedMs = (nowMs - startedMs) / 1000.0;
            StopReason = VoiceStopReason.User;
            Phase = VoicePhase.Finalizing;
            return true;
        }
    }

    /// <summary>
    /// Внешний прерывание: ошибка микрофона/системный сбой/stopped-сервис.
    /// Идемпотентно: первый вызов фиксирует причину, остальные игнорируются.
    /// </summary>
    public bool Fail(VoiceStopReason reason, double nowMs)
    {
        lock (gate)
        {
            if (StopReason != VoiceStopReason.None)
            {
                return false; // не затирать UserStop/etc.
            }
            if (Phase is not (VoicePhase.Starting or VoicePhase.Recording))
            {
                return false;
            }

            frozenElapsedMs = (nowMs - startedMs) / 1000.0;
            StopReason = reason;
            Phase = VoicePhase.Finalizing;
            return true;
        }
    }

    /// <summary>
    /// Дозапись завершена. Только из Finalizing (Starting-«самоликвидация» без стопа запрещена).
    /// hasAudio → Completed; без аудио: NoSpeech → Cancelled, MicError/ServiceStopped → Failed.
    /// </summary>
    public VoicePhase FinalizeAudio(double nowMs, bool hasAudio)
    {
        lock (gate)
        {
            if (Phase == VoicePhase.Finalizing)
            {
                if (hasAudio)
                {
                    Phase = VoicePhase.Completed;
                }
                else
                {
                    Phase = StopReason is VoiceStopReason.MicError or VoiceStopReason.ServiceStopped
                        ? VoicePhase.Failed
                        : VoicePhase.Cancelled;
                }
                frozenElapsedMs ??= (nowMs - startedMs) / 1000.0;
            }
            return Phase;
        }
    }

    /// <summary>Атомарный срез текущего состояния. В Recording elapsed теперь, Finalizing/терминал — замороженный.</summary>
    public VoiceSessionSnapshot Snapshot(double nowMs)
    {
        lock (gate)
        {
            var elapsed = Phase == VoicePhase.Recording
                ? (nowMs - startedMs) / 1000.0
                : frozenElapsedMs ?? 0;
            return new VoiceSessionSnapshot(Phase, Source, SpeechSeen, elapsed, StopReason);
        }
    }
}
