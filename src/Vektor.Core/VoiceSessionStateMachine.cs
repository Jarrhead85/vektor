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

public sealed record VoiceSignal(VoiceSignalKind Kind, double AtSeconds, string Note);

public sealed class VoiceSessionSettings
{
    /// <summary>Автостоп виджета: тишина ПОСЛЕ первой речи. D06. Не является переключателем: правило фиксировано источником.</summary>
    public TimeSpan WidgetAutoSilence { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Лимит 10 минут для любого источника.</summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Предупреждение за 30 с до лимита (09:30).</summary>
    public TimeSpan LimitWarnLead { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Технический таймаут отсутствия первой речи (предложение 15 с, подтвердить на тестах).</summary>
    public TimeSpan InitialNoSpeechTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Чистая машина состояний записи (docs/voice-state-machine.md).
/// Не содержит Android/таймеров: время передаётся снаружи (fake clock в тестах).
/// Событие Finalizing обрабатывается сервисом синхронно: сигнал остановки → дозапись → FinalizeAudio.
/// Stop single-flight: RequestStop/поставленные сигналы дедуплицируются через stopRequested.
/// </summary>
public sealed class VoiceSessionStateMachine
{
    private readonly VoiceSessionSettings settings;
    private readonly object gate = new();

    public VoicePhase Phase { get; private set; } = VoicePhase.Idle;
    public CaptureSource Source { get; private set; }
    public bool SpeechSeen { get; private set; }

    private DateTime startedAt;
    private DateTime lastSpeechAt;
    private DateTime? silenceSince;
    private bool stopRequested;
    private bool limitWarned;

    public VoiceSessionStateMachine(VoiceSessionSettings? settings = null)
    {
        this.settings = settings ?? new VoiceSessionSettings();
    }

    /// <summary>Idle → Starting. Повторный Start игнорируется (одна сессия в каждый момент).</summary>
    public bool TryStart(CaptureSource source, DateTime now)
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
            startedAt = now;
            lastSpeechAt = now;
            silenceSince = null;
            stopRequested = false;
            limitWarned = false;
            return true;
        }
    }

    /// <summary>Starting → Recording (микрофон занят, таймер пущен).</summary>
    public void AudioReady(DateTime now)
    {
        lock (gate)
        {
            if (Phase != VoicePhase.Starting)
            {
                return;
            }

            Phase = VoicePhase.Recording;
            startedAt = now;
        }
    }

    public void StartFailed()
    {
        lock (gate)
        {
            if (Phase == VoicePhase.Starting)
            {
                Phase = VoicePhase.Failed;
            }
        }
    }

    /// <summary>Feed VAD-кадра. speech=true сбрасывает непрерывную тишину и фиксирует firstSpeech.</summary>
    public IReadOnlyList<VoiceSignal> AudioFrame(bool speech, DateTime now)
    {
        lock (gate)
        {
            if (Phase != VoicePhase.Recording)
            {
                return [];
            }

            if (speech)
            {
                SpeechSeen = true;
                lastSpeechAt = now;
                silenceSince = null;
            }
            else if (SpeechSeen)
            {
                // Непрерывная тишина считается от последнего речевого кадра (между ними может не быть silent-фрейма: шаг > 30 мс).
                silenceSince ??= lastSpeechAt;
            }

            return CheckUpkeep(now);
        }
    }

    /// <summary>Периодический апдит независимого от кадров времени.</summary>
    public IReadOnlyList<VoiceSignal> Upkeep(DateTime now)
    {
        lock (gate)
        {
            return CheckUpkeep(now);
        }
    }

    private List<VoiceSignal> CheckUpkeep(DateTime now)
    {
        var signals = new List<VoiceSignal>();
        if (Phase != VoicePhase.Recording || stopRequested)
        {
            return signals;
        }

        var duration = now - startedAt;

        // Лимит 10 минут — для любого источника.
        if (duration >= settings.MaxDuration)
        {
            stopRequested = true;
            signals.Add(new VoiceSignal(VoiceSignalKind.LimitReached, duration.TotalSeconds, "лимит 10 минут"));
            return signals;
        }

        // Предупреждение в 09:30.
        if (!limitWarned && duration >= settings.MaxDuration - settings.LimitWarnLead)
        {
            limitWarned = true;
            signals.Add(new VoiceSignal(VoiceSignalKind.LimitWarning, duration.TotalSeconds, "осталось 30 секунд"));
        }

        // Автостоп только у виджета: тишина ≥3 с после первой речи. App-паузу НЕ заканчиваем.
        if (Source == CaptureSource.WidgetQuickCapture && SpeechSeen && silenceSince is { } since)
        {
            var silence = now - since;
            if (silence >= settings.WidgetAutoSilence)
            {
                stopRequested = true;
                signals.Add(new VoiceSignal(VoiceSignalKind.AutoStopRequested, duration.TotalSeconds, $"тишина {silence.TotalSeconds:0.#}с после речи"));
            }
        }

        // Начальная тишина ≠ пустая заметка.
        if (!SpeechSeen && duration >= settings.InitialNoSpeechTimeout)
        {
            stopRequested = true;
            signals.Add(new VoiceSignal(VoiceSignalKind.NoSpeechTimeout, duration.TotalSeconds, "нет первой речи на старте"));
        }

        return signals;
    }

    /// <summary>
    /// Ручной Stop. Single-flight: true только однажды (дедуп: повторный тап виджета, тап из уведомления, гонки).
    /// </summary>
    public bool RequestStop(DateTime now)
    {
        lock (gate)
        {
            if (stopRequested || (Phase != VoicePhase.Recording && Phase != VoicePhase.Starting))
            {
                return false;
            }

            stopRequested = true;
            return true;
        }
    }

    /// <summary>Дозапись завершена. Recording → Completed (есть аудио) / Cancelled (пустая тишина).</summary>
    public VoicePhase FinalizeAudio(DateTime now, bool hasAudio)
    {
        lock (gate)
        {
            if (Phase == VoicePhase.Recording)
            {
                Phase = hasAudio ? VoicePhase.Completed : VoicePhase.Cancelled;
            }
            else if (Phase == VoicePhase.Starting && !hasAudio)
            {
                Phase = VoicePhase.Cancelled;
            }
            return Phase;
        }
    }

    /// <summary>Системный сбой/отзыв записи. Resources освобождает сервис, фрагмент сохраняется где возможно.</summary>
    public void Fail()
    {
        lock (gate)
        {
            if (Phase is VoicePhase.Starting or VoicePhase.Recording or VoicePhase.Finalizing)
            {
                Phase = VoicePhase.Failed;
            }
        }
    }
}
