using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;

namespace MobileEssControl.Views.AutoCharge;

public enum ChargeFlowState
{
    Ready,
    Starting,
    Charging,
    Stopping,
    Stopped,
    Completed,
    Fault,
    Disconnected
}
public partial class AutoChargeView : UserControl
{
    public AutoChargeView()
    {
        InitializeComponent();
    }

    private void OnSettingsButtonClick(object? sender, RoutedEventArgs e)
    {
        if (SettingsFlyoutRoot is null)
        {
            return;
        }

        double windowWidth =
            TopLevel.GetTopLevel(this) is Window window
                ? window.Bounds.Width
                : 1920;

        SettingsFlyoutRoot.Width =
            Math.Clamp(windowWidth * 0.7, 640, 1360);
    }
}
