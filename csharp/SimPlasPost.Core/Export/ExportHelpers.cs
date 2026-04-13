namespace SimPlasPost.Core.Export;

/// <summary>
/// Shared escape/sanitization functions for export formats.
/// Fixes the XSS/injection vulnerabilities identified in the audit.
/// </summary>
public static class ExportHelpers
{
    /// <summary>Escape special XML characters for SVG output.</summary>
    public static string EscapeXml(string s)
    {
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
    }

    /// <summary>Escape special PostScript string characters for EPS output.</summary>
    public static string EscapePs(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("(", "\\(")
                .Replace(")", "\\)");
    }

    public const string CmFontFamily =
        "\"Computer Modern Serif\",\"CMU Serif\",\"Latin Modern Roman\",\"Times New Roman\",serif";
}
