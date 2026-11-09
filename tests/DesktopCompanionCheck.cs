// Exercises real WinForms state rendering without starting GTA, the bridge, or the installed app.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using GTAVRidingCompanion;

public static class DesktopCompanionCheck
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static T Field<T>(object instance, string name) { return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
    static void Set(object instance, string name, object value) { instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value); }
    static void Call(object instance, string name) { instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, null); }
    static void Render(Control control, string path)
    {
        control.Dock = DockStyle.None; control.Size = new Size(1040, 850); control.CreateControl(); control.PerformLayout();
        using (var image = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(image, control.ClientRectangle); image.Save(path, ImageFormat.Png); }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Directory.CreateDirectory(args[0]);
            using (var camera = new CameraPanel(() => null))
            {
                Set(camera, "camera", Json.Parse(@"{""available"":true,""reasonCode"":null,""currentMode"":""medium"",""modes"":[""near"",""medium"",""far"",""firstPerson"",""cinematic""],""headingDeg"":15,""pitchDeg"":-5,""canLook"":true,""lookReasonCode"":null,""busy"":false}"));
                Set(camera, "capabilities", Json.Parse(@"{""capabilities"":[{""id"":""camera.next"",""available"":true},{""id"":""camera.setMode"",""available"":true},{""id"":""camera.look"",""available"":true}]}"));
                Call(camera, "UpdateState");
                var modes = Field<Dictionary<string, Button>>(camera, "modes");
                Check(modes.Count == 5 && modes["medium"].Enabled && (bool)modes["medium"].Tag && !(bool)modes["near"].Tag, "camera reflects observed mode");
                Check(Field<TrackBar>(camera, "heading").Value == 15 && Field<TrackBar>(camera, "pitch").Value == -5, "observed camera angles");
                Check(Field<Button>(camera, "left").Enabled, "orbit available");
                Render(camera, Path.Combine(args[0], "windows-camera.png"));
                var document = Field<Dictionary<string, object>>(camera, "camera");
                document["canLook"] = false; document["lookReasonCode"] = "CAMERA_CONTROL_IN_USE";
                Call(camera, "UpdateState");
                Check(!Field<Button>(camera, "left").Enabled && !Field<TrackBar>(camera, "heading").Enabled && modes["near"].Enabled, "look behind blocks orbit but keeps view selection");
                Set(camera, "errorCode", "ADAPTER_STALE"); Call(camera, "UpdateState");
                Check(!Field<Button>(camera, "next").Enabled && !modes["near"].Enabled, "stale camera disables commands");
            }
            using (var mod = new ModPanel(() => null, () => false))
            {
                var state = Json.Parse(@"{""layers"":{""adapter"":""fresh"",""mod"":""dataReady"",""gameControl"":""inControl""},""testInput"":{""mode"":""keyboard"",""targetPowerW"":200},""metrics"":{""speedMps"":{""value"":5,""quality"":""valid""},""powerW"":{""value"":200,""quality"":""valid""},""cadenceRpm"":{""value"":85,""quality"":""valid""},""heartRateBpm"":{""value"":null,""quality"":""unavailable""},""distanceM"":{""value"":0,""quality"":""valid""},""ridingTimeMs"":{""value"":10000,""quality"":""valid""}}}");
                Set(mod, "state", state); Call(mod, "ShowState");
                Check(Field<Label>(mod, "powerValue").Text == "200 W (simulated)", "synthetic power labelled");
                Check(Field<Label>(mod, "speedValue").Text.StartsWith("18.0 km/h"), "SI speed converted for Windows");
                Check(Field<Label>(mod, "heartRateValue").Text == "—", "missing heart rate is not zero");
                Check(Field<Label>(mod, "distanceValue").Text.StartsWith("0.00 km"), "real zero retained");
                Check(Field<Label>(mod, "keyboardValue").Text.Contains("SIMULATED"), "keyboard mode explicit on PC");
                Render(mod, Path.Combine(args[0], "windows-keyboard-trainer.png"));
                Json.Obj(state, "layers")["adapter"] = "stale";
                Json.Obj(Json.Obj(state, "metrics"), "powerW")["quality"] = "stale";
                Call(mod, "ShowState");
                Check(Field<Label>(mod, "powerValue").Text.Contains("last value"), "stale power is explicit");
                Check(!Field<Button>(mod, "toggle").Enabled, "stale data disables mod toggle");
                Set(mod, "state", null); Call(mod, "ShowState");
                Check(Field<Label>(mod, "powerValue").Text == "—", "offline clears readings");
            }
            using (var riders = new RidersPanel(() => null))
                Check(Field<DataGridView>(riders, "riders").ReadOnly, "ranking remains read only");
            using (var diagnostics = new DiagnosticsPanel(() => null))
            {
                Set(diagnostics, "hello", Json.Parse(@"{""bridgeVersion"":""1.1.0"",""apiMajor"":1,""apiMinor"":15,""transport"":""https"",""token"":""SECRET_TOKEN""}"));
                Set(diagnostics, "state", Json.Parse(@"{""modVersion"":""0.8.0.4"",""bindingProfile"":""gtbikev-0804"",""snapshotAgeMs"":50,""layers"":{""adapter"":""fresh"",""binding"":""compatible"",""mod"":""dataReady"",""antReceiver"":""uninitialized"",""bleReceiver"":""uninitialized"",""gameControl"":""inControl""},""testInput"":{""mode"":""keyboard""},""sensors"":[{""deviceId"":""PRIVATE_DEVICE""}],""path"":""PRIVATE_PATH"",""riderName"":""PRIVATE_RIDER""}"));
                Set(diagnostics, "capabilities", Json.Parse(@"{""capabilities"":[{""id"":""metrics.read"",""available"":true},{""id"":""camera.look"",""available"":false,""reasonCode"":""CAMERA_CONTROL_IN_USE""}]}"));
                Call(diagnostics, "UpdateState");
                string report = diagnostics.ReportText;
                Check(report.Contains("keyboard test (simulated)") && report.Contains("CAMERA_CONTROL_IN_USE"), "diagnostics carry input provenance and reasons");
                Check(!report.Contains("SECRET_TOKEN") && !report.Contains("PRIVATE_"), "support report excludes credentials and private fields");
                Check(Field<ListView>(diagnostics, "capabilityList").Items.Count == 2, "capabilities visible in Windows");
                Render(diagnostics, Path.Combine(args[0], "windows-diagnostics.png"));
            }
            Console.WriteLine("Desktop companion checks passed: " + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
