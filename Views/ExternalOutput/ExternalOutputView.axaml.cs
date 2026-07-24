using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MobileEssControl.Views.ExternalOutput;

public partial class ExternalOutputView : UserControl
{
    public ExternalOutputView()
    {
        InitializeComponent();
    }

    private void OnSettingsButtonClick(object? sender, RoutedEventArgs e)
    {
        SettingsOverlayBackdrop.IsVisible = true;
    }

    private void OnCloseSettingsClick(object? sender, RoutedEventArgs e)
    {
        SettingsOverlayBackdrop.IsVisible = false;
    }

    private void OnSettingsOverlayBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        // 반투명 배경 자체를 클릭했을 때만 닫습니다 (안쪽 패널 클릭은 아래 핸들러가 막습니다).
        SettingsOverlayBackdrop.IsVisible = false;
    }

    private void OnSettingsOverlayPanelPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }
}
