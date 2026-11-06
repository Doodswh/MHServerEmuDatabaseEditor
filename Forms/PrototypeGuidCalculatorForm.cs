using System.Windows.Forms;
using MHDBEditor.Utility;

namespace MHDBEditor.Forms
{
    // Computes the DataRef (PrototypeId) for a prototype/resource path using the same
    // Adler32+CRC32 hash the server uses (ported in Utility.HashHelper).
    //
    // IMPORTANT: this is NOT the same number space as DBEntity.EntityProtoGuid /
    // InventoryProtoGuid in the database - those hold PrototypeGuid values, a stable id baked
    // directly into the game's data files (looked up via GameDatabase, not computed from a
    // path). There is no way to compute a PrototypeGuid from a path string. Use
    // "Tools > Load Prototype Name List..." (a guid->name file, e.g. dumped from a running
    // server) to resolve those DB columns instead - this calculator is for DataRef only,
    // which shows up in other contexts (e.g. matching against known constants/asset paths).
    public class PrototypeGuidCalculatorForm : Form
    {
        public PrototypeGuidCalculatorForm()
        {
            Text = "Compute Prototype DataRef (not the database ProtoGuid)";
            Width = 560;
            Height = 260;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            Label warningLabel = new()
            {
                Text = "This computes a DataRef (path hash), NOT the ProtoGuid stored in EntityProtoGuid/\nInventoryProtoGuid - those need a guid->name list loaded instead (Tools menu).",
                AutoSize = false,
                Left = 12,
                Top = 8,
                Width = 520,
                Height = 34,
                ForeColor = System.Drawing.Color.DarkRed,
            };

            Label pathLabel = new() { Text = "Path:", AutoSize = true, Left = 12, Top = 52 };
            TextBox pathBox = new() { Left = 90, Top = 48, Width = 440 };

            RadioButton calligraphyRadio = new() { Text = "Calligraphy path (e.g. Entity/Items/.../X.prototype)", Left = 12, Top = 80, Width = 500, Checked = true };
            RadioButton resourceRadio = new() { Text = "Resource path", Left = 12, Top = 104, Width = 200 };

            Button computeButton = new() { Text = "Compute", Left = 12, Top = 138, Width = 90 };

            Label resultDecLabel = new() { Text = "DataRef (decimal):", AutoSize = true, Left = 12, Top = 180 };
            TextBox resultDecBox = new() { Left = 130, Top = 176, Width = 180, ReadOnly = true };
            Label resultHexLabel = new() { Text = "DataRef (hex):", AutoSize = true, Left = 322, Top = 180 };
            TextBox resultHexBox = new() { Left = 410, Top = 176, Width = 120, ReadOnly = true };

            computeButton.Click += (_, _) =>
            {
                string path = pathBox.Text.Trim();
                if (path.Length == 0) return;

                string prepared = calligraphyRadio.Checked
                    ? HashHelper.ToCalligraphyPath(path)
                    : HashHelper.ToResourcePath(path);

                ulong guid = HashHelper.HashPath(prepared);
                resultDecBox.Text = guid.ToString();
                resultHexBox.Text = $"0x{guid:X}";
            };

            Controls.AddRange(new Control[]
            {
                warningLabel, pathLabel, pathBox, calligraphyRadio, resourceRadio, computeButton,
                resultDecLabel, resultDecBox, resultHexLabel, resultHexBox,
            });
        }
    }
}
