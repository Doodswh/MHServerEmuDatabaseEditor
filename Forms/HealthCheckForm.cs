using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MHDBEditor.Data;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Tools > Database Health Check. Scans the whole DB for rows the server ignores, skips or
    // wipes (see HealthCheckService), lists them, and fixes the safe ones after a backup.
    public class HealthCheckForm : Form
    {
        private const int MaxRowsShown = 5000;

        private readonly DbConnectionManager _connectionManager;
        private readonly HealthCheckService _service;
        private readonly PrototypeResolver _resolver;

        private readonly Label _statusLabel;
        private readonly ListView _issueList;
        private readonly Label _explanationLabel;
        private readonly ListView _rowList;
        private readonly Button _scanButton;
        private readonly Button _fixButton;
        private readonly Button _exportButton;
        private readonly CheckBox _compactCheck;

        private HealthCheckService.Report _report;
        private CancellationTokenSource _cancel;
        private bool _busy;

        public HealthCheckForm(DbConnectionManager connectionManager, PrototypeResolver resolver)
        {
            _connectionManager = connectionManager;
            _resolver = resolver;
            _service = new HealthCheckService(connectionManager, resolver);

            Text = "Database Health Check";
            Width = 1100;
            Height = 720;
            MinimumSize = new Size(800, 500);
            StartPosition = FormStartPosition.CenterParent;

            Label intro = new()
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(8, 6, 8, 0),
                Text = "Finds rows the server never loads, skips at login, or silently wipes on the next save, based on how it actually loads an account. " +
                       (resolver.HasGameData ? "" : "Load game data (Tools menu) for the prototype, affix and start-location checks."),
                ForeColor = resolver.HasGameData ? SystemColors.ControlText : Color.DarkOrange,
            };

            _issueList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                CheckBoxes = true,
            };
            _issueList.Columns.Add("Fix?", 50);
            _issueList.Columns.Add("Severity", 80);
            _issueList.Columns.Add("Issue", 420);
            _issueList.Columns.Add("Rows", 80);
            _issueList.Columns.Add("What the fix does", 380);
            _issueList.SelectedIndexChanged += (_, _) => ShowSelectedIssue();
            _issueList.ItemCheck += (_, e) =>
            {
                // Report-only issues can't be ticked.
                if (_issueList.Items[e.Index].Tag is HealthCheckService.Issue issue && issue.CanFix == false)
                    e.NewValue = CheckState.Unchecked;
            };
            _issueList.ItemChecked += (_, _) => UpdateButtons();

            _explanationLabel = new Label { Dock = DockStyle.Top, Height = 48, Padding = new Padding(4, 6, 4, 0) };
            _rowList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
            };
            _rowList.Columns.Add("Table", 110);
            _rowList.Columns.Add("DbGuid", 160);
            _rowList.Columns.Add("Owner", 140);
            _rowList.Columns.Add("What", 260);
            _rowList.Columns.Add("Detail", 420);

            SplitContainer split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 180 };
            split.Panel1.Controls.Add(_issueList);
            split.Panel2.Controls.Add(_rowList);
            split.Panel2.Controls.Add(_explanationLabel);
            Load += (_, _) => split.SplitterDistance = 170;   // only sticks once the form has its real size

            FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(6), FlowDirection = FlowDirection.LeftToRight };
            _scanButton = new Button { Text = "Scan", Width = 90 };
            _fixButton = new Button { Text = "Fix Ticked...", Width = 110, Enabled = false };
            _exportButton = new Button { Text = "Export Rows (CSV)...", Width = 150, Enabled = false };
            _compactCheck = new CheckBox { Text = "Compact the database after fixing (shrinks the file; can take minutes)", AutoSize = true, Margin = new Padding(16, 7, 3, 3) };
            _scanButton.Click += async (_, _) => await RunScan();
            _fixButton.Click += async (_, _) => await RunFix();
            _exportButton.Click += (_, _) => ExportRows();
            buttons.Controls.AddRange(new Control[] { _scanButton, _fixButton, _exportButton, _compactCheck });

            _statusLabel = new Label { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(8, 4, 8, 0), Text = "Press Scan. A full scan reads every row (about a minute on a 2 GB database)." };

            Controls.Add(split);
            Controls.Add(intro);
            Controls.Add(_statusLabel);
            Controls.Add(buttons);

            FormClosing += (_, e) =>
            {
                if (_busy && _cancel == null)
                {
                    // A fix/compact is running - don't leave it half-reported.
                    e.Cancel = true;
                    MessageBox.Show(this, "Wait for the fix to finish.", "Busy", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                _cancel?.Cancel();
            };
        }

        private async Task RunScan()
        {
            if (_busy) { _cancel?.Cancel(); return; }

            SetBusy(true, cancellable: true);
            _scanButton.Text = "Cancel";
            Progress<string> progress = new(text => { if (IsDisposed == false) _statusLabel.Text = text; });
            CancellationToken token = _cancel.Token;
            try
            {
                _report = await Task.Run(() => _service.Scan(progress, token));
                ShowReport();
            }
            catch (OperationCanceledException)
            {
                if (IsDisposed) return;
                _statusLabel.Text = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _statusLabel.Text = "Scan failed.";
                MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (IsDisposed == false)
                {
                    _scanButton.Text = "Scan";
                    SetBusy(false, cancellable: false);
                }
            }
        }

        private void ShowReport()
        {
            _issueList.BeginUpdate();
            _issueList.Items.Clear();
            foreach (HealthCheckService.Issue issue in _report.Issues)
            {
                ListViewItem item = new(new[] { "", issue.Severity.ToString(), issue.Title, issue.Rows.Count.ToString("N0"), issue.FixDescription ?? "(report only)" })
                {
                    Tag = issue,
                    UseItemStyleForSubItems = true,
                    BackColor = issue.Severity switch
                    {
                        HealthCheckService.Severity.Problem => Color.MistyRose,
                        HealthCheckService.Severity.Warning => Color.LightYellow,
                        _ => SystemColors.Window,
                    },
                };
                _issueList.Items.Add(item);
            }
            _issueList.EndUpdate();
            if (_issueList.Items.Count > 0) _issueList.Items[0].Selected = true;
            else { _rowList.Items.Clear(); _explanationLabel.Text = ""; }

            string counts = string.Join(", ", _report.RowCounts.Select(kv => $"{kv.Value:N0} {kv.Key}"));
            _statusLabel.Text = _report.Issues.Count == 0
                ? $"No problems found. Checked {counts} in {_report.Elapsed.TotalSeconds:0}s."
                : $"{_report.Issues.Count} kind(s) of issue found. Checked {counts} in {_report.Elapsed.TotalSeconds:0}s." +
                  (_report.UsedGameData ? "" : " (Game data not loaded - some checks skipped.)");
            UpdateButtons();
        }

        private void ShowSelectedIssue()
        {
            if (_issueList.SelectedItems.Count == 0 || _issueList.SelectedItems[0].Tag is not HealthCheckService.Issue issue) return;

            _explanationLabel.Text = issue.Explanation + (issue.Rows.Count > MaxRowsShown ? $"  (Showing the first {MaxRowsShown:N0} of {issue.Rows.Count:N0} - Export lists all.)" : "");
            _rowList.BeginUpdate();
            _rowList.Items.Clear();
            foreach (HealthCheckService.IssueRow row in issue.Rows.Take(MaxRowsShown))
                _rowList.Items.Add(new ListViewItem(new[] { row.Table, $"0x{row.DbGuid:X}", row.Owner ?? "?", row.What, row.Detail }));
            _rowList.EndUpdate();
        }

        private List<HealthCheckService.Issue> TickedIssues() =>
            _issueList.CheckedItems.Cast<ListViewItem>().Select(i => (HealthCheckService.Issue)i.Tag).Where(i => i.CanFix).ToList();

        private async Task RunFix()
        {
            List<HealthCheckService.Issue> ticked = TickedIssues();
            if (ticked.Count == 0) return;

            string list = string.Join("\r\n", ticked.Select(i => $"  - {i.FixDescription}: {i.Rows.Count:N0} row(s)"));
            if (MessageBox.Show(this,
                    $"This will:\r\n{list}\r\n\r\nA backup of the whole database is made first.\r\n" +
                    "Ideally stop the server first: players who are online keep their in-memory copy and save over these changes.",
                    "Fix ticked issues", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            SetBusy(true, cancellable: false);
            Progress<string> progress = new(text => { if (IsDisposed == false) _statusLabel.Text = text; });
            bool compact = _compactCheck.Checked;
            try
            {
                var (backupPath, results) = await Task.Run(() =>
                {
                    ((IProgress<string>)progress).Report("Creating backup...");
                    string path = _connectionManager.CreateBackup();
                    List<string> fixResults = _service.Fix(ticked, progress);
                    if (compact) _service.Compact(progress);
                    return (path, fixResults);
                });

                MessageBox.Show(this, string.Join("\r\n", results) + (compact ? "\r\nDatabase compacted." : "") + $"\r\n\r\nBackup: {backupPath}",
                    "Fixed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message + "\r\n\r\nThe fixes run in one transaction, so nothing was changed unless the error was during compacting.",
                    "Fix failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, cancellable: false);
            }

            await RunScan();
        }

        private void ExportRows()
        {
            if (_issueList.SelectedItems.Count == 0 || _issueList.SelectedItems[0].Tag is not HealthCheckService.Issue issue) return;

            using SaveFileDialog dialog = new() { Filter = "CSV (*.csv)|*.csv", FileName = $"{issue.Kind}.csv" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            StringBuilder csv = new("Table,DbGuid,Owner,What,Detail\r\n");
            foreach (HealthCheckService.IssueRow row in issue.Rows)
                csv.Append(string.Join(",", new[] { row.Table, $"0x{row.DbGuid:X}", row.Owner, row.What, row.Detail }.Select(Csv))).Append("\r\n");
            File.WriteAllText(dialog.FileName, csv.ToString());
        }

        private static string Csv(string value) =>
            value == null ? "" : value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

        private void SetBusy(bool busy, bool cancellable)
        {
            _busy = busy;
            _cancel?.Dispose();
            _cancel = busy && cancellable ? new CancellationTokenSource() : null;
            _scanButton.Enabled = busy == false || cancellable;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            _fixButton.Enabled = _busy == false && _report != null && TickedIssues().Count > 0;
            _exportButton.Enabled = _busy == false && _report != null && _report.Issues.Count > 0;
            _compactCheck.Enabled = _busy == false;
            _issueList.Enabled = _busy == false;
        }
    }
}
