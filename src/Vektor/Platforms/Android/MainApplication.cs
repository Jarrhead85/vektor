using Android.App;
using Android.Runtime;
using Microsoft.Maui;

namespace Vektor.Platforms.Android;

[Application(AllowBackup = false)]
public sealed class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
