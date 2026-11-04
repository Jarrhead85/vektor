namespace Vektor;

/// <summary>Спайк-приложение: один экран ручной приёмки (AppManualCapture, exact reminder).</summary>
public sealed class App : Application
{
    public App()
    {
        MainPage = new SpikeMainPage();
    }
}
