using Vektor.Core;
using Vektor.Platforms.Android;

namespace Vektor;

/// <summary>
/// Спайк-экран этапа 1: старт/стоп AppManualCapture, напоминание/отмена, статус/ошибки на экране.
/// Полный UI (разделы Диалог/План/Профиль) — этап 5 по UI kit; здесь только приёмка платформенных инвариантов.
/// </summary>
public sealed class SpikeMainPage : ContentPage
{
    private static readonly Color ColorReady = Colors.DimGray;
    private static readonly Color ColorRecording = Colors.Green;
    private static readonly Color ColorError = Colors.OrangeRed;

    private readonly Label statusLabel;

    public SpikeMainPage()
    {
        Title = "Вектор — spike этапа 1";

        var startButton = new Button { Text = "Начать запись в приложении" };
        startButton.Clicked += (_, _) => MainActivity()?.StartAppCapture();

        var stopButton = new Button { Text = "Стоп ■" };
        stopButton.Clicked += (_, _) => MainActivity()?.StopCapture();

        var reminderButton = new Button { Text = "Напоминание через 1 минуту" };
        reminderButton.Clicked += (_, _) => MainActivity()?.ScheduleTestReminder();

        var cancelReminderButton = new Button { Text = "Отменить напоминание" };
        cancelReminderButton.Clicked += (_, _) => MainActivity()?.CancelTestReminder();

        var hintLabel = new Label
        {
            Text = "Spike: ручной стоп — паузы не останавливают запись. Лимит 10 минут, предупреждение в 09:30. ASR — этап 2.",
            FontSize = 12,
        };

        statusLabel = new Label
        {
            Text = "Готово",
            FontSize = 16,
            TextColor = ColorReady,
        };

        Content = new VerticalStackLayout
        {
            Spacing = 16,
            Padding = new Thickness(24, 48),
            Children = { statusLabel, startButton, stopButton, reminderButton, cancelReminderButton, hintLabel },
        };

        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            var (text, color) = BuildStatus();
            statusLabel.Text = text;
            statusLabel.TextColor = color;
            return true; // продолжать
        });
    }

    private static (string Text, Color Color) BuildStatus()
    {
        var sessionStore = VoiceSessionStore.Instance;
        if (sessionStore.Running && sessionStore.ActiveSource is { } source)
        {
            var elapsed = sessionStore.StartedAt is { } started ? (DateTime.Now - started).ToString(@"hh\:mm\:ss") : "—";
            return ($"Запись: {source}… {elapsed} (стоп вручную или через уведомление)", ColorRecording);
        }

        var appStatus = AppStatusStore.Instance;
        var error = appStatus.GetError();
        if (!string.IsNullOrEmpty(error))
        {
            return (error, ColorError);
        }

        var info = appStatus.GetInfo();
        if (!string.IsNullOrEmpty(info))
        {
            return (info, ColorRecording);
        }

        return (sessionStore.LastResult ?? "Готово", ColorReady);
    }

    private static MainActivity? MainActivity() =>
        (MainActivity?)Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
}
