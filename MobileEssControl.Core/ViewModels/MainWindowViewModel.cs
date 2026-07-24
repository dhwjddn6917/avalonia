using System;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MobileEssControl.Constants;
using MobileEssControl.Services.Dialogs;
using MobileEssControl.Services.Ems;
using MobileEssControl.ViewModels.Admin;
using MobileEssControl.ViewModels.AutoCharge;
using MobileEssControl.ViewModels.ExternalOutput;
using MobileEssControl.ViewModels.GridDischarge;
using MobileEssControl.ViewModels.MainModeSelect;


namespace MobileEssControl.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    // 관리자 화면 진입용 비밀번호입니다.
    // 비밀번호를 변경할 때는 이 값만 수정하면 됩니다.
    private const string AdminPassword = "1234";

    private readonly EmsService _emsService;
    private readonly DispatcherTimer _menuLockTimer;
    private readonly DispatcherTimer _panelLedBlinkTimer;

    private bool _isMenuLockRefreshing;

    private static readonly TimeSpan AutoReconnectInterval =
        TimeSpan.FromSeconds(5);

    private bool _isAutoReconnecting;

    private DateTime _nextAutoReconnectAttemptUtc =
        DateTime.MinValue;

    // 연결이 끊겼을 때 임의로 메뉴 잠금을 풀지 않기 위해
    // 마지막으로 정상 수신한 Mode / Run 상태를 보관합니다.
    private bool _hasLastKnownEmsStatus;

    private EmsOperationMode _lastKnownOperationMode =
        EmsOperationMode.Standby;

    private bool _lastKnownIsRunning;

    // 시작/정지 명령 도중 통신이 끊긴 경우에는
    // 재연결 후 실제 상태를 읽기 전까지 화면 이동을 잠급니다.
    private bool _requiresStateConfirmationAfterReconnect;

    // =========================================================
    // 실제 조작 패널 LED
    //
    // P/W      : 31023 System Status2 Bit11 PowerSwitchSts
    // 입/출력   : 31022 System Status1 Bit14 LampOutput
    // 알람      : 31022 System Status1 Bit12 LampAlarm
    // 고장      : 31022 System Status1 Bit13 LampFault
    // 비상정지  : 31023 System Status2 Bit10 EmStopSwitchSts
    // =========================================================

    // OFF 또는 상태 확인 불가일 때 사용하는 어두운 표시색입니다.
    private static readonly IBrush LedUnknownBrush =
        new SolidColorBrush(Color.Parse("#475569"));

    // P/W와 입/출력은 ON 상태에서 녹색으로 고정 표시합니다.
    private static readonly IBrush PowerLedOnBrush =
        new SolidColorBrush(Color.Parse("#22C55E"));

    private static readonly IBrush OutputLedOnBrush =
        new SolidColorBrush(Color.Parse("#22C55E"));

    // 알람은 노란색, 고장과 비상정지는 빨간색으로 점멸합니다.
    private static readonly IBrush AlarmLedOnBrush =
        new SolidColorBrush(Color.Parse("#FACC15"));

    private static readonly IBrush FaultLedOnBrush =
        new SolidColorBrush(Color.Parse("#EF4444"));

    private static readonly IBrush EmergencyStopLedOnBrush =
        new SolidColorBrush(Color.Parse("#EF4444"));

    // 마지막으로 수신한 실제 LED 상태를 보관합니다.
    // 알람/고장/비상정지 상태가 ON이면 _isPanelLedBlinkOn 값에 따라
    // 점등색과 어두운색을 1초마다 번갈아 표시합니다.
    private bool _hasPanelLedStatus;
    private bool _isPanelLedBlinkOn = true;
    private bool _isPowerLedOn;
    private bool _isOutputLedOn;
    private bool _isAlarmLedOn;
    private bool _isFaultLedOn;
    private bool _isEmergencyStopLedOn;

    // 상단 공통 상태창 색상
    private static readonly IBrush CommonStatusNeutralBrush =
        new SolidColorBrush(Color.Parse("#CBD5E1"));

    private static readonly IBrush CommonStatusRunningBrush =
        new SolidColorBrush(Color.Parse("#86EFAC"));

    private static readonly IBrush CommonStatusWarningBrush =
        new SolidColorBrush(Color.Parse("#FDE68A"));

    private static readonly IBrush CommonStatusErrorBrush =
        new SolidColorBrush(Color.Parse("#FCA5A5"));

    private IBrush _powerLedBrush = LedUnknownBrush;

    public IBrush PowerLedBrush
    {
        get => _powerLedBrush;
        private set => SetProperty(ref _powerLedBrush, value);
    }

    private IBrush _outputLedBrush = LedUnknownBrush;

    public IBrush OutputLedBrush
    {
        get => _outputLedBrush;
        private set => SetProperty(ref _outputLedBrush, value);
    }

    private IBrush _alarmLedBrush = LedUnknownBrush;

    public IBrush AlarmLedBrush
    {
        get => _alarmLedBrush;
        private set => SetProperty(ref _alarmLedBrush, value);
    }

    private IBrush _faultLedBrush = LedUnknownBrush;

    public IBrush FaultLedBrush
    {
        get => _faultLedBrush;
        private set => SetProperty(ref _faultLedBrush, value);
    }

    private IBrush _emergencyStopLedBrush = LedUnknownBrush;

    public IBrush EmergencyStopLedBrush
    {
        get => _emergencyStopLedBrush;
        private set => SetProperty(ref _emergencyStopLedBrush, value);
    }

    private string _powerLedStateText =
        "P/W 상태 확인 전";

    public string PowerLedStateText
    {
        get => _powerLedStateText;
        private set => SetProperty(ref _powerLedStateText, value);
    }

    private string _outputLedStateText =
        "입/출력 상태 확인 전";

    public string OutputLedStateText
    {
        get => _outputLedStateText;
        private set => SetProperty(ref _outputLedStateText, value);
    }

    private string _alarmLedStateText =
        "알람 상태 확인 전";

    public string AlarmLedStateText
    {
        get => _alarmLedStateText;
        private set => SetProperty(ref _alarmLedStateText, value);
    }

    private string _faultLedStateText =
        "고장 상태 확인 전";

    public string FaultLedStateText
    {
        get => _faultLedStateText;
        private set => SetProperty(ref _faultLedStateText, value);
    }

    private string _emergencyStopStateText =
        "비상정지 상태 확인 전";

    public string EmergencyStopStateText
    {
        get => _emergencyStopStateText;
        private set => SetProperty(ref _emergencyStopStateText, value);
    }

    private ViewModelBase _currentPage = null!;

    public ViewModelBase CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    private string _connectionStatus = "EMS 연결 준비";

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetProperty(ref _connectionStatus, value);
    }

    private IBrush _connectionStatusBrush =
        CommonStatusNeutralBrush;

    public IBrush ConnectionStatusBrush
    {
        get => _connectionStatusBrush;
        private set => SetProperty(
            ref _connectionStatusBrush,
            value);
    }

    private string _menuLockStatusText =
        "대기 / 메뉴 전체 사용 가능";

    public string MenuLockStatusText
    {
        get => _menuLockStatusText;
        set => SetProperty(ref _menuLockStatusText, value);
    }

    private bool _isAutoChargeMenuEnabled = true;

    public bool IsAutoChargeMenuEnabled
    {
        get => _isAutoChargeMenuEnabled;
        set => SetProperty(ref _isAutoChargeMenuEnabled, value);
    }

    private bool _isExternalOutputMenuEnabled = true;

    public bool IsExternalOutputMenuEnabled
    {
        get => _isExternalOutputMenuEnabled;
        set => SetProperty(ref _isExternalOutputMenuEnabled, value);
    }

    private bool _isGridDischargeMenuEnabled = true;

    public bool IsGridDischargeMenuEnabled
    {
        get => _isGridDischargeMenuEnabled;
        set => SetProperty(ref _isGridDischargeMenuEnabled, value);
    }

    private bool _isUpsMenuEnabled = true;

    public bool IsUpsMenuEnabled
    {
        get => _isUpsMenuEnabled;
        set => SetProperty(ref _isUpsMenuEnabled, value);
    }

    private bool _isAdminMenuEnabled = true;

    public bool IsAdminMenuEnabled
    {
        get => _isAdminMenuEnabled;
        set => SetProperty(ref _isAdminMenuEnabled, value);
    }

    private bool _isModeSelectNavigationEnabled = true;

    public bool IsModeSelectNavigationEnabled
    {
        get => _isModeSelectNavigationEnabled;
        private set => SetProperty(ref _isModeSelectNavigationEnabled, value);
    }

    private bool _isConnecting;

    public bool IsConnecting
    {
        get => _isConnecting;
        set => SetProperty(ref _isConnecting, value);
    }

    public MainWindowViewModel(EmsService emsService)
    {
        _emsService = emsService;
        _emsService.OperationSequenceStateChanged +=
            EmsService_OperationSequenceStateChanged;

        CurrentPage = CreateModeSelectViewModel();

        _menuLockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        // 기존 코드에는 이 연결이 빠져 있어서
        // 최초 1회 이후 메뉴 잠금 상태가 갱신되지 않았습니다.
        _menuLockTimer.Tick += MenuLockTimer_Tick;
        _menuLockTimer.Start();

        // 알람/고장/비상정지 표시등 점멸 전용 타이머입니다.
        // 1초마다 점등/소등 상태를 전환합니다.
        _panelLedBlinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _panelLedBlinkTimer.Tick +=
            PanelLedBlinkTimer_Tick;

        _panelLedBlinkTimer.Start();

        _ = RefreshMenuLockStateAsync();
    }

    private void EmsService_OperationSequenceStateChanged(
        bool isSequenceRunning)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (isSequenceRunning)
            {
                _requiresStateConfirmationAfterReconnect = true;

                IsModeSelectNavigationEnabled = false;
                IsAdminMenuEnabled = false;

                MenuLockStatusText =
                    "운전 명령 처리 중 / 화면 이동 잠금";

                UpdateCommonSequenceStatus();

                return;
            }

            // 시퀀스 종료 후 실제 Mode / Run / Fault 상태를 다시 읽어
            // 상단 공통 상태창을 갱신합니다.
            _ = RefreshMenuLockStateAsync();
        });
    }

    private async void MenuLockTimer_Tick(
        object? sender,
        EventArgs e)
    {
        await RefreshMenuLockStateAsync();
    }

    private void PanelLedBlinkTimer_Tick(
        object? sender,
        EventArgs e)
    {
        _isPanelLedBlinkOn =
            !_isPanelLedBlinkOn;

        ApplyPanelLedBrushes();
    }

    private MainModeSelectViewModel CreateModeSelectViewModel()
    {
        return new MainModeSelectViewModel(
     ShowAutoCharge,
     ShowExternalOutput,
     ShowGridDischarge);
    }

    private void Navigate(ViewModelBase nextPage)
    {
        if (CurrentPage is IDisposable disposablePage)
        {
            disposablePage.Dispose();
        }

        CurrentPage = nextPage;
    }

    private async Task RefreshMenuLockStateAsync()
    {
        if (_isMenuLockRefreshing)
        {
            return;
        }

        if (!_emsService.IsConnected)
        {
            await HandleDisconnectedStateAsync();
            return;
        }

        // 시작/정지 시퀀스 중에는 EmsService가 상태 Read를 잠그므로,
        // 기존 메뉴 상태를 유지하고 다음 Tick에서 다시 확인합니다.
        if (_emsService.IsOperationSequenceRunning)
        {
            IsModeSelectNavigationEnabled = false;
            IsAdminMenuEnabled = false;

            MenuLockStatusText =
                "운전 명령 처리 중 / 화면 이동 잠금";

            UpdateCommonSequenceStatus();

            return;
        }

        _isMenuLockRefreshing = true;

        try
        {
            var status =
                await _emsService.ReadStatusAsync();

            EmsOperationMode currentMode =
                EmsControlWord1.EmsSystemStatus1.GetOperatingMode(
                    status.SystemStatus1);

            bool isRunning =
                EmsControlWord1.EmsSystemStatus2.IsRunning(
                    status.SystemStatus2);

            _hasLastKnownEmsStatus = true;
            _lastKnownOperationMode = currentMode;
            _lastKnownIsRunning = isRunning;
            _requiresStateConfirmationAfterReconnect = false;

            UpdatePanelLedState(
                status.SystemStatus1,
                status.SystemStatus2);

            UpdateCommonSystemStatus(
                currentMode,
                isRunning,
                status.SystemStatus1,
                status.SystemStatus2,
                status.AlarmStatus1);

            ApplyMenuLock(
                currentMode,
                isRunning);
        }
        catch
        {
            // ModbusService에서 실제 통신 단절을 감지하면 포트를 닫아
            // IsConnected가 false가 됩니다. 이 경우 다음 Tick을 기다리지 않고
            // 자동 재연결 상태를 바로 표시합니다.
            if (!_emsService.IsConnected)
            {
                await HandleDisconnectedStateAsync();
                return;
            }

            // CRC 오류처럼 포트는 열려 있지만 응답이 올바르지 않은 경우에는
            // 기존 운전 잠금을 임의로 풀지 않습니다.
            SetPanelLedsUnknown("EMS 상태 읽기 실패");

            ConnectionStatus =
                "EMS 통신 응답 오류 · 상태 확인 필요";

            ConnectionStatusBrush =
                CommonStatusErrorBrush;

            MenuLockStatusText =
                IsModeSelectNavigationEnabled
                    ? "상태 확인 불가 / 현재 화면 유지"
                    : "상태 확인 불가 / 안전을 위해 화면 이동 잠금 유지";
        }
        finally
        {
            _isMenuLockRefreshing = false;
        }
    }

    private async Task HandleDisconnectedStateAsync()
    {
        SetPanelLedsUnknown("EMS 미연결");

        bool mustKeepNavigationLocked =
            _requiresStateConfirmationAfterReconnect ||
            _emsService.IsOperationSequenceRunning ||
            (_hasLastKnownEmsStatus &&
             (_lastKnownIsRunning ||
              _lastKnownOperationMode != EmsOperationMode.Standby));

        if (mustKeepNavigationLocked)
        {
            LockAllMenusForDisconnectedState();

            MenuLockStatusText =
                "EMS 연결 끊김 / 안전을 위해 화면 이동 잠금 유지";
        }
        else
        {
            UnlockAllMenus();
            IsModeSelectNavigationEnabled = true;

            MenuLockStatusText =
                "EMS 미연결 / 운전 명령 사용 불가";
        }

        // 관리자 화면에서 사용자가 직접 '연결 해제'를 선택한 경우에는
        // 자동으로 다시 연결하지 않습니다. 관리자 연결 버튼으로 재연결합니다.
        if (_emsService.IsAutoReconnectSuppressed)
        {
            ConnectionStatus =
                "EMS 수동 연결 해제";

            ConnectionStatusBrush =
                CommonStatusNeutralBrush;

            return;
        }

        if (_isAutoReconnecting)
        {
            ConnectionStatus =
                "EMS 연결 끊김 · 자동 재연결 중...";

            ConnectionStatusBrush =
                CommonStatusWarningBrush;

            return;
        }

        if (DateTime.UtcNow <
            _nextAutoReconnectAttemptUtc)
        {
            ConnectionStatus =
                _hasLastKnownEmsStatus
                    ? "EMS 연결 끊김 · 자동 재연결 대기"
                    : "EMS 연결 실패 · 자동 재연결 대기";

            ConnectionStatusBrush =
                CommonStatusErrorBrush;

            return;
        }

        await TryAutoReconnectAsync();
    }

    private async Task TryAutoReconnectAsync()
    {
        if (_isAutoReconnecting ||
            IsConnecting ||
            _emsService.IsConnected ||
            _emsService.IsOperationSequenceRunning ||
            _emsService.IsAutoReconnectSuppressed)
        {
            return;
        }

        _isAutoReconnecting = true;

        _nextAutoReconnectAttemptUtc =
            DateTime.UtcNow + AutoReconnectInterval;

        ConnectionStatus =
            _hasLastKnownEmsStatus
                ? "EMS 연결 끊김 · 자동 재연결 중..."
                : "EMS 자동 연결 중...";

        ConnectionStatusBrush =
            CommonStatusWarningBrush;

        try
        {
            string? portName =
                await _emsService.FindAndConnectAsync();

            if (portName is null)
            {
                ConnectionStatus =
                    "EMS 재연결 실패 · 5초 후 다시 시도";

                ConnectionStatusBrush =
                    CommonStatusErrorBrush;

                return;
            }

            ConnectionStatus =
                $"EMS 재연결됨 · {portName} · 상태 확인 중";

            ConnectionStatusBrush =
                CommonStatusWarningBrush;

            _nextAutoReconnectAttemptUtc =
                DateTime.MinValue;
        }
        catch
        {
            ConnectionStatus =
                "EMS 재연결 오류 · 5초 후 다시 시도";

            ConnectionStatusBrush =
                CommonStatusErrorBrush;
        }
        finally
        {
            _isAutoReconnecting = false;
        }
    }

    private void LockAllMenusForDisconnectedState()
    {
        IsAutoChargeMenuEnabled = false;
        IsExternalOutputMenuEnabled = false;
        IsGridDischargeMenuEnabled = false;

        IsAdminMenuEnabled = false;
        IsModeSelectNavigationEnabled = false;
    }

    private void UpdateCommonSequenceStatus()
    {
        string sequenceName =
            _emsService.CurrentOperationSequenceName ??
            "운전";

        string sequenceText =
            sequenceName switch
            {
                "자동충전" =>
                    "배터리 충전 시작 처리 중",

                "외부 전원 출력" =>
                    "외부 출력 시작 처리 중",

                "계통방전" =>
                    "계통 방전 시작 처리 중",

                "UPS" =>
                    "UPS 시작 처리 중",

                "운전 정지" =>
                    "운전 정지 처리 중",

                _ =>
                    $"{sequenceName} 명령 처리 중"
            };

        ConnectionStatus =
            $"{GetConnectedStatusPrefix()} · {sequenceText}";

        ConnectionStatusBrush =
            CommonStatusWarningBrush;
    }

    private void UpdateCommonSystemStatus(
        EmsOperationMode currentMode,
        bool isRunning,
        ushort systemStatus1,
        ushort systemStatus2,
        ushort alarmStatus1)
    {
        ushort systemFaultLevel =
            EmsControlWord1.EmsSystemStatus1.GetSystemFaultLevel(
                systemStatus1);

        bool hasPackOrInverterFault =
            EmsControlWord1.EmsSystemAlarms1.HasPackOrInverterFault(
                alarmStatus1);

        bool hasCanFault =
            EmsControlWord1.EmsSystemAlarms1.HasCanFault(
                alarmStatus1);

        string prefix =
            GetConnectedStatusPrefix();

        bool isEmergencyStopOn =
            (systemStatus2 & (1 << 10)) != 0;

        if (isEmergencyStopOn)
        {
            ConnectionStatus =
                $"{prefix} · 비상정지 ON · 운전 시작 차단";

            ConnectionStatusBrush =
                CommonStatusErrorBrush;

            return;
        }

        if (systemFaultLevel != 0 ||
            hasPackOrInverterFault ||
            hasCanFault)
        {
            ConnectionStatus =
                hasCanFault
                    ? $"{prefix} · CAN 통신 고장 · 장비 상태 확인"
                    : $"{prefix} · 시스템 고장 · 장비 상태 확인";

            ConnectionStatusBrush =
                CommonStatusErrorBrush;

            return;
        }

        if (isRunning)
        {
            ConnectionStatus =
                $"{prefix} · {GetRunningStatusText(currentMode)}";

            ConnectionStatusBrush =
                CommonStatusRunningBrush;

            return;
        }

        if (currentMode == EmsOperationMode.Standby)
        {
            ConnectionStatus =
                $"{prefix} · 시스템 대기";

            ConnectionStatusBrush =
                CommonStatusNeutralBrush;

            return;
        }

        ConnectionStatus =
            $"{prefix} · {GetModeReadyStatusText(currentMode)}";

        ConnectionStatusBrush =
            CommonStatusWarningBrush;
    }

    private string GetConnectedStatusPrefix()
    {
        string? portName =
            _emsService.ConnectedPortName;

        return string.IsNullOrWhiteSpace(portName)
            ? "EMS 연결됨"
            : $"EMS 연결됨 · {portName}";
    }

    private static string GetRunningStatusText(
        EmsOperationMode mode)
    {
        return mode switch
        {
            EmsOperationMode.AutoCharge =>
                "배터리 충전 중",

            EmsOperationMode.ExternalOutput =>
                "외부 출력 중",

            EmsOperationMode.GridDischarge =>
                "계통 방전 중",

            (EmsOperationMode)7 =>
       "지원하지 않는 운전 모드 7 감지",

            _ =>
                $"{mode} 운전 중"
        };
    }

    private static string GetModeReadyStatusText(
        EmsOperationMode mode)
    {
        return mode switch
        {
            EmsOperationMode.AutoCharge =>
                "배터리 충전 준비 · Run=Stop",

            EmsOperationMode.ExternalOutput =>
                "외부 출력 준비 · Run=Stop",

            EmsOperationMode.GridDischarge =>
                "계통 방전 준비 · Run=Stop",

            (EmsOperationMode)7 =>
                "UPS 준비 · Run=Stop",

            _ =>
                $"{mode} · Run=Stop"
        };
    }

    private void UpdatePanelLedState(
        ushort systemStatus1,
        ushort systemStatus2)
    {
        // 엑셀 Address Map의 실제 Lamp/스위치 상태 비트를 그대로 표시합니다.
        bool isPowerOn =
            (systemStatus2 & (1 << 11)) != 0;

        bool isOutputOn =
            (systemStatus1 & (1 << 14)) != 0;

        bool isAlarmOn =
            (systemStatus1 & (1 << 12)) != 0;

        bool isFaultOn =
            (systemStatus1 & (1 << 13)) != 0;

        bool isEmergencyStopOn =
            (systemStatus2 & (1 << 10)) != 0;

        // 새 경고 상태가 발생했을 때는 첫 화면을 점등 상태로 시작합니다.
        bool warningChangedToOn =
            (!_isAlarmLedOn && isAlarmOn) ||
            (!_isFaultLedOn && isFaultOn) ||
            (!_isEmergencyStopLedOn && isEmergencyStopOn);

        _hasPanelLedStatus = true;
        _isPowerLedOn = isPowerOn;
        _isOutputLedOn = isOutputOn;
        _isAlarmLedOn = isAlarmOn;
        _isFaultLedOn = isFaultOn;
        _isEmergencyStopLedOn = isEmergencyStopOn;

        if (warningChangedToOn)
        {
            _isPanelLedBlinkOn = true;
        }

        ApplyPanelLedBrushes();

        PowerLedStateText =
            isPowerOn
                ? "P/W ON · 31023 Bit11=1"
                : "P/W OFF · 31023 Bit11=0";

        OutputLedStateText =
            isOutputOn
                ? "입/출력 ON · 31022 Bit14=1"
                : "입/출력 OFF · 31022 Bit14=0";

        AlarmLedStateText =
            isAlarmOn
                ? "알람 ON · 노란색 1초 점멸 · 31022 Bit12=1"
                : "알람 OFF · 31022 Bit12=0";

        FaultLedStateText =
            isFaultOn
                ? "고장 ON · 빨간색 1초 점멸 · 31022 Bit13=1"
                : "고장 OFF · 31022 Bit13=0";

        EmergencyStopStateText =
            isEmergencyStopOn
                ? "비상정지 ON · 빨간색 1초 점멸 · 31023 Bit10=1 · 운전 시작 차단"
                : "비상정지 해제 · 31023 Bit10=0";
    }

    private void ApplyPanelLedBrushes()
    {
        if (!_hasPanelLedStatus)
        {
            PowerLedBrush = LedUnknownBrush;
            OutputLedBrush = LedUnknownBrush;
            AlarmLedBrush = LedUnknownBrush;
            FaultLedBrush = LedUnknownBrush;
            EmergencyStopLedBrush = LedUnknownBrush;
            return;
        }

        // P/W와 입/출력은 ON일 때 녹색으로 계속 켜 둡니다.
        PowerLedBrush =
            _isPowerLedOn
                ? PowerLedOnBrush
                : LedUnknownBrush;

        OutputLedBrush =
            _isOutputLedOn
                ? OutputLedOnBrush
                : LedUnknownBrush;

        // 알람/고장/비상정지는 ON일 때만 1초 주기로 점멸합니다.
        AlarmLedBrush =
            _isAlarmLedOn && _isPanelLedBlinkOn
                ? AlarmLedOnBrush
                : LedUnknownBrush;

        FaultLedBrush =
            _isFaultLedOn && _isPanelLedBlinkOn
                ? FaultLedOnBrush
                : LedUnknownBrush;

        EmergencyStopLedBrush =
            _isEmergencyStopLedOn && _isPanelLedBlinkOn
                ? EmergencyStopLedOnBrush
                : LedUnknownBrush;
    }

    private void SetPanelLedsUnknown(
        string reason)
    {
        _hasPanelLedStatus = false;
        _isPowerLedOn = false;
        _isOutputLedOn = false;
        _isAlarmLedOn = false;
        _isFaultLedOn = false;
        _isEmergencyStopLedOn = false;

        ApplyPanelLedBrushes();

        PowerLedStateText = $"P/W 확인 불가 · {reason}";
        OutputLedStateText = $"입/출력 확인 불가 · {reason}";
        AlarmLedStateText = $"알람 확인 불가 · {reason}";
        FaultLedStateText = $"고장 확인 불가 · {reason}";
        EmergencyStopStateText = $"비상정지 확인 불가 · {reason}";
    }

    private void ApplyMenuLock(
        EmsOperationMode currentMode,
        bool isRunning)
    {
        if (!isRunning)
        {
            UnlockAllMenus();
            IsModeSelectNavigationEnabled = true;

            MenuLockStatusText =
                "대기 / 오퍼레이션 모드 이동 가능";

            return;
        }

        // 운전 중에는 현재 운전 화면에서 벗어날 수 없습니다.
        // 오퍼레이션 모드 선택 화면과 관리자 화면 이동을 모두 잠급니다.
        IsModeSelectNavigationEnabled = false;

        IsAutoChargeMenuEnabled =
            currentMode == EmsOperationMode.AutoCharge;

        IsExternalOutputMenuEnabled =
            currentMode == EmsOperationMode.ExternalOutput;

        IsGridDischargeMenuEnabled =
            currentMode == EmsOperationMode.GridDischarge;





        MenuLockStatusText =
            currentMode switch
            {
                EmsOperationMode.AutoCharge =>
                    "자동충전 운전 중 / 오퍼레이션 모드 이동 잠금",

                EmsOperationMode.ExternalOutput =>
                    "외부출력 운전 중 / 오퍼레이션 모드 이동 잠금",

                EmsOperationMode.GridDischarge =>
                    "계통방전 운전 중 / 오퍼레이션 모드 이동 잠금",

                _ =>
                    $"{currentMode} 운전 중 / 오퍼레이션 모드 이동 잠금"
            };

        EnsureCurrentOperationPage(currentMode);
    }

    private void EnsureCurrentOperationPage(
        EmsOperationMode currentMode)
    {
        if (CurrentPage is not MainModeSelectViewModel)
        {
            return;
        }

        ViewModelBase? operationPage =
            currentMode switch
            {
                EmsOperationMode.AutoCharge =>
                    new AutoChargeViewModel(_emsService),

                EmsOperationMode.ExternalOutput =>
                    new ExternalOutputViewModel(_emsService),

                EmsOperationMode.GridDischarge =>
                    new GridDischargeViewModel(_emsService),



                _ => null
            };

        if (operationPage is not null)
        {
            Navigate(operationPage);
        }
    }

    private void UnlockAllMenus()
    {
        IsAutoChargeMenuEnabled = true;
        IsExternalOutputMenuEnabled = true;
        IsGridDischargeMenuEnabled = true;
        IsUpsMenuEnabled = true;
        IsAdminMenuEnabled = true;
    }

    private bool CanOpenMode(
        bool isMenuEnabled,
        string requestedModeName)
    {
        if (isMenuEnabled)
        {
            return true;
        }

        ConnectionStatus =
            $"다른 모드 운전 중에는 {requestedModeName} 화면으로 이동할 수 없습니다.";

        return false;
    }

    public async Task AutoConnectEmsOnStartupAsync()
    {
        if (IsConnecting ||
            _isAutoReconnecting ||
            _emsService.IsConnected)
        {
            return;
        }

        IsConnecting = true;

        ConnectionStatus =
            "EMS USB 검색 중...";

        ConnectionStatusBrush =
            CommonStatusWarningBrush;

        try
        {
            string? portName =
                await _emsService.FindAndConnectAsync();

            if (portName is null)
            {
                _nextAutoReconnectAttemptUtc =
                    DateTime.UtcNow + AutoReconnectInterval;

                ConnectionStatus =
                    "EMS 연결 실패 · 5초 후 자동 재연결";

                ConnectionStatusBrush =
                    CommonStatusErrorBrush;
            }
            else
            {
                _nextAutoReconnectAttemptUtc =
                    DateTime.MinValue;

                ConnectionStatus =
                    $"EMS 연결됨 · {portName} · 상태 확인 중";

                ConnectionStatusBrush =
                    CommonStatusWarningBrush;
            }
        }
        catch
        {
            _nextAutoReconnectAttemptUtc =
                DateTime.UtcNow + AutoReconnectInterval;

            ConnectionStatus =
                "EMS 연결 오류 · 5초 후 자동 재연결";

            ConnectionStatusBrush =
                CommonStatusErrorBrush;
        }
        finally
        {
            IsConnecting = false;
        }

        if (_emsService.IsConnected)
        {
            await RefreshMenuLockStateAsync();
        }
    }


    public async Task<bool> RequestSafeShutdownAsync()
    {
        if (!_emsService.IsConnected)
        {
            return await AppDialogService.ShowConfirmAsync(
                "EMS 미연결",
                "EMS 통신이 끊겨 상태를 확인할 수 없습니다.\n" +
                "장비가 운전 중일 수 있습니다.\n" +
                "그래도 프로그램을 종료하시겠습니까?",
                confirmText: "강제 종료",
                cancelText: "취소",
                dialogWidth: 420,
                dialogHeight: 240);
        }

        bool sequenceWasRunning =
            _emsService.IsOperationSequenceRunning;

        EmsOperationMode currentMode =
            EmsOperationMode.Standby;

        bool isRunning = false;

        try
        {
            if (!sequenceWasRunning)
            {
                var status =
                    await _emsService.ReadStatusAsync();

                currentMode =
                    EmsControlWord1.EmsSystemStatus1.GetOperatingMode(
                        status.SystemStatus1);

                isRunning =
                    EmsControlWord1.EmsSystemStatus2.IsRunning(
                        status.SystemStatus2);
            }
        }
        catch (Exception ex)
        {
            return await AppDialogService.ShowConfirmAsync(
                "EMS 상태 확인 불가",
                "EMS 통신 응답 오류로 현재 운전 상태와 정지 상태를 확인할 수 없습니다.\n\n" +
                $"오류: {ex.Message}\n\n" +
                "프로그램만 종료해도 장비는 계속 운전할 수 있습니다.\n" +
                "그래도 프로그램을 종료하시겠습니까?",
                confirmText: "상태 확인 없이 종료",
                cancelText: "취소");
        }

        bool requiresSafeStop =
            sequenceWasRunning ||
            isRunning ||
            currentMode != EmsOperationMode.Standby;

        if (!requiresSafeStop)
        {
            return await AppDialogService.ShowConfirmAsync(
                "프로그램 종료",
                "EMS가 Standby / Stop 상태입니다.\n\n프로그램을 종료하시겠습니까?",
                confirmText: "종료",
                cancelText: "취소");
        }

        string modeText =
            sequenceWasRunning
                ? "운전 명령 처리 중"
                : GetOperationModeDisplayName(currentMode);

        bool confirmed =
            await AppDialogService.ShowConfirmAsync(
                "운전 정지 후 종료",
                $"현재 {modeText} 상태입니다.\n\n" +
                "안전한 종료를 위해 운전을 정지하고\n" +
                "EMS의 Standby / Stop 상태를 확인한 후 프로그램을 종료합니다.",
                confirmText: "정지 후 종료",
                cancelText: "취소");

        if (!confirmed)
        {
            return false;
        }

        try
        {
            IsModeSelectNavigationEnabled = false;
            IsAdminMenuEnabled = false;

            MenuLockStatusText =
                "프로그램 종료 요청 / 운전 안전 정지 중";

            ConnectionStatus =
                sequenceWasRunning
                    ? "현재 운전 명령 완료 대기 중..."
                    : "EMS 운전 정지 요청 중...";

            ConnectionStatusBrush =
                CommonStatusWarningBrush;

            if (sequenceWasRunning)
            {
                await WaitForOperationSequenceToFinishAsync();
            }

            var statusBeforeStop =
                await _emsService.ReadStatusAsync();

            EmsOperationMode modeBeforeStop =
                EmsControlWord1.EmsSystemStatus1.GetOperatingMode(
                    statusBeforeStop.SystemStatus1);

            bool runningBeforeStop =
                EmsControlWord1.EmsSystemStatus2.IsRunning(
                    statusBeforeStop.SystemStatus2);

            if (runningBeforeStop ||
                modeBeforeStop != EmsOperationMode.Standby)
            {
                ConnectionStatus =
                    "EMS를 Standby / Stop 상태로 전환 중...";

                ConnectionStatusBrush =
                    CommonStatusWarningBrush;

                await _emsService.StopAllAsync();
            }

            var finalStatus =
                await _emsService.ReadStatusAsync();

            EmsOperationMode finalMode =
                EmsControlWord1.EmsSystemStatus1.GetOperatingMode(
                    finalStatus.SystemStatus1);

            bool finalIsRunning =
                EmsControlWord1.EmsSystemStatus2.IsRunning(
                    finalStatus.SystemStatus2);

            if (finalMode != EmsOperationMode.Standby ||
                finalIsRunning)
            {
                throw new InvalidOperationException(
                    $"최종 상태가 Standby / Stop이 아닙니다. " +
                    $"Mode={finalMode}, Run={(finalIsRunning ? "Run" : "Stop")}");
            }

            ConnectionStatus =
                "EMS 안전 정지 완료 · 프로그램 종료";

            ConnectionStatusBrush =
                CommonStatusRunningBrush;

            MenuLockStatusText =
                "Standby / Stop 확인 완료";

            return true;
        }
        catch (Exception ex)
        {
            ConnectionStatus =
                "EMS 안전 정지 실패 · 상태 확인 필요";

            ConnectionStatusBrush =
                CommonStatusErrorBrush;

            MenuLockStatusText =
                "정지 확인 실패 / 프로그램 종료 확인 필요";

            // 안전 정지 도중 실제 통신이 끊긴 경우에는
            // 정지 명령 및 최종 상태를 더 이상 확인할 수 없으므로
            // 사용자에게 위험을 명확히 알리고 프로그램만 종료할 수 있게 합니다.
            if (!_emsService.IsConnected)
            {
                return await AppDialogService.ShowConfirmAsync(
                    "통신 끊김 · 안전 정지 확인 불가",
                    "프로그램 종료 과정에서 EMS 통신이 끊겼습니다.\n" +
                    "운전 정지 및 Standby 상태를 확인할 수 없습니다.\n\n" +
                    "장비가 계속 운전 중일 가능성이 있습니다.\n" +
                    "프로그램만 종료해도 장비는 정지하지 않을 수 있습니다.\n\n" +
                    $"오류: {ex.Message}\n\n" +
                    "그래도 프로그램을 종료하시겠습니까?",
                    confirmText: "확인 없이 종료",
                    cancelText: "취소");
            }

            await AppDialogService.ShowWarningAsync(
                "프로그램 종료 중단",
                "EMS의 안전 정지를 확인하지 못했습니다.\n" +
                "프로그램을 종료하지 않습니다.\n\n" +
                "통신 상태와 장비 상태를 확인한 뒤 다시 시도해 주세요.\n\n" +
                $"오류: {ex.Message}");

            await RefreshMenuLockStateAsync();

            return false;
        }
    }

    private async Task WaitForOperationSequenceToFinishAsync()
    {
        const int maxWaitCount = 200;
        const int waitDelayMilliseconds = 100;

        for (int count = 0; count < maxWaitCount; count++)
        {
            if (!_emsService.IsOperationSequenceRunning)
            {
                return;
            }

            await Task.Delay(
                waitDelayMilliseconds);
        }

        throw new TimeoutException(
            "진행 중인 운전 명령이 제한시간 안에 완료되지 않았습니다.");
    }

    private static string GetOperationModeDisplayName(
        EmsOperationMode mode)
    {
        return mode switch
        {
            EmsOperationMode.Standby => "대기",
            EmsOperationMode.AutoCharge => "자동충전 운전",
            EmsOperationMode.ExternalOutput => "외부출력 운전",
            EmsOperationMode.GridDischarge => "계통방전 운전",
            (EmsOperationMode)7 => "UPS 운전",
            _ => $"{mode} 운전"
        };
    }

    [RelayCommand]
    private void ShowModeSelect()
    {
        if (!IsModeSelectNavigationEnabled ||
            _emsService.IsOperationSequenceRunning)
        {
            ConnectionStatus =
                "운전 중에는 오퍼레이션 모드 화면으로 이동할 수 없습니다.";

            return;
        }

        Navigate(
            CreateModeSelectViewModel());
    }

    [RelayCommand]
    private void ShowAutoCharge()
    {
        if (!CanOpenMode(
                IsAutoChargeMenuEnabled,
                "자동충전"))
        {
            return;
        }

        Navigate(
            new AutoChargeViewModel(_emsService));
    }

    [RelayCommand]
    private void ShowExternalOutput()
    {
        if (!CanOpenMode(
                IsExternalOutputMenuEnabled,
                "외부출력"))
        {
            return;
        }

        // 실제 EMS 서비스가 전달되어야 외부출력 시작/정지와 상태 갱신이 동작합니다.
        Navigate(
            new ExternalOutputViewModel(_emsService));
    }

    [RelayCommand]
    private void ShowGridDischarge()
    {
        if (!CanOpenMode(
                IsGridDischargeMenuEnabled,
                "계통방전"))
        {
            return;
        }

        Navigate(
            new GridDischargeViewModel(_emsService));
    }


    [RelayCommand]
    private async Task ShowAdmin()
    {
        if (!IsAdminMenuEnabled ||
            _emsService.IsOperationSequenceRunning)
        {
            ConnectionStatus =
                "운전 중에는 관리자 화면으로 이동할 수 없습니다.";

            return;
        }

        string? enteredPassword =
            await AppDialogService.ShowNumericPasswordAsync(
                title: "관리자 인증",
                message: "관리자 비밀번호 4자리를 입력하세요.",
                maxLength: 4);

        // 취소 또는 우측 상단 X로 닫은 경우
        if (enteredPassword is null)
        {
            return;
        }

        if (!string.Equals(
                enteredPassword,
                AdminPassword,
                StringComparison.Ordinal))
        {
            await AppDialogService.ShowWarningAsync(
                "관리자 인증 실패",
                "비밀번호가 올바르지 않습니다.");

            return;
        }

        Navigate(
            new AdminViewModel(
                _emsService,
                ShowModeSelect));
    }
}
