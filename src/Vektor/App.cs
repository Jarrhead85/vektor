using Microsoft.Maui.Controls;
using Microsoft.Maui.Essentials;

namespace Vektor;

/// <summary>Спайк-приложение: один экран ручной приёмки (AppManualCapture, exact reminder).</summary>
public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new SpikeMainPage());
    }
}
