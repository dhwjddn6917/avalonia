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

        double flyoutWidth =
            Math.Clamp(windowWidth * 0.7, 640, 1360);

        SettingsFlyoutRoot.Width = flyoutWidth;

        // "Bottom" 배치는 버튼 왼쪽 끝에 맞춰 오른쪽으로 펼쳐지므로,
        // 버튼 오른쪽 끝에 맞춰 왼쪽으로 펼쳐지도록 오프셋을 보정합니다.
        if (sender is Button button && SettingsFlyout is not null)
        {
            SettingsFlyout.HorizontalOffset =
                button.Bounds.Width - flyoutWidth;
        }
    }
}
