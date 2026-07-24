using MobileEssControl.Constants;
using MobileEssControl.Models.System;
using MobileEssControl.Services.Ems;
using MobileEssControl.Services.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MobileEssControl.Services.Mobile;

public sealed class MobileTelemetryService : IDisposable
{
    // =========================================================
    // 랜덤 전송 시험 스위치
    //
    // true  : 실제 EMS 값 대신 5초마다 랜덤 시험값 전송
    // false : 실제 EMS 값을 읽어서 전송
    //
    // 현장 적용 전에는 반드시 false로 변경하세요.
    // =========================================================
    private const bool UseRandomTestData = false;

    private readonly EmsService _emsService;
    private readonly MobileTelemetryOptions _options;
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _stopCts = new();

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition =
                JsonIgnoreCondition.Never
        };

    private Task? _workerTask;
    private bool _urlMissingLogged;

    public MobileTelemetryService(
        EmsService emsService,
        MobileTelemetryOptions options)
    {
        _emsService =
            emsService ??
            throw new ArgumentNullException(
                nameof(emsService));

        _options =
            options ??
            throw new ArgumentNullException(
                nameof(options));

        if (_options.ServerPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "모바일 TCP 포트는 1~65535 범위여야 합니다.");
        }

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "MobileEssControl/1.0");
    }

    public void Start()
    {
        if (_workerTask is not null)
        {
            return;
        }

        if (UseRandomTestData)
        {
            FileAppLogger.Warning(
                "MOBILE",
                "랜덤 시험 데이터 전송 모드가 활성화되었습니다. " +
                "실제 EMS 값은 모바일 서버로 전송되지 않습니다.");
        }

        _workerTask = Task.Run(
            () => RunAsync(_stopCts.Token));
    }

    private async Task RunAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(
                    _options.IpLookupUrl))
            {
                if (!_urlMissingLogged)
                {
                    _urlMissingLogged = true;

                    FileAppLogger.Warning(
                        "MOBILE",
                        "모바일 서버 IP 조회 홈페이지 주소가 비어 있습니다. " +
                        "App.axaml.cs의 IpLookupUrl에 주소를 입력하세요.");
                }

                await DelayReconnectAsync(
                    cancellationToken);

                continue;
            }

            try
            {
                string serverIp =
                    await ReadServerIpAsync(
                        cancellationToken);

                using var tcpClient =
                    new TcpClient();

                using var connectTimeoutCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                connectTimeoutCts.CancelAfter(
                    TimeSpan.FromSeconds(8));

                await tcpClient.ConnectAsync(
                    serverIp,
                    _options.ServerPort,
                    connectTimeoutCts.Token);

                tcpClient.NoDelay = true;
                tcpClient.Client.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.KeepAlive,
                    true);

                FileAppLogger.Info(
                    "MOBILE",
                    $"TCP 연결 성공 | " +
                    $"Server={serverIp}:{_options.ServerPort}");

                await using NetworkStream stream =
                    tcpClient.GetStream();

                // TCP가 새로 연결될 때마다 최초 1회는 auth입니다.
                await SendPacketAsync(
                    stream,
                    dataAction: "auth",
                    cancellationToken);

                FileAppLogger.Info(
                    "MOBILE",
                    $"auth 전송 완료 | " +
                    $"Server={serverIp}:{_options.ServerPort}");

                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(
                        _options.UpdateInterval,
                        cancellationToken);

                    await SendPacketAsync(
                        stream,
                        dataAction: "update",
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                FileAppLogger.Error(
                    "MOBILE",
                    "모바일 TCP 연결 또는 전송 실패 · 재연결을 시도합니다.",
                    ex);

                await DelayReconnectAsync(
                    cancellationToken);
            }
        }
    }

    private async Task<string> ReadServerIpAsync(
        CancellationToken cancellationToken)
    {
        string response =
            await _httpClient.GetStringAsync(
                _options.IpLookupUrl,
                cancellationToken);

        string ipText =
            response
                .Trim()
                .Trim('\uFEFF', '"', '\'');

        if (!IPAddress.TryParse(
                ipText,
                out IPAddress? parsedAddress))
        {
            throw new InvalidDataException(
                "IP 조회 홈페이지 응답이 올바른 IP 주소가 아닙니다. " +
                $"응답={ipText}");
        }

        string normalizedIp =
            parsedAddress.ToString();

        FileAppLogger.Info(
            "MOBILE",
            $"서버 IP 조회 성공 | IP={normalizedIp}");

        return normalizedIp;
    }

    private async Task SendPacketAsync(
        NetworkStream stream,
        string dataAction,
        CancellationToken cancellationToken)
    {
        MobileTelemetryPacket packet =
            await CreatePacketAsync(
                dataAction,
                cancellationToken);

        string json =
            JsonSerializer.Serialize(
                packet,
                _jsonOptions);

        // 서버가 줄바꿈으로 JSON 패킷 끝을 구분합니다.
        byte[] sendBytes =
            Encoding.UTF8.GetBytes(
                json + "\n");

        await stream.WriteAsync(
            sendBytes,
            cancellationToken);

        await stream.FlushAsync(
            cancellationToken);

        FileAppLogger.Detailed(
            "MOBILE",
            $"{dataAction} 전송 | {json}");
    }

    private async Task<MobileTelemetryPacket> CreatePacketAsync(
        string dataAction,
        CancellationToken cancellationToken)
    {
        string currentPcTime =
            DateTime.Now.ToString(
                "yyyyMMdd:HH:mm:ss");

        if (UseRandomTestData)
        {
            return CreateRandomTestPacket(
                dataAction,
                currentPcTime);
        }

        // EMS 미연결 또는 상태 Read 실패 시에는
        // 측정값을 0으로 위조하지 않고 null로 전송합니다.
        if (!_emsService.IsConnected)
        {
            return CreateDisconnectedPacket(
                dataAction,
                currentPcTime);
        }

        try
        {
            var snapshot =
                await _emsService.ReadMobileTelemetrySnapshotAsync(
                    cancellationToken);

            EssStatusData status =
                snapshot.Status;

            ushort[] controls =
                snapshot.ControlValues;

            EmsOperationMode operationMode =
                EmsControlWord1.EmsSystemStatus1.GetOperatingMode(
                    status.SystemStatus1);

            bool isRunning =
                EmsControlWord1.EmsSystemStatus2.IsRunning(
                    status.SystemStatus2);

            // 선간전압은 인버터 1/2 값을 평균냅니다.
            double? acAbVoltage =
                SelectCombinedAcValue(
                    status.Inverter1Voltage,
                    status.Inverter2Voltage);

            double? acBcVoltage =
                SelectCombinedAcValue(
                    status.Inverter1BcVoltage,
                    status.Inverter2BcVoltage);

            double? acCaVoltage =
                SelectCombinedAcValue(
                    status.Inverter1CaVoltage,
                    status.Inverter2CaVoltage);

            // A/B/C상 전압도 인버터 1/2 값을 평균냅니다.
            double? acPhaseAVoltage =
                SelectCombinedAcValue(
                    status.Inverter1PhaseAVoltage,
                    status.Inverter2PhaseAVoltage);

            double? acPhaseBVoltage =
                SelectCombinedAcValue(
                    status.Inverter1PhaseBVoltage,
                    status.Inverter2PhaseBVoltage);

            double? acPhaseCVoltage =
                SelectCombinedAcValue(
                    status.Inverter1PhaseCVoltage,
                    status.Inverter2PhaseCVoltage);

            // 병렬 인버터의 동일 상 전류는 합산합니다.
            // 상별 필드는 서버 예시처럼 양의 크기로 전송합니다.
            double acPhaseACurrent =
                Math.Abs(status.Inverter1PhaseACurrent) +
                Math.Abs(status.Inverter2PhaseACurrent);

            double acPhaseBCurrent =
                Math.Abs(status.Inverter1PhaseBCurrent) +
                Math.Abs(status.Inverter2PhaseBCurrent);

            double acPhaseCCurrent =
                Math.Abs(status.Inverter1PhaseCCurrent) +
                Math.Abs(status.Inverter2PhaseCCurrent);

            // ac_current는 A/B/C상 합산전류의 평균값입니다.
            double averageAcCurrent =
                (acPhaseACurrent +
                 acPhaseBCurrent +
                 acPhaseCCurrent) / 3.0;

            // tot_power_kw는 배터리 DC 전압 × 전류로 계산합니다.
            double dcTotalPowerKw =
                status.BatteryVoltage *
                status.BatteryCurrent /
                1000.0;

            // ac_tot_power_kw는 인버터 1/2 유효전력 합계입니다.
            double acTotalPowerKw =
                status.Inverter1PowerKw +
                status.Inverter2PowerKw;

            return new MobileTelemetryPacket
            {
                DataAction = dataAction,
                DataTimestamp = currentPcTime,
                EssCarId = _options.EssCarId,

                EssData = new MobileEssData
                {
                    Active = isRunning,
                    OperationMode =
                        GetOperationModeText(
                            operationMode),
                    Connected = true,
                    Soc = Round1(status.Soc),
                    BatteryVoltage =
                        Round1(status.BatteryVoltage),
                    BatteryCurrent =
                        Round1(status.BatteryCurrent),

                    TotalPowerKw =
                        Round1(
                            NormalizeTotalPowerKw(
                                operationMode,
                                dcTotalPowerKw)),

                    AcVoltage =
                        Round1Nullable(
                            AverageNullable(
                                acAbVoltage,
                                acBcVoltage,
                                acCaVoltage)),

                    AcCurrent =
                        Round1(
                            NormalizeTotalPowerKw(
                                operationMode,
                                averageAcCurrent)),

                    AcTotalPowerKw =
                        Round1(
                            NormalizeTotalPowerKw(
                                operationMode,
                                acTotalPowerKw)),

                    AcPhaseAVoltage =
                        Round1Nullable(acPhaseAVoltage),
                    AcPhaseACurrent =
                        Round1(acPhaseACurrent),

                    AcPhaseBVoltage =
                        Round1Nullable(acPhaseBVoltage),
                    AcPhaseBCurrent =
                        Round1(acPhaseBCurrent),

                    AcPhaseCVoltage =
                        Round1Nullable(acPhaseCVoltage),
                    AcPhaseCCurrent =
                        Round1(acPhaseCCurrent)
                },

                ChargeData = new MobileChargeData
                {
                    // 30008 / 0.1%
                    TargetSoc =
                        GetScaledControlValue(
                            controls,
                            absoluteAddress: 30008,
                            divisor: 10.0)
                },

                GridDischargeData =
                    new MobileGridDischargeData
                    {
                        // 30009 / 0.1%
                        TargetSoc =
                            GetScaledControlValue(
                                controls,
                                absoluteAddress: 30009,
                                divisor: 10.0)
                    },

                ExternalData =
                    new MobileExternalData
                    {
                        Data01 = string.Empty
                    }
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileAppLogger.Error(
                "MOBILE",
                "EMS 모바일 전송 데이터 생성 실패 · null 상태값으로 전송합니다.",
                ex);

            return CreateDisconnectedPacket(
                dataAction,
                currentPcTime);
        }
    }

    private MobileTelemetryPacket CreateRandomTestPacket(
        string dataAction,
        string currentPcTime)
    {
        int modeIndex =
            Random.Shared.Next(0, 4);

        string operationMode;
        bool active;
        double batteryCurrent;
        double totalPowerKw;

        switch (modeIndex)
        {
            case 1:
                operationMode = "AUTO_CHARGE";
                active = true;
                batteryCurrent =
                    RandomRange(5.0, 30.0);
                totalPowerKw =
                    RandomRange(2.0, 20.0);
                break;

            case 2:
                operationMode = "GRID_DISCHARGE";
                active = true;
                batteryCurrent =
                    -RandomRange(5.0, 50.0);
                totalPowerKw =
                    -RandomRange(2.0, 40.0);
                break;

            case 3:
                operationMode = "EXTERNAL_OUTPUT";
                active = true;
                batteryCurrent =
                    -RandomRange(5.0, 50.0);
                totalPowerKw =
                    -RandomRange(2.0, 40.0);
                break;

            default:
                operationMode = "STANDBY";
                active = false;
                batteryCurrent = 0.0;
                totalPowerKw = 0.0;
                break;
        }

        return new MobileTelemetryPacket
        {
            DataAction = dataAction,
            DataTimestamp = currentPcTime,
            EssCarId = _options.EssCarId,

            EssData = new MobileEssData
            {
                Active = active,
                OperationMode = operationMode,
                Connected = true,
                Soc = RandomRange(20.0, 95.0),
                BatteryVoltage =
                    RandomRange(300.0, 340.0),
                BatteryCurrent =
                    Round1(batteryCurrent),
                TotalPowerKw =
                    Round1(totalPowerKw),
                AcVoltage =
                    RandomRange(375.0, 385.0),
                AcCurrent =
                    active
                        ? Round1(
                            operationMode is
                                "GRID_DISCHARGE" or
                                "EXTERNAL_OUTPUT"
                                ? -RandomRange(3.0, 20.0)
                                : RandomRange(3.0, 20.0))
                        : 0.0,
                AcTotalPowerKw =
                    Round1(totalPowerKw),
                AcPhaseAVoltage =
                    active
                        ? RandomRange(215.0, 230.0)
                        : 0.0,
                AcPhaseACurrent =
                    active
                        ? RandomRange(3.0, 20.0)
                        : 0.0,
                AcPhaseBVoltage =
                    active
                        ? RandomRange(215.0, 230.0)
                        : 0.0,
                AcPhaseBCurrent =
                    active
                        ? RandomRange(3.0, 20.0)
                        : 0.0,
                AcPhaseCVoltage =
                    active
                        ? RandomRange(215.0, 230.0)
                        : 0.0,
                AcPhaseCCurrent =
                    active
                        ? RandomRange(3.0, 20.0)
                        : 0.0
            },

            ChargeData = new MobileChargeData
            {
                TargetSoc =
                    RandomRange(85.0, 100.0)
            },

            GridDischargeData =
                new MobileGridDischargeData
                {
                    TargetSoc =
                        RandomRange(10.0, 20.0)
                },

            // 향후 확장용 필드이므로 현재는 빈 문자열을 유지합니다.
            ExternalData =
                new MobileExternalData
                {
                    Data01 = string.Empty
                }
        };
    }

    private MobileTelemetryPacket CreateDisconnectedPacket(
        string dataAction,
        string currentPcTime)
    {
        return new MobileTelemetryPacket
        {
            DataAction = dataAction,
            DataTimestamp = currentPcTime,
            EssCarId = _options.EssCarId,

            EssData = new MobileEssData
            {
                Active = false,
                OperationMode = "UNKNOWN",
                Connected = false
            },

            ChargeData =
                new MobileChargeData(),

            GridDischargeData =
                new MobileGridDischargeData(),

            ExternalData =
                new MobileExternalData
                {
                    Data01 = string.Empty
                }
        };
    }

    private static string GetOperationModeText(
        EmsOperationMode mode)
    {
        return mode switch
        {
            EmsOperationMode.Standby =>
                "STANDBY",

            EmsOperationMode.AutoCharge =>
                "AUTO_CHARGE",

            EmsOperationMode.ExternalOutput =>
                "EXTERNAL_OUTPUT",

            EmsOperationMode.GridDischarge =>
                "GRID_DISCHARGE",

            // UPS 기능은 삭제되었으므로 Mode 7도 정상 모드로 보내지 않습니다.
            _ =>
                "UNKNOWN"
        };
    }

    private static double NormalizeTotalPowerKw(
        EmsOperationMode mode,
        double measuredPowerKw)
    {
        double magnitude =
            Math.Abs(measuredPowerKw);

        return mode switch
        {
            // 모바일 규격: 충전은 양수
            EmsOperationMode.AutoCharge =>
                magnitude,

            // 모바일 규격: 방전과 외부 출력은 음수
            EmsOperationMode.GridDischarge =>
                -magnitude,

            EmsOperationMode.ExternalOutput =>
                -magnitude,

            _ =>
                measuredPowerKw
        };
    }

    private static double? GetScaledControlValue(
        ushort[] values,
        int absoluteAddress,
        double divisor)
    {
        const int controlStartAddress = 30001;

        int index =
            absoluteAddress -
            controlStartAddress;

        if (index < 0 ||
            index >= values.Length)
        {
            return null;
        }

        return Math.Round(
            values[index] / divisor,
            1);
    }

    private static double? SelectCombinedAcValue(
        double inverter1Value,
        double inverter2Value)
    {
        bool inverter1Valid =
            !double.IsNaN(inverter1Value) &&
            !double.IsInfinity(inverter1Value) &&
            Math.Abs(inverter1Value) > 0.001;

        bool inverter2Valid =
            !double.IsNaN(inverter2Value) &&
            !double.IsInfinity(inverter2Value) &&
            Math.Abs(inverter2Value) > 0.001;

        if (inverter1Valid &&
            inverter2Valid)
        {
            return (
                inverter1Value +
                inverter2Value) / 2.0;
        }

        if (inverter1Valid)
        {
            return inverter1Value;
        }

        if (inverter2Valid)
        {
            return inverter2Value;
        }

        return null;
    }

    private static double? AverageNullable(
        double? first,
        double? second,
        double? third)
    {
        double total = 0.0;
        int count = 0;

        if (first.HasValue)
        {
            total += first.Value;
            count++;
        }

        if (second.HasValue)
        {
            total += second.Value;
            count++;
        }

        if (third.HasValue)
        {
            total += third.Value;
            count++;
        }

        return count > 0
            ? total / count
            : null;
    }

    private static double Round1(
        double value)
    {
        return Math.Round(
            value,
            1);
    }

    private static double RandomRange(
        double minimum,
        double maximum)
    {
        return Math.Round(
            minimum +
            Random.Shared.NextDouble() *
            (maximum - minimum),
            1);
    }

    private static double? Round1Nullable(
        double? value)
    {
        return value.HasValue
            ? Math.Round(
                value.Value,
                1)
            : null;
    }

    private async Task DelayReconnectAsync(
        CancellationToken cancellationToken)
    {
        await Task.Delay(
            _options.ReconnectInterval,
            cancellationToken);
    }

    public void Dispose()
    {
        if (!_stopCts.IsCancellationRequested)
        {
            _stopCts.Cancel();
        }

        _httpClient.Dispose();
    }
}