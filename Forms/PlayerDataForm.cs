using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MHDBEditor.ArchiveFormat;
using MHDBEditor.Data;
using MHDBEditor.Models;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Views and edits the parts of a player's ArchiveData that come after the property block
    // (PlayerTailCodec): missions, stash tab unlocks, friends/ignore, vendor purchase limits and
    // the admin difficulty override. Changes are held until Save, which re-encodes the blob.
    public partial class PlayerDataForm : Form
    {
        private Font _strikeoutFont;   // shared by every "reset" mission row; freed in Disposed

        private static readonly string[] MissionStates = { "Invalid", "Inactive", "Available", "Active", "Completed", "Failed" };

        private readonly AccountRepository _accountRepository;
        private readonly EntityRepository _entityRepository;
        private readonly PrototypeResolver _resolver;
        private readonly DBAccount _account;
        private readonly ParsedEntityArchive _parsed;
        private readonly PlayerTail _tail;
        private readonly List<string> _changes = new();

        private readonly Label _changesLabel;
        private readonly Button _saveButton;

        // Missions tab
        private readonly TextBox _missionSearch;
        private readonly ComboBox _missionState;
        private readonly ComboBox _missionGroup;
        private readonly ListView _missionList;
        private readonly Label _missionCount;
        private readonly HashSet<PlayerTail.Mission> _missionsToReset = new();

        // Other tabs
        private readonly ListView _stashList;
        private readonly ListView _friendList;
        private readonly ListView _vendorList;
        private readonly ComboBox _difficultyCombo;
        private readonly Label _overviewLabel;
        private bool _filling;

        private PlayerDataForm(AccountRepository accountRepository, EntityRepository entityRepository, PrototypeResolver resolver,
            DBAccount account, ParsedEntityArchive parsed, PlayerTail tail)
        {
            Disposed += (_, _) => _strikeoutFont?.Dispose();
            _accountRepository = accountRepository;
            _entityRepository = entityRepository;
            _resolver = resolver;
            _account = account;
            _parsed = parsed;
            _tail = tail;

            Text = $"Player Data - {account.PlayerName}";
            Width = 1000;
            Height = 680;
            MinimumSize = new Size(760, 480);
            StartPosition = FormStartPosition.CenterParent;

            TabControl tabs = new() { Dock = DockStyle.Fill };

            // --- Overview ---
            TabPage overview = new("Overview");
            _overviewLabel = new Label { Dock = DockStyle.Top, Height = 215, Padding = new Padding(10), Font = new Font("Consolas", 9.5f) };
            FlowLayoutPanel difficultyRow = new() { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 4, 8, 0), WrapContents = false };
            difficultyRow.Controls.Add(new Label { Text = "Admin difficulty override:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            _difficultyCombo = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
            difficultyRow.Controls.Add(_difficultyCombo);
            Label difficultyHelp = new()
            {
                Dock = DockStyle.Top,
                Height = 60,
                Padding = new Padding(10, 4, 10, 0),
                ForeColor = SystemColors.GrayText,
                Text = "Forces every region this player enters to this difficulty, and the in-game difficulty picker is ignored (Player.GetDifficultyTierPreference / OnChangeDifficulty). " +
                       "It's saved with the player, so it survives logouts - set to (none) to give the player their own choice back.",
            };
            overview.Controls.Add(difficultyHelp);
            overview.Controls.Add(difficultyRow);
            overview.Controls.Add(_overviewLabel);

            // --- Missions ---
            TabPage missions = new("Missions");
            FlowLayoutPanel missionFilters = new() { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 4, 6, 0), WrapContents = false };
            _missionSearch = new TextBox { Width = 220, PlaceholderText = "Search mission name" };
            _missionState = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
            _missionState.Items.Add("All states");
            _missionState.Items.AddRange(MissionStates);
            _missionState.SelectedIndex = 0;
            _missionGroup = new ComboBox { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
            _missionGroup.Items.Add("All groups");
            _missionGroup.Items.AddRange(tail.Missions.Select(m => resolver.MissionGroup(m.Guid)).Distinct().OrderBy(g => g).ToArray());
            _missionGroup.SelectedIndex = 0;
            _missionCount = new Label { AutoSize = true, Margin = new Padding(10, 7, 3, 3) };
            missionFilters.Controls.AddRange(new Control[] { _missionSearch, _missionState, _missionGroup, _missionCount });

            _missionList = NewList(true, ("Mission", 330), ("Group", 150), ("State", 90), ("Version", 60), ("Reset?", 70));
            Label missionHelp = HelpLabel("Tick missions and press Reset Ticked. The mission is removed from the save; at the next login the server recreates it fresh " +
                "(MissionManager.InitializeMissions), so it can be done again. Missions that depended on it keep their own state.");
            FlowLayoutPanel missionButtons = ButtonRow(
                ("Tick All Shown", () => SetAllChecked(_missionList, true)),
                ("Untick All", () => SetAllChecked(_missionList, false)),
                ("Reset Ticked", ResetTickedMissions),
                ("Undo Resets", () => { _missionsToReset.Clear(); _changes.RemoveAll(c => c.StartsWith("Reset ")); FillMissions(); UpdateChanges(); }));
            missions.Controls.Add(_missionList);
            missions.Controls.Add(missionHelp);
            missions.Controls.Add(missionFilters);
            missions.Controls.Add(missionButtons);

            // --- Stash tabs ---
            TabPage stash = new("Stash Tabs");
            _stashList = NewList(true, ("Stash tab", 240), ("For hero", 170), ("Status", 120), ("Name", 160), ("Color", 70), ("Order", 50));
            _stashList.DoubleClick += (_, _) => EditStashOptions();
            stash.Controls.Add(_stashList);
            stash.Controls.Add(HelpLabel("Ticked = unlocked. The server adds unlocked tabs at login (Player.OnUnpackComplete). " +
                "Locking a tab that still has items is refused - those items would be skipped and then deleted on the next save. Double-click a tab to set its name, color and order."));

            // --- Friends ---
            TabPage friends = new("Friends / Ignore");
            _friendList = NewList(false, ("Name", 220), ("Player id", 170), ("Lists", 200));
            friends.Controls.Add(_friendList);
            friends.Controls.Add(ButtonRow(("Remove Selected", RemoveSelectedFriends)));

            // --- Vendors ---
            TabPage vendors = new("Vendor Purchases");
            _vendorList = NewList(false, ("Vendor inventory", 420), ("Size", 80));
            vendors.Controls.Add(_vendorList);
            vendors.Controls.Add(HelpLabel("What the player has bought from vendors with per-player purchase limits. Removing an entry resets that vendor's limits for this player."));
            vendors.Controls.Add(ButtonRow(("Reset Selected", () => ResetVendors(false)), ("Reset All", () => ResetVendors(true))));

            tabs.TabPages.AddRange(new[] { overview, missions, stash, friends, vendors });
            foreach (TabPage page in BuildSectionTabs())
                tabs.TabPages.Insert(tabs.TabPages.Count - 2, page);

            // --- Bottom ---
            FlowLayoutPanel bottom = new() { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            _saveButton = new Button { Text = "Save", Width = 90, Enabled = false };
            Button cancel = new() { Text = "Close", Width = 90 };
            _saveButton.Click += (_, _) => Save();
            cancel.Click += (_, _) => Close();
            _changesLabel = new Label { AutoSize = true, Margin = new Padding(3, 8, 12, 3) };
            bottom.Controls.AddRange(new Control[] { cancel, _saveButton, _changesLabel });

            Controls.Add(tabs);
            Controls.Add(bottom);

            _missionSearch.TextChanged += (_, _) => FillMissions();
            _missionState.SelectedIndexChanged += (_, _) => FillMissions();
            _missionGroup.SelectedIndexChanged += (_, _) => FillMissions();
            _stashList.ItemCheck += StashItemCheck;
            _difficultyCombo.SelectedIndexChanged += (_, _) => DifficultyChanged();
            FormClosing += (_, e) =>
            {
                if (_changes.Count > 0 && DialogResult != DialogResult.OK &&
                    MessageBox.Show(this, "Discard unsaved changes?", "Player Data", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    e.Cancel = true;
            };

            FillOverview();
            FillDifficulty();
            FillMissions();
            FillStash();
            FillFriends();
            FillVendors();
            UpdateChanges();
        }

        // Loads and decodes the player; shows why if it can't. Returns null when there's nothing to edit.
        public static PlayerDataForm TryCreate(IWin32Window owner, AccountRepository accounts, EntityRepository entities, PrototypeResolver resolver, DBAccount account)
        {
            DBPlayer player = accounts.GetPlayer(account.Id);
            if (player?.ArchiveData == null || player.ArchiveData.Length == 0)
            {
                MessageBox.Show(owner, "This player has no saved data yet (never finished a first save).", "Nothing to edit", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            ParsedEntityArchive parsed;
            try { parsed = EntityArchiveCodec.Parse(player.ArchiveData); }
            catch (Exception ex)
            {
                MessageBox.Show(owner, ex.Message, "Can't read player data", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }

            PlayerTail tail = PlayerTailCodec.TryDecode(parsed.TailBytes, parsed.Version, out string error);
            if (tail == null)
            {
                // Stock MHServerEmu built for game 1.48 lays out gameplay options differently (settings as a bit array, first).
                if (parsed.GameBuildNumber is 380454 or 391562)
                    error += "\r\n\r\nThis save was written by a game 1.48 server, whose player data layout the editor doesn't support (1.52 and 1.53 are supported).";
                MessageBox.Show(owner, $"{error}\r\n\r\nNothing will be changed for this player.", "Can't decode player data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            return new PlayerDataForm(accounts, entities, resolver, account, parsed, tail);
        }

        // ---------------- Overview / difficulty ----------------

        private void FillOverview()
        {
            int Count(int state) => _tail.Missions.Count(m => m.HeaderState == state);
            _overviewLabel.Text =
                $"Archive version     {_tail.ArchiveVersion} (game build {_parsed.GameBuildNumber}), {(_tail.Layout == ServerLayout.Coa ? "COA build" : "stock MHServerEmu")} layout\r\n" +
                $"Logins              {_tail.LoginCount:N0}\r\n" +
                $"Missions            {_tail.Missions.Count:N0}  ({Count(4):N0} completed, {Count(3):N0} active, {Count(2):N0} available, {Count(1):N0} inactive, {Count(5):N0} failed)\r\n" +
                $"Unlocked stash tabs {_tail.UnlockedInventories.Count:N0}\r\n" +
                $"Friends / ignore    {_tail.Members.Count:N0}\r\n" +
                $"Vendor purchases    {_tail.VendorPurchases.Count:N0} vendor(s)\r\n" +
                $"Map discovery       {_tail.Maps.Count:N0} region(s)\r\n" +
                $"Stash tab options   {_tail.StashTabs.Count:N0}\r\n" +
                $"Hero properties     {_tail.AvatarProperties.Count:N0} (decoded; empty for every player in the current DB)\r\n" +
                $"Achievements        {_tail.Achievements.Count:N0} tracked ({_tail.Achievements.Count(a => a.CompletedTime > 0):N0} completed)";
        }

        private void FillDifficulty()
        {
            _filling = true;
            _difficultyCombo.Items.Clear();
            _difficultyCombo.Items.Add(new DifficultyChoice(0, _tail.HasAdminDifficulty ? "(none)" : "(not saved by this server version)"));
            foreach (var (dataRef, name) in _resolver.GetDifficultyTiers())
                _difficultyCombo.Items.Add(new DifficultyChoice(dataRef, name));
            DifficultyChoice current = _difficultyCombo.Items.Cast<DifficultyChoice>().FirstOrDefault(c => c.DataRef == _tail.AdminDifficultyOverride);
            if (current == null)
            {
                current = new DifficultyChoice(_tail.AdminDifficultyOverride, $"{_resolver.FormatDataRef(_tail.AdminDifficultyOverride)} (current)");
                _difficultyCombo.Items.Add(current);
            }
            _difficultyCombo.SelectedItem = current;
            _difficultyCombo.Enabled = _tail.HasAdminDifficulty;   // stock MHServerEmu has no admin difficulty override
            _filling = false;
        }

        private void DifficultyChanged()
        {
            if (_filling || _difficultyCombo.SelectedItem is not DifficultyChoice choice || choice.DataRef == _tail.AdminDifficultyOverride) return;
            _tail.AdminDifficultyOverride = choice.DataRef;
            _changes.RemoveAll(c => c.StartsWith("Difficulty"));
            _changes.Add($"Difficulty override -> {choice.Name}");
            UpdateChanges();
        }

        private record DifficultyChoice(ulong DataRef, string Name)
        {
            public override string ToString() => Name;
        }

        // ---------------- Missions ----------------

        private void FillMissions()
        {
            string[] words = _missionSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int state = _missionState.SelectedIndex - 1;
            string group = _missionGroup.SelectedIndex > 0 ? (string)_missionGroup.SelectedItem : null;

            List<ListViewItem> items = new();
            foreach (PlayerTail.Mission mission in _tail.Missions)
            {
                if (state >= 0 && mission.HeaderState != state) continue;
                string missionGroup = _resolver.MissionGroup(mission.Guid);
                if (group != null && missionGroup != group) continue;
                string name = _resolver.Format(mission.Guid);
                if (words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase) == false)) continue;

                bool resetting = _missionsToReset.Contains(mission);
                items.Add(new ListViewItem(new[]
                {
                    name, missionGroup, mission.HeaderState < MissionStates.Length ? MissionStates[mission.HeaderState] : mission.HeaderState.ToString(),
                    mission.Version.ToString(), resetting ? "reset" : "",
                })
                { Tag = mission, ForeColor = resetting ? Color.Gray : SystemColors.WindowText, Font = resetting ? _strikeoutFont ??= new Font(_missionList.Font, FontStyle.Strikeout) : _missionList.Font });
            }

            _missionList.BeginUpdate();
            _missionList.Items.Clear();
            _missionList.Items.AddRange(items.ToArray());
            _missionList.EndUpdate();
            _missionCount.Text = $"{items.Count:N0} of {_tail.Missions.Count:N0}";
        }

        private void ResetTickedMissions()
        {
            List<PlayerTail.Mission> ticked = _missionList.CheckedItems.Cast<ListViewItem>().Select(i => (PlayerTail.Mission)i.Tag).Where(m => _missionsToReset.Contains(m) == false).ToList();
            if (ticked.Count == 0) return;
            foreach (PlayerTail.Mission mission in ticked)
            {
                _missionsToReset.Add(mission);
                _changes.Add($"Reset {_resolver.Format(mission.Guid)}");
            }
            FillMissions();
            UpdateChanges();
        }

        // ---------------- Stash tabs ----------------

        private void FillStash()
        {
            _filling = true;
            HashSet<ulong> unlocked = _tail.UnlockedInventories.ToHashSet();
            _stashList.BeginUpdate();
            _stashList.Items.Clear();
            foreach (PrototypeResolver.StashTab tab in _resolver.GetStashTabs())
            {
                bool isUnlocked = tab.LockedByDefault == false || unlocked.Contains(tab.Guid);
                string customName = StashOptionsText(tab.Guid, out string color, out string order);
                ListViewItem item = new(new[] { tab.Name, tab.ForAvatar ?? "(general)", tab.LockedByDefault ? (isUnlocked ? "Unlocked" : "Locked") : "Always open", customName, color, order })
                {
                    Tag = tab,
                    Checked = isUnlocked,
                    ForeColor = tab.LockedByDefault ? SystemColors.WindowText : SystemColors.GrayText,
                    ToolTipText = tab.Path,
                };
                _stashList.Items.Add(item);
            }
            // Unlocked inventories the game data doesn't know about (removed custom tabs?) - keep them visible.
            foreach (ulong guid in _tail.UnlockedInventories.Where(g => _stashList.Items.Cast<ListViewItem>().All(i => ((PrototypeResolver.StashTab)i.Tag).Guid != g)))
                _stashList.Items.Add(new ListViewItem(new[] { _resolver.Format(guid), "?", "Unlocked (not a stash tab in game data)" })
                    { Tag = new PrototypeResolver.StashTab { Guid = guid, Name = _resolver.Format(guid), LockedByDefault = true }, Checked = true });
            _stashList.EndUpdate();
            _stashList.ShowItemToolTips = true;
            _filling = false;
        }

        private void StashItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_filling) return;
            PrototypeResolver.StashTab tab = (PrototypeResolver.StashTab)_stashList.Items[e.Index].Tag;
            if (tab.LockedByDefault == false) { e.NewValue = CheckState.Checked; return; }

            // ListView raises ItemCheck for pre-ticked rows when it's first shown, so compare with
            // the real state instead of trusting the event to mean a user change.
            bool unlock = e.NewValue == CheckState.Checked;
            if (unlock == _tail.UnlockedInventories.Contains(tab.Guid)) return;
            if (unlock)
            {
                if (_tail.UnlockedInventories.Contains(tab.Guid) == false) _tail.UnlockedInventories.Add(tab.Guid);
                RecordStashChange(tab, true);
            }
            else
            {
                int items = _entityRepository.GetForContainer(DBEntityCategory.Item, _account.Id).Count(i => unchecked((ulong)i.InventoryProtoGuid) == tab.Guid);
                if (items > 0)
                {
                    e.NewValue = CheckState.Checked;
                    MessageBox.Show(this, $"{tab.Name} still holds {items} item(s). Move them out first (Manage Items/Entities), or they'd be deleted on the player's next save.",
                        "Tab not empty", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _tail.UnlockedInventories.Remove(tab.Guid);
                RecordStashChange(tab, false);
            }
            _stashList.Items[e.Index].SubItems[2].Text = unlock ? "Unlocked" : "Locked";
        }

        private void RecordStashChange(PrototypeResolver.StashTab tab, bool unlocked)
        {
            string unlockText = $"Unlock {tab.Name}", lockText = $"Lock {tab.Name}";
            // Toggling back and forth cancels out.
            if (_changes.Remove(unlocked ? lockText : unlockText) == false)
                _changes.Add(unlocked ? unlockText : lockText);
            UpdateChanges();
        }

        // ---------------- Friends ----------------

        private void FillFriends()
        {
            _friendList.BeginUpdate();
            _friendList.Items.Clear();
            foreach (PlayerTail.CommunityMember member in _tail.Members)
            {
                string circles = string.Join(", ", member.Circles.Select(c => c >= 0 && c < _tail.CircleNames.Count ? _tail.CircleNames[c].Trim('_') : $"#{c}"));
                _friendList.Items.Add(new ListViewItem(new[] { member.Name, $"0x{member.DbId:X}", circles }) { Tag = member });
            }
            _friendList.EndUpdate();
        }

        private void RemoveSelectedFriends()
        {
            foreach (ListViewItem item in _friendList.SelectedItems)
            {
                PlayerTail.CommunityMember member = (PlayerTail.CommunityMember)item.Tag;
                _tail.Members.Remove(member);
                _changes.Add($"Remove {member.Name} from friends/ignore");
            }
            FillFriends();
            UpdateChanges();
        }

        // ---------------- Vendors ----------------

        private void FillVendors()
        {
            _vendorList.BeginUpdate();
            _vendorList.Items.Clear();
            foreach (byte[] body in _tail.VendorPurchases)
            {
                string name;
                try { name = _resolver.Format(PlayerTail.VendorInventoryGuid(body)); } catch (Exception) { name = "?"; }
                _vendorList.Items.Add(new ListViewItem(new[] { name, $"{body.Length} B" }) { Tag = body });
            }
            _vendorList.EndUpdate();
        }

        private void ResetVendors(bool all)
        {
            IEnumerable<ListViewItem> items = all ? _vendorList.Items.Cast<ListViewItem>() : _vendorList.SelectedItems.Cast<ListViewItem>();
            foreach (ListViewItem item in items.ToList())
            {
                _tail.VendorPurchases.Remove((byte[])item.Tag);
                _changes.Add($"Reset vendor {item.Text}");
            }
            FillVendors();
            UpdateChanges();
        }

        // ---------------- Save ----------------

        private void UpdateChanges()
        {
            _saveButton.Enabled = _changes.Count > 0;
            _changesLabel.Text = _changes.Count == 0 ? "No changes" : $"{_changes.Count} unsaved change(s)";
            FillOverview();
        }

        private void Save()
        {
            if (_changes.Count == 0) return;
            string summary = string.Join("\r\n", _changes.Take(15).Select(c => "  - " + c)) + (_changes.Count > 15 ? $"\r\n  ...and {_changes.Count - 15} more" : "");
            if (MessageBox.Show(this, $"Save these changes for {_account.PlayerName}?\r\n\r\n{summary}\r\n\r\nThe player must be logged out, or the server will save over this.",
                    "Save player data", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            try
            {
                _tail.Missions.RemoveAll(m => _missionsToReset.Contains(m));
                _parsed.TailBytes = PlayerTailCodec.Encode(_tail);
                byte[] newData = _parsed.Rebuild();

                // Sanity check: the new blob must decode cleanly before it goes anywhere near the DB.
                ParsedEntityArchive check = EntityArchiveCodec.Parse(newData);
                if (PlayerTailCodec.TryDecode(check.TailBytes, check.Version, _tail.Layout, out string error) == null)
                    throw new InvalidOperationException("The re-encoded data didn't decode back: " + error);

                _accountRepository.SetPlayerArchiveData(_account.Id, newData);
                DialogResult = DialogResult.OK;
                MessageBox.Show(this, $"Saved {_changes.Count} change(s).", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message + "\r\n\r\nNothing was written.", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------- helpers ----------------

        private static ListView NewList(bool checkBoxes, params (string Text, int Width)[] columns)
        {
            ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = checkBoxes, HideSelection = false };
            foreach (var (text, width) in columns) list.Columns.Add(text, width);
            return list;
        }

        private static Label HelpLabel(string text) =>
            new() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 4, 8, 0), ForeColor = SystemColors.GrayText, Text = text };

        private static FlowLayoutPanel ButtonRow(params (string Text, Action Click)[] buttons)
        {
            FlowLayoutPanel row = new() { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
            foreach (var (text, click) in buttons)
            {
                Button button = new() { Text = text, AutoSize = true, MinimumSize = new Size(100, 0) };
                button.Click += (_, _) => click();
                row.Controls.Add(button);
            }
            return row;
        }

        private static void SetAllChecked(ListView list, bool value)
        {
            list.BeginUpdate();
            foreach (ListViewItem item in list.Items) item.Checked = value;
            list.EndUpdate();
        }
    }
}
