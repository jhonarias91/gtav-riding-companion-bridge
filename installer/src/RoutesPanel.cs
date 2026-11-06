// Routes tab: the GTBike V course catalog with search, details, loop/reverse, Apply, Free ride and "Show in game",
// through the same bridge commands and warnings as the Android app (routes.apply, ui.openPanel + routes.preview).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class RoutesPanel : BridgePanel
    {
        readonly TextBox search = new TextBox { Width = 300, Margin = new Padding(0, 4, 8, 0) };
        readonly ListView list = List("Route|260", "Distance|90|r", "Climb|80|r", "Category|150", "Loop|50");
        readonly Label active = new Label { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        readonly Label name = new Label { Font = Ui.Heading, AutoSize = true, MaximumSize = new Size(420, 0) };
        readonly Label facts = new Label { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(420, 0), Margin = new Padding(0, 4, 0, 8) };
        readonly Label description = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 10) };
        readonly CheckBox loop = new CheckBox { Text = "Loop route", AutoSize = true };
        readonly CheckBox reverse = new CheckBox { Text = "Reverse route", AutoSize = true };
        readonly Button apply = Ui.Primary("Apply route"), freeRide = Ui.Secondary("Free ride (no route)"), show = Ui.Secondary("Show in game");
        List<Dictionary<string, object>> routes = new List<Dictionary<string, object>>();
        string catalogRevision, activeId, workoutName;
        bool workoutRunning;

        public RoutesPanel(Func<BridgeClient> client) : base(client, 2000)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(16, 12, 16, 0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 460));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.BringToFront();

            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            top.Controls.Add(new Label { Text = "Search", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
            top.Controls.Add(search);
            top.Controls.Add(active);
            top.SetFlowBreak(active, false);
            layout.Controls.Add(top, 0, 0);
            layout.SetColumnSpan(top, 2);
            search.TextChanged += (s, e) => Fill();

            list.Margin = new Padding(0, 0, 12, 8);
            list.SelectedIndexChanged += (s, e) => ShowSelected();
            list.DoubleClick += async (s, e) => await Apply();
            layout.Controls.Add(list, 0, 1);

            var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Ui.CardBack, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 8) };
            side.Controls.AddRange(new Control[] { name, facts, description, loop, reverse });
            reverse.Margin = new Padding(3, 3, 3, 12);
            loop.CheckedChanged += (s, e) => { if (!loop.Checked) reverse.Checked = false; UpdateButtons(); };
            apply.Width = 200; freeRide.Width = 200; show.Width = 200;
            apply.Margin = freeRide.Margin = show.Margin = new Padding(0, 0, 0, 8);
            apply.Click += async (s, e) => await Apply();
            freeRide.Click += async (s, e) => await FreeRide();
            show.Click += async (s, e) => await ShowInGame();
            side.Controls.AddRange(new Control[] { apply, freeRide, show,
                Note("Applying a route works like the mod menu: the game may move you to the start, save the current activity, reset totals, add route bots and turn on autopilot. Stop before applying.", 400) });
            layout.Controls.Add(side, 1, 1);
            ShowSelected();
        }

        protected override async Task Refresh(BridgeClient c)
        {
            var state = await c.GetAsync("/api/v1/state");
            var progress = await c.GetAsync("/api/v1/workout-progress");
            var route = state.Ok ? Json.Obj(state.Body, "activeRoute") : null;
            activeId = route == null ? null : Json.Str(route, "id");
            workoutRunning = progress.Ok && Json.Bool(progress.Body, "present") == true && Json.Bool(progress.Body, "finished") != true;
            workoutName = progress.Ok ? Json.Str(progress.Body, "name") : null;
            bool fresh = state.Ok && Json.Str(Json.Obj(state.Body, "layers"), "adapter") == "fresh";
            // The catalog is reloaded only when its revision changes (it lists every course the mod knows).
            var first = await c.GetAsync("/api/v1/routes?offset=0&limit=1");
            string revision = first.Ok ? Json.Str(first.Body, "catalogRevision") : null;
            if (revision != catalogRevision)
            {
                var all = new List<Dictionary<string, object>>();
                for (int offset = 0; revision != null && offset < 5000; offset += 100)
                {
                    var page = await c.GetAsync("/api/v1/routes?offset=" + offset + "&limit=100");
                    if (!page.Ok) break;
                    var items = Json.List(page.Body, "items");
                    all.AddRange(items);
                    if (items.Count < 100 || all.Count >= (Json.Int(page.Body, "total") ?? 0)) break;
                }
                routes = all;
                catalogRevision = revision;
                Fill();
            }
            var current = routes.FirstOrDefault(r => Json.Str(r, "id") == activeId);
            active.Text = "Active: " + (!fresh ? "— (game not running)" : current != null ? Json.Str(current, "name") + Flags(route) : activeId != null ? "route " + activeId + Flags(route) : "free ride");
            if (!fresh && catalogRevision == null) Info("Start GTA V with GTBike V: the route list comes from the game.");
            else if (catalogRevision == null) Info("GTBike V has not listed its routes yet. Activate the mod in the Mod tab.");
            foreach (ListViewItem item in list.Items) item.Font = new Font(list.Font, (string)item.Tag == activeId ? FontStyle.Bold : FontStyle.Regular);
            UpdateButtons();
        }

        static string Flags(Dictionary<string, object> route)
        {
            if (route == null) return "";
            var flags = new List<string>();
            if (Json.Bool(route, "loop") == true) flags.Add("loop");
            if (Json.Bool(route, "reverse") == true) flags.Add("reverse");
            return flags.Count == 0 ? "" : " (" + String.Join(", ", flags) + ")";
        }

        void Fill()
        {
            string selected = Selected == null ? null : Json.Str(Selected, "id");
            string needle = search.Text.Trim().ToLowerInvariant();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var r in routes)
            {
                var categories = (Json.Get(r, "categories") as System.Collections.IList ?? new object[0]).Cast<object>().Select(Convert.ToString).ToArray();
                string text = (Json.Str(r, "name") + " " + Json.Str(r, "author") + " " + String.Join(" ", categories)).ToLowerInvariant();
                if (needle.Length > 0 && !text.Contains(needle)) continue;
                var item = new ListViewItem(new[] { Json.Str(r, "name"), Fmt.Km(Json.Num(r, "distanceM")), Fmt.M(Json.Num(r, "elevationGainM")), String.Join(", ", categories), Json.Bool(r, "loopAllowed") == true ? "Yes" : "" });
                item.Tag = Json.Str(r, "id");
                list.Items.Add(item);
                if ((string)item.Tag == selected) item.Selected = true;
            }
            list.EndUpdate();
            if (list.SelectedItems.Count == 0) ShowSelected();
        }

        Dictionary<string, object> Selected
        {
            get
            {
                if (list.SelectedItems.Count == 0) return null;
                string id = (string)list.SelectedItems[0].Tag;
                return routes.FirstOrDefault(r => Json.Str(r, "id") == id);
            }
        }

        void ShowSelected()
        {
            var r = Selected;
            if (r == null)
            {
                name.Text = routes.Count == 0 ? "No routes yet" : "Choose a route";
                facts.Text = routes.Count == 0 ? "" : routes.Count + " routes. Double-click one to apply it.";
                description.Text = "";
            }
            else
            {
                name.Text = Json.Str(r, "name");
                facts.Text = Fmt.Km(Json.Num(r, "distanceM")) + " · climb " + Fmt.M(Json.Num(r, "elevationGainM")) + " · highest " + Fmt.M(Json.Num(r, "maxElevationM"))
                    + (Json.Str(r, "author") != null ? "\nBy " + Json.Str(r, "author") : "") + (Json.Str(r, "version") != null ? " · version " + Json.Str(r, "version") : "");
                description.Text = "";
                loop.Checked = loop.Checked && Json.Bool(r, "loopAllowed") == true;
                LoadDescription(Json.Str(r, "id"));
            }
            UpdateButtons();
        }

        async void LoadDescription(string id)
        {
            BridgeClient c = Client();
            if (c == null) return;
            var detail = await c.GetAsync("/api/v1/routes/" + id);
            if (!detail.Ok || Selected == null || Json.Str(Selected, "id") != id) return;
            var route = Json.Obj(detail.Body, "route");
            description.Text = String.Join("\n\n", new[] { Json.Str(route, "description"), Json.Str(route, "notes") }.Where(s => !String.IsNullOrWhiteSpace(s)).ToArray());
        }

        void UpdateButtons()
        {
            var r = Selected;
            bool ready = catalogRevision != null && !Busy;
            loop.Enabled = r != null && Json.Bool(r, "loopAllowed") == true && !Busy;
            reverse.Enabled = loop.Enabled && loop.Checked && Json.Bool(r, "reverseAllowed") == true;
            Tips.SetToolTip(loop, r != null && !loop.Enabled ? "This route does not support it." : null);
            apply.Enabled = ready && r != null;
            freeRide.Enabled = ready && activeId != null;
            show.Enabled = ready && r != null;
        }

        protected override void OnBusyChanged() { UpdateButtons(); }

        async Task Apply()
        {
            var r = Selected;
            if (r == null || catalogRevision == null) return;
            string routeName = Json.Str(r, "name");
            bool ok = workoutRunning
                ? Confirm("Restart the workout in progress?", "“" + (workoutName ?? "The workout in progress") + "” is running. Applying “" + routeName + "” works as in the mod menu: the current activity is saved (if over 100 m), the route loads and the workout starts again from its countdown, with the trainer in ERG. What you did of the workout is not marked as finished.", "Apply and restart")
                : Confirm("Apply " + routeName + "?", "The game may move your rider to the start, save the current activity, reset totals, add route bots and turn on autopilot. Stop before applying.", "Apply route");
            if (!ok) return;
            await Run("routes.apply", new Dictionary<string, object> { { "routeId", Json.Str(r, "id") }, { "loop", loop.Checked }, { "reverse", reverse.Checked && loop.Checked } },
                catalogRevision, "Loading " + routeName + "… (the game builds the course; this can take a while)", routeName + " is the active route.");
        }

        async Task FreeRide()
        {
            if (catalogRevision == null) return;
            string body = "Clears the current route. Your ride continues without following a course; nothing is saved or turned off.";
            if (workoutRunning) body += "\n\n“" + (workoutName ?? "The workout in progress") + "” keeps running without restarting, but the route riders go away, including the workout's reference rider.";
            if (!Confirm("Switch to free ride?", body, "Free ride")) return;
            await Run("routes.apply", new Dictionary<string, object> { { "routeId", null }, { "loop", false }, { "reverse", false } }, catalogRevision, "Clearing the route…", "Free ride: no route loaded.");
        }

        /// <summary>Opens the mod's route selector in the game and shows this route there (same as the phone's "Show on PC").</summary>
        async Task ShowInGame()
        {
            var r = Selected;
            if (r == null) return;
            var open = await Run("ui.openPanel", new Dictionary<string, object> { { "panel", "routes" } }, null, "Opening the route selector in the game…", "Route selector open in the game.");
            if (!open.Ok) return;
            await Run("routes.preview", new Dictionary<string, object> { { "routeId", Json.Str(r, "id") } }, catalogRevision, "Showing the route in the game…", "The game's route selector shows " + Json.Str(r, "name") + ".");
        }
    }
}
