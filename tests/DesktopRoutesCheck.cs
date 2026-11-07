// Persistence and editor integration checks. All writes stay in the temporary directory supplied by the test runner.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using GTAVRidingCompanion;

public static class DesktopRoutesCheck
{
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static void Reject(Action action, string message)
    {
        try { action(); } catch (IOException) { checks++; return; }
        throw new Exception(message);
    }
    static T Field<T>(object instance, string name) { return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
    static void Call(object instance, string name, params object[] args) { instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, args); }
    static Dictionary<string, object> Read(string path) { return Json.Parse(File.ReadAllText(path)); }
    static void Write(string path, object value) { File.WriteAllText(path, RouteLibrary.Serialize(value)); }
    static void SetField(object instance, string name, object value) { instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value); }

    // Exercise mouse handlers and actual rendering without showing or driving a desktop window.
    static void MapFixture(MapView map)
    {
        map.Dock = DockStyle.None; map.Size = new Size(1000, 1000);
        SetField(map, "full", new Bitmap(1000, 1000));
        SetField(map, "imageWidth", 1000); SetField(map, "imageHeight", 1000);
        SetField(map, "zoom", 1f); SetField(map, "centerX", 500f); SetField(map, "centerY", 500f); SetField(map, "fitRoutePending", false);
    }
    static void Mouse(MapView map, string method, int x, int y) { Call(map, method, new MouseEventArgs(MouseButtons.Left, 1, x, y, 0)); }
    static void Click(MapView map, int x, int y) { Mouse(map, "OnMouseDown", x, y); Mouse(map, "OnMouseUp", x, y); }

    static void CheckMapEditing()
    {
        var calibration = new MapCalibration { ScaleX = 1, OffsetX = 0, ScaleY = 1, OffsetY = 0, Width = 1000, Height = 1000 };
        using (var map = new MapView(calibration) { EditPoints = true, ShowRouteEndpoints = true })
        {
            MapFixture(map);
            var geometry = new[] { new KeyValuePair<double, double>(100, 100), new KeyValuePair<double, double>(300, 100), new KeyValuePair<double, double>(500, 300) };
            map.SetRoute(geometry);
            int selected = -1, moved = 0, movedIndex = -1, added = 0;
            double movedX = 0, movedY = 0, addedX = 0, addedY = 0;
            map.RoutePointSelected += index => selected = index;
            map.RoutePointMoved += (index, x, y) => { moved++; movedIndex = index; movedX = x; movedY = y; };
            map.RoutePointAdded += (x, y) => { added++; addedX = x; addedY = y; };
            Click(map, 220, 100);
            Check(selected == 1 && map.SelectedRoutePoint == 1 && moved == 0, "A segment click did not select the closest waypoint without moving it.");
            using (var bitmap = new Bitmap(1000, 1000))
            using (var g = Graphics.FromImage(bitmap))
            {
                Call(map, "OnPaint", new PaintEventArgs(g, new Rectangle(0, 0, 1000, 1000)));
                Check(bitmap.GetPixel(300, 100).ToArgb() == Color.DodgerBlue.ToArgb(), "The selected waypoint was not visibly blue.");
            }
            Mouse(map, "OnMouseDown", 300, 100); Mouse(map, "OnMouseMove", 340, 140);
            Check(moved == 0 && Field<List<PointF>>(map, "route")[1] == new PointF(340, 140), "Dragging committed too early or did not preview the new geometry.");
            Mouse(map, "OnMouseUp", 340, 140);
            Check(moved == 1 && movedIndex == 1 && movedX == 340 && movedY == 140, "Mouse release did not commit the new location exactly once.");
            Check(Field<float>(map, "centerX") == 500 && Field<float>(map, "centerY") == 500, "Moving a waypoint panned the map.");

            // Grabbing a segment must move its nearest waypoint relatively, without snapping it to the grab point.
            map.SetRoute(geometry);
            Mouse(map, "OnMouseDown", 220, 100); Mouse(map, "OnMouseMove", 230, 120); Mouse(map, "OnMouseUp", 230, 120);
            Check(movedIndex == 1 && movedX == 310 && movedY == 120, "Dragging from a segment made the nearest point jump to the cursor.");
            map.SetRoute(geometry, true); Click(map, 220, 160);
            Check(selected == 0, "A closing segment selected a synthetic duplicate instead of the real start.");

            map.SetRoute(geometry);
            int commits = moved;
            Mouse(map, "OnMouseDown", 300, 100); Mouse(map, "OnMouseMove", 350, 150);
            Call(map, "OnMouseCaptureChanged", EventArgs.Empty);
            Check(moved == commits && Field<List<PointF>>(map, "route")[1] == new PointF(300, 100), "Losing mouse capture committed or retained an abandoned drag.");
            Mouse(map, "OnMouseDown", 300, 100); Mouse(map, "OnMouseMove", 350, 150);
            var escape = new KeyEventArgs(Keys.Escape); Call(map, "OnKeyDown", escape);
            Check(escape.SuppressKeyPress && moved == commits && Field<List<PointF>>(map, "route")[1] == new PointF(300, 100), "Escape did not cancel only the active drag.");

            map.SetRoute(null); map.AddRoutePoints = true;
            Click(map, 200, 300);
            Check(added == 1 && addedX == 200 && addedY == 300, "A creation click did not emit a world-coordinate point.");
            Mouse(map, "OnMouseDown", 600, 600); Mouse(map, "OnMouseMove", 620, 620); Mouse(map, "OnMouseUp", 620, 620);
            Check(added == 1, "Panning while creating a route added an unwanted point.");
            float zoom = Field<float>(map, "zoom"); Call(map, "OnMouseDoubleClick", new MouseEventArgs(MouseButtons.Left, 2, 300, 300, 0));
            Check(Field<float>(map, "zoom") == zoom, "A double click changed zoom in point editing mode.");

            map.AddRoutePoints = false; map.EditPoints = false; map.SelectedRoutePoint = -1; map.SetRoute(geometry);
            SetField(map, "centerX", 500f); SetField(map, "centerY", 500f);
            Mouse(map, "OnMouseDown", 300, 100); Mouse(map, "OnMouseMove", 340, 140); Mouse(map, "OnMouseUp", 340, 140);
            Check(map.SelectedRoutePoint == -1 && moved == commits && Field<float>(map, "centerX") == 460, "Read-only Mod/Routes maps stopped panning or started editing points.");
        }
    }

    static void CheckNewRoute(RouteLibrary library)
    {
        var template = RouteLibrary.NewRoute();
        using (var editor = new RouteEditorDialog(library, template, true, true))
        {
            var tabs = Field<TabControl>(editor, "tabs");
            Check(tabs.SelectedIndex == 0 && tabs.TabPages[0].Text == "Map", "The simplified editor did not open on the map.");
            var map = Field<RouteMapPanel>(editor, "preview").Map;
            var grid = Field<DataGridView>(editor, "points");
            var save = Field<Button>(editor, "save");
            Check(map.AddRoutePoints && !save.Enabled && grid.Rows.Count == 0, "New route did not start empty in point placement mode.");
            // The real calibration still applies: choose world coordinates using the map's private conversion.
            MapFixture(map);
            var calibration = Field<MapCalibration>(map, "calibration");
            Click(map, 200, 300);
            Check(grid.Rows.Count == 0 && Json.Obj(Field<Dictionary<string, object>>(editor, "data"), "StartPoint") != null, "The first creation click was not the start.");
            Call(editor, "ToggleDrawing");
            Check(map.AddRoutePoints && !save.Enabled, "An incomplete new course could be finished.");
            Click(map, 400, 300);
            Click(map, 600, 500); Call(editor, "UndoPoint");
            Check(grid.Rows.Count == 1, "Undo did not remove the last placed waypoint.");
            Click(map, 650, 550);
            Call(editor, "Save");
            Check(editor.Saved == null, "An unfinished course was saved.");
            Call(editor, "ToggleDrawing");
            Check(!map.AddRoutePoints && save.Enabled && grid.Rows.Count == 2, "Finishing the new route did not switch to drag editing and allow saving.");

            Mouse(map, "OnMouseDown", 400, 300); Mouse(map, "OnMouseMove", 450, 350);
            Check(Math.Abs(Convert.ToDouble(grid.Rows[0].Cells["X"].Value) - calibration.WorldX(400)) < 0.001, "The editor updated a waypoint before the drag was released.");
            Mouse(map, "OnMouseUp", 450, 350);
            Check(Math.Abs(Convert.ToDouble(grid.Rows[0].Cells["X"].Value) - calibration.WorldX(450)) < 0.001
                && Math.Abs(Convert.ToDouble(grid.Rows[0].Cells["Y"].Value) - calibration.WorldY(350)) < 0.001, "Releasing a point did not update the editor's model.");
            Check(map.SelectedRoutePoint == 1, "Dragging lost the selected waypoint.");
            Field<TextBox>(editor, "routeName").Text = "Map-built route";
            Call(editor, "Save");
            Check(editor.Saved != null, "A completed map-built route did not save.");
            var saved = editor.Saved;
            var summary = library.Catalog().First(r => Json.Str(r, "name") == "Map-built route");
            var reopened = library.OpenAsync(summary).GetAwaiter().GetResult();
            Check(reopened.IsCopy && Json.Str(reopened.Data, "Name") == "Map-built route" && Json.List(reopened.Data, "WayPointList").Count == 2, "A newly created route could not be reopened.");
            var last = Json.List(reopened.Data, "WayPointList")[1];
            Check(Math.Abs((Json.Num(last, "X") ?? 0) - calibration.WorldX(650)) < 0.001 && Json.Num(last, "Z") == 0, "The final point or automatic ground height was not saved.");
            Check(Json.Num(saved.Info, "Distance") > 0 && File.Exists(saved.FilePath), "New course geometry/statistics were not persisted.");
            Check(Json.Obj(template.Data, "StartPoint") == null, "Creating a course mutated its template.");
        }
        using (var editor = new RouteEditorDialog(library, RouteLibrary.NewRoute(), true, true))
        {
            Call(editor, "PlacePoint", 10.0, 20.0); Call(editor, "PlacePoint", 30.0, 40.0);
            Call(editor, "UndoPoint"); Call(editor, "UndoPoint");
            Check(Json.Obj(Field<Dictionary<string, object>>(editor, "data"), "StartPoint") == null && !Field<Button>(editor, "save").Enabled, "Undoing the start left a phantom route or enabled saving.");
            Call(editor, "PlacePoint", 10.0, 20.0); Call(editor, "PlacePoint", 30.0, 40.0);
            Call(editor, "ToggleDrawing"); Call(editor, "Save");
            Check(editor.Saved != null && Json.List(editor.Saved.Data, "WayPointList").Count == 1, "A simple start-and-finish route could not be saved.");
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            var en = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            System.Threading.Thread.CurrentThread.CurrentCulture = en;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = en;
            string dir = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(dir);
            bool running = true;
            var library = new RouteLibrary(dir, () => running);
            var data = Json.Parse(@"{""Name"":""Original"",""StartPoint"":{""X"":0.123456,""Y"":1,""Z"":2,""StartExtra"":""keep""},
                ""WayPointConfig"":{""Color"":""FF880000"",""Radius"":10,""Extra"":42},
                ""Props"":[{""Name"":""prop_start_gate_01b"",""Position"":{""X"":1,""Y"":2,""Z"":3},""Rotation"":{""X"":0,""Y"":0,""Z"":90}}],
                ""WayPointList"":[{""X"":10.123456789,""Y"":20,""Z"":3,""Routed"":""False"",""CloseByRange"":12,""ExtraPoint"":""keep""},{""X"":30,""Y"":40,""Z"":4}],
                ""UnknownOption"":{""Enabled"":true,""Label"":""{quoted} \""text\""""}}");
            var info = Json.Parse(@"{""Id"":1,""Name"":""Original"",""Version"":""24.0"",""Distance"":9.64088,""ElevationGain"":68,""ElevationLoss"":68,""MaxElevation"":37,
                ""Author"":{""Name"":""Author"",""OtherCredit"":""keep""},""Description"":""Original description"",""Notes"":""Original notes"",""Loop"":true,""Type"":[""bike"",""run""],""Category"":[""Road""],""UnknownInfo"":true}");
            // A pre-existing high id must not be reused by personal copies.
            var occupied = RouteLibrary.Clone(info); occupied["Id"] = 30000; occupied["Name"] = "Existing custom course";
            var catalog = new Dictionary<string, object> { { "ListVersion", 27.0 }, { "CourseList", new[] { info, occupied } }, { "ExtraCatalog", "keep" } };
            string nativeCatalog = Path.Combine(dir, "courses.json"), originalPath = Path.Combine(dir, "1-24.0.json");
            Write(nativeCatalog, catalog); Write(originalPath, data);
            string original = File.ReadAllText(originalPath), originalCatalog = File.ReadAllText(nativeCatalog);
            var summary = library.Catalog().First(r => Json.Str(r, "id") == "1");
            var source = library.OpenAsync(summary).GetAwaiter().GetResult();
            Check(!source.IsCopy && Json.Bool(source.Info, "UnknownInfo") == true, "Opening lost native metadata.");
            var copyInfo = RouteLibrary.Clone(source.Info); copyInfo["Name"] = "My copy";
            var copy = library.Save(source, copyInfo, source.Data, true);
            Check(Json.Int(copy.Info, "Id") == 30001, "The copy reused an existing course id.");
            Check(copy.IsCopy && copy.FilePath.StartsWith(library.CopiesPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Copy escaped the personal folder.");
            Check(File.ReadAllText(originalPath) == original && File.ReadAllText(nativeCatalog) == originalCatalog, "Creating a copy changed original files.");
            Check(Json.Obj(copy.Data, "UnknownOption") != null && Json.Str(Json.Obj(copy.Info, "Author"), "OtherCredit") == "keep", "Copy lost unknown fields or attribution.");
            Check(library.NeedsSync() && !library.SyncToGame(), "Publishing was allowed while GTA V ran.");
            Check(File.ReadAllText(nativeCatalog) == originalCatalog && !File.Exists(Path.Combine(dir, "30001-1.0.json")), "Blocked sync wrote game files.");

            running = false;
            Check(library.SyncToGame() && !library.NeedsSync(), "Copy was not published after the game closed.");
            var published = Read(nativeCatalog);
            Check(Json.Num(published, "ListVersion") == 27 && Json.Str(published, "ExtraCatalog") == "keep", "Sync changed remote version or other catalog fields.");
            Check(Json.List(published, "CourseList").Count == 3 && File.Exists(Path.Combine(dir, "30001-1.0.json")), "Native course registration is incomplete.");
            Check(Directory.GetFiles(Path.Combine(library.CopiesPath, "backups"), "*.json").Length == 1, "The game catalog was not backed up.");

            // A remote library update may replace the native index. Personal copies must remain recoverable.
            catalog["ListVersion"] = 28; Write(nativeCatalog, catalog);
            var reopened = new RouteLibrary(dir, () => running);
            Check(reopened.CopyIds().Contains("30001") && reopened.NeedsSync(), "Remote refresh erased the personal library.");
            Check(reopened.SyncToGame() && Json.Num(Read(nativeCatalog), "ListVersion") == 28, "Restoring copies rolled back the remote library version.");

            var geometry = RouteLibrary.Geometry(data, true, true);
            Check(geometry.Count == 4 && geometry[0].Key == 0.123456 && geometry[1].Key == 30 && geometry[3].Equals(geometry[0]), "Loop/reverse preview geometry is wrong.");
            Check(RouteLibrary.Serialize(Json.Parse(RouteEditorDialog.Pretty(data))) == RouteLibrary.Serialize(data), "Advanced JSON formatting changed the course.");

            // Exercise the actual editor: reordered points retain options; metadata and map changes reach the saved definition.
            using (var editor = new RouteEditorDialog(library, copy, false))
            {
                Field<TextBox>(editor, "routeName").Text = "Edited copy";
                Field<TextBox>(editor, "description").Text = "Edited description";
                var grid = Field<DataGridView>(editor, "points");
                grid.CurrentCell = grid.Rows[1].Cells["X"];
                Call(editor, "MovePoint", -1);
                grid.Rows[0].Cells["X"].Value = "50.5";
                Call(editor, "Save");
                Check(editor.Saved != null, "The editor did not save valid changes.");
                var saved = editor.Saved;
                Check(Json.Str(saved.Info, "Version") == "2.0" && Json.Str(saved.Info, "Description") == "Edited description", "Edits did not create a new native course version.");
                Check(Json.Str(saved.Data, "Name") == "Edited copy" && Json.Num(Json.List(saved.Data, "WayPointList")[0], "X") == 50.5, "The editor did not save name/geometry changes.");
                var moved = Json.List(saved.Data, "WayPointList")[1];
                Check(Json.Str(moved, "ExtraPoint") == "keep" && Json.Bool(moved, "Routed") == false, "Reordering points lost native options.");
                Check(Json.Num(Json.Obj(saved.Data, "StartPoint"), "X") == 0.123456 && Json.Str(Json.Obj(saved.Data, "StartPoint"), "StartExtra") == "keep", "The editor rounded or replaced an untouched start point.");
                Check(Json.Num(Json.Obj(saved.Data, "WayPointConfig"), "Extra") == 42 && Json.List(saved.Data, "Props").Count == 1, "The editor lost props or appearance fields.");
                Check(library.NeedsSync(), "The edited copy was not marked for publishing.");
                copy = saved;
            }
            // Applying advanced JSON must preserve unexposed per-point fields through subsequent form edits.
            using (var editor = new RouteEditorDialog(library, copy, false))
            {
                var changed = RouteLibrary.Clone(copy.Data);
                changed["AdvancedFlag"] = "saved";
                Field<TextBox>(editor, "raw").Text = RouteLibrary.Serialize(changed);
                Call(editor, "Save");
                Check(editor.Saved != null && Json.Str(editor.Saved.Data, "AdvancedFlag") == "saved", "Advanced JSON did not survive saving.");
                copy = editor.Saved;
            }
            File.AppendAllText(copy.FilePath, " ");
            Reject(() => library.Save(copy, copy.Info, copy.Data, false), "An external edit was silently overwritten.");
            Check(File.ReadAllText(originalPath) == original, "Editing a copy changed the original course.");

            var invalid = RouteLibrary.Clone(data); Json.Obj(invalid, "StartPoint")["X"] = Double.NaN;
            Reject(() => RouteLibrary.Validate(invalid), "NaN coordinates were accepted.");
            invalid = RouteLibrary.Clone(data); Json.Obj(invalid, "WayPointConfig")["Color"] = "bad";
            Reject(() => RouteLibrary.Validate(invalid), "Invalid waypoint colors were accepted.");
            invalid = RouteLibrary.Clone(data); invalid["WayPointList"] = new object[] { null };
            Reject(() => RouteLibrary.Validate(invalid), "Malformed waypoint arrays were accepted.");
            var traversal = RouteLibrary.Clone(summary); traversal["version"] = "../../escape";
            Reject(() => library.OpenAsync(traversal).GetAwaiter().GetResult(), "A course version escaped its directory.");
            CheckMapEditing();
            CheckNewRoute(library);
            Console.WriteLine("Desktop routes: " + checks + " checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
