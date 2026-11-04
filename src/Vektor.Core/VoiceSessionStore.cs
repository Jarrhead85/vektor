namespace Vektor.Core;

/// <summary>
/// Общее состояние активной сессии для виджета/UI (виджет не обязан получать PCM: lifecycle у сервиса).
/// Не содержит аудио и текста записи.
/// </summary>
public sealed class VoiceSessionStore
{
    public static VoiceSessionStore Instance { get; } = new();

    private VoiceSessionStore()
    {
    }

    // Public set: сервис/сессия — другая сборка (Vektor.csproj); API записи ограничено сессией ниже.
    public bool Running { get; set; }
    public CaptureSource? ActiveSource { get; set; }
    public DateTime? StartedAt { get; set; }

    /// <summary>Текстовая сводка последнего исхода (для UI/уведомлений spike).</summary>
    public string? LastResult { get; set; }

    public event Action? Changed;

    public void Notify() => Changed?.Invoke();

    public void Clear()
    {
        Running = false;
        ActiveSource = null;
        StartedAt = null;
        Notify();
    }
}
