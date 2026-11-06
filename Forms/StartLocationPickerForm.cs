using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MHDBEditor.ArchiveFormat;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Picks a start location (RegionConnectionTargetPrototype) from the loaded game data
    // instead of typing a raw prototype id. Result is the target's DataRef, which is what
    // Player.StartTarget stores.
    public class StartLocationPickerForm : Form
    {
        private Font _boldFont;   // shared by every noted row; freed in Disposed

        public PrototypeResolver.StartLocation Result { get; private set; }
        public bool AlsoSetLastTown => _lastTownCheck.Checked && _lastTownCheck.Enabled;

        private readonly List<PrototypeResolver.StartLocation> _all;
        private readonly TextBox _search;
        private readonly CheckBox _townsOnly;
        private readonly CheckBox _entryPointsOnly;
        private readonly CheckBox _lastTownCheck;
        private readonly ListView _list;
        private readonly Button _okButton;
        private readonly ulong _current;

        public StartLocationPickerForm(PrototypeResolver resolver, long currentValue, bool canSetLastTown)
        {
            Disposed += (_, _) => _boldFont?.Dispose();
            _all = resolver.GetStartLocations();
            _current = unchecked((ulong)currentValue);

            Text = "Change Player Location";
            Width = 860;
            Height = 600;
            MinimumSize = new Size(600, 400);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            var (currentText, currentOk) = resolver.DescribeStartTarget(currentValue);
            Label currentLabel = new()
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(8, 6, 8, 0),
                ForeColor = currentOk ? SystemColors.ControlText : Color.DarkRed,
                Text = $"Currently: {currentText}\r\nThe player is sent here on next login. Use this for players stuck on the loading screen. Save only while they're logged out, or the server overwrites it.",
            };

            FlowLayoutPanel filters = new() { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 4, 6, 0), WrapContents = false };
            _search = new TextBox { Width = 260, PlaceholderText = "Search region or spot (e.g. avengers, genosha)" };
            _townsOnly = new CheckBox { Text = "Towns / hubs only", Checked = true, AutoSize = true, Margin = new Padding(12, 3, 3, 3) };
            _entryPointsOnly = new CheckBox { Text = "Main entry point only", Checked = true, AutoSize = true, Margin = new Padding(12, 3, 3, 3) };
            filters.Controls.AddRange(new Control[] { _search, _townsOnly, _entryPointsOnly });

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
            };
            _list.Columns.Add("Region", 260);
            _list.Columns.Add("Spot", 260);
            _list.Columns.Add("Type", 110);
            _list.Columns.Add("Note", 180);

            _lastTownCheck = new CheckBox
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                Padding = new Padding(8, 0, 8, 0),
                Checked = true,
                Enabled = false,
                Text = "Also set the player's last town to this region. The server rewrites the start location from the last town on every save, so this stops the old spot coming back.",
            };
            if (canSetLastTown == false)
                _lastTownCheck.Text = "Can't set last town: this player's saved data couldn't be read. Only the start location will change.";
            _lastTownCheck.Tag = canSetLastTown;

            FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
            _okButton = new Button { Text = "Use This Location", Width = 140, Enabled = false };
            Button cancelButton = new() { Text = "Cancel", Width = 90 };
            _okButton.Click += (_, _) => Accept();
            cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(_okButton);
            AcceptButton = _okButton;
            CancelButton = cancelButton;

            Controls.Add(_list);
            Controls.Add(filters);
            Controls.Add(currentLabel);
            Controls.Add(_lastTownCheck);
            Controls.Add(buttons);

            _search.TextChanged += (_, _) => Fill();
            _townsOnly.CheckedChanged += (_, _) => Fill();
            _entryPointsOnly.CheckedChanged += (_, _) => Fill();
            _list.SelectedIndexChanged += (_, _) => UpdateSelection();
            _list.DoubleClick += (_, _) => Accept();

            Fill();
        }

        private void Fill()
        {
            string[] words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<PrototypeResolver.StartLocation> shown = _all.Where(l =>
                // Server defaults are always listed so there's a known-safe pick.
                l.Note != null ||
                ((_townsOnly.Checked == false || l.IsTown) && (_entryPointsOnly.Checked == false || l.IsRegionStart)));
            if (words.Length > 0)
                shown = shown.Where(l => words.All(w => l.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || l.RegionPath.Contains(w, StringComparison.OrdinalIgnoreCase)));

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (PrototypeResolver.StartLocation location in shown)
            {
                ListViewItem item = new(new[] { location.RegionName, location.Name, location.Behavior ?? "?", location.Note ?? "" })
                {
                    Tag = location,
                    ToolTipText = $"{location.Path}\r\nRegion: {location.RegionPath}\r\nid: {unchecked((long)location.DataRef)}",
                };
                if (location.Note != null) item.Font = _boldFont ??= new Font(_list.Font, FontStyle.Bold);
                if (location.DataRef == _current)
                {
                    item.Text += "  (current)";
                    item.BackColor = Color.LightYellow;
                }
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            _list.ShowItemToolTips = true;
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            PrototypeResolver.StartLocation location = _list.SelectedItems.Count > 0 ? (PrototypeResolver.StartLocation)_list.SelectedItems[0].Tag : null;
            _okButton.Enabled = location != null;
            // Last town only makes sense for town regions - that's all the server ever stores there.
            _lastTownCheck.Enabled = location != null && location.IsTown && (bool)_lastTownCheck.Tag;
        }

        // Player archive with LastTownRegionForAccount set to the location's region, or null if
        // the archive can't be read. PlayerConnection.SaveToDBAccount() rewrites StartTarget from
        // this property on every save, so leaving it on a broken region brings the bad spot back.
        public static byte[] WithLastTown(PrototypeResolver resolver, byte[] archiveData, PrototypeResolver.StartLocation location)
        {
            ParsedEntityArchive parsed = TryParse(archiveData);
            if (parsed == null || resolver.TryResolveByName("LastTownRegionForAccount", out ulong propGuid) == false)
                return null;

            PropertyRecord record = parsed.Properties.FirstOrDefault(p => p.PrototypeGuid == propGuid && p.Params.Count == 0);
            if (record == null)
            {
                record = new PropertyRecord { Version = 0, PrototypeGuid = propGuid, DataType = PropertyDataType.Prototype };
                parsed.Properties.Add(record);
            }
            record.ValueGuid = location.RegionGuid;
            return parsed.Rebuild();
        }

        public static bool CanSetLastTown(PrototypeResolver resolver, byte[] archiveData) =>
            TryParse(archiveData) != null && resolver.TryResolveByName("LastTownRegionForAccount", out _);

        private static ParsedEntityArchive TryParse(byte[] archiveData)
        {
            if (archiveData == null || archiveData.Length == 0) return null;
            try { return EntityArchiveCodec.Parse(archiveData); }
            catch (Exception) { return null; }
        }

        private void Accept()
        {
            if (_list.SelectedItems.Count == 0) return;
            Result = (PrototypeResolver.StartLocation)_list.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
