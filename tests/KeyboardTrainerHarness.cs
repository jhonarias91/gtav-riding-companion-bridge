// Actual 0.8.0.4 VirtualTrainer physics compiled alongside controlled game/receiver/timer stubs.
// NativePoll reproduces the ordering that used to erase test watts before the mod applied inertia.
#pragma warning disable 0649, 0414
using System;
using System.Collections.Generic;
using System.Reflection;
using CompanionBridge.V1;

internal static class KeyboardTrainerHarness
{
    private static int checks;
    private static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
    private static GtbikeV1KeyboardTrainer Create(bool supported)
    {
        return new GtbikeV1KeyboardTrainer(name => typeof(GTBikeV.DataTransceiver).Assembly.GetType(name), supported, GTBikeV.GTBikeVScript.Instance);
    }
    private static void Frame(GtbikeV1KeyboardTrainer test, bool w, bool s, bool allowed)
    {
        GTBikeV.Timer.Now += 16;
        GTBikeV.DataTransceiver.NativePoll();
        test.Step(w, s, allowed, 0.016);
    }
    public static int Main()
    {
        try
        {
            GTBikeV.Timer.Add("DATA_UPDATE", 64);
            GTBikeV.Timer.Add("OTHER", 42);
            var wrong = Create(false);
            Check(wrong.Start() != null && !wrong.Active, "unsupported mod rejected");
            var test = Create(true);
            GTBikeV.DataTransceiver.DTInitialized = true;
            Check(test.Start() != null && !test.Active, "real receiver startup rejected");
            GTBikeV.DataTransceiver.DTInitialized = false;
            GTBikeV.DataTransceiver.ANTStatus = GTBikeV.DataTransceiver.ReceiverState.ERROR;
            Check(test.Start() != null, "uncertain receiver state rejected");
            GTBikeV.DataTransceiver.ANTStatus = GTBikeV.DataTransceiver.ReceiverState.UNINITIALIZED;
            GTBikeV.Devices.DeviceInfo.Connected = true;
            Check(test.Start() != null, "physical device rejected");
            GTBikeV.Devices.DeviceInfo.Connected = false;
            GTBikeV.GTBikeVScript.Instance.TestActivity = null;
            object trainer = GTBikeV.DataTransceiver._virtualTrainer, rows = GTBikeV.DataTransceiver._receiverData;
            Check(test.Start() == null && test.Active, "keyboard start");
            Check(GTBikeV.DataTransceiver.DTInitialized, "data transceiver initialized for simulation");
            Check(GTBikeV.Timer.Get("DATA_UPDATE").interval == uint.MaxValue && GTBikeV.Timer.Get("OTHER").interval == 42, "only hardware update gated");
            Check(!object.ReferenceEquals(trainer, GTBikeV.DataTransceiver._virtualTrainer), "original physics isolated");
            test.TargetPowerW = 5000; Check(test.TargetPowerW == 1000, "power upper bound");
            test.TargetPowerW = 0; Check(test.TargetPowerW == 50, "power lower bound");
            test.TargetPowerW = 200;
            GTBikeV.GTBikeVScript.Instance.TestActivity = new GTBikeV.Activity();
            for (int i = 0; i < 900; i++) Frame(test, true, false, true);
            float speed = GTBikeV.DataTransceiver.Speed_ms;
            Check(speed > 1f && speed < 25f, "W accelerates using real native physics despite native ANT poll");
            Check(GTBikeV.DataTransceiver.Power_w == 200 && GTBikeV.DataTransceiver.Cadence_rpm == 85, "synthetic pedalling");
            Check(GTBikeV.DataTransceiver.TotalDistance_m > 10 && GTBikeV.DataTransceiver.RidingTime_ms > 10000, "distance and elapsed time advance");
            Check(GTBikeV.GTBikeVScript.Instance.TestActivity.MINIMUM_DISTANCE_TO_SAVE_ACTIVITY == 100, "test activity can be encoded and saved");
            float distance = GTBikeV.DataTransceiver.TotalDistance_m;
            for (int i = 0; i < 180; i++) Frame(test, false, false, true);
            Check(GTBikeV.DataTransceiver.Power_w == 0 && GTBikeV.DataTransceiver.Cadence_rpm == 0, "release W stops pedal input");
            Check(GTBikeV.DataTransceiver.Speed_ms < speed, "coasting decelerates");
            Check(GTBikeV.DataTransceiver.TotalDistance_m > distance, "coasting still covers distance");
            float coast = GTBikeV.DataTransceiver.Speed_ms;
            for (int i = 0; i < 180; i++) Frame(test, false, true, true);
            Check(GTBikeV.DataTransceiver.Speed_ms < coast, "S brakes");
            distance = GTBikeV.DataTransceiver.TotalDistance_m;
            long time = GTBikeV.DataTransceiver.RidingTime_ms;
            float beforePause = GTBikeV.DataTransceiver.Speed_ms;
            for (int i = 0; i < 30; i++) Frame(test, true, false, false);
            Check(GTBikeV.DataTransceiver.Power_w == 0 && GTBikeV.DataTransceiver.Cadence_rpm == 0, "pause/focus/menu suppress input");
            Check(GTBikeV.DataTransceiver.Speed_ms == 0, "pause or dismount stops the native rider and allows mounting");
            Check(GTBikeV.DataTransceiver.TotalDistance_m == distance && GTBikeV.DataTransceiver.RidingTime_ms == time, "pause does not accumulate distance/time");
            GTBikeV.Timer.Now += 5000;
            GTBikeV.DataTransceiver.NativePoll();
            test.Step(true, false, true, 5.0);
            Check(Math.Abs(GTBikeV.DataTransceiver.Speed_ms - beforePause) < 0.001f, "resume after all scripts suspended has no physics jump");
            Check(GTBikeV.DataTransceiver.TotalDistance_m == distance && GTBikeV.DataTransceiver.RidingTime_ms == time, "suspended gap never counted as ride");
            var state = new Dictionary<string, object> { { "metrics", new Dictionary<string, object>() },
                { "commands", new List<string> { "mod.setActive", "activities.finish" } },
                { "activities", new Dictionary<string, object> { { "saveEnabled", true } } } };
            test.Overlay(state);
            Check(((Dictionary<string, object>)state["testInput"])["mode"].Equals("keyboard"), "test telemetry label");
            Check(((Dictionary<string, object>)state["receivers"])["ble"].Equals("ready"), "simulated ble receiver ready");
            Check(((List<string>)state["commands"]).Contains("activities.finish") && ((List<string>)state["commands"]).Contains("mod.setActive")
                && object.Equals(((Dictionary<string, object>)state["activities"])["saveEnabled"], true), "activities can be finished and saved in simulated mode");
            Check(((List<Dictionary<string, object>>)state["sensors"]).Count > 0, "simulated smart trainer sensors reported");
            test.Stop(); test.Stop();
            Check(!test.Active && test.Report() == null, "idempotent exit");
            Check(object.ReferenceEquals(trainer, GTBikeV.DataTransceiver._virtualTrainer) && object.ReferenceEquals(rows, GTBikeV.DataTransceiver._receiverData), "native objects restored");
            Check(GTBikeV.DataTransceiver.ANTStatus == GTBikeV.DataTransceiver.ReceiverState.UNINITIALIZED && GTBikeV.DataTransceiver.BLEStatus == GTBikeV.DataTransceiver.ReceiverState.UNINITIALIZED, "native receiver status restored");
            Check(GTBikeV.Timer.Get("DATA_UPDATE").interval == 64 && GTBikeV.Timer.Get("OTHER").interval == 42, "timers restored");
            Check(GTBikeV.GTBikeVScript.Instance.TestActivity != null, "ride activity preserved on stop");
            Check(test.Start() == null, "can restart");
            GTBikeV.DataTransceiver.stopRequested = true;
            Frame(test, true, false, true);
            Check(!test.Active && !GTBikeV.DataTransceiver.stopRequested, "native stop unwinds test state");
            Console.WriteLine("Keyboard trainer harness passed: " + checks + " checks (native 0.8.0.4 physics).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

namespace GTBikeV
{
    internal sealed class Activity
    {
        public float MINIMUM_DISTANCE_TO_SAVE_ACTIVITY = 100;
        public bool telemetryReporting = true;
    }
    internal sealed class GTBikeVScript
    {
        public static readonly GTBikeVScript Instance = new GTBikeVScript();
        public static object CurrentPlayer { get { return new object(); } }
        private Activity currentActivity { get; set; }
        private object currentCourse { get; set; }
        public Activity TestActivity { get { return currentActivity; } set { currentActivity = value; } }
    }
    internal static class Timer
    {
        public static long Now = 10000;
        private static Dictionary<string, TimerContent> timerCollection = new Dictionary<string, TimerContent>();
        public static long MillisSinceStartup() { return Now; }
        public static void Add(string name, uint interval) { timerCollection[name] = new TimerContent { interval = interval }; }
        public static TimerContent Get(string name) { return timerCollection[name]; }
        public sealed class TimerContent { public uint interval; public bool timeOut; public long lastTimeOut; }
    }
    internal sealed class VirtualGearBox
    {
        public Func<float, bool, float> PowerCalculationFunction;
        public bool BrakesApplied { get; set; }
        public void Reset() { }
        public void Update(byte cadence, short size) { }
    }
    internal static class Config
    {
        public static VirtualGearBox VirtualGears = new VirtualGearBox();
        public static byte BrakingDivisor { get { return 6; } }
    }
    internal static class Utils { public static double Cbrt(double value) { return Math.Sign(value) * Math.Pow(Math.Abs(value), 1.0 / 3); } }
    internal static class Log
    {
        public static void Error(string message) { }
        public static void Warning(string message) { }
        public static void Verbose(string message) { }
    }
    internal static class DataTransceiver
    {
        public enum ReceiverState { UNINITIALIZED, INITIALIZED, STARTED, COMPLETE, READY, STOPPING, STOPPED, ERROR }
        public enum SportType { SPORT_TYPE_UNKNOWN, SPORT_TYPE_CYCLING = 25 }
        public enum BrakingStatus { NotBraking, InternalBraking }
        public enum Source { ANT, BLE }
        public sealed class Row
        {
            public int Power_w = -1; public byte Cadence_rpm = 255, HeartRate = 255, BrakingValue = 255;
            public float Speed_ms = -1; public int SteeringAngle = 32767; public bool IsReceiverOK; public SportType Sport;
        }
        public sealed class Bicycle { public float Mass = 10, GripFactor = 1; }
        public sealed class Atmosphere { public float windResCoeff = 0.4f; public int realWindSpeed_kmh; }
        public static bool DTInitialized, stopRequested, AlwaysUseVirtualSpeed = true;
        public static ReceiverState ANTStatus, BLEStatus;
        public static long startTicks;
        public static object player;
        public static Dictionary<Source, Row> _receiverData = new Dictionary<Source, Row> { { Source.ANT, new Row() }, { Source.BLE, new Row() } };
        public static VirtualTrainer _virtualTrainer = new VirtualTrainer();
        public static float PlayerWeight = 75, TerrainFriction = 0.004f, TerrainTraction, DraftingCoeff = 1;
        public static Bicycle VehicleParams = new Bicycle();
        public static BrakingStatus BrakingState;
        public static byte brakingLevel;
        public static float Slope { get; set; }
        public static float TotalDistance_m { get; private set; }
        public static long RidingTime_ms { get; private set; }
        public static bool IsDTOK { get { return ANTStatus == ReceiverState.READY || BLEStatus == ReceiverState.READY; } }
        public static float Speed_ms { get { return IsDTOK ? _virtualTrainer.GetSpeed() : -1; } }
        public static int Power_w { get { return IsDTOK ? _receiverData[Source.ANT].Power_w : -1; } }
        public static byte Cadence_rpm { get { return IsDTOK ? _receiverData[Source.ANT].Cadence_rpm : (byte)255; } }
        private static Atmosphere AtmosphericCalculation() { return new Atmosphere(); }
        public static void NativePoll()
        {
            if (!IsDTOK) return;
            _receiverData[Source.ANT].Power_w = 0;
            _receiverData[Source.ANT].Cadence_rpm = 0;
            _virtualTrainer.Power = 0;
            _virtualTrainer.Cadence = 0;
            _virtualTrainer.Update(false);
        }
    }
}
namespace GTBikeV.Devices
{
    internal enum DeviceFunctionType { Power, Resistance, Cadence, HeartRate }
    internal static class DeviceInfo
    {
        public static bool Connected;
        public static object GetCurrentDeviceFor(DeviceFunctionType role) { return Connected ? new object() : null; }
    }
}
