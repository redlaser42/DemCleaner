using System.Windows;
using System.Windows.Media;

namespace DemCleaner.Services;

public record AppThemePalette(
    string Name,
    string BgWindow,
    string BgCard,
    string BgWell,
    string BgRowAlt,
    string BorderCard,
    string BorderControl,
    string TextPrimary,
    string TextSecondary,
    string TextMuted,
    string AccentPrimary,
    string AccentPrimaryHover,
    string AccentExport,
    string AccentExportHover,
    string AccentDanger,
    string AccentWarning);

public static class ThemeManager
{
    public static readonly IReadOnlyList<AppThemePalette> Themes = new List<AppThemePalette>
    {
        // 0. Sage & Sandstone (Darker Warm Tan Window Background + Rich Forest Green Panels) — Default mixed earthy theme
        new(
            Name: "Sage & Sandstone (Dark Tan BG / Green Panels)",
            BgWindow: "#AD9168",       // Darker warm sandstone/saddle tan canvas
            BgCard: "#1E3326",         // Deep forest pine green panels
            BgWell: "#15241B",         // Darker moss green inner wells/inputs
            BgRowAlt: "#243D2E",       // Alternating sage-pine table row
            BorderCard: "#3B5E49",     // Sage green panel border
            BorderControl: "#8C6F48",  // Darker warm bronze-tan control border
            TextPrimary: "#F0E6D2",    // Warm tan-cream text on green panels
            TextSecondary: "#CDBB9E",  // Darker sand secondary text
            TextMuted: "#96AA9B",      // Muted sage-tan label text
            AccentPrimary: "#C27D38",  // Richer dark ochre/amber tan buttons on green panels
            AccentPrimaryHover: "#A66726",
            AccentExport: "#529E66",   // Vibrant meadow green export action
            AccentExportHover: "#3F8250",
            AccentDanger: "#D95B43",
            AccentWarning: "#E09B38"
        ),
        // 1. Topographic Moss (Darker Khaki Tan Background + Olive & Moss Green Panels)
        new(
            Name: "Topo Buckskin & Olive (Dark Tan BG / Olive Panels)",
            BgWindow: "#B59C77",       // Darker topographic buckskin/khaki tan
            BgCard: "#2B3626",         // Earthy olive-moss green panels
            BgWell: "#1F271B",         // Deep olive well
            BgRowAlt: "#34422E",       // Alternating moss row
            BorderCard: "#516346",     // Olive border
            BorderControl: "#8F7856",  // Darker khaki-tan control border
            TextPrimary: "#F2E9D8",    // Warm ivory-tan text
            TextSecondary: "#D1C1A5",  // Deeper tan secondary text
            TextMuted: "#A5B09C",      // Soft sage muted text
            AccentPrimary: "#7FA655",  // Fresh fern green accent
            AccentPrimaryHover: "#688C41",
            AccentExport: "#BA7832",   // Rich copper-tan export button
            AccentExportHover: "#9E6222",
            AccentDanger: "#D65341",
            AccentWarning: "#DC9A32"
        ),
        // 2. Canyon & Pine (Deep Pine Window Background + Darker Tan & Sandstone Panels)
        new(
            Name: "Canyon & Pine (Deep Green BG / Dark Tan Panels)",
            BgWindow: "#16261C",       // Deep evergreen forest backdrop
            BgCard: "#BD9F77",         // Darker warm canyon tan / sandstone panels
            BgWell: "#A88B63",         // Recessed darker saddle-sand input wells
            BgRowAlt: "#B3956D",       // Alternating warm dark sand row
            BorderCard: "#8C704B",     // Deep tan-bark border
            BorderControl: "#3E614B",  // Forest green control accents
            TextPrimary: "#18241C",    // Deep forest charcoal text on tan panels
            TextSecondary: "#2A3D30",  // Dark pine secondary text
            TextMuted: "#47594D",      // Muted sage-bark text
            AccentPrimary: "#265E3D",  // Rich pine green primary buttons
            AccentPrimaryHover: "#1B472D",
            AccentExport: "#995820",   // Deep canyon clay export button
            AccentExportHover: "#7D4516",
            AccentDanger: "#B83326",
            AccentWarning: "#A8620E"
        ),
        // 3. Forest Canopy (Deep Greens & Dark Saddle Tan Accents)
        new(
            Name: "Forest Canopy (Deep Green & Saddle Tan)",
            BgWindow: "#111A14",
            BgCard: "#1A281F",
            BgWell: "#2B2216",         // Rich dark umber-tan inner wells
            BgRowAlt: "#152119",
            BorderCard: "#364F3E",
            BorderControl: "#826946",  // Darker tan-bronze borders
            TextPrimary: "#EFE6D5",
            TextSecondary: "#C9B89D",
            TextMuted: "#92A397",
            AccentPrimary: "#B87835",  // Warm dark saddle-tan primary accent
            AccentPrimaryHover: "#9C6226",
            AccentExport: "#4D9E6D",
            AccentExportHover: "#3B8257",
            AccentDanger: "#E05A47",
            AccentWarning: "#D49322"
        ),
        // 4. Desert Mesa (Deep Roasted Tan & Clay with Sage Green Accents)
        new(
            Name: "Desert Mesa (Dark Clay Tan & Sage Green)",
            BgWindow: "#211911",
            BgCard: "#36291C",
            BgWell: "#1C241E",         // Deep sage-tinted inner wells
            BgRowAlt: "#2C2116",
            BorderCard: "#5C4730",
            BorderControl: "#5A735E",  // Sage green control borders
            TextPrimary: "#F2E8DA",
            TextSecondary: "#CFBFA8",
            TextMuted: "#A38E73",
            AccentPrimary: "#BE7C3B",
            AccentPrimaryHover: "#A36629",
            AccentExport: "#598F62",
            AccentExportHover: "#45734D",
            AccentDanger: "#D9534F",
            AccentWarning: "#DF983B"
        ),
        // 5. Midnight Slate (Neutral Charcoal & Slate Gray)
        new(
            Name: "Midnight Slate (Dark Gray)",
            BgWindow: "#181A1D",       // Deep neutral charcoal-gray window backdrop
            BgCard: "#23262B",         // Slate-gray panel surface
            BgWell: "#141619",         // Recessed dark graphite input well
            BgRowAlt: "#292D33",       // Alternating neutral gray table row
            BorderCard: "#3B4048",     // Medium slate-gray card border
            BorderControl: "#525863",  // Neutral pewter-gray control border
            TextPrimary: "#ECEFF4",    // Crisp off-white text
            TextSecondary: "#C5CAD3",  // Soft silver-gray secondary text
            TextMuted: "#8F96A3",      // Muted neutral gray label text
            AccentPrimary: "#637B96",  // Muted steel-slate primary button accent
            AccentPrimaryHover: "#4F657D",
            AccentExport: "#489468",   // Balanced sage-emerald export accent
            AccentExportHover: "#397A54",
            AccentDanger: "#D9534F",
            AccentWarning: "#D99B38"
        )
    };

    public static void ApplyTheme(ResourceDictionary resources, AppThemePalette palette)
    {
        SetBrush(resources, "ThemeBgWindow", palette.BgWindow);
        SetBrush(resources, "ThemeBgCard", palette.BgCard);
        SetBrush(resources, "ThemeBgWell", palette.BgWell);
        SetBrush(resources, "ThemeBgRowAlt", palette.BgRowAlt);
        SetBrush(resources, "ThemeBorderCard", palette.BorderCard);
        SetBrush(resources, "ThemeBorderControl", palette.BorderControl);
        SetBrush(resources, "ThemeTextPrimary", palette.TextPrimary);
        SetBrush(resources, "ThemeTextSecondary", palette.TextSecondary);
        SetBrush(resources, "ThemeTextMuted", palette.TextMuted);
        SetBrush(resources, "ThemeAccentPrimary", palette.AccentPrimary);
        SetBrush(resources, "ThemeAccentPrimaryHover", palette.AccentPrimaryHover);
        SetBrush(resources, "ThemeAccentExport", palette.AccentExport);
        SetBrush(resources, "ThemeAccentExportHover", palette.AccentExportHover);
        SetBrush(resources, "ThemeAccentDanger", palette.AccentDanger);
        SetBrush(resources, "ThemeAccentWarning", palette.AccentWarning);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string hexColor)
    {
        Color color = (Color)ColorConverter.ConvertFromString(hexColor);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}
