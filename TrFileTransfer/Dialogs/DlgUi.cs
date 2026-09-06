using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Shared helpers for the code-built dialog windows: theme resource lookup,
    /// window chrome initialization, and styled control factories.</summary>
    internal static class DlgUi
    {
        public static T Res<T>(string key) where T : class
        {
            return Application.Current.TryFindResource(key) as T;
        }

        /// <summary>Applies the dark chrome, size clamps to the work area, and defaults.</summary>
        public static void Init(Window w, string title, double width, double height, double minW, double minH)
        {
            UiChrome.ApplyDark(w);
            w.Title = title;
            double waW = SystemParameters.WorkArea.Width;
            double waH = SystemParameters.WorkArea.Height;
            w.Width = Math.Min(width, waW - 24);
            w.Height = Math.Min(height, waH - 24);
            w.MinWidth = Math.Min(minW, w.Width);
            w.MinHeight = Math.Min(minH, w.Height);
            w.Background = Res<Brush>("Brush.Window");
            w.FontFamily = Res<FontFamily>("Font.Main");
            w.FontSize = 12;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        public static Button Primary(string text)
        {
            return new Button { Style = Res<Style>("BtnPrimary"), Content = text };
        }

        public static Button Secondary(string text)
        {
            return new Button { Style = Res<Style>("BtnSecondary"), Content = text };
        }

        public static ListBox DarkList()
        {
            return new ListBox { Style = Res<Style>("ListDark") };
        }

        public static TextBox Input()
        {
            return new TextBox { Style = Res<Style>("TxtInput") };
        }

        public static TextBlock Label(string text)
        {
            return new TextBlock { Style = Res<Style>("FieldLabel"), Text = text };
        }

        /// <summary>Right-aligned button row (visual order left→right = argument order).</summary>
        public static StackPanel ButtonRowRight(params Button[] buttons)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            for (int i = 0; i < buttons.Length; i++)
            {
                if (i > 0) buttons[i].Margin = new Thickness(8, 0, 0, 0);
                panel.Children.Add(buttons[i]);
            }
            return panel;
        }

        public static Button PrimaryMin(string text, double min)
        {
            var b = Primary(text);
            b.MinWidth = min;
            return b;
        }

        public static Button SecondaryMin(string text, double min)
        {
            var b = Secondary(text);
            b.MinWidth = min;
            return b;
        }
    }
}
