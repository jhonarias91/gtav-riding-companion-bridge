// Native virtual gears: the same live commands and closed-game INI configuration as Android.
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
    sealed class TrainerPanel : BridgePanel
    {
        readonly Label observed = Note("Waiting for the game…", 720), liveReason = Note("", 720), source = Note("", 720), configReason = Note("", 720);
        readonly Button down = Ui.Secondary("−  Lower gear"), up = Ui.Secondary("Higher gear  +");
        readonly CheckBox enabled = Ui.Check("Enable native virtual gears at the next game start");
        readonly TextBox ratios = new TextBox { Width = 690, Multiline = true, Height = 68, ScrollBars = ScrollBars.Vertical, AccessibleName = "Native gear ratios" };
        readonly Button save = Ui.Primary("Save changes"), discard = Ui.Secondary("Discard"), defaults = Ui.Secondary("Restore defaults");
        Dictionary<string, object> live, config, state;
        bool filling, dirty, saving;
        string revision, liveError, configError;
        bool? originalEnabled;
        double[] originalRatios;

        public TrainerPanel(Func<BridgeClient> client) : base(client, 1500)
        {
            var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16, 12, 16, 12) };
            Controls.Add(page); page.BringToFront();
            page.Controls.Add(Title("Virtual gears"));
            page.Controls.Add(Note("Native GTBike V gearing. Live changes use the mod's own gear controls and wait for the game to confirm the selected gear.", 720));
            page.Controls.Add(observed); page.Controls.Add(source);
            var shift = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
            down.Width = 170; up.Width = 170;
            down.Click += async (s, e) => await Shift(false);
            up.Click += async (s, e) => await Shift(true);
            shift.Controls.AddRange(new Control[] { down, up }); page.Controls.Add(shift);
            page.Controls.Add(liveReason);
            page.Controls.Add(Title("Trainer configuration"));
            page.Controls.Add(Note("Close GTA V before changing these settings. GTBike V rewrites its INI while running. Changes apply at the next game start, and the bridge keeps a backup before writing.", 720));
            page.Controls.Add(configReason);
            enabled.CheckedChanged += (s, e) => Changed();
            ratios.TextChanged += (s, e) => Changed();
            page.Controls.Add(enabled);
            page.Controls.Add(Note("Ratios, from lowest to highest (comma separated, 1–64 values, 0.1–20, up to three decimal places).", 720));
            page.Controls.Add(ratios);
            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
            save.Width = 150; discard.Width = 110; defaults.Width = 160;
            save.Click += async (s, e) => await Save();
            discard.Click += (s, e) => { dirty = false; Fill(); };
            defaults.Click += (s, e) => FillDefaults();
            buttons.Controls.AddRange(new Control[] { save, discard, defaults }); page.Controls.Add(buttons);
            page.Controls.Add(Note("Enabled in the file and enabled in the current game can differ until GTA V restarts. ERG resistance control is separate; these buttons change only native virtual gears.", 720));
            UpdateState();
        }

        protected override async Task Refresh(BridgeClient c)
        {
            var a = c.GetAsync("/api/v1/trainer");
            var b = c.GetAsync("/api/v1/trainer-config");
            var d = c.GetAsync("/api/v1/state");
            await Task.WhenAll(a, b, d);
            live = a.Result.Ok ? a.Result.Body : null; liveError = a.Result.Ok ? null : a.Result.ErrorCode;
            config = b.Result.Ok ? b.Result.Body : null; configError = b.Result.Ok ? null : b.Result.ErrorCode;
            state = d.Result.Ok ? d.Result.Body : null;
            if (!dirty) Fill();
            UpdateState();
        }

        protected override void Offline()
        {
            live = config = state = null; liveError = configError = "BRIDGE_UNREACHABLE";
            UpdateState(); base.Offline();
        }

        static string Explain(string code)
        {
            if (code == "GEARS_DISABLED") return "Virtual gears are disabled in the current game. Configure them below with GTA V closed, then restart it.";
            if (code == "GEAR_LIMIT") return "The selected gear is already at this end of the range.";
            if (code == "NOT_FOUND" || code == "PHASE_NOT_AVAILABLE") return "Update the PC bridge and game adapter to use native virtual gears.";
            if (code == "GAME_RUNNING") return "GTA V is running. Close the game to edit this configuration.";
            return Reasons.Text(code);
        }

        string Block(bool higher)
        {
            if (Busy || saving || Json.Bool(live, "busy") == true) return "BRIDGE_BUSY";
            if (liveError != null) return liveError;
            if (Json.Bool(live, "available") != true) return Json.Str(live, "reasonCode") ?? "CAPABILITY_UNAVAILABLE";
            if (Json.Bool(live, higher ? "canShiftUp" : "canShiftDown") != true) return Json.Str(live, "shiftReasonCode") ?? "GEAR_LIMIT";
            return null;
        }

        void UpdateState()
        {
            bool readable = liveError == null && Json.Bool(live, "available") == true;
            string mode = Json.Str(live, "mode");
            string modeName = mode == "tempManual" ? "Temporary manual" : mode == "native" ? "Native trainer" : mode == "auto" ? "Automatic" : mode == "manual" ? "Manual" : mode == "none" ? "Disabled" : "Unknown";
            int? gear = readable ? Json.Int(live, "gear") : null, count = readable ? Json.Int(live, "gearCount") : null;
            double? ratio = readable ? Json.Num(live, "ratio") : null;
            observed.Text = readable ? "Current game: " + modeName + (gear.HasValue ? " · Gear " + gear + (count.HasValue ? " / " + count : "") : "") + (ratio.HasValue ? " · Ratio " + ratio.Value.ToString("0.###", CultureInfo.InvariantCulture) : "") : "Current game: unavailable";
            string adapter = Json.Str(Json.Obj(state, "layers"), "adapter");
            bool test = Json.Str(Json.Obj(state, "testInput"), "mode") == "keyboard";
            source.Text = adapter == "stale" ? "Game data is stale. Wait for fresh observations." : test ? "Keyboard test is active: pedalling is simulated." : "";
            source.ForeColor = Ui.Warn;
            string lower = Block(false), higher = Block(true);
            down.Enabled = lower == null; up.Enabled = higher == null;
            Tips.SetToolTip(down, lower == null ? null : Explain(lower)); Tips.SetToolTip(up, higher == null ? null : Explain(higher));
            string reason = liveError ?? Json.Str(live, "reasonCode") ?? Json.Str(live, "shiftReasonCode");
            liveReason.Text = reason == null ? "" : Explain(reason);
            bool editable = configError == null && Json.Bool(config, "available") == true && Json.Bool(config, "editable") == true && !Busy && !saving;
            enabled.Enabled = ratios.Enabled = editable;
            save.Enabled = editable && dirty; defaults.Enabled = editable; discard.Enabled = dirty && !saving && !Busy;
            string savedMode = Json.Str(config, "nativeMode");
            configReason.Text = configError != null ? Explain(configError) : config == null ? "Start the bridge to read GTBikeVConfig.ini." : Json.Bool(config, "gameRunning") == true ? Explain("GAME_RUNNING") : Json.Bool(config, "available") != true ? Explain(Json.Str(config, "reasonCode") ?? "CAPABILITY_UNAVAILABLE") : dirty ? "Unsaved changes. Save to apply at the next GTA V start." : "Configuration in the file: " + (savedMode ?? "unknown") + ". Applies at the next GTA V start.";
        }

        void Changed()
        {
            if (filling) return;
            if (!dirty) revision = Json.Str(config, "revision");
            dirty = true; UpdateState();
        }

        static string RatiosText(object value)
        {
            IList list = value as IList;
            return list == null ? "" : String.Join(", ", list.Cast<object>().Select(n => Convert.ToDouble(n, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture)));
        }

        void Fill()
        {
            if (config == null) { UpdateState(); return; }
            var options = Json.Obj(config, "options");
            filling = true;
            try
            {
                originalEnabled = Json.Bool(options, "enabled");
                IList list = Json.Get(options, "ratios") as IList;
                originalRatios = list == null ? null : list.Cast<object>().Select(n => Convert.ToDouble(n, CultureInfo.InvariantCulture)).ToArray();
                enabled.Checked = originalEnabled == true;
                ratios.Text = RatiosText(Json.Get(options, "ratios"));
                revision = Json.Str(config, "revision");
            }
            finally { filling = false; }
            UpdateState();
        }

        void FillDefaults()
        {
            var values = Json.Obj(config, "defaults");
            if (values == null) return;
            filling = true;
            try { enabled.Checked = Json.Bool(values, "enabled") == true; ratios.Text = RatiosText(Json.Get(values, "ratios")); }
            finally { filling = false; }
            Changed();
        }

        async Task Save()
        {
            if (saving || !save.Enabled) return;
            double[] parsed;
            try
            {
                string[] parts = ratios.Text.Split(',');
                if (parts.Length < 1 || parts.Length > 64) throw new FormatException();
                parsed = parts.Select(p => Double.Parse(p.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)).ToArray();
                if (parsed.Any(x => Double.IsNaN(x) || Double.IsInfinity(x) || x < 0.1 || x > 20 || Math.Abs(Math.Round(x, 3) - x) > 1e-9) || parsed.Skip(1).Where((x, i) => x <= parsed[i]).Any()) throw new FormatException();
            }
            catch (FormatException) { Fail("Enter 1–64 ascending ratios between 0.1 and 20, separated by commas, with up to three decimal places."); return; }
            catch (OverflowException) { Fail("A gear ratio is outside the allowed range."); return; }
            var changes = ChangedOptions(parsed);
            if (changes.Count == 0) { dirty = false; UpdateState(); Info("Nothing changed."); return; }
            BridgeClient c = Client();
            if (c == null) { Fail(Explain("BRIDGE_UNREACHABLE")); return; }
            saving = true; UpdateState();
            try
            {
                var result = await c.PostAsync("/api/v1/trainer-config", new Dictionary<string, object> { { "expectedRevision", revision }, { "options", changes } });
                if (result.Ok) { config = result.Body; dirty = false; Fill(); Done("Trainer configuration saved with a backup. Restart GTA V to apply it."); }
                else Fail(result.ErrorCode == "REVISION_CONFLICT" ? "The file changed. Discard to reload it before editing again." : Explain(result.ErrorCode));
            }
            catch (Exception error) { Fail(error.Message); }
            finally { saving = false; UpdateState(); }
        }

        Dictionary<string, object> ChangedOptions(double[] parsed)
        {
            var changes = new Dictionary<string, object>();
            // Preserve an existing Auto/Native token when only its ratios changed. The boolean writes
            // an explicit Manual/None mode, so send it only when the user changed the original value.
            if (!originalEnabled.HasValue || enabled.Checked != originalEnabled.Value) changes["enabled"] = enabled.Checked;
            if (originalRatios == null || !parsed.SequenceEqual(originalRatios)) changes["ratios"] = parsed;
            return changes;
        }

        async Task Shift(bool higher)
        {
            string reason = Block(higher);
            if (reason != null) { Fail(Explain(reason)); return; }
            await Run(higher ? "gears.shiftUp" : "gears.shiftDown", new Dictionary<string, object>(), null, "Changing gear…", "Game confirmed the gear change.");
        }
        protected override void OnBusyChanged() { UpdateState(); }
    }
}
