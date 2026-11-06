// Shared look for the window: colours, fonts and small control factories (WinForms, no designer).
using System;
using System.Drawing;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    static class Ui
    {
        internal static readonly Color Surface = Color.FromArgb(244, 246, 250);
        internal static readonly Color CardBack = Color.White;
        internal static readonly Color Border = Color.FromArgb(221, 227, 236);
        internal static readonly Color Text = Color.FromArgb(31, 41, 55);
        internal static readonly Color Muted = Color.FromArgb(100, 110, 125);
        internal static readonly Color Accent = Color.FromArgb(232, 113, 26);
        internal static readonly Color AccentDark = Color.FromArgb(200, 92, 14);
        internal static readonly Color Header = Color.FromArgb(17, 24, 39);
        internal static readonly Color Good = Color.FromArgb(21, 128, 61);
        internal static readonly Color Warn = Color.FromArgb(180, 83, 9);
        internal static readonly Color Bad = Color.FromArgb(185, 28, 28);
        internal const int CardLabelWidth = 160;
        internal static readonly Font Body =new Font("Segoe UI", 10f);
        internal static readonly Font Heading = new Font("Segoe UI Semibold", 12f);
        internal static readonly Font Title = new Font("Segoe UI Semibold", 17f);

        internal static Label Value() { return new Label { AutoSize = true, Text = "—", ForeColor = Text, Margin = new Padding(3, 5, 3, 3) }; }

        internal static Button Primary(string text)
        {
            var button = new Button { Text = text, Height = 34, Width = 140, Margin = new Padding(0, 0, 8, 0), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            SetKind(button, true);
            return button;
        }

        internal static Button Secondary(string text)
        {
            var button = new Button { Text = text, Height = 34, Width = 140, Margin = new Padding(0, 0, 8, 0), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            SetKind(button, false);
            return button;
        }

        /// <summary>Primary = filled accent button; secondary = outlined.</summary>
        internal static void SetKind(Button button, bool primary)
        {
            if (!(button.Tag is bool)) button.EnabledChanged += (s, e) => Paint(button);
            button.Tag = primary;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.Font = primary ? new Font("Segoe UI Semibold", 10f) : Body;
            Paint(button);
        }

        /// <summary>Flat buttons keep their colours when disabled; grey them out explicitly.</summary>
        static void Paint(Button button)
        {
            bool primary = button.Tag is bool && (bool)button.Tag;
            bool on = button.Enabled;
            button.FlatAppearance.BorderColor = !on ? Border : primary ? AccentDark : Border;
            button.FlatAppearance.MouseOverBackColor = primary ? AccentDark : Color.FromArgb(235, 239, 245);
            button.BackColor = !on ? Color.FromArgb(238, 240, 244) : primary ? Accent : CardBack;
            button.ForeColor = !on ? Color.FromArgb(160, 167, 178) : primary ? Color.White : Text;
        }

        internal static NumericUpDown Number() { return new NumericUpDown { Width = 96, TextAlign = HorizontalAlignment.Right, Margin = new Padding(3, 3, 3, 3) }; }

        internal static CheckBox Check(string text) { return new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 4, 3, 4) }; }

        internal static ComboBox Combo() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Margin = new Padding(3, 3, 3, 3) }; }

        /// <summary>White box with a title; rows go in two columns (label, control).</summary>
        internal static TableLayoutPanel Card(string title, int width)
        {
            var card = new TableLayoutPanel
            {
                Width = width, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, BackColor = Ui.CardBack,
                Padding = new Padding(14, 10, 14, 12), Margin = new Padding(0, 0, 0, 12),
            };
            // Fixed column widths: percentage columns make an auto-sized TableLayoutPanel mis-measure its height.
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CardLabelWidth));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width - card.Padding.Horizontal - CardLabelWidth));
            card.Paint += (s, e) => { using (var pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1); };
            card.RowCount = 1;
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label { Text = title, Font = Heading, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            card.Controls.Add(label, 0, 0);
            card.SetColumnSpan(label, 2);
            return card;
        }

        internal static void Row(TableLayoutPanel card, string label, Control control)
        {
            int row = card.RowCount++;
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 6, 3, 3), MaximumSize = new Size(CardLabelWidth - 4, 0) }, 0, row);
            control.Width = Math.Min(control.Width, (int)card.ColumnStyles[1].Width - control.Margin.Horizontal);
            card.Controls.Add(control, 1, row);
        }

        internal static void Full(TableLayoutPanel card, Control control)
        {
            int row = card.RowCount++;
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(control, 0, row);
            card.SetColumnSpan(control, 2);
        }

        internal static void Status(Label label, string text, Color color)
        {
            label.Text = text;
            label.ForeColor = color;
        }

        internal static string Mark(bool ok) { return ok ? "✓" : "✗"; }
    }
}
