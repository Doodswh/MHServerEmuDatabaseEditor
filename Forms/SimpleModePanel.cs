using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MHDBEditor.ArchiveFormat;
using MHDBEditor.Data;
using MHDBEditor.Models;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // The friendly landing page: find a player by name, then do one of a handful of common,
    // guided tasks - no hex, guids, or grids in sight. Meant for someone who isn't comfortable
    // with the rest of this tool; "Advanced" (the other tabs) is still there for anyone who
    // needs the full editor. Everything here is built entirely out of what the advanced tabs
    // already do - it's just a friendlier front door onto the same repositories.
    public class SimpleModePanel : UserControl
    {
        private readonly PrototypeResolver _prototypeResolver;
        private readonly ItemTemplateStore _itemTemplateStore;

        private AccountRepository _accountRepository;
        private EntityRepository _entityRepository;
        private bool _isConfigured;

        private DBAccount _selectedAccount;

        private readonly Label _introLabel;
        private readonly TextBox _searchBox;
        private readonly ListBox _resultsList;
        private readonly FlowLayoutPanel _actionsPanel;
        private readonly Label _selectedLabel;
        private readonly Label _statusLabel;
        private Button _banButton;

        public SimpleModePanel(PrototypeResolver prototypeResolver, ItemTemplateStore itemTemplateStore)
        {
            _prototypeResolver = prototypeResolver;
            _itemTemplateStore = itemTemplateStore;

            Dock = DockStyle.Fill;

            _introLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(16, 12, 16, 0),
                Font = new Font("Segoe UI", 11),
                Text = "Open a database first (File > Open Database...).",
            };

            TableLayoutPanel searchRow = new() { Dock = DockStyle.Top, Height = 52, ColumnCount = 2, Padding = new Padding(16, 6, 16, 6) };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 78));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));

            _searchBox = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11), PlaceholderText = "Type a player's name or email..." };
            _searchBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Search(); e.Handled = true; } };

            Button searchButton = new() { Text = "Find", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11) };
            searchButton.Click += (_, _) => Search();

            searchRow.Controls.Add(_searchBox, 0, 0);
            searchRow.Controls.Add(searchButton, 1, 0);

            _resultsList = new ListBox { Dock = DockStyle.Top, Height = 150, Font = new Font("Segoe UI", 10.5f), IntegralHeight = false };
            _resultsList.DoubleClick += (_, _) => SelectFromResults();

            _selectedLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(16, 10, 16, 0),
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                Text = string.Empty,
            };

            _actionsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 172,
                Padding = new Padding(12, 8, 12, 8),
                FlowDirection = FlowDirection.LeftToRight,
                Visible = false,
            };
            BuildActionButtons();

            _statusLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 56,
                Padding = new Padding(16, 8, 16, 8),
                Font = new Font("Segoe UI", 10.5f),
                Text = string.Empty,
            };

            Controls.Add(_statusLabel);
            Controls.Add(_actionsPanel);
            Controls.Add(_selectedLabel);
            Controls.Add(_resultsList);
            Controls.Add(searchRow);
            Controls.Add(_introLabel);
        }

        public void Configure(AccountRepository accountRepository, EntityRepository entityRepository)
        {
            _accountRepository = accountRepository;
            _entityRepository = entityRepository;
            _isConfigured = true;

            _introLabel.Text = "Find a player, then choose what you want to do.";
            _resultsList.Items.Clear();
            _actionsPanel.Visible = false;
            _selectedLabel.Text = string.Empty;
            _statusLabel.Text = string.Empty;
            _selectedAccount = null;
        }

        private void BuildActionButtons()
        {
            Button giveItemButton = MakeActionButton("Give Item...");
            Button giveCurrencyButton = MakeActionButton("Give Currency...");
            Button setLevelButton = MakeActionButton("Set Character Level...");
            _banButton = MakeActionButton("Ban This Player");
            Button renameButton = MakeActionButton("Rename Player...");
            Button resetPasswordButton = MakeActionButton("Reset Password...");

            giveItemButton.Click += (_, _) => GiveItem();
            giveCurrencyButton.Click += (_, _) => GiveCurrency();
            setLevelButton.Click += (_, _) => SetCharacterLevel();
            _banButton.Click += (_, _) => ToggleBan();
            renameButton.Click += (_, _) => RenamePlayer();
            resetPasswordButton.Click += (_, _) => ResetPassword();

            _actionsPanel.Controls.Add(giveItemButton);
            _actionsPanel.Controls.Add(giveCurrencyButton);
            _actionsPanel.Controls.Add(setLevelButton);
            _actionsPanel.Controls.Add(_banButton);
            _actionsPanel.Controls.Add(renameButton);
            _actionsPanel.Controls.Add(resetPasswordButton);
        }

        private static Button MakeActionButton(string text)
        {
            return new Button
            {
                Text = text,
                Width = 170,
                Height = 64,
                Font = new Font("Segoe UI", 10.5f),
                Margin = new Padding(6),
            };
        }

        private void Search()
        {
            if (_isConfigured == false) return;

            string term = _searchBox.Text.Trim();
            if (term.Length == 0) return;

            List<DBAccount> results = _accountRepository.Search(term);
            _resultsList.Items.Clear();
            foreach (DBAccount account in results)
                _resultsList.Items.Add(new AccountOption(account));

            _actionsPanel.Visible = false;
            _selectedLabel.Text = string.Empty;
            _statusLabel.Text = results.Count == 0 ? "No players found with that name or email." : string.Empty;
        }

        private void SelectFromResults()
        {
            if (_resultsList.SelectedItem is not AccountOption option) return;
            _selectedAccount = option.Account;
            RefreshSelectedLabel();
            _actionsPanel.Visible = true;
            _statusLabel.Text = string.Empty;
        }

        private void RefreshSelectedLabel()
        {
            if (_selectedAccount == null) return;

            _selectedAccount = _accountRepository.GetById(_selectedAccount.Id) ?? _selectedAccount;
            string banState = _selectedAccount.HasFlag(AccountFlags.IsBanned) ? "  (currently BANNED)" : "";
            _selectedLabel.Text = $"Selected: {_selectedAccount.PlayerName}{banState}";
            _banButton.Text = _selectedAccount.HasFlag(AccountFlags.IsBanned) ? "Unban This Player" : "Ban This Player";
        }

        // ----- Actions -----

        private void GiveItem()
        {
            if (_selectedAccount == null) return;

            if (_itemTemplateStore.Templates.Count == 0)
            {
                MessageBox.Show(this,
                    "No item templates have been saved yet. Ask whoever set this tool up to save one first " +
                    "(open an account's items in the advanced editor, select an item, and click \"Save As Template...\").",
                    "No templates yet", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using ItemTemplatePickerForm picker = new(_itemTemplateStore.Templates);
            if (picker.ShowDialog(FindForm()) != DialogResult.OK || picker.Selected == null) return;

            ItemTemplate template = picker.Selected;

            try
            {
                long newId = IdGenerator.GenerateEntity(_selectedAccount.Id);
                uint slot = _entityRepository.GetNextFreeSlot(DBEntityCategory.Item, _selectedAccount.Id, template.InventoryProtoGuid);

                DBEntity newItem = new()
                {
                    DbGuid = newId,
                    ContainerDbGuid = _selectedAccount.Id,
                    InventoryProtoGuid = template.InventoryProtoGuid,
                    Slot = slot,
                    EntityProtoGuid = template.EntityProtoGuid,
                    ArchiveData = template.ArchiveData,
                };

                _entityRepository.Insert(DBEntityCategory.Item, newItem);

                ShowStatus($"Gave \"{template.DisplayName}\" to {_selectedAccount.PlayerName}. It'll be in their stash.");
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void GiveCurrency()
        {
            if (_selectedAccount == null) return;

            string input = PromptForm.ShowSingle(FindForm(), "Give Currency", "How much Gazillionite to add?", "100");
            if (input == null) return;

            if (long.TryParse(input.Trim(), out long amount) == false || amount <= 0)
            {
                MessageBox.Show(this, "Enter a positive whole number.", "Invalid amount", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                DBPlayer player = _accountRepository.GetPlayer(_selectedAccount.Id) ?? new DBPlayer { DbGuid = _selectedAccount.Id };
                if (player.GazillioniteBalance < 0) player.GazillioniteBalance = 0; // -1 means "not yet initialized"
                player.GazillioniteBalance += amount;
                _accountRepository.UpdatePlayerFields(player);

                ShowStatus($"Gave {_selectedAccount.PlayerName} {amount} Gazillionite (new total: {player.GazillioniteBalance}).");
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void SetCharacterLevel()
        {
            if (_selectedAccount == null) return;

            if (_prototypeResolver.TryResolveByName("CharacterLevel", out ulong levelGuid) == false)
            {
                MessageBox.Show(this,
                    "This needs a prototype name list loaded first. Ask whoever set this tool up to load one " +
                    "(Tools > Load Prototype Name List...).",
                    "Not available yet", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<DBEntity> avatars = _entityRepository.GetForContainer(DBEntityCategory.Avatar, _selectedAccount.Id);
            if (avatars.Count == 0)
            {
                MessageBox.Show(this, $"{_selectedAccount.PlayerName} doesn't have any characters yet.", "No characters", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DBEntity avatar;
            if (avatars.Count == 1)
            {
                avatar = avatars[0];
            }
            else
            {
                using AvatarPickerForm picker = new(avatars, _prototypeResolver);
                if (picker.ShowDialog(FindForm()) != DialogResult.OK || picker.Selected == null) return;
                avatar = picker.Selected;
            }

            string input = PromptForm.ShowSingle(FindForm(), "Set Character Level", "New level:", "60");
            if (input == null) return;

            if (int.TryParse(input.Trim(), out int newLevel) == false || newLevel <= 0)
            {
                MessageBox.Show(this, "Enter a positive whole number.", "Invalid level", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                ParsedEntityArchive parsed = EntityArchiveCodec.Parse(avatar.ArchiveData);

                PropertyRecord existing = parsed.Properties.Find(p => p.PrototypeGuid == levelGuid && p.Params.Count == 0);
                if (existing != null)
                {
                    existing.DataType = PropertyDataType.Integer;
                    existing.ValueLong = newLevel;
                }
                else
                {
                    parsed.Properties.Add(new PropertyRecord
                    {
                        Version = 0,
                        PrototypeGuid = levelGuid,
                        DataType = PropertyDataType.Integer,
                        ValueLong = newLevel,
                    });
                }

                byte[] newData = parsed.Rebuild();
                _entityRepository.SetRawArchiveData(DBEntityCategory.Avatar, avatar.DbGuid, newData);

                ShowStatus($"Set that character's level to {newLevel}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Couldn't update this character's data: {ex.Message}\n\nNothing was changed.",
                    "Couldn't set level", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ToggleBan()
        {
            if (_selectedAccount == null) return;

            bool currentlyBanned = _selectedAccount.HasFlag(AccountFlags.IsBanned);

            if (currentlyBanned == false)
            {
                DialogResult confirm = MessageBox.Show(this,
                    $"Ban {_selectedAccount.PlayerName}? They won't be able to log in until unbanned.",
                    "Confirm ban", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes) return;
            }

            try
            {
                AccountFlags newFlags = currentlyBanned
                    ? _selectedAccount.Flags & ~AccountFlags.IsBanned
                    : _selectedAccount.Flags | AccountFlags.IsBanned;

                _accountRepository.UpdateFlags(_selectedAccount.Id, newFlags);
                RefreshSelectedLabel();

                ShowStatus(currentlyBanned ? $"{_selectedAccount.PlayerName} has been unbanned." : $"{_selectedAccount.PlayerName} has been banned.");
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void RenamePlayer()
        {
            if (_selectedAccount == null) return;

            string input = PromptForm.ShowSingle(FindForm(), "Rename Player", "New name:", _selectedAccount.PlayerName);
            if (string.IsNullOrWhiteSpace(input)) return;

            try
            {
                _accountRepository.Rename(_selectedAccount.Id, input.Trim());
                RefreshSelectedLabel();
                ShowStatus($"Renamed to \"{input.Trim()}\".");
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void ResetPassword()
        {
            if (_selectedAccount == null) return;

            string input = PromptForm.ShowSingle(FindForm(), "Reset Password",
                $"New password for {_selectedAccount.PlayerName}:");
            if (input == null) return;

            if (PasswordHasher.IsValidPassword(input) == false)
            {
                MessageBox.Show(this,
                    $"The password needs to be between {PasswordHasher.PasswordMinLength} and {PasswordHasher.PasswordMaxLength} characters long.",
                    "Password not changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                _accountRepository.SetPassword(_selectedAccount.Id, input);
                RefreshSelectedLabel();
                ShowStatus($"{_selectedAccount.PlayerName}'s password has been changed. They can log in with it now (email: {_selectedAccount.Email}).");
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void ShowStatus(string message)
        {
            _statusLabel.ForeColor = Color.DarkGreen;
            _statusLabel.Text = message;
        }

        private void ShowError(Exception ex)
        {
            _statusLabel.ForeColor = Color.DarkRed;
            _statusLabel.Text = $"Something went wrong: {ex.Message}";
        }

        private class AccountOption
        {
            public DBAccount Account { get; }
            public AccountOption(DBAccount account) { Account = account; }
            public override string ToString() => $"{Account.PlayerName}  ({Account.Email})";
        }
    }
}
