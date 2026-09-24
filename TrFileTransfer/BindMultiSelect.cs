using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>
    /// Bind-address picker with checkboxes: two wildcard entries (all-IPv4, all-IPv6)
    /// plus every interface address, all selectable at once — each selected address
    /// gets its own listener on the tab's port. A family's wildcard absorbs that
    /// family's specific selections (they would be shadowed anyway).
    /// </summary>
    public sealed class BindMultiSelect : UserControl
    {
        private sealed class Entry
        {
            public IPAddress Address;
            public string Label;
            public CheckBox Box;
        }

        private readonly Button _summary;
        private readonly Popup _popup;
        private readonly StackPanel _panel;
        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>Fires whenever the checked set changes (label refresh, config save).</summary>
        public event Action SelectionChanged;

        /// <summary>Close the dropdown when the control gets disabled (tab running).</summary>
        public BindMultiSelect()
        {
            _summary = new Button
            {
                Style = (Style)TryFindResource("BtnSecondary"),
                MinWidth = 60,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            _summary.Click += (s, e) => { _popup.IsOpen = !_popup.IsOpen; };

            var listScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 220
            };
            _panel = new StackPanel();
            listScroll.Content = _panel;

            _popup = new Popup
            {
                StaysOpen = false,
                PlacementTarget = _summary,
                Placement = PlacementMode.Bottom,
                AllowsTransparency = true,
                Child = new Border
                {
                    Background = (Brush)TryFindResource("Brush.ItemCard"),
                    BorderBrush = (Brush)TryFindResource("Brush.CardBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 6, 8, 6),
                    Child = listScroll
                }
            };

            var grid = new Grid();
            grid.Children.Add(_summary);
            Content = grid;
            Focusable = false;
            IsEnabledChanged += (s, e) =>
            {
                if (!(bool)e.NewValue && _popup != null) _popup.IsOpen = false;
            };
            // Window hide/minimize does not change IsEnabled and the popup is a separate
            // HWND — it would keep floating over the desktop. Follow visibility instead.
            _summary.IsVisibleChanged += (s, e) =>
            {
                if (!(bool)e.NewValue && _popup != null) _popup.IsOpen = false;
            };
            UpdateSummary();
        }

        /// <summary>Replaces the item list (addresses + wildcard entries); selection is
        /// by IPAddress value, so a refresh keeps what is still present.</summary>
        public void SetItems(IEnumerable<KeyValuePair<IPAddress, string>> items)
        {
            var keep = new HashSet<IPAddress>(GetSelectedAddresses());
            _panel.Children.Clear();
            _entries.Clear();
            foreach (var kv in items)
            {
                IPAddress addr = kv.Key;
                bool wildcard = Utils.IsWildcardAddress(addr);
                var box = new CheckBox
                {
                    Style = (Style)TryFindResource("ChkBox"),
                    Content = kv.Value,
                    Tag = wildcard ? "wildcard" : null,
                    Margin = new Thickness(0, 0, 0, 4),
                    MinWidth = 170,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                if (keep.Contains(addr)) box.IsChecked = true;
                box.Checked += (s, e) => OnToggled();
                box.Unchecked += (s, e) => OnToggled();
                _entries.Add(new Entry { Address = addr, Label = kv.Value, Box = box });
                _panel.Children.Add(box);
            }
            UpdateSummary();
        }

        private void OnToggled()
        {
            UpdateSummary();
            var h = SelectionChanged;
            if (h != null) h();
        }

        /// <summary>Selected addresses, normalized: a family wildcard absorbs that
        /// family's specifics; duplicates collapse.</summary>
        public List<IPAddress> GetSelectedAddresses()
        {
            bool anyV4 = false, anyV6 = false;
            var selected = new List<IPAddress>();
            foreach (var e in _entries)
            {
                if (e.Box.IsChecked != true) continue;
                if (Utils.IsWildcardAddress(e.Address))
                {
                    if (e.Address.AddressFamily == AddressFamily.InterNetwork) anyV4 = true;
                    else anyV6 = true;
                }
                if (!selected.Contains(e.Address)) selected.Add(e.Address);
            }
            if (!anyV4 && !anyV6) return selected;
            var result = new List<IPAddress>();
            foreach (IPAddress a in selected)
            {
                bool v4 = a.AddressFamily == AddressFamily.InterNetwork;
                bool isWildcard = Utils.IsWildcardAddress(a);
                bool absorbed = !isWildcard && (v4 ? anyV4 : anyV6);
                if (!absorbed) result.Add(a);
            }
            return result;
        }

        public void SetSelected(IEnumerable<IPAddress> addresses)
        {
            var want = new HashSet<IPAddress>(addresses ?? Enumerable.Empty<IPAddress>());
            foreach (var e in _entries)
                e.Box.IsChecked = want.Contains(e.Address);
            UpdateSummary();
        }

        public bool IsEmptySelection
        {
            get { return GetSelectedAddresses().Count == 0; }
        }

        /// <summary>Compact button text: "All IPv4 + All IPv6", "192.168.1.5 +2", …</summary>
        private void UpdateSummary()
        {
            var sel = GetSelectedAddresses();
            if (sel.Count == 0)
            {
                _summary.Content = L.BindPickNone;
                return;
            }
            var sb = new StringBuilder();
            int extra = 0;
            foreach (IPAddress a in sel)
            {
                bool wildcard = Utils.IsWildcardAddress(a);
                string label = wildcard
                    ? (a.AddressFamily == AddressFamily.InterNetwork ? L.BindAllV4 : L.BindAllV6)
                    : a.ToString();
                bool longLabel = wildcard;
                if (sb.Length == 0)
                {
                    if (longLabel)
                    {
                        // shorten to the family name for the first slot
                        sb.Append(a.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6");
                        sb.Append(" *");
                    }
                    else sb.Append(label);
                }
                else
                {
                    if (longLabel) sb.Append(a.AddressFamily == AddressFamily.InterNetwork ? " +IPv4*" : " +IPv6*");
                    else extra++;
                }
            }
            if (extra > 0) sb.Append(" +").Append(extra);
            _summary.Content = sb.ToString();
            _summary.ToolTip = string.Join("; ", sel.Select(a => a.ToString()).ToArray());
        }
    }
}
