namespace Vektor.Core;

/// <summary>
/// Потокобезопасный статус для экрана приложения: инфо и ошибки (в т.ч. про разрешения).
/// Отдельно от VoiceSessionStore: ошибка разрешения не является исходом записи.
/// </summary>
public sealed class AppStatusStore
{
    public static AppStatusStore Instance { get; } = new();

    private AppStatusStore()
    {
    }

    private readonly object gate = new();
    private string? info;
    private string? error;

    public event Action? Changed;

    public string? GetInfo()
    {
        lock (gate)
        {
            return info;
        }
    }

    public string? GetError()
    {
        lock (gate)
        {
            return error;
        }
    }

    public void SetInfo(string message)
    {
        lock (gate)
        {
            info = message;
            Changed?.Invoke();
        }
    }

    /// <summary>Screen-ошибка не затирает предыдущую silently; ClearError вызывается при новом успешном старте.</summary>
    public void SetError(string message)
    {
        lock (gate)
        {
            error = message;
            Changed?.Invoke();
        }
    }

    public void ClearError()
    {
        lock (gate)
        {
            error = null;
            Changed?.Invoke();
        }
    }

    public void ClearAll()
    {
        lock (gate)
        {
            info = null;
            error = null;
            Changed?.Invoke();
        }
    }
}
