using System.Drawing.Text;

namespace EasyRecorder;

internal static class UiFonts
{
    private static readonly string UiFamily = Resolve(
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "Segoe UI",
        "Arial");

    private static readonly string MonoFamily = Resolve(
        "Consolas",
        "Cascadia Mono",
        "Courier New",
        "Lucida Console");

    public static Font Create(float size, FontStyle style = FontStyle.Regular)
    {
        return new Font(UiFamily, size, style);
    }

    public static Font CreateMono(float size, FontStyle style = FontStyle.Regular)
    {
        return new Font(MonoFamily, size, style);
    }

    private static string Resolve(params string[] preferred)
    {
        HashSet<string> installed;
        try
        {
            using InstalledFontCollection collection = new();
            installed = collection.Families
                .Select(f => f.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (string candidate in preferred)
        {
            if (installed.Contains(candidate))
            {
                return candidate;
            }
        }

        try
        {
            string? fallback = SystemFonts.MessageBoxFont?.FontFamily.Name;
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }
        }
        catch
        {
        }

        return FontFamily.GenericSansSerif.Name;
    }
}
