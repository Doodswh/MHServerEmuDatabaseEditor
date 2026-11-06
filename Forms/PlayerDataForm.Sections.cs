using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MHDBEditor.ArchiveFormat;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Player Data tabs for the sections decoded in the second pass: achievements, map discovery,
    // gameplay options, and stash tab names/colors/order.
    public partial class PlayerDataForm
    {
        private AchievementCatalog _achievements;
        private TextBox _achievementSearch;
        private ComboBox _achievementFilter;
        private ComboBox _achievementCategory;
        private CheckBox _achievementShowAll;
        private ListView _achievementList;
        private Label _achievementCount;
        private ListView _mapList;
        private ListView _settingList;
        private ListView _chatFilterList;
        private ListView _vaporizeList;

        private IEnumerable<TabPage> BuildSectionTabs()
        {
            _achievements = AchievementCatalog.TryLoadNextTo(_resolver.LoadedFrom);
            yield return BuildAchievementsTab();
            yield return BuildMapsTab();
            yield return BuildOptionsTab();
        }

        // ---------------- Achievements ----------------

        private TabPage BuildAchievementsTab()
        {
            TabPage page = new("Achievements");
            FlowLayoutPanel filters = new() { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 4, 6, 0), WrapContents = false };
            _achievementSearch = new TextBox { Width = 200, PlaceholderText = "Search name or category" };
            _achievementFilter = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            _achievementFilter.Items.AddRange(new object[] { "All", "Completed", "In progress" });
            _achievementFilter.SelectedIndex = 0;
            _achievementCategory = new ComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            _achievementCategory.Items.Add("All categories");
            if (_achievements != null)
                _achievementCategory.Items.AddRange(_achievements.All.Select(i => i.Category).Where(c => c != null).Distinct().OrderBy(c => c).ToArray());
            _achievementCategory.SelectedIndex = 0;
            _achievementShowAll = new CheckBox { Text = "Include ones not started", AutoSize = true, Margin = new Padding(10, 6, 3, 3), Enabled = _achievements != null };
            _achievementCount = new Label { AutoSize = true, Margin = new Padding(10, 7, 3, 3) };
            filters.Controls.AddRange(new Control[] { _achievementSearch, _achievementFilter, _achievementCategory, _achievementShowAll, _achievementCount });

            _achievementList = NewList(true, ("Achievement", 300), ("Category", 200), ("Progress", 100), ("Completed", 150), ("Id", 60));
            Label help = HelpLabel(_achievements == null
                ? "Achievement names come from Data/Game/Achievements next to Calligraphy.sip, which wasn't found - showing ids only."
                : "Complete sets the count to the threshold and stamps today's date; Reset removes the progress. Editing here doesn't run the server's reward event, and parent achievements recount next time they update.");
            FlowLayoutPanel buttons = ButtonRow(
                ("Tick All Shown", () => SetAllChecked(_achievementList, true)),
                ("Untick All", () => SetAllChecked(_achievementList, false)),
                ("Complete Ticked", () => EditTickedAchievements(true)),
                ("Reset Ticked", () => EditTickedAchievements(false)));

            page.Controls.Add(_achievementList);
            page.Controls.Add(help);
            page.Controls.Add(filters);
            page.Controls.Add(buttons);

            _achievementSearch.TextChanged += (_, _) => FillAchievements();
            _achievementFilter.SelectedIndexChanged += (_, _) => FillAchievements();
            _achievementCategory.SelectedIndexChanged += (_, _) => FillAchievements();
            _achievementShowAll.CheckedChanged += (_, _) => FillAchievements();
            FillAchievements();
            return page;
        }

        private IEnumerable<uint> AllAchievementIds()
        {
            HashSet<uint> ids = _tail.Achievements.Select(a => a.Id).ToHashSet();
            if (_achievements != null)
                ids.UnionWith(_achievements.All.Where(i => i.Enabled).Select(i => i.Id));
            return ids;
        }

        private void FillAchievements()
        {
            Dictionary<uint, PlayerTail.Achievement> progress = _tail.Achievements.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
            IEnumerable<uint> ids = _achievementShowAll.Checked ? AllAchievementIds() : progress.Keys;
            string[] words = _achievementSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string category = _achievementCategory.SelectedIndex > 0 ? (string)_achievementCategory.SelectedItem : null;

            List<ListViewItem> items = new();
            foreach (uint id in ids.OrderBy(i => i))
            {
                progress.TryGetValue(id, out PlayerTail.Achievement entry);
                bool completed = entry?.CompletedTime > 0;
                if (_achievementFilter.SelectedIndex == 1 && completed == false) continue;
                if (_achievementFilter.SelectedIndex == 2 && (entry == null || completed)) continue;

                AchievementCatalog.Info info = null;
                _achievements?.TryGet(id, out info);
                if (category != null && info?.Category != category) continue;
                string name = _achievements?.NameOf(id) ?? $"Achievement {id}";
                string where = info == null ? "" : string.IsNullOrEmpty(info.SubCategory) ? info.Category : $"{info.Category} / {info.SubCategory}";
                if (words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase) == false && (where ?? "").Contains(w, StringComparison.OrdinalIgnoreCase) == false)) continue;

                string count = entry == null ? "-" : info?.Threshold > 0 ? $"{entry.Count} / {info.Threshold}" : entry.Count.ToString();
                items.Add(new ListViewItem(new[] { name, where, count, PlayerTail.FromArchiveTime(entry?.CompletedTime ?? 0)?.ToString("yyyy-MM-dd HH:mm") ?? "", id.ToString() })
                {
                    Tag = id,
                    ForeColor = entry == null ? SystemColors.GrayText : completed ? Color.DarkGreen : SystemColors.WindowText,
                });
            }

            _achievementList.BeginUpdate();
            _achievementList.Items.Clear();
            _achievementList.Items.AddRange(items.ToArray());
            _achievementList.EndUpdate();
            _achievementCount.Text = $"{items.Count:N0} shown, {_tail.Achievements.Count(a => a.CompletedTime > 0):N0} completed";
        }

        private void EditTickedAchievements(bool complete)
        {
            List<uint> ids = _achievementList.CheckedItems.Cast<ListViewItem>().Select(i => (uint)i.Tag).ToList();
            if (ids.Count == 0) return;
            long now = PlayerTail.ToArchiveTime(DateTime.Now);
            int changed = 0;
            foreach (uint id in ids)
            {
                PlayerTail.Achievement entry = _tail.Achievements.FirstOrDefault(a => a.Id == id);
                if (complete)
                {
                    uint threshold = _achievements != null && _achievements.TryGet(id, out var info) ? Math.Max(1, info.Threshold) : 1;
                    if (entry == null) _tail.Achievements.Add(entry = new PlayerTail.Achievement { Id = id });
                    else if (entry.CompletedTime > 0 && entry.Count >= threshold) continue;
                    entry.Count = Math.Max(entry.Count, threshold);
                    if (entry.CompletedTime <= 0) entry.CompletedTime = now;
                }
                else
                {
                    if (_tail.Achievements.RemoveAll(a => a.Id == id) == 0) continue;
                }
                changed++;
            }
            if (changed > 0)
                _changes.Add($"{(complete ? "Complete" : "Reset")} {changed} achievement(s)");
            FillAchievements();
            UpdateChanges();
        }

        // ---------------- Map discovery ----------------

        private TabPage BuildMapsTab()
        {
            TabPage page = new("Map Discovery");
            _mapList = NewList(false, ("Region", 300), ("Last visit", 150), ("Discovered markers", 130), ("Map", 140));
            page.Controls.Add(_mapList);
            page.Controls.Add(HelpLabel("The minimap fog and discovered map markers saved per region. Reveal sets the region's map to fully revealed (LowResMap reveal-all); Forget clears it so it's explored again."));
            page.Controls.Add(ButtonRow(("Reveal Selected", () => EditMaps(true, false)), ("Reveal All", () => EditMaps(true, true)), ("Forget Selected", () => EditMaps(false, false))));
            FillMaps();
            return page;
        }

        private void FillMaps()
        {
            _mapList.BeginUpdate();
            _mapList.Items.Clear();
            foreach (PlayerTail.MapDiscovery map in _tail.Maps)
            {
                string coverage = map.RevealAll ? "Fully revealed" : $"{CountBits(map.MapBits):N0} / {map.MapBitCount:N0} cells";
                _mapList.Items.Add(new ListViewItem(new[]
                {
                    _resolver.Format(map.RegionGuid), PlayerTail.FromGameTime(map.AccessTime)?.ToString("yyyy-MM-dd HH:mm") ?? "", map.DiscoveredEntities.Count.ToString("N0"), coverage,
                }) { Tag = map });
            }
            _mapList.EndUpdate();
        }

        private static int CountBits(byte[] bits) => bits.Sum(b => System.Numerics.BitOperations.PopCount(b));

        private void EditMaps(bool reveal, bool all)
        {
            List<PlayerTail.MapDiscovery> maps = (all ? _mapList.Items.Cast<ListViewItem>() : _mapList.SelectedItems.Cast<ListViewItem>()).Select(i => (PlayerTail.MapDiscovery)i.Tag).ToList();
            int changed = 0;
            foreach (PlayerTail.MapDiscovery map in maps)
            {
                if (reveal)
                {
                    if (map.RevealAll) continue;
                    map.RevealAll = true;
                    map.MapBitCount = 0;
                    map.MapBits = Array.Empty<byte>();
                }
                else _tail.Maps.Remove(map);
                changed++;
            }
            if (changed > 0) _changes.Add($"{(reveal ? "Reveal" : "Forget")} {changed} map(s)");
            FillMaps();
            UpdateChanges();
        }

        // ---------------- Gameplay options ----------------

        private TabPage BuildOptionsTab()
        {
            TabPage page = new("Options");
            _settingList = NewList(false, ("Setting", 260), ("Value", 80));
            _chatFilterList = NewList(true, ("Chat channel (ticked = shown in main tab)", 300));
            _vaporizeList = NewList(false, ("Slot", 120), ("Vaporize up to rarity", 200));
            _settingList.DoubleClick += (_, _) => EditSetting();
            _vaporizeList.DoubleClick += (_, _) => EditVaporize();
            _chatFilterList.ItemCheck += ChatFilterCheck;

            TableLayoutPanel grid = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(new Label { Text = "Gameplay settings (double-click to edit)", AutoSize = true }, 0, 0);
            grid.Controls.Add(new Label { Text = "Chat filters", AutoSize = true }, 1, 0);
            grid.Controls.Add(new Label { Text = "Pet vacuum thresholds (double-click)", AutoSize = true }, 2, 0);
            grid.Controls.Add(_settingList, 0, 1);
            grid.Controls.Add(_chatFilterList, 1, 1);
            grid.Controls.Add(_vaporizeList, 2, 1);
            page.Controls.Add(grid);

            FillOptions();
            return page;
        }

        private void FillOptions()
        {
            _filling = true;
            _settingList.Items.Clear();
            for (int i = 0; i < _tail.OptionSettings.Count; i++)
                _settingList.Items.Add(new ListViewItem(new[] { i < PlayerTail.OptionSettingNames.Length ? PlayerTail.OptionSettingNames[i] : $"Setting {i}", _tail.OptionSettings[i].ToString() }) { Tag = i });

            _chatFilterList.Items.Clear();
            for (int i = 0; i < _tail.ChatFilters.Count; i++)
                _chatFilterList.Items.Add(new ListViewItem(_resolver.Format(_tail.ChatFilters[i].ChannelGuid)) { Tag = i, Checked = _tail.ChatFilters[i].Enabled });

            _vaporizeList.Items.Clear();
            for (int i = 0; i < _tail.VaporizeThresholds.Count; i++)
            {
                var (slot, rarity) = _tail.VaporizeThresholds[i];
                _vaporizeList.Items.Add(new ListViewItem(new[] { slot < (ulong)PlayerTail.EquipmentSlotNames.Length ? PlayerTail.EquipmentSlotNames[slot] : slot.ToString(), rarity == 0 ? "(off)" : _resolver.Format(rarity) }) { Tag = i });
            }
            _filling = false;
        }

        private void ChatFilterCheck(object sender, ItemCheckEventArgs e)
        {
            if (_filling) return;
            int index = (int)_chatFilterList.Items[e.Index].Tag;
            bool enabled = e.NewValue == CheckState.Checked;
            if (_tail.ChatFilters[index].Enabled == enabled) return;   // ListView re-raises for pre-ticked rows on first show
            _tail.ChatFilters[index] = (_tail.ChatFilters[index].ChannelGuid, enabled);
            _changes.Add($"Chat filter {_chatFilterList.Items[e.Index].Text} -> {(enabled ? "on" : "off")}");
            UpdateChanges();
        }

        private void EditSetting()
        {
            if (_settingList.SelectedItems.Count == 0) return;
            int index = (int)_settingList.SelectedItems[0].Tag;
            string name = _settingList.SelectedItems[0].Text;
            string input = PromptForm.ShowSingle(this, "Edit setting", $"{name} (booleans are 0/1, levels 0-100):", _tail.OptionSettings[index].ToString());
            if (input == null || long.TryParse(input.Trim(), out long value) == false || value == _tail.OptionSettings[index]) return;
            _tail.OptionSettings[index] = value;
            _changes.Add($"{name} -> {value}");
            FillOptions();
            UpdateChanges();
        }

        private void EditVaporize()
        {
            if (_vaporizeList.SelectedItems.Count == 0) return;
            int index = (int)_vaporizeList.SelectedItems[0].Tag;
            List<(ulong Guid, string Name)> rarities = new() { (0, "(off)") };
            rarities.AddRange(_resolver.Find().Where(e => e.IsAbstract == false && _resolver.GetRuntimeClass(e.Guid) == "RarityPrototype").Select(e => (e.Guid, e.DisplayName)).OrderBy(r => r.DisplayName));

            using Form pick = new() { Text = "Vaporize threshold", Width = 320, Height = 140, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
            ComboBox combo = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.AddRange(rarities.Select(r => r.Name).ToArray());
            combo.SelectedIndex = Math.Max(0, rarities.FindIndex(r => r.Guid == _tail.VaporizeThresholds[index].RarityGuid));
            Button ok = new() { Text = "OK", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
            pick.Controls.Add(combo);
            pick.Controls.Add(ok);
            pick.AcceptButton = ok;
            if (pick.ShowDialog(this) != DialogResult.OK) return;

            ulong guid = rarities[combo.SelectedIndex].Guid;
            if (guid == _tail.VaporizeThresholds[index].RarityGuid) return;
            _tail.VaporizeThresholds[index] = (_tail.VaporizeThresholds[index].Slot, guid);
            _changes.Add($"Vacuum {_vaporizeList.SelectedItems[0].Text} -> {rarities[combo.SelectedIndex].Name}");
            FillOptions();
            UpdateChanges();
        }

        // ---------------- Stash tab names / colors / order ----------------

        // The client saves names URL-escaped ("Moon%20Knight").
        private static string StashName(string raw)
        {
            try { return Uri.UnescapeDataString(raw ?? ""); } catch (Exception) { return raw; }
        }

        private PlayerTail.StashTab StashOptionsFor(ulong inventoryGuid) => _tail.StashTabs.FirstOrDefault(s => s.InventoryGuid == inventoryGuid);

        private string StashOptionsText(ulong inventoryGuid, out string color, out string order)
        {
            PlayerTail.StashTab options = StashOptionsFor(inventoryGuid);
            color = options == null ? "" : options.Color >= 0 && options.Color < PlayerTail.StashTabColorNames.Length ? PlayerTail.StashTabColorNames[options.Color] : options.Color.ToString();
            order = options?.SortOrder.ToString() ?? "";
            return options == null ? "" : StashName(options.DisplayName);
        }

        private void EditStashOptions()
        {
            if (_stashList.SelectedItems.Count == 0) return;
            PrototypeResolver.StashTab tab = (PrototypeResolver.StashTab)_stashList.SelectedItems[0].Tag;
            PlayerTail.StashTab options = StashOptionsFor(tab.Guid);

            using Form edit = new() { Text = $"Stash tab - {tab.Name}", Width = 360, Height = 210, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
            TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
            TextBox name = new() { Dock = DockStyle.Fill, Text = StashName(options?.DisplayName) };
            ComboBox color = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            color.Items.AddRange(PlayerTail.StashTabColorNames);
            color.SelectedIndex = Math.Clamp(options?.Color ?? 0, 0, PlayerTail.StashTabColorNames.Length - 1);
            NumericUpDown order = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 999, Value = Math.Clamp(options?.SortOrder ?? 0, 0, 999) };
            layout.Controls.Add(new Label { Text = "Name", AutoSize = true }, 0, 0); layout.Controls.Add(name, 1, 0);
            layout.Controls.Add(new Label { Text = "Color", AutoSize = true }, 0, 1); layout.Controls.Add(color, 1, 1);
            layout.Controls.Add(new Label { Text = "Sort order", AutoSize = true }, 0, 2); layout.Controls.Add(order, 1, 2);
            Button ok = new() { Text = "OK", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
            edit.Controls.Add(layout);
            edit.Controls.Add(ok);
            edit.AcceptButton = ok;
            if (edit.ShowDialog(this) != DialogResult.OK) return;

            string newName = Uri.EscapeDataString(name.Text.Trim());
            if (options != null && options.DisplayName == newName && options.Color == color.SelectedIndex && options.SortOrder == (int)order.Value) return;
            if (options == null)
                _tail.StashTabs.Add(options = new PlayerTail.StashTab { InventoryGuid = tab.Guid });
            options.DisplayName = newName;
            options.Color = color.SelectedIndex;
            options.SortOrder = (int)order.Value;
            _changes.Add($"Stash tab {tab.Name}: '{name.Text.Trim()}', {PlayerTail.StashTabColorNames[color.SelectedIndex]}, order {order.Value}");
            FillStash();
            UpdateChanges();
        }
    }
}
