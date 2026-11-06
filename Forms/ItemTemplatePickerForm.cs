using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MHDBEditor.Models;

namespace MHDBEditor.Forms
{
    // Simple "pick one by name" list for Simple Mode's Give Item action - no guids in sight.
    public class ItemTemplatePickerForm : Form
    {
        public ItemTemplate Selected { get; private set; }

        public ItemTemplatePickerForm(IReadOnlyList<ItemTemplate> templates)
        {
            Text = "Choose an Item";
            Width = 420;
            Height = 480;
            StartPosition = FormStartPosition.CenterParent;

            ListBox list = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10.5f) };
            foreach (ItemTemplate template in templates)
                list.Items.Add(new TemplateOption(template));
            if (list.Items.Count > 0)
                list.SelectedIndex = 0;

            FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            Button giveButton = new() { Text = "Give This Item", Width = 140 };
            Button cancelButton = new() { Text = "Cancel", Width = 90 };

            giveButton.Click += (_, _) =>
            {
                if (list.SelectedItem is TemplateOption option)
                {
                    Selected = option.Template;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            list.DoubleClick += (_, _) => giveButton.PerformClick();

            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(giveButton);

            Controls.Add(list);
            Controls.Add(buttons);
        }

        private class TemplateOption
        {
            public ItemTemplate Template { get; }
            public TemplateOption(ItemTemplate template) { Template = template; }
            public override string ToString() => Template.DisplayName;
        }
    }
}
