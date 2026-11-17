using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IPManager.Services;

public sealed record ReportTable
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public required IReadOnlyList<string> Headers { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public static class ReportExporter
{
    public static string ToCsv(ReportTable table)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", table.Headers.Select(EscapeCsv)));
        foreach (var row in table.Rows)
            sb.AppendLine(string.Join(",", row.Select(EscapeCsv)));
        return sb.ToString();
    }

    public static void WriteCsv(ReportTable table, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToCsv(table), new UTF8Encoding(true));
    }

    public static void WriteJson(ReportTable table, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new
        {
            title = table.Title,
            subtitle = table.Subtitle,
            generatedUtc = DateTime.UtcNow,
            application = "IP Manager",
            metadata = table.Metadata,
            headers = table.Headers,
            rows = table.Rows.Select(r => r.Select((v, i) => new { key = i < table.Headers.Count ? table.Headers[i] : $"column{i}", value = v })
                .ToDictionary(x => x.key, x => x.value))
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void WritePdf(ReportTable table, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pdf = SimplePdfWriter.CreateReport(table);
        File.WriteAllBytes(path, pdf);
    }

    private static string EscapeCsv(string? value)
    {
        var v = value ?? "";
        if (v.Contains('"') || v.Contains(',') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}

/// <summary>
/// Small dependency-free PDF 1.4 writer for tabular reports. Text is embedded with the
/// standard Helvetica/Courier fonts (WinAnsi), one table row per text line.
/// </summary>
public static class SimplePdfWriter
{
    private const int PageWidth = 842;  // A4 landscape, points
    private const int PageHeight = 595;
    private const float Margin = 32;
    private const float LineHeight = 10.5f;

    public static byte[] CreateReport(ReportTable table)
    {
        // Build text lines per page (title, metadata, header row, data rows).
        var lines = new List<(string Text, bool Bold, bool Header)>();
        lines.Add((table.Title, true, false));
        if (!string.IsNullOrWhiteSpace(table.Subtitle)) lines.Add((table.Subtitle, false, false));
        lines.Add(($"Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} by IP Manager - {table.Rows.Count} row(s)", false, false));
        foreach (var meta in table.Metadata) lines.Add(($"{meta.Key}: {meta.Value}", false, false));
        lines.Add(("", false, false));
        lines.Add((string.Join(" | ", table.Headers), true, true));
        lines.Add((new string('-', 150), false, false));
        foreach (var row in table.Rows)
            lines.Add((string.Join(" | ", row.Select(Clean)), false, false));
        if (table.Rows.Count == 0)
            lines.Add(("(no records)", false, false));

        var usableWidth = PageWidth - Margin * 2;
        var wrapped = new List<(string Text, bool Bold, bool Header)>();
        foreach (var line in lines)
        {
            foreach (var part in Wrap(line.Text, usableWidth / 4.6f))
                wrapped.Add((part, line.Bold, line.Header));
        }

        var linesPerPage = (int)((PageHeight - Margin * 2 - 24) / LineHeight);
        var pages = wrapped.Chunk(linesPerPage).ToList();
        if (pages.Count == 0) pages = new[] { wrapped.ToArray() }.ToList();

        var objects = new List<string>();
        var pageObjectIds = new List<int>();
        var contentObjectIds = new List<int>();

        // Object numbering: 1 catalog, 2 pages, 3 font Helvetica, 4 font Courier, 5 font Helvetica-Bold,
        // then page/content pairs starting at 6.
        for (var i = 0; i < pages.Count; i++)
        {
            pageObjectIds.Add(6 + i * 2);
            contentObjectIds.Add(7 + i * 2);
        }

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{string.Join(" ", pageObjectIds.Select(id => $"{id} 0 R"))}] /Count {pages.Count} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        for (var i = 0; i < pages.Count; i++)
        {
            var content = BuildPageContent(pages[i], i + 1, pages.Count);
            var contentBytes = Encoding.Latin1.GetBytes(content);
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] " +
                        $"/Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >> >> /Contents {contentObjectIds[i]} 0 R >>");
            objects.Add($"<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream");
        }

        return Assemble(objects);
    }

    private static string BuildPageContent(IEnumerable<(string Text, bool Bold, bool Header)> lines, int pageNumber, int pageCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BT");
        var y = PageHeight - Margin;
        var first = true;
        foreach (var line in lines)
        {
            var font = line.Bold || line.Header ? "/F3" : "/F2";
            var size = line.Bold && !line.Header ? 10 : 7.6f;
            if (first)
            {
                sb.AppendLine($"{font} {size} Tf");
                sb.AppendLine($"1 0 0 1 {Margin} {y} Tm");
                first = false;
            }
            else
            {
                sb.AppendLine($"{font} {size} Tf");
            }

            sb.AppendLine($"({EscapePdf(line.Text)}) Tj");
            sb.AppendLine($"0 -{LineHeight.ToString(CultureInfo.InvariantCulture)} Td");
            y -= LineHeight;
        }
        sb.AppendLine("ET");
        sb.AppendLine($"BT /F1 7 Tf 1 0 0 1 {PageWidth - 110} {Margin - 12} Tm (Page {pageNumber} of {pageCount}) Tj ET");
        return sb.ToString();
    }

    private static IEnumerable<string> Wrap(string text, float maxChars)
    {
        var limit = Math.Max(20, (int)maxChars);
        if (text.Length <= limit) { yield return text; yield break; }
        var remaining = text;
        while (remaining.Length > limit)
        {
            var cut = remaining.LastIndexOf(' ', limit);
            if (cut < limit / 2) cut = limit;
            yield return remaining[..cut];
            remaining = remaining[cut..].TrimStart();
        }
        if (remaining.Length > 0) yield return remaining;
    }

    private static string Clean(string value)
    {
        var clean = new string(value.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
        return clean.Length > 220 ? clean[..220] : clean;
    }

    private static string EscapePdf(string text)
    {
        var clean = new string(text.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
        return clean.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    }

    private static byte[] Assemble(List<string> objects)
    {
        using var ms = new MemoryStream();
        void Write(string s)
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefPos = ms.Position;
        Write($"xref\n0 {objects.Count + 1}\n");
        Write("0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info << /Producer (IP Manager) /Creator (IP Manager) >> >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return ms.ToArray();
    }
}
