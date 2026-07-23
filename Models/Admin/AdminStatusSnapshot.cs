namespace MobileEssControl.Models.Admin;

/// <summary>
/// 관리자 상태 화면에서 한 번의 Poll 결과를 묶어 전달합니다.
/// 모든 배열은 MobileESS_AddressMap_V1의 Relative Address 기준입니다.
/// </summary>
public sealed class AdminStatusSnapshot
{
    public required ushort[] EmsValues { get; init; }

    public required ushort[] Pack1Values { get; init; }

    public required ushort[] Pack2Values { get; init; }

    public required ushort[] Inverter1Values { get; init; }

    public required ushort[] Inverter2Values { get; init; }
}
