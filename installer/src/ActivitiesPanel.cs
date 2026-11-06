// Activities tab: rides GTBike V saved as FIT files, their summary (read by the bridge from the file), opening them in
// Explorer, and "Save current activity" (activities.finish), like the Android app.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class ActivitiesPanel : BridgePanel
    {
        readonly ListView list = List("Date|150", "File|300", "Size|90|r");
        readonly Label name = new Label { Font = Ui.Heading, AutoSize = true, MaximumSize = new Size(420, 0) };
        readonly Label summary = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 6, 0, 8), Font = new Font("Segoe UI", 10f) };
        readonly PowerChart chart = new PowerChart { Width = 420, Height = 120, Margin = new Padding(0, 0, 0, 10) };
        readonly Button showFile = Ui.Secondary("Show file in folder"), openFolder = Ui.Secondary("Open Activities folder"), save = Ui.Primary("Save current activity"), reload = Ui.Secondary("Refresh list");
        List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
        string session, shown;
        bool canSave;
        string saveReason;

        public ActivitiesPanel(Func<BridgeClient> client) : base(client, 3000)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(16, 12, 16, 0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 460));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.BringToFront();

            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 8) };
            save.Width = 200; reload.Width = 130; openFolder.Width = 190;
            save.Click += async (s, e) => await Save();
            reload.Click += async (s, e) => { var c = Client(); if (c != null) await LoadList(c); };
            openFolder.Click += (s, e) => Open(ActivitiesFolder(), null);
            top.Controls.AddRange(new Control[] { save, reload, openFolder,
                Note("Stop first. GTBike V saves rides longer than 100 m and then starts a new activity; the game then shows its own summary (Discard there deletes the file).", 560) });
            layout.Controls.Add(top, 0, 0);
            layout.SetColumnSpan(top, 2);

            list.Margin = new Padding(0, 0, 12, 8);
            list.SelectedIndexChanged += async (s, e) => await ShowSelected();
            list.DoubleClick += (s, e) => ShowFile();
            layout.Controls.Add(list, 0, 1);

            var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Ui.CardBack, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 8) };
            showFile.Width = 200;
            showFile.Click += (s, e) => ShowFile();
            side.Controls.AddRange(new Control[] { name, summary, chart, showFile, Note("Values read from the FIT file. Missing values stay empty.", 400) });
            layout.Controls.Add(side, 1, 1);
            Clear();
        }

        protected override async Task Refresh(BridgeClient c)
        {
            var caps = await c.GetAsync("/api/v1/capabilities");
            var finish = Json.List(caps.Body, "capabilities").FirstOrDefault(x => Json.Str(x, "id") == "activities.finish");
            canSave = Json.Bool(finish, "available") == true;
            saveReason = Json.Str(finish, "reasonCode");
            var state = await c.GetAsync("/api/v1/state");
            session = state.Ok ? Json.Str(state.Body, "sessionGeneration") : null;
            if (Ticks % 10 == 0) await LoadList(c);
            UpdateButtons();
        }

        async Task LoadList(BridgeClient c)
        {
            var result = await c.GetAsync("/api/v1/activities?offset=0&limit=100");
            if (!result.Ok) { Info(result.ErrorCode == "CAPABILITY_UNAVAILABLE" ? "The game has not reported its Activities folder yet. Start GTA V with the adapter once." : Reasons.Text(result.ErrorCode)); return; }
            var next = Json.List(result.Body, "items");
            if (next.Select(i => Json.Str(i, "id")).SequenceEqual(items.Select(i => Json.Str(i, "id")))) return;
            items = next;
            string selected = list.SelectedItems.Count > 0 ? (string)list.SelectedItems[0].Tag : null;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var a in items)
            {
                var item = new ListViewItem(new[] { Fmt.LocalTime(Json.Str(a, "modifiedUtc")), Json.Str(a, "fileName"), Fmt.Bytes(Json.Num(a, "sizeBytes")) }) { Tag = Json.Str(a, "id") };
                list.Items.Add(item);
                if ((string)item.Tag == selected) item.Selected = true;
            }
            list.EndUpdate();
            if (items.Count == 0) Info("No saved activities on this PC yet.");
        }

        async Task ShowSelected()
        {
            if (list.SelectedItems.Count == 0) { Clear(); return; }
            string id = (string)list.SelectedItems[0].Tag;
            shown = id;
            BridgeClient c = Client();
            if (c == null) return;
            var result = await c.GetAsync("/api/v1/activities/" + id);
            if (shown != id) return;
            var entry = items.FirstOrDefault(i => Json.Str(i, "id") == id);
            name.Text = Json.Str(entry, "fileName");
            if (!result.Ok) { summary.Text = result.ErrorCode == "FIT_INVALID" ? "The FIT file is incomplete or damaged." : Reasons.Text(result.ErrorCode); chart.Values = null; UpdateButtons(); return; }
            var a = Json.Obj(result.Body, "activity");
            summary.Text = "Start            " + Fmt.LocalTime(Json.Str(a, "startUtc")) + "\n"
                + "Distance         " + Fmt.Km(Json.Num(a, "distanceM")) + "\n"
                + "Moving time      " + Fmt.Duration(Json.Num(a, "movingS")) + "  (total " + Fmt.Duration(Json.Num(a, "elapsedS")) + ")\n"
                + "Climb            " + Fmt.M(Json.Num(a, "ascentM")) + "\n"
                + "Average power    " + Fmt.Num(Json.Num(a, "avgPowerW"), "W") + "  (max " + Fmt.Num(Json.Num(a, "maxPowerW"), "W") + ")\n"
                + "Heart rate       " + Fmt.Num(Json.Num(a, "avgHeartRateBpm"), "bpm") + "  (max " + Fmt.Num(Json.Num(a, "maxHeartRateBpm"), "bpm") + ")\n"
                + "Cadence          " + Fmt.Num(Json.Num(a, "avgCadenceRpm"), "rpm") + "\n"
                + "Calories         " + Fmt.Num(Json.Num(a, "calories"), "kcal");
            summary.Font = new Font("Consolas", 9.5f);
            chart.Shown = true;
            var power = Json.Obj(a, "power");
            chart.Values = power == null ? null : (Json.Get(power, "values") as System.Collections.IList ?? new object[0]).Cast<object>()
                .Select(v => v == null ? (double?)null : Convert.ToDouble(v)).ToList();
            UpdateButtons();
        }

        void Clear()
        {
            shown = null;
            name.Text = "Choose an activity";
            summary.Text = "";
            chart.Values = null;
            chart.Shown = false;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            save.Enabled = canSave && session != null && !Busy;
            Tips.SetToolTip(save, canSave ? null : Reasons.Text(saveReason));
            showFile.Enabled = shown != null;
            openFolder.Enabled = ActivitiesFolder() != null;
        }

        protected override void OnBusyChanged() { UpdateButtons(); }

        async Task Save()
        {
            if (!Confirm("Save this activity?", "GTBike V writes the FIT file, resets totals and starts a new activity. The game then shows its own summary: choosing Discard there deletes the file.", "Save activity")) return;
            var result = await Run("activities.finish", new Dictionary<string, object>(), session, "Saving the activity…", "Activity saved.");
            if (result.Ok) { var c = Client(); if (c != null) { items.Clear(); await LoadList(c); } }
            else if (result.Code == "OPERATION_UNKNOWN") Fail("No confirmation. Check the Activities list before saving again.");
        }

        /// <summary>The folder the adapter reported (paths.json), or null before the game ran once with it.</summary>
        static string ActivitiesFolder()
        {
            try
            {
                string file = Path.Combine(Paths.AdapterDir, "paths.json");
                if (!File.Exists(file)) return null;
                string folder = Json.Str(Json.Parse(File.ReadAllText(file)), "activitiesPath");
                return folder != null && Directory.Exists(folder) ? folder : null;
            }
            catch (Exception) { return null; }
        }

        void ShowFile()
        {
            var entry = items.FirstOrDefault(i => Json.Str(i, "id") == shown);
            Open(ActivitiesFolder(), Json.Str(entry, "fileName"));
        }

        void Open(string folder, string fileName)
        {
            if (folder == null) { Fail("The game has not reported its Activities folder yet. Start GTA V with the adapter once."); return; }
            try
            {
                string file = fileName == null ? null : Path.Combine(folder, Path.GetFileName(fileName));
                if (file != null && File.Exists(file)) Process.Start("explorer.exe", "/select,\"" + file + "\"");
                else Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception error) { Fail(error.Message); }
        }

        sealed class PowerChart : Control
        {
            List<double?> values;
            public PowerChart() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Color.FromArgb(248, 250, 252); }
            public List<double?> Values { set { values = value; Invalidate(); } }
            public bool Shown { set { Visible = value; } }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                using (var font = new Font("Segoe UI", 8.5f))
                {
                    if (values == null || values.Count < 2 || !values.Any(v => v.HasValue)) { g.DrawString("This file has no power data.", font, Brushes.DimGray, 6, 6); return; }
                    double max = Math.Max(1, values.Where(v => v.HasValue).Max(v => v.Value));
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var pen = new Pen(Ui.Accent, 1.6f))
                    {
                        PointF? last = null;
                        for (int i = 0; i < values.Count; i++)
                        {
                            if (!values[i].HasValue) { last = null; continue; } // Breaks are gaps in the recording.
                            var p = new PointF(i * (Width - 1f) / (values.Count - 1), Height - 2 - (float)(values[i].Value / max * (Height - 18)));
                            if (last.HasValue) g.DrawLine(pen, last.Value, p);
                            last = p;
                        }
                    }
                    g.DrawString("Power, up to " + Math.Round(max) + " W", font, Brushes.DimGray, 4, 2);
                }
            }
        }
    }
}
