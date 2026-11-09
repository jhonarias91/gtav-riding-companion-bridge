// Observed GTA bicycle camera, through the same typed camera commands and operation readback as Android.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class CameraPanel : BridgePanel
    {
        static readonly string[] ModeIds = { "near", "medium", "far", "firstPerson", "cinematic" };
        static readonly string[] ModeNames = { "Near", "Medium", "Far", "First person", "Cinematic" };
        readonly Dictionary<string, Button> modes = new Dictionary<string, Button>();
        readonly Label current = new Label { Font = Ui.Heading, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        readonly Label modeReason = Note("Reading camera…", 740), lookReason = Note("", 700), observed = Note("", 700);
        readonly Label headingLabel = Note("Heading", 700), pitchLabel = Note("Pitch", 700);
        readonly Button next = Ui.Secondary("Next view"), left = Ui.Secondary("Look left"), right = Ui.Secondary("Look right"), center = Ui.Secondary("Center"), reset = Ui.Secondary("Reset direction");
        readonly TrackBar heading = new TrackBar { Minimum = -180, Maximum = 180, SmallChange = 5, LargeChange = 30, TickFrequency = 30, Width = 680, Height = 45, AccessibleName = "Camera heading" };
        readonly TrackBar pitch = new TrackBar { Minimum = -45, Maximum = 45, SmallChange = 5, LargeChange = 15, TickFrequency = 15, Width = 680, Height = 45, AccessibleName = "Camera pitch" };
        readonly HashSet<TrackBar> editing = new HashSet<TrackBar>(), dirty = new HashSet<TrackBar>();
        Dictionary<string, object> camera, capabilities;
        string errorCode;
        bool loading;

        public CameraPanel(Func<BridgeClient> client) : base(client, 1000)
        {
            var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16, 12, 16, 12) };
            Controls.Add(page);
            page.BringToFront();
            page.Controls.Add(Title("Camera"));
            page.Controls.Add(Note("Choose the bicycle camera in GTA V. The selected view and angles below are read from the game; changes wait for its confirmation.", 740));
            page.Controls.Add(current);
            var choices = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(740, 0), Margin = new Padding(0, 0, 0, 8) };
            for (int i = 0; i < ModeIds.Length; i++)
            {
                string id = ModeIds[i];
                var button = Ui.Secondary(ModeNames[i]);
                button.Width = 130;
                button.Margin = new Padding(0, 0, 8, 6);
                button.Click += async (s, e) => await SelectMode(id);
                modes[id] = button;
                choices.Controls.Add(button);
            }
            page.Controls.Add(choices);
            next.Click += async (s, e) => {
                if (Block("camera.next") == null) await Send("camera.next", new Dictionary<string, object>());
            };
            page.Controls.Add(next);
            modeReason.Margin = new Padding(0, 8, 0, 12);
            page.Controls.Add(modeReason);

            var orbit = new FlowLayoutPanel { Width = 740, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.CardBack, Padding = new Padding(16, 12, 16, 12), Margin = Padding.Empty };
            orbit.Controls.Add(Title("Look direction"));
            orbit.Controls.Add(observed);
            var actions = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            left.Click += async (s, e) => await Turn(30);
            right.Click += async (s, e) => await Turn(-30);
            center.Click += async (s, e) => await Look(0, 0);
            actions.Controls.AddRange(new Control[] { left, right, center });
            orbit.Controls.Add(actions);
            orbit.Controls.Add(Note("Release a slider to apply its angle. You can also adjust it with the arrow keys.", 700));
            orbit.Controls.Add(headingLabel);
            orbit.Controls.Add(heading);
            orbit.Controls.Add(pitchLabel);
            orbit.Controls.Add(pitch);
            WireSlider(heading);
            WireSlider(pitch);
            reset.Width = 180;
            reset.Click += async (s, e) => await Look(0, 0);
            orbit.Controls.Add(reset);
            orbit.Controls.Add(Note("Reset centers heading and pitch. Distance follows the Near, Medium and Far presets; GTA V does not expose a continuous distance control here.", 700));
            orbit.Controls.Add(lookReason);
            page.Controls.Add(orbit);
            Info("Camera changes are confirmed by GTA V.");
            UpdateState();
        }

        protected override async Task Refresh(BridgeClient c)
        {
            var cameraRead = c.GetAsync("/api/v1/camera");
            var capabilitiesRead = c.GetAsync("/api/v1/capabilities");
            await Task.WhenAll(cameraRead, capabilitiesRead);
            camera = cameraRead.Result.Ok ? cameraRead.Result.Body : null;
            capabilities = capabilitiesRead.Result.Ok ? capabilitiesRead.Result.Body : null;
            errorCode = !cameraRead.Result.Ok ? cameraRead.Result.ErrorCode : !capabilitiesRead.Result.Ok ? capabilitiesRead.Result.ErrorCode : null;
            UpdateState();
        }

        protected override void Offline()
        {
            camera = capabilities = null;
            errorCode = "BRIDGE_UNREACHABLE";
            editing.Clear();
            dirty.Clear();
            UpdateState();
            base.Offline();
        }

        string Block(string capability)
        {
            if (Busy || Json.Bool(camera, "busy") == true) return "BRIDGE_BUSY";
            if (errorCode != null) return errorCode;
            var cap = Json.List(capabilities, "capabilities").FirstOrDefault(c => Json.Str(c, "id") == capability);
            if (cap == null) return capabilities == null ? "CAPABILITY_UNAVAILABLE" : "PHASE_NOT_AVAILABLE";
            if (Json.Bool(cap, "available") != true) return Json.Str(cap, "reasonCode") ?? "CAPABILITY_UNAVAILABLE";
            if (Json.Bool(camera, "available") != true) return Json.Str(camera, "reasonCode") ?? "CAPABILITY_UNAVAILABLE";
            return null;
        }

        string LookBlock()
        {
            return Block("camera.look") ?? Json.Str(camera, "lookReasonCode")
                ?? (Json.Bool(camera, "canLook") != true || Json.Num(camera, "headingDeg") == null || Json.Num(camera, "pitchDeg") == null ? "CAPABILITY_UNAVAILABLE" : null);
        }

        static string Explain(string code)
        {
            if (code == "NOT_FOUND" || code == "PHASE_NOT_AVAILABLE") return "Update the PC bridge and game adapter to use camera controls.";
            if (code == "CAMERA_CONTROL_IN_USE") return "GTBike V is controlling the look direction. Release Look Behind in the game and try again.";
            return Reasons.Text(code);
        }

        void UpdateState()
        {
            bool available = errorCode == null && Json.Bool(camera, "available") == true;
            string selected = available ? Json.Str(camera, "currentMode") : null;
            int selectedIndex = Array.IndexOf(ModeIds, selected);
            current.Text = "Current view: " + (selectedIndex >= 0 ? ModeNames[selectedIndex] : "—");
            var supported = Json.Get(camera, "modes") as IList;
            string modeBlock = Block("camera.setMode"), nextBlock = Block("camera.next"), lookBlock = LookBlock();
            foreach (var pair in modes)
            {
                bool isSelected = pair.Key == selected;
                if (!(pair.Value.Tag is bool) || (bool)pair.Value.Tag != isSelected) Ui.SetKind(pair.Value, isSelected);
                pair.Value.Enabled = modeBlock == null && supported != null && supported.Contains(pair.Key);
                Tips.SetToolTip(pair.Value, modeBlock == null ? null : Explain(modeBlock));
            }
            next.Enabled = nextBlock == null;
            modeReason.Text = nextBlock != null ? Explain(nextBlock) : modeBlock != null ? Explain(modeBlock) : "";
            double? h = available ? Json.Num(camera, "headingDeg") : null, p = available ? Json.Num(camera, "pitchDeg") : null;
            observed.Text = h == null || p == null ? "Observed direction: —" : "Observed direction: heading " + Degrees(h.Value) + ", pitch " + Degrees(p.Value) + ".";
            loading = true;
            try
            {
                if (!editing.Contains(heading) && !dirty.Contains(heading)) heading.Value = Math.Max(heading.Minimum, Math.Min(heading.Maximum, (int)Math.Round(h ?? 0)));
                if (!editing.Contains(pitch) && !dirty.Contains(pitch)) pitch.Value = Math.Max(pitch.Minimum, Math.Min(pitch.Maximum, (int)Math.Round(p ?? 0)));
            }
            finally { loading = false; }
            UpdateDraftLabels();
            foreach (Control control in new Control[] { left, right, center, reset, heading, pitch }) control.Enabled = lookBlock == null;
            lookReason.Text = lookBlock == null ? "" : lookBlock == "CAPABILITY_UNAVAILABLE" && nextBlock == null
                ? "Look direction is available in the Near, Medium and Far views." : Explain(lookBlock);
        }

        static string Degrees(double value) { return value.ToString("0.#", CultureInfo.InvariantCulture) + "°"; }
        void UpdateDraftLabels() { headingLabel.Text = "Heading: " + Degrees(heading.Value); pitchLabel.Text = "Pitch: " + Degrees(pitch.Value); }
        protected override void OnBusyChanged() { UpdateState(); }

        void WireSlider(TrackBar slider)
        {
            slider.MouseDown += (s, e) => editing.Add(slider);
            slider.KeyDown += (s, e) => { if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Home || e.KeyCode == Keys.End || e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown) editing.Add(slider); };
            slider.ValueChanged += (s, e) => { if (!loading) UpdateDraftLabels(); };
            slider.Scroll += (s, e) => { if (!loading) dirty.Add(slider); };
            slider.MouseUp += async (s, e) => await CommitSlider(slider);
            slider.KeyUp += async (s, e) => await CommitSlider(slider);
            // TrackBar raises MouseWheel before its native scroll has updated Value.
            slider.MouseWheel += (s, e) => BeginInvoke(new Action(async () => { if (!IsDisposed) await CommitSlider(slider); }));
            slider.LostFocus += async (s, e) => await CommitSlider(slider);
        }

        async Task CommitSlider(TrackBar slider)
        {
            editing.Remove(slider);
            if (!dirty.Remove(slider)) return;
            if (LookBlock() != null) { UpdateState(); return; }
            double h = slider == heading ? heading.Value : Json.Num(camera, "headingDeg").Value;
            double p = slider == pitch ? pitch.Value : Json.Num(camera, "pitchDeg").Value;
            await Look(h, p);
        }

        async Task SelectMode(string mode)
        {
            var supported = Json.Get(camera, "modes") as IList;
            if (Block("camera.setMode") != null || supported == null || !supported.Contains(mode) || mode == Json.Str(camera, "currentMode")) return;
            await Send("camera.setMode", new Dictionary<string, object> { { "mode", mode } });
        }

        async Task Turn(double delta)
        {
            if (LookBlock() != null) return;
            await Look(Json.Num(camera, "headingDeg").Value + delta, Json.Num(camera, "pitchDeg").Value);
        }

        async Task Look(double h, double p)
        {
            if (LookBlock() != null || Double.IsNaN(h) || Double.IsNaN(p) || Double.IsInfinity(h) || Double.IsInfinity(p)) return;
            h = ((h + 180) % 360 + 360) % 360 - 180;
            p = Math.Max(-45, Math.Min(45, p));
            await Send("camera.look", new Dictionary<string, object> { { "headingDeg", h }, { "pitchDeg", p } });
        }

        async Task Send(string type, Dictionary<string, object> args)
        {
            var result = await Run(type, args, null, "Changing the camera…", "Camera change confirmed by GTA V.");
            if (!result.Ok) Fail(Explain(result.Code));
        }
    }
}
