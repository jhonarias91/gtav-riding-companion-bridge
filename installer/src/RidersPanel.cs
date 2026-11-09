// Same read-only multiplayer board as Android; session changes remain in GTBike V's own menu.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class RidersPanel : BridgePanel
    {
        readonly Label connection = Ui.Value(), course = Ui.Value(), busiest = Ui.Value();
        readonly DataGridView riders = new DataGridView {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoGenerateColumns = false, RowHeadersVisible = false, BackgroundColor = Ui.CardBack, BorderStyle = BorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false
        };

        public RidersPanel(Func<BridgeClient> client) : base(client, 2000)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var summary = Ui.Card("Riders in GTA V", 640);
            Ui.Row(summary, "Multiplayer", connection); Ui.Row(summary, "Current route", course); Ui.Row(summary, "Busiest route", busiest);
            layout.Controls.Add(summary, 0, 0);
            layout.Controls.Add(Note("Observed ranking, including your rider and bots. Join another route from GTBike V's game menu.", 900), 0, 1);
            foreach (string name in new[] { "Rank", "Rider", "Type", "W/kg", "Speed (km/h)" })
                riders.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, SortMode = DataGridViewColumnSortMode.NotSortable,
                    FillWeight = name == "Rider" ? 200 : name == "Rank" ? 40 : 90 });
            layout.Controls.Add(riders, 0, 2);
            Controls.Add(layout); layout.BringToFront();
        }

        protected override async Task Refresh(BridgeClient client)
        {
            var caps = await client.GetAsync("/api/v1/capabilities");
            if (!caps.Ok) { Clear(); Fail(Reasons.Text(caps.ErrorCode)); return; }
            Dictionary<string, object> capability = null;
            foreach (var item in Json.List(caps.Body, "capabilities"))
                if (Json.Str(item, "id") == "riders.read") { capability = item; break; }
            if (Json.Bool(capability, "available") != true) { Clear(); Info(Reasons.Text(Json.Str(capability, "reasonCode") ?? "CAPABILITY_UNAVAILABLE")); return; }
            var result = await client.GetAsync("/api/v1/riders");
            if (!result.Ok) { Clear(); Fail(Reasons.Text(result.ErrorCode)); return; }
            bool connected = Json.Bool(result.Body, "connected") == true;
            Ui.Status(connection, connected ? "Connected" : "Offline", connected ? Ui.Good : Ui.Muted);
            Ui.Status(course, Course(Json.Obj(result.Body, "course")), Ui.Text);
            Ui.Status(busiest, Course(Json.Obj(result.Body, "busiestRoute")), Ui.Text);
            riders.Rows.Clear();
            foreach (var rider in Json.List(result.Body, "riders"))
            {
                string kind = Json.Str(rider, "kind");
                string type = kind == "local" ? "You" : kind == "human" ? "Player" : kind == "bot" ? "Bot" : "Unknown";
                double? watts = Json.Num(rider, "wattsPerKg"), speed = Json.Num(rider, "speedMps");
                riders.Rows.Add(Json.Int(rider, "rank"), Json.Str(rider, "name") ?? "—", type,
                    watts == null ? "—" : watts.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    speed == null ? "—" : (speed.Value * 3.6).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            }
            Info(riders.Rows.Count == 0 ? "No riders reported by the game." : "Ranking reported by GTBike V.");
        }

        static string Course(Dictionary<string, object> value)
        {
            return value == null ? "—" : "Route " + Json.Str(value, "id") + (Json.Bool(value, "reverse") == true ? " (reverse)" : "");
        }
        void Clear()
        {
            riders.Rows.Clear(); Ui.Status(connection, "—", Ui.Muted); Ui.Status(course, "—", Ui.Muted); Ui.Status(busiest, "—", Ui.Muted);
        }
        protected override void Offline() { Clear(); base.Offline(); }
    }
}
