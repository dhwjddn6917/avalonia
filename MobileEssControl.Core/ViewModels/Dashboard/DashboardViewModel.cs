using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MobileEssControl.Models.System;
using MobileEssControl.Services.Ems;
using System;
using System.Threading.Tasks;

namespace MobileEssControl.ViewModels.Dashboard;

public partial class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly EmsService _emsService;

    private bool _isRefreshing;
    private bool _isReconnecting;
    private bool _hasReceivedEmsStatus;

    // 최저 SOC 자동 정지를 동시에 여러 번 보내지 않기 위한 플래그
    private bool _isSafetyStopping;

    // SOC 보호 정지가 성공한 뒤 같은 정지 명령을 반복하지 않기 위한 플래그
    private bool _socProtectionLatched;

    private int _communicationFailureCount;

    private DateTime _nextReconnectAttemptUtc = DateTime.MinValue;

    private const int ReconnectFailureThreshold = 2;
    private const int ReconnectRetryIntervalSeconds = 2;

    // 0 = 대기 / 1 = 충전 / 2 = 외부 전원 출력 / 3 = 계통 방전
    private int _modeIndex;

    // 전체 시스템 출력 게이지 최대값
    // 22kW 인버터 2대 = 최대 44kW
    private const double MaxOutputKw = 44.0;

    // =========================================================
    // 사용자 설정값 범위 / 조절 단위
    // =========================================================

    private const double MinOutputLimitKw = 1.0;
    private const double MaxOutputLimitKw = 40.0;
    private const double OutputLimitStepKw = 1.0;

    private const double MinSocLimit = 10.0;
    private const double MaxSocLimit = 90.0;
    private const double SocLimitStep = 5.0;

    public DashboardViewModel(EmsService emsService)
    {
        _emsService = emsService;

        ApplyMode();
        ApplyStoppedState();

        UpdateGaugeGeometries();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _timer.Tick += Timer_Tick;
        _timer.Start();

        _ = RefreshStatusAsync();
    }

    // =========================================================
    // 화면 상태값
    // =========================================================

    [ObservableProperty]
    private double soc;

    [ObservableProperty]
    private double currentPowerKw;

    [ObservableProperty]
    private double voltage;

    [ObservableProperty]
    private double frequency;

    [ObservableProperty]
    private string currentMode;

    [ObservableProperty]
    private string runStatus;

    [ObservableProperty]
    private string alarmStatus;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private string communicationStatus = "EMS 연결 대기";

    [ObservableProperty]
    private IBrush communicationIndicatorBrush =
        new SolidColorBrush(Color.Parse("#94A3B8"));

    // =========================================================
    // 운전 설정 카드 표시값
    // 모드 변경 시 이 4줄이 같이 바뀜
    // =========================================================

    [ObservableProperty]
    private string operationSettingLine1 = "선택 모드 : 대기";

    [ObservableProperty]
    private string operationSettingLine2 = "운전 상태 : 모드를 선택하세요";

    [ObservableProperty]
    private string operationSettingLine3 = "시작 조건 : EMS 통신 정상 필요";

    [ObservableProperty]
    private string operationSettingLine4 = "";

    // =========================================================
    // 모드별 사용자 설정값
    // 현재는 화면 내부 값만 변경한다.
    // EMS Modbus Write 주소 확정 후 실제 송신을 연결한다.
    // =========================================================
    // 자동 충전 설정
    [ObservableProperty]
    private double autoChargeLimitKw = 40.0;

    // 외부 전원 출력 설정
    [ObservableProperty]
    private double externalOutputLimitKw = 40.0;

    [ObservableProperty]
    private double externalOutputMinSoc = 20.0;

    // 계통 방전 설정
    [ObservableProperty]
    private double gridDischargeLimitKw = 10.0;

    [ObservableProperty]
    private double gridDischargeMinSoc = 20.0;

    // =========================================================
    // 원형 게이지에 사용할 Path 데이터
    // =========================================================

    [ObservableProperty]
    private Geometry? socArcGeometry;

    [ObservableProperty]
    private Geometry? powerArcGeometry;

    // =========================================================
    // 화면 표시 문자열
    // =========================================================

    public string SocText => $"{Soc:0.0}%";

    public string PowerText => $"{CurrentPowerKw:0.0} kW";

    public string AutoChargeLimitText =>
    $"{AutoChargeLimitKw:0} kW";
    public string VoltageText => $"{Voltage:0} V";

    public string FrequencyText => $"{Frequency:0.0} Hz";

    public string ExternalOutputLimitText =>
        $"{ExternalOutputLimitKw:0} kW";

    public string ExternalOutputMinSocText =>
        $"{ExternalOutputMinSoc:0} %";

    public string GridDischargeLimitText =>
        $"{GridDischargeLimitKw:0} kW";

    public string GridDischargeMinSocText =>
        $"{GridDischargeMinSoc:0} %";
    public string OperationSettingsTitle =>
    CurrentMode == "대기"
        ? "운전 설정"
        : $"운전 설정 : {CurrentMode}";
    public string ModeDescription => CurrentMode switch
    {
        "충전" => "배터리 충전 운전",
        "외부 전원 출력" => "외부 부하 출력 운전",
        "계통 방전" => "계통에 전력을 공급하여 방전",
        _ => "운전 모드를 선택하세요"
    };
    public bool IsStandbyMode => CurrentMode == "대기";

    public bool IsChargeMode => CurrentMode == "충전";

    public bool IsExternalOutputMode =>
        CurrentMode == "외부 전원 출력";

    public bool IsGridDischargeMode =>
        CurrentMode == "계통 방전";
    public string StartDescription => CurrentMode switch
    {
        "충전" => "충전 시작",
        "외부 전원 출력" => "외부 출력 시작",
        "계통 방전" => "계통 방전 시작",
        _ => "운전 모드를 선택하세요"
    };

    // =========================================================
    // 값 변경 시 화면 갱신
    // =========================================================

    partial void OnSocChanged(double value)
    {
        OnPropertyChanged(nameof(SocText));
        UpdateSocGauge();
    }

    partial void OnCurrentPowerKwChanged(double value)
    {
        OnPropertyChanged(nameof(PowerText));
        UpdatePowerGauge();
    }

    partial void OnVoltageChanged(double value)
    {
        OnPropertyChanged(nameof(VoltageText));
    }

    partial void OnFrequencyChanged(double value)
    {
        OnPropertyChanged(nameof(FrequencyText));
    }

    partial void OnCurrentModeChanged(string value)
    {
        OnPropertyChanged(nameof(ModeDescription));
        OnPropertyChanged(nameof(StartDescription));

        OnPropertyChanged(nameof(IsStandbyMode));
        OnPropertyChanged(nameof(IsChargeMode));
        OnPropertyChanged(nameof(IsExternalOutputMode));
        OnPropertyChanged(nameof(IsGridDischargeMode));
        OnPropertyChanged(nameof(OperationSettingsTitle));
    }
    partial void OnAutoChargeLimitKwChanged(double value)
    {
        OnPropertyChanged(nameof(AutoChargeLimitText));

        if (_modeIndex == 1)
        {
            ApplyOperationSettings();
        }
    }
    partial void OnExternalOutputLimitKwChanged(double value)
    {
        OnPropertyChanged(nameof(ExternalOutputLimitText));

        if (_modeIndex == 2)
        {
            ApplyOperationSettings();
        }
    }

    partial void OnExternalOutputMinSocChanged(double value)
    {
        OnPropertyChanged(nameof(ExternalOutputMinSocText));

        if (_modeIndex == 2)
        {
            ApplyOperationSettings();
        }
    }

    partial void OnGridDischargeLimitKwChanged(double value)
    {
        OnPropertyChanged(nameof(GridDischargeLimitText));

        if (_modeIndex == 3)
        {
            ApplyOperationSettings();
        }
    }

    partial void OnGridDischargeMinSocChanged(double value)
    {
        OnPropertyChanged(nameof(GridDischargeMinSocText));

        if (_modeIndex == 3)
        {
            ApplyOperationSettings();
        }
    }

    // =========================================================
    // 설정값 조절 버튼 명령
    // =========================================================
    [RelayCommand]
    private void DecreaseAutoChargeLimit()
    {
        AutoChargeLimitKw = Math.Max(
            MinOutputLimitKw,
            AutoChargeLimitKw - OutputLimitStepKw);
    }

    [RelayCommand]
    private void IncreaseAutoChargeLimit()
    {
        AutoChargeLimitKw = Math.Min(
            MaxOutputLimitKw,
            AutoChargeLimitKw + OutputLimitStepKw);
    }
    [RelayCommand]
    private void DecreaseExternalOutputLimit()
    {
        ExternalOutputLimitKw = Math.Max(
            MinOutputLimitKw,
            ExternalOutputLimitKw - OutputLimitStepKw);
    }

    [RelayCommand]
    private void IncreaseExternalOutputLimit()
    {
        ExternalOutputLimitKw = Math.Min(
            MaxOutputLimitKw,
            ExternalOutputLimitKw + OutputLimitStepKw);
    }

    [RelayCommand]
    private void DecreaseExternalOutputMinSoc()
    {
        ExternalOutputMinSoc = Math.Max(
            MinSocLimit,
            ExternalOutputMinSoc - SocLimitStep);
    }

    [RelayCommand]
    private void IncreaseExternalOutputMinSoc()
    {
        ExternalOutputMinSoc = Math.Min(
            MaxSocLimit,
            ExternalOutputMinSoc + SocLimitStep);
    }

    [RelayCommand]
    private void DecreaseGridDischargeLimit()
    {
        GridDischargeLimitKw = Math.Max(
            MinOutputLimitKw,
            GridDischargeLimitKw - OutputLimitStepKw);
    }

    [RelayCommand]
    private void IncreaseGridDischargeLimit()
    {
        GridDischargeLimitKw = Math.Min(
            MaxOutputLimitKw,
            GridDischargeLimitKw + OutputLimitStepKw);
    }

    [RelayCommand]
    private void DecreaseGridDischargeMinSoc()
    {
        GridDischargeMinSoc = Math.Max(
            MinSocLimit,
            GridDischargeMinSoc - SocLimitStep);
    }

    [RelayCommand]
    private void IncreaseGridDischargeMinSoc()
    {
        GridDischargeMinSoc = Math.Min(
            MaxSocLimit,
            GridDischargeMinSoc + SocLimitStep);
    }

    // =========================================================
    // 일반 버튼
    // =========================================================

    [RelayCommand]
    private void ChangeMode()
    {
        if (IsRunning)
        {
            AlarmStatus = "운전 중 모드 변경 불가";
            return;
        }

        _modeIndex++;

        // 대기(0)는 초기 상태만 사용
        // 버튼으로는 충전 → 외부 출력 → 계통 방전 순서로 변경
        if (_modeIndex > 3)
        {
            _modeIndex = 1;
        }

        ApplyMode();
        ApplyStoppedState();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_isReconnecting)
        {
            AlarmStatus = "EMS 재연결 중";
            RunStatus = "시작 불가";
            return;
        }

        if (!_emsService.IsConnected)
        {
            AlarmStatus = "통신 미연결";
            RunStatus = "시작 불가";
            return;
        }

        // 대기 상태에서 시작하면 충전 모드로 시작
        if (_modeIndex == 0)
        {
            _modeIndex = 1;
            ApplyMode();
        }

        // 아직 EMS 실시간 상태를 한 번도 받지 못했으면
        // SOC 확인 없이 출력 운전을 시작하지 않는다.
        if (!_hasReceivedEmsStatus)
        {
            AlarmStatus = "EMS 상태 확인 전에는 시작할 수 없습니다.";
            RunStatus = "시작 불가";
            return;
        }

        // 외부출력은 설정한 최저 SOC 이하에서 시작 금지
        if (_modeIndex == 2 &&
            Soc <= ExternalOutputMinSoc)
        {
            AlarmStatus =
                $"현재 SOC {Soc:0.0}% · 외부출력 최저 SOC " +
                $"{ExternalOutputMinSoc:0.0}% 이하";

            RunStatus = "외부 출력 시작 불가";
            return;
        }

        // 계통방전은 설정한 최저 SOC 이하에서 시작 금지
        if (_modeIndex == 3 &&
            Soc <= GridDischargeMinSoc)
        {
            AlarmStatus =
                $"현재 SOC {Soc:0.0}% · 계통방전 최저 SOC " +
                $"{GridDischargeMinSoc:0.0}% 이하";

            RunStatus = "계통 방전 시작 불가";
            return;
        }

        // 새 운전을 시작할 때 SOC 보호 정지를 다시 활성화한다.
        _socProtectionLatched = false;

        try
        {
            switch (_modeIndex)
            {
                case 1:
                    await _emsService.StartAutoChargeAsync(
    90.0,
    AutoChargeLimitKw);

                    RunStatus = "충전 시작 요청";
                    break;

                case 2:
                    await _emsService.StartExternalOutputAsync(
                        ExternalOutputLimitKw,
                        ExternalOutputMinSoc);

                    RunStatus = "외부 전원 출력 시작 요청";
                    break;

                case 3:
                    await _emsService.StartGridDischargeAsync(
                        GridDischargeLimitKw,
                        GridDischargeMinSoc);

                    RunStatus = "계통 방전 시작 요청";
                    break;

                default:
                    AlarmStatus = "운전 모드 선택 오류";
                    RunStatus = "시작 불가";
                    return;
            }

            AlarmStatus = "시작 명령 전송 완료";

            await RefreshStatusAsync();
        }
        catch
        {
            AlarmStatus = "시작 명령 전송 실패";
            RunStatus = "통신 오류";
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_isReconnecting)
        {
            AlarmStatus = "EMS 재연결 중";
            RunStatus = "정지 요청 대기";
            return;
        }

        if (!_emsService.IsConnected)
        {
            AlarmStatus = "통신 미연결";
            RunStatus = "정지 불가";
            return;
        }

        try
        {
            await _emsService.StopAllAsync();

            CurrentPowerKw = 0.0;
            IsRunning = false;

            RunStatus = "정지 요청";
            AlarmStatus = "정지 명령 전송 완료";

            await RefreshStatusAsync();
        }
        catch
        {
            AlarmStatus = "정지 명령 전송 실패";
            RunStatus = "통신 오류";
        }
    }

    // =========================================================
    // 모드별 상태
    // =========================================================

    private void ApplyMode()
    {
        CurrentMode = _modeIndex switch
        {
            1 => "충전",
            2 => "외부 전원 출력",
            3 => "계통 방전",
            _ => "대기"
        };

        AlarmStatus = "정상";

        ApplyOperationSettings();
    }

    private void ApplyOperationSettings()
    {
        switch (_modeIndex)
        {
            case 1:
                OperationSettingLine1 = "선택 모드 : 충전";
                OperationSettingLine2 =
                    $"최대 충전전력 : {AutoChargeLimitText}";
                OperationSettingLine3 =
                    $"병렬 분배 기준 : INV1 / INV2 합계 {AutoChargeLimitText}";
                OperationSettingLine4 =
                    "종료 조건 : BMS 충전 완료 · 충전 금지 · 알람";
                break;

            case 2:
                OperationSettingLine1 = "선택 모드 : 외부 전원 출력";
                OperationSettingLine2 =
                    "출력 전압 / 주파수 : 380 V / 60 Hz";
                OperationSettingLine3 =
                    $"최대 출력전력 : {ExternalOutputLimitText}";
                OperationSettingLine4 =
                    $"최저 SOC : {ExternalOutputMinSocText} / " +
                    "정지 조건 : BMS 방전 금지 · 알람";
                break;

            case 3:
                OperationSettingLine1 = "선택 모드 : 계통 방전";
                OperationSettingLine2 =
                    $"방전 제한전력 : {GridDischargeLimitText}";
                OperationSettingLine3 =
                    $"최저 SOC : {GridDischargeMinSocText} / 역률 : 1.00";
                OperationSettingLine4 =
                    "정지 조건 : 계통 이상 · BMS 방전 금지 · 알람";
                break;

            default:
                OperationSettingLine1 = "선택 모드 : 대기";
                OperationSettingLine2 = "운전 상태 : 모드를 선택하세요";
                OperationSettingLine3 = "시작 조건 : EMS 통신 정상 필요";
                OperationSettingLine4 = "";
                break;
        }
    }

    private void ApplyStoppedState()
    {
        IsRunning = false;
        CurrentPowerKw = 0.0;

        RunStatus = _modeIndex switch
        {
            1 => "충전 대기",
            2 => "외부 출력 대기",
            3 => "계통 방전 대기",
            _ => "대기"
        };
    }

    // =========================================================
    // SOC / 출력 원형 게이지 계산
    // =========================================================

    private void UpdateGaugeGeometries()
    {
        UpdateSocGauge();
        UpdatePowerGauge();
    }

    private void UpdateSocGauge()
    {
        SocArcGeometry = CreateArcGeometry(Soc, 100.0);
    }

    private void UpdatePowerGauge()
    {
        PowerArcGeometry = CreateArcGeometry(
            CurrentPowerKw,
            MaxOutputKw);
    }

    private static Geometry? CreateArcGeometry(
        double value,
        double maximum)
    {
        if (maximum <= 0)
        {
            return null;
        }

        double percent = Math.Clamp(value / maximum, 0.0, 1.0);

        if (percent <= 0)
        {
            return null;
        }

        const double center = 135.0;
        const double radius = 112.0;

        if (percent >= 0.999)
        {
            return new EllipseGeometry(
                new Rect(
                    center - radius,
                    center - radius,
                    radius * 2,
                    radius * 2));
        }

        Point startPoint = new(center, center - radius);

        double endAngle = -90 + (360 * percent);
        double endRadians = endAngle * Math.PI / 180.0;

        Point endPoint = new(
            center + radius * Math.Cos(endRadians),
            center + radius * Math.Sin(endRadians));

        var geometry = new StreamGeometry();

        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(startPoint, false);

            context.ArcTo(
                endPoint,
                new Size(radius, radius),
                0,
                percent > 0.5,
                SweepDirection.Clockwise,
                true);
        }

        return geometry;
    }

    // =========================================================
    // EMS 통신 상태 / 자동 재연결
    // =========================================================

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        await RefreshStatusAsync();
    }

    private void SetCommunicationWaiting()
    {
        CommunicationStatus = "EMS 연결 대기";

        CommunicationIndicatorBrush =
            new SolidColorBrush(Color.Parse("#94A3B8"));
    }

    private void SetCommunicationNormal()
    {
        CommunicationStatus = "통신 정상";

        CommunicationIndicatorBrush =
            new SolidColorBrush(Color.Parse("#22C55E"));
    }

    private void SetCommunicationError()
    {
        CommunicationStatus = "통신 오류";

        CommunicationIndicatorBrush =
            new SolidColorBrush(Color.Parse("#EF4444"));
    }

    private void SetCommunicationReconnecting()
    {
        CommunicationStatus = "EMS 재연결 중";

        CommunicationIndicatorBrush =
            new SolidColorBrush(Color.Parse("#F59E0B"));
    }

    private async Task TryReconnectEmsAsync()
    {
        if (_isReconnecting)
        {
            return;
        }

        if (DateTime.UtcNow < _nextReconnectAttemptUtc)
        {
            return;
        }

        _isReconnecting = true;

        _nextReconnectAttemptUtc =
            DateTime.UtcNow.AddSeconds(
                ReconnectRetryIntervalSeconds);

        try
        {
            SetCommunicationReconnecting();

            AlarmStatus = "EMS 통신 재연결 중";
            RunStatus = "통신 재연결 중";

            if (_emsService.IsConnected)
            {
                await _emsService.DisconnectAsync();
            }

            string? portName =
                await _emsService.FindAndConnectAsync();

            if (string.IsNullOrWhiteSpace(portName))
            {
                SetCommunicationError();

                AlarmStatus = "EMS 재연결 실패";
                RunStatus = "통신 오류";

                return;
            }

            CommunicationStatus =
                $"EMS 상태 확인 중 · {portName}";

            CommunicationIndicatorBrush =
                new SolidColorBrush(Color.Parse("#94A3B8"));

            AlarmStatus = "EMS 상태값 확인 중";
            RunStatus = "통신 연결됨";
        }
        catch
        {
            SetCommunicationError();

            AlarmStatus = "EMS 재연결 오류";
            RunStatus = "통신 오류";
        }
        finally
        {
            _isReconnecting = false;
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_isRefreshing || _isReconnecting)
        {
            return;
        }

        _isRefreshing = true;

        try
        {
            if (!_emsService.IsConnected)
            {
                if (_hasReceivedEmsStatus)
                {
                    await TryReconnectEmsAsync();
                }
                else
                {
                    SetCommunicationWaiting();

                    AlarmStatus = "통신 미연결";
                    RunStatus = "EMS 연결 대기";
                }

                return;
            }

            EssStatusData status =
                await _emsService.ReadStatusAsync();

            _hasReceivedEmsStatus = true;
            _communicationFailureCount = 0;

            SetCommunicationNormal();

            Soc = status.Soc;
            CurrentPowerKw = status.TotalPowerKw;
            Voltage = status.BatteryVoltage;
            Frequency = status.Frequency;

            // 기존 기능 유지:
            // 실제 인버터 출력값으로 IsRunning과 현재 선택 모드를 판단함
            ApplyStatusText(status);

            // EMS 31022 / 31023 상태 Bit를 한글 상태문구로 표시
            RunStatus = EmsStatusDecoder.BuildSummary(
                status.SystemStatus1,
                status.SystemStatus2);

            // EMS 31024 / 31025 알람 Bit를 한글 알람문구로 표시
            AlarmStatus = EmsAlarmDecoder.BuildShortSummary(
                status.AlarmStatus1,
                status.AlarmStatus2);


        }
        catch
        {
            _communicationFailureCount++;

            SetCommunicationError();

            if (_communicationFailureCount <
                ReconnectFailureThreshold)
            {
                AlarmStatus = "EMS 응답 없음";
                RunStatus = "통신 확인 중";

                return;
            }

            AlarmStatus = "EMS 통신 끊김 · 재연결 시도";
            RunStatus = "통신 오류";

            await TryReconnectEmsAsync();
        }
        finally
        {
            _isRefreshing = false;
        }
    }
    private async Task CheckMinimumSocStopAsync()
    {
        // 충전 모드와 대기 모드에서는 SOC 자동 정지를 하지 않는다.
        if (_modeIndex is not 2 and not 3)
        {
            return;
        }

        // 실제 EMS가 운전 중이 아니면 정지 명령을 보낼 필요가 없다.
        if (!IsRunning)
        {
            return;
        }

        // 이미 자동 정지 처리 중이거나,
        // 같은 SOC 조건으로 이미 정지한 경우 반복 전송하지 않는다.
        if (_isSafetyStopping || _socProtectionLatched)
        {
            return;
        }

        double minimumSoc = _modeIndex switch
        {
            2 => ExternalOutputMinSoc,
            3 => GridDischargeMinSoc,
            _ => 0.0
        };

        // SOC가 최저값보다 높으면 계속 운전한다.
        if (Soc > minimumSoc)
        {
            return;
        }

        string modeText = _modeIndex switch
        {
            2 => "외부 전원 출력",
            3 => "계통 방전",
            _ => "출력 운전"
        };

        _isSafetyStopping = true;

        try
        {
            RunStatus = $"{modeText} · 최저 SOC 도달 · 정지 중";

            AlarmStatus =
                $"SOC {Soc:0.0}% ≤ 설정 {minimumSoc:0.0}% · " +
                "보호 정지 명령 전송";

            // EmsService의 StopAllAsync()
            // → EMS 30001 = 0x0000 대기 명령 전송
            await _emsService.StopAllAsync();

            _socProtectionLatched = true;

            CurrentPowerKw = 0.0;
            IsRunning = false;

            RunStatus = $"{modeText} · 최저 SOC 도달로 정지";

            AlarmStatus =
                $"SOC 보호 정지 완료 · 현재 {Soc:0.0}%";
        }
        catch
        {
            // 정지에 실패하면 다음 상태 갱신 때 다시 시도할 수 있게 한다.
            _socProtectionLatched = false;

            RunStatus = "SOC 보호 정지 실패";

            AlarmStatus =
                "최저 SOC 도달 · EMS 정지 명령 전송 실패";
        }
        finally
        {
            _isSafetyStopping = false;
        }
    }
    private void ApplyStatusText(EssStatusData status)
    {
        // 31022 System Status1
        // Bit3~5 : EMS 실제 운전 모드
        int emsMode =
            (status.SystemStatus1 >> 3) & 0x0007;

        // 31023 System Status2
        // Bit13 : System RunStop
        // 0 = 정지 / 1 = 운전
        bool emsIsRunning =
            (status.SystemStatus2 & (1 << 13)) != 0;

        // 실제 EMS 운전 상태만 반영
        IsRunning = emsIsRunning;

        // ---------------------------------------------------------
        // EMS가 정지 상태면:
        // 사용자가 모드변경 버튼으로 선택한 _modeIndex를 건드리지 않는다.
        // ---------------------------------------------------------
        if (!emsIsRunning)
        {
            return;
        }

        // ---------------------------------------------------------
        // EMS가 실제 운전 중일 때만:
        // 현재 운전 모드를 EMS 값 기준으로 화면에 맞춘다.
        // ---------------------------------------------------------
        switch (emsMode)
        {
            case 1:
                // AC 자동 충전
                _modeIndex = 1;
                ApplyMode();
                break;

            case 3:
                // 외부 전원 출력
                _modeIndex = 2;
                ApplyMode();
                break;

            case 4:
                // 계통 방전
                _modeIndex = 3;
                ApplyMode();
                break;

            case 0:
                // EMS가 Run인데 Mode=0인 경우는
                // 현재 사용자가 선택한 화면 모드를 유지한다.
                break;

            // 수동제어 / DC 급속충전 / UPS 모드는
            // 현재 Dashboard 일반 메뉴에 없으므로
            // _modeIndex를 바꾸지 않는다.
            case 2:
            case 5:
            case 6:
            case 7:
                break;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }
}