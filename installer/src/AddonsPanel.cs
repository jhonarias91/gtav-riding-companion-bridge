// Add-ons tab (P18): optional game modes the adapter runs next to GTBike V, such as the Alleycat courier mission.
// Everything here is drawn from GET /api/v1/addons: one control per parameter descriptor, so a new add-on needs no
// change in this window. Start/Stop send addons.setEnabled; Save sends addons.configure with the changed values only.
// The PWA and the Android app use the same contract. C# 5 only.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class AddonsPanel : BridgePanel
    {
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        readonly ListView list = List("Add-on|200", "State|110");
        readonly Label name = new Label { Font = Ui.Heading, AutoSize = true, MaximumSize = new Size(560, 0) };
        readonly Label summary = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 2, 0, 6) };
        readonly Label description = new Label { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 10) };
        readonly Label status = new Label { AutoSize = true, Font = Ui.Heading, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 4) };
        readonly Label progress = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 10) };
        readonly Button start = Ui.Primary("Start"), stop = Ui.Secondary("Stop"), save = Ui.Primary("Save settings"), revert = Ui.Secondary("Discard changes");
        readonly TableLayoutPanel settings = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 8) };
        readonly Dictionary<string, Control> editors = new Dictionary<string, Control>();
        // P18 ghost: the add-on's saved segments (created on the Mod tab map).
        readonly Label segmentsTitle = Title("Segments");
        readonly ListView segmentList = List("Segment|190", "Best|80|r", "Avg power|80|r", "Tries|50|r", "Last|80|r");
        readonly Button renameSegment = Ui.Secondary("Rename…"), removeSegment = Ui.Secondary("Remove…");
        readonly FlowLayoutPanel segmentButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 6) };
        // P18 discover: XP progress and saved places.
        readonly Label progressTitle = Title("Your progress");
        readonly Label progressInfo = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 4) };
        readonly ProgressBar levelBar = new ProgressBar { Width = 560, Height = 12, Maximum = 100, Margin = new Padding(0, 0, 0, 10) };
        readonly Label placesTitle = Title("My places");
        readonly ListView placeList = List("Place|240", "Found|60|r", "Best|80|r", "Last found|130");
        readonly Button renamePlace = Ui.Secondary("Rename…"), removePlace = Ui.Secondary("Remove…");
        readonly FlowLayoutPanel placeButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 6) };
        readonly Label placesNote = Note("Save places on the map in the Mod tab: right-click → Discover: save place here. Progress is kept on this PC in %LOCALAPPDATA%\\GTBikeVCompanion\\v1\\discover-progress.json.", 560);
        readonly Label segmentsNote = Note("Mark segments on the map in the Mod tab: right-click → Ghost segment: start here, then right-click → Ghost segment: finish here (the same point twice makes a loop).", 560);
        readonly HashSet<string> dirty = new HashSet<string>();
        Dictionary<string, object> doc;
        string builtFor; // add-on id whose editors are built
        bool filling, refreshing;

        public AddonsPanel(Func<BridgeClient> client) : base(client, 1000)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(16, 12, 16, 0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.BringToFront();
            var intro = Note("Optional game modes that run inside GTA V next to GTBike V. They use the game's own map markers, weather and wind; GTBike V turns the wind into trainer resistance and rain into lower grip. The phone and web apps can start them too.", 980);
            layout.Controls.Add(intro, 0, 0);
            layout.SetColumnSpan(intro, 2);
            list.Margin = new Padding(0, 0, 12, 8);
            list.SelectedIndexChanged += (s, e) => { if (!refreshing) ShowSelected(true); };
            layout.Controls.Add(list, 0, 1);

            var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Ui.CardBack, Padding = new Padding(18, 12, 18, 12), Margin = new Padding(0, 0, 0, 8) };
            var runButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 12) };
            start.Width = 160;
            stop.Width = 120;
            runButtons.Controls.AddRange(new Control[] { start, stop });
            var saveButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 8) };
            save.Width = 160;
            revert.Width = 160;
            saveButtons.Controls.AddRange(new Control[] { save, revert });
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            segmentList.Width = 560;
            segmentList.Height = 170;
            segmentList.Dock = DockStyle.None;
            segmentList.Margin = new Padding(0, 0, 0, 6);
            renameSegment.Width = removeSegment.Width = 120;
            segmentButtons.Controls.AddRange(new Control[] { renameSegment, removeSegment });
            segmentList.SelectedIndexChanged += (s, e) => UpdateButtons();
            renameSegment.Click += async (s, e) => await RenameSegment();
            removeSegment.Click += async (s, e) => await RemoveSegment();
            placeList.Width = 560;
            placeList.Height = 170;
            placeList.Dock = DockStyle.None;
            placeList.Margin = new Padding(0, 0, 0, 6);
            renamePlace.Width = removePlace.Width = 120;
            placeButtons.Controls.AddRange(new Control[] { renamePlace, removePlace });
            placeList.SelectedIndexChanged += (s, e) => UpdateButtons();
            renamePlace.Click += async (s, e) => await RenamePlace();
            removePlace.Click += async (s, e) => await RemovePlace();
            side.Controls.AddRange(new Control[] { name, summary, description, status, progress, runButtons, progressTitle, progressInfo, levelBar,
                placesTitle, placeList, placeButtons, placesNote, segmentsTitle, segmentList, segmentButtons, segmentsNote, Title("Settings"), settings, saveButtons,
                Note("Settings marked * can only change while the add-on is stopped. They are kept on this PC for the next time.", 560) });
            layout.Controls.Add(side, 1, 1);
            start.Click += async (s, e) => await SetEnabled(true);
            stop.Click += async (s, e) => await SetEnabled(false);
            save.Click += async (s, e) => await Save();
            revert.Click += (s, e) => { dirty.Clear(); FillEditors(); UpdateButtons(); };
            ShowSelected(false);
        }

        protected override async Task Refresh(BridgeClient c)
        {
            var result = await c.GetAsync("/api/v1/addons");
            if (!result.Ok) { Info(Reasons.Text(result.ErrorCode)); return; }
            doc = result.Body;
            string selected = Selected == null ? null : Json.Str(Selected, "id");
            refreshing = true; // Rebuilding the rows must not count as the user picking another add-on.
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var addon in Json.List(doc, "addons"))
            {
                var item = new ListViewItem(new[] { Json.Str(addon, "name"), StateText(Json.Str(addon, "state")) }) { Tag = Json.Str(addon, "id") };
                list.Items.Add(item);
                if ((string)item.Tag == selected || (selected == null && list.Items.Count == 1)) item.Selected = true;
            }
            list.EndUpdate();
            refreshing = false;
            string reason = Json.Str(doc, "reasonCode");
            if (reason != null) Info(Reasons.Text(reason));
            ShowSelected(false);
        }

        Dictionary<string, object> Selected
        {
            get
            {
                if (list.SelectedItems.Count == 0) return null;
                string id = (string)list.SelectedItems[0].Tag;
                return Json.List(doc, "addons").FirstOrDefault(a => Json.Str(a, "id") == id);
            }
        }

        static string StateText(string state)
        {
            switch (state)
            {
                case "preparing": return "Starting…";
                case "running": return "Running";
                case "finished": return "Finished";
                case "aborted": return "Stopped";
                case "failed": return "Could not start";
                default: return "Ready";
            }
        }

        void ShowSelected(bool userChanged)
        {
            var addon = Selected;
            if (addon == null)
            {
                name.Text = "Choose an add-on in the list.";
                summary.Text = description.Text = status.Text = progress.Text = "";
                ShowSegments(null);
                ShowDiscover(null);
                settings.Controls.Clear();
                editors.Clear();
                builtFor = null;
                UpdateButtons();
                return;
            }
            string id = Json.Str(addon, "id");
            if (userChanged || builtFor != id) { dirty.Clear(); BuildEditors(addon); }
            name.Text = Json.Str(addon, "name");
            summary.Text = Json.Str(addon, "summary");
            description.Text = Json.Str(addon, "description");
            string state = Json.Str(addon, "state");
            status.Text = StateText(state);
            status.ForeColor = state == "running" ? Ui.Good : state == "failed" ? Ui.Bad : Ui.Text;
            progress.Text = ProgressText(addon);
            ShowSegments(addon);
            ShowDiscover(addon);
            FillEditors();
            UpdateButtons();
        }

        static string ProgressText(Dictionary<string, object> addon)
        {
            var run = Json.Obj(addon, "run");
            var sb = new StringBuilder();
            if (run != null)
            {
                string phase = Json.Str(run, "phase");
                sb.AppendLine(phase == "toPickup" ? "Ride to the pickup (yellow marker on the game map)."
                    : phase == "toDropoff" ? "Deliver the package (green marker on the game map)."
                    : phase == "armed" ? "Waiting for you to reach the target speed."
                    : phase == "chased" ? "Chased! Hold the target speed and keep pedalling."
                    : phase == "waiting" ? "Ride through a segment start (blue circle in the game) to race your ghost."
                    : phase == "onSegment" ? "On a segment: race to the finish."
                    : phase == "searching" ? "Find the hidden spot: follow the hint, hot or cold."
                    : phase == "found" ? "Spot reached. Next one coming…" : "Finding the next point…");
                double? left = Json.Num(run, "timeLeftS");
                if (phase == "searching") sb.AppendLine("Time left for this spot: " + Fmt.Duration(left));
                else if (phase != "waiting" && phase != "onSegment" && phase != "found" && phase != "locating")
                    sb.AppendLine(left != null ? "Time left: " + Fmt.Duration(left) : phase == "armed" ? "The clock starts when the chaser appears." : "The clock starts at the first pickup.");
                else if (phase == "onSegment") sb.AppendLine("Time: " + Fmt.Duration(Json.Num(run, "elapsedS")));
                var point = Json.Obj(run, "nextPoint");
                if (point != null && Json.Num(point, "distanceM") != null) sb.AppendLine("Distance: " + Fmt.Km(Json.Num(point, "distanceM")) + " in a straight line");
                AppendStats(sb, Json.List(run, "stats"));
                return sb.ToString().TrimEnd();
            }
            var result = Json.Obj(addon, "result");
            if (result != null)
            {
                sb.AppendLine("Last run: " + Outcome(Json.Str(result, "outcome")) + (Json.Str(result, "finishedAtUtc") != null ? " (" + Fmt.LocalTime(Json.Str(result, "finishedAtUtc")) + ")" : ""));
                AppendStats(sb, Json.List(result, "stats"));
            }
            string startReason = Json.Str(addon, "startReasonCode");
            if (startReason != null) sb.AppendLine("To start: " + StartHint(startReason));
            return sb.ToString().TrimEnd();
        }

        static void AppendStats(StringBuilder sb, List<Dictionary<string, object>> stats)
        {
            var parts = new List<string>();
            foreach (var stat in stats)
            {
                object value = Json.Get(stat, "value");
                if (value == null || (value is bool && !(bool)value && Json.Str(stat, "id") == "heavy")) continue;
                string text = value is bool ? ((bool)value ? "yes" : "no")
                    : Json.Str(stat, "id") == "elapsedS" || Json.Str(stat, "id") == "survivedS" ? Fmt.Duration(Json.Num(stat, "value"))
                    : Convert.ToString(value, En) + (Json.Str(stat, "unit") != null ? " " + Json.Str(stat, "unit") : "");
                parts.Add(Json.Str(stat, "label") + ": " + text);
            }
            if (parts.Count > 0) sb.AppendLine(String.Join("   ·   ", parts.ToArray()));
        }

        static string Outcome(string outcome)
        {
            switch (outcome)
            {
                case "completed": return "completed";
                case "timeUp": return "time is up";
                case "caught": return "caught by the chaser";
                case "stopped": return "stopped";
                case "courseLoaded": return "ended because a course was loaded";
                case "modInactive": return "ended because GTBike V was turned off";
                case "riderLost": return "ended (rider lost)";
                case "noPoints": return "no Downtown street points found";
                default: return "ended by an error";
            }
        }

        static string StartHint(string code)
        {
            switch (code)
            {
                case "MOD_INACTIVE": return "activate GTBike V in the Mod tab.";
                case "ADDON_NO_POINTS": return "save some places on the map, or widen the distances.";
                case "VALUE_NOT_ALLOWED": return "leave the loaded course first (free ride).";
                case "ADDON_BUSY": return "stop the other add-on first.";
                case "RIDER_NOT_ON_BIKE": return "get on a bicycle in the game.";
                case "GAME_NOT_IN_CONTROL": return "unpause the game.";
                default: return Reasons.Text(code);
            }
        }

        void BuildEditors(Dictionary<string, object> addon)
        {
            settings.SuspendLayout();
            settings.Controls.Clear();
            settings.RowStyles.Clear();
            editors.Clear();
            builtFor = Json.Str(addon, "id");
            int row = 0;
            foreach (var p in Json.List(addon, "parameters"))
            {
                string id = Json.Str(p, "id");
                string type = Json.Str(p, "type");
                string unit = Json.Str(p, "unit");
                var label = new Label { Text = Json.Str(p, "label") + (unit != null && type != "boolean" ? " (" + unit + ")" : "") + (Json.Bool(p, "liveEdit") == true ? "" : " *"), AutoSize = true, Margin = new Padding(0, 7, 8, 3) };
                Control editor;
                if (type == "boolean")
                {
                    var check = Ui.Check("On");
                    check.CheckedChanged += (s, e) => Changed(id);
                    editor = check;
                }
                else if (type == "enum")
                {
                    var combo = Ui.Combo();
                    foreach (var option in Json.List(p, "options")) combo.Items.Add(new Choice(Json.Str(option, "id"), Json.Str(option, "label")));
                    combo.SelectedIndexChanged += (s, e) => Changed(id);
                    editor = combo;
                }
                else
                {
                    var number = Ui.Number();
                    double step = Json.Num(p, "step") ?? 1;
                    number.DecimalPlaces = step == Math.Floor(step) ? 0 : step * 10 == Math.Floor(step * 10) ? 1 : 2;
                    number.Minimum = (decimal)(Json.Num(p, "min") ?? 0);
                    number.Maximum = (decimal)(Json.Num(p, "max") ?? 100);
                    number.Increment = (decimal)step;
                    number.ValueChanged += (s, e) => Changed(id);
                    editor = number;
                }
                Tips.SetToolTip(label, Json.Str(p, "description"));
                Tips.SetToolTip(editor, Json.Str(p, "description"));
                editor.Tag = p;
                editors[id] = editor;
                settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                settings.Controls.Add(label, 0, row);
                settings.Controls.Add(editor, 1, row);
                row++;
            }
            settings.ResumeLayout();
        }

        sealed class Choice
        {
            internal readonly string Id, Label;
            internal Choice(string id, string label) { Id = id; Label = label; }
            public override string ToString() { return Label; }
        }

        void Changed(string id)
        {
            if (filling) return;
            dirty.Add(id);
            UpdateButtons();
        }

        /// <summary>Shows the values the game reports, except in controls the user is editing.</summary>
        void FillEditors()
        {
            var addon = Selected;
            if (addon == null) return;
            filling = true;
            try
            {
                foreach (var p in Json.List(addon, "parameters"))
                {
                    string id = Json.Str(p, "id");
                    Control editor;
                    if (!editors.TryGetValue(id, out editor)) continue;
                    editor.Enabled = !Busy && Json.Bool(p, "editable") == true;
                    if (dirty.Contains(id)) continue;
                    object value = Json.Get(p, "value");
                    if (value == null) continue;
                    var check = editor as CheckBox;
                    var combo = editor as ComboBox;
                    var number = editor as NumericUpDown;
                    if (check != null) check.Checked = value is bool && (bool)value;
                    else if (combo != null) combo.SelectedItem = combo.Items.Cast<Choice>().FirstOrDefault(c => c.Id == value as string);
                    else if (number != null) number.Value = Math.Max(number.Minimum, Math.Min(number.Maximum, Convert.ToDecimal(value, En)));
                }
            }
            finally { filling = false; }
        }

        object EditorValue(Control editor)
        {
            var p = (Dictionary<string, object>)editor.Tag;
            var check = editor as CheckBox;
            var combo = editor as ComboBox;
            var number = editor as NumericUpDown;
            if (check != null) return check.Checked;
            if (combo != null) return combo.SelectedItem == null ? null : ((Choice)combo.SelectedItem).Id;
            if (number == null) return null;
            if (Json.Str(p, "type") == "integer") return (int)number.Value;
            return (double)number.Value;
        }

        void UpdateButtons()
        {
            var addon = Selected;
            bool enabled = addon != null && Json.Bool(addon, "enabled") == true;
            bool fresh = doc != null && Json.Str(doc, "reasonCode") == null;
            start.Enabled = !Busy && addon != null && !enabled && Json.Str(addon, "startReasonCode") == null && dirty.Count == 0;
            stop.Enabled = !Busy && enabled && fresh;
            save.Enabled = !Busy && dirty.Count > 0 && addon != null && Json.Str(addon, "configureReasonCode") == null;
            revert.Enabled = !Busy && dirty.Count > 0;
            renameSegment.Enabled = removeSegment.Enabled = !Busy && SelectedSegment != null;
            renamePlace.Enabled = removePlace.Enabled = !Busy && SelectedPlace != null;
            Tips.SetToolTip(start, addon == null ? null : dirty.Count > 0 ? "Save or discard the changed settings first."
                : Json.Str(addon, "startReasonCode") != null ? StartHint(Json.Str(addon, "startReasonCode"))
                : "Starting cancels the current course or other add-on first.");
        }

        protected override void OnBusyChanged() { FillEditors(); UpdateButtons(); }

        async Task SetEnabled(bool enabled)
        {
            var addon = Selected;
            if (addon == null) return;
            string label = Json.Str(addon, "name");
            if (!enabled && !Confirm("Stop " + label + "?", "The run ends now and counts as stopped. Weather and wind go back to normal.", "Stop")) return;
            await Run("addons.setEnabled", new Dictionary<string, object> { { "addonId", Json.Str(addon, "id") }, { "enabled", enabled } },
                enabled ? Json.Str(doc, "addonsRevision") : null, enabled ? "Starting " + label + "…" : "Stopping " + label + "…",
                enabled ? label + " started. Follow the marker on the game map." : label + " stopped.");
        }

        void ShowSegments(Dictionary<string, object> addon)
        {
            bool has = addon != null && Json.Get(addon, "segments") is System.Collections.IEnumerable;
            foreach (Control c in new Control[] { segmentsTitle, segmentList, segmentButtons, segmentsNote }) c.Visible = has;
            if (!has) return;
            string selected = SelectedSegment == null ? null : Json.Str(SelectedSegment, "id");
            segmentList.BeginUpdate();
            segmentList.Items.Clear();
            foreach (var seg in Json.List(addon, "segments"))
            {
                var best = Json.Obj(seg, "best");
                var item = new ListViewItem(new[] {
                    (Json.Bool(seg, "loop") == true ? "⟳ " : "") + Json.Str(seg, "name"),
                    best == null ? "—" : Clock(Json.Num(best, "timeS")),
                    best == null || Json.Num(best, "avgPowerW") == null ? "—" : Fmt.Num(Json.Num(best, "avgPowerW"), "W"),
                    Convert.ToString(Json.Int(seg, "attempts") ?? 0, En),
                    Json.Num(seg, "lastTimeS") == null ? "—" : Clock(Json.Num(seg, "lastTimeS")) }) { Tag = seg };
                segmentList.Items.Add(item);
                if (Json.Str(seg, "id") == selected) item.Selected = true;
            }
            segmentList.EndUpdate();
        }

        void ShowDiscover(Dictionary<string, object> addon)
        {
            var prog = addon == null ? null : Json.Obj(addon, "progress");
            foreach (Control c in new Control[] { progressTitle, progressInfo, levelBar }) c.Visible = prog != null;
            if (prog != null)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Level " + (Json.Int(prog, "level") ?? 1) + " · " + Json.Str(prog, "levelName") + " · " + (Json.Int(prog, "xp") ?? 0).ToString("#,0", En) + " XP ("
                    + (Json.Int(prog, "xpToNextLevel") ?? 0).ToString("#,0", En) + " to the next level)");
                AppendStats(sb, Json.List(prog, "stats"));
                var last = Json.Obj(prog, "lastFind");
                if (last != null)
                {
                    sb.AppendLine("Last find: " + Json.Str(last, "label") + " (+" + (Json.Int(last, "xp") ?? 0) + " XP, " + Fmt.Duration(Json.Num(last, "timeS")) + ")");
                    var parts = Json.List(last, "stats").Where(st => Json.Num(st, "value") > 0).Select(st => Json.Str(st, "label") + " " + Convert.ToString(Json.Get(st, "value"), En) + " " + Json.Str(st, "unit"));
                    sb.AppendLine("   " + String.Join(" · ", parts.ToArray()));
                }
                var lastRoute = Json.Obj(prog, "lastRoute");
                if (lastRoute != null) sb.AppendLine("Last route: " + Json.Str(lastRoute, "label") + " (+" + (Json.Int(lastRoute, "xp") ?? 0) + " XP, " + Fmt.Duration(Json.Num(lastRoute, "timeS")) + ")");
                int routesDone = Json.List(prog, "routes").Count;
                if (routesDone > 0) sb.AppendLine(routesDone + (routesDone == 1 ? " route completed" : " routes completed") + " (marked in the Routes tab). Completing GTBike V routes also earns XP.");
                progressInfo.Text = sb.ToString().TrimEnd();
                levelBar.Value = Math.Max(0, Math.Min(100, Json.Int(prog, "levelProgressPct") ?? 0));
            }
            bool hasPlaces = addon != null && Json.Get(addon, "places") is System.Collections.IEnumerable;
            foreach (Control c in new Control[] { placesTitle, placeList, placeButtons, placesNote }) c.Visible = hasPlaces;
            if (!hasPlaces) return;
            string selected = SelectedPlace == null ? null : Json.Str(SelectedPlace, "id");
            placeList.BeginUpdate();
            placeList.Items.Clear();
            foreach (var place in Json.List(addon, "places"))
            {
                int found = Json.Int(place, "found") ?? 0;
                var item = new ListViewItem(new[] { (found > 0 ? "✓ " : "") + Json.Str(place, "name"), found.ToString(En),
                    Json.Num(place, "bestTimeS") == null ? "—" : Fmt.Duration(Json.Num(place, "bestTimeS")),
                    Json.Str(place, "lastFoundAtUtc") == null ? "never" : Fmt.LocalTime(Json.Str(place, "lastFoundAtUtc")) }) { Tag = place };
                placeList.Items.Add(item);
                if (Json.Str(place, "id") == selected) item.Selected = true;
            }
            placeList.EndUpdate();
        }

        Dictionary<string, object> SelectedPlace
        {
            get { return placeList.SelectedItems.Count == 0 ? null : placeList.SelectedItems[0].Tag as Dictionary<string, object>; }
        }

        async Task RenamePlace()
        {
            var place = SelectedPlace;
            var addon = Selected;
            if (place == null || addon == null) return;
            string newName = PromptDialog.Ask(FindForm(), "Rename place", "New name:", Json.Str(place, "name"), 40);
            if (newName == null) return;
            await Run("addons.savePlace", new Dictionary<string, object> { { "addonId", Json.Str(addon, "id") }, { "placeId", Json.Str(place, "id") }, { "name", newName }, { "point", Json.Obj(place, "point") } },
                null, "Renaming…", "Place renamed to “" + newName + "”.");
        }

        async Task RemovePlace()
        {
            var place = SelectedPlace;
            var addon = Selected;
            if (place == null || addon == null) return;
            if (!Confirm("Remove “" + Json.Str(place, "name") + "”?", "The place leaves the map and Discover. Your XP and past finds stay.", "Remove")) return;
            await Run("addons.removePlace", new Dictionary<string, object> { { "addonId", Json.Str(addon, "id") }, { "placeId", Json.Str(place, "id") } }, null, "Removing…", "Place removed.");
        }

        static string Clock(double? seconds)
        {
            if (seconds == null) return "—";
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds.Value));
            return ((int)t.TotalMinutes).ToString(En) + ":" + t.Seconds.ToString("00", En) + "." + (t.Milliseconds / 100).ToString(En);
        }

        Dictionary<string, object> SelectedSegment
        {
            get { return segmentList.SelectedItems.Count == 0 ? null : segmentList.SelectedItems[0].Tag as Dictionary<string, object>; }
        }

        async Task RenameSegment()
        {
            var seg = SelectedSegment;
            var addon = Selected;
            if (seg == null || addon == null) return;
            string name = PromptDialog.Ask(FindForm(), "Rename segment", "New name:", Json.Str(seg, "name"), 40);
            if (name == null) return;
            await Run("addons.saveSegment", new Dictionary<string, object> {
                    { "addonId", Json.Str(addon, "id") }, { "segmentId", Json.Str(seg, "id") }, { "name", name },
                    { "start", Json.Obj(seg, "start") }, { "finish", Json.Obj(seg, "finish") } },
                null, "Renaming…", "Segment renamed to “" + name + "”.");
        }

        async Task RemoveSegment()
        {
            var seg = SelectedSegment;
            var addon = Selected;
            if (seg == null || addon == null) return;
            if (!Confirm("Remove “" + Json.Str(seg, "name") + "”?", "Its record and its ghost are deleted too. This cannot be undone.", "Remove")) return;
            await Run("addons.removeSegment", new Dictionary<string, object> { { "addonId", Json.Str(addon, "id") }, { "segmentId", Json.Str(seg, "id") } },
                null, "Removing…", "Segment removed.");
        }

        async Task Save()
        {
            var addon = Selected;
            if (addon == null || dirty.Count == 0) return;
            var changes = new Dictionary<string, object>();
            foreach (string id in dirty)
            {
                Control editor;
                if (!editors.TryGetValue(id, out editor)) continue;
                object value = EditorValue(editor);
                if (value != null) changes[id] = value;
            }
            if (changes.Count == 0) { dirty.Clear(); UpdateButtons(); return; }
            var outcome = await Run("addons.configure", new Dictionary<string, object> { { "addonId", Json.Str(addon, "id") }, { "params", changes } },
                Json.Str(doc, "addonsRevision"), "Saving settings…", "Settings saved.");
            if (outcome.Ok) { dirty.Clear(); FillEditors(); UpdateButtons(); }
        }
    }
}
