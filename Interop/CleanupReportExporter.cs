using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SizeMonitor.Interop;

public static class CleanupReportExporter
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.Default };
    public static string ToJson(CleanupAuditReport report) => JsonSerializer.Serialize(report, Json) + Environment.NewLine;
    public static string ToMarkdown(CleanupAuditReport report)
    {
        var text = new StringBuilder().AppendLine("# Canopy cleanup preview report").AppendLine()
            .AppendLine($"Generated: {report.GeneratedUtc:O}").AppendLine($"Estimated potentially reclaimable bytes: {report.Preview.ReclaimableBytes}")
            .AppendLine($"Estimate basis: {(report.Preview.Items.Count == 0 ? "not applicable (no proposed items)" : report.Preview.UsesPhysicalAllocation ? "uniquely accounted physical allocation" : "partial/logical fallback")}")
            .AppendLine($"Risk counts: {report.Preview.LowRiskCount} low, {report.Preview.MediumRiskCount} medium, {report.Preview.HighRiskCount} high")
            .AppendLine().AppendLine("Preview only. This report did not modify the filesystem; execution requires a separate confirmed cleanup operation.").AppendLine()
            .AppendLine("## Rule coverage").AppendLine().AppendLine("| Rule | Matches | Estimated bytes |").AppendLine("|---|---:|---:|");
        foreach (CleanupRuleCoverage rule in report.Rules) text.AppendLine($"| {Escape(rule.RuleId)} | {rule.MatchCount} | {rule.EstimatedPotentialBytes} attributed |");
        text.AppendLine().AppendLine("## Proposed items").AppendLine();
        foreach (CleanupPreviewItem item in report.Preview.Items.OrderBy(x => x.Path, StringComparer.Ordinal))
            text.AppendLine($"- `{Escape(item.Path)}` — {item.Risk}; {item.ReclaimableBytes} estimated bytes; rules: {string.Join(", ", item.RuleIds.OrderBy(x => x, StringComparer.Ordinal).Select(Escape))}; rationale: {string.Join("; ", item.Reasons.Select(Escape))}");
        if (report.Warnings.Count > 0) { text.AppendLine().AppendLine("## Warnings").AppendLine(); foreach (string warning in report.Warnings) text.AppendLine("- " + Escape(warning)); }
        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
    static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    public static async Task WriteAtomicAsync(string destination, string content, CancellationToken token = default)
    {
        string full = Path.GetFullPath(destination); string? directory = Path.GetDirectoryName(full);
        if (directory is null) throw new ArgumentException("Export path must have a parent directory.", nameof(destination));
        Directory.CreateDirectory(directory); string temporary = Path.Combine(directory, "." + Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Move(temporary, full, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
