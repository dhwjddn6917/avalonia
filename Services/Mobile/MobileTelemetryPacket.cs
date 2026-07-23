using System.Text.Json.Serialization;

namespace MobileEssControl.Services.Mobile;

internal sealed class MobileTelemetryPacket
{
    [JsonPropertyName("data_action")]
    public string DataAction { get; init; } = string.Empty;

    [JsonPropertyName("data_timesp")]
    public string DataTimestamp { get; init; } = string.Empty;

    [JsonPropertyName("ess_car_id")]
    public string EssCarId { get; init; } = string.Empty;

    [JsonPropertyName("ess_data")]
    public MobileEssData EssData { get; init; } = new();

    [JsonPropertyName("charge_data")]
    public MobileChargeData ChargeData { get; init; } = new();

    [JsonPropertyName("grid_discharge_data")]
    public MobileGridDischargeData GridDischargeData { get; init; } = new();

    [JsonPropertyName("ext_data_01")]
    public MobileExternalData ExternalData { get; init; } = new();
}

internal sealed class MobileEssData
{
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("op_mode")]
    public string OperationMode { get; init; } = "UNKNOWN";

    [JsonPropertyName("connected")]
    public bool Connected { get; init; }

    [JsonPropertyName("soc")]
    public double? Soc { get; init; }

    [JsonPropertyName("batt_volt")]
    public double? BatteryVoltage { get; init; }

    [JsonPropertyName("batt_current")]
    public double? BatteryCurrent { get; init; }

    [JsonPropertyName("tot_power_kw")]
    public double? TotalPowerKw { get; init; }

    [JsonPropertyName("ac_volt")]
    public double? AcVoltage { get; init; }

    [JsonPropertyName("ac_current")]
    public double? AcCurrent { get; init; }

    [JsonPropertyName("ac_tot_power_kw")]
    public double? AcTotalPowerKw { get; init; }

    [JsonPropertyName("ac_a_volt")]
    public double? AcPhaseAVoltage { get; init; }

    [JsonPropertyName("ac_a_current")]
    public double? AcPhaseACurrent { get; init; }

    [JsonPropertyName("ac_b_volt")]
    public double? AcPhaseBVoltage { get; init; }

    [JsonPropertyName("ac_b_current")]
    public double? AcPhaseBCurrent { get; init; }

    [JsonPropertyName("ac_c_volt")]
    public double? AcPhaseCVoltage { get; init; }

    [JsonPropertyName("ac_c_current")]
    public double? AcPhaseCCurrent { get; init; }
}

internal sealed class MobileChargeData
{
    [JsonPropertyName("target_soc")]
    public double? TargetSoc { get; init; }
}

internal sealed class MobileGridDischargeData
{
    [JsonPropertyName("target_soc")]
    public double? TargetSoc { get; init; }
}

internal sealed class MobileExternalData
{
    [JsonPropertyName("data01")]
    public string Data01 { get; init; } = string.Empty;
}