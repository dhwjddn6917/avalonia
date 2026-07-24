using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MobileEssControl.Models.Admin;

/// <summary>
/// 관리자 모드 모니터링 표(31001~31057)를 엑셀("Registers" + "BitFields" 시트)에서
/// 읽어 <see cref="AdminRegisterRow"/> 목록으로 만듭니다.
/// 30001~30016 쓰기 제어표는 대상이 아닙니다.
/// </summary>
public static class AdminMapExcelLoader
{
    // AdminBitFieldDecoder와 동일한 폭으로 맞춰 ':' 위치가 어긋나지 않게 합니다.
    private const int RemarkNameWidth = 46;

    public static AdminMapDefinition Load(string filePath)
    {
        using XLWorkbook workbook = new(filePath);

        if (!workbook.TryGetWorksheet("Registers", out IXLWorksheet? registersSheet))
        {
            throw new InvalidOperationException(
                "엑셀에 'Registers' 시트가 없습니다.");
        }

        if (!workbook.TryGetWorksheet("BitFields", out IXLWorksheet? bitFieldsSheet))
        {
            throw new InvalidOperationException(
                "엑셀에 'BitFields' 시트가 없습니다.");
        }

        AdminMapDefinition map = new();

        int rowIndex = 0;

        foreach (Dictionary<string, string> row in ReadRowsByHeader(registersSheet))
        {
            rowIndex++;

            map.Registers.Add(new AdminMapRegisterDefinition
            {
                RelativeAddress = (ushort)GetRequiredInt(row, "RelativeAddress", "Registers", rowIndex),
                AbsoluteAddress = GetRequiredString(row, "AbsoluteAddress", "Registers", rowIndex),
                Name = GetRequiredString(row, "Name", "Registers", rowIndex),
                Unit = GetString(row, "Unit", "-"),
                DataType = GetString(row, "DataType", "UINT16"),
                Scale = GetDouble(row, "Scale", 1.0),
                IsSigned = GetBool(row, "IsSigned"),
                DecimalPlaces = GetInt(row, "DecimalPlaces", 2),
                IsBitField = GetBool(row, "IsBitField"),
                WordLength = GetInt(row, "WordLength", 1),
                FormatterKind = GetString(row, "FormatterKind", "")
            });
        }

        if (map.Registers.Count == 0)
        {
            throw new InvalidOperationException(
                "'Registers' 시트에 데이터가 없습니다.");
        }

        rowIndex = 0;

        foreach (Dictionary<string, string> row in ReadRowsByHeader(bitFieldsSheet))
        {
            rowIndex++;

            map.BitFields.Add(new AdminMapBitFieldDefinition
            {
                RegisterKey = GetRequiredString(row, "RegisterKey", "BitFields", rowIndex),
                FieldName = GetRequiredString(row, "FieldName", "BitFields", rowIndex),
                BitStart = GetRequiredInt(row, "BitStart", "BitFields", rowIndex),
                BitWidth = GetInt(row, "BitWidth", 1),
                Labels = GetRequiredString(row, "Labels", "BitFields", rowIndex)
            });
        }

        return map;
    }

    /// <summary>
    /// 로드된 정의로 <see cref="AdminRegisterRow"/> 목록을 만듭니다.
    /// textFormatters/valueFormatters는 FormatterKind 문자열과 매칭되는
    /// 특수 포맷 함수(예: 역순 아스키 문자열, 제조일자, 버전)를 제공합니다.
    /// </summary>
    public static List<AdminRegisterRow> BuildRegisterRows(
        AdminMapDefinition map,
        IReadOnlyDictionary<string, Func<ushort[], int, string>> textFormatters,
        IReadOnlyDictionary<string, Func<ushort, string>> valueFormatters)
    {
        Dictionary<string, List<AdminMapBitFieldDefinition>> bitFieldsByRegister =
            map.BitFields
                .GroupBy(field => field.RegisterKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(field => field.BitStart).ToList(),
                    StringComparer.OrdinalIgnoreCase);

        List<AdminRegisterRow> rows = new();

        foreach (AdminMapRegisterDefinition definition in
            map.Registers.OrderBy(definition => definition.RelativeAddress))
        {
            Func<ushort, string>? bitFieldDecoder = null;

            if (definition.IsBitField &&
                bitFieldsByRegister.TryGetValue(definition.AbsoluteAddress, out List<AdminMapBitFieldDefinition>? fields))
            {
                bitFieldDecoder = BuildBitFieldDecoder(fields);
            }

            Func<ushort[], int, string>? textFormatter = null;
            Func<ushort, string>? valueFormatter = null;

            if (!string.IsNullOrEmpty(definition.FormatterKind))
            {
                if (textFormatters.TryGetValue(definition.FormatterKind, out Func<ushort[], int, string>? foundTextFormatter))
                {
                    textFormatter = foundTextFormatter;
                }
                else if (valueFormatters.TryGetValue(definition.FormatterKind, out Func<ushort, string>? foundValueFormatter))
                {
                    valueFormatter = foundValueFormatter;
                }
            }

            rows.Add(new AdminRegisterRow
            {
                RelativeAddress = definition.RelativeAddress,
                AbsoluteAddressText = definition.AbsoluteAddress,
                Name = definition.Name,
                Unit = definition.Unit,
                DataType = definition.DataType,
                Scale = definition.Scale,
                IsSigned = definition.IsSigned,
                DecimalPlaces = definition.DecimalPlaces,
                IsBitField = definition.IsBitField,
                BitFieldDecoder = bitFieldDecoder,
                WordLength = definition.WordLength,
                ValuesFormatter = textFormatter,
                ValueFormatter = valueFormatter
            });
        }

        return rows;
    }

    private static Func<ushort, string> BuildBitFieldDecoder(
        List<AdminMapBitFieldDefinition> fields)
    {
        return raw =>
        {
            List<string> lines = new(fields.Count);

            foreach (AdminMapBitFieldDefinition field in fields)
            {
                int mask = (1 << field.BitWidth) - 1;
                int value = (raw >> field.BitStart) & mask;
                string label = LookupLabel(field.Labels, value);

                lines.Add($"{field.FieldName.PadRight(RemarkNameWidth)} : {label}");
            }

            return string.Join(Environment.NewLine, lines);
        };
    }

    private static string LookupLabel(string labels, int value)
    {
        foreach (string pair in labels.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equalsIndex = pair.IndexOf('=');

            if (equalsIndex <= 0)
            {
                continue;
            }

            string keyText = pair[..equalsIndex].Trim();
            string labelText = pair[(equalsIndex + 1)..].Trim();

            if (int.TryParse(keyText, out int key) && key == value)
            {
                return labelText;
            }
        }

        return "Reserved";
    }

    private static List<Dictionary<string, string>> ReadRowsByHeader(IXLWorksheet worksheet)
    {
        List<Dictionary<string, string>> result = new();

        IXLRow? headerRow = worksheet.RowsUsed().FirstOrDefault();

        if (headerRow is null)
        {
            return result;
        }

        Dictionary<string, int> columnIndexByHeader = new(StringComparer.OrdinalIgnoreCase);

        foreach (IXLCell cell in headerRow.CellsUsed())
        {
            string header = cell.GetString().Trim();

            if (!string.IsNullOrEmpty(header))
            {
                columnIndexByHeader[header] = cell.Address.ColumnNumber;
            }
        }

        foreach (IXLRow row in worksheet.RowsUsed().Skip(1))
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, int> column in columnIndexByHeader)
            {
                values[column.Key] = row.Cell(column.Value).GetString().Trim();
            }

            if (values.Values.All(string.IsNullOrEmpty))
            {
                continue;
            }

            result.Add(values);
        }

        return result;
    }

    private static string GetString(
        Dictionary<string, string> row,
        string column,
        string defaultValue = "")
    {
        return row.TryGetValue(column, out string? value) && !string.IsNullOrEmpty(value)
            ? value
            : defaultValue;
    }

    private static string GetRequiredString(
        Dictionary<string, string> row,
        string column,
        string sheetName,
        int dataRowIndex)
    {
        if (!row.TryGetValue(column, out string? value) || string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"'{sheetName}' 시트 데이터 {dataRowIndex}번째 행에 '{column}' 값이 없습니다.");
        }

        return value;
    }

    private static int GetInt(
        Dictionary<string, string> row,
        string column,
        int defaultValue = 0)
    {
        string text = GetString(row, column);

        return int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out int value)
            ? value
            : defaultValue;
    }

    private static int GetRequiredInt(
        Dictionary<string, string> row,
        string column,
        string sheetName,
        int dataRowIndex)
    {
        string text = GetRequiredString(row, column, sheetName, dataRowIndex);

        if (!int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out int value))
        {
            throw new InvalidOperationException(
                $"'{sheetName}' 시트 데이터 {dataRowIndex}번째 행의 '{column}' 값 '{text}'을(를) 숫자로 읽을 수 없습니다.");
        }

        return value;
    }

    private static double GetDouble(
        Dictionary<string, string> row,
        string column,
        double defaultValue = 0)
    {
        string text = GetString(row, column);

        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double value)
            ? value
            : defaultValue;
    }

    private static bool GetBool(
        Dictionary<string, string> row,
        string column)
    {
        string text = GetString(row, column).Trim();

        return text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("1", StringComparison.Ordinal) ||
               text.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("YES", StringComparison.OrdinalIgnoreCase);
    }
}
