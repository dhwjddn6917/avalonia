using System.Collections.Generic;

namespace MobileEssControl.Models.Admin;

/// <summary>
/// 엑셀 "Registers" 시트 한 줄에 대응합니다.
/// </summary>
public sealed class AdminMapRegisterDefinition
{
    public required ushort RelativeAddress { get; init; }

    public required string AbsoluteAddress { get; init; }

    public required string Name { get; init; }

    public string Unit { get; init; } = "-";

    public string DataType { get; init; } = "UINT16";

    public double Scale { get; init; } = 1.0;

    public bool IsSigned { get; init; }

    public int DecimalPlaces { get; init; } = 2;

    public bool IsBitField { get; init; }

    public int WordLength { get; init; } = 1;

    /// <summary>
    /// 비어 있으면 일반 숫자 값입니다.
    /// "ReverseAscii8" / "ManufacturerDate" / "MajorMinorVersion"는
    /// AdminMapExcelLoader에 등록된 특수 포맷터를 사용합니다.
    /// </summary>
    public string FormatterKind { get; init; } = "";
}

/// <summary>
/// 엑셀 "BitFields" 시트 한 줄에 대응합니다.
/// RegisterKey는 소속 레지스터의 AbsoluteAddress와 일치해야 합니다.
/// </summary>
public sealed class AdminMapBitFieldDefinition
{
    public required string RegisterKey { get; init; }

    public required string FieldName { get; init; }

    public required int BitStart { get; init; }

    public int BitWidth { get; init; } = 1;

    /// <summary>
    /// "0=Off;1=On" 형식. 매칭되지 않는 값은 "Reserved"로 표시됩니다.
    /// </summary>
    public required string Labels { get; init; }
}

public sealed class AdminMapDefinition
{
    public List<AdminMapRegisterDefinition> Registers { get; } = new();

    public List<AdminMapBitFieldDefinition> BitFields { get; } = new();
}
