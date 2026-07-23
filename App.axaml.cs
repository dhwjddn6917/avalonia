using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

using Avalonia.Markup.Xaml;
using MobileEssControl.Services.Ems;
using MobileEssControl.Services.Interfaces;
using MobileEssControl.Services.Logging;
using MobileEssControl.Services.Modbus;
using MobileEssControl.Services.Mobile;
using MobileEssControl.ViewModels;
using MobileEssControl.Views;
using System;
using System.Threading.Tasks;

namespace MobileEssControl;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Avalonia XAML 프리뷰어에서는 실제 EMS/Modbus 초기화를 하지 않음
        if (Design.IsDesignMode)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        RegisterGlobalExceptionLogging();
        FileAppLogger.IsDetailedCommunicationLogEnabled = false;
        try
        {
            FileAppLogger.Info(
                "APP",
                $"프로그램 시작 | Version={GetType().Assembly.GetName().Version} | PC={Environment.MachineName}");

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                IModbusService modbusService = new ModbusService();
                var emsService = new EmsService(modbusService);

                var mobileTelemetryOptions =
                    new MobileTelemetryOptions
                    {
                        // =====================================================
                        // 여기에 IP만 표시되는 홈페이지의 정확한 주소를 입력하세요.
                        // 예: "http://example.com/mobile-server-ip"
                        // =====================================================
                        IpLookupUrl = "https://evkmc.kr:8091/serverip",

                        ServerPort = 19991,
                        EssCarId = "evkmc_vcess_MobESS01"
                    };

                var mobileTelemetryService =
                    new MobileTelemetryService(
                        emsService,
                        mobileTelemetryOptions);

                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(emsService)
                };

                mobileTelemetryService.Start();

                desktop.Exit += (_, _) =>
                {
                    mobileTelemetryService.Dispose();

                    FileAppLogger.Info("APP", "프로그램 종료");
                };

                FileAppLogger.Info("APP", "메인 화면 초기화 완료");
            }
        }
        catch (Exception ex)
        {
            FileAppLogger.Error(
                "APP",
                "프로그램 초기화 중 오류가 발생했습니다.",
                ex);

            throw;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RegisterGlobalExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                FileAppLogger.Error(
                    "APP",
                    "처리되지 않은 프로그램 예외가 발생했습니다.",
                    exception);
            }
            else
            {
                FileAppLogger.Error(
                    "APP",
                    $"처리되지 않은 프로그램 예외가 발생했습니다. 내용={eventArgs.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            FileAppLogger.Error(
                "APP",
                "처리되지 않은 비동기 작업 예외가 발생했습니다.",
                eventArgs.Exception);

            eventArgs.SetObserved();
        };
    }
}