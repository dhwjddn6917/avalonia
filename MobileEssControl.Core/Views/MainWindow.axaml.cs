using System;
using Avalonia.Controls;
using MobileEssControl.ViewModels;

namespace MobileEssControl.Views;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _closeRequestInProgress;

    public MainWindow()
    {
        InitializeComponent();

        Opened += MainWindow_Opened;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Opened(
        object? sender,
        EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.AutoConnectEmsOnStartupAsync();
        }
    }

    private async void MainWindow_Closing(
        object? sender,
        WindowClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        // 사용자가 X 또는 Alt+F4를 눌렀을 때
        // 즉시 종료하지 않고 안전 종료 절차를 먼저 실행합니다.
        e.Cancel = true;

        if (_closeRequestInProgress)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _closeRequestInProgress = true;

        try
        {
            bool canClose =
                await viewModel.RequestSafeShutdownAsync();

            if (!canClose)
            {
                return;
            }

            // 안전 종료 또는 통신 불가 강제 종료가 승인된 경우
            // 두 번째 Closing 이벤트를 통과시킵니다.
            _allowClose = true;
            Close();
        }
        finally
        {
            _closeRequestInProgress = false;
        }
    }
}
