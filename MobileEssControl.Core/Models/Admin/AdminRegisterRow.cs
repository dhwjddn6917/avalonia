using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;


namespace MobileEssControl.Models.Admin;

/// <summary>
/// MobileESS Address Map의 EMS 상태 레지스터를 관리자 화면에 표시하기 위한 모델입니다.
/// RelativeAddress는 현재 읽어 온 배열 안에서의 0-base 위치입니다.
/// Bit Field 항목은 기본적으로 접어 두고, 상세 버튼을 누르면 각 상태를 한 줄씩 표시합니다.
/// </summary>
public partial class AdminRegisterRow : ObservableObject
{
    public required ushort RelativeAddress { get; init; }

    public required string AbsoluteAddressText { get; init; }

    public required string Name { get; init; }

    public required string Unit { get; init; }

    public required string DataType { get; init; }

    public double Scale { get; init; } = 1.0;

    public bool IsSigned { get; init; }

    public bool IsBitField { get; init; }

    public Func<ushort, string>? BitFieldDecoder { get; init; }

    public Func<ushort, string>? ValueFormatter { get; init; }

    /// <summary>
    /// Manufacturer Name, Device Code처럼 여러 Register를 하나의 값으로 표현할 때 사용합니다.
    /// </summary>
    public int WordLength { get; init; } = 1;

    public Func<ushort[], int, string>? ValuesFormatter { get; init; }

    public int DecimalPlaces { get; init; } = 2;

    /// <summary>
    /// Bit Field일 때만 상세 항목을 한 줄씩 담습니다.
    /// </summary>
    public ObservableCollection<AdminBitDetailRow> BitDetails { get; } = new();

    [ObservableProperty]
    private string displayValue = "-";

    [ObservableProperty]
    private string rawValue = "-";

    [ObservableProperty]
    private bool isExpanded;

    public string ExpandButtonText => IsExpanded ? "− 접기" : "+ 상세";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandButtonText));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        if (IsBitField)
        {
            IsExpanded = !IsExpanded;
        }
    }

    public void Update(ushort[] values, ushort startAddress)
    {
        int index = RelativeAddress - startAddress;

        if (index < 0 || index >= values.Length)
        {
            return;
        }

        int wordCount = Math.Max(1, WordLength);
        int availableWordCount = Math.Min(wordCount, values.Length - index);

        if (ValuesFormatter is not null)
        {
            RawValue = FormatRawWords(values, index, availableWordCount);
            DisplayValue = ValuesFormatter(values, index);
            return;
        }

        Update(values[index]);
    }

    public void Update(ushort raw)
    {
        RawValue = $"0x{raw:X4} ({raw})";

        if (IsBitField)
        {
            string decodedText = BitFieldDecoder?.Invoke(raw) ?? $"0x{raw:X4}";

            UpdateBitDetails(decodedText);
            DisplayValue = $"상세 상태 {BitDetails.Count}개 항목";
            return;
        }

        if (ValueFormatter is not null)
        {
            DisplayValue = ValueFormatter(raw);
            return;
        }

        double sourceValue = IsSigned
            ? unchecked((short)raw)
            : raw;

        double scaledValue = sourceValue * Scale;
        string numberFormat = $"F{DecimalPlaces}";

        DisplayValue = scaledValue.ToString(numberFormat, CultureInfo.InvariantCulture);
    }

    private void UpdateBitDetails(string decodedText)
    {
        List<(string Name, string Value)> parsedRows = ParseBitFieldLines(decodedText);

        for (int i = 0; i < parsedRows.Count; i++)
        {
            (string name, string value) = parsedRows[i];

            if (i < BitDetails.Count)
            {
                BitDetails[i].Update(name, value);
            }
            else
            {
                BitDetails.Add(new AdminBitDetailRow(name, value));
            }
        }

        while (BitDetails.Count > parsedRows.Count)
        {
            BitDetails.RemoveAt(BitDetails.Count - 1);
        }
    }

    internal static List<(string Name, string Value)> ParseBitFieldLines(string decodedText)
    {
        var result = new List<(string Name, string Value)>();

        string[] lines = decodedText
            .Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            int colonIndex = line.IndexOf(':');

            string name = colonIndex >= 0
                ? line[..colonIndex].Trim()
                : line.Trim();

            string value = colonIndex >= 0 && colonIndex < line.Length - 1
                ? line[(colonIndex + 1)..].Trim()
                : "-";

            if (!string.IsNullOrWhiteSpace(name))
            {
                result.Add((name, value));
            }
        }

        return result;
    }

    private static string FormatRawWords(ushort[] values, int startIndex, int wordCount)
    {
        var builder = new StringBuilder();

        for (int i = 0; i < wordCount; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append($"0x{values[startIndex + i]:X4}");
        }

        return builder.ToString();
    }
}

/// <summary>
/// 하나의 Bit Field 내부 상태를 "항목 / 값"으로 나누어 표시하기 위한 행입니다.
/// </summary>
public partial class AdminBitDetailRow : ObservableObject
{
    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string value;

    public AdminBitDetailRow(string name, string value)
    {
        this.name = name;
        this.value = value;
    }

    public void Update(string name, string value)
    {
        Name = name;
        Value = value;
    }
}
