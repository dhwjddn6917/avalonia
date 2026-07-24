using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace MobileEssControl.Android;

[Activity(
    Label = "MobileEssControl",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation |
                            ConfigChanges.ScreenSize |
                            ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<MobileEssControl.App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
