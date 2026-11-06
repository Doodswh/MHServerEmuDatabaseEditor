using System;
using System.Windows.Forms;
using MHDBEditor.Data;
using MHDBEditor.Models;

namespace MHDBEditor.Forms
{
    public class GuildMembersForm : Form
    {
        private readonly GuildRepository _guildRepository;
        private readonly DBGuild _guild;
        private readonly DataGridView _grid;

        public GuildMembersForm(GuildRepository guildRepository, DBGuild guild)
        {
            _guildRepository = guildRepository;
            _guild = guild;

            Text = $"Members of {guild.Name}";
            Width = 600;
            Height = 450;
            StartPosition = FormStartPosition.CenterParent;

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AutoGenerateColumns = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
            };

            FlowLayoutPanel buttons = new()
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.LeftToRight,
                Height = 44,
                Padding = new Padding(8),
            };

            Button addButton = new() { Text = "Add / Set Membership...", Width = 170 };
            Button removeButton = new() { Text = "Remove Selected", Width = 130 };
            Button refreshButton = new() { Text = "Refresh", Width = 90 };

            addButton.Click += (_, _) => AddOrUpdateMember();
            removeButton.Click += (_, _) => RemoveSelected();
            refreshButton.Click += (_, _) => LoadMembers();

            buttons.Controls.Add(addButton);
            buttons.Controls.Add(removeButton);
            buttons.Controls.Add(refreshButton);

            Controls.Add(_grid);
            Controls.Add(buttons);

            LoadMembers();
        }

        private void LoadMembers()
        {
            var guilds = _guildRepository.GetAll();
            DBGuild fresh = guilds.Find(g => g.Id == _guild.Id) ?? _guild;
            _grid.DataSource = fresh.Members;
        }

        private void AddOrUpdateMember()
        {
            var input = PromptForm.ShowMultiple(this, "Add / Set Membership", new[]
            {
                ("Player DbGuid (decimal or 0x hex)", ""),
                ("Membership (integer rank)", "0"),
            });
            if (input == null) return;

            try
            {
                long playerDbGuid = GuidTextHelper.Parse(input["Player DbGuid (decimal or 0x hex)"]);
                long membership = long.Parse(input["Membership (integer rank)"]);

                long? current = _guildRepository.GetGuildOf(playerDbGuid);
                if (current != null && current != _guild.Id)
                {
                    string other = _guildRepository.GetAll().Find(g => g.Id == current)?.Name ?? $"guild {current}";
                    if (MessageBox.Show(this, $"Player 0x{playerDbGuid:X} is in \"{other}\". A player can only be in one guild - move them to this one?",
                            "Already in a guild", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        return;
                }
                _guildRepository.SetMembership(playerDbGuid, _guild.Id, membership);
                LoadMembers();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed to set membership", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RemoveSelected()
        {
            if (_grid.CurrentRow?.DataBoundItem is not DBGuildMember member)
                return;

            if (MessageBox.Show(this, $"Remove player 0x{member.PlayerDbGuid:X} from this guild?", "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            _guildRepository.RemoveMember(member.PlayerDbGuid, _guild.Id);
            LoadMembers();
        }

    }
}
