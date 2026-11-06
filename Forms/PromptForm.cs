using System.Collections.Generic;
using System.Windows.Forms;

namespace MHDBEditor.Forms
{
    // Small reusable modal dialog for prompting one or more labeled text fields.
    internal static class PromptForm
    {
        public static string ShowSingle(IWin32Window owner, string title, string label, string defaultValue = "")
        {
            Dictionary<string, string> result = ShowMultiple(owner, title, new[] { (label, defaultValue) });
            return result?[label];
        }

        public static Dictionary<string, string> ShowMultiple(IWin32Window owner, string title, (string Label, string Default)[] fields)
        {
            using Form form = new()
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                Width = 440,
                Height = 100 + fields.Length * 34,
            };

            TableLayoutPanel layout = new()
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = fields.Length,
                Padding = new Padding(12),
                AutoSize = true,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

            TextBox[] textBoxes = new TextBox[fields.Length];

            for (int i = 0; i < fields.Length; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

                Label fieldLabel = new() { Text = fields[i].Label, AutoSize = true, Anchor = AnchorStyles.Left };
                TextBox textBox = new() { Text = fields[i].Default ?? string.Empty, Dock = DockStyle.Fill };
                textBoxes[i] = textBox;

                layout.Controls.Add(fieldLabel, 0, i);
                layout.Controls.Add(textBox, 1, i);
            }

            FlowLayoutPanel buttonPanel = new()
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(12),
            };

            Button okButton = new() { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
            Button cancelButton = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
            buttonPanel.Controls.Add(cancelButton);
            buttonPanel.Controls.Add(okButton);

            form.Controls.Add(layout);
            form.Controls.Add(buttonPanel);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            if (form.ShowDialog(owner) != DialogResult.OK)
                return null;

            Dictionary<string, string> result = new();
            for (int i = 0; i < fields.Length; i++)
                result[fields[i].Label] = textBoxes[i].Text;

            return result;
        }
    }
}
