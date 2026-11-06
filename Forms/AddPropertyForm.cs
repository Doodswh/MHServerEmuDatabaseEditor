using System;
using System.Windows.Forms;
using MHDBEditor.ArchiveFormat;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Builds a new PropertyRecord to add to an entity's Properties list - pick the property by
    // name (from loaded game data) or paste a raw guid, choose its data type and value,
    // and optionally one param. Doesn't validate that the data type/param shape actually matches
    // what the server expects for that property - it's on you to get that right, same as with
    // any other raw edit in this tool.
    public class AddPropertyForm : Form
    {
        public PropertyRecord Result { get; private set; }

        private readonly ComboBox _propertyCombo;
        private readonly ComboBox _dataTypeCombo;
        private readonly TextBox _valueBox;
        private readonly ComboBox _param1TypeCombo;
        private readonly TextBox _param1ValueBox;
        private readonly PrototypeResolver _resolver;

        public AddPropertyForm(PrototypeResolver resolver)
        {
            _resolver = resolver ?? new PrototypeResolver();

            Text = "Add Property";
            Width = 640;
            Height = 400;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            TableLayoutPanel layout = new() { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 5, Padding = new Padding(16), AutoSize = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));

            _propertyCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                Dock = DockStyle.Fill,
                DropDownWidth = 560,
            };

            // Only real property definitions (Property/Info/...), not every prototype in the game.
            foreach (PrototypeResolver.Entry entry in _resolver.Find("Property/Info/"))
                _propertyCombo.Items.Add(entry);

            _dataTypeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            // Only the types that can be typed in and saved (not Curve, Int21Vector3 or Invalid).
            foreach (PropertyDataType type in new[] { PropertyDataType.Boolean, PropertyDataType.Real, PropertyDataType.Integer, PropertyDataType.Prototype,
                         PropertyDataType.Asset, PropertyDataType.EntityId, PropertyDataType.Time, PropertyDataType.Guid, PropertyDataType.RegionId })
                _dataTypeCombo.Items.Add(type.ToString());
            _dataTypeCombo.SelectedItem = PropertyDataType.Integer.ToString();

            _valueBox = new TextBox { Text = "0", Dock = DockStyle.Fill };

            _param1TypeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            _param1TypeCombo.Items.Add("None");
            _param1TypeCombo.Items.Add(PropertyParamType.Integer.ToString());
            _param1TypeCombo.Items.Add(PropertyParamType.Asset.ToString());
            _param1TypeCombo.Items.Add(PropertyParamType.Prototype.ToString());
            _param1TypeCombo.SelectedIndex = 0;

            _param1ValueBox = new TextBox { Text = "", PlaceholderText = "Number, or a prototype/asset name", Dock = DockStyle.Fill };
            _valueBox.PlaceholderText = "Number, true/false, date, or a prototype name";

            AddFieldRow(layout, 0, "Property (type to search):", _propertyCombo, 44);
            AddFieldRow(layout, 1, "Data Type:", _dataTypeCombo, 34);
            AddFieldRow(layout, 2, "Value:", _valueBox, 34);
            AddFieldRow(layout, 3, "Param 1 Type:", _param1TypeCombo, 34);
            AddFieldRow(layout, 4, "Param 1 Value:", _param1ValueBox, 34);

            FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8) };
            Button okBtn = new() { Text = "OK", Width = 90 };
            Button cancelBtn = new() { Text = "Cancel", Width = 90 };

            okBtn.Click += (_, _) => Save();
            cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

            buttons.Controls.Add(cancelBtn);
            buttons.Controls.Add(okBtn);

            Controls.Add(layout);
            Controls.Add(buttons);
        }

        private static void AddFieldRow(TableLayoutPanel layout, int row, string labelText, Control input, int rowHeight)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
            layout.Controls.Add(new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(input, 1, row);
        }

        private void Save()
        {
            try
            {
                ulong propGuid = _propertyCombo.SelectedItem is PrototypeResolver.Entry entry
                    ? entry.Guid
                    : _resolver.ParsePrototype(_propertyCombo.Text);
                if (propGuid == 0)
                    throw new FormatException("Pick a property first.");

                PropertyDataType dataType = Enum.Parse<PropertyDataType>(_dataTypeCombo.Text);

                PropertyRecord record = new()
                {
                    Version = 0,
                    PrototypeGuid = propGuid,
                    DataType = dataType,
                };

                record.ValueText = dataType switch
                {
                    PropertyDataType.Prototype => $"0x{_resolver.ParsePrototype(_valueBox.Text):X}",
                    PropertyDataType.Asset => $"0x{_resolver.ParseAsset(_valueBox.Text):X}",
                    _ => _valueBox.Text,
                };

                if (_param1TypeCombo.Text != "None")
                {
                    PropertyParamType paramType = Enum.Parse<PropertyParamType>(_param1TypeCombo.Text);
                    PropertyParamRecord param = new() { Type = paramType };

                    switch (paramType)
                    {
                        case PropertyParamType.Integer: param.IntValue = int.Parse(_param1ValueBox.Text.Trim()); break;
                        case PropertyParamType.Asset: param.GuidValue = _resolver.ParseAsset(_param1ValueBox.Text); break;
                        default: param.GuidValue = _resolver.ParsePrototype(_param1ValueBox.Text); break;
                    }

                    record.Params.Add(param);
                }

                Result = record;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
