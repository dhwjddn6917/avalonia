using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;

namespace MobileEssControl.Views.ExternalOutput;

public partial class ExternalOutputView : UserControl
{
    public ExternalOutputView()
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

        // 창 밖으로 삐져나가 스크롤이 생기지 않도록, 창 너비를 넘지 않는 선에서
        // 최대한 크게(여백 40px만 남기고) 잡습니다.
        double flyoutWidth =
            Math.Min(windowWidth - 40, 1800);

        SettingsFlyoutRoot.Width = flyoutWidth;

        // "Bottom" 배치는 버튼 왼쪽 끝에 맞춰 오른쪽으로 펼쳐지므로,
        // 버튼 오른쪽 끝에 맞춰 왼쪽으로 펼쳐지도록 오프셋을 보정합니다.
        if (sender is Button { Flyout: Flyout flyout } button)
        {
            flyout.HorizontalOffset =
                button.Bounds.Width - flyoutWidth;
        }
    }
}
