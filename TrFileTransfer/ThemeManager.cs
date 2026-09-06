using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Live dark/light theming. The token brushes declared in Themes/Tokens.xaml
    /// are unfrozen shared instances — every control that captured them via
    /// StaticResource (templates included) re-renders when their Color is mutated, so
    /// switching theme needs no resource-dictionary swap. Both palettes live here;
    /// persistence is the Config key "Theme" ("dark"/"light").</summary>
    internal static class ThemeManager
    {
        private static readonly Dictionary<string, Color> Dark = new Dictionary<string, Color>();
        private static readonly Dictionary<string, Color> Light = new Dictionary<string, Color>();
        private static ResourceDictionary _tokens;
        private static bool _dark = true;

        /// <summary>Raised after a theme switch (UI thread) so chrome can be refreshed.</summary>
        public static event Action Changed;

        public static bool IsDark
        {
            get { return _dark; }
        }

        /// <summary>Merges Tokens.xaml and Controls.xaml into the app resources, then
        /// repaints per the saved theme. Must run before any window parses.</summary>
        public static void Initialize()
        {
            BuildPalettes();

            var app = Application.Current;
            _tokens = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Tokens.xaml") };
            app.Resources.MergedDictionaries.Add(_tokens);
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Controls.xaml") });

            _dark = Config.Get("Theme", "dark") != "light";
            Apply(_dark);
        }

        public static void Toggle()
        {
            SetDark(!_dark);
        }

        public static void SetDark(bool dark)
        {
            if (dark == _dark) return;
            _dark = dark;
            Apply(dark);
            Config.Set("Theme", dark ? "dark" : "light");
            Config.Save();
            var handler = Changed;
            if (handler != null) handler();
        }

        private static void Apply(bool dark)
        {
            // Swap entries in the tokens dictionary — DynamicResource references
            // (templates included) re-query and the whole UI recolors live
            var palette = dark ? Dark : Light;
            foreach (var kv in palette)
            {
                if (_tokens.Contains(kv.Key))
                    _tokens[kv.Key] = new SolidColorBrush(kv.Value);
            }
        }

        /// <summary>Window background for Mica-enabled windows (translucent so the
        /// system backdrop tints through); cards stay opaque on top.</summary>
        public static Brush MicaBackground()
        {
            return _dark
                ? new SolidColorBrush(Color.FromArgb(0xB3, 0x17, 0x19, 0x1F))
                : new SolidColorBrush(Color.FromArgb(0xD9, 0xF2, 0xF4, 0xF8));
        }

        private static Color C(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        private static void BuildPalettes()
        {
            if (Dark.Count > 0) return;

            // key                          dark        light
            Dark["Brush.Window"] = C("#FF17191F");       Light["Brush.Window"] = C("#FFF2F4F8");
            Dark["Brush.Card"] = C("#FF1F232C");         Light["Brush.Card"] = C("#FFFFFFFF");
            Dark["Brush.CardBorder"] = C("#FF2A2F3A");   Light["Brush.CardBorder"] = C("#FFDDE2EA");
            Dark["Brush.CardInner"] = C("#FF1A1E26");    Light["Brush.CardInner"] = C("#FFEDF0F5");
            Dark["Brush.ItemCard"] = C("#FF2A303C");     Light["Brush.ItemCard"] = C("#FFF5F7FA");
            Dark["Brush.ItemCardBorder"] = C("#FF343B4B"); Light["Brush.ItemCardBorder"] = C("#FFD8DEE8");

            Dark["Brush.Accent"] = C("#FF2563EB");       Light["Brush.Accent"] = C("#FF2563EB");
            Dark["Brush.AccentHover"] = C("#FF3B82F6");  Light["Brush.AccentHover"] = C("#FF1D4ED8");
            Dark["Brush.AccentDown"] = C("#FF1D4ED8");   Light["Brush.AccentDown"] = C("#FF1E40AF");

            Dark["Brush.TextPrimary"] = C("#FFF3F4F6");  Light["Brush.TextPrimary"] = C("#FF1F2937");
            Dark["Brush.TextSecondary"] = C("#FF9CA3AF"); Light["Brush.TextSecondary"] = C("#FF6B7280");
            Dark["Brush.TextDisabled"] = C("#FF5B6270"); Light["Brush.TextDisabled"] = C("#FFB6BCC8");

            Dark["Brush.InputBg"] = C("#FF262B36");      Light["Brush.InputBg"] = C("#FFFFFFFF");
            Dark["Brush.InputBorder"] = C("#FF343B4B");  Light["Brush.InputBorder"] = C("#FFCBD2DD");
            Dark["Brush.HoverFill"] = C("#FF2A303C");    Light["Brush.HoverFill"] = C("#FFEEF1F6");
            Dark["Brush.DownFill"] = C("#FF323949");     Light["Brush.DownFill"] = C("#FFE2E7EF");
            Dark["Brush.SelBg"] = C("#FF243550");        Light["Brush.SelBg"] = C("#FFDCE9FB");

            Dark["Brush.LogBg"] = C("#FF121419");        Light["Brush.LogBg"] = C("#FFF7F8FA");
            Dark["Brush.LogFg"] = C("#FFC9D1D9");        Light["Brush.LogFg"] = C("#FF3A4150");

            Dark["Brush.Danger"] = C("#FFF87171");       Light["Brush.Danger"] = C("#FFDC2626");
            Dark["Brush.Success"] = C("#FF34D399");      Light["Brush.Success"] = C("#FF059669");

            Dark["Brush.ScrollThumb"] = C("#FF3A404D");  Light["Brush.ScrollThumb"] = C("#FFC3CAD6");
            Dark["Brush.ScrollThumbHover"] = C("#FF4A5262"); Light["Brush.ScrollThumbHover"] = C("#FFAEB7C4");
            Dark["Brush.ScrollThumbDrag"] = C("#FF5A6375"); Light["Brush.ScrollThumbDrag"] = C("#FF98A2B1");
        }
    }
}
