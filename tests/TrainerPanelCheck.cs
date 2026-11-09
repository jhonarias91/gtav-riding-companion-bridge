// Windows native gears rendering/state checks. Does not start GTA/Bridge or write user preferences.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using GTAVRidingCompanion;

public static class TrainerPanelCheck
{
    static int checks;
    static T Field<T>(object instance, string name) { return (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance); }
    static void Put(object instance, string name, object value) { instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value); }
    static void Call(object instance, string name) { instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, null); }
    static void Check(bool pass, string reason) { checks++; if (!pass) throw new Exception(reason); }
    static void Render(Control control, string path)
    {
        using (var form = new Form { Size = new Size(940, 850), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) })
        {
            form.Controls.Add(control); form.Show(); control.PerformLayout(); Application.DoEvents();
            using (var image = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(image, control.ClientRectangle); image.Save(path, ImageFormat.Png); }
            form.Hide(); form.Controls.Remove(control);
        }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Directory.CreateDirectory(args[0]);
            using (var panel = new TrainerPanel(() => null))
            {
                var live = Json.Parse(@"{""available"":true,""enabled"":true,""mode"":""manual"",""gear"":3,""gearCount"":24,""ratio"":0.99,""canShiftUp"":true,""canShiftDown"":true,""busy"":false}");
                var config = Json.Parse(@"{""available"":true,""reasonCode"":null,""revision"":""modcfg-original"",""gameRunning"":true,""editable"":false,""applyMode"":""nextGameStart"",""nativeMode"":""manual"",""options"":{""enabled"":true,""ratios"":[0.75,0.87,0.99]},""defaults"":{""enabled"":false,""ratios"":[0.75,0.87,0.99]}}");
                Put(panel, "live", live); Put(panel, "config", config);
                Put(panel, "state", Json.Parse(@"{""layers"":{""adapter"":""fresh""},""testInput"":{""mode"":""keyboard""}}"));
                Call(panel, "Fill"); Call(panel, "UpdateState");
                Check(Field<Button>(panel, "up").Enabled && Field<Button>(panel, "down").Enabled, "native observed gear permits both shifts");
                Check(Field<Label>(panel, "observed").Text.Contains("Gear 3 / 24") && Field<Label>(panel, "observed").Text.Contains("0.99"), "gear and ratio are observed");
                Check(Field<Label>(panel, "source").Text.Contains("simulated"), "simulation is explicit");
                Check(!Field<CheckBox>(panel, "enabled").Enabled && !Field<TextBox>(panel, "ratios").Enabled && !Field<Button>(panel, "save").Enabled, "GTA running locks INI editors");
                Render(panel, Path.Combine(args[0], "windows-virtual-gears.png"));
                live["available"] = false; live["reasonCode"] = "ADAPTER_STALE"; Call(panel, "UpdateState");
                Check(!Field<Button>(panel, "up").Enabled && !Field<Button>(panel, "down").Enabled, "stale game data blocks gear changes");
                Check(Field<Label>(panel, "liveReason").Text.Length > 0, "stale reason is visible");
                live["available"] = true; live["reasonCode"] = null; live["canShiftDown"] = false; live["shiftReasonCode"] = "GEAR_LIMIT"; Call(panel, "UpdateState");
                Check(!Field<Button>(panel, "down").Enabled && Field<Button>(panel, "up").Enabled, "gear bound blocks only its direction");
                config["editable"] = true; config["gameRunning"] = false; Call(panel, "Fill");
                Check(Field<CheckBox>(panel, "enabled").Enabled && Field<TextBox>(panel, "ratios").Enabled, "closed GTA unlocks native INI options");
                Field<CheckBox>(panel, "enabled").Checked = false;
                Check(Field<Button>(panel, "save").Enabled && Field<string>(panel, "revision") == "modcfg-original", "draft retains its original revision");
                config["revision"] = "modcfg-external-change"; Call(panel, "UpdateState");
                Check(Field<string>(panel, "revision") == "modcfg-original", "polling cannot overwrite the edited draft revision");
                Render(panel, Path.Combine(args[0], "windows-trainer-config.png"));
                foreach (string mode in new[] { "auto", "native" })
                {
                    config["nativeMode"] = mode;
                    Json.Obj(config, "options")["enabled"] = true;
                    Call(panel, "Fill");
                    var changes = (Dictionary<string, object>)typeof(TrainerPanel).GetMethod("ChangedOptions", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(panel, new object[] { new double[] { 0.8, 1.0, 1.2 } });
                    Check(changes.Count == 1 && changes.ContainsKey("ratios") && !changes.ContainsKey("enabled"), "ratio-only save preserves " + mode + " mode");
                }
                Field<CheckBox>(panel, "enabled").Checked = false;
                var toggleChanges = (Dictionary<string, object>)typeof(TrainerPanel).GetMethod("ChangedOptions", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(panel, new object[] { new double[] { 0.75, 0.87, 0.99 } });
                Check(toggleChanges.Count == 1 && toggleChanges.ContainsKey("enabled") && !(bool)toggleChanges["enabled"], "enabled-only save omits unchanged ratios");
            }
            Console.WriteLine("Windows trainer UI: " + checks + " checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
