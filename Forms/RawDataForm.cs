using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MHDBEditor.Forms
{
    // Shows an entity's raw ArchiveData blob as Base64 for export, and lets you paste in a
    // replacement blob (e.g. copied from another entity of the same EntityProtoGuid) or load
    // one from a .bin file. This is a raw byte-level operation - MHDBEditor cannot validate
    // that what you paste in is a well-formed Archive for this entity type.
    public class RawDataForm : Form
    {
        private readonly TextBox _textBox;
        public byte[] Result { get; private set; }

        public RawDataForm(string title, byte[] initialData)
        {
            Text = title;
            Width = 700;
            Height = 500;
            StartPosition = FormStartPosition.CenterParent;

            _textBox = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9),
                Text = initialData != null ? Convert.ToBase64String(initialData) : string.Empty,
            };

            FlowLayoutPanel buttons = new()
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(8),
            };

            Button closeButton = new() { Text = "Close", Width = 90 };
            Button applyButton = new() { Text = "Save Changes", Width = 110 };
            Button loadFileButton = new() { Text = "Load From File...", Width = 130 };
            Button saveFileButton = new() { Text = "Export To File...", Width = 130 };

            closeButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            applyButton.Click += (_, _) =>
            {
                try
                {
                    Result = Convert.FromBase64String(_textBox.Text.Trim());
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (FormatException)
                {
                    MessageBox.Show(this, "That isn't valid Base64.", "Invalid data", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            loadFileButton.Click += (_, _) =>
            {
                using OpenFileDialog dialog = new() { Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*" };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _textBox.Text = Convert.ToBase64String(File.ReadAllBytes(dialog.FileName));
            };
            saveFileButton.Click += (_, _) =>
            {
                using SaveFileDialog dialog = new() { Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*" };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllBytes(dialog.FileName, Convert.FromBase64String(_textBox.Text.Trim()));
                    }
                    catch (FormatException)
                    {
                        MessageBox.Show(this, "That isn't valid Base64.", "Invalid data", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            };

            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(applyButton);
            buttons.Controls.Add(loadFileButton);
            buttons.Controls.Add(saveFileButton);

            Controls.Add(_textBox);
            Controls.Add(buttons);
        }
    }
}
