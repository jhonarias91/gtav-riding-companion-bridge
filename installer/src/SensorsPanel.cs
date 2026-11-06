// Sensors tab: which source GTBike V uses for each function and its rule (Auto / Lock / Skip), plus disconnecting a
// Bluetooth sensor, through devices.configure and devices.disconnect like the Android app. GTBike V itself finds and
// connects sensors; finding a new Bluetooth sensor to assign stays in the phone app (it scans with the phone).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class SensorsPanel : BridgePanel
    {
        readonly ListView list = List("Function|170", "In use|300", "Connection|110", "Rule|180");
        readonly Label detail = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 4, 0, 12) };
        readonly Button auto = Ui.Secondary("Auto"), lockButton = Ui.Secondary("Lock to this device"), skip = Ui.Secondary("Skip this device"), disconnect = Ui.Secondary("Disconnect");
        Dictionary<string, object> doc;

        public SensorsPanel(Func<BridgeClient> client) : base(client, 1500)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(16, 12, 16, 0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 460));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.BringToFront();
            var intro = Note("Sensors are found and connected by GTBike V on this PC. Here you choose which source the game uses for each function. To add a new Bluetooth sensor, use Find sensors in the Android app.", 900);
            layout.Controls.Add(intro, 0, 0);
            layout.SetColumnSpan(intro, 2);
            list.Margin = new Padding(0, 0, 12, 8);
            list.SelectedIndexChanged += (s, e) => ShowSelected();
            layout.Controls.Add(list, 0, 1);

            var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Ui.CardBack, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 8) };
            side.Controls.Add(Title("Rule for this function"));
            side.Controls.Add(detail);
            foreach (var b in new[] { auto, lockButton, skip, disconnect }) { b.Width = 220; b.Margin = new Padding(0, 0, 0, 8); side.Controls.Add(b); }
            side.Controls.Add(Note("Auto: the game takes the first suitable device. Lock: always this device. Skip: never this device; the game looks for another one.", 400));
            auto.Click += async (s, e) => await Configure("auto");
            lockButton.Click += async (s, e) => await Configure("lock");
            skip.Click += async (s, e) => await Configure("skip");
            disconnect.Click += async (s, e) => await Disconnect();
            layout.Controls.Add(side, 1, 1);
            ShowSelected();
        }

        protected override async Task Refresh(BridgeClient c)
        {
            var result = await c.GetAsync("/api/v1/device-roles");
            doc = result.Ok ? result.Body : null;
            string selected = Role == null ? null : Json.Str(Role, "role");
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var role in Json.List(doc, "roles").Where(r => Json.Bool(r, "visible") == true))
            {
                var current = Json.Obj(role, "current");
                string policy = Json.Str(role, "policy");
                var item = new ListViewItem(new[] {
                    Fmt.Role(Json.Str(role, "role")), Device(current), current == null ? "" : Json.Str(current, "source") == "BLE" ? "Bluetooth" : "ANT+",
                    policy == "locked" ? "Locked to a device" : policy == "filtered" ? "Some devices skipped" : "Automatic" }) { Tag = Json.Str(role, "role") };
                list.Items.Add(item);
                if ((string)item.Tag == selected) item.Selected = true;
            }
            list.EndUpdate();
            string reason = Json.Str(doc, "reasonCode");
            if (!result.Ok) Info(Reasons.Text(result.ErrorCode));
            else if (reason != null) Info(Reasons.Text(reason));
            else if (list.Items.Count == 0) Info("GTBike V has not reported any sensor functions yet. Activate the mod in the Mod tab.");
            ShowSelected();
        }

        static string Device(Dictionary<string, object> current)
        {
            if (current == null) return "None";
            string name = Json.Str(current, "name");
            string id = Json.Str(current, "deviceId") ?? "";
            string type = Json.Str(current, "sensorType");
            if (name != null) return name + " (" + type + ")";
            if (Json.Str(current, "source") == "BLE" && id.Length > 6)
            {
                ulong address;
                if (UInt64.TryParse(id, out address) && address < (1UL << 48)) id = String.Join(":", Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xff).ToString("X2")).ToArray());
            }
            return type + " " + id;
        }

        Dictionary<string, object> Role
        {
            get
            {
                if (list.SelectedItems.Count == 0) return null;
                string id = (string)list.SelectedItems[0].Tag;
                return Json.List(doc, "roles").FirstOrDefault(r => Json.Str(r, "role") == id);
            }
        }

        void ShowSelected()
        {
            var role = Role;
            var actions = Json.Obj(role, "actions");
            var current = Json.Obj(role, "current");
            detail.Text = role == null ? "Choose a function in the list." : Fmt.Role(Json.Str(role, "role")) + ": " + Device(current);
            auto.Enabled = !Busy && Json.Bool(actions, "auto") == true;
            lockButton.Enabled = !Busy && Json.Bool(actions, "lock") == true && current != null;
            skip.Enabled = !Busy && Json.Bool(actions, "skip") == true && current != null;
            disconnect.Enabled = !Busy && current != null && Json.Str(current, "source") == "BLE";
            Tips.SetToolTip(disconnect, current != null && Json.Str(current, "source") != "BLE" ? "Only Bluetooth sensors can be disconnected from here." : null);
        }

        protected override void OnBusyChanged() { ShowSelected(); }

        async Task Configure(string action)
        {
            var role = Role;
            if (role == null) return;
            var current = Json.Obj(role, "current");
            string label = Fmt.Role(Json.Str(role, "role"));
            await Run("devices.configure", new Dictionary<string, object> {
                    { "role", Json.Str(role, "role") }, { "action", action },
                    { "expectedDeviceId", action == "auto" ? null : Json.Str(current, "deviceId") },
                    { "expectedSource", action == "auto" ? null : Json.Str(current, "source") } },
                Json.Str(doc, "devicesRevision"), "Changing the rule for " + label + "…",
                label + ": " + (action == "auto" ? "automatic." : action == "lock" ? "locked to " + Device(current) + "." : Device(current) + " skipped."));
        }

        async Task Disconnect()
        {
            var role = Role;
            var current = Json.Obj(role, "current");
            if (current == null || Json.Str(current, "source") != "BLE") return;
            if (!Confirm("Disconnect " + Device(current) + "?", "The PC drops this Bluetooth sensor and every function that used it goes back to Auto. It can connect again later.", "Disconnect")) return;
            await Run("devices.disconnect", new Dictionary<string, object> { { "sensorType", Json.Str(current, "sensorType") }, { "deviceId", Json.Str(current, "deviceId") } },
                Json.Str(doc, "devicesRevision"), "Disconnecting…", Device(current) + " disconnected.");
        }
    }
}
