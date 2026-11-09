// Runs the shipped 0.8.0.4 VirtualGearBox source with controlled game state and Tick events.
using System;
using System.Collections.Generic;
using System.Reflection;
using CompanionBridge.V1;

internal static class GearsHarness
{
    private static int checks;
    private static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
    private static Dictionary<string, object> Empty() { return new Dictionary<string, object>(); }
    private static void Main()
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var script = new GTBikeV.GTBikeVScript();
        Func<string, Type> types = name => typeof(GTBikeV.DataTransceiver).Assembly.GetType(name);
        var gears = new GtbikeV1Gears(types, true, script);
        Check(gears.Supported, "native shape supported");
        Check(!new GtbikeV1Gears(types, false, script).Supported, "version gate");
        GTBikeV.DataTransceiver.VirtualGearBox = new GTBikeV.VirtualGearBox("False,1,2,3,4");
        Check((string)gears.Command("gears.shiftUp", "disabled", Empty(), "inControl")["reasonCode"] == "GEARS_DISABLED", "disabled guard");
        GTBikeV.DataTransceiver.VirtualGearBox = new GTBikeV.VirtualGearBox("True,1,2,3,4");
        var before = gears.Capture("inControl");
        Check((int)before["gear"] == 1 && (int)before["gearCount"] == 4 && (string)before["mode"] == "manual", "true means native manual and one-based gear");
        Check((string)gears.Command("gears.shiftUp", "badargs", new Dictionary<string, object> { { "key", "F5" } }, "inControl")["reasonCode"] == "INVALID_REQUEST", "no generic key args");
        Check((string)gears.Command("gears.shiftDown", "limit", Empty(), "inControl")["reasonCode"] == "GEAR_LIMIT", "lower boundary");
        Check((string)gears.Command("gears.shiftUp", "up", Empty(), "inControl")["status"] == "dispatched", "queued shift");
        Check(GTBikeV.DataTransceiver.VirtualGearBox.GearIndex == 0, "no invocation on adapter thread");
        Check((string)gears.Command("gears.shiftUp", "again", Empty(), "inControl")["reasonCode"] == "BRIDGE_BUSY", "one in flight");
        script.Raise(); script.Raise();
        Check(GTBikeV.DataTransceiver.VirtualGearBox.GearIndex == 1, "native Tick runs exactly once");
        var result = (Dictionary<string, object>)gears.Capture("inControl")["command"];
        Check((string)result["requestId"] == "up" && (string)result["status"] == "done" && (int)result["gear"] == 2, "request-specific observed result");
        gears.Command("gears.shiftUp", "paused", Empty(), "inControl");
        script.InControl = false; script.Raise(); script.InControl = true;
        Check(GTBikeV.DataTransceiver.VirtualGearBox.GearIndex == 1, "recheck control on native Tick");
        Check((string)((Dictionary<string, object>)gears.Capture("inControl")["command"])["reasonCode"] == "GAME_NOT_IN_CONTROL", "paused result");
        GTBikeV.DataTransceiver.IsDTOK = false;
        Check((string)gears.Command("gears.shiftUp", "inactive", Empty(), "inControl")["reasonCode"] == "MOD_INACTIVE", "inactive guard");
        GTBikeV.DataTransceiver.IsDTOK = true;
        GTBikeV.DataTransceiver.VirtualGearBox = new GTBikeV.VirtualGearBox("Auto,1,2,3,4");
        gears.Command("gears.shiftUp", "auto", Empty(), "inControl"); script.Raise();
        Check((string)gears.Capture("inControl")["mode"] == "tempManual", "native external shift preserves Auto to TempManual behavior");
        GTBikeV.DataTransceiver.VirtualGearBox = new GTBikeV.VirtualGearBox("Native,1,2,3,4");
        gears.Command("gears.shiftUp", "native", Empty(), "inControl"); script.Raise();
        Check(GTBikeV.DataTransceiver.LastRatio == 2f, "native gearing ratio dispatch");
        Check(GTBikeV.DataTransceiver.ForbiddenPowerWrites == 0, "no ERG or workout bias writes");
        Console.WriteLine("Native gears harness passed: " + checks + " checks.");
    }
}
namespace GTBikeV
{
    public static class DataTransceiver
    {
        internal static VirtualGearBox VirtualGearBox { get; set; }
        public static bool IsDTOK { get; set; }
        public static float LastRatio;
        public static int ForbiddenPowerWrites;
        public static void SendGearRatio(float ratio) { LastRatio = ratio; }
        static DataTransceiver() { IsDTOK = true; }
    }
    public sealed class GTBikeVScript
    {
        public event EventHandler Tick;
        public bool InControl = true;
        private bool IsPlayerInControl { get { return InControl; } }
        public void Raise() { if (Tick != null) Tick(this, EventArgs.Empty); }
    }
    internal static class Timer { public static long MillisSinceStartup() { return Environment.TickCount; } }
    internal static class Config { public static float UserFTP = 250; }
}
namespace GTBikeV.Workouts
{
    internal static class Workout { public static float GetPowerPctFromZone(int zone) { return zone == 0 ? 0.5f : 1.2f; } }
}
