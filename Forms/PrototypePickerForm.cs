using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Searchable "pick a prototype by name" dialog. Scoped to a path prefix (e.g. only affixes
    // under Entity/Items/Affixes/) so the list is the handful of things that make sense for the
    // field being edited, with a checkbox to search everything when the scope is too narrow.
    public class PrototypePickerForm : Form
    {
        private const int MaxShown = 2000;

        private readonly PrototypeResolver _resolver;
        private readonly string _pathPrefix;
        private readonly TextBox _searchBox;
        private readonly CheckBox _showAllBox;
        private readonly ListBox _list;
        private readonly Label _pathLabel;
        private List<PrototypeResolver.Entry> _scoped;

        public ulong SelectedGuid { get; private set; }

        public PrototypePickerForm(PrototypeResolver resolver, string title, string pathPrefix, ulong currentGuid = 0)
        {
            _resolver = resolver;
            _pathPrefix = pathPrefix;

            Text = title;
            Width = 720;
            Height = 600;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            _searchBox = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Type to search by name or folder (e.g. \"cosmic health\")" };
            _showAllBox = new CheckBox { Dock = DockStyle.Top, Height = 26, Text = pathPrefix != null ? $"Search all prototypes (not just {pathPrefix})" : "Search all prototypes", Checked = pathPrefix == null, Enabled = pathPrefix != null };
            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _pathLabel = new Label { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4), AutoEllipsis = true };

            FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(6) };
            Button okButton = new() { Text = "Pick", Width = 90 };
            Button noneButton = new() { Text = "Set to (none)", Width = 110 };
            Button cancelButton = new() { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
            okButton.Click += (_, _) => Accept();
            noneButton.Click += (_, _) => { SelectedGuid = 0; DialogResult = DialogResult.OK; Close(); };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(noneButton);

            Controls.Add(_list);
            Controls.Add(_showAllBox);
            Controls.Add(_searchBox);
            Controls.Add(_pathLabel);
            Controls.Add(buttons);
            CancelButton = cancelButton;

            _searchBox.TextChanged += (_, _) => Refilter();
            _searchBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Down && _list.Items.Count > 0) { _list.Focus(); _list.SelectedIndex = 0; e.Handled = true; }
                if (e.KeyCode == Keys.Enter) { Accept(); e.SuppressKeyPress = true; }
            };
            _showAllBox.CheckedChanged += (_, _) => { _scoped = null; Refilter(); };
            _list.SelectedIndexChanged += (_, _) =>
                _pathLabel.Text = _list.SelectedItem is PrototypeResolver.Entry entry ? $"{entry.Path}   (0x{entry.Guid:X})" : string.Empty;
            _list.DoubleClick += (_, _) => Accept();
            _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Accept(); e.SuppressKeyPress = true; } };

            Refilter();

            if (currentGuid != 0 && resolver.TryGetEntry(currentGuid, out PrototypeResolver.Entry current))
            {
                int index = _list.Items.IndexOf(current);
                if (index >= 0) _list.SelectedIndex = index;
            }
        }

        private void Refilter()
        {
            _scoped ??= _resolver.Find(_showAllBox.Checked ? null : _pathPrefix, includeAbstract: false).ToList();

            // Every word must appear somewhere in the name or path, so "cosmic health" finds
            // Entity/Items/Affixes/.../Cosmic/HealthMaxCosmic.
            string[] words = _searchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<PrototypeResolver.Entry> matches = _scoped.Where(e =>
                words.All(w => e.Path.Contains(w, StringComparison.OrdinalIgnoreCase)));

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (PrototypeResolver.Entry entry in matches.Take(MaxShown))
                _list.Items.Add(entry);
            _list.EndUpdate();

            if (_list.Items.Count == MaxShown)
                _pathLabel.Text = $"Showing the first {MaxShown} matches - type more to narrow it down.";
        }

        private void Accept()
        {
            PrototypeResolver.Entry entry = _list.SelectedItem as PrototypeResolver.Entry
                ?? (_list.Items.Count == 1 ? _list.Items[0] as PrototypeResolver.Entry : null);
            if (entry == null) return;

            SelectedGuid = entry.Guid;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
