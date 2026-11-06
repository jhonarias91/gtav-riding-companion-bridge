// Game options tab (P15): the gtbikev.com configuration options of GTBikeVConfig.ini that this app manages ([main]
// gameplay and two [network] switches), through GET/POST /api/v1/mod-config. Editable only with GTA V closed: GTBike V
// rewrites the file from memory while it runs. Changes apply the next time the game starts.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class GameOptionsPanel : BridgePanel
    {
        sealed class Binding
        {
            public Control Control;
            public Func<object> Get;
            public Action<object> Set;
        }

        static readonly string[] BotNames = { "Random", "Companion", "Climber", "Sprinter", "Time trialer" };
        readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>();
        readonly Label banner = new Label { AutoSize = true, MaximumSize = new Size(1100, 0), Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 0, 0, 10) };
        readonly Button save = Ui.Primary("Save changes"), discard = Ui.Secondary("Discard"), defaults = Ui.Secondary("Restore defaults"), folder = Ui.Secondary("Open config folder");
        readonly ComboBox[] bots = Enumerable.Range(0, 5).Select(i => Ui.Combo()).ToArray();
        readonly Panel colorSwatch = new Panel { Width = 60, Height = 26, BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand, Margin = new Padding(3, 3, 3, 3) };
        Dictionary<string, object> doc;
        bool loading, dirty;
        string waypointAlpha = "FF";

        public GameOptionsPanel(Func<BridgeClient> client) : base(client, 3000)
        {
            var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16, 12, 16, 12) };
            Controls.Add(page);
            page.BringToFront();
            page.Controls.Add(Title("Game options"));
            page.Controls.Add(Note("Settings from GTBike V's configuration file (see gtbikev.com → Configuration). GTBike V rewrites that file while GTA V runs, so they can be changed only with the game closed and apply the next time it starts. A backup is kept before every change. Weight, FTP, heart rate, trainer difficulty, Road Feel, bicycle and character change live in the Mod tab.", 1100));
            page.Controls.Add(banner);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 10) };
            save.Width = 150; discard.Width = 110; defaults.Width = 160; folder.Width = 170;
            save.Click += async (s, e) => await Save();
            discard.Click += (s, e) => { dirty = false; Fill(); };
            defaults.Click += (s, e) => RestoreDefaults();
            folder.Click += (s, e) => OpenFolder();
            buttons.Controls.AddRange(new Control[] { save, discard, defaults, folder });
            page.Controls.Add(buttons);

            var cards = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, MaximumSize = new Size(1300, 0), Margin = Padding.Empty };
            page.Controls.Add(cards);
            int w = 400;

            var traffic = Ui.Card("Traffic", w);
            Number(traffic, "trafficBikesPercent", "Cyclists instead of cars (%)", 0, 100, 0, 5);
            Choice(traffic, "trafficStyle", "Cyclists' riding style", new object[] { "NORMAL", "AGRESSIVE", "BREAKLAWS" }, new[] { "Normal", "Aggressive", "Breaks traffic laws" });
            Ui.Full(traffic, Hint("100% shows no cars; 0% leaves the traffic as GTA V makes it."));
            cards.Controls.Add(traffic);

            var crowd = Ui.Card("Cheering crowds", w);
            Check(crowd, "crowdsEnabled", "Fans cheer on climbs");
            Number(crowd, "crowdsSlopePercent", "On climbs steeper than (%)", 0, 20, 0, 1);
            Number(crowd, "crowdsAmount", "Maximum fans", 0, 100, 0, 5);
            cards.Controls.Add(crowd);

            var botCard = Ui.Card("Route bots", w);
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Items.Add("None");
                bots[i].Items.AddRange(BotNames);
                bots[i].SelectedIndexChanged += (s, e) => Changed();
                Ui.Row(botCard, "Bot " + (i + 1), bots[i]);
            }
            bindings["bots"] = new Binding
            {
                Control = botCard,
                Get = () => bots.Where(b => b.SelectedIndex > 0).Select(b => b.SelectedIndex - 1).ToArray(),
                Set = v =>
                {
                    var list = (v as System.Collections.IList ?? new object[0]).Cast<object>().Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToList();
                    for (int i = 0; i < bots.Length; i++) bots[i].SelectedIndex = i < list.Count && list[i] >= 0 && list[i] <= 4 ? list[i] + 1 : 0;
                },
            };
            Ui.Full(botCard, Hint("Riders that join you on a route. Companion: blue, Climber: orange, Sprinter: brown, Time trialer: purple. Up to 5; all None for no bots."));
            cards.Controls.Add(botCard);

            var riding = Ui.Card("Riding", w);
            Choice(riding, "defensiveLevel", "Self-defence", new object[] { 0, 1, 2 }, new[] { "Passive", "Defensive", "Aggressive" });
            Number(riding, "fineSteering", "Key steering sensitivity", 0.1m, 1m, 2, 0.05m);
            Ui.Full(riding, Hint("Defensive: anyone aggressive close by dies. Aggressive: anyone too close who is not on a bike dies or their vehicle explodes. Sensitivity applies to the A/D steering keys (1 = full steer)."));
            cards.Controls.Add(riding);

            var waypoints = Ui.Card("Route waypoints", w);
            colorSwatch.Click += (s, e) => PickColor();
            Ui.Row(waypoints, "Color", colorSwatch);
            bindings["waypointColor"] = new Binding
            {
                Control = colorSwatch,
                Get = () => waypointAlpha + (colorSwatch.BackColor.ToArgb() & 0xFFFFFF).ToString("X6"),
                Set = v => { string hex = v as string ?? "FFADFF2F"; waypointAlpha = hex.Substring(0, 2); colorSwatch.BackColor = Color.FromArgb(int.Parse("FF" + hex.Substring(2), NumberStyles.HexNumber)); },
            };
            Number(waypoints, "waypointRadius", "Size (0 hides them)", 0, 40, 0, 1);
            Ui.Full(waypoints, Hint("A route's own settings take precedence."));
            cards.Controls.Add(waypoints);

            var shots = Ui.Card("Screenshots", w);
            Number(shots, "screenshotIntervalKm", "Automatic screenshot every (km)", 1, 100, 0, 1);
            Number(shots, "screenshotXOffset", "Screen X offset (px)", -20000, 20000, 0, 10);
            Ui.Full(shots, Hint("Use the offset with several monitors when GTA V is not on the main one: the main screen's width, or negative if the game screen is to its left."));
            cards.Controls.Add(shots);

            var screen = Ui.Card("Menu and screen", w);
            Check(screen, "menuAutoOpen", "Open the mod menu when you stop");
            Check(screen, "showClock", "Show a clock when the menu is hidden");
            Check(screen, "debugWindow", "Show the debug panels");
            Choice(screen, "logLevel", "Log detail", new object[] { 0, 1, 2, 3, 4, 5 }, new[] { "0 (least)", "1", "2", "3 – Info (default)", "4", "5 – Verbose" });
            Ui.Full(screen, Hint("For crash reports set the log detail to 4 or 5 and send GTBike V's log files."));
            cards.Controls.Add(screen);

            var gps = Ui.Card("GPS origin for FIT files", w);
            Number(gps, "gpsOriginLat", "Latitude", -90, 90, 5, 0.01m);
            Number(gps, "gpsOriginLon", "Longitude", -180, 180, 5, 0.01m);
            Ui.Full(gps, Hint("Real-world point that matches the centre of the GTA map in your FIT files. The default is a small island in the Pacific."));
            cards.Controls.Add(gps);

            var network = Ui.Card("Network", w);
            Check(network, "telemetry", "Send anonymous usage data to GTBike V");
            Check(network, "multiplayer", "Connect to the GTBike V multiplayer server");
            Ui.Full(network, Hint("Turning multiplayer back on uses the official GTBike V server."));
            cards.Controls.Add(network);

            UpdateState();
        }

        static Label Hint(string text) { return new Label { Text = text, ForeColor = Ui.Muted, AutoSize = true, MaximumSize = new Size(368, 0), Margin = new Padding(0, 6, 0, 0) }; }

        void Changed() { if (!loading) { dirty = true; UpdateState(); } }

        void Number(TableLayoutPanel card, string option, string label, decimal min, decimal max, int decimals, decimal step)
        {
            var box = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step, Width = 110, TextAlign = HorizontalAlignment.Right, Margin = new Padding(3) };
            box.ValueChanged += (s, e) => Changed();
            Ui.Row(card, label, box);
            bindings[option] = new Binding
            {
                Control = box,
                Get = () => decimals == 0 ? (object)(int)box.Value : (double)box.Value,
                Set = v => box.Value = Math.Max(min, Math.Min(max, Convert.ToDecimal(v ?? min, CultureInfo.InvariantCulture))),
            };
        }

        void Check(TableLayoutPanel card, string option, string label)
        {
            var box = Ui.Check(label);
            box.CheckedChanged += (s, e) => Changed();
            Ui.Full(card, box);
            bindings[option] = new Binding { Control = box, Get = () => box.Checked, Set = v => box.Checked = v is bool && (bool)v };
        }

        void Choice(TableLayoutPanel card, string option, string label, object[] values, string[] labels)
        {
            var box = Ui.Combo();
            box.Items.AddRange(labels);
            box.SelectedIndexChanged += (s, e) => Changed();
            Ui.Row(card, label, box);
            bindings[option] = new Binding
            {
                Control = box,
                Get = () => box.SelectedIndex < 0 ? null : values[box.SelectedIndex],
                Set = v => box.SelectedIndex = Array.FindIndex(values, x => Convert.ToString(x, CultureInfo.InvariantCulture) == Convert.ToString(v, CultureInfo.InvariantCulture)),
            };
        }

        protected override async Task Refresh(BridgeClient c)
        {
            var result = await c.GetAsync("/api/v1/mod-config");
            if (!result.Ok) { doc = null; Fail(Reasons.Text(result.ErrorCode)); UpdateState(); return; }
            doc = result.Body;
            if (!dirty) Fill();
            UpdateState();
        }

        protected override void Offline() { doc = null; UpdateState(); base.Offline(); }

        /// <summary>Values from the file; a missing line shows the mod's default.</summary>
        void Fill()
        {
            if (doc == null) return;
            var options = Json.Obj(doc, "options") ?? new Dictionary<string, object>();
            var defaultsDoc = Json.Obj(doc, "defaults");
            loading = true;
            try { foreach (var pair in bindings) pair.Value.Set(Json.Get(options, pair.Key) ?? Json.Get(defaultsDoc, pair.Key)); }
            finally { loading = false; }
            UpdateState();
        }

        void RestoreDefaults()
        {
            var defaultsDoc = Json.Obj(doc, "defaults");
            if (defaultsDoc == null) return;
            loading = true;
            try { foreach (var pair in bindings) pair.Value.Set(Json.Get(defaultsDoc, pair.Key)); }
            finally { loading = false; }
            dirty = true;
            UpdateState();
            Info("Defaults filled in. Press Save changes to write them.");
        }

        void UpdateState()
        {
            bool available = doc != null && Json.Bool(doc, "available") == true;
            bool editable = available && Json.Bool(doc, "editable") == true && !Busy;
            foreach (var b in bindings.Values) b.Control.Enabled = editable;
            foreach (var b in bots) b.Enabled = editable;
            save.Enabled = editable && dirty;
            discard.Enabled = dirty && !Busy;
            defaults.Enabled = editable;
            folder.Enabled = File.Exists(ConfigPath());
            if (doc == null) { banner.BackColor = Color.FromArgb(241, 245, 249); banner.ForeColor = Ui.Muted; banner.Text = "Start the bridge (Phone tab) to load the game options."; }
            else if (!available) { banner.BackColor = Color.FromArgb(254, 243, 199); banner.ForeColor = Ui.Warn; banner.Text = "GTBikeVConfig.ini was not found. Start GTA V with GTBike V once so it creates the file."; }
            else if (Json.Bool(doc, "gameRunning") == true) { banner.BackColor = Color.FromArgb(254, 243, 199); banner.ForeColor = Ui.Warn; banner.Text = "GTA V is running. Close the game to change these options (GTBike V would overwrite them). They apply the next time it starts."; }
            else { banner.BackColor = Color.FromArgb(220, 252, 231); banner.ForeColor = Ui.Good; banner.Text = dirty ? "Unsaved changes. Press Save changes; they apply the next time you start GTA V." : "GTA V is closed: you can change these options. They apply the next time you start the game."; }
        }

        protected override void OnBusyChanged() { UpdateState(); }

        async Task Save()
        {
            if (doc == null) return;
            var current = Json.Obj(doc, "options") ?? new Dictionary<string, object>();
            var changes = new Dictionary<string, object>();
            foreach (var pair in bindings)
            {
                object value = pair.Value.Get();
                if (value == null) continue;
                if (!SameValue(value, Json.Get(current, pair.Key))) changes[pair.Key] = value;
            }
            if (changes.Count == 0) { dirty = false; UpdateState(); Info("Nothing changed."); return; }
            BridgeClient c = Client();
            if (c == null) return;
            var result = await c.PostAsync("/api/v1/mod-config", new Dictionary<string, object> { { "expectedRevision", Json.Str(doc, "revision") }, { "options", changes } });
            if (result.Ok)
            {
                doc = result.Body;
                dirty = false;
                Fill();
                Done(changes.Count + (changes.Count == 1 ? " option saved." : " options saved.") + " They apply the next time you start GTA V.");
            }
            else
            {
                string field = Json.Str(Json.Obj(Json.Obj(result.Body, "error"), "details"), "field");
                Fail(result.ErrorCode == "GAME_RUNNING" ? "GTA V is running: close it first, then save again."
                    : result.ErrorCode == "REVISION_CONFLICT" ? "The file changed since it was loaded. Press Discard to reload it, then make your changes again."
                    : Reasons.Text(result.ErrorCode) + (field != null ? " (" + field + ")" : ""));
            }
            UpdateState();
        }

        static bool SameValue(object a, object b)
        {
            if (b == null) return false;
            var la = a as System.Collections.IList;
            var lb = b as System.Collections.IList;
            if (la != null || lb != null)
                return la != null && lb != null && la.Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).SequenceEqual(lb.Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
            if ((a is double || a is int) && (b is decimal || b is int || b is double)) return Math.Abs(Convert.ToDouble(a) - Convert.ToDouble(b)) < 1e-9;
            return Convert.ToString(a, CultureInfo.InvariantCulture) == Convert.ToString(b, CultureInfo.InvariantCulture);
        }

        void PickColor()
        {
            if (!colorSwatch.Enabled) return;
            using (var dialog = new ColorDialog { Color = colorSwatch.BackColor, FullOpen = true })
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK) { colorSwatch.BackColor = dialog.Color; Changed(); }
        }

        static string ConfigPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rockstar Games", "GTA V", "ModSettings", "GTBikeVConfig.ini");
        }

        void OpenFolder()
        {
            try { Process.Start("explorer.exe", "/select,\"" + ConfigPath() + "\""); }
            catch (Exception error) { Fail(error.Message); }
        }

        internal static string ModConfigPath { get { return ConfigPath(); } }
    }
}
