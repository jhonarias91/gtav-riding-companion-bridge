// Shared plumbing for the bridge-backed tabs: polling while visible, sending a command and following its operation
// until the game confirms it, confirmation dialogs and a status line. C# 5 only.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class OpResult
    {
        public bool Ok;
        public Dictionary<string, object> Op;
        public string Code;
    }

    static class Ops
    {
        /// <summary>POST /api/v1/commands with the adapter instance from a fresh state read, then follows the operation.</summary>
        internal static async Task<OpResult> Send(BridgeClient c, string type, Dictionary<string, object> args, string revision)
        {
            if (c == null) return new OpResult { Code = "BRIDGE_UNREACHABLE" };
            var state = await c.GetAsync("/api/v1/state");
            string instance = state.Ok ? Json.Str(state.Body, "adapterInstanceId") : null;
            if (instance == null) return new OpResult { Code = state.Ok ? "ADAPTER_ABSENT" : state.ErrorCode };
            string requestId = Guid.NewGuid().ToString();
            var result = await c.PostAsync("/api/v1/commands", new Dictionary<string, object> {
                { "requestId", requestId }, { "adapterInstanceId", instance }, { "type", type }, { "expectedRevision", revision }, { "args", args } });
            var op = result.Body;
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (result.Ok && op != null && new[] { "validated", "dispatched", "pending" }.Contains(Json.Str(op, "status")) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(300);
                var next = await c.GetAsync("/api/v1/operations/" + requestId);
                if (next.Ok) op = next.Body;
            }
            string status = Json.Str(op, "status");
            if (result.Ok && status == "applied") return new OpResult { Ok = true, Op = op };
            string code = !result.Ok ? result.ErrorCode : Json.Str(Json.Obj(op, "error"), "code") ?? (status == "unknown" ? "OPERATION_UNKNOWN" : "INTERNAL");
            return new OpResult { Op = op, Code = code };
        }
    }

    static class Fmt
    {
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        internal static string Km(double? metres) { return metres == null ? "—" : (metres.Value / 1000).ToString("0.0", En) + " km"; }
        internal static string M(double? metres) { return metres == null ? "—" : Math.Round(metres.Value).ToString("0", En) + " m"; }
        internal static string Duration(double? seconds)
        {
            if (seconds == null) return "—";
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds.Value));
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.ToString("mm\\:ss") : t.ToString("m\\:ss");
        }
        internal static string Num(double? value, string unit) { return value == null ? "—" : Math.Round(value.Value).ToString("0", En) + " " + unit; }
        internal static string Bytes(double? bytes) { return bytes == null ? "—" : bytes.Value >= 1048576 ? (bytes.Value / 1048576).ToString("0.0", En) + " MB" : Math.Ceiling(bytes.Value / 1024).ToString("0", En) + " KB"; }
        internal static string LocalTime(string iso)
        {
            DateTime parsed;
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", En) : (iso ?? "—");
        }
        internal static string Role(string role)
        {
            switch (role)
            {
                case "power": return "Power";
                case "resistance": return "Trainer resistance";
                case "cadence": return "Cadence";
                case "heartRate": return "Heart rate";
                case "steering": return "Steering";
                case "controls": return "Controls";
                case "runningSpeed": return "Running speed";
                default: return role;
            }
        }
    }

    /// <summary>Base for a tab that talks to the bridge: Active starts/stops polling; subclasses implement Refresh.</summary>
    abstract class BridgePanel : UserControl
    {
        protected readonly Func<BridgeClient> Client;
        protected readonly Label Message = new Label { AutoSize = false, Height = 44, Dock = DockStyle.Bottom, ForeColor = Ui.Muted, Padding = new Padding(16, 6, 16, 0) };
        protected readonly ToolTip Tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300 };
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        bool polling;
        protected bool Busy { get; private set; }
        protected int Ticks { get; private set; }

        protected BridgePanel(Func<BridgeClient> client, int intervalMs)
        {
            Client = client;
            Dock = DockStyle.Fill;
            BackColor = Ui.Surface;
            timer.Interval = intervalMs;
            timer.Tick += async (s, e) => await PollOnce();
            Controls.Add(Message);
        }

        public bool Active
        {
            set
            {
                timer.Enabled = value;
                if (value) { Ticks = 0; var ignored = PollOnce(); }
            }
        }

        async Task PollOnce()
        {
            if (polling || Busy) return;
            polling = true;
            try
            {
                BridgeClient c = Client();
                if (c == null) { Offline(); return; }
                await Refresh(c);
                Ticks++;
            }
            catch (Exception error) { Fail(error.Message); }
            finally { polling = false; }
        }

        /// <summary>Called on each poll with a live client.</summary>
        protected abstract Task Refresh(BridgeClient c);

        /// <summary>Called when the bridge is not running.</summary>
        protected virtual void Offline() { Info("Start the bridge (Phone tab) to use this tab."); }

        /// <summary>Runs one command with the busy state, a pending text and the result message.</summary>
        protected async Task<OpResult> Run(string type, Dictionary<string, object> args, string revision, string pendingText, string doneText)
        {
            if (Busy) { Fail("Wait for the previous action to finish."); return new OpResult { Code = "BRIDGE_BUSY" }; }
            Busy = true;
            OnBusyChanged();
            Info(pendingText);
            OpResult outcome;
            try
            {
                outcome = await Ops.Send(Client(), type, args, revision);
                if (outcome.Ok) Done(doneText); else Fail(Reasons.Text(outcome.Code));
            }
            catch (Exception error) { Fail(error.Message); outcome = new OpResult { Code = "INTERNAL" }; }
            finally
            {
                Busy = false;
                OnBusyChanged();
            }
            BridgeClient c = Client();
            if (c != null) { try { await Refresh(c); } catch (Exception) { } } // Show what the game now reports.
            return outcome;
        }

        protected virtual void OnBusyChanged() { }

        protected bool Confirm(string title, string body, string action)
        {
            using (var dialog = new ConfirmDialog(title, body, action)) return dialog.ShowDialog(FindForm()) == DialogResult.OK;
        }

        protected void Info(string text) { Message.ForeColor = Ui.Muted; Message.Text = text; }
        protected void Done(string text) { Message.ForeColor = Ui.Good; Message.Text = "✓ " + text; }
        protected void Fail(string text) { Message.ForeColor = Ui.Bad; Message.Text = text; }

        protected static Label Title(string text) { return new Label { Text = text, Font = Ui.Heading, AutoSize = true, Margin = new Padding(0, 0, 0, 4) }; }
        protected static Label Note(string text, int width) { return new Label { Text = text, ForeColor = Ui.Muted, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, 2, 0, 8) }; }

        protected static ListView List(params string[] columns)
        {
            var list = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
            foreach (string column in columns)
            {
                string[] parts = column.Split('|');
                list.Columns.Add(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 120, parts.Length > 2 && parts[2] == "r" ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            }
            return list;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); Tips.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Plain confirmation with a title, a wrapped explanation and an action button.</summary>
    sealed class ConfirmDialog : Form
    {
        internal ConfirmDialog(string title, string body, string action)
        {
            Text = Program.Name;
            Font = Ui.Body;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.CardBack;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(22, 18, 22, 16), WrapContents = false };
            layout.Controls.Add(new Label { Text = title, Font = Ui.Heading, AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 8) });
            layout.Controls.Add(new Label { Text = body, AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = Ui.Text, Margin = new Padding(0, 0, 0, 16) });
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = 480, Margin = Padding.Empty };
            var ok = Ui.Primary(action);
            ok.Width = Math.Max(140, TextRenderer.MeasureText(action, ok.Font).Width + 30);
            ok.DialogResult = DialogResult.OK;
            var cancel = Ui.Secondary("Cancel");
            cancel.Width = 100;
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
