// Training tab: GTBike V workout plans and workouts with their power profile, the workout running in the game, and
// "Select and start" (workouts.select, P13). Pausing, skipping, ending and ERG stay in the game, as on the phone.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class TrainingPanel : BridgePanel
    {
        readonly ListView plans = List("Plan|220", "Workouts|80|r");
        readonly ListView workouts = List("Workout|240", "Length|80|r", "Segments|80|r");
        readonly Label progressTitle = new Label { Font = Ui.Heading, AutoSize = true };
        readonly Label progressText = new Label { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(900, 0) };
        readonly Label name = new Label { Font = Ui.Heading, AutoSize = true, MaximumSize = new Size(420, 0) };
        readonly Label facts = new Label { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(420, 0), Margin = new Padding(0, 4, 0, 6) };
        readonly Label description = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 8) };
        readonly ProfileChart chart = new ProfileChart { Width = 420, Height = 150, Margin = new Padding(0, 0, 0, 10) };
        readonly Button start = Ui.Primary("Select and start");
        string revision, loadedPlan, shownWorkout;
        int? ftp;
        bool running;
        string runningName;

        public TrainingPanel(Func<BridgeClient> client) : base(client, 1000)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(16, 12, 16, 0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 460));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.BringToFront();

            var banner = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.CardBack, Padding = new Padding(14, 8, 14, 6), Margin = new Padding(0, 0, 0, 10) };
            banner.Controls.Add(progressTitle);
            banner.Controls.Add(progressText);
            layout.Controls.Add(banner, 0, 0);
            layout.SetColumnSpan(banner, 3);

            plans.Margin = new Padding(0, 0, 10, 8);
            workouts.Margin = new Padding(0, 0, 12, 8);
            plans.SelectedIndexChanged += async (s, e) => await LoadWorkouts();
            workouts.SelectedIndexChanged += async (s, e) => await ShowWorkout();
            workouts.DoubleClick += async (s, e) => await Start();
            layout.Controls.Add(plans, 0, 1);
            layout.Controls.Add(workouts, 1, 1);

            var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Ui.CardBack, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 8) };
            start.Width = 200;
            start.Click += async (s, e) => await Start();
            side.Controls.AddRange(new Control[] { name, facts, chart, description, start,
                Note("Starting works like the Select button in GTBike V: the trainer goes into ERG and follows the workout's target power, calculated with your FTP. Pause, skip or end it in the game.", 400) });
            layout.Controls.Add(side, 2, 1);
            ShowNothing();
        }

        protected override async Task Refresh(BridgeClient c)
        {
            var progress = await c.GetAsync("/api/v1/workout-progress");
            ShowProgress(progress.Ok ? progress.Body : null);
            if (Ticks % 5 != 0 && revision != null) return;
            var result = await c.GetAsync("/api/v1/workout-plans");
            if (!result.Ok) { if (revision == null) Info(Reasons.Text(result.ErrorCode)); return; }
            if (Json.Bool(result.Body, "ready") == false) { Info("GTBike V is still loading its workouts. Activate the mod in the Mod tab."); }
            ftp = Json.Int(result.Body, "ftpReferenceW");
            string next = Json.Str(result.Body, "catalogRevision");
            if (next == revision) return;
            revision = next;
            loadedPlan = null;
            string selected = plans.SelectedItems.Count > 0 ? (string)plans.SelectedItems[0].Tag : null;
            plans.BeginUpdate();
            plans.Items.Clear();
            foreach (var p in Json.List(result.Body, "plans"))
            {
                var item = new ListViewItem(new[] { Json.Str(p, "name"), Convert.ToString(Json.Int(p, "workoutCount") ?? 0) }) { Tag = Json.Str(p, "id"), ToolTipText = Json.Str(p, "description") };
                plans.Items.Add(item);
                if ((string)item.Tag == selected) item.Selected = true;
            }
            plans.ShowItemToolTips = true;
            plans.EndUpdate();
            if (plans.SelectedItems.Count == 0 && plans.Items.Count > 0) plans.Items[0].Selected = true;
            if (plans.Items.Count == 0) Info("No workouts to show.");
        }

        void ShowProgress(Dictionary<string, object> p)
        {
            running = p != null && Json.Bool(p, "present") == true && Json.Bool(p, "finished") != true;
            runningName = p == null ? null : Json.Str(p, "name");
            if (p == null || Json.Bool(p, "present") != true)
            {
                progressTitle.Text = "No workout in the game";
                progressText.Text = "Pick a plan and a workout, then press Select and start. You can also ride without one.";
            }
            else
            {
                string status = Json.Bool(p, "finished") == true ? "Finished" : Json.Bool(p, "inCountdown") == true ? "Countdown" : Json.Bool(p, "started") == true ? "In progress" : "Not started";
                double? elapsed = Json.Num(p, "elapsed"), total = Json.Num(p, "total");
                bool distance = Json.Str(p, "durationType") == "distance";
                string remaining = elapsed == null || total == null ? "—" : distance ? Fmt.Km(total - elapsed) : Fmt.Duration(total - elapsed);
                var segment = Json.Obj(p, "segment");
                string target = "";
                if (segment != null)
                {
                    int? a = Json.Int(segment, "startPowerW"), b = Json.Int(segment, "endPowerW");
                    target = " · segment " + ((Json.Int(segment, "index") ?? 0) + 1) + " of " + (Json.Int(p, "segmentCount") ?? 0)
                        + (a == null ? "" : " · " + (a == b || b == null ? a + " W" : a + "–" + b + " W") + " reference");
                }
                progressTitle.Text = "Workout in the game: " + (Json.Str(p, "name") ?? "—");
                progressText.Text = status + " · remaining " + remaining + target + ". Pause, skip or end this workout in the game.";
            }
            UpdateButtons();
        }

        async Task LoadWorkouts()
        {
            if (plans.SelectedItems.Count == 0) return;
            string planId = (string)plans.SelectedItems[0].Tag;
            if (planId == loadedPlan) return;
            loadedPlan = planId;
            BridgeClient c = Client();
            if (c == null) return;
            var result = await c.GetAsync("/api/v1/workouts?planId=" + Uri.EscapeDataString(planId));
            if (!result.Ok || planId != loadedPlan) return;
            workouts.BeginUpdate();
            workouts.Items.Clear();
            foreach (var w in Json.List(result.Body, "workouts"))
            {
                bool distance = Json.Str(w, "durationType") == "distance";
                double? total = Json.Num(w, "total");
                workouts.Items.Add(new ListViewItem(new[] { Json.Str(w, "name"), distance ? Fmt.Km(total) : Fmt.Duration(total), Convert.ToString(Json.Int(w, "segmentCount") ?? 0) }) { Tag = Json.Str(w, "id") });
            }
            workouts.EndUpdate();
            ShowNothing();
        }

        async Task ShowWorkout()
        {
            if (workouts.SelectedItems.Count == 0) { ShowNothing(); return; }
            string id = (string)workouts.SelectedItems[0].Tag;
            shownWorkout = id;
            BridgeClient c = Client();
            if (c == null) return;
            var result = await c.GetAsync("/api/v1/workouts/" + id);
            if (!result.Ok || shownWorkout != id) return;
            var w = Json.Obj(result.Body, "workout");
            bool distance = Json.Str(w, "durationType") == "distance";
            name.Text = Json.Str(w, "name");
            facts.Text = (distance ? Fmt.Km(Json.Num(w, "total")) : Fmt.Duration(Json.Num(w, "total"))) + " · " + (Json.Int(w, "segmentCount") ?? 0) + " segments"
                + (Json.Str(w, "author") != null ? " · by " + Json.Str(w, "author") : "")
                + (ftp != null ? "\nReference watts as the game computes them with FTP " + ftp + " W." : "");
            description.Text = Json.Str(w, "description") ?? "";
            chart.Segments = Json.List(w, "segments");
            UpdateButtons();
        }

        void ShowNothing()
        {
            shownWorkout = null;
            name.Text = "Choose a workout";
            facts.Text = "";
            description.Text = "";
            chart.Segments = null;
            UpdateButtons();
        }

        void UpdateButtons() { start.Enabled = shownWorkout != null && revision != null && !Busy; }

        protected override void OnBusyChanged() { UpdateButtons(); }

        async Task Start()
        {
            if (shownWorkout == null || revision == null) return;
            string chosen = name.Text;
            bool ok = running
                ? Confirm("End the workout in progress?", "“" + (runningName ?? "The workout") + "” is running and will end: it stops and is replaced by “" + chosen + "”, as if you chose it in the mod menu. It is not saved as finished; what you have done stays in the current activity. The trainer stays in ERG with the new workout's power, starting from its countdown.", "End and start")
                : Confirm("Start “" + chosen + "”?", "The game starts it as the Select button in GTBike V does and puts your trainer in ERG: the resistance will follow the workout's target power, calculated with your FTP. Pause, skip or end it in the game.", "Select and start");
            if (!ok) return;
            await Run("workouts.select", new Dictionary<string, object> { { "workoutId", shownWorkout } }, revision, "Starting " + chosen + "…", chosen + " started. Its countdown runs in the game.");
        }

        /// <summary>Step/ramp power profile of a workout, like the phone's chart.</summary>
        sealed class ProfileChart : Control
        {
            List<Dictionary<string, object>> segments;
            public ProfileChart() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Color.FromArgb(248, 250, 252); }
            public List<Dictionary<string, object>> Segments { set { segments = value; Visible = value != null && value.Count > 0; Invalidate(); } }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                if (segments == null || segments.Count == 0) return;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                double end = segments.Max(s => Json.Num(s, "end") ?? 0);
                double max = Math.Max(1, segments.Max(s => Math.Max(Json.Num(s, "startPowerW") ?? 0, Json.Num(s, "endPowerW") ?? 0)) * 1.1);
                if (end <= 0) return;
                using (var fill = new SolidBrush(Color.FromArgb(200, Ui.Accent)))
                    foreach (var s in segments)
                    {
                        float x0 = (float)((Json.Num(s, "start") ?? 0) / end * Width), x1 = (float)((Json.Num(s, "end") ?? 0) / end * Width);
                        float y0 = Height - (float)((Json.Num(s, "startPowerW") ?? 0) / max * (Height - 14)), y1 = Height - (float)((Json.Num(s, "endPowerW") ?? Json.Num(s, "startPowerW") ?? 0) / max * (Height - 14));
                        g.FillPolygon(fill, new[] { new PointF(x0, Height), new PointF(x0, y0), new PointF(x1, y1), new PointF(x1, Height) });
                    }
                using (var font = new Font("Segoe UI", 8f)) g.DrawString(Math.Round(max / 1.1) + " W", font, Brushes.DimGray, 2, 1);
            }
        }
    }
}
