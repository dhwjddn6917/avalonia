using System;

namespace MobileEssControl.Services.Mobile;

public sealed class MobileTelemetryOptions
{
    /// <summary>
    /// TCP 서버 IP만 표시하는 홈페이지 주소입니다.
    /// App.axaml.cs에서 값을 입력합니다.
    /// </summary>
    public string IpLookupUrl { get; init; } = string.Empty;

    public int ServerPort { get; init; } = 19991;

    public string EssCarId { get; init; } =
        "evkmc_vcess_MobESS01";

    public TimeSpan UpdateInterval { get; init; } =
        TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectInterval { get; init; } =
        TimeSpan.FromSeconds(5);
}
