using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Detects whether a Segoe icon font (Fluent Icons on Win11, MDL2 on Win10)
    /// is installed. Callers hide glyph decorations entirely on systems without one
    /// (Win7) instead of rendering tofu boxes.</summary>
    internal static class IconFont
    {
        public static readonly bool Available = Detect();

        private static bool Detect()
        {
            try
            {
                using (var fonts = new System.Drawing.Text.InstalledFontCollection())
                {
                    foreach (var f in fonts.Families)
                    {
                        if (f.Name == "Segoe Fluent Icons" || f.Name == "Segoe MDL2 Assets")
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
