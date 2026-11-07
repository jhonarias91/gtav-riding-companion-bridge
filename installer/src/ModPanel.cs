// "Mod" tab: the same GTBike V options the Android app offers (activate, rider profile, ride settings, bicycle and
// character), through the same bridge commands, plus a live map of where the rider is.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class ModPanel : UserControl
    {
        sealed class Choice
        {
            public object Id; public string Label;
            public override string ToString() { return Label; }
        }

        readonly Func<BridgeClient> client;
        readonly Func<bool> bridgeRunning;
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300 };
        readonly Label adapterValue = Ui.Value(), modValue = Ui.Value(), gameValue = Ui.Value(), routeValue = Ui.Value();
        readonly Button toggle = Ui.Primary("Activate GTBike V");
        readonly NumericUpDown weight = Ui.Number(), ftp = Ui.Number(), hrMax = Ui.Number();
        readonly Button saveProfile = Ui.Primary("Save"), discardProfile = Ui.Secondary("Discard");
        readonly CheckBox autopilot = Ui.Check("Autopilot"), obstacles = Ui.Check("Avoid obstacles"), roadFeel = Ui.Check("Road Feel");
        readonly ComboBox difficulty = Ui.Combo(), bike = Ui.Combo(), character = Ui.Combo();
        readonly Label message = new Label { ForeColor = Ui.Muted, Padding = new Padding(2, 6, 2, 0) };
        readonly MapView map;
        readonly MapCalibration calibration;
        readonly TableLayoutPanel downloadCard = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 61, 107), ColumnCount = 1, RowCount = 1 };
        readonly Button downloadButton = Ui.Primary("Download map (15 MB)");
        readonly ProgressBar downloadProgress = new ProgressBar { Width = 360, Height = 14, Visible = false, Margin = new Padding(0, 10, 0, 6) };
        readonly Label downloadStatus = new Label { AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Color.FromArgb(203, 213, 225) };
        readonly CheckBox follow = new CheckBox { Text = "Follow rider", AutoSize = true, Checked = true, Margin = new Padding(6, 8, 10, 0) };
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
        Dictionary<string, object> state, settings;
        bool polling, loading, profileDirty, commandRunning;
        int tick;
        string lastModLayer;

        public ModPanel(Func<BridgeClient> client, Func<bool> bridgeRunning)
        {
            this.client = client;
            this.bridgeRunning = bridgeRunning;
            Dock = DockStyle.Fill;
            BackColor = Ui.Surface;
            calibration = MapCalibration.Load(Paths.Resource("gta-map.calibration.json"));
            map = new MapView(calibration) { Dock = DockStyle.Fill };
            map.Overlay = "Start the bridge to see the game.";

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 8, 10) };
            layout.Controls.Add(side, 0, 0);
            int width = 356;

            var status = Ui.Card("Game", width);
            Ui.Row(status, "Game adapter", adapterValue);
            Ui.Row(status, "GTBike V", modValue);
            Ui.Row(status, "Game", gameValue);
            Ui.Row(status, "Route", routeValue);
            toggle.Width = width - 28; toggle.Height = 40; toggle.Margin = new Padding(0, 8, 0, 2);
            toggle.Click += async (s, e) => await ToggleMod();
            Ui.Full(status, toggle);
            side.Controls.Add(status);

            var profile = Ui.Card("Rider profile", width);
            Ui.Row(profile, "Weight (kg)", weight);
            Ui.Row(profile, "FTP (W)", ftp);
            Ui.Row(profile, "Max heart rate (bpm)", hrMax);
            foreach (var box in new[] { weight, ftp, hrMax }) box.ValueChanged += (s, e) => { if (!loading) { profileDirty = true; UpdateProfileButtons(); } };
            var profileButtons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 0) };
            saveProfile.Width = 100; discardProfile.Width = 100;
            saveProfile.Click += async (s, e) => await SaveProfile();
            discardProfile.Click += (s, e) => { profileDirty = false; ApplySettings(); };
            profileButtons.Controls.Add(saveProfile); profileButtons.Controls.Add(discardProfile);
            Ui.Full(profile, profileButtons);
            side.Controls.Add(profile);

            var ride = Ui.Card("Ride settings", width);
            Ui.Full(ride, autopilot); Ui.Full(ride, obstacles); Ui.Full(ride, roadFeel);
            Ui.Row(ride, "Trainer difficulty", difficulty);
            autopilot.CheckedChanged += async (s, e) => await RideChanged("autopilot", autopilot.Checked);
            obstacles.CheckedChanged += async (s, e) => await RideChanged("avoidObstacles", obstacles.Checked);
            roadFeel.CheckedChanged += async (s, e) => await RideChanged("roadFeel", roadFeel.Checked);
            difficulty.SelectionChangeCommitted += async (s, e) => { var c = difficulty.SelectedItem as Choice; if (c != null) await RideChanged("difficulty", c.Id); };
            side.Controls.Add(ride);

            var look = Ui.Card("Bicycle and character", width);
            Ui.Row(look, "Bicycle", bike);
            Ui.Row(look, "Character", character);
            bike.SelectionChangeCommitted += async (s, e) => await AppearanceChanged("bikeId", bike);
            character.SelectionChangeCommitted += async (s, e) => await AppearanceChanged("characterId", character);
            side.Controls.Add(look);

            var note = new Label { Text = "Values are read from GTBike V. Changes apply in the game and show here once the game confirms them.", ForeColor = Ui.Muted, Width = width, Height = 48, Margin = new Padding(0, 4, 0, 0) };
            side.Controls.Add(note);

            var mapArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 10, 12, 10) };
            mapArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            mapArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mapArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.Controls.Add(mapArea, 1, 0);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var title = new Label { Text = "Map", Font = Ui.Heading, AutoSize = true, Margin = new Padding(0, 6, 16, 0) };
            var zoomIn = Ui.Secondary("+"); zoomIn.Width = 36; zoomIn.Click += (s, e) => map.ZoomBy(1.5f);
            var zoomOut = Ui.Secondary("−"); zoomOut.Width = 36; zoomOut.Click += (s, e) => map.ZoomBy(1 / 1.5f);
            var whole = Ui.Secondary("Whole map"); whole.Width = 100; whole.Click += (s, e) => { map.SetFollow(false); map.FitAll(); };
            follow.CheckedChanged += (s, e) => map.SetFollow(follow.Checked);
            map.FollowChanged += () => { if (follow.Checked != map.Follow) follow.Checked = map.Follow; };
            map.TeleportRequested += async (x, y) => await Teleport(x, y);
            toolbar.Controls.AddRange(new Control[] { title, follow, zoomIn, zoomOut, whole });
            mapArea.Controls.Add(toolbar, 0, 0);
            var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Ui.Border };
            frame.Controls.Add(map);
            // The map image is not shipped in the installer: offer to download it once.
            var mapBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Anchor = AnchorStyles.None, BackColor = Ui.CardBack, Padding = new Padding(22, 18, 22, 18) };
            mapBox.Controls.Add(new Label { Text = "Download the map", Font = Ui.Heading, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
            mapBox.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Ui.Text, Margin = new Padding(0, 0, 0, 12),
                Text = "To see where you are, this app needs a GTA V satellite map (about 15 MB). It is not included in the installer: download it once and it stays on this PC.\n\nSource: " + calibration.ImageUrl });
            downloadButton.Width = 220;
            downloadButton.Click += async (s, e) => await DownloadMap();
            downloadStatus.ForeColor = Ui.Muted;
            mapBox.Controls.AddRange(new Control[] { downloadButton, downloadProgress, downloadStatus });
            downloadCard.Controls.Add(mapBox, 0, 0);
            frame.Controls.Add(downloadCard);
            downloadCard.BringToFront();
            downloadCard.Visible = !File.Exists(Paths.MapImage);
            mapArea.Controls.Add(frame, 0, 1);
            var hint = new Label { Dock = DockStyle.Fill, ForeColor = Ui.Muted, Padding = new Padding(0, 6, 0, 0),
                Text = "Scroll to zoom · drag to move · double-click to zoom in · right-click to teleport or copy coordinates" };
            mapArea.Controls.Add(hint, 0, 2);

            side.Controls.Add(message);
            message.Width = width; message.Height = 60; message.AutoEllipsis = false;

            timer.Tick += async (s, e) => await Poll();
            SetEnabled(false);
            UpdateProfileButtons();
        }

        /// <summary>Starts polling the bridge while the tab is visible.</summary>
        public bool Active
        {
            set
            {
                timer.Enabled = value;
                if (value && map.Tag == null && File.Exists(Paths.MapImage)) { map.Tag = "loading"; map.LoadImage(Paths.MapImage); }
                downloadCard.Visible = !File.Exists(Paths.MapImage);
                if (value) { tick = 0; var ignored = Poll(); }
            }
        }

        /// <summary>Downloads the map image once, checks it is the calibrated one, then shows it.</summary>
        async Task DownloadMap()
        {
            string temp = Paths.MapImage + ".download";
            downloadButton.Enabled = false;
            downloadProgress.Value = 0;
            downloadProgress.Visible = true;
            downloadStatus.ForeColor = Ui.Muted;
            downloadStatus.Text = "Downloading…";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Paths.MapImage));
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var web = new WebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent] = "GTAVRidingCompanion/" + BuildInfo.Version;
                    web.DownloadProgressChanged += (s, e) => { downloadProgress.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage)); downloadStatus.Text = "Downloading… " + (e.BytesReceived / 1048576) + " MB"; };
                    await web.DownloadFileTaskAsync(new Uri(calibration.ImageUrl), temp);
                }
                long size = new FileInfo(temp).Length;
                if (calibration.ImageBytes > 0 && size != calibration.ImageBytes) throw new Exception("The file has a different size than expected (" + size + " bytes), so it is not the calibrated map.");
                using (var image = Image.FromFile(temp))
                    if (image.Width != calibration.Width || image.Height != calibration.Height) throw new Exception("The image is " + image.Width + "×" + image.Height + "; the map calibration needs " + calibration.Width + "×" + calibration.Height + ".");
                if (File.Exists(Paths.MapImage)) File.Delete(Paths.MapImage);
                File.Move(temp, Paths.MapImage);
                downloadCard.Visible = false;
                map.Tag = "loading";
                map.LoadImage(Paths.MapImage);
            }
            catch (Exception error)
            {
                downloadStatus.ForeColor = Ui.Bad;
                downloadStatus.Text = "The map could not be downloaded: " + (error.InnerException ?? error).Message + " Check the internet connection and try again.";
                downloadProgress.Visible = false;
                downloadButton.Enabled = true;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }

        async Task Poll()
        {
            if (polling) return;
            polling = true;
            try
            {
                BridgeClient c = client();
                if (c == null)
                {
                    state = null; settings = null;
                    // A running bridge without a desktop token is an older install than this window.
                    map.Overlay = bridgeRunning() ? "The installed bridge is older than this window. Press Update in the Setup tab." : "Start the bridge (Phone tab) to see the game.";
                    map.SetRider(null, null, null);
                    ShowState();
                    return;
                }
                var world = await c.GetAsync("/api/v1/desktop/world");
                ApplyWorld(world);
                if (tick % 2 == 0)
                {
                    var s = await c.GetAsync("/api/v1/state");
                    state = s.Ok ? s.Body : null;
                    ShowState();
                }
                if (tick % 4 == 0 && !commandRunning)
                {
                    var s = await c.GetAsync("/api/v1/settings");
                    settings = s.Ok ? s.Body : null;
                    ApplySettings();
                }
                tick++;
            }
            catch (Exception error) { message.Text = error.Message; }
            finally { polling = false; }
        }

        void ApplyWorld(ApiResult world)
        {
            if (!world.Ok)
            {
                map.Overlay = Reasons.Text(world.ErrorCode);
                map.SetRider(null, null, null);
                map.TeleportBlock = "no game data";
                return;
            }
            var body = world.Body;
            // P16: the bridge says why "Teleport here" is unavailable; an older bridge sends no "teleport" at all.
            map.TeleportVisible = Json.Get(body, "teleport") != null;
            string teleportReason = Json.Str(Json.Obj(body, "teleport"), "reasonCode");
            map.TeleportBlock = commandRunning ? "wait for the current action" : teleportReason == null ? null : TeleportBlockText(teleportReason);
            string adapter = Json.Str(body, "adapter"), mod = Json.Str(body, "mod"), control = Json.Str(body, "gameControl");
            var position = Json.Obj(body, "position");
            map.SetRider(Json.Num(position, "x"), Json.Num(position, "y"), Json.Num(position, "heading"));
            var route = Json.Obj(body, "route");
            map.SetRoute(Json.List(route, "waypoints").Select(p => new KeyValuePair<double, double>(Json.Num(p, "x") ?? 0, Json.Num(p, "y") ?? 0)));
            if (adapter != "fresh") map.Overlay = adapter == "stale" ? "The game is not sending data (paused, minimized or loading)." : "Waiting for GTA V. Start the game in Story Mode with GTBike V.";
            else if (mod != "dataReady") map.Overlay = mod == "initializing" ? "GTBike V is starting…" : "Activate GTBike V to see where you are on the map.";
            else if (position == null) map.Overlay = control == "inControl" ? "Waiting for your position…" : "The game is paused or loading.";
            else map.Overlay = null;
            if (mod == "dataReady" && lastModLayer != "dataReady") { follow.Checked = true; map.SetFollow(true); }
            lastModLayer = mod;
        }

        void ShowState()
        {
            var layers = Json.Obj(state, "layers");
            string adapter = Json.Str(layers, "adapter"), mod = Json.Str(layers, "mod"), control = Json.Str(layers, "gameControl");
            Ui.Status(adapterValue, state == null ? (bridgeRunning() ? "No answer from the bridge" : "Bridge stopped") : adapter == "fresh" ? "Connected" : adapter == "stale" ? "Not responding" : "Waiting for GTA V",
                adapter == "fresh" ? Ui.Good : adapter == "stale" ? Ui.Warn : Ui.Muted);
            string modText = mod == "dataReady" ? "Active" : mod == "inactive" ? "Not active" : mod == "initializing" ? "Starting…" : mod == "stopping" ? "Stopping…" : mod == "error" ? "Error" : "—";
            Ui.Status(modValue, modText, mod == "dataReady" ? Ui.Good : mod == "error" ? Ui.Bad : mod == "inactive" ? Ui.Warn : Ui.Muted);
            string controlText = control == "inControl" ? "In control" : control == "paused" ? "Paused" : control == "loading" ? "Loading" : control == "notInControl" ? "Not in control" : "—";
            Ui.Status(gameValue, controlText, control == "inControl" ? Ui.Good : Ui.Muted);
            var route = Json.Obj(state, "activeRoute");
            Ui.Status(routeValue, adapter != "fresh" ? "—" : route == null ? "Free ride" : "Route " + Json.Str(route, "id") + (Json.Bool(route, "loop") == true ? " (loop)" : "") + (Json.Bool(route, "reverse") == true ? " (reverse)" : ""), Ui.Text);

            bool fresh = adapter == "fresh";
            toggle.Enabled = fresh && !commandRunning && (mod == "dataReady" || mod == "inactive") && control == "inControl";
            toggle.Text = mod == "dataReady" ? "Deactivate GTBike V" : mod == "initializing" ? "Starting GTBike V…" : mod == "stopping" ? "Stopping GTBike V…" : "Activate GTBike V";
            Ui.SetKind(toggle, mod != "dataReady");
            tips.SetToolTip(toggle, toggle.Enabled ? null : !fresh ? Reasons.Text(adapter == "stale" ? "ADAPTER_STALE" : "ADAPTER_ABSENT") : control != "inControl" ? Reasons.Text("GAME_NOT_IN_CONTROL") : Reasons.Text("MOD_TRANSITION_IN_PROGRESS"));
            if (!fresh) { settings = null; ApplySettings(); }
        }

        void ApplySettings()
        {
            loading = true;
            try
            {
                if (settings == null) { SetEnabled(false); return; }
                var profile = Json.Obj(settings, "profile");
                SetNumber(weight, Json.Obj(profile, "weightKg"));
                SetNumber(ftp, Json.Obj(profile, "ftpW"));
                SetNumber(hrMax, Json.Obj(profile, "hrMaxBpm"));
                var ride = Json.Obj(settings, "ride");
                SetBool(autopilot, Json.Obj(ride, "autopilot"));
                SetBool(obstacles, Json.Obj(ride, "avoidObstacles"));
                SetBool(roadFeel, Json.Obj(ride, "roadFeel"));
                var level = Json.Obj(ride, "difficulty");
                var allowed = (Json.Get(level, "allowed") as System.Collections.IList ?? new object[0]).Cast<object>().Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToList();
                SetChoices(difficulty, allowed.Select(v => new Choice { Id = v, Label = v == 255 ? "Disabled" : v + " %" }).ToList(), Json.Int(level, "value"), level);
                var appearance = Json.Obj(settings, "appearance");
                foreach (var pair in new[] { Tuple.Create(bike, Json.Obj(appearance, "bike")), Tuple.Create(character, Json.Obj(appearance, "character")) })
                    SetChoices(pair.Item1, Json.List(pair.Item2, "options").Select(o => new Choice { Id = Json.Str(o, "id"), Label = Json.Str(o, "label") }).ToList(), Json.Str(pair.Item2, "selectedId"), pair.Item2);
                UpdateProfileButtons();
            }
            finally { loading = false; }
        }

        void SetEnabled(bool enabled)
        {
            foreach (Control c in new Control[] { weight, ftp, hrMax, autopilot, obstacles, roadFeel, difficulty, bike, character }) c.Enabled = enabled;
            UpdateProfileButtons();
        }

        bool Editable(Dictionary<string, object> field, Control control)
        {
            bool editable = Json.Bool(field, "editable") == true && !commandRunning;
            tips.SetToolTip(control, Json.Bool(field, "editable") == false ? Reasons.Text(Json.Str(field, "reasonCode")) : null);
            return editable;
        }

        void SetNumber(NumericUpDown box, Dictionary<string, object> field)
        {
            box.Minimum = (decimal)(Json.Num(field, "min") ?? 0);
            box.Maximum = (decimal)(Json.Num(field, "max") ?? 1000);
            double? value = Json.Num(field, "value");
            if (!profileDirty && value.HasValue) box.Value = Math.Max(box.Minimum, Math.Min(box.Maximum, (decimal)Math.Round(value.Value)));
            box.Enabled = Editable(field, box) && value.HasValue;
        }

        void SetBool(CheckBox box, Dictionary<string, object> field)
        {
            bool? value = Json.Bool(field, "value");
            box.Checked = value == true;
            box.Enabled = Editable(field, box) && value.HasValue;
        }

        void SetChoices(ComboBox box, List<Choice> choices, object selected, Dictionary<string, object> field)
        {
            if (box.DroppedDown) return;
            bool same = box.Items.Count == choices.Count && choices.Select((c, i) => Equals(((Choice)box.Items[i]).Id, c.Id) && ((Choice)box.Items[i]).Label == c.Label).All(x => x);
            if (!same) { box.Items.Clear(); box.Items.AddRange(choices.Cast<object>().ToArray()); }
            box.SelectedItem = choices.Count == 0 ? null : box.Items.Cast<Choice>().FirstOrDefault(c => Equals(c.Id, selected));
            box.Enabled = Editable(field, box) && choices.Count > 0;
        }

        void UpdateProfileButtons()
        {
            bool any = weight.Enabled || ftp.Enabled || hrMax.Enabled;
            saveProfile.Enabled = profileDirty && any && !commandRunning;
            discardProfile.Enabled = profileDirty;
        }

        async Task ToggleMod()
        {
            bool enable = Json.Str(Json.Obj(state, "layers"), "mod") != "dataReady";
            var op = await Command("mod.setActive", new Dictionary<string, object> { { "enabled", enable } }, Json.Str(state, "stateRevision"),
                enable ? "Activating GTBike V… (the game connects to your sensors)" : "Deactivating GTBike V…");
            if (op != null) Done(enable ? "GTBike V is active." : "GTBike V is no longer active.");
        }

        async Task SaveProfile()
        {
            var profile = Json.Obj(settings, "profile");
            var args = new Dictionary<string, object>();
            foreach (var pair in new[] { Tuple.Create("weightKg", weight), Tuple.Create("ftpW", ftp), Tuple.Create("hrMaxBpm", hrMax) })
            {
                double? current = Json.Num(Json.Obj(profile, pair.Item1), "value");
                int wanted = (int)pair.Item2.Value;
                if (pair.Item2.Enabled && (current == null || (int)Math.Round(current.Value) != wanted)) args[pair.Item1] = wanted;
            }
            if (args.Count == 0) { profileDirty = false; UpdateProfileButtons(); return; }
            var op = await Command("profile.update", args, Json.Str(settings, "settingsRevision"), "Saving the rider profile…");
            if (op != null) { profileDirty = false; Done("Rider profile saved in GTBike V."); }
            await ReloadSettings();
        }

        async Task RideChanged(string key, object value)
        {
            if (loading) return;
            var op = await Command("rideSettings.update", new Dictionary<string, object> { { key, value } }, Json.Str(settings, "settingsRevision"), "Applying ride settings…");
            if (op != null) Done("Ride settings applied.");
            await ReloadSettings();
        }

        async Task AppearanceChanged(string key, ComboBox box)
        {
            var choice = box.SelectedItem as Choice;
            if (loading || choice == null) return;
            var op = await Command("appearance.select", new Dictionary<string, object> { { key, choice.Id } }, Json.Str(settings, "settingsRevision"),
                key == "bikeId" ? "Changing the bicycle…" : "Changing the character…");
            if (op != null) Done(key == "bikeId" ? "Bicycle changed." : "Character changed.");
            await ReloadSettings();
        }

        static string TeleportBlockText(string code)
        {
            switch (code)
            {
                case "VALUE_NOT_ALLOWED": return "not during a route: choose Free ride first";
                case "MOD_INACTIVE": return "activate GTBike V first";
                case "GAME_NOT_IN_CONTROL": return "the game is paused or loading";
                case "BRIDGE_BUSY": return "a teleport is in progress";
                case "ADAPTER_STALE": return "the game is not sending data";
                case "ADAPTER_ABSENT": return "GTA V is not running";
                default: return Reasons.Text(code).TrimEnd('.');
            }
        }

        /// <summary>P16: moves the rider (and the bike) to the clicked point; the game finds the ground and the nearest road.</summary>
        async Task Teleport(double x, double y)
        {
            map.SetTarget(x, y);
            try
            {
                var args = new Dictionary<string, object> { { "x", Math.Round(x, 1) }, { "y", Math.Round(y, 1) } };
                var op = await Command("player.teleport", args, null, "Teleporting… the game is loading that area.", code =>
                    code == "VALUE_NOT_ALLOWED" ? "You cannot teleport there (open water, or a route is loaded). You stay where you were."
                    : code == "INTERNAL" ? "The game found no ground there in time, so you were put back. Try a spot closer to a road."
                    : null);
                if (op == null) return;
                bool snapped = Json.Bool(Json.Obj(op, "observed"), "snappedToRoad") == true;
                Done(snapped ? "Teleported to the nearest road." : "Teleported.");
                follow.Checked = true;
                map.SetFollow(true);
            }
            finally { map.SetTarget(null, null); }
        }

        async Task ReloadSettings()
        {
            BridgeClient c = client();
            if (c == null) return;
            var s = await c.GetAsync("/api/v1/settings");
            settings = s.Ok ? s.Body : null;
            ApplySettings();
        }

        /// <summary>Sends one command and follows its operation until the game confirms it (or not).</summary>
        async Task<Dictionary<string, object>> Command(string type, Dictionary<string, object> args, string revision, string pendingText, Func<string, string> explain = null)
        {
            BridgeClient c = client();
            string instance = Json.Str(state, "adapterInstanceId");
            if (c == null || instance == null) { Fail(Reasons.Text(c == null ? "BRIDGE_UNREACHABLE" : "ADAPTER_ABSENT")); return null; }
            if (commandRunning) { Fail("Wait for the previous action to finish."); return null; }
            commandRunning = true;
            SetEnabled(false);
            toggle.Enabled = false;
            Busy(pendingText);
            try
            {
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
                if (result.Ok && status == "applied") return op;
                string code = !result.Ok ? result.ErrorCode : Json.Str(Json.Obj(op, "error"), "code") ?? (status == "unknown" ? "OPERATION_UNKNOWN" : "INTERNAL");
                Fail((explain == null ? null : explain(code)) ?? Reasons.Text(code));
                return null;
            }
            catch (Exception error) { Fail(error.Message); return null; }
            finally
            {
                commandRunning = false;
                ApplySettings();
                ShowState();
            }
        }

        void Busy(string text) { message.ForeColor = Ui.Muted; message.Text = text; }
        void Done(string text) { message.ForeColor = Ui.Good; message.Text = "✓ " + text; }
        void Fail(string text) { message.ForeColor = Ui.Bad; message.Text = text; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
