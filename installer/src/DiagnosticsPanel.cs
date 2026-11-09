// Read-only bridge diagnostics. The support report is built from an explicit allowlist, never a raw response dump.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class DiagnosticsPanel : BridgePanel
    {
        static readonly string[] LayerKeys = { "adapter", "binding", "mod", "antReceiver", "bleReceiver", "gameControl" };
        static readonly string[] LayerNames = { "Game adapter", "Binding", "GTBike V", "ANT+ receiver", "Bluetooth receiver", "Game control" };
        readonly Dictionary<string, Label> values = new Dictionary<string, Label>();
        readonly ListView capabilityList = List("Capability|215", "Status|100", "Reason|440");
        readonly Label capabilityStatus = Note("Reading capabilities…", 680);
        readonly Button copy = Ui.Secondary("Copy support report");
        Dictionary<string, object> hello, state, capabilities;
        string helloError, stateError, capabilitiesError;

        public DiagnosticsPanel(Func<BridgeClient> client) : base(client, 2000)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(16, 12, 16, 0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 410));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.BringToFront();
            var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            header.Controls.Add(Title("Diagnostics"));
            header.Controls.Add(Note("Versions, game layers and the reason each control is available. The support report contains no tokens, pairing codes, PC addresses, device IDs, rider names or file paths.", 900));
            copy.Width = 200;
            copy.Click += (s, e) => CopyReport();
            header.Controls.Add(copy);
            layout.Controls.Add(header, 0, 0);
            layout.SetColumnSpan(header, 2);

            var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(0, 0, 12, 8) };
            var versions = Ui.Card("Versions", 380);
            AddField(versions, "app", "Windows app");
            AddField(versions, "bridge", "Bridge / API");
            AddField(versions, "transport", "Transport");
            AddField(versions, "modVersion", "GTBike V version");
            AddField(versions, "bindingProfile", "Binding profile");
            side.Controls.Add(versions);
            var layers = Ui.Card("Game layers", 380);
            for (int i = 0; i < LayerKeys.Length; i++) AddField(layers, LayerKeys[i], LayerNames[i]);
            AddField(layers, "age", "Snapshot age");
            side.Controls.Add(layers);
            layout.Controls.Add(side, 0, 1);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.Controls.Add(capabilityStatus, 0, 0);
            right.Controls.Add(capabilityList, 0, 1);
            layout.Controls.Add(right, 1, 1);
            UpdateState();
        }

        void AddField(TableLayoutPanel card, string key, string name)
        {
            var label = Ui.Value();
            label.MaximumSize = new Size(185, 0);
            values[key] = label;
            Ui.Row(card, name, label);
        }

        protected override async Task Refresh(BridgeClient c)
        {
            var identityRead = c.GetAsync("/api/v1/hello");
            var stateRead = c.GetAsync("/api/v1/state");
            var capabilitiesRead = c.GetAsync("/api/v1/capabilities");
            await Task.WhenAll(identityRead, stateRead, capabilitiesRead);
            hello = identityRead.Result.Ok ? identityRead.Result.Body : null;
            state = stateRead.Result.Ok ? stateRead.Result.Body : null;
            capabilities = capabilitiesRead.Result.Ok ? capabilitiesRead.Result.Body : null;
            helloError = ReadError(identityRead.Result);
            stateError = ReadError(stateRead.Result);
            capabilitiesError = ReadError(capabilitiesRead.Result);
            UpdateState();
            if (capabilitiesError != null) Info("Capabilities: " + Reasons.Text(capabilitiesError));
            else if (stateError != null) Info("Game state: " + Reasons.Text(stateError));
            else if (helloError != null) Info("Bridge identity: " + Reasons.Text(helloError));
            else Info("Values are read from this PC bridge.");
        }

        static string ReadError(ApiResult result) { return result.Ok && result.Body != null && result.ErrorCode == null ? null : result.ErrorCode ?? "INVALID_RESPONSE"; }

        protected override void Offline()
        {
            hello = state = capabilities = null;
            helloError = stateError = capabilitiesError = "BRIDGE_UNREACHABLE";
            UpdateState();
            base.Offline();
        }

        void UpdateState()
        {
            values["app"].Text = BuildInfo.Version;
            values["bridge"].Text = hello == null ? "—" : Safe(Json.Str(hello, "bridgeVersion")) + " / API " + Json.Int(hello, "apiMajor") + "." + Json.Int(hello, "apiMinor");
            values["transport"].Text = hello == null ? "—" : Json.Str(hello, "transport") == "https" ? "HTTPS" : "HTTP (development)";
            values["modVersion"].Text = Safe(Json.Str(state, "modVersion"));
            values["bindingProfile"].Text = Safe(Json.Str(state, "bindingProfile"));
            var layers = Json.Obj(state, "layers");
            foreach (string key in LayerKeys) values[key].Text = Safe(Json.Str(layers, key));
            double? age = Json.Num(state, "snapshotAgeMs");
            values["age"].Text = age == null ? "—" : age.Value.ToString("0", CultureInfo.InvariantCulture) + " ms";
            capabilityList.BeginUpdate();
            capabilityList.Items.Clear();
            foreach (var capability in Json.List(capabilities, "capabilities"))
            {
                bool available = Json.Bool(capability, "available") == true;
                string reason = Json.Str(capability, "reasonCode");
                capabilityList.Items.Add(new ListViewItem(new[] { Safe(Json.Str(capability, "id")), available ? "Available" : "Unavailable",
                    available ? "" : Safe(reason) + " · " + Reasons.Text(reason) }) { ForeColor = available ? Ui.Good : Ui.Muted });
            }
            capabilityList.EndUpdate();
            capabilityStatus.Text = capabilitiesError != null ? "Capabilities unavailable: " + Safe(capabilitiesError) + ". " + Reasons.Text(capabilitiesError)
                : capabilities == null ? "Waiting for capabilities…" : capabilityList.Items.Count + " capabilities reported by the bridge.";
        }

        // Only these metadata fields enter a report; state IDs, metrics, sensors, profiles and private paths are excluded.
        internal string ReportText { get { return Report(); } }

        string Report()
        {
            var report = new StringBuilder();
            report.AppendLine(Program.Name + " Windows " + BuildInfo.Version);
            report.AppendLine("Windows " + Environment.OSVersion.Version);
            report.AppendLine("Bridge: " + values["bridge"].Text + " / " + values["transport"].Text);
            report.AppendLine("Mod: " + values["modVersion"].Text + " / profile " + values["bindingProfile"].Text);
            report.AppendLine("Layers: " + String.Join(" ", LayerKeys.Select(key => key + "=" + values[key].Text).ToArray()));
            report.AppendLine("Snapshot age: " + values["age"].Text);
            report.AppendLine("Input: " + (Json.Str(Json.Obj(state, "testInput"), "mode") == "keyboard" ? "keyboard test (simulated)" : "normal"));
            if (helloError != null) report.AppendLine("Bridge identity error: " + Safe(helloError));
            if (stateError != null) report.AppendLine("Game state error: " + Safe(stateError));
            if (capabilitiesError != null) report.AppendLine("Capabilities error: " + Safe(capabilitiesError));
            foreach (var capability in Json.List(capabilities, "capabilities"))
                report.AppendLine(Safe(Json.Str(capability, "id")) + ": " + (Json.Bool(capability, "available") == true ? "ok" : Safe(Json.Str(capability, "reasonCode"))));
            return report.ToString();
        }

        static string Safe(string value)
        {
            if (String.IsNullOrEmpty(value)) return "—";
            value = value.Replace('\r', ' ').Replace('\n', ' ');
            return value.Length > 128 ? value.Substring(0, 128) : value;
        }

        void CopyReport()
        {
            try { Clipboard.SetText(Report()); Done("Support report copied."); }
            catch (ExternalException) { Fail("The clipboard is busy. Try again."); }
        }
    }
}
