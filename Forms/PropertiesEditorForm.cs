using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using MHDBEditor.ArchiveFormat;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Decodes and edits the Properties collection inside any Database-archive ArchiveData blob
    // - a DBEntity row (Avatar/TeamUp/Item/ControlledEntity) or DBPlayer.ArchiveData, both of
    // which start with Entity.Serialize() = Properties first. For items specifically, also
    // attempts to decode ItemSpec (rarity/level/credits/seed/equippableBy/affixes) - shown as a
    // second tab only when that decode fully succeeds. Everything else in the blob
    // (ConditionCollection, and ItemSpec itself when it isn't decoded) is preserved
    // byte-for-byte and never shown here.
    //
    // Decoupled from where the bytes come from: caller supplies the current ArchiveData and a
    // callback that persists a rebuilt one, so this same form works for DBEntity rows and the
    // Player row alike.
    public class PropertiesEditorForm : Form
    {
        private readonly PrototypeResolver _prototypeResolver;
        private readonly Action<byte[]> _onSave;

        private readonly byte[] _originalData;
        private ParsedEntityArchive _parsed;

        private readonly TabControl _tabs;
        private readonly Label _infoLabel;
        private readonly DataGridView _propertiesGrid;

        // Item Spec tab controls (only populated/shown when _parsed.ItemSpec != null)
        private TabPage _itemSpecTab;
        private TextBox _itemProtoGuidBox, _rarityProtoGuidBox, _itemLevelBox, _creditsBox, _seedBox, _equippableByBox;
        private DataGridView _affixGrid;
        private DataGridView _builtInGrid;
        private Label _builtInLabel;
        private Label _affixCheckLabel;

        private const string PropNameColumn = "PropName", PropParamsColumn = "PropParams", PropValueColumn = "PropValue";
        private const string AffixColumn = "Affix", ScopeColumn = "Scope", RawColumn = "Raw", RawScopeColumn = "RawScope", GrantsColumn = "Grants", StatusColumn = "Status";

        // Where the picker looks first for each kind of field.
        private const string ItemsPrefix = "Entity/Items/";
        private const string RarityPrefix = "Entity/Items/Rarity/";
        private const string AvatarsPrefix = "Entity/Characters/Avatars/";
        private const string AffixesPrefix = "Entity/Items/Affixes/";

        public PropertiesEditorForm(string title, byte[] archiveData, PrototypeResolver prototypeResolver, Action<byte[]> onSave)
        {
            _originalData = archiveData;
            _prototypeResolver = prototypeResolver ?? new PrototypeResolver();
            _onSave = onSave;

            Text = title;
            Width = 1200;
            Height = 800;
            StartPosition = FormStartPosition.CenterParent;

            _infoLabel = new Label { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8), Text = "Parsing..." };
            _tabs = new TabControl { Dock = DockStyle.Fill };

            // --- Properties tab ---
            TabPage propertiesTab = new("Properties");
            _propertiesGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                ShowCellToolTips = true,
            };
            _propertiesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = PropNameColumn, DataPropertyName = "PrototypeGuidHex", HeaderText = "Property", Width = 300, ReadOnly = true });
            _propertiesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = PropParamsColumn, DataPropertyName = "ParamsDisplay", HeaderText = "For (params)", Width = 300, ReadOnly = true });
            _propertiesGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "DataType", HeaderText = "Type", Width = 80, ReadOnly = true });
            _propertiesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = PropValueColumn, DataPropertyName = "ValueText", HeaderText = "Value (editable)", Width = 280, ReadOnly = false });
            _propertiesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = RawColumn, DataPropertyName = "PrototypeGuidHex", HeaderText = "Raw Guid", Width = 160, ReadOnly = true, Visible = false });
            _propertiesGrid.CellFormatting += PropertiesGrid_CellFormatting;
            _propertiesGrid.CellParsing += PropertiesGrid_CellParsing;
            _propertiesGrid.CellToolTipTextNeeded += PropertiesGrid_CellToolTipTextNeeded;
            _propertiesGrid.CellDoubleClick += PropertiesGrid_CellDoubleClick;
            _propertiesGrid.DataError += Grid_DataError;
            propertiesTab.Controls.Add(_propertiesGrid);
            _tabs.TabPages.Add(propertiesTab);

            FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
            Button saveButton = new() { Text = "Save", Width = 100 };
            Button cancelButton = new() { Text = "Close Without Saving", Width = 170 };
            Button addPropertyButton = new() { Text = "Add Property...", Width = 140 };
            Button removePropertyButton = new() { Text = "Remove Property", Width = 140 };
            CheckBox showRawBox = new() { Text = "Show raw guids", AutoSize = true, Padding = new Padding(12, 6, 0, 0) };
            saveButton.Click += (_, _) => Save();
            removePropertyButton.Click += (_, _) => RemoveProperty();
            showRawBox.CheckedChanged += (_, _) => SetRawColumnsVisible(showRawBox.Checked);
            cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            addPropertyButton.Click += (_, _) => AddProperty();
            buttons.Controls.Add(saveButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(addPropertyButton);
            buttons.Controls.Add(removePropertyButton);
            buttons.Controls.Add(showRawBox);

            Controls.Add(_tabs);
            Controls.Add(buttons);
            Controls.Add(_infoLabel);

            LoadAndParse();
        }

        private void LoadAndParse()
        {
            try
            {
                _parsed = EntityArchiveCodec.Parse(_originalData);

                string tailBreakdown = _parsed.ItemSpec != null
                    ? $"ItemSpec decoded ({_parsed.ItemSpec.Affixes.Count} affix(es)) - see the Item Spec tab."
                    : DescribeTail(_parsed.TailBytes);

                _infoLabel.Text = $"Version={_parsed.Version}, GameBuildNumber={_parsed.GameBuildNumber}, " +
                    $"{_parsed.Properties.Count} propert{(_parsed.Properties.Count == 1 ? "y" : "ies")}. {tailBreakdown}";
                _propertiesGrid.DataSource = _parsed.Properties;

                if (_parsed.ItemSpec != null)
                    BuildItemSpecTab();
            }
            catch (Exception ex)
            {
                _infoLabel.Text = $"Could not parse this blob's properties: {ex.Message}";
                _tabs.Enabled = false;
            }
        }

        // Best-effort only, purely for the info label when ItemSpec wasn't decoded:
        // ConditionCollection is always the very next thing after properties, and - like
        // ItemSpec - it's an ISerialize object auto-size-prefixed on persistent archives.
        // Reading just that length (not its content) reports a rough breakdown without risk:
        // if this guess is ever wrong, the try/catch falls back to a total, and Rebuild()
        // preserves the tail exactly either way.
        private static string DescribeTail(byte[] tail)
        {
            try
            {
                if (tail.Length < 4)
                    return $"{tail.Length} bytes after properties.";

                uint conditionCollectionSize = BitConverter.ToUInt32(tail, 0);
                if (conditionCollectionSize <= tail.Length)
                {
                    int remaining = tail.Length - (int)conditionCollectionSize;
                    return $"{conditionCollectionSize} bytes for ConditionCollection, {remaining} bytes of type-specific " +
                        "data after that - preserved as-is, not decoded or touched.";
                }
            }
            catch { /* fall through to the generic message below */ }

            return $"{tail.Length} bytes after properties - preserved as-is, not decoded or touched.";
        }

        private void BuildItemSpecTab()
        {
            ItemSpecRecord spec = _parsed.ItemSpec;

            _itemSpecTab = new TabPage("Item Spec");

            TableLayoutPanel fields = new()
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 6,
                Padding = new Padding(16),
                AutoSize = true,
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));

            _itemProtoGuidBox = new TextBox { Text = _prototypeResolver.Format(spec.ItemProtoGuid), Dock = DockStyle.Fill };
            _rarityProtoGuidBox = new TextBox { Text = _prototypeResolver.Format(spec.RarityProtoGuid), Dock = DockStyle.Fill };
            _itemLevelBox = new TextBox { Text = spec.ItemLevel.ToString(), Dock = DockStyle.Fill };
            _creditsBox = new TextBox { Text = spec.CreditsAmount.ToString(), Dock = DockStyle.Fill };
            _seedBox = new TextBox { Text = spec.Seed.ToString(), Dock = DockStyle.Fill };
            _equippableByBox = new TextBox { Text = _prototypeResolver.Format(spec.EquippableByGuid), Dock = DockStyle.Fill };

            AddFieldRow(fields, 0, "Item", WithPickButton(_itemProtoGuidBox, "Pick Item", ItemsPrefix));
            AddFieldRow(fields, 1, "Rarity", WithPickButton(_rarityProtoGuidBox, "Pick Rarity", RarityPrefix));
            AddFieldRow(fields, 2, "Item Level", _itemLevelBox);
            AddFieldRow(fields, 3, "Credits Amount", _creditsBox);
            AddFieldRow(fields, 4, "Seed", _seedBox);
            AddFieldRow(fields, 5, "Equippable By", WithPickButton(_equippableByBox, "Pick Hero", AvatarsPrefix));

            Label affixLabel = new() { Text = "Saved affixes (stored on this item - editable; double-click an affix or scope to pick a different one, or type a name):", Dock = DockStyle.Top, Height = 28, Padding = new Padding(16, 8, 0, 0) };

            _affixGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ShowCellToolTips = true,
            };
            bool showRaw = _propertiesGrid.Columns[RawColumn].Visible;
            _affixGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = AffixColumn, DataPropertyName = "AffixProtoGuidText", HeaderText = "Affix", Width = 300 });
            _affixGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = ScopeColumn, DataPropertyName = "ScopeProtoGuidText", HeaderText = "Scope (what it applies to)", Width = 220 });
            _affixGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Seed", HeaderText = "Seed", Width = 110 });
            _affixGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = StatusColumn, HeaderText = "Status (hover for why)", ReadOnly = true, Width = 190 });
            _affixGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = GrantsColumn, HeaderText = "Grants (rolled from its seed - hover for ranges)", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 200 });
            _affixGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = RawColumn, DataPropertyName = "AffixProtoGuidText", HeaderText = "Raw Affix Guid", Width = 160, ReadOnly = true, Visible = showRaw });
            _affixGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = RawScopeColumn, DataPropertyName = "ScopeProtoGuidText", HeaderText = "Raw Scope Guid", Width = 160, ReadOnly = true, Visible = showRaw });
            _affixGrid.CellFormatting += AffixGrid_CellFormatting;
            _affixGrid.CellParsing += AffixGrid_CellParsing;
            _affixGrid.CellToolTipTextNeeded += AffixGrid_CellToolTipTextNeeded;
            _affixGrid.CellDoubleClick += AffixGrid_CellDoubleClick;
            _affixGrid.DataError += Grid_DataError;
            _affixGrid.CellValueChanged += (_, _) => { _affixGrid.Invalidate(); UpdateAffixCheck(); };
            _affixGrid.DataSource = spec.Affixes;

            _affixCheckLabel = new Label { Dock = DockStyle.Top, Height = 34, Padding = new Padding(16, 2, 8, 0), AutoEllipsis = true };

            FlowLayoutPanel affixButtons = new() { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
            Button addAffixButton = new() { Text = "Add Affix...", Width = 110 };
            Button removeAffixButton = new() { Text = "Remove Affix", Width = 120 };
            addAffixButton.Click += (_, _) =>
            {
                if (TryPick("Add Affix", AffixesPrefix, 0, out ulong affixGuid) == false) return;
                // Seed 0 makes the server stop applying the item's affixes, so never add one.
                spec.Affixes.Add(new AffixSpecRecord { AffixProtoGuid = affixGuid, Seed = Random.Shared.Next(1, int.MaxValue) });
                RefreshAffixGrid();
            };
            removeAffixButton.Click += (_, _) =>
            {
                if (_affixGrid.CurrentRow?.DataBoundItem is AffixSpecRecord affix)
                {
                    spec.Affixes.Remove(affix);
                    RefreshAffixGrid();
                }
            };
            affixButtons.Controls.Add(addAffixButton);
            affixButtons.Controls.Add(removeAffixButton);

            // Built-in stats: everything the server adds from the game data when the item loads.
            // Most of a medal's/unique's/artifact's stats live here rather than in the saved
            // affix list above, so without this the item looks nearly empty.
            _builtInLabel = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(16, 8, 8, 0) };
            _builtInGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ShowCellToolTips = true,
                BackgroundColor = System.Drawing.SystemColors.Control,
                DefaultCellStyle = { BackColor = System.Drawing.Color.FromArgb(245, 245, 245) },
            };
            _builtInGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Kind", HeaderText = "Kind", Width = 70 });
            _builtInGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Stat", Width = 260 });
            _builtInGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "For", HeaderText = "For (params)", Width = 300 });
            _builtInGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Value", HeaderText = "Value", Width = 130 });
            _builtInGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Source", HeaderText = "Comes from", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _builtInGrid.CellToolTipTextNeeded += (_, e) =>
            {
                if (e.RowIndex >= 0 && _builtInGrid.Rows[e.RowIndex].DataBoundItem is PrototypeResolver.BuiltInStat stat)
                    e.ToolTipText = stat.Details;
            };

            SplitContainer split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(_affixGrid);
            split.Panel1.Controls.Add(affixButtons);
            split.Panel1.Controls.Add(_affixCheckLabel);
            split.Panel1.Controls.Add(affixLabel);
            split.Panel2.Controls.Add(_builtInGrid);
            split.Panel2.Controls.Add(_builtInLabel);

            _itemSpecTab.Controls.Add(split);
            _itemSpecTab.Controls.Add(fields);
            _itemSpecTab.Layout += (_, _) => { if (split.Height > 200 && split.SplitterDistance < 100) split.SplitterDistance = split.Height / 2; };

            // The built-in values depend on these fields, so recompute when they change.
            foreach (TextBox box in new[] { _itemProtoGuidBox, _rarityProtoGuidBox, _itemLevelBox, _seedBox, _equippableByBox })
                box.TextChanged += (_, _) => { RefreshBuiltIns(); _affixGrid.Invalidate(); UpdateAffixCheck(); };
            RefreshBuiltIns();
            UpdateAffixCheck();

            _tabs.TabPages.Add(_itemSpecTab);
        }

        private void RefreshAffixGrid()
        {
            _affixGrid.DataSource = null;
            _affixGrid.DataSource = _parsed.ItemSpec.Affixes;
            UpdateAffixCheck();
        }

        // Item-wide verdict above the grid: how many saved affixes actually do something, and
        // which stats stack past the property's own clamp (the server caps those silently).
        private void UpdateAffixCheck()
        {
            if (_affixCheckLabel == null) return;
            if (_prototypeResolver.HasGameData == false)
            {
                _affixCheckLabel.Text = "Affix check: load game data to see which affixes work.";
                return;
            }

            List<AffixSpecRecord> affixes = _parsed.ItemSpec.Affixes;
            int breakIndex = FirstBreakingAffix();
            List<PrototypeResolver.AffixReport> reports = affixes.Select(GetAffixReport).ToList();
            int Count(params PrototypeResolver.AffixHealth[] health) =>
                reports.Where((r, i) => (breakIndex < 0 || i < breakIndex) && r != null && health.Contains(r.Health)).Count();

            List<string> parts = new()
            {
                $"{Count(PrototypeResolver.AffixHealth.Works, PrototypeResolver.AffixHealth.Hidden)} work",
                $"{Count(PrototypeResolver.AffixHealth.Hidden)} of those with hidden stats",
                $"{Count(PrototypeResolver.AffixHealth.Partial)} partly",
                $"{Count(PrototypeResolver.AffixHealth.Inert)} do nothing",
                $"{Count(PrototypeResolver.AffixHealth.Metadata)} have no stats by design",
            };
            if (breakIndex >= 0)
                parts.Add($"affix #{breakIndex + 1} BREAKS the item ({affixes.Count - breakIndex - 1} after it never apply)");

            IEnumerable<PrototypeResolver.AffixStat> allStats = reports.Where((r, i) => r != null && (breakIndex < 0 || i < breakIndex)).SelectMany(r => r.Stats)
                .Concat((_builtInGrid?.DataSource as List<PrototypeResolver.BuiltInStat> ?? new()).Where(b => b.Report != null).SelectMany(b => b.Report.Stats));
            List<string> caps = PrototypeResolver.FindStackedCaps(allStats);

            _affixCheckLabel.Text = $"Affix check: {string.Join(", ", parts)}." + (caps.Count > 0 ? $"   Over the cap: {string.Join("; ", caps)}" : "");
            _affixCheckLabel.ForeColor = breakIndex >= 0 ? System.Drawing.Color.DarkRed : System.Drawing.SystemColors.ControlText;
        }

        // ApplyItemSpec() returns at the first bad saved affix, so nothing after it applies.
        private int FirstBreakingAffix()
        {
            List<AffixSpecRecord> affixes = _parsed.ItemSpec.Affixes;
            for (int i = 0; i < affixes.Count; i++)
                if (GetAffixReport(affixes[i])?.Health == PrototypeResolver.AffixHealth.BreaksItem) return i;
            return -1;
        }

        private void RefreshBuiltIns()
        {
            if (_builtInGrid == null) return;

            List<PrototypeResolver.BuiltInStat> stats = null;
            if (TryParsePrototype(_itemProtoGuidBox.Text, out ulong itemGuid) && TryParsePrototype(_rarityProtoGuidBox.Text, out ulong rarityGuid)
                && int.TryParse(_itemLevelBox.Text.Trim(), out int itemLevel) && int.TryParse(_seedBox.Text.Trim(), out int seed)
                && TryParsePrototype(_equippableByBox.Text, out ulong equippableBy))
            {
                try { stats = _prototypeResolver.GetBuiltInStats(itemGuid, rarityGuid, itemLevel, seed, equippableBy); }
                catch (Exception ex) { _builtInLabel.Text = $"Couldn't read built-in stats from the game data: {ex.Message}"; return; }
            }

            _builtInGrid.DataSource = stats ?? new List<PrototypeResolver.BuiltInStat>();

            if (_prototypeResolver.HasGameData == false)
                _builtInLabel.Text = "Built-in stats: load the server's Calligraphy.sip (Tools > Load Game Data) to see the stats this item gets from the game data.";
            else if (stats == null)
                _builtInLabel.Text = "Built-in stats: fix the Item / Rarity / Item Level / Seed / Equippable By fields above to see them.";
            else
                _builtInLabel.Text = $"Built-in stats ({stats.Count}) - from the game data, NOT saved on the item. The server re-applies these every time the item loads, " +
                    "so they can't be edited here. Values are what this item's seed and level roll (hover for the range).";
        }

        private Control WithPickButton(TextBox box, string pickTitle, string pathPrefix)
        {
            TableLayoutPanel row = new() { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            Button pickButton = new() { Text = "Pick...", Dock = DockStyle.Fill };
            pickButton.Click += (_, _) =>
            {
                ulong current = TryParsePrototype(box.Text, out ulong guid) ? guid : 0;
                if (TryPick(pickTitle, pathPrefix, current, out ulong picked))
                    box.Text = _prototypeResolver.Format(picked);
            };
            row.Controls.Add(box, 0, 0);
            row.Controls.Add(pickButton, 1, 0);
            return row;
        }

        private bool TryPick(string title, string pathPrefix, ulong current, out ulong picked)
        {
            picked = 0;
            if (_prototypeResolver.Count == 0)
            {
                MessageBox.Show(this, "Load game data first (Tools > Load Game Data) to pick things by name.\nYou can still type a raw guid.",
                    "No names loaded", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            using PrototypePickerForm picker = new(_prototypeResolver, title, pathPrefix, current);
            if (picker.ShowDialog(this) != DialogResult.OK) return false;
            picked = picker.SelectedGuid;
            return true;
        }

        private bool TryParsePrototype(string text, out ulong guid)
        {
            try { guid = _prototypeResolver.ParsePrototype(text); return true; }
            catch (FormatException) { guid = 0; return false; }
        }

        private void SetRawColumnsVisible(bool visible)
        {
            _propertiesGrid.Columns[RawColumn].Visible = visible;
            if (_affixGrid != null)
            {
                _affixGrid.Columns[RawColumn].Visible = visible;
                _affixGrid.Columns[RawScopeColumn].Visible = visible;
            }
        }

        private static void AddFieldRow(TableLayoutPanel layout, int row, string labelText, Control input)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.Controls.Add(new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(input, 1, row);
        }

        private void AddProperty()
        {
            if (_parsed == null) return;

            using AddPropertyForm form = new(_prototypeResolver);
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                _parsed.Properties.Add(form.Result);
                RefreshPropertiesGrid();
            }
        }

        private void RemoveProperty()
        {
            if (_parsed == null || _propertiesGrid.CurrentRow?.DataBoundItem is not PropertyRecord record) return;

            DialogResult confirm = MessageBox.Show(this, $"Remove {_prototypeResolver.Format(record.PrototypeGuid)}{FormatParams(record)}? (Nothing is written until you Save.)",
                "Remove property", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            _parsed.Properties.Remove(record);
            RefreshPropertiesGrid();
        }

        private void RefreshPropertiesGrid()
        {
            _propertiesGrid.DataSource = null;
            _propertiesGrid.DataSource = _parsed.Properties;
        }

        private string FormatParams(PropertyRecord record)
        {
            if (record.Params.Count == 0) return string.Empty;
            return " [" + string.Join(", ", record.Params.Select(FormatParam)) + "]";
        }

        private string FormatParam(PropertyParamRecord param) => param.Type switch
        {
            PropertyParamType.Integer => param.IntValue.ToString(),
            PropertyParamType.Asset => _prototypeResolver.FormatAsset(param.GuidValue),
            PropertyParamType.Prototype => _prototypeResolver.Format(param.GuidValue),
            _ => "?",
        };

        private string Tooltip(ulong guid) => _prototypeResolver.TryGetEntry(guid, out PrototypeResolver.Entry entry)
            ? $"{entry.Path}\n0x{guid:X}"
            : $"0x{guid:X}";

        private void PropertiesGrid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || _propertiesGrid.Rows[e.RowIndex].DataBoundItem is not PropertyRecord record) return;

            switch (_propertiesGrid.Columns[e.ColumnIndex].Name)
            {
                case PropNameColumn:
                    e.Value = _prototypeResolver.Format(record.PrototypeGuid);
                    e.FormattingApplied = true;
                    break;

                case PropParamsColumn:
                    e.Value = record.Params.Count == 0 ? "-" : string.Join(", ", record.Params.Select(FormatParam));
                    e.FormattingApplied = true;
                    break;

                case PropValueColumn when record.DataType == PropertyDataType.Prototype:
                    e.Value = _prototypeResolver.Format(record.ValueGuid);
                    e.FormattingApplied = true;
                    break;

                case PropValueColumn when record.DataType == PropertyDataType.Asset:
                    e.Value = _prototypeResolver.FormatAsset(record.ValueGuid);
                    e.FormattingApplied = true;
                    break;
            }
        }

        private void PropertiesGrid_CellToolTipTextNeeded(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _propertiesGrid.Rows[e.RowIndex].DataBoundItem is not PropertyRecord record) return;

            e.ToolTipText = _propertiesGrid.Columns[e.ColumnIndex].Name switch
            {
                PropNameColumn => Tooltip(record.PrototypeGuid),
                PropParamsColumn => string.Join("\n", record.Params.Select(p => p.Type == PropertyParamType.Integer
                    ? $"{p.Type}: {p.IntValue}" : $"{p.Type}: 0x{p.GuidValue:X}")),
                PropValueColumn when record.DataType == PropertyDataType.Prototype => Tooltip(record.ValueGuid) + "\nDouble-click to pick, or type a name.",
                PropValueColumn when record.DataType == PropertyDataType.Asset => $"0x{record.ValueGuid:X}",
                PropValueColumn when record.DataType == PropertyDataType.Time => $"{record.ValueLong} ms",
                _ => e.ToolTipText,
            };
        }

        // Typed names (or hex) in the Value column become the hex text ValueText expects.
        private void PropertiesGrid_CellParsing(object sender, DataGridViewCellParsingEventArgs e)
        {
            if (_propertiesGrid.Columns[e.ColumnIndex].Name != PropValueColumn) return;
            if (_propertiesGrid.Rows[e.RowIndex].DataBoundItem is not PropertyRecord record) return;
            if (record.DataType != PropertyDataType.Prototype && record.DataType != PropertyDataType.Asset) return;

            try
            {
                ulong guid = record.DataType == PropertyDataType.Prototype
                    ? _prototypeResolver.ParsePrototype(e.Value as string)
                    : _prototypeResolver.ParseAsset(e.Value as string);
                e.Value = $"0x{guid:X}";
            }
            catch (FormatException ex)
            {
                MessageBox.Show(this, ex.Message, "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Error);
                e.Value = record.ValueText;
            }
            e.ParsingApplied = true;
        }

        private void PropertiesGrid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            // Row/column headers raise this too, with index -1.
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _propertiesGrid.Columns[e.ColumnIndex].Name != PropValueColumn) return;
            if (_propertiesGrid.Rows[e.RowIndex].DataBoundItem is not PropertyRecord record || record.DataType != PropertyDataType.Prototype) return;

            if (TryPick($"Pick value for {_prototypeResolver.Format(record.PrototypeGuid)}", null, record.ValueGuid, out ulong picked))
            {
                _propertiesGrid.CancelEdit();
                record.ValueGuid = picked;
                _propertiesGrid.InvalidateRow(e.RowIndex);
            }
        }

        private void AffixGrid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || _affixGrid.Rows[e.RowIndex].DataBoundItem is not AffixSpecRecord affix) return;

            string column = _affixGrid.Columns[e.ColumnIndex].Name;
            if (column == GrantsColumn)
            {
                e.Value = _prototypeResolver.HasGameData ? PrototypeResolver.SummarizeAffixStats(GetAffixReport(affix)?.Stats) : "(load game data)";
                e.FormattingApplied = true;
                return;
            }
            if (column == StatusColumn)
            {
                FormatStatusCell(e, affix);
                return;
            }
            if (column != AffixColumn && column != ScopeColumn) return;

            ulong guid = column == AffixColumn ? affix.AffixProtoGuid : affix.ScopeProtoGuid;
            e.Value = _prototypeResolver.Format(guid);
            e.FormattingApplied = true;
        }

        private void AffixGrid_CellToolTipTextNeeded(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _affixGrid.Rows[e.RowIndex].DataBoundItem is not AffixSpecRecord affix) return;

            string column = _affixGrid.Columns[e.ColumnIndex].Name;
            if (column == AffixColumn) e.ToolTipText = Tooltip(affix.AffixProtoGuid);
            else if (column == ScopeColumn) e.ToolTipText = Tooltip(affix.ScopeProtoGuid);
            else if (column == GrantsColumn) e.ToolTipText = PrototypeResolver.DescribeAffixStats(GetAffixReport(affix)?.Stats);
            else if (column == StatusColumn) e.ToolTipText = IsAfterBreakingAffix(e.RowIndex)
                ? "An earlier affix breaks the item, so the server never gets to this one."
                : GetAffixReport(affix)?.Describe();
        }

        private bool IsAfterBreakingAffix(int rowIndex)
        {
            int breakIndex = FirstBreakingAffix();
            return breakIndex >= 0 && rowIndex > breakIndex;
        }

        private void FormatStatusCell(DataGridViewCellFormattingEventArgs e, AffixSpecRecord affix)
        {
            PrototypeResolver.AffixReport report = GetAffixReport(affix);
            bool blocked = IsAfterBreakingAffix(e.RowIndex);
            PrototypeResolver.AffixHealth? health = blocked ? PrototypeResolver.AffixHealth.Inert : report?.Health;

            e.Value = blocked ? "Never applied (see above)" : report?.StatusText ?? "(load game data)";
            e.CellStyle.BackColor = health switch
            {
                PrototypeResolver.AffixHealth.Works => System.Drawing.Color.FromArgb(220, 245, 220),
                PrototypeResolver.AffixHealth.Hidden => System.Drawing.Color.FromArgb(215, 230, 250),
                PrototypeResolver.AffixHealth.Partial => System.Drawing.Color.FromArgb(255, 245, 200),
                PrototypeResolver.AffixHealth.Inert => System.Drawing.Color.FromArgb(255, 220, 190),
                PrototypeResolver.AffixHealth.BreaksItem => System.Drawing.Color.FromArgb(255, 180, 180),
                _ => e.CellStyle.BackColor,
            };
            e.FormattingApplied = true;
        }

        // What a saved affix grants with this item's current Level / Equippable By and the
        // affix's own seed, plus whether the server actually applies it. Cached because cell
        // formatting runs on every repaint.
        private readonly Dictionary<(ulong, ulong, int, int, ulong), PrototypeResolver.AffixReport> _grantsCache = new();

        private PrototypeResolver.AffixReport GetAffixReport(AffixSpecRecord affix)
        {
            int itemLevel = int.TryParse(_itemLevelBox.Text.Trim(), out int level) ? level : _parsed.ItemSpec.ItemLevel;
            ulong equippableBy = TryParsePrototype(_equippableByBox.Text, out ulong avatar) ? avatar : _parsed.ItemSpec.EquippableByGuid;

            var key = (affix.AffixProtoGuid, affix.ScopeProtoGuid, affix.Seed, itemLevel, equippableBy);
            if (_grantsCache.TryGetValue(key, out var cached)) return cached;

            PrototypeResolver.AffixReport report;
            try { report = _prototypeResolver.GetAffixReport(affix.AffixProtoGuid, affix.ScopeProtoGuid, equippableBy, itemLevel, affix.Seed, savedAffix: true); }
            catch (Exception ex)
            {
                report = new PrototypeResolver.AffixReport();
                report.Stats.Add(new PrototypeResolver.AffixStat { Name = "(couldn't read)", For = "-", Value = ex.Message });
            }

            _grantsCache[key] = report;
            return report;
        }

        private void AffixGrid_CellParsing(object sender, DataGridViewCellParsingEventArgs e)
        {
            string column = _affixGrid.Columns[e.ColumnIndex].Name;
            if (column != AffixColumn && column != ScopeColumn) return;
            if (_affixGrid.Rows[e.RowIndex].DataBoundItem is not AffixSpecRecord affix) return;

            try
            {
                e.Value = $"0x{_prototypeResolver.ParsePrototype(e.Value as string):X}";
            }
            catch (FormatException ex)
            {
                MessageBox.Show(this, ex.Message, "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Error);
                e.Value = column == AffixColumn ? affix.AffixProtoGuidText : affix.ScopeProtoGuidText;
            }
            e.ParsingApplied = true;
        }

        private void AffixGrid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _affixGrid.Rows[e.RowIndex].DataBoundItem is not AffixSpecRecord affix) return;

            string column = _affixGrid.Columns[e.ColumnIndex].Name;
            if (column != AffixColumn && column != ScopeColumn) return;

            _affixGrid.CancelEdit();
            if (column == AffixColumn && TryPick("Pick Affix", AffixesPrefix, affix.AffixProtoGuid, out ulong pickedAffix))
                affix.AffixProtoGuid = pickedAffix;
            else if (column == ScopeColumn && TryPick("Pick Scope", null, affix.ScopeProtoGuid, out ulong pickedScope))
                affix.ScopeProtoGuid = pickedScope;

            // Other rows' status can depend on this one (e.g. "never applied"), so redraw them all.
            _affixGrid.Invalidate();
            UpdateAffixCheck();
        }

        private void Grid_DataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            MessageBox.Show(this, e.Exception.Message, "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Error);
            e.ThrowException = false;
        }

        private void Save()
        {
            if (_parsed == null) return;

            try
            {
                if (_parsed.ItemSpec != null)
                    ApplyItemSpecEdits();

                byte[] newData = _parsed.Rebuild();
                _onSave(newData);
                MessageBox.Show(this, "Saved.", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed to save", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ApplyItemSpecEdits()
        {
            ItemSpecRecord spec = _parsed.ItemSpec;

            spec.ItemProtoGuid = _prototypeResolver.ParsePrototype(_itemProtoGuidBox.Text);
            spec.RarityProtoGuid = _prototypeResolver.ParsePrototype(_rarityProtoGuidBox.Text);
            spec.ItemLevel = int.Parse(_itemLevelBox.Text.Trim());
            spec.CreditsAmount = int.Parse(_creditsBox.Text.Trim());
            spec.Seed = int.Parse(_seedBox.Text.Trim());
            spec.EquippableByGuid = _prototypeResolver.ParsePrototype(_equippableByBox.Text);
        }
    }
}
