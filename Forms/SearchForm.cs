using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MHDBEditor.Data;
using MHDBEditor.Models;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Tools > Search & Bulk Actions: find items or players across the whole database, tick
    // results, and act on all of them at once.
    public class SearchForm : Form
    {
        private const int DisplayLimit = 20000;

        private readonly DbConnectionManager _connectionManager;
        private readonly PrototypeResolver _resolver;
        private readonly ItemTemplateStore _templates;
        private readonly SearchService _search;
        private readonly BulkActionService _bulk;

        private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
        private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(8, 4, 8, 0), AutoEllipsis = true };
        private CancellationTokenSource _cancel;

        // Items tab
        private ComboBox _categoryCombo, _itemCombo, _rarityCombo, _placeCombo, _affixCombo, _problemCombo, _itemPlayerCombo;
        private NumericUpDown _minLevel, _maxLevel;
        private List<SearchService.CatalogEntry> _itemCatalog = new();
        private List<(long Id, string Name)> _playerNames = new();
        private Button _itemSearchButton, _itemCancelButton;
        private ListView _itemList;
        private List<SearchService.ItemHit> _itemHits = new();

        // Players tab
        private ComboBox _playerCombo, _regionCombo, _heroCombo, _seenCombo;
        private CheckBox _bannedOnly;
        private Label _ownerFilterLabel;
        private ListView _playerList;
        private HashSet<long> _onlyAccounts;
        private List<SearchService.PlayerHit> _playerHits = new();

        public SearchForm(DbConnectionManager connectionManager, PrototypeResolver resolver, ItemTemplateStore templates)
        {
            _connectionManager = connectionManager;
            _resolver = resolver;
            _templates = templates;
            _search = new SearchService(connectionManager, resolver);
            _bulk = new BulkActionService(connectionManager, resolver);

            Text = "Search & Bulk Actions";
            Width = 1250;
            Height = 760;
            MinimumSize = new Size(900, 500);
            StartPosition = FormStartPosition.CenterParent;

            _itemCatalog = _search.GetItemCatalog();
            _playerNames = _search.GetPlayerNames();
            _tabs.TabPages.Add(BuildItemsTab());
            _tabs.TabPages.Add(BuildPlayersTab());
            Controls.Add(_tabs);
            Controls.Add(_status);
            _status.Text = _resolver.HasGameData ? ""
                : "Game data isn't loaded (Tools > Load Game Data): names will show as ids and the affix and location filters won't work.";

            FormClosing += (_, _) => { _cancel?.Cancel(); _affixLoadCancel.Cancel(); };
            Disposed += (_, _) => { _cancel?.Dispose(); _affixLoadCancel.Dispose(); };
            Shown += async (_, _) => await LoadAffixes();
        }

        // The affix list needs every item blob read (a few seconds), so it fills in after the window opens.
        private readonly CancellationTokenSource _affixLoadCancel = new();

        private async Task LoadAffixes()
        {
            try
            {
                List<SearchService.CatalogEntry> affixes = await Task.Run(() => _search.GetAffixCatalog(_affixLoadCancel.Token));
                if (IsDisposed) return;
                _affixCombo.BeginUpdate();
                _affixCombo.Items.Clear();
                _affixCombo.Items.Add(new Choice(0, "(any affix)"));
                foreach (SearchService.CatalogEntry affix in affixes)
                    _affixCombo.Items.Add(new Choice(affix.Guid, $"{affix.Group}: {affix.Name}  ({affix.Count:N0})"));
                _affixCombo.SelectedIndex = 0;
                _affixCombo.EndUpdate();
                _affixCombo.Enabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsDisposed == false) { _affixCombo.Items.Clear(); _affixCombo.Items.Add(new Choice(0, "(couldn't list affixes)")); _affixCombo.SelectedIndex = 0; _status.Text = ex.Message; } }
        }

        // A dropdown you can also type into to jump to a match. Picking is enough - typing is optional.
        private static ComboBox NewPicker(int width) => new()
        {
            Width = width,
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
            MaxDropDownItems = 25,
        };

        // What's picked; typed text that matches an entry counts too, anything else means "any".
        private static ulong Picked(ComboBox combo)
        {
            if (combo.SelectedItem is Choice choice && string.Equals(choice.Name, combo.Text, StringComparison.Ordinal)) return choice.Guid;
            Choice typed = combo.Items.OfType<Choice>().FirstOrDefault(c => string.Equals(c.Name, combo.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (typed != null) { combo.SelectedItem = typed; return typed.Guid; }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
            return 0;
        }

        private void FillItemCombo()
        {
            string category = _categoryCombo.SelectedItem is Choice c && c.Guid != 0 ? c.Name : null;
            _itemCombo.BeginUpdate();
            _itemCombo.Items.Clear();
            _itemCombo.Items.Add(new Choice(0, "(any item)"));
            foreach (SearchService.CatalogEntry item in _itemCatalog.Where(i => category == null || i.Group == category))
                _itemCombo.Items.Add(new Choice(item.Guid, category == null ? $"{item.Group}: {item.Name}  ({item.Count:N0})" : $"{item.Name}  ({item.Count:N0})"));
            _itemCombo.SelectedIndex = 0;
            _itemCombo.EndUpdate();
        }

        private static void FillPlayerCombo(ComboBox combo, List<(long Id, string Name)> players)
        {
            combo.BeginUpdate();
            combo.Items.Add(new Choice(0, "(any player)"));
            foreach (var (id, name) in players) combo.Items.Add(new Choice(unchecked((ulong)id), name));
            combo.SelectedIndex = 0;
            combo.EndUpdate();
        }

        // ================================================================ Items

        private TabPage BuildItemsTab()
        {
            TabPage page = new("Items");

            TableLayoutPanel filters = new() { Dock = DockStyle.Top, Height = 72, ColumnCount = 10, RowCount = 2, Padding = new Padding(6, 6, 6, 0) };
            _categoryCombo = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, MaxDropDownItems = 25 };
            _itemCombo = NewPicker(300);
            _rarityCombo = new ComboBox { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
            _minLevel = new NumericUpDown { Width = 55, Minimum = 0, Maximum = 100 };
            _maxLevel = new NumericUpDown { Width = 55, Minimum = 0, Maximum = 100 };
            _placeCombo = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
            _affixCombo = NewPicker(300);
            _problemCombo = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _itemPlayerCombo = NewPicker(150);
            _itemSearchButton = new Button { Text = "Search", Width = 90 };
            _itemCancelButton = new Button { Text = "Cancel", Width = 80, Enabled = false };

            _categoryCombo.Items.Add(new Choice(0, "(any category)"));
            foreach (var group in _itemCatalog.GroupBy(i => i.Group).OrderBy(g => g.Key))
                _categoryCombo.Items.Add(new Choice(1, group.Key));
            _categoryCombo.SelectedIndex = 0;
            _categoryCombo.SelectedIndexChanged += (_, _) => FillItemCombo();
            FillItemCombo();
            _rarityCombo.Items.Add(new Choice(0, "(any rarity)"));
            foreach (var (guid, name) in _search.GetRarities()) _rarityCombo.Items.Add(new Choice(guid, name));
            _rarityCombo.SelectedIndex = 0;
            _placeCombo.Items.AddRange(new object[] { "Anywhere", "Player inventories / stash", "Equipped on heroes", "On team-ups", "No owner (orphaned)" });
            _placeCombo.SelectedIndex = 0;
            _affixCombo.Items.Add(new Choice(0, "(listing affixes...)"));
            _affixCombo.SelectedIndex = 0;
            _affixCombo.Enabled = false;
            _problemCombo.Items.AddRange(new object[] { "(don't check)", "Any problem", "Stat over its cap", "Breaks the item", "Does nothing" });
            _problemCombo.SelectedIndex = 0;
            FillPlayerCombo(_itemPlayerCombo, _playerNames);

            Label L(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 0) };
            FlowLayoutPanel levels = new() { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            levels.Controls.AddRange(new Control[] { _minLevel, new Label { Text = "to", AutoSize = true, Margin = new Padding(2, 7, 2, 0) }, _maxLevel });

            filters.Controls.Add(L("Category"), 0, 0); filters.Controls.Add(_categoryCombo, 1, 0);
            filters.Controls.Add(L("Item"), 2, 0); filters.Controls.Add(_itemCombo, 3, 0);
            filters.Controls.Add(L("Rarity"), 4, 0); filters.Controls.Add(_rarityCombo, 5, 0);
            filters.Controls.Add(L("Level"), 6, 0); filters.Controls.Add(levels, 7, 0);
            filters.Controls.Add(L("Where"), 0, 1); filters.Controls.Add(_placeCombo, 1, 1);
            filters.Controls.Add(L("Affix"), 2, 1); filters.Controls.Add(_affixCombo, 3, 1);
            filters.Controls.Add(L("Problem"), 4, 1); filters.Controls.Add(_problemCombo, 5, 1);
            filters.Controls.Add(L("Player"), 6, 1); filters.Controls.Add(_itemPlayerCombo, 7, 1);
            filters.Controls.Add(_itemSearchButton, 8, 1); filters.Controls.Add(_itemCancelButton, 9, 1);

            _itemList = NewList(("Player", 130), ("Where", 150), ("Item", 230), ("Rarity", 90), ("Level", 50), ("Affixes", 260), ("Problem", 220), ("Inventory", 170), ("Slot", 50));

            FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6, 4, 6, 0) };
            buttons.Controls.Add(NewButton("Tick All", 80, () => SetAllChecked(_itemList, true)));
            buttons.Controls.Add(NewButton("Untick All", 80, () => SetAllChecked(_itemList, false)));
            buttons.Controls.Add(NewButton("Take Back Ticked Items...", 180, TakeBackItems));
            buttons.Controls.Add(NewButton("Show Owners in Players Tab", 190, ShowOwners));
            buttons.Controls.Add(NewButton("Export CSV...", 100, () => ExportCsv(_itemList, "items")));

            _itemSearchButton.Click += async (_, _) => await RunItemSearch();
            _itemCancelButton.Click += (_, _) => _cancel?.Cancel();
            foreach (Control box in new Control[] { _itemCombo, _affixCombo, _itemPlayerCombo, _minLevel, _maxLevel })
                box.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && _itemSearchButton.Enabled) { e.SuppressKeyPress = true; await RunItemSearch(); } };

            page.Controls.Add(_itemList);
            page.Controls.Add(buttons);
            page.Controls.Add(filters);
            return page;
        }

        private async Task RunItemSearch()
        {
            SearchService.ItemQuery query = new()
            {
                Category = _categoryCombo.SelectedIndex > 0 ? ((Choice)_categoryCombo.SelectedItem).Name : null,
                ItemGuid = unchecked((long)Picked(_itemCombo)),
                RarityGuid = ((Choice)_rarityCombo.SelectedItem).Guid,
                MinLevel = _minLevel.Value > 0 ? (int)_minLevel.Value : null,
                MaxLevel = _maxLevel.Value > 0 ? (int)_maxLevel.Value : null,
                AffixGuid = Picked(_affixCombo),
                Problem = (SearchService.AffixProblem)_problemCombo.SelectedIndex,
                Place = (SearchService.ItemPlace)_placeCombo.SelectedIndex,
                AccountId = unchecked((long)Picked(_itemPlayerCombo)),
            };
            if (query.Problem != SearchService.AffixProblem.DontCheck && _resolver.HasGameData == false)
            {
                MessageBox.Show(this, "The affix check needs game data (Tools > Load Game Data).", "Game data needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            CancellationToken token = _cancel.Token;
            _itemSearchButton.Enabled = false;
            _itemCancelButton.Enabled = true;
            Progress<string> progress = new(text => { if (IsDisposed == false) _status.Text = text; });
            DateTime started = DateTime.Now;
            try
            {
                int total = 0;
                List<SearchService.ItemHit> hits = await Task.Run(() => _search.FindItems(query, DisplayLimit, progress, token, out total));
                _itemHits = hits;
                FillItems();
                _status.Text = $"{total:N0} item(s) match" + (total > hits.Count ? $" - showing the first {hits.Count:N0}, narrow the search to see the rest" : "")
                    + $" ({(DateTime.Now - started).TotalSeconds:N1}s).";
            }
            catch (OperationCanceledException) { if (IsDisposed == false) _status.Text = "Search cancelled."; }
            catch (Exception ex) { if (IsDisposed == false) MessageBox.Show(this, ex.Message, "Search failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                if (IsDisposed == false) { _itemSearchButton.Enabled = true; _itemCancelButton.Enabled = false; }
            }
        }

        private void FillItems()
        {
            _itemList.BeginUpdate();
            _itemList.Items.Clear();
            _itemList.Items.AddRange(_itemHits.Select(h => new ListViewItem(new[]
            {
                h.PlayerName, h.Where, h.ItemName, h.Rarity, h.Level.ToString(), h.Affixes, h.Problem, h.Inventory, h.Slot.ToString(),
            }) { Tag = h, ForeColor = h.Problem.Length > 0 ? Color.DarkRed : SystemColors.WindowText }).ToArray());
            _itemList.EndUpdate();
        }

        private void TakeBackItems()
        {
            List<SearchService.ItemHit> ticked = Ticked<SearchService.ItemHit>(_itemList);
            if (ticked.Count == 0) { NothingTicked(); return; }
            int players = ticked.Select(h => h.AccountId).Distinct().Count();
            string sample = string.Join("\r\n", ticked.GroupBy(h => h.ItemName).OrderByDescending(g => g.Count()).Take(8).Select(g => $"  {g.Count():N0} x {g.Key}"));
            if (Confirm($"Delete {ticked.Count:N0} item(s) from {players:N0} player(s)?\r\n\r\n{sample}", out bool backup) == false) return;

            RunBulk(backup, () => $"Took back {_bulk.DeleteItems(ticked.Select(h => h.DbGuid)):N0} item(s).", async () => await RunItemSearch());
        }

        private void ShowOwners()
        {
            List<SearchService.ItemHit> source = Ticked<SearchService.ItemHit>(_itemList);
            if (source.Count == 0) source = _itemHits;
            HashSet<long> owners = source.Where(h => h.AccountId != 0).Select(h => h.AccountId).ToHashSet();
            if (owners.Count == 0) { MessageBox.Show(this, "None of these items has an owner.", "No owners", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            _onlyAccounts = owners;
            _ownerFilterLabel.Text = $"Only the {owners.Count:N0} owner(s) of the {(source == _itemHits ? "found" : "ticked")} items. Click here to show everyone again.";
            _ownerFilterLabel.Visible = true;
            _tabs.SelectedIndex = 1;
            RunPlayerSearch();
        }

        // ================================================================ Players

        private TabPage BuildPlayersTab()
        {
            TabPage page = new("Players");

            FlowLayoutPanel filters = new() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 6, 6, 0), WrapContents = false };
            _playerCombo = NewPicker(170);
            FillPlayerCombo(_playerCombo, _playerNames);
            _regionCombo = new ComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
            _heroCombo = new ComboBox { Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
            _seenCombo = new ComboBox { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
            _bannedOnly = new CheckBox { Text = "Banned only", AutoSize = true, Margin = new Padding(8, 6, 3, 0) };
            Button search = NewButton("Search", 90, RunPlayerSearch);

            _regionCombo.Items.Add(new Choice(0, "(anywhere)"));
            _regionCombo.Items.Add(new Choice(ulong.MaxValue, "(broken / unknown location)"));
            foreach (var (guid, name) in _search.GetRegions()) _regionCombo.Items.Add(new Choice(guid, name));
            _regionCombo.SelectedIndex = 0;
            _heroCombo.Items.Add(new Choice(0, "(any hero)"));
            foreach (var (guid, name) in _search.GetHeroes()) _heroCombo.Items.Add(new Choice(guid, $"owns {name}"));
            _heroCombo.SelectedIndex = 0;
            _seenCombo.Items.AddRange(new object[] { "(any last login)", "seen in the last 7 days", "seen in the last 30 days", "not seen for 30+ days", "not seen for 90+ days", "not seen for a year+" });
            _seenCombo.SelectedIndex = 0;

            Label L(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(6, 6, 2, 0) };
            filters.Controls.AddRange(new Control[] { L("Player"), _playerCombo, L("In"), _regionCombo, _heroCombo, _seenCombo, _bannedOnly, search });

            _ownerFilterLabel = new Label { Dock = DockStyle.Top, Height = 24, Padding = new Padding(10, 4, 0, 0), ForeColor = Color.DarkBlue, Visible = false, Cursor = Cursors.Hand };
            _ownerFilterLabel.Click += (_, _) => { _onlyAccounts = null; _ownerFilterLabel.Visible = false; RunPlayerSearch(); };

            _playerList = NewList(("Player", 160), ("Email", 220), ("Location (next login)", 260), ("Heroes", 60), ("Last logout", 130), ("Banned", 60), ("User level", 80));

            FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6, 4, 6, 0) };
            buttons.Controls.Add(NewButton("Tick All", 80, () => SetAllChecked(_playerList, true)));
            buttons.Controls.Add(NewButton("Untick All", 80, () => SetAllChecked(_playerList, false)));
            buttons.Controls.Add(NewButton("Move Ticked...", 110, MovePlayers));
            buttons.Controls.Add(NewButton("Give Item...", 100, GiveItem));
            buttons.Controls.Add(NewButton("Give Gazillionite...", 140, GiveGazillionite));
            buttons.Controls.Add(NewButton("Ban", 60, () => SetBanned(true)));
            buttons.Controls.Add(NewButton("Unban", 60, () => SetBanned(false)));
            buttons.Controls.Add(NewButton("Export CSV...", 100, () => ExportCsv(_playerList, "players")));

            _playerCombo.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RunPlayerSearch(); } };

            page.Controls.Add(_playerList);
            page.Controls.Add(_ownerFilterLabel);
            page.Controls.Add(buttons);
            page.Controls.Add(filters);
            return page;
        }

        private void RunPlayerSearch()
        {
            ulong region = ((Choice)_regionCombo.SelectedItem).Guid;
            int seen = _seenCombo.SelectedIndex;
            try
            {
                Cursor = Cursors.WaitCursor;
                _playerHits = _search.FindPlayers(new SearchService.PlayerQuery
                {
                    AccountId = unchecked((long)Picked(_playerCombo)),
                    RegionGuid = region == ulong.MaxValue ? 0 : region,
                    BrokenLocationOnly = region == ulong.MaxValue,
                    HeroGuid = ((Choice)_heroCombo.SelectedItem).Guid,
                    SeenWithinDays = seen == 1 ? 7 : seen == 2 ? 30 : null,
                    NotSeenForDays = seen == 3 ? 30 : seen == 4 ? 90 : seen == 5 ? 365 : null,
                    BannedOnly = _bannedOnly.Checked,
                    OnlyAccounts = _onlyAccounts,
                });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Search failed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            finally { Cursor = Cursors.Default; }

            _playerList.BeginUpdate();
            _playerList.Items.Clear();
            _playerList.Items.AddRange(_playerHits.Take(DisplayLimit).Select(h => new ListViewItem(new[]
            {
                h.PlayerName, h.Email, h.Location, h.Heroes.ToString(), h.LastLogout?.ToString("yyyy-MM-dd HH:mm") ?? "never", h.Banned ? "yes" : "", h.UserLevel.ToString(),
            }) { Tag = h, ForeColor = h.LocationOk ? (h.Banned ? Color.Gray : SystemColors.WindowText) : Color.DarkRed }).ToArray());
            _playerList.EndUpdate();
            _status.Text = $"{_playerHits.Count:N0} player(s) match" + (_playerHits.Count > DisplayLimit ? $" - showing the first {DisplayLimit:N0}" : "") + ".";
        }

        private void MovePlayers()
        {
            List<SearchService.PlayerHit> ticked = Ticked<SearchService.PlayerHit>(_playerList);
            if (ticked.Count == 0) { NothingTicked(); return; }
            if (_resolver.HasGameData == false) { MessageBox.Show(this, "Load game data first (Tools > Load Game Data): the list of places comes from it.", "Game data needed", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            using StartLocationPickerForm picker = new(_resolver, ticked.Count == 1 ? ticked[0].StartTarget : 0, canSetLastTown: true);
            picker.Text = $"Move {ticked.Count:N0} player(s)";
            if (picker.ShowDialog(this) != DialogResult.OK || picker.Result == null) return;
            PrototypeResolver.StartLocation location = picker.Result;
            bool lastTown = picker.AlsoSetLastTown;

            if (Confirm($"Send {ticked.Count:N0} player(s) to {location.RegionName} ({location.Name}) on their next login" +
                (lastTown ? ", and make it their last town?" : "?"), out bool backup) == false) return;
            RunBulk(backup, () =>
            {
                var (moved, towns) = _bulk.MovePlayers(ticked.Select(h => h.AccountId), location, lastTown);
                return $"Moved {moved:N0} player(s) to {location.RegionName}" + (lastTown ? $" ({towns:N0} also got it as their last town)." : ".");
            }, RunPlayerSearch);
        }

        private void GiveItem()
        {
            List<SearchService.PlayerHit> ticked = Ticked<SearchService.PlayerHit>(_playerList);
            if (ticked.Count == 0) { NothingTicked(); return; }
            if (_templates.Templates.Count == 0)
            {
                MessageBox.Show(this, "No item templates saved yet. Open a player's items (Manage Items/Entities...), select an item and click \"Save As Template...\".",
                    "No templates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using ItemTemplatePickerForm picker = new(_templates.Templates);
            if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null) return;
            ItemTemplate template = picker.Selected;

            if (Confirm($"Give \"{template.DisplayName}\" to {ticked.Count:N0} player(s)? It goes in the same inventory the template came from (usually the stash).", out bool backup) == false) return;
            RunBulk(backup, () => $"Gave \"{template.DisplayName}\" to {_bulk.GiveItem(template, ticked.Select(h => h.AccountId)):N0} player(s).", RunPlayerSearch);
        }

        private void GiveGazillionite()
        {
            List<SearchService.PlayerHit> ticked = Ticked<SearchService.PlayerHit>(_playerList);
            if (ticked.Count == 0) { NothingTicked(); return; }
            string input = PromptForm.ShowSingle(this, "Give Gazillionite", $"How much Gazillionite to add to each of the {ticked.Count:N0} player(s)?", "100");
            if (input == null) return;
            if (long.TryParse(input.Trim(), out long amount) == false || amount <= 0)
            {
                MessageBox.Show(this, "Enter a positive whole number.", "Invalid amount", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (Confirm($"Add {amount:N0} Gazillionite to each of {ticked.Count:N0} player(s)?", out bool backup) == false) return;
            RunBulk(backup, () => $"Gave {amount:N0} Gazillionite to {_bulk.GiveGazillionite(ticked.Select(h => h.AccountId), amount):N0} player(s).", RunPlayerSearch);
        }

        private void SetBanned(bool banned)
        {
            List<SearchService.PlayerHit> ticked = Ticked<SearchService.PlayerHit>(_playerList);
            if (ticked.Count == 0) { NothingTicked(); return; }
            if (Confirm($"{(banned ? "Ban" : "Unban")} {ticked.Count:N0} player(s)?", out bool backup) == false) return;
            RunBulk(backup, () => $"{(banned ? "Banned" : "Unbanned")} {_bulk.SetBanned(ticked.Select(h => h.AccountId), banned):N0} player(s).", RunPlayerSearch);
        }

        // ================================================================ shared

        // Yes = back up first, No = go ahead without one, Cancel = stop.
        private bool Confirm(string what, out bool backup)
        {
            DialogResult answer = MessageBox.Show(this,
                $"{what}\r\n\r\nThe players must be logged out, or the server will undo this when they log out.\r\n\r\n" +
                "Yes = back up the database first, then do it\r\nNo = do it without a backup\r\nCancel = don't do anything",
                "Bulk action", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            backup = answer == DialogResult.Yes;
            return answer != DialogResult.Cancel;
        }

        private void RunBulk(bool backup, Func<string> action, Action refresh)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                string backupPath = null;
                if (backup)
                {
                    _status.Text = "Backing up the database...";
                    _status.Refresh();
                    backupPath = _connectionManager.CreateBackup();
                }
                string result = action();
                Cursor = Cursors.Default;
                MessageBox.Show(this, result + (backupPath != null ? $"\r\n\r\nBackup: {backupPath}" : ""), "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
                refresh();
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, $"Nothing was changed.\r\n\r\n{ex.Message}", "Bulk action failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static ListView NewList(params (string Header, int Width)[] columns)
        {
            ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, HideSelection = false };
            foreach (var (header, width) in columns) list.Columns.Add(header, width);
            // Space toggles the ticks of every selected row, so a block can be ticked with shift-click + space.
            list.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Space || list.SelectedItems.Count < 2) return;
                bool check = list.SelectedItems.Cast<ListViewItem>().Any(i => i.Checked == false);
                list.BeginUpdate();
                foreach (ListViewItem item in list.SelectedItems) item.Checked = check;
                list.EndUpdate();
                e.SuppressKeyPress = true;
            };
            return list;
        }

        private static Button NewButton(string text, int width, Action click)
        {
            Button button = new() { Text = text, Width = width, Height = 28 };
            button.Click += (_, _) => click();
            return button;
        }

        private static void SetAllChecked(ListView list, bool check)
        {
            list.BeginUpdate();
            foreach (ListViewItem item in list.Items) item.Checked = check;
            list.EndUpdate();
        }

        private static List<T> Ticked<T>(ListView list) => list.CheckedItems.Cast<ListViewItem>().Select(i => (T)i.Tag).ToList();

        private void NothingTicked() =>
            MessageBox.Show(this, "Tick the rows to act on first (Tick All ticks everything shown).", "Nothing ticked", MessageBoxButtons.OK, MessageBoxIcon.Information);

        private void ExportCsv(ListView list, string name)
        {
            if (list.Items.Count == 0) return;
            using SaveFileDialog dialog = new() { Filter = "CSV (*.csv)|*.csv", FileName = $"{name}_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            static string Cell(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
            StringBuilder csv = new();
            csv.AppendLine(string.Join(",", list.Columns.Cast<ColumnHeader>().Select(c => Cell(c.Text))));
            foreach (ListViewItem item in list.Items)
                csv.AppendLine(string.Join(",", item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => Cell(s.Text))));
            File.WriteAllText(dialog.FileName, csv.ToString(), Encoding.UTF8);
            _status.Text = $"Exported {list.Items.Count:N0} row(s) to {dialog.FileName}.";
        }


        private record Choice(ulong Guid, string Name)
        {
            public override string ToString() => Name;
        }
    }
}
