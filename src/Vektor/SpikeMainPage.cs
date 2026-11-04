using Vektor.Core;
using Vektor.Platforms.Android;

namespace Vektor;

/// <summary>
/// Спайк-экран этапа 1: старт/стоп AppManualCapture, тестовый exact reminder, статус сессии.
/// Полный UI (разделы Диалог/План/Профиль) — этап 5 по UI kit; здесь только приёмка платформенных инвариантов.
/// </summary>
public sealed class SpikeMainPage : ContentPage
{
    private readonly Label statusLabel = new()
    {
        Text = "Готово",
        FontSize = 16,
    };

    public SpikeMainPage()
    {
        Title = "Вектор — spike этапа 1";

        var startButton = new Button
        {
            Text = "Начать запись в приложении",
        };
        startButton.Clicked += (_, _) => MainActivity()?.StartAppCapture();

        var stopButton = new Button
        {
            Text = "Стоп ■",
        };
        stopButton.Clicked += (_, _) => MainActivity()?.StopCapture();

        var reminderButton = new Button
        {
            Text = "Напоминание через 1 минуту",
        };
        reminderButton.Clicked += (_, _) => MainActivity()?.ScheduleTestReminder();

        var hintLabel = new Label
        {
            Text = "Spike: ручной стоп — паузы не останавливают запись. Лимит 10 минут, предупреждение в 09:30. ASR — этап 2.",
            FontSize = 12,
        };

        Content = new VerticalStackLayout
        {
            Spacing = 16,
            Padding = new Thickness(24, 48),
            Children = { statusLabel, startButton, stopButton, reminderButton, hintLabel },
        };

        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            statusLabel.Text = BuildStatus();
            return true; // продолжать
        });
    }

    private static string BuildStatus()
    {
        var store = VoiceSessionStore.Instance;
        if (store.Running && store.ActiveSource is { } source)
        {
            var elapsed = store.StartedAt is { } started ? (DateTime.Now - started).ToString(@"hh\:mm\:ss") : "—";
            return $"Запись: {source}… {elapsed} (стоп вручную или через уведомление)";
        }
        return store.LastResult ?? "Готово";
    }

    private static MainActivity? MainActivity() =>
        (MainActivity?)Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
}
