// Edits personal courses, preserving unexposed JSON fields, props and per-point options.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class RouteEditorDialog : Form
    {
        readonly RouteLibrary library;
        readonly RouteDocument source;
        readonly bool newCopy, creating;
        Dictionary<string, object> data;
        readonly TextBox routeName = new TextBox { Width = 290 }, author = new TextBox { Width = 290 }, categories = new TextBox { Width = 290 };
        readonly TextBox description = new TextBox { Width = 290, Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical };
        readonly TextBox notes = new TextBox { Width = 290, Multiline = true, Height = 55, ScrollBars = ScrollBars.Vertical };
        readonly CheckBox loop = Ui.Check("Allow loops / reverse"), bike = Ui.Check("Bike"), run = Ui.Check("Run"), waypointConfig = Ui.Check("Custom waypoint appearance");
        readonly TextBox color = new TextBox { Width = 120 };
        readonly NumericUpDown radius = Number(0, 40, 1), distance = Number(0, 100000, 3), climb = Number(0, 100000, 1), loss = Number(0, 100000, 1), highest = Number(-100000, 100000, 1);
        readonly NumericUpDown startX = Number(-100000, 100000, 3), startY = Number(-100000, 100000, 3), startZ = Number(-100000, 100000, 3);
        readonly DataGridView points = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false, BackgroundColor = Color.White, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        readonly RouteMapPanel preview = new RouteMapPanel();
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly Button drawPoints = Ui.Secondary("Add points"), undoPoint = Ui.Secondary("Undo last point"), save = Ui.Primary("Save changes");
        readonly Label help = new Label { Dock = DockStyle.Fill, ForeColor = Ui.Muted, Padding = new Padding(0, 5, 0, 0) };
        readonly TextBox raw = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsTab = true, AcceptsReturn = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9f), MaxLength = 8 * 1024 * 1024 };
        readonly Label status = new Label { Dock = DockStyle.Fill, ForeColor = Ui.Muted, Padding = new Padding(8, 6, 8, 0) };
        bool updating, dirty, rawDirty, drawing;
        int selectedPoint = -1;
        bool HasStart { get { return Json.Obj(data, "StartPoint") != null; } }
        internal RouteDocument Saved { get; private set; }

        internal RouteEditorDialog(RouteLibrary library, RouteDocument source, bool newCopy, bool creating = false)
        {
            this.library = library; this.source = source; this.newCopy = newCopy; this.creating = creating;
            data = RouteLibrary.Clone(source.Data);
            Text = creating ? "New route" : newCopy ? "Create an editable route copy" : "Edit personal route";
            Font = Ui.Body; BackColor = Ui.Surface;
            Width = 1380; Height = 860; MinimumSize = new Size(1020, 680);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            Controls.Add(root);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            toolbar.Controls.Add(new Label { Text = "Name", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
            routeName.Width = 320; routeName.Margin = new Padding(0, 4, 14, 0);
            drawPoints.Width = 170; undoPoint.Width = 170;
            drawPoints.Click += (s, e) => ToggleDrawing();
            undoPoint.Click += (s, e) => UndoPoint();
            toolbar.Controls.AddRange(new Control[] { routeName, drawPoints, undoPoint });
            root.Controls.Add(toolbar, 0, 0);
            root.Controls.Add(help, 0, 1);
            TabPage mapTab = new TabPage("Map"), detailsTab = new TabPage("Details"), pointsTab = new TabPage("Waypoints"), advancedTab = new TabPage("Advanced JSON");
            tabs.TabPages.AddRange(new[] { mapTab, detailsTab, pointsTab, advancedTab });
            tabs.SelectedIndex = 0;
            root.Controls.Add(tabs, 0, 2);
            mapTab.Controls.Add(preview);
            preview.Margin = Padding.Empty;
            preview.Map.EditPoints = true;
            preview.Map.EditPointRequested += PickPoint;
            preview.Map.RoutePointSelected += SelectPoint;
            preview.Map.RoutePointMoved += MoveMapPoint;
            preview.Map.RoutePointAdded += PlacePoint;
            preview.Map.Overlay = null;

            var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            detailsTab.AutoScroll = true;
            detailsTab.Controls.Add(fields);
            Field(fields, "Author", author); Field(fields, "Description", description); Field(fields, "Notes", notes); Field(fields, "Categories (comma)", categories);
            var sports = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            sports.Controls.AddRange(new Control[] { bike, run });
            Field(fields, "Activity types", sports); Field(fields, "Course options", loop);
            Field(fields, "Distance (km)", distance); Field(fields, "Climb (m)", climb); Field(fields, "Descent (m)", loss); Field(fields, "Highest point (m)", highest);
            var estimate = Ui.Secondary("Estimate from waypoints"); estimate.Width = 280;
            estimate.Click += (s, e) => Estimate();
            Field(fields, "Statistics", estimate);
            Field(fields, "Start X (m)", startX); Field(fields, "Start Y (m)", startY); Field(fields, "Start Z (m)", startZ);
            Field(fields, "Waypoints", waypointConfig); Field(fields, "Color (AARRGGBB)", color); Field(fields, "Radius (m, 0 hides)", radius);
            waypointConfig.CheckedChanged += (s, e) => { color.Enabled = radius.Enabled = waypointConfig.Checked; };

            points.Columns.Add(new DataGridViewTextBoxColumn { Name = "Index", HeaderText = "#", ReadOnly = true, FillWeight = 35 });
            foreach (string axis in new[] { "X", "Y", "Z" }) points.Columns.Add(new DataGridViewTextBoxColumn { Name = axis, HeaderText = axis, FillWeight = 90 });
            points.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Routed", HeaderText = "GPS", FillWeight = 45 });
            points.Columns.Add(new DataGridViewTextBoxColumn { Name = "Range", HeaderText = "Range (m)", FillWeight = 80 });
            points.DataError += (s, e) => { e.ThrowException = false; status.Text = "Enter valid coordinates and a range in metres."; };
            points.CellEndEdit += (s, e) => { dirty = true; UpdatePreview(); };
            points.CurrentCellDirtyStateChanged += (s, e) => { if (points.IsCurrentCellDirty && points.CurrentCell is DataGridViewCheckBoxCell) points.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            points.SelectionChanged += (s, e) => { if (!updating && points.CurrentRow != null) SelectPoint(points.CurrentRow.Index + 1); };
            var pointBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(5), WrapContents = true };
            Button add = Ui.Secondary("Add"), remove = Ui.Secondary("Remove"), up = Ui.Secondary("↑"), down = Ui.Secondary("↓");
            add.Width = 75; remove.Width = 90; up.Width = down.Width = 45;
            add.Click += (s, e) => { try { AddPoint(null, null); } catch (Exception error) { status.Text = error.Message; status.ForeColor = Ui.Bad; } };
            remove.Click += (s, e) => { if (points.CurrentRow == null) return; points.Rows.Remove(points.CurrentRow); Renumber(); dirty = true; selectedPoint = points.CurrentRow == null ? (HasStart ? 0 : -1) : points.CurrentRow.Index + 1; UpdatePreview(); };
            up.Click += (s, e) => MovePoint(-1); down.Click += (s, e) => MovePoint(1);
            pointBar.Controls.AddRange(new Control[] { add, remove, up, down });
            pointsTab.Controls.Add(points); pointsTab.Controls.Add(pointBar);
            var advancedBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(6) };
            var applyJson = Ui.Secondary("Apply JSON to preview"); applyJson.Width = 240;
            applyJson.Click += (s, e) => { try { ApplyRaw(); UpdatePreview(); } catch (Exception error) { status.Text = error.Message; status.ForeColor = Ui.Bad; } };
            advancedBar.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(450, 0), Text = "Full course definition, including props and extra per-point options. Apply it to update the form and map." });
            advancedBar.Controls.Add(applyJson);
            advancedTab.Controls.Add(raw); advancedTab.Controls.Add(advancedBar);
            tabs.Selecting += (s, e) =>
            {
                try
                {
                    if (tabs.SelectedTab == advancedTab && rawDirty) ApplyRaw();
                    if (e.TabPage == advancedTab) { Collect(); updating = true; raw.Text = Pretty(data); updating = false; rawDirty = false; }
                }
                catch (Exception error) { updating = false; e.Cancel = true; status.Text = error.Message; status.ForeColor = Ui.Bad; }
            };
            raw.TextChanged += (s, e) => { if (!updating) { dirty = rawDirty = true; } };

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
            bottom.Controls.Add(status, 0, 0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
            save.Text = creating ? "Save route" : newCopy ? "Save copy" : "Save changes";
            Button cancel = Ui.Secondary("Cancel");
            cancel.Click += (s, e) => Close();
            save.Click += (s, e) => Save();
            buttons.Controls.AddRange(new Control[] { save, cancel }); bottom.Controls.Add(buttons, 1, 0); root.Controls.Add(bottom, 0, 3);
            CancelButton = cancel;
            foreach (var text in new[] { routeName, author, categories, description, notes, color }) text.TextChanged += (s, e) => { if (!updating) dirty = true; };
            foreach (var number in new[] { startX, startY, startZ, distance, climb, loss, highest, radius }) number.ValueChanged += (s, e) => { if (!updating) { dirty = true; UpdatePreview(); } };
            foreach (var check in new[] { loop, bike, run, waypointConfig }) check.CheckedChanged += (s, e) => { if (!updating) { dirty = true; UpdatePreview(); } };

            updating = true;
            var info = source.Info;
            routeName.Text = (Json.Str(info, "Name") ?? Json.Str(data, "Name")) + (newCopy && !creating ? " (copy)" : "");
            author.Text = Json.Str(Json.Obj(info, "Author"), "Name") ?? "";
            description.Text = Json.Str(info, "Description") ?? ""; notes.Text = Json.Str(info, "Notes") ?? "";
            categories.Text = String.Join(", ", Strings(Json.Get(info, "Category")));
            var types = Strings(Json.Get(info, "Type")); bike.Checked = types.Contains("bike"); run.Checked = types.Contains("run");
            loop.Checked = Json.Bool(info, "Loop") == true;
            Set(distance, (Json.Num(info, "Distance") ?? 0)); Set(climb, Json.Num(info, "ElevationGain") ?? 0); Set(loss, Json.Num(info, "ElevationLoss") ?? 0); Set(highest, Json.Num(info, "MaxElevation") ?? 0);
            LoadDataFields();
            updating = false; dirty = false; drawing = creating;
            selectedPoint = HasStart ? 0 : -1;
            preview.Map.AddRoutePoints = drawing;
            status.Text = "Personal copies: " + library.CopiesPath;
            UpdatePreview();
            FormClosing += (s, e) =>
            {
                if (Saved == null && dirty && MessageBox.Show(this, "Discard the unsaved changes to this route?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) e.Cancel = true;
            };
        }

        static string[] Strings(object value)
        {
            var list = value as System.Collections.IList;
            return list == null ? new string[0] : list.Cast<object>().Select(Convert.ToString).ToArray();
        }

        static NumericUpDown Number(decimal min, decimal max, int decimals)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Width = 150, Increment = 1, ThousandsSeparator = false };
        }
        static void Set(NumericUpDown control, double value) { control.Value = Math.Max(control.Minimum, Math.Min(control.Maximum, (decimal)value)); }

        static void Field(TableLayoutPanel panel, string label, Control control)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = label, AutoSize = true, MaximumSize = new Size(145, 0), ForeColor = Ui.Muted, Margin = new Padding(0, 6, 4, 4) }, 0, row);
            control.Margin = new Padding(0, 3, 0, 6);
            panel.Controls.Add(control, 1, row);
        }

        void LoadDataFields()
        {
            var start = Json.Obj(data, "StartPoint");
            Set(startX, Json.Num(start, "X") ?? 0); Set(startY, Json.Num(start, "Y") ?? 0); Set(startZ, Json.Num(start, "Z") ?? 0);
            startX.Enabled = startY.Enabled = startZ.Enabled = HasStart;
            var config = Json.Obj(data, "WayPointConfig");
            waypointConfig.Checked = config != null; color.Text = Json.Str(config, "Color") ?? "FFADFF2F"; Set(radius, Json.Num(config, "Radius") ?? 20);
            color.Enabled = radius.Enabled = waypointConfig.Checked;
            points.Rows.Clear();
            foreach (var p in Json.List(data, "WayPointList")) AddRow(p);
            Renumber();
        }

        void AddRow(Dictionary<string, object> point)
        {
            bool routed;
            if (!Boolean.TryParse(Convert.ToString(Json.Get(point, "Routed"), CultureInfo.InvariantCulture), out routed)) routed = true;
            int row = points.Rows.Add(points.Rows.Count + 1, (Json.Num(point, "X") ?? 0).ToString("R", CultureInfo.InvariantCulture),
                (Json.Num(point, "Y") ?? 0).ToString("R", CultureInfo.InvariantCulture), (Json.Num(point, "Z") ?? 0).ToString("R", CultureInfo.InvariantCulture), routed, (Json.Num(point, "CloseByRange") ?? 20).ToString("R", CultureInfo.InvariantCulture));
            points.Rows[row].Tag = RouteLibrary.Clone(point);
        }

        void Renumber() { for (int i = 0; i < points.Rows.Count; i++) points.Rows[i].Cells[0].Value = i + 1; }

        void AddPoint(double? x, double? y)
        {
            if (!HasStart) throw new IOException("Click the map to choose the start first.");
            var selected = points.CurrentRow;
            var p = selected == null ? RouteLibrary.Clone(Json.Obj(data, "StartPoint")) : RouteLibrary.Clone((Dictionary<string, object>)selected.Tag);
            if (selected != null)
            {
                foreach (string axis in new[] { "X", "Y", "Z" }) p[axis] = CellNumber(selected, axis);
            }
            if (x.HasValue) p["X"] = x.Value; if (y.HasValue) p["Y"] = y.Value;
            AddRow(p); points.CurrentCell = points.Rows[points.Rows.Count - 1].Cells[1];
            selectedPoint = points.Rows.Count; dirty = true; UpdatePreview();
        }

        void MovePoint(int delta)
        {
            if (points.CurrentRow == null || !points.EndEdit()) return;
            int from = points.CurrentRow.Index, to = from + delta;
            if (to < 0 || to >= points.Rows.Count) return;
            var row = points.Rows[from]; points.Rows.RemoveAt(from); points.Rows.Insert(to, row); points.CurrentCell = row.Cells[1];
            selectedPoint = to + 1; Renumber(); dirty = true; UpdatePreview();
        }

        void PickPoint(double x, double y, bool append)
        {
            if (append) PlacePoint(x, y); else MoveMapPoint(selectedPoint, x, y);
        }

        void SelectPoint(int index)
        {
            if (index < 0 || index > points.Rows.Count || !HasStart) return;
            if (!points.EndEdit()) return;
            selectedPoint = index;
            bool wasUpdating = updating;
            updating = true;
            if (index == 0) { points.CurrentCell = null; points.ClearSelection(); }
            else points.CurrentCell = points.Rows[index - 1].Cells["X"];
            updating = wasUpdating;
            preview.Map.SelectedRoutePoint = index;
            preview.Map.Invalidate();
            UpdateInteraction();
        }

        void MoveMapPoint(int index, double x, double y)
        {
            try
            {
                if (!HasStart || index < 0 || index > points.Rows.Count) return;
                if (!points.EndEdit()) throw new IOException("Finish editing the coordinate first.");
                if (index == 0)
                {
                    updating = true; Set(startX, x); Set(startY, y); Set(startZ, 0); updating = false;
                }
                else
                {
                    var row = points.Rows[index - 1];
                    row.Cells["X"].Value = x.ToString("R", CultureInfo.InvariantCulture);
                    row.Cells["Y"].Value = y.ToString("R", CultureInfo.InvariantCulture);
                    row.Cells["Z"].Value = "0"; // Let GTBike V resolve terrain at the new location.
                }
                selectedPoint = index; dirty = true; UpdatePreview();
                status.ForeColor = Ui.Good; status.Text = "Position updated. Save when ready.";
            }
            catch (Exception error) { updating = false; status.Text = error.Message; status.ForeColor = Ui.Bad; }
        }

        void PlacePoint(double x, double y)
        {
            try
            {
                if (points.Rows.Count >= 10000) throw new IOException("A route can have at most 10000 waypoints.");
                bool first = !HasStart;
                var p = new Dictionary<string, object> { { "X", x }, { "Y", y }, { "Z", 0 }, { "Routed", true }, { "CloseByRange", 20 } };
                updating = true;
                if (first)
                {
                    data["StartPoint"] = p;
                    Set(startX, x); Set(startY, y); Set(startZ, 0);
                    startX.Enabled = startY.Enabled = startZ.Enabled = true;
                    selectedPoint = 0;
                }
                else
                {
                    AddRow(p);
                    points.CurrentCell = points.Rows[points.Rows.Count - 1].Cells["X"];
                    selectedPoint = points.Rows.Count;
                }
                updating = false; dirty = true; UpdatePreview();
                status.ForeColor = Ui.Muted;
                status.Text = first ? "Start placed. Click to add the next point." : points.Rows.Count + " waypoint(s). The last point will be the finish.";
            }
            catch (Exception error) { updating = false; status.Text = error.Message; status.ForeColor = Ui.Bad; }
        }

        void ToggleDrawing()
        {
            try
            {
                if (rawDirty) ApplyRaw();
                if (drawing && (!HasStart || points.Rows.Count < 1)) throw new IOException("Choose a start and a finish before finishing the route.");
                drawing = !drawing;
                preview.Map.AddRoutePoints = drawing;
                tabs.SelectedIndex = 0;
                if (!drawing && creating) Estimate();
                UpdatePreview();
                status.ForeColor = Ui.Muted;
                status.Text = drawing ? "Click the map to add points. Drag empty space to pan." : "Route finished. Drag a point to adjust it, then save.";
            }
            catch (Exception error) { status.Text = error.Message; status.ForeColor = Ui.Bad; }
        }

        void UndoPoint()
        {
            if (!drawing) return;
            updating = true;
            if (points.Rows.Count > 0)
            {
                points.Rows.RemoveAt(points.Rows.Count - 1);
                selectedPoint = points.Rows.Count;
            }
            else
            {
                data["StartPoint"] = null;
                startX.Enabled = startY.Enabled = startZ.Enabled = false;
                selectedPoint = -1;
            }
            updating = false; dirty = true; UpdatePreview();
            status.ForeColor = Ui.Muted; status.Text = "Last point undone.";
        }

        void UpdateInteraction()
        {
            drawPoints.Text = drawing ? "Finish route" : "Add points";
            if (!(drawPoints.Tag is bool) || (bool)drawPoints.Tag != drawing) Ui.SetKind(drawPoints, drawing);
            undoPoint.Visible = drawing;
            undoPoint.Enabled = HasStart || points.Rows.Count > 0;
            save.Enabled = !drawing && HasStart && points.Rows.Count >= 1;
            help.Text = drawing
                ? !HasStart ? "Click the map to place the start. Then click each waypoint up to the finish. Drag to pan; scroll to zoom."
                    : "Click to add the next waypoint. The last point is the finish. Press Finish route when done; Undo last point removes the last click."
                : (selectedPoint >= 0 ? (selectedPoint == 0 ? "Start" : "Waypoint " + selectedPoint) + " selected in blue. " : "")
                    + "Click a point or segment to select its nearest waypoint. Hold the left mouse button and drag; release to update its position.";
            preview.InteractionHint = drawing ? "Click to place points · drag to pan · scroll to zoom" : "Blue point: hold and drag · empty space: pan · scroll: zoom";
        }

        static double CellNumber(DataGridViewRow row, string column)
        {
            double number;
            if (!Double.TryParse(Convert.ToString(row.Cells[column].Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                || Double.IsNaN(number) || Double.IsInfinity(number) || Math.Abs(number) > 100000) throw new IOException("Waypoint " + (row.Index + 1) + ": enter a valid " + column + " value between -100000 and 100000 (use . for decimals).");
            return number;
        }

        void Collect()
        {
            if (!points.EndEdit()) throw new IOException("Finish editing the waypoint before saving.");
            data["Name"] = routeName.Text.Trim();
            if (HasStart)
            {
                var start = RouteLibrary.Clone(Json.Obj(data, "StartPoint"));
                start["X"] = (double)startX.Value; start["Y"] = (double)startY.Value; start["Z"] = (double)startZ.Value;
                data["StartPoint"] = start;
            }
            var list = new List<Dictionary<string, object>>();
            foreach (DataGridViewRow row in points.Rows)
            {
                var p = RouteLibrary.Clone((Dictionary<string, object>)row.Tag);
                foreach (string axis in new[] { "X", "Y", "Z" }) p[axis] = CellNumber(row, axis);
                p["Routed"] = Convert.ToBoolean(row.Cells["Routed"].Value, CultureInfo.InvariantCulture);
                p["CloseByRange"] = CellNumber(row, "Range"); list.Add(p);
            }
            data["WayPointList"] = list;
            if (waypointConfig.Checked)
            {
                var config = Json.Obj(data, "WayPointConfig") ?? new Dictionary<string, object>();
                config["Color"] = color.Text.Trim().ToUpperInvariant(); config["Radius"] = (double)radius.Value;
                data["WayPointConfig"] = config;
            }
            else data.Remove("WayPointConfig");
        }

        void ApplyRaw()
        {
            var parsed = Json.Parse(raw.Text);
            RouteLibrary.Validate(parsed);
            updating = true;
            data = parsed; routeName.Text = Json.Str(data, "Name");
            LoadDataFields();
            selectedPoint = 0;
            updating = false; rawDirty = false; dirty = true; UpdatePreview();
        }

        void UpdatePreview()
        {
            if (updating) return;
            try
            {
                Collect();
                preview.Map.SetRoute(RouteLibrary.Geometry(data, false, false), loop.Checked);
                preview.Map.SelectedRoutePoint = selectedPoint;
                if (!preview.Map.IsHandleCreated && !creating) preview.Map.FitRoute();
                preview.Map.Invalidate();
                UpdateInteraction();
                status.ForeColor = Ui.Muted;
            }
            catch (Exception error) { status.Text = error.Message; status.ForeColor = Ui.Bad; }
        }

        void Estimate()
        {
            try
            {
                Collect();
                var all = new List<Dictionary<string, object>> { Json.Obj(data, "StartPoint") };
                all.AddRange(Json.List(data, "WayPointList")); if (loop.Checked) all.Add(all[0]);
                double total = 0, gain = 0, descent = 0, max = -100000;
                for (int i = 0; i < all.Count; i++)
                {
                    double z = Json.Num(all[i], "Z") ?? 0; max = Math.Max(max, z);
                    if (i == 0) continue;
                    double dx = (Json.Num(all[i], "X") ?? 0) - (Json.Num(all[i - 1], "X") ?? 0), dy = (Json.Num(all[i], "Y") ?? 0) - (Json.Num(all[i - 1], "Y") ?? 0), dz = z - (Json.Num(all[i - 1], "Z") ?? 0);
                    total += Math.Sqrt(dx * dx + dy * dy + dz * dz); if (dz > 0) gain += dz; else descent -= dz;
                }
                updating = true; Set(distance, total / 1000); Set(climb, gain); Set(loss, descent); Set(highest, max); updating = false; dirty = true;
                status.ForeColor = Ui.Warn; status.Text = "Estimated from straight segments between waypoints. The actual road distance can be longer.";
            }
            catch (Exception error) { updating = false; status.Text = error.Message; status.ForeColor = Ui.Bad; }
        }

        void Save()
        {
            try
            {
                if (rawDirty) ApplyRaw();
                if (drawing) throw new IOException("Press Finish route before saving.");
                Collect(); RouteLibrary.Validate(data);
                if (!bike.Checked && !run.Checked) throw new IOException("Choose at least one activity type.");
                var info = RouteLibrary.Clone(source.Info);
                info["Name"] = routeName.Text.Trim(); info["Description"] = description.Text; info["Notes"] = notes.Text;
                var owner = Json.Obj(info, "Author") ?? new Dictionary<string, object>(); owner["Name"] = author.Text.Trim(); info["Author"] = owner;
                info["Category"] = categories.Text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToArray();
                var types = Strings(Json.Get(info, "Type")).Where(t => t != "bike" && t != "run").ToList();
                if (bike.Checked) types.Add("bike"); if (run.Checked) types.Add("run");
                info["Type"] = types; info["Loop"] = loop.Checked;
                info["Distance"] = (double)distance.Value; info["ElevationGain"] = (double)climb.Value; info["ElevationLoss"] = (double)loss.Value; info["MaxElevation"] = (double)highest.Value;
                Saved = library.Save(source, info, data, newCopy);
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception error) { status.ForeColor = Ui.Bad; status.Text = error.Message; }
        }

        internal static string Pretty(object value)
        {
            string json = RouteLibrary.Serialize(value);
            var result = new StringBuilder(); int depth = 0; bool quoted = false, escaped = false;
            foreach (char c in json)
            {
                if (quoted)
                {
                    result.Append(c);
                    if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') { quoted = true; result.Append(c); }
                else if (c == '{' || c == '[') { depth++; result.Append(c).AppendLine().Append(' ', depth * 2); }
                else if (c == '}' || c == ']') { depth--; result.AppendLine().Append(' ', depth * 2).Append(c); }
                else if (c == ',') result.Append(c).AppendLine().Append(' ', depth * 2);
                else if (c == ':') result.Append(": ");
                else if (!Char.IsWhiteSpace(c)) result.Append(c);
            }
            return result.ToString();
        }
    }
}
