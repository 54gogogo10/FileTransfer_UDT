using System;
using System.Windows;
using System.Windows.Controls;

namespace TrFileTransfer
{
    /// <summary>Compact numeric input with up/down spinners — stands in for the WinForms
    /// NumericUpDown used across the panels. Value is clamped to [Min, Max].</summary>
    public partial class NumericBox : UserControl
    {
        public static readonly DependencyProperty MinProperty = DependencyProperty.Register(
            "Min", typeof(int), typeof(NumericBox), new PropertyMetadata(0));
        public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
            "Max", typeof(int), typeof(NumericBox), new PropertyMetadata(100));
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "Value", typeof(int), typeof(NumericBox),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        private bool _syncing;

        public NumericBox()
        {
            InitializeComponent();
            Txt.Text = "0";
        }

        public int Min
        {
            get { return (int)GetValue(MinProperty); }
            set { SetValue(MinProperty, value); }
        }

        public int Max
        {
            get { return (int)GetValue(MaxProperty); }
            set { SetValue(MaxProperty, value); }
        }

        public int Value
        {
            get { return (int)GetValue(ValueProperty); }
            set { SetValue(ValueProperty, ClampToRange(value)); }
        }

        /// <summary>Raised after the value settles (parsed from text or changed via spinner).</summary>
        public event EventHandler ValueChanged;

        private int ClampToRange(int v)
        {
            return Math.Max(Min, Math.Min(Max, v));
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = (NumericBox)d;
            box.SyncText();
            var handler = box.ValueChanged;
            if (handler != null) handler(box, EventArgs.Empty);
        }

        private void SyncText()
        {
            if (_syncing) return;
            _syncing = true;
            Txt.Text = Value.ToString();
            Txt.CaretIndex = Txt.Text.Length;
            _syncing = false;
        }

        private void Txt_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncing) return;
            int v;
            if (int.TryParse(Txt.Text.Trim(), out v))
            {
                int clamped = ClampToRange(v);
                if (clamped != Value)
                {
                    _syncing = true;
                    Value = clamped; // SyncText suppressed while typing
                    _syncing = false;
                }
            }
        }

        private void Txt_LostFocus(object sender, RoutedEventArgs e)
        {
            int v;
            if (!int.TryParse(Txt.Text.Trim(), out v)) v = Value;
            Value = ClampToRange(v);
        }

        private void Up_Click(object sender, RoutedEventArgs e)
        {
            Value++;
        }

        private void Down_Click(object sender, RoutedEventArgs e)
        {
            Value--;
        }
    }
}
