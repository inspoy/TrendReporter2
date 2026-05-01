using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace TrendReporter2.App.DataView;

public static class DataViewRenderer
{
    public static string RenderTable(DataViewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Collection {result.CollectionName}");
        builder.AppendLine($"{result.ReturnedRowCount} rows");

        if (result.ReturnedRowCount == 0)
        {
            return builder.ToString().TrimEnd();
        }

        var columns = GetColumns(result.Rows);
        if (columns.Count == 0)
        {
            builder.Append("(no fields)");
            return builder.ToString().TrimEnd();
        }

        var columnWidths = GetColumnWidths(columns, result.Rows);

        builder.AppendLine(RenderBorder(columns, columnWidths));
        builder.AppendLine(RenderRow(columns, columnWidths, column => column));
        builder.AppendLine(RenderBorder(columns, columnWidths));

        foreach (var row in result.Rows)
        {
            builder.AppendLine(RenderRow(columns, columnWidths, column => FormatCell(row.Fields.TryGetValue(column, out var value) ? value : null)));
        }

        builder.Append(RenderBorder(columns, columnWidths));
        return builder.ToString().TrimEnd();
    }

    public static string RenderJson(DataViewResult result)
        => JsonConvert.SerializeObject(ToSerializable(result), Formatting.None);

    private static IReadOnlyList<string> GetColumns(IReadOnlyList<DataViewRow> rows)
    {
        var allColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var column in row.Fields.Keys)
            {
                allColumns.Add(column);
            }
        }

        var orderedColumns = new List<string>(allColumns.Count);
        if (allColumns.Remove("_id"))
        {
            orderedColumns.Add("_id");
        }
        else if (allColumns.Remove("Id"))
        {
            orderedColumns.Add("Id");
        }

        orderedColumns.AddRange(allColumns.OrderBy(column => column, StringComparer.Ordinal));
        return orderedColumns;
    }

    private static Dictionary<string, int> GetColumnWidths(IReadOnlyList<string> columns, IReadOnlyList<DataViewRow> rows)
    {
        var widths = columns.ToDictionary(column => column, column => column.Length, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            foreach (var column in columns)
            {
                var value = row.Fields.TryGetValue(column, out var fieldValue) ? FormatCell(fieldValue) : string.Empty;
                if (value.Length > widths[column])
                {
                    widths[column] = value.Length;
                }
            }
        }

        return widths;
    }

    private static string RenderRow(IReadOnlyList<string> columns, IReadOnlyDictionary<string, int> widths, Func<string, string> valueSelector)
    {
        var cells = columns.Select(column => valueSelector(column).PadRight(widths[column]));
        return $"| {string.Join(" | ", cells)} |";
    }

    private static object ToSerializable(DataViewResult result)
        => new
        {
            result.CollectionName,
            result.RequestedLimit,
            result.ReturnedRowCount,
            Rows = result.Rows.Select(row => ToOrderedDictionary(row.Fields)).ToList()
        };

    private static Dictionary<string, object?> ToOrderedDictionary(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        var fieldList = fields.ToList();
        var fieldDictionary = new Dictionary<string, object?>(fieldList.Count, StringComparer.Ordinal);

        foreach (var field in fieldList)
        {
            fieldDictionary[field.Key] = field.Value;
        }

        var orderedFields = new Dictionary<string, object?>(fieldDictionary.Count, StringComparer.Ordinal);
        var columns = GetColumns(new[] { new DataViewRow(fieldDictionary) });

        foreach (var column in columns)
        {
            orderedFields[column] = ToSerializableValue(fieldDictionary[column]);
        }

        return orderedFields;
    }

    private static object? ToSerializableValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is IReadOnlyDictionary<string, object?> dictionary)
        {
            return ToOrderedDictionary(dictionary);
        }

        if (value is IDictionary<string, object?> mutableDictionary)
        {
            return ToOrderedDictionary(mutableDictionary);
        }

        if (value is not string && value is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object?>().Select(ToSerializableValue).ToList();
        }

        return value;
    }

    private static string FormatCell(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            string stringValue => stringValue,
            char charValue => charValue.ToString(),
            bool boolValue => boolValue.ToString(),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable when value is not Enum => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ when IsJsonValue(value) => JsonConvert.SerializeObject(value, Formatting.None),
            _ => JsonConvert.SerializeObject(value, Formatting.None)
        };

        return text.Length <= 120 ? text : text[..117] + "...";
    }

    private static string RenderBorder(IReadOnlyList<string> columns, IReadOnlyDictionary<string, int> widths)
        => $"| {string.Join(" | ", columns.Select(column => new string('-', widths[column])))} |";

    private static bool IsJsonValue(object value)
        => value is IReadOnlyDictionary<string, object?>
            or IDictionary<string, object?>
            or System.Collections.IEnumerable;
}
