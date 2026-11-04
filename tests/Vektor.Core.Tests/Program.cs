// Спайк-тесты этапа 1.1 (docs/voice-state-machine.md).
// Fake clock: время передаётся монотонными elapsedMs явно; Thread.Sleep не используется.
// Запуск ОБЯЗАТЕЛЬНО через: dotnet run --project tests/Vektor.Core.Tests/Vektor.Core.Tests.csproj
// dotnet test здесь НЕ используется. Runner: PASS/FAIL, exit code.

using Vektor.Core;

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

List<VoiceSignal> Pump(VoiceSessionStateMachine m, bool speech, int fromMs, int toMs, int stepMs = 500)
{
    var all = new List<VoiceSignal>();
    for (var t = fromMs; t <= toMs; t += stepMs)
    {
        all.AddRange(m.AudioFrame(speech, t));
    }
    return all;
}

const int FrameStepMs = 500;

// 1. Виджет: firstSpeech, речь до 60с, тишина до 63с → автостоп на 63с (монотонные elapsedMs).
{
    var m = new VoiceSessionStateMachine();
    Check("1a start виджет", m.TryStart(CaptureSource.WidgetQuickCapture, 0));
    m.AudioReady(0);
    var speech = Pump(m, speech: true, 0, 60_000);
    Check("1b нет автостопа во время речи/предупреждения до 09:30", speech.All(s => s.Kind is not (VoiceSignalKind.AutoStopRequested or VoiceSignalKind.NoSpeechTimeout or VoiceSignalKind.LimitWarning)));

    var silence = Pump(m, speech: false, 60_500, 63_000);
    Check("1c автостоп ровно на 3с тишины", silence.Any(s => s.Kind == VoiceSignalKind.AutoStopRequested && Math.Abs(s.AtSeconds - 63.0) < 0.26),
        string.Join(", ", silence.Select(s => $"{s.Kind}@{s.AtSeconds:0.#}")));
    Check("1d фаза Finalizing сразу после сигнала", m.Phase == VoicePhase.Finalizing);
    Check("1e финализация → Completed", m.FinalizeAudio(64_000, true) == VoicePhase.Completed);
    Check("1f FinalizeAudio после Completed — no-op", m.FinalizeAudio(64_500, true) == VoicePhase.Completed);
}

// 2. Дедуп стопа: RequestStop true один раз; повторные — no-op.
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.WidgetQuickCapture, 0);
    m.AudioReady(0);
    _ = Pump(m, speech: true, 0, 3_000);
    Check("2a первый RequestStop=true", m.RequestStop(3_100));
    Check("2b повторный RequestStop=false (single-flight)", !m.RequestStop(3_200));
    Check("2c Phase=Finalizing, не Completed", m.Phase == VoicePhase.Finalizing);
    Check("2d StopReason=User", m.StopReason == VoiceStopReason.User);
    Check("2e финализация → Completed", m.FinalizeAudio(3_300, true) == VoicePhase.Completed);
    Check("2f RequestStop после Completed=false", !m.RequestStop(3_400));
}

// 3. AppManualCapture: тишина 3/10/40с НЕ останавливает запись (manual stop только).
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.AppManualCapture, 0);
    m.AudioReady(0);
    _ = Pump(m, speech: true, 0, 5_000);
    var silence = Pump(m, speech: false, 5_500, 56_000);
    Check("3a нет автостопа у AppManualCapture", silence.All(s => s.Kind != VoiceSignalKind.AutoStopRequested));
    Check("3b нет NoSpeechTimeout после речи", silence.All(s => s.Kind != VoiceSignalKind.NoSpeechTimeout));
    Check("3c Phase=Recording, тишина не мешает", m.Phase == VoicePhase.Recording);
}

// 4. Лимит 10 минут у AppManual: предупреждение в 09:30, стоп в 10:00.
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.AppManualCapture, 0);
    m.AudioReady(0);
    _ = Pump(m, speech: true, 0, 5_000);
    var silence = Pump(m, speech: false, 5_500, 600_000);
    Check("4a предупреждение на 09:30", silence.Any(s => s.Kind == VoiceSignalKind.LimitWarning && s.AtSeconds >= 569.9 && s.AtSeconds <= 570.1));
    Check("4b LimitReached на 10:00", silence.Any(s => s.Kind == VoiceSignalKind.LimitReached && s.AtSeconds >= 600.0 && s.AtSeconds <= 600.3));
    Check("4c Limit идёт ровно один раз", silence.Count(s => s.Kind == VoiceSignalKind.LimitReached) == 1);
    Check("4d после Limit фаза Finalizing", m.Phase == VoicePhase.Finalizing);
    Check("4e финализация → Completed", m.FinalizeAudio(600_500, true) == VoicePhase.Completed);
}

// 5. Начальная тишина: NoSpeechTimeout → Cancelled; пустую заметку не создаём.
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.WidgetQuickCapture, 0);
    m.AudioReady(0);
    var silence = Pump(m, speech: false, 0, 16_000);
    Check("5a NoSpeechTimeout на 15с", silence.Any(s => s.Kind == VoiceSignalKind.NoSpeechTimeout && s.AtSeconds >= 15.0 && s.AtSeconds <= 15.3));
    Check("5b автостоп не подставлялся", silence.All(s => s.Kind != VoiceSignalKind.AutoStopRequested));
    Check("5c FinalizeAudio(false) → Cancelled (не Failed)", m.FinalizeAudio(16_000, false) == VoicePhase.Cancelled);
}

// 6. Одна сессия: повторный Start игнорируется; источник неизменяем.
{
    var m = new VoiceSessionStateMachine();
    Check("6a первый Start ок", m.TryStart(CaptureSource.AppManualCapture, 0));
    Check("6b повторный Start отброшен", !m.TryStart(CaptureSource.WidgetQuickCapture, 100));
    Check("6c источник не изменился", m.Source == CaptureSource.AppManualCapture);
}

// 7. Ручной стоп виджета во время тишины; upkeep после — молчит.
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.WidgetQuickCapture, 0);
    m.AudioReady(0);
    _ = Pump(m, speech: true, 0, 10_000);
    _ = Pump(m, speech: false, 10_500, 12_000);
    Check("7 ручной стоп во время тишины принимается", m.RequestStop(11_000));
    Check("7b после stopRequested upkeep молчит", m.Upkeep(11_500).Count == 0);
}

// 8. MicError ≠ тишина: с фрагментом → Completed; без фрагмента → Failed.
{
    var m1 = new VoiceSessionStateMachine();
    m1.TryStart(CaptureSource.AppManualCapture, 0);
    m1.AudioReady(0);
    _ = Pump(m1, speech: true, 0, 5_000);
    Check("8a Fail(MicError) после речи принят", m1.Fail(VoiceStopReason.MicError, 5_100));
    Check("8b фаза Finalizing", m1.Phase == VoicePhase.Finalizing);
    Check("8c StopReason=MicError", m1.StopReason == VoiceStopReason.MicError);
    Check("8d финализация с аудио → Completed", m1.FinalizeAudio(5_200, true) == VoicePhase.Completed);
    Check("8e снапшот хранит reason", m1.Snapshot(6_000).StopReason == VoiceStopReason.MicError);

    var m2 = new VoiceSessionStateMachine();
    m2.TryStart(CaptureSource.AppManualCapture, 0);
    m2.AudioReady(0);
    _ = Pump(m2, speech: false, 0, 2_000);
    Check("8f Fail(MicError) без речи принят", m2.Fail(VoiceStopReason.MicError, 2_100));
    Check("8g без аудио и MicError → Failed (не маскируем тишиной)", m2.FinalizeAudio(2_200, false) == VoicePhase.Failed);
}

// 9. Fail НЕ перезатирает другую причину; FinalizeAudio вне Finalizing не меняет состояние.
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.WidgetQuickCapture, 0);
    m.AudioReady(0);
    _ = Pump(m, speech: true, 0, 2_000);
    Check("9a UserStop принят", m.RequestStop(2_100));
    Check("9b Fail после конечной причины отклонён", !m.Fail(VoiceStopReason.MicError, 2_200));
    Check("9c StopReason остался User", m.StopReason == VoiceStopReason.User);

    var n = new VoiceSessionStateMachine();
    n.TryStart(CaptureSource.AppManualCapture, 0);
    n.AudioReady(10_000);
    Check("9d FinalizeAudio вне Finalizing — no-op", n.FinalizeAudio(10_100, false) == VoicePhase.Recording);
    Check("9e Phase остался Recording", n.Phase == VoicePhase.Recording);
}

// 10. RequestStop в Starting → Finalizing; AudioReady потом не «оживляет»; финализация → Cancelled.
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.AppManualCapture, 0);
    Check("10a RequestStop в Starting", m.RequestStop(500));
    Check("10b Phase=Finalizing", m.Phase == VoicePhase.Finalizing);
    m.AudioReady(1_000);
    Check("10c AudioReady после стопа — no-op", m.Phase == VoicePhase.Finalizing);
    Check("10d финализация → Cancelled", m.FinalizeAudio(1_100, false) == VoicePhase.Cancelled);
}

// 11. Монотонность: убывающий elapsedMs игнорируется.
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.WidgetQuickCapture, 0);
    m.AudioReady(0);
    _ = Pump(m, speech: false, 0, 10_000);
    var before = m.Snapshot(10_000);
    m.AudioFrame(speech: true, nowMs: 9_000);
    var after = m.Snapshot(10_000);
    Check("11a кадр назад — SpeechSeen не изменился", before.SpeechSeen == after.SpeechSeen && before.SpeechSeen == false);
    Check("11b Phase остался Recording", after.Phase == VoicePhase.Recording);
    m.AudioFrame(speech: true, nowMs: 10_500);
    Check("11c корректный прямой кадр принимается", m.Snapshot(10_500).SpeechSeen);
}

// 12. Snapshot: фаза, источник, elapsed живой/замороженный, StopReason.
{
    var m = new VoiceSessionStateMachine();
    m.TryStart(CaptureSource.WidgetQuickCapture, 0);
    var snapStarting = m.Snapshot(10_000);
    Check("12a фаза Starting в snapshot", snapStarting.Phase == VoicePhase.Starting);
    Check("12b Source передан", snapStarting.Source == CaptureSource.WidgetQuickCapture);

    m.AudioReady(0);
    _ = Pump(m, speech: true, 0, 5_000);
    var snapRec = m.Snapshot(7_000);
    Check("12c Recording elapsed ≈7с", Math.Abs(snapRec.ElapsedSeconds - 7.0) < 0.05, $"{snapRec.ElapsedSeconds:0.###}");

    m.RequestStop(7_500);
    var final = m.Snapshot(8_000);
    Check("12d Finalizing", final.Phase == VoicePhase.Finalizing);
    Check("12e elapsed заморожен на 7.5с", Math.Abs(final.ElapsedSeconds - 7.5) < 0.05, $"{final.ElapsedSeconds:0.###}");
    Check("12f StopReason=User", final.StopReason == VoiceStopReason.User);
}

// 13. ReminderRules: version/trigger identity, stale и ранний fire отбрасываются.
{
    var cur = new ReminderIdentity(3, 1_100_000L);

    Check("13a после срока — принимается (поздний fire)", ReminderRules.IsApplicable(new(3, 1_100_000), cur, 1_200_000));
    Check("13a2 срабатывание ровно на trigger — принимается", ReminderRules.IsApplicable(new(3, 1_100_000), cur, 1_100_000));
    Check("13b на 30с раньше терпимости — принимается", ReminderRules.IsApplicable(new(3, 1_100_000), cur, 1_090_000));
    Check("13c stale version (1) отброшена", !ReminderRules.IsApplicable(new(1, 1_100_000), cur, 1_100_000));
    Check("13d другой trigger при той же версии отброшен", !ReminderRules.IsApplicable(new(3, 2_000_000), cur, 1_100_000));
    Check("13e сильно раньше срока (>30с) отброшен", !ReminderRules.IsApplicable(new(3, 1_100_000), cur, 1_060_000));
    Check("13f без current (после отмены) отброшен", !ReminderRules.IsApplicable(new(3, 1_100_000), null, 1_100_000));
    Check("13g version ≤0 отброшен", !ReminderRules.IsApplicable(new(0, 1_100_000), cur, 1_100_000));
}

// 14. AppStatusStore: Set/Get/Clear/ClearAll (чистый Core, без Android).
{
    var s1 = AppStatusStore.Instance;
    s1.SetError("err");
    Check("14a error выставлен и читается", s1.GetError() == "err");
    s1.ClearError();
    Check("14b ClearError работает", s1.GetError() is null);
    s1.SetInfo("info");
    Check("14c info выставлен и читается", s1.GetInfo() == "info");
    s1.SetError("e2");
    s1.ClearAll();
    Check("14d ClearAll чистит оба", s1.GetInfo() is null && s1.GetError() is null);
}

Console.WriteLine($"---- passed={passed} failed={failures}");
return failures == 0 ? 0 : 1;
