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
        // Avalonia XAML ���������� ���� EMS/Modbus �ʱ�ȭ�� ���� ����
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
                $"���α׷� ���� | Version={GetType().Assembly.GetName().Version} | PC={Environment.MachineName}");

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                IModbusService modbusService = new ModbusService();
                var emsService = new EmsService(modbusService);

                var mobileTelemetryOptions =
                    new MobileTelemetryOptions
                    {
                        // =====================================================
                        // ���⿡ IP�� ǥ�õǴ� Ȩ�������� ��Ȯ�� �ּҸ� �Է��ϼ���.
                        // ��: "http://example.com/mobile-server-ip"
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

                    FileAppLogger.Info("APP", "���α׷� ����");
                };

                FileAppLogger.Info("APP", "���� ȭ�� �ʱ�ȭ �Ϸ�");
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            {
                // Android/모바일 첫 단계: 구조 확인용 화면만 띄웁니다.
                // EMS/Modbus 연동(CAN-over-Bluetooth)은 아직 준비되지 않았습니다.
                singleView.MainView = new AndroidPlaceholderView();

                FileAppLogger.Info("APP", "단일 화면(모바일) 초기화 완료 - EMS 연동 대기");
            }
        }
        catch (Exception ex)
        {
            FileAppLogger.Error(
                "APP",
                "���α׷� �ʱ�ȭ �� ������ �߻��߽��ϴ�.",
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
                    "ó������ ���� ���α׷� ���ܰ� �߻��߽��ϴ�.",
                    exception);
            }
            else
            {
                FileAppLogger.Error(
                    "APP",
                    $"ó������ ���� ���α׷� ���ܰ� �߻��߽��ϴ�. ����={eventArgs.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            FileAppLogger.Error(
                "APP",
                "ó������ ���� �񵿱� �۾� ���ܰ� �߻��߽��ϴ�.",
                eventArgs.Exception);

            eventArgs.SetObserved();
        };
    }
}