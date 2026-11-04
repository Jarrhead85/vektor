// Спайк-тесты state machine этапа 1 (docs/voice-state-machine.md).
// Fake clock: время передаётся явными DateTime, Thread.Sleep не используется.
// Пока без xunit (нулевые новые зависимости, restore согласуется отдельно): примитивный runner «PASS/FAIL», exit code.

using Vektor.Core;

const int FrameStepMs = 500;
var failures = 0;
var passed = 0;

void Check(string name, bool condition, string detail = "")
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"PASS  {name}");
    }
    else
    {
        failures++;
        Console.WriteLine($"FAIL  {name} {detail}");
    }
}

var t0 = new DateTime(2026, 11, 4, 20, 0, 0, DateTimeKind.Local);

List<VoiceSignal> Pump(VoiceSessionStateMachine m, bool speech, int fromMs, int toMs)
{
    var all = new List<VoiceSignal>();
    for (var t = fromMs; t <= toMs; t += FrameStepMs)
    {
        all.AddRange(m.AudioFrame(speech, T(t)));
    }
    return all;
}

DateTime T(int ms) => t0.AddMilliseconds(ms);

// 1. Виджет: firstSpeech в 2с, речь до 60с, тишина до 63с → автостоп на 63с.
{
    var m = new VoiceSessionStateMachine();
    Check("1a start виджет", m.TryStart(CaptureSource.WidgetQuickCapture, T(0)));
    m.AudioReady(T(0));
    var speech = Pump(m, speech: true, 0, 60_000);
    Check("1b нет автостопа во время речи и нет предупреждения лимита до 09:30", speech.All(s => s.Kind is not (VoiceSignalKind.AutoStopRequested or VoiceSignalKind.NoSpeechTimeout or VoiceSignalKind.LimitWarning)));

    var silence = Pump(m, speech: false, 60_500, 63_000);
    Check("1c автостоп ровно на 3с тишины", silence.Any(s => s.Kind == VoiceSignalKind.AutoStopRequested && Math.Abs(s.AtSeconds - 63.0) < 0.26),
        string.Join(", ", silence.Select(s => $"{s.Kind}@{s.AtSeconds:0.#}")));    Check("1c2 автостоп раньше конца тишины не приходил", silence.All(s => s.Kind == VoiceSignalKind.AutoStopRequested ? s.AtSeconds >= 63.0 - 0.01 : true));
    Check("1d Phase→FinalizeAudio(hasAudio)→Completed", m.FinalizeAudio(T(64_000), true) == VoicePhase.Completed);
}

// 2. Дедуп стопа: повторное нажатие виджета, второй вызов не_signal.
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.WidgetQuickCapture, T(0));
    m.AudioReady(T(0));
    _ = Pump(m, speech: true, 0, 3_000);
    Check("2a первый RequestStop=true", m.RequestStop(T(3_100)));
    Check("2b повторный RequestStop=false (single-flight)", !m.RequestStop(T(3_200)));
    Check("2c Phase всё ещё Recording до дозаписи", m.Phase == VoicePhase.Recording);
    Check("2d финализация → Completed", m.FinalizeAudio(T(3_300), true) == VoicePhase.Completed);
    Check("2e после Completed RequestStop=false", !m.RequestStop(T(3_400)));
}

// 3. AppManualCapture: тишина 3/10/40с НЕ останавливает запись (manual stop только).
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.AppManualCapture, T(0));
    m.AudioReady(T(0));
    _ = Pump(m, speech: true, 0, 5_000);
    var silence = Pump(m, speech: false, 5_500, 56_000); // 50 с тишины — больше 40с сценария
    Check("3a нет автостопа у AppManualCapture", silence.All(s => s.Kind != VoiceSignalKind.AutoStopRequested));
    Check("3b нет NoSpeechTimeout после речи", silence.All(s => s.Kind != VoiceSignalKind.NoSpeechTimeout));
    Check("3c Phase=Recording, тишина не мешает продолжать", m.Phase == VoicePhase.Recording);
}

// 4. Лимит 10 минут у AppManual: предупреждение в 09:30, стоп в 10:00. Сначала есть речь, потом паузы (manual).
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.AppManualCapture, T(0));
    m.AudioReady(T(0));
    _ = Pump(m, speech: true, 0, 5_000);
    var silence = Pump(m, speech: false, 5_500, 600_000);
    Check("4a предупреждение на 09:30", silence.Any(s => s.Kind == VoiceSignalKind.LimitWarning && s.AtSeconds >= 569.9 && s.AtSeconds <= 570.1));
    Check("4b LimitReached на 10:00", silence.Any(s => s.Kind == VoiceSignalKind.LimitReached && s.AtSeconds >= 600.0 && s.AtSeconds <= 600.3));
    Check("4c Limit идёт ровно один раз", silence.Count(s => s.Kind == VoiceSignalKind.LimitReached) == 1);
    Check("4d после Limit статус не встаёт до дозаписи", m.Phase == VoicePhase.Recording);
    Check("4e финализация → Completed", m.FinalizeAudio(T(600_500), true) == VoicePhase.Completed);
}

// 5. Начальная тишина: пустую заметку не создаём — NoSpeechTimeout, Cancelled.
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.WidgetQuickCapture, T(0));
    m.AudioReady(T(0));
    var silence = Pump(m, speech: false, 0, 16_000);
    Check("5a NoSpeechTimeout на 15с", silence.Any(s => s.Kind == VoiceSignalKind.NoSpeechTimeout && s.AtSeconds >= 15.0 && s.AtSeconds <= 15.3));
    Check("5b автостоп не подставлялся", silence.All(s => s.Kind != VoiceSignalKind.AutoStopRequested));
    Check("5c финализация без аудио → Cancelled", m.FinalizeAudio(T(16_000), false) == VoicePhase.Cancelled);
}

// 6. Одна сессия: повторный Start игнорируется.
{
    var m = new VoiceSessionStateMachine();
    Check("6a первый Start ок", m.TryStart(CaptureSource.AppManualCapture, T(0)));
    Check("6b повторный Start отброшен", !m.TryStart(CaptureSource.WidgetQuickCapture, T(100)));
    Check("6c источник не изменился", m.Source == CaptureSource.AppManualCapture);
}

// 7. Сигнальный второй тап виджета в тишине после речи: RequestStop, а не автостоп, приходит первым по времени.
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.WidgetQuickCapture, T(0));
    m.AudioReady(T(0));
    _ = Pump(m, speech: true, 0, 10_000);
    _ = Pump(m, speech: false, 10_500, 12_000);
    Check("7 ручной стак во время тишины принимается", m.RequestStop(T(11_000)));
    Check("7b после stopRequested upkeep молчит", m.Upkeep(T(11_500)).Count == 0);
}

Console.WriteLine($"---- passed={passed} failed={failures}");
return failures == 0 ? 0 : 1;
