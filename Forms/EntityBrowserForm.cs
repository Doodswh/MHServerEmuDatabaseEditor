using System.Linq;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using MHDBEditor.Data;
using MHDBEditor.Models;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Browse and edit the Avatar/TeamUp/Item/ControlledEntity rows belonging to one account.
    // Everything here operates on the plain relational columns (container/inventory/slot/proto
    // guid) plus raw ArchiveData bytes - see README for what is and isn't safe without the
    // game's Archive serializer.
    public class EntityBrowserForm : Form
    {
        private readonly EntityRepository _entityRepository;
        private readonly DBAccount _account;
        private readonly PrototypeResolver _prototypeResolver;
        private readonly ItemTemplateStore _itemTemplateStore;

        private readonly ComboBox _categoryCombo;
        private readonly ComboBox _containerCombo;
        private readonly TextBox _customContainerBox;
        private readonly DataGridView _grid;

        private List<DBEntity> _avatarsAndTeamUps = new();

        public EntityBrowserForm(EntityRepository entityRepository, DBAccount account, PrototypeResolver prototypeResolver, ItemTemplateStore itemTemplateStore)
        {
            _entityRepository = entityRepository;
            _account = account;
            _prototypeResolver = prototypeResolver;
            _itemTemplateStore = itemTemplateStore;

            Text = $"Entities - {account.PlayerName} (0x{account.Id:X})";
            Width = 1050;
            Height = 650;
            StartPosition = FormStartPosition.CenterParent;

            TableLayoutPanel topPanel = new()
            {
                Dock = DockStyle.Top,
                Height = 40,
                ColumnCount = 6,
                Padding = new Padding(6),
            };
            topPanel.Controls.Add(new Label { Text = "Table:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);

            _categoryCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
            _categoryCombo.Items.AddRange(Enum.GetNames(typeof(DBEntityCategory)));
            _categoryCombo.SelectedIndex = 2; // Item
            _categoryCombo.SelectedIndexChanged += (_, _) => RefreshGrid();
            topPanel.Controls.Add(_categoryCombo, 1, 0);

            topPanel.Controls.Add(new Label { Text = "Container:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);

            _containerCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            _containerCombo.SelectedIndexChanged += (_, _) => RefreshGrid();
            topPanel.Controls.Add(_containerCombo, 3, 0);

            _customContainerBox = new TextBox { Width = 160, PlaceholderText = "or custom container DbGuid" };
            topPanel.Controls.Add(_customContainerBox, 4, 0);

            Button goButton = new() { Text = "Go", Width = 50 };
            goButton.Click += (_, _) => RefreshGrid();
            topPanel.Controls.Add(goButton, 5, 0);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "DbGuid", HeaderText = "DbGuid", Width = 150, DefaultCellStyle = new DataGridViewCellStyle { Format = "X" } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ContainerDbGuid", HeaderText = "ContainerDbGuid", Width = 150, DefaultCellStyle = new DataGridViewCellStyle { Format = "X" } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "InventoryProtoGuid", HeaderText = "InventoryProtoGuid", Width = 150, DefaultCellStyle = new DataGridViewCellStyle { Format = "X" } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Slot", HeaderText = "Slot", Width = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "EntityProtoGuid", HeaderText = "EntityProtoGuid", Width = 150, DefaultCellStyle = new DataGridViewCellStyle { Format = "X" } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "ArchiveDataLength", HeaderText = "ArchiveData (bytes)", Width = 120 });
            _grid.CellFormatting += Grid_CellFormatting;

            FlowLayoutPanel buttons = new()
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.LeftToRight,
                Height = 44,
                Padding = new Padding(8),
            };
            Button refreshButton = new() { Text = "Refresh", Width = 90 };
            Button moveButton = new() { Text = "Move...", Width = 90 };
            Button duplicateButton = new() { Text = "Duplicate...", Width = 100 };
            Button deleteButton = new() { Text = "Delete", Width = 80 };
            Button rawDataButton = new() { Text = "View/Edit Raw Data...", Width = 160 };
            Button propertiesButton = new() { Text = "Edit Properties...", Width = 130 };
            Button transferButton = new() { Text = "Transfer To Another Account...", Width = 220 };
            Button saveTemplateButton = new() { Text = "Save As Template...", Width = 150 };

            refreshButton.Click += (_, _) => RefreshGrid();
            moveButton.Click += (_, _) => MoveSelected();
            duplicateButton.Click += (_, _) => DuplicateSelected();
            deleteButton.Click += (_, _) => DeleteSelected();
            rawDataButton.Click += (_, _) => EditRawDataSelected();
            propertiesButton.Click += (_, _) => EditPropertiesSelected();
            transferButton.Click += (_, _) => TransferSelected();
            saveTemplateButton.Click += (_, _) => SaveAsTemplateSelected();

            buttons.Controls.Add(refreshButton);
            buttons.Controls.Add(moveButton);
            buttons.Controls.Add(duplicateButton);
            buttons.Controls.Add(deleteButton);
            buttons.Controls.Add(rawDataButton);
            buttons.Controls.Add(propertiesButton);
            buttons.Controls.Add(transferButton);
            buttons.Controls.Add(saveTemplateButton);

            Controls.Add(_grid);
            Controls.Add(buttons);
            Controls.Add(topPanel);

            LoadContainers();
            RefreshGrid();
        }

        private void LoadContainers()
        {
            _avatarsAndTeamUps = _entityRepository.GetForContainer(DBEntityCategory.Avatar, _account.Id);
            _avatarsAndTeamUps.AddRange(_entityRepository.GetForContainer(DBEntityCategory.TeamUp, _account.Id));

            long previous = (_containerCombo.SelectedItem as ContainerOption)?.DbGuid ?? _account.Id;
            _containerCombo.Items.Clear();
            _containerCombo.Items.Add(new ContainerOption("Account stash", _account.Id));
            foreach (DBEntity entity in _avatarsAndTeamUps)
                _containerCombo.Items.Add(new ContainerOption($"Avatar/TeamUp 0x{entity.DbGuid:X} (proto 0x{entity.EntityProtoGuid:X})", entity.DbGuid));

            int keep = _containerCombo.Items.Cast<ContainerOption>().ToList().FindIndex(o => o.DbGuid == previous);
            _containerCombo.SelectedIndex = keep >= 0 ? keep : 0;
        }

        // After a change that can add or remove an avatar / team-up container.
        private void ReloadAfterChange()
        {
            LoadContainers();
            RefreshGrid();
        }

        private long GetSelectedContainerDbGuid()
        {
            if (string.IsNullOrWhiteSpace(_customContainerBox.Text) == false)
            {
                try { return GuidTextHelper.Parse(_customContainerBox.Text); }
                catch (Exception)
                {
                    MessageBox.Show(this, $"\"{_customContainerBox.Text}\" isn't a valid id. Use decimal or 0x hex.", "Invalid container id", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _customContainerBox.Clear();
                }
            }

            if (_containerCombo.SelectedItem is ContainerOption option)
                return option.DbGuid;

            return _account.Id;
        }

        private DBEntityCategory GetSelectedCategory()
            => Enum.Parse<DBEntityCategory>((string)_categoryCombo.SelectedItem);

        private void RefreshGrid()
        {
            if (_categoryCombo.SelectedItem == null) return;

            long containerDbGuid = GetSelectedContainerDbGuid();
            _grid.DataSource = _entityRepository.GetForContainer(GetSelectedCategory(), containerDbGuid);
        }

        private DBEntity GetSelectedEntity()
        {
            return _grid.CurrentRow?.DataBoundItem as DBEntity;
        }

        private void MoveSelected()
        {
            DBEntity entity = GetSelectedEntity();
            if (entity == null) return;

            var input = PromptForm.ShowMultiple(this, "Move Entity", new[]
            {
                ("New ContainerDbGuid (dec or 0x hex)", $"0x{entity.ContainerDbGuid:X}"),
                ("New InventoryProtoGuid (dec or 0x hex)", $"0x{entity.InventoryProtoGuid:X}"),
                ("New Slot", entity.Slot.ToString()),
            });
            if (input == null) return;

            try
            {
                long newContainer = GuidTextHelper.Parse(input["New ContainerDbGuid (dec or 0x hex)"]);
                long newInventoryProto = GuidTextHelper.Parse(input["New InventoryProtoGuid (dec or 0x hex)"]);
                uint newSlot = uint.Parse(input["New Slot"]);

                _entityRepository.Move(GetSelectedCategory(), entity.DbGuid, newContainer, newInventoryProto, newSlot);
                ReloadAfterChange();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed to move", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DuplicateSelected()
        {
            DBEntity entity = GetSelectedEntity();
            if (entity == null) return;

            var input = PromptForm.ShowMultiple(this, "Duplicate Entity", new[]
            {
                ("Destination ContainerDbGuid (dec or 0x hex)", $"0x{entity.ContainerDbGuid:X}"),
                ("Destination InventoryProtoGuid (dec or 0x hex)", $"0x{entity.InventoryProtoGuid:X}"),
                ("Destination Slot", (entity.Slot + 1).ToString()),
            });
            if (input == null) return;

            try
            {
                long destContainer = GuidTextHelper.Parse(input["Destination ContainerDbGuid (dec or 0x hex)"]);
                long destInventoryProto = GuidTextHelper.Parse(input["Destination InventoryProtoGuid (dec or 0x hex)"]);
                uint destSlot = uint.Parse(input["Destination Slot"]);

                DBEntity clone = _entityRepository.Duplicate(GetSelectedCategory(), entity.DbGuid, destContainer, destInventoryProto, destSlot);
                MessageBox.Show(this, $"Created new {GetSelectedCategory()} 0x{clone.DbGuid:X}.", "Duplicated", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ReloadAfterChange();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed to duplicate", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteSelected()
        {
            DBEntity entity = GetSelectedEntity();
            if (entity == null) return;

            if (MessageBox.Show(this, $"Permanently delete 0x{entity.DbGuid:X} from {GetSelectedCategory()}?", "Confirm delete",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            _entityRepository.Delete(GetSelectedCategory(), entity.DbGuid);
            ReloadAfterChange();
        }

        private void EditRawDataSelected()
        {
            DBEntity entity = GetSelectedEntity();
            if (entity == null) return;

            using RawDataForm form = new($"ArchiveData - 0x{entity.DbGuid:X}", entity.ArchiveData);
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                _entityRepository.SetRawArchiveData(GetSelectedCategory(), entity.DbGuid, form.Result);
                ReloadAfterChange();
            }
        }

        private void EditPropertiesSelected()
        {
            DBEntity entity = GetSelectedEntity();
            if (entity == null) return;

            DBEntityCategory category = GetSelectedCategory();

            using PropertiesEditorForm form = new(
                $"Properties - {category} 0x{entity.DbGuid:X}",
                entity.ArchiveData,
                _prototypeResolver,
                newData => _entityRepository.SetRawArchiveData(category, entity.DbGuid, newData));

            if (form.ShowDialog(this) == DialogResult.OK)
                ReloadAfterChange();
        }

        private void SaveAsTemplateSelected()
        {
            DBEntity entity = GetSelectedEntity();
            if (entity == null) return;

            if (GetSelectedCategory() != DBEntityCategory.Item)
            {
                MessageBox.Show(this, "Only items can be saved as templates for Simple Mode's \"Give Item\".",
                    "Not an item", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string name = PromptForm.ShowSingle(this, "Save As Template",
                "Friendly name (shown to whoever uses Simple Mode)", $"Item 0x{entity.EntityProtoGuid:X}");
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                _itemTemplateStore.Add(new ItemTemplate
                {
                    DisplayName = name.Trim(),
                    EntityProtoGuid = entity.EntityProtoGuid,
                    InventoryProtoGuid = entity.InventoryProtoGuid,
                    ArchiveData = entity.ArchiveData,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't save the template file: {ex.Message}", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            MessageBox.Show(this, $"Saved as a template named \"{name.Trim()}\". It'll show up in Simple Mode's \"Give Item\" list.",
                "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void TransferSelected()
        {
            DBEntity entity = GetSelectedEntity();
            if (entity == null) return;

            var input = PromptForm.ShowMultiple(this, "Transfer To Another Account", new[]
            {
                ("Destination Account Id (dec or 0x hex)", ""),
                ("Destination InventoryProtoGuid (dec or 0x hex)", $"0x{entity.InventoryProtoGuid:X}"),
                ("Destination Slot", "0"),
            });
            if (input == null) return;

            try
            {
                long destAccountId = GuidTextHelper.Parse(input["Destination Account Id (dec or 0x hex)"]);
                long destInventoryProto = GuidTextHelper.Parse(input["Destination InventoryProtoGuid (dec or 0x hex)"]);
                uint destSlot = uint.Parse(input["Destination Slot"]);

                _entityRepository.Move(GetSelectedCategory(), entity.DbGuid, destAccountId, destInventoryProto, destSlot);
                MessageBox.Show(this,
                    "Transferred. Note: this only works cleanly for account-level containers (e.g. stash) - " +
                    "avatar-owned items need the destination avatar's DbGuid, not the account Id.",
                    "Transferred", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ReloadAfterChange();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed to transfer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // When a prototype name list has been loaded (Tools > Load Prototype Name List...),
        // show the resolved path alongside the raw guid for the two proto-guid columns.
        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (_prototypeResolver == null || _prototypeResolver.Count == 0) return;
            if (e.Value is not long guidValue) return;

            string columnName = _grid.Columns[e.ColumnIndex].DataPropertyName;
            if (columnName != "EntityProtoGuid" && columnName != "InventoryProtoGuid") return;

            if (_prototypeResolver.TryResolve(guidValue, out string path))
            {
                e.Value = $"0x{guidValue:X} ({path})";
                e.FormattingApplied = true;
            }
        }

        private class ContainerOption
        {
            public string Label { get; }
            public long DbGuid { get; }

            public ContainerOption(string label, long dbGuid)
            {
                Label = label;
                DbGuid = dbGuid;
            }

            public override string ToString() => Label;
        }
    }
}
