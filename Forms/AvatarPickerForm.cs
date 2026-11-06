using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MHDBEditor.Models;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Simple "pick one character" list for Simple Mode actions that need to know which of a
    // player's avatars to touch - shows a resolved name if one's available, else a generic label.
    public class AvatarPickerForm : Form
    {
        public DBEntity Selected { get; private set; }

        public AvatarPickerForm(IReadOnlyList<DBEntity> avatars, PrototypeResolver resolver)
        {
            Text = "Choose a Character";
            Width = 420;
            Height = 420;
            StartPosition = FormStartPosition.CenterParent;

            ListBox list = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10.5f) };
            foreach (DBEntity avatar in avatars)
                list.Items.Add(new AvatarOption(avatar, resolver));
            if (list.Items.Count > 0)
                list.SelectedIndex = 0;

            FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            Button chooseButton = new() { Text = "Choose", Width = 100 };
            Button cancelButton = new() { Text = "Cancel", Width = 90 };

            chooseButton.Click += (_, _) =>
            {
                if (list.SelectedItem is AvatarOption option)
                {
                    Selected = option.Avatar;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            list.DoubleClick += (_, _) => chooseButton.PerformClick();

            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(chooseButton);

            Controls.Add(list);
            Controls.Add(buttons);
        }

        private class AvatarOption
        {
            public DBEntity Avatar { get; }
            private readonly string _display;

            public AvatarOption(DBEntity avatar, PrototypeResolver resolver)
            {
                Avatar = avatar;
                _display = resolver != null && resolver.TryResolve(avatar.EntityProtoGuid, out string name)
                    ? name
                    : $"Character (0x{avatar.EntityProtoGuid:X})";
            }

            public override string ToString() => _display;
        }
    }
}
