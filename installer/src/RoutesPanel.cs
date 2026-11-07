// Routes tab: local/online catalog, an in-tab map preview, and editable personal copies in the game's Documents folder.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class RoutesPanel : BridgePanel
    {
        readonly TextBox search = new TextBox { Width = 230, Margin = new Padding(0, 4, 8, 0) };
        readonly Button newRoute = Ui.Primary("New");
        readonly ListView list = List("Route|225", "Distance|85|r", "Climb|75|r", "Category|130", "Loop|50", "Source|90", "Completed|120");
        // P18: routes completed in the game (GET /api/v1/progress): route id -> "✓ ×2 · 41:12".
        Dictionary<string, string> completed = new Dictionary<string, string>();
        string completedStamp;
        readonly Label active = new Label { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        readonly Label name = new Label { Font = Ui.Heading, AutoSize = true, MaximumSize = new Size(580, 0) };
        readonly Label facts = new Label { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(580, 0), Margin = new Padding(0, 4, 0, 8) };
        readonly Label description = new Label { AutoSize = true, MaximumSize = new Size(580, 0), Margin = new Padding(0, 0, 0, 10) };
        readonly TextBox filePath = new TextBox { ReadOnly = true, Multiline = true, Width = 540, Height = 42, BorderStyle = BorderStyle.None, BackColor = Ui.CardBack, ForeColor = Ui.Muted };
        readonly CheckBox loop = new CheckBox { Text = "Loop route", AutoSize = true };
        readonly CheckBox reverse = new CheckBox { Text = "Reverse route", AutoSize = true };
        readonly Button apply = Ui.Primary("Apply route"), freeRide = Ui.Secondary("Free ride (no route)"), show = Ui.Secondary("Show in game");
        readonly Button copy = Ui.Secondary("Create editable copy"), edit = Ui.Secondary("Edit copy"), sync = Ui.Secondary("Add copies to game"), folder = Ui.Secondary("Open copies folder");
        readonly RouteMapPanel preview = new RouteMapPanel();
        readonly RouteLibrary library;
        List<Dictionary<string, object>> routes = new List<Dictionary<string, object>>();
        List<Dictionary<string, object>> localRoutes = new List<Dictionary<string, object>>(), gameRoutes = new List<Dictionary<string, object>>();
        HashSet<string> copyIds = new HashSet<string>();
        RouteDocument document;
        string catalogRevision, activeId, workoutName, localStamp, adapterInstance;
        bool workoutRunning, gameReady, filling, editing, pendingCopies;
        int selectionVersion;

        public RoutesPanel(Func<BridgeClient> client) : this(client, new RouteLibrary(Paths.CoursesDir, () => Installer.GameRunning)) { }

        internal RoutesPanel(Func<BridgeClient> client, RouteLibrary library) : base(client, 2000)
        {
            this.library = library;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(16, 12, 16, 0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.BringToFront();

            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            newRoute.Width = 85;
            newRoute.Click += (s, e) => EditDocument(RouteLibrary.NewRoute(), true, true);
            top.Controls.Add(newRoute);
            top.Controls.Add(new Label { Text = "Search", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
            top.Controls.Add(search);
            top.Controls.Add(active);
            top.SetFlowBreak(active, false);
            layout.Controls.Add(top, 0, 0);
            layout.SetColumnSpan(top, 2);
            search.TextChanged += (s, e) => Fill();

            list.Margin = new Padding(0, 0, 12, 8);
            list.SelectedIndexChanged += (s, e) => { if (!filling) ShowSelected(); };
            list.DoubleClick += async (s, e) => await Apply();
            layout.Controls.Add(list, 0, 1);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 8) };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            right.Controls.Add(preview, 0, 0);
            var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Ui.CardBack, Padding = new Padding(12, 10, 12, 12), Margin = Padding.Empty };
            side.Controls.AddRange(new Control[] { name, facts, description, filePath });
            var flags = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            flags.Controls.AddRange(new Control[] { loop, reverse }); side.Controls.Add(flags);
            reverse.Margin = new Padding(3, 3, 3, 12);
            loop.CheckedChanged += (s, e) => { if (!loop.Checked) reverse.Checked = false; UpdateButtons(); UpdatePreview(false); };
            reverse.CheckedChanged += (s, e) => UpdatePreview(false);
            apply.Click += async (s, e) => await Apply();
            freeRide.Click += async (s, e) => await FreeRide();
            show.Click += async (s, e) => await ShowInGame();
            copy.Click += (s, e) => EditCopy(true);
            edit.Click += (s, e) => EditCopy(false);
            sync.Click += (s, e) => PublishCopies();
            folder.Click += (s, e) => { try { Directory.CreateDirectory(library.CopiesPath); Process.Start("explorer.exe", "\"" + library.CopiesPath + "\""); } catch (Exception error) { Fail(error.Message); } };
            foreach (var rowButtons in new[] { new[] { apply, freeRide, show }, new[] { copy, edit }, new[] { sync, folder } })
            {
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 6) };
                foreach (var button in rowButtons) { button.Width = button == copy || button == sync || button == folder ? 185 : 165; row.Controls.Add(button); }
                side.Controls.Add(row);
            }
            side.Controls.Add(Note("The map connects course waypoints; the game calculates the road path. Copies can be edited offline. Close GTA V, then add the copies to the game before its next start.", 540));
            side.Controls.Add(Note("Applying a route may move you to the start, save the current activity, reset totals, add bots and turn on autopilot. Stop before applying.", 540));
            side.SizeChanged += (s, e) =>
            {
                int width = Math.Max(200, side.ClientSize.Width - 46);
                foreach (var label in new[] { name, facts, description }) label.MaximumSize = new Size(width, 0);
                filePath.Width = width;
                foreach (Control child in side.Controls) if (child is FlowLayoutPanel) child.MaximumSize = new Size(width, 0);
            };
            right.Controls.Add(side, 0, 1);
            layout.Controls.Add(right, 1, 1);
            VisibleChanged += (s, e) => { if (Visible) { RefreshLocal(false); preview.EnsureImage(); } };
            RefreshLocal(false);
            ShowSelected();
        }

        protected override async Task Refresh(BridgeClient c)
        {
            RefreshLocal(false);
            preview.EnsureImage();
            var state = await c.GetAsync("/api/v1/state");
            var progress = await c.GetAsync("/api/v1/workout-progress");
            var route = state.Ok ? Json.Obj(state.Body, "activeRoute") : null;
            var riderProgress = await c.GetAsync("/api/v1/progress");
            var done = new Dictionary<string, string>();
            foreach (var r in Json.List(Json.Obj(riderProgress.Ok ? riderProgress.Body : null, "progress"), "routes"))
            {
                int finishes = Json.Int(r, "completions") ?? 0, laps = Json.Int(r, "laps") ?? 0;
                string best = Json.Num(r, "bestTimeS") == null ? "" : " · " + Fmt.Duration(Json.Num(r, "bestTimeS"));
                done[Json.Str(r, "routeId")] = "✓ " + (finishes > 0 ? "×" + finishes : "") + (laps > 0 ? (finishes > 0 ? " + " : "") + laps + " laps" : "") + best;
            }
            string stamp = String.Join("|", done.Select(e => e.Key + "=" + e.Value).OrderBy(x => x).ToArray());
            if (stamp != completedStamp) { completedStamp = stamp; completed = done; Fill(); }
            activeId = route == null ? null : Json.Str(route, "id");
            workoutRunning = progress.Ok && Json.Bool(progress.Body, "present") == true && Json.Bool(progress.Body, "finished") != true;
            workoutName = progress.Ok ? Json.Str(progress.Body, "name") : null;
            bool fresh = state.Ok && Json.Str(Json.Obj(state.Body, "layers"), "adapter") == "fresh";
            gameReady = fresh;
            string instance = state.Ok ? Json.Str(state.Body, "adapterInstanceId") : null;
            // The catalog is reloaded only when its revision changes (it lists every course the mod knows).
            var first = await c.GetAsync("/api/v1/routes?offset=0&limit=1");
            string revision = first.Ok ? Json.Str(first.Body, "catalogRevision") : null;
            if (revision != catalogRevision || instance != adapterInstance)
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
                gameRoutes = all;
                catalogRevision = revision;
                adapterInstance = instance;
                MergeRoutes(null);
            }
            var current = routes.FirstOrDefault(r => Json.Str(r, "id") == activeId);
            active.Text = "Active: " + (!fresh ? "— (game not running)" : current != null ? Json.Str(current, "name") + Flags(route) : activeId != null ? "route " + activeId + Flags(route) : "free ride");
            if (!fresh && catalogRevision == null) Info("Local courses and copies can be previewed and edited offline. Start GTA V with GTBike V to apply a route.");
            else if (catalogRevision == null) Info("GTBike V has not listed its routes yet. Activate the mod in the Mod tab.");
            foreach (ListViewItem item in list.Items)
            {
                var style = (string)item.Tag == activeId ? FontStyle.Bold : FontStyle.Regular;
                if (item.Font.Style != style) { var old = item.Font; item.Font = new Font(list.Font, style); if (old != list.Font) old.Dispose(); }
            }
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

        void Fill() { Fill(null); }

        void Fill(string preferredId)
        {
            string selected = preferredId ?? (Selected == null ? null : Json.Str(Selected, "id"));
            string needle = search.Text.Trim().ToLowerInvariant();
            list.BeginUpdate();
            filling = true;
            list.Items.Clear();
            foreach (var r in routes)
            {
                var categories = (Json.Get(r, "categories") as System.Collections.IList ?? new object[0]).Cast<object>().Select(Convert.ToString).ToArray();
                string text = (Json.Str(r, "name") + " " + Json.Str(r, "author") + " " + String.Join(" ", categories)).ToLowerInvariant();
                if (needle.Length > 0 && !text.Contains(needle)) continue;
                var item = new ListViewItem(new[] { Json.Str(r, "name"), Fmt.Km(Json.Num(r, "distanceM")), Fmt.M(Json.Num(r, "elevationGainM")), String.Join(", ", categories), Json.Bool(r, "loopAllowed") == true ? "Yes" : "", copyIds.Contains(Json.Str(r, "id")) ? "My copy" : "Catalog",
                    completed.ContainsKey(Json.Str(r, "id") ?? "") ? completed[Json.Str(r, "id")] : "" });
                item.Tag = Json.Str(r, "id");
                list.Items.Add(item);
                if ((string)item.Tag == selected) item.Selected = true;
            }
            list.EndUpdate();
            filling = false;
            ShowSelected();
        }

        void MergeRoutes(string selected)
        {
            var merged = localRoutes.ToDictionary(r => Json.Str(r, "id"));
            foreach (var route in gameRoutes) if (!copyIds.Contains(Json.Str(route, "id"))) merged[Json.Str(route, "id")] = route;
            routes = merged.Values.OrderBy(r => Json.Str(r, "name"), StringComparer.CurrentCultureIgnoreCase).ToList();
            Fill(selected);
        }

        void RefreshLocal(bool force)
        {
            if (editing) return;
            try
            {
                // Also restores personal entries after a later remote library update.
                if (!Installer.GameRunning && library.NeedsSync()) library.SyncToGame();
                string stamp = library.Stamp;
                if (!force && stamp == localStamp) return;
                localRoutes = library.Catalog(); copyIds = library.CopyIds(); pendingCopies = library.NeedsSync();
                localStamp = stamp;
                MergeRoutes(null);
            }
            catch (Exception error) { Fail("Local courses: " + error.Message); }
        }

        protected override void Offline()
        {
            gameReady = false; activeId = null;
            RefreshLocal(false); preview.EnsureImage(); UpdateButtons();
            active.Text = "Active: — (bridge stopped)";
            if (String.IsNullOrEmpty(Message.Text)) Info("Local routes can be previewed and edited offline. Start the bridge to apply a route.");
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
            int version = ++selectionVersion;
            document = null; preview.Map.SetRoute(null); preview.Map.Overlay = "Choose a route to preview"; filePath.Text = "";
            var r = Selected;
            if (r == null)
            {
                name.Text = routes.Count == 0 ? "No routes yet" : "Choose a route";
                facts.Text = routes.Count == 0 ? "Local courses are read from " + library.DirectoryPath : routes.Count + " routes. Select one to preview its map.";
                description.Text = "";
            }
            else
            {
                name.Text = Json.Str(r, "name");
                facts.Text = Fmt.Km(Json.Num(r, "distanceM")) + " · climb " + Fmt.M(Json.Num(r, "elevationGainM")) + " · highest " + Fmt.M(Json.Num(r, "maxElevationM"))
                    + (Json.Str(r, "author") != null ? "\nBy " + Json.Str(r, "author") : "") + (Json.Str(r, "version") != null ? " · version " + Json.Str(r, "version") : "");
                description.Text = String.Join("\n\n", new[] { Json.Str(r, "description"), Json.Str(r, "notes") }.Where(t => !String.IsNullOrWhiteSpace(t)));
                loop.Checked = loop.Checked && Json.Bool(r, "loopAllowed") == true;
                preview.Map.Overlay = "Loading " + Json.Str(r, "name") + "…";
                var ignored = LoadSelected(r, version);
            }
            UpdateButtons();
        }

        async Task LoadSelected(Dictionary<string, object> selected, int version)
        {
            try
            {
                string id = Json.Str(selected, "id");
                var route = selected;
                BridgeClient c = Client();
                if (c != null && !copyIds.Contains(id))
                {
                    var detail = await c.GetAsync("/api/v1/routes/" + id);
                    if (IsDisposed || version != selectionVersion) return;
                    if (detail.Ok) route = Json.Obj(detail.Body, "route") ?? selected;
                }
                description.Text = String.Join("\n\n", new[] { Json.Str(route, "description"), Json.Str(route, "notes") }.Where(s => !String.IsNullOrWhiteSpace(s)));
                var loaded = await library.OpenAsync(route);
                if (IsDisposed || version != selectionVersion) return;
                document = loaded;
                filePath.Text = loaded.FilePath;
                Tips.SetToolTip(filePath, loaded.FilePath);
                preview.Map.Overlay = null; UpdatePreview(true); UpdateButtons();
            }
            catch (Exception error)
            {
                if (IsDisposed || version != selectionVersion) return;
                preview.Map.Overlay = "Course preview unavailable. Check the local file or internet connection.";
                preview.Map.Invalidate(); filePath.Text = error.Message; UpdateButtons();
            }
        }

        void UpdatePreview(bool fit)
        {
            if (document == null) return;
            preview.Map.SetRoute(RouteLibrary.Geometry(document.Data, loop.Checked, reverse.Checked && loop.Checked));
            if (fit) preview.Map.FitRoute();
        }

        bool CanUseInGame(Dictionary<string, object> route)
        {
            if (!gameReady || route == null || catalogRevision == null) return false;
            string id = Json.Str(route, "id");
            var inGame = gameRoutes.FirstOrDefault(r => Json.Str(r, "id") == id);
            return inGame != null && (!copyIds.Contains(id) || Json.Str(inGame, "version") == Json.Str(route, "version"));
        }

        void UpdateButtons()
        {
            var r = Selected;
            bool ready = catalogRevision != null && gameReady && !Busy && !editing;
            loop.Enabled = r != null && Json.Bool(r, "loopAllowed") == true && !Busy;
            reverse.Enabled = loop.Enabled && loop.Checked && Json.Bool(r, "reverseAllowed") == true;
            Tips.SetToolTip(loop, r != null && !loop.Enabled ? "This route does not support it." : null);
            apply.Enabled = ready && CanUseInGame(r);
            freeRide.Enabled = ready && activeId != null;
            show.Enabled = ready && CanUseInGame(r);
            copy.Enabled = document != null && !Busy && !editing;
            newRoute.Enabled = !Busy && !editing;
            edit.Enabled = copy.Enabled && document.IsCopy;
            sync.Enabled = copyIds.Count > 0 && !Busy && !editing;
            sync.Text = pendingCopies ? "Add copies to game *" : "Add copies to game";
            Tips.SetToolTip(sync, "Close GTA V before adding saved copies. Start the game again to load them.");
            Tips.SetToolTip(apply, r != null && copyIds.Contains(Json.Str(r, "id")) && !CanUseInGame(r) ? "Save the copy, close GTA V, add copies to the game, then start GTA V again." : null);
        }

        void EditCopy(bool newCopy)
        {
            if (document == null || (!newCopy && !document.IsCopy)) return;
            EditDocument(document, newCopy, false);
        }

        void EditDocument(RouteDocument source, bool newCopy, bool creating)
        {
            if (Busy || editing) return;
            editing = true; UpdateButtons();
            try
            {
                using (var editor = new RouteEditorDialog(library, source, newCopy, creating))
                {
                    if (editor.ShowDialog(FindForm()) != DialogResult.OK || editor.Saved == null) return;
                    var saved = editor.Saved;
                    editing = false;
                    bool published = library.SyncToGame();
                    RefreshLocal(true);
                    if (!String.IsNullOrEmpty(search.Text)) search.Text = "";
                    Fill(Convert.ToString(Json.Get(saved.Info, "Id"), System.Globalization.CultureInfo.InvariantCulture));
                    Done("Saved: " + saved.FilePath + (published ? ". Ready for the next GTA V start." : ". Close GTA V, then press Add copies to game before starting it again."));
                }
            }
            catch (Exception error) { Fail(error.Message); }
            finally { editing = false; UpdateButtons(); }
        }

        void PublishCopies()
        {
            try
            {
                if (!library.SyncToGame()) { Info("Your copies are saved. Close GTA V, then press Add copies to game before starting it again."); return; }
                RefreshLocal(true);
                Done("Personal copies added to the game's course catalog. Start GTA V to use them.");
            }
            catch (Exception error) { Fail("Could not add the copies to the game: " + error.Message); }
        }

        protected override void OnBusyChanged() { UpdateButtons(); }

        async Task Apply()
        {
            var r = Selected;
            if (!CanUseInGame(r)) return;
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
