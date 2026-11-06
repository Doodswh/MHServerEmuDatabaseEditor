using System;
using System.IO;
using System.Windows.Forms;
using MHDBEditor.Data;
using MHDBEditor.Models;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    public class MainForm : Form
    {
        private DbConnectionManager _connectionManager;
        private AccountRepository _accountRepository;
        private EntityRepository _entityRepository;
        private GuildRepository _guildRepository;
        private HardwareBanRepository _hardwareBanRepository;
        // Fork-only schema bits, shown only when the open database has them.
        private DataGridViewColumn _machineIdColumn, _totalKillsColumn;
        private Button _clearMachineIdButton;
        private TabPage _hwidTab;
        private AccountSnapshotService _accountSnapshotService;

        private readonly PrototypeResolver _prototypeResolver = new();
        private readonly ItemTemplateStore _itemTemplateStore = new();
        private readonly SimpleModePanel _simpleModePanel;

        private readonly Label _statusLabel;
        private readonly TabControl _tabs;
        private readonly ToolStripMenuItem _backupMenuItem;
        private readonly ToolStripMenuItem _healthCheckMenuItem;
        private readonly ToolStripMenuItem _searchMenuItem;

        private readonly TextBox _accountSearchBox;
        private readonly DataGridView _accountGrid;

        private readonly DataGridView _guildGrid;

        private readonly DataGridView _hwidGrid;

        public MainForm()
        {
            Text = "MHDBEditor";
            Width = 1100;
            Height = 700;
            StartPosition = FormStartPosition.CenterScreen;

            _itemTemplateStore.Load();
            if (_itemTemplateStore.LoadError != null)
                Shown += (_, _) => MessageBox.Show(this, _itemTemplateStore.LoadError, "Item templates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _simpleModePanel = new SimpleModePanel(_prototypeResolver, _itemTemplateStore);

            // --- Menu ---
            MenuStrip menu = new();
            ToolStripMenuItem fileMenu = new("File");
            ToolStripMenuItem openItem = new("Open Database...");
            _backupMenuItem = new ToolStripMenuItem("Create Backup Now") { Enabled = false };
            ToolStripMenuItem exitItem = new("Exit");

            openItem.Click += (_, _) => OpenDatabase();
            _backupMenuItem.Click += (_, _) => CreateBackup();
            exitItem.Click += (_, _) => Close();

            fileMenu.DropDownItems.Add(openItem);
            fileMenu.DropDownItems.Add(_backupMenuItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(exitItem);
            menu.Items.Add(fileMenu);

            ToolStripMenuItem toolsMenu = new("Tools");
            ToolStripMenuItem computeGuidItem = new("Compute Prototype DataRef...");
            ToolStripMenuItem loadResolverItem = new("Load Game Data (names)...");
            computeGuidItem.Click += (_, _) => { using PrototypeGuidCalculatorForm f = new(); f.ShowDialog(this); };
            loadResolverItem.Click += (_, _) => LoadPrototypeResolver();
            toolsMenu.DropDownItems.Add(computeGuidItem);
            toolsMenu.DropDownItems.Add(loadResolverItem);
            _healthCheckMenuItem = new ToolStripMenuItem("Database Health Check...") { Enabled = false };
            _healthCheckMenuItem.Click += (_, _) => { using HealthCheckForm f = new(_connectionManager, _prototypeResolver); f.ShowDialog(this); };
            _searchMenuItem = new ToolStripMenuItem("Search && Bulk Actions...") { Enabled = false };
            _searchMenuItem.Click += (_, _) => { using SearchForm f = new(_connectionManager, _prototypeResolver, _itemTemplateStore); f.ShowDialog(this); LoadAccounts(null); };
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(_searchMenuItem);
            toolsMenu.DropDownItems.Add(_healthCheckMenuItem);
            menu.Items.Add(toolsMenu);

            MainMenuStrip = menu;

            _statusLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Text = "No database loaded. File > Open Database...",
                Padding = new Padding(8, 4, 0, 0),
            };

            _tabs = new TabControl { Dock = DockStyle.Fill };

            // ================= Accounts tab =================
            TabPage accountsTab = new("Accounts");
            TableLayoutPanel accountsTop = new() { Dock = DockStyle.Top, Height = 36, ColumnCount = 3, Padding = new Padding(6) };
            _accountSearchBox = new TextBox { Width = 250, PlaceholderText = "search by email / name / id" };
            Button accountSearchButton = new() { Text = "Search", Width = 80 };
            Button accountShowAllButton = new() { Text = "Show All", Width = 80 };
            accountSearchButton.Click += (_, _) => LoadAccounts(_accountSearchBox.Text);
            accountShowAllButton.Click += (_, _) => LoadAccounts(null);
            accountsTop.Controls.Add(_accountSearchBox, 0, 0);
            accountsTop.Controls.Add(accountSearchButton, 1, 0);
            accountsTop.Controls.Add(accountShowAllButton, 2, 0);

            _accountGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
            };
            _accountGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "Id", Width = 150, DefaultCellStyle = new DataGridViewCellStyle { Format = "X" } });
            _accountGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Email", HeaderText = "Email", Width = 200 });
            _accountGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "PlayerName", HeaderText = "PlayerName", Width = 150 });
            _accountGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "UserLevel", HeaderText = "UserLevel", Width = 90 });
            _accountGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Flags", HeaderText = "Flags", Width = 220 });
            _machineIdColumn = new DataGridViewTextBoxColumn { DataPropertyName = "LastKnownMachineId", HeaderText = "LastKnownMachineId", Width = 150 };
            _accountGrid.Columns.Add(_machineIdColumn);

            FlowLayoutPanel accountButtons = new() { Dock = DockStyle.Bottom, Height = 116, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
            Button toggleBanButton = new() { Text = "Toggle Ban", Width = 100 };
            Button toggleWhitelistButton = new() { Text = "Toggle Whitelist", Width = 120 };
            Button toggleBypassButton = new() { Text = "Toggle Bypass Queue", Width = 140 };
            Button toggleDevFlagButton = new() { Text = "Toggle Dev Flag", Width = 110 };
            Button editFlagsRawButton = new() { Text = "Edit Flags (raw)...", Width = 140 };
            Button setUserLevelButton = new() { Text = "Set User Level...", Width = 130 };
            Button renameButton = new() { Text = "Rename...", Width = 90 };
            Button changeEmailButton = new() { Text = "Change Email...", Width = 120 };
            Button resetPasswordButton = new() { Text = "Reset Password...", Width = 130 };
            Button clearHwidButton = new() { Text = "Clear Machine Id", Width = 130 };
            _clearMachineIdButton = clearHwidButton;
            Button playerFieldsButton = new() { Text = "Player Fields...", Width = 120 };
            Button changeLocationButton = new() { Text = "Change Location...", Width = 130 };
            Button playerPropertiesButton = new() { Text = "Edit Player Properties...", Width = 190 };
            Button playerDataButton = new() { Text = "Player Data (missions, stash...)", Width = 210 };
            Button manageEntitiesButton = new() { Text = "Manage Items/Entities...", Width = 190 };
            Button exportAccountButton = new() { Text = "Export Account...", Width = 140 };
            Button importAccountButton = new() { Text = "Import Account...", Width = 140 };
            Button deleteAccountButton = new() { Text = "Delete Account", Width = 120 };

            toggleBanButton.Click += (_, _) => ToggleFlag(AccountFlags.IsBanned);
            toggleWhitelistButton.Click += (_, _) => ToggleFlag(AccountFlags.IsWhitelisted);
            toggleBypassButton.Click += (_, _) => ToggleFlag(AccountFlags.BypassLoginQueue);
            toggleDevFlagButton.Click += (_, _) => ToggleFlag(AccountFlags.IsDev);
            editFlagsRawButton.Click += (_, _) => EditFlagsRaw();
            setUserLevelButton.Click += (_, _) => SetUserLevelPrompt();
            renameButton.Click += (_, _) => RenameSelected();
            changeEmailButton.Click += (_, _) => ChangeEmailSelected();
            resetPasswordButton.Click += (_, _) => ResetPasswordSelected();
            clearHwidButton.Click += (_, _) => ClearMachineIdSelected();
            playerFieldsButton.Click += (_, _) => EditPlayerFieldsSelected();
            changeLocationButton.Click += (_, _) => ChangeLocationSelected();
            playerPropertiesButton.Click += (_, _) => EditPlayerPropertiesSelected();
            playerDataButton.Click += (_, _) => EditPlayerDataSelected();
            manageEntitiesButton.Click += (_, _) => ManageEntitiesSelected();
            exportAccountButton.Click += (_, _) => ExportAccountSelected();
            importAccountButton.Click += (_, _) => ImportAccount();
            deleteAccountButton.Click += (_, _) => DeleteAccountSelected();

            accountButtons.Controls.AddRange(new Control[]
            {
                toggleBanButton, toggleWhitelistButton, toggleBypassButton, toggleDevFlagButton, editFlagsRawButton,
                setUserLevelButton, renameButton, changeEmailButton, resetPasswordButton, clearHwidButton, playerFieldsButton, changeLocationButton,
                playerPropertiesButton, playerDataButton, manageEntitiesButton, exportAccountButton, importAccountButton, deleteAccountButton,
            });

            accountsTab.Controls.Add(_accountGrid);
            accountsTab.Controls.Add(accountButtons);
            accountsTab.Controls.Add(accountsTop);

            // ================= Guilds tab =================
            TabPage guildsTab = new("Guilds");
            _guildGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
            };
            _guildGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "Id", Width = 100 });
            _guildGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Name", Width = 200 });
            _guildGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Motd", HeaderText = "Motd", Width = 300 });
            _totalKillsColumn = new DataGridViewTextBoxColumn { DataPropertyName = "TotalKills", HeaderText = "TotalKills", Width = 100 };
            _guildGrid.Columns.Add(_totalKillsColumn);
            _guildGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "MemberCount", HeaderText = "Members", Width = 80 });

            FlowLayoutPanel guildButtons = new() { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
            Button guildRefreshButton = new() { Text = "Refresh", Width = 90 };
            Button guildEditButton = new() { Text = "Edit Name/Motd...", Width = 150 };
            Button guildMembersButton = new() { Text = "Manage Members...", Width = 150 };
            Button guildDeleteButton = new() { Text = "Delete Guild", Width = 100 };

            guildRefreshButton.Click += (_, _) => LoadGuilds();
            guildEditButton.Click += (_, _) => EditGuildSelected();
            guildMembersButton.Click += (_, _) => ManageGuildMembersSelected();
            guildDeleteButton.Click += (_, _) => DeleteGuildSelected();

            guildButtons.Controls.AddRange(new Control[] { guildRefreshButton, guildEditButton, guildMembersButton, guildDeleteButton });

            guildsTab.Controls.Add(_guildGrid);
            guildsTab.Controls.Add(guildButtons);

            // ================= HWID Bans tab =================
            TabPage hwidTab = new("HWID Bans");
            _hwidTab = hwidTab;
            _hwidGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
            };
            _hwidGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Hwid", HeaderText = "Hwid", Width = 250 });
            _hwidGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "BannedBy", HeaderText = "BannedBy", Width = 150 });
            _hwidGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Reason", HeaderText = "Reason", Width = 250 });
            _hwidGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "BanDate", HeaderText = "BanDate", Width = 150 });

            FlowLayoutPanel hwidButtons = new() { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
            Button hwidRefreshButton = new() { Text = "Refresh", Width = 90 };
            Button hwidBanButton = new() { Text = "Ban HWID...", Width = 110 };
            Button hwidUnbanButton = new() { Text = "Unban Selected", Width = 130 };

            hwidRefreshButton.Click += (_, _) => LoadHardwareBans();
            hwidBanButton.Click += (_, _) => BanHwidPrompt();
            hwidUnbanButton.Click += (_, _) => UnbanSelectedHwid();

            hwidButtons.Controls.AddRange(new Control[] { hwidRefreshButton, hwidBanButton, hwidUnbanButton });

            hwidTab.Controls.Add(_hwidGrid);
            hwidTab.Controls.Add(hwidButtons);

            TabPage simpleModeTab = new("Simple Mode");
            simpleModeTab.Controls.Add(_simpleModePanel);

            _tabs.TabPages.Add(simpleModeTab);
            _tabs.TabPages.Add(accountsTab);
            _tabs.TabPages.Add(guildsTab);
            _tabs.TabPages.Add(hwidTab);
            _tabs.SelectedIndex = 0;
            Controls.Add(_tabs);
            Controls.Add(_statusLabel);
            Controls.Add(menu);

            LoadRememberedGameData();
            UpdateStatusLabel();
        }

        // ================= File menu =================

        private void OpenDatabase()
        {
            using OpenFileDialog dialog = new()
            {
                Filter = "SQLite database (*.db;*.sqlite)|*.db;*.sqlite|All files (*.*)|*.*",
                Title = "Open MHServerEmu account database",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                _connectionManager = new DbConnectionManager(dialog.FileName);
                _accountRepository = new AccountRepository(_connectionManager);
                _entityRepository = new EntityRepository(_connectionManager);
                _guildRepository = new GuildRepository(_connectionManager);
                _hardwareBanRepository = new HardwareBanRepository(_connectionManager);
                _accountSnapshotService = new AccountSnapshotService(_connectionManager, _entityRepository);

                ApplySchemaFeatures();
                _backupMenuItem.Enabled = true;
                _healthCheckMenuItem.Enabled = true;
                _searchMenuItem.Enabled = true;

                if (_prototypeResolver.Count == 0)
                    TryAutoLoadGameData(dialog.FileName);
                UpdateStatusLabel();

                LoadAccounts(null);
                LoadGuilds();
                LoadHardwareBans();
                _simpleModePanel.Configure(_accountRepository, _entityRepository);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed to open database", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Stock MHServerEmu has no machine id / HWID ban table (and no server has Guild.TotalKills
        // yet); hide those bits so nothing tries to read or write columns that aren't there.
        private void ApplySchemaFeatures()
        {
            bool machineId = _connectionManager.HasMachineId;
            // The machine id column only exists in the COA build, so it also tells us which
            // player-save layout to try first (archive version 10 means different things in each).
            MHDBEditor.ArchiveFormat.PlayerTailCodec.PreferredLayout = machineId ? MHDBEditor.ArchiveFormat.ServerLayout.Coa : MHDBEditor.ArchiveFormat.ServerLayout.Stock;
            _machineIdColumn.Visible = machineId;
            _clearMachineIdButton.Visible = machineId;
            _totalKillsColumn.Visible = _connectionManager.HasColumn("Guild", "TotalKills");

            bool bans = _connectionManager.HasHardwareBans;
            if (bans && _tabs.TabPages.Contains(_hwidTab) == false) _tabs.TabPages.Add(_hwidTab);
            else if (bans == false && _tabs.TabPages.Contains(_hwidTab)) _tabs.TabPages.Remove(_hwidTab);
        }

        private void CreateBackup()
        {
            if (_connectionManager == null) return;
            try
            {
                string path = _connectionManager.CreateBackup();
                MessageBox.Show(this, $"Backup written to:\n{path}", "Backup created", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Backup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool RequireDatabase()
        {
            if (_connectionManager != null) return true;
            MessageBox.Show(this, "Open a database first (File > Open Database...).", "No database loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Where the last successfully loaded game data / name list lives, so names are there
        // from the moment the app opens.
        private static readonly string GameDataPathFile = Path.Combine(AppContext.BaseDirectory, "game_data_path.txt");

        private void LoadPrototypeResolver()
        {
            using OpenFileDialog dialog = new()
            {
                Filter = "Game data (Calligraphy.sip)|Calligraphy.sip|Name lists (*.tsv;*.txt;*.csv)|*.tsv;*.txt;*.csv|All files (*.*)|*.*",
                Title = @"Pick the server's Data\Game\Calligraphy.sip (or a guid/name list)",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            if (TryLoadGameData(dialog.FileName, showErrors: true))
                MessageBox.Show(this, $"Loaded names for {_prototypeResolver.Count:N0} prototypes and {_prototypeResolver.AssetCount:N0} assets.\n" +
                    "This file will be loaded automatically next time.", "Loaded", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private bool TryLoadGameData(string path, bool showErrors)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                _prototypeResolver.LoadFromFile(path);
                UpdateStatusLabel();
            }
            catch (Exception ex)
            {
                if (showErrors)
                    MessageBox.Show(this, ex.Message, "Failed to load", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            // Remembering the path is only a convenience - a read-only install folder
            // shouldn't turn a successful load into an error.
            try { File.WriteAllText(GameDataPathFile, path); }
            catch (Exception) { }
            return true;
        }

        private void LoadRememberedGameData()
        {
            try
            {
                if (File.Exists(GameDataPathFile))
                {
                    string path = File.ReadAllText(GameDataPathFile).Trim();
                    if (File.Exists(path))
                        TryLoadGameData(path, showErrors: false);
                }
            }
            catch { /* names are a convenience - never block startup on them */ }
        }

        // The server keeps Account.db in Data\ and game data in Data\Game\, so look next to
        // the database (and a couple of folders up) before asking the user for anything.
        private void TryAutoLoadGameData(string dbPath)
        {
            DirectoryInfo dir = new FileInfo(dbPath).Directory;
            for (int i = 0; i < 3 && dir != null; i++, dir = dir.Parent)
            {
                foreach (string candidate in new[] { Path.Combine(dir.FullName, "Game", "Calligraphy.sip"), Path.Combine(dir.FullName, "Data", "Game", "Calligraphy.sip") })
                {
                    if (File.Exists(candidate) && TryLoadGameData(candidate, showErrors: false))
                        return;
                }
            }
        }

        private void UpdateStatusLabel()
        {
            string db = _connectionManager != null ? $"Database: {_connectionManager.DbFilePath}" : "No database loaded. File > Open Database...";
            string names = _prototypeResolver.Count > 0
                ? $"Names: {_prototypeResolver.Count:N0} loaded from {Path.GetFileName(_prototypeResolver.LoadedFrom)}"
                : "Names: not loaded (Tools > Load Game Data) - ids will show as hex";
            _statusLabel.Text = $"{db}    |    {names}";
        }

        // ================= Accounts tab =================

        private void LoadAccounts(string searchTerm)
        {
            if (RequireDatabase() == false) return;
            _accountGrid.DataSource = _accountRepository.Search(searchTerm);
        }

        private DBAccount GetSelectedAccount()
        {
            return _accountGrid.CurrentRow?.DataBoundItem as DBAccount;
        }

        private void ToggleFlag(AccountFlags flag)
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            AccountFlags newFlags = account.HasFlag(flag) ? account.Flags & ~flag : account.Flags | flag;
            _accountRepository.UpdateFlags(account.Id, newFlags);
            LoadAccounts(_accountSearchBox.Text);
        }

        private void EditFlagsRaw()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            string input = PromptForm.ShowSingle(this, "Edit Flags (raw)",
                "Flags value (integer). Bits: 1=Banned,2=Archived,4=PasswordExpired,16=Whitelisted,32=BypassQueue,64=Dev",
                ((int)account.Flags).ToString());
            if (input == null) return;

            if (int.TryParse(input.Trim(), out int value) == false)
            {
                MessageBox.Show(this, "Must be an integer.", "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _accountRepository.UpdateFlags(account.Id, (AccountFlags)value);
            LoadAccounts(_accountSearchBox.Text);
        }

        private void SetUserLevelPrompt()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            string input = PromptForm.ShowSingle(this, "Set User Level",
                "User Level (User / Moderator / Admin / Dev)", account.UserLevel.ToString());
            if (input == null) return;

            if (Enum.TryParse(input.Trim(), true, out AccountUserLevel level) == false)
            {
                MessageBox.Show(this, "Must be one of: User, Moderator, Admin, Dev", "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _accountRepository.UpdateUserLevel(account.Id, level);
            LoadAccounts(_accountSearchBox.Text);
        }

        private void RenameSelected()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            string input = PromptForm.ShowSingle(this, "Rename Player", "New PlayerName", account.PlayerName);
            if (string.IsNullOrWhiteSpace(input)) return;

            _accountRepository.Rename(account.Id, input.Trim());
            LoadAccounts(_accountSearchBox.Text);
        }

        private void ChangeEmailSelected()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            string input = PromptForm.ShowSingle(this, "Change Email", "New Email", account.Email);
            if (string.IsNullOrWhiteSpace(input)) return;

            _accountRepository.UpdateEmail(account.Id, input.Trim());
            LoadAccounts(_accountSearchBox.Text);
        }

        private void ResetPasswordSelected()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            string input = PromptForm.ShowSingle(this, "Reset Password", "New Password");
            if (input == null) return;

            if (PasswordHasher.IsValidPassword(input) == false)
            {
                MessageBox.Show(this,
                    $"Password must be {PasswordHasher.PasswordMinLength}-{PasswordHasher.PasswordMaxLength} characters (server's AccountManager rule).",
                    "Invalid password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _accountRepository.SetPassword(account.Id, input);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Password not changed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            LoadAccounts(_accountSearchBox.Text);
            MessageBox.Show(this, $"Password for {account.Email} has been reset.", "Password reset", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ClearMachineIdSelected()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            _accountRepository.ClearMachineId(account.Id);
            LoadAccounts(_accountSearchBox.Text);
        }

        private void EditPlayerFieldsSelected()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            DBPlayer player = _accountRepository.GetPlayer(account.Id) ?? new DBPlayer { DbGuid = account.Id };
            using PlayerFieldsForm form = new(_accountRepository, player, _prototypeResolver);
            form.ShowDialog(this);
        }

        private void ChangeLocationSelected()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            if (_prototypeResolver.HasGameData == false)
            {
                MessageBox.Show(this, "Load game data first (Tools > Load Game Data) - the list of places comes from the game data.", "Game data needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DBPlayer player = _accountRepository.GetPlayer(account.Id);
            if (player == null)
            {
                MessageBox.Show(this, "This account has no Player row yet (never logged in) - it will start at the server's default location.", "Nothing to change", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool canSetLastTown = StartLocationPickerForm.CanSetLastTown(_prototypeResolver, player.ArchiveData);
            using StartLocationPickerForm picker = new(_prototypeResolver, player.StartTarget, canSetLastTown);
            if (picker.ShowDialog(this) != DialogResult.OK) return;

            PrototypeResolver.StartLocation location = picker.Result;
            try
            {
                byte[] newArchive = picker.AlsoSetLastTown ? StartLocationPickerForm.WithLastTown(_prototypeResolver, player.ArchiveData, location) : null;

                player.StartTarget = unchecked((long)location.DataRef);
                _accountRepository.UpdatePlayerFields(player);
                if (newArchive != null)
                    _accountRepository.SetPlayerArchiveData(account.Id, newArchive);

                MessageBox.Show(this,
                    $"{account.PlayerName} will log in at {location}." + (newArchive != null ? $"\r\nLast town set to {location.RegionName}." : ""),
                    "Location changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Location not changed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void EditPlayerDataSelected()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            if (_prototypeResolver.HasGameData == false)
            {
                MessageBox.Show(this, "Load game data first (Tools > Load Game Data) - mission, stash tab and difficulty names come from it.", "Game data needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using PlayerDataForm form = PlayerDataForm.TryCreate(this, _accountRepository, _entityRepository, _prototypeResolver, account);
            form?.ShowDialog(this);
        }

        private void EditPlayerPropertiesSelected()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            DBPlayer player = _accountRepository.GetPlayer(account.Id);
            if (player?.ArchiveData == null || player.ArchiveData.Length == 0)
            {
                MessageBox.Show(this, "This player has no ArchiveData to decode yet.", "Nothing to edit", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using PropertiesEditorForm form = new(
                $"Player Properties - {account.PlayerName}",
                player.ArchiveData,
                _prototypeResolver,
                newData => _accountRepository.SetPlayerArchiveData(account.Id, newData));

            form.ShowDialog(this);
        }

        private void ManageEntitiesSelected()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            using EntityBrowserForm form = new(_entityRepository, account, _prototypeResolver, _itemTemplateStore);
            form.ShowDialog(this);
        }

        private void ExportAccountSelected()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            try
            {
                DBPlayer player = _accountRepository.GetPlayer(account.Id) ?? new DBPlayer { DbGuid = account.Id };
                DBAccountSnapshot snapshot = _accountSnapshotService.BuildSnapshot(account, player);

                using SaveFileDialog dialog = new()
                {
                    Filter = "Account save (*.sav)|*.sav|All files (*.*)|*.*",
                    FileName = $"{account.PlayerName}_0x{account.Id:X}.sav",
                };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                AccountSnapshotBinarySerializer.SerializeToFile(dialog.FileName, snapshot);
                MessageBox.Show(this, "Account exported.", "Exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ImportAccount()
        {
            if (RequireDatabase() == false) return;

            using OpenFileDialog openDialog = new() { Filter = "Account save (*.sav)|*.sav|All files (*.*)|*.*" };
            if (openDialog.ShowDialog(this) != DialogResult.OK) return;

            DBAccountSnapshot snapshot;
            try
            {
                snapshot = AccountSnapshotBinarySerializer.DeserializeFromFile(openDialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed to read file", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            DialogResult cloneChoice = MessageBox.Show(this,
                $"File contains account '{snapshot.Account.PlayerName}' (0x{snapshot.Account.Id:X}).\n\n" +
                "Yes = import as a NEW account with a freshly generated Id (safe, recommended)\n" +
                "No = restore/overwrite using the Id stored in the file (replaces that account if it already exists)\n" +
                "Cancel = abort",
                "Import Account", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (cloneChoice == DialogResult.Cancel) return;

            try
            {
                if (cloneChoice == DialogResult.Yes)
                {
                    var input = PromptForm.ShowMultiple(this, "New Account Details", new[]
                    {
                        ("New Account Id (dec or 0x hex)", $"0x{IdGenerator.GenerateLike(snapshot.Account.Id):X}"),
                        ("Email", snapshot.Account.Email),
                        ("PlayerName", snapshot.Account.PlayerName + "_copy"),
                    });
                    if (input == null) return;

                    long newId = GuidTextHelper.Parse(input["New Account Id (dec or 0x hex)"]);
                    DBAccountSnapshot clone = _accountSnapshotService.CloneWithNewIds(snapshot, newId, input["Email"], input["PlayerName"]);
                    _accountSnapshotService.ApplySnapshot(clone);
                }
                else
                {
                    _accountSnapshotService.ApplySnapshot(snapshot);
                }

                MessageBox.Show(this, "Account imported.", "Imported", MessageBoxButtons.OK, MessageBoxIcon.Information);
                LoadAccounts(_accountSearchBox.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteAccountSelected()
        {
            DBAccount account = GetSelectedAccount();
            if (account == null) return;

            DialogResult confirm = MessageBox.Show(this,
                $"Permanently delete account {account} and ALL of its avatars, team-ups, items and controlled entities?\n" +
                "This cannot be undone. Consider creating a backup first (File > Create Backup Now).",
                "Confirm account deletion", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            try
            {
                _accountRepository.DeleteAccountCascade(account.Id);
                LoadAccounts(_accountSearchBox.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed to delete account", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ================= Guilds tab =================

        private void LoadGuilds()
        {
            if (RequireDatabase() == false) return;
            _guildGrid.DataSource = _guildRepository.GetAll();
        }

        private DBGuild GetSelectedGuild()
        {
            return _guildGrid.CurrentRow?.DataBoundItem as DBGuild;
        }

        private void EditGuildSelected()
        {
            DBGuild guild = GetSelectedGuild();
            if (guild == null) return;

            var input = PromptForm.ShowMultiple(this, "Edit Guild", new[]
            {
                ("Name", guild.Name),
                ("Motd", guild.Motd),
            });
            if (input == null) return;

            _guildRepository.UpdateNameAndMotd(guild.Id, input["Name"], input["Motd"]);
            LoadGuilds();
        }

        private void ManageGuildMembersSelected()
        {
            DBGuild guild = GetSelectedGuild();
            if (guild == null) return;

            using GuildMembersForm form = new(_guildRepository, guild);
            form.ShowDialog(this);
            LoadGuilds();
        }

        private void DeleteGuildSelected()
        {
            DBGuild guild = GetSelectedGuild();
            if (guild == null) return;

            if (MessageBox.Show(this, $"Permanently delete guild '{guild.Name}' and remove all its members?", "Confirm delete",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            _guildRepository.Delete(guild.Id);
            LoadGuilds();
        }

        // ================= HWID Bans tab =================

        private void LoadHardwareBans()
        {
            if (RequireDatabase() == false) return;
            _hwidGrid.DataSource = _hardwareBanRepository.GetAll();
        }

        private BannedHardwareEntry GetSelectedHwid()
        {
            return _hwidGrid.CurrentRow?.DataBoundItem as BannedHardwareEntry;
        }

        private void BanHwidPrompt()
        {
            if (RequireDatabase() == false) return;

            var input = PromptForm.ShowMultiple(this, "Ban Hardware Id", new[]
            {
                ("Hwid", ""),
                ("Banned By", Environment.UserName),
                ("Reason", ""),
            });
            if (input == null || string.IsNullOrWhiteSpace(input["Hwid"])) return;

            _hardwareBanRepository.Ban(input["Hwid"].Trim(), input["Banned By"], input["Reason"]);
            LoadHardwareBans();
        }

        private void UnbanSelectedHwid()
        {
            BannedHardwareEntry entry = GetSelectedHwid();
            if (entry == null) return;

            _hardwareBanRepository.Unban(entry.Hwid);
            LoadHardwareBans();
        }
    }
}
