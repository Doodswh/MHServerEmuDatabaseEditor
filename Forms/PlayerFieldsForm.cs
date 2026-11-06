using System;
using System.Drawing;
using System.Windows.Forms;
using MHDBEditor.Data;
using MHDBEditor.Models;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Edits the plain (non-blob) columns on a Player row: Gazillionite balance, AOI volume,
    // start location, last logout time. ArchiveData is only touched when the location picker
    // is told to set the last town as well.
    public class PlayerFieldsForm : Form
    {
        private readonly AccountRepository _accountRepository;
        private readonly DBPlayer _player;

        private readonly NumericUpDown _gazillioniteInput;
        private readonly NumericUpDown _aoiInput;
        private readonly TextBox _startTargetInput;
        private readonly TextBox _lastLogoutInput;
        private readonly Label _startTargetInfo;
        private readonly PrototypeResolver _resolver;
        private PrototypeResolver.StartLocation _pendingLastTown;

        public PlayerFieldsForm(AccountRepository accountRepository, DBPlayer player, PrototypeResolver resolver)
        {
            _accountRepository = accountRepository;
            _player = player;
            _resolver = resolver;

            Text = $"Player Fields - 0x{player.DbGuid:X}";
            Width = 560;
            Height = 310;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            TableLayoutPanel layout = new()
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(12),
                AutoSize = true,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));

            _gazillioniteInput = new NumericUpDown { Minimum = -1, Maximum = long.MaxValue, Value = Math.Max(-1, player.GazillioniteBalance), Dock = DockStyle.Fill };
            _aoiInput = new NumericUpDown { Minimum = 0, Maximum = int.MaxValue, Value = Math.Max(0, player.AOIVolume), Dock = DockStyle.Fill };
            _startTargetInput = new TextBox { Text = player.StartTarget.ToString(), Dock = DockStyle.Fill };
            _lastLogoutInput = new TextBox { Text = player.LastLogoutTime.ToString(), Dock = DockStyle.Fill };

            AddRow(layout, 0, "Gazillionite Balance (-1 = reset to default)", _gazillioniteInput);
            AddRow(layout, 1, "AOI Volume", _aoiInput);
            Button pickButton = new() { Text = "Pick...", Width = 70, Anchor = AnchorStyles.Right };
            pickButton.Click += (_, _) => PickStartLocation();
            TableLayoutPanel startRow = new() { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            startRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            startRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            startRow.Controls.Add(_startTargetInput, 0, 0);
            startRow.Controls.Add(pickButton, 1, 0);
            _startTargetInfo = new Label { AutoSize = false, Dock = DockStyle.Fill, Height = 34 };
            _startTargetInput.TextChanged += (_, _) => { _pendingLastTown = null; UpdateStartTargetInfo(); };

            AddRow(layout, 2, "Start Location (prototype id)", startRow);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.Controls.Add(_startTargetInfo, 0, 3);
            layout.SetColumnSpan(_startTargetInfo, 2);
            AddRow(layout, 4, "Last Logout Time (unix milliseconds)", _lastLogoutInput);
            UpdateStartTargetInfo();

            FlowLayoutPanel buttons = new()
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(8),
            };
            Button saveButton = new() { Text = "Save", Width = 90 };
            Button cancelButton = new() { Text = "Cancel", Width = 90 };
            saveButton.Click += (_, _) => Save();
            cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(saveButton);

            Controls.Add(layout);
            Controls.Add(buttons);
        }

        private static void AddRow(TableLayoutPanel layout, int row, string labelText, Control input)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(input, 1, row);
        }

        private void UpdateStartTargetInfo()
        {
            if (long.TryParse(_startTargetInput.Text.Trim(), out long value) == false)
            {
                _startTargetInfo.Text = "Not a number.";
                _startTargetInfo.ForeColor = Color.DarkRed;
                return;
            }
            var (text, ok) = _resolver.DescribeStartTarget(value);
            _startTargetInfo.Text = (_pendingLastTown != null ? "(last town will be set too) " : "") + text;
            _startTargetInfo.ForeColor = ok ? SystemColors.GrayText : Color.DarkRed;
        }

        private void PickStartLocation()
        {
            if (_resolver.HasGameData == false)
            {
                MessageBox.Show(this, "Load game data first (Tools > Load Game Data) - the list of places comes from the game data.", "Game data needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            long.TryParse(_startTargetInput.Text.Trim(), out long current);
            using StartLocationPickerForm picker = new(_resolver, current, StartLocationPickerForm.CanSetLastTown(_resolver, _player.ArchiveData));
            if (picker.ShowDialog(this) != DialogResult.OK) return;

            _startTargetInput.Text = unchecked((long)picker.Result.DataRef).ToString();
            _pendingLastTown = picker.AlsoSetLastTown ? picker.Result : null;
            UpdateStartTargetInfo();
        }

        private void Save()
        {
            try
            {
                _player.GazillioniteBalance = (long)_gazillioniteInput.Value;
                _player.AOIVolume = (int)_aoiInput.Value;
                _player.StartTarget = long.Parse(_startTargetInput.Text.Trim());
                _player.LastLogoutTime = long.Parse(_lastLogoutInput.Text.Trim());

                byte[] newArchive = _pendingLastTown != null ? StartLocationPickerForm.WithLastTown(_resolver, _player.ArchiveData, _pendingLastTown) : null;
                _accountRepository.UpdatePlayerFields(_player);
                if (newArchive != null)
                    _accountRepository.SetPlayerArchiveData(_player.DbGuid, newArchive);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed to save", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
