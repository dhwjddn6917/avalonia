using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace MobileEssControl.Android;

[Application]
public class MainApplication : AvaloniaAndroidApplication<MobileEssControl.App>
{
    public MainApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
