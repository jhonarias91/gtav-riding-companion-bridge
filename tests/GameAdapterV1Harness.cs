// Mock GTBikeV 0.8.0.4 surface for the v1 binding. Not an in-game test.
// Run: bridge\Test-GameAdapterV1.ps1
using System;
using System.Collections.Generic;
using CompanionBridge.V1;

namespace GTBikeV
{
    public static class DataTransceiver
    {
        private enum ReceiverState { UNINITIALIZED, INITIALIZED, STARTED, COMPLETE, READY, STOPPING, STOPPED, ERROR }
        public enum SportType { SPORT_TYPE_UNKNOWN, SPORT_TYPE_RUNNING = 19, SPORT_TYPE_ROWING = 22, SPORT_TYPE_CYCLING = 25 }
        private static ReceiverState ANTStatus;
        private static ReceiverState BLEStatus;
        public static int Power_w { get; set; }
        public static float Speed_ms { get; set; }
        public static bool IsSpeedVirtual { get; set; }
        public static byte Cadence_rpm { get; set; }
        public static byte HeartRate { get; set; }
        public static float TotalDistance_m { get; set; }
        public static long RidingTime_ms { get; set; }
        public static float Slope { get; set; }
        public static float WindSpeed_kmh { get; set; }
        public static float WindDir_rad { get; set; }
        public static SportType Sport { get; set; }
        public static bool IsDTOK
        {
            get
            {
                return ANTStatus == ReceiverState.READY || ANTStatus == ReceiverState.STOPPING ||
                       BLEStatus == ReceiverState.READY || BLEStatus == ReceiverState.STOPPING;
            }
        }
        public static void SetStates(string ant, string ble)
        {
            ANTStatus = (ReceiverState)Enum.Parse(typeof(ReceiverState), ant);
            BLEStatus = (ReceiverState)Enum.Parse(typeof(ReceiverState), ble);
        }
    }

    public class LapData
    {
        public long ElapsedTime_ms { get { return 754000; } }
        public ushort Distance { get { return 6120; } }
        public ushort PowerAverage { get { return 231; } }
    }

    internal class Activity
    {
        public long TotalMovingTime { get; set; }
        public float ElevationGain { get; set; }
        public float ElevationLoss { get; set; }
        public GTBikeV.Workouts.Workout Workout;
    }
}

namespace GTBikeV.Courses
{
    // Same shape as GTBike V 0.8.0.4: internal class, public event of a nested public delegate with a public LapData.
    internal class Course
    {
        public delegate void LoopEvent(string name, bool noLoop, uint loopNumber, GTBikeV.LapData lapData);
        public event LoopEvent OnCourseCompleted;
        public int Listeners { get { return OnCourseCompleted == null ? 0 : OnCourseCompleted.GetInvocationList().Length; } }
        public void Finish(string name, bool loop, uint lap, GTBikeV.LapData data) { LoopEvent e = OnCourseCompleted; if (e != null) e(name, loop, lap, data); }
    }
}

namespace GTBikeV.Workouts
{
    public class Workout { public bool Finished { get; set; } }
}

namespace GTBikeV.Network
{
    internal static class Multiplayer { internal static GTBikeV.Activity CurrentActivity { get; set; } }
}

namespace GTBikeV.Devices
{
    public enum DeviceFunctionType { Power, Resistance, Cadence, HeartRate, Steering, Controls, RunningSpeed }
    public enum DataSource { Unknown = -1, ANT, BLE }
    public enum SensorDeviceType { FEC = 17, CTRLS = 16, CPS = 11, HR = 120, CAD = 122, SPD, FPOD, BTS = 84, STZ = 19, KICKR = 96 }
    internal interface IDeviceInfo
    {
        bool Present { get; }
        ulong DeviceId { get; }
        DataSource DataSourceType { get; }
        SensorDeviceType SensorType { get; }
    }
    internal class FakeDevice : IDeviceInfo
    {
        public bool Present { get; set; }
        public ulong DeviceId { get; set; }
        public DataSource DataSourceType { get; set; }
        public SensorDeviceType SensorType { get; set; }
    }
    internal static class DeviceInfo
    {
        internal static Dictionary<DeviceFunctionType, IDeviceInfo> Devices = new Dictionary<DeviceFunctionType, IDeviceInfo>();
        internal static int Calls;
        internal static IDeviceInfo GetCurrentDeviceFor(DeviceFunctionType type)
        {
            Calls++;
            IDeviceInfo d;
            return Devices.TryGetValue(type, out d) ? d : null;
        }
    }
}

namespace GTBikeV.UI
{
    public class TestItem
    {
        public bool Enabled { get; set; }
        public event EventHandler Activated;
        public void Add(EventHandler h) { Activated += h; }
        protected internal void OnActivated(object sender) { if (Activated != null) Activated(sender, EventArgs.Empty); }
    }
    public static class Menus
    {
        public static int Activations;
        private static TestItem activateModMenuItem = new TestItem { Enabled = true };
        static Menus()
        {
            activateModMenuItem.Add(delegate { Activations++; });
            // Native toggle semantics: !IsDTOK -> initialize, else deinitialize.
            activateModMenuItem.Add(delegate
            {
                if (GTBikeV.DataTransceiver.IsDTOK) GTBikeV.DataTransceiver.SetStates("STOPPING", "STOPPING");
                else GTBikeV.DataTransceiver.SetStates("INITIALIZED", "INITIALIZED");
            });
        }
    }
}

public static class HarnessV1
{
    private static int checks;
    private static void Equal(object actual, object expected, string name)
    {
        checks++;
        if (!object.Equals(actual, expected)) throw new Exception(name + ": got " + (actual ?? "null") + ", expected " + (expected ?? "null"));
    }
    private static Dictionary<string, object> Dict(object o) { return (Dictionary<string, object>)o; }
    private static GtbikeV1Binding Bind(string version)
    {
        return new GtbikeV1Binding(delegate(string n) { return typeof(HarnessV1).Assembly.GetType(n, false); }, version);
    }

    public static int Main()
    {
        try
        {
            DateTime t0 = new DateTime(2026, 11, 4, 15, 0, 0, DateTimeKind.Utc);
            GtbikeV1Binding b = Bind("0.8.0.4");
            Equal(b.BindingStatus, "compatible", "all bindings resolve on 0.8.0.4");
            Equal(b.CanSetActive, true, "writes allowed on exact version");
            GtbikeV1Binding other = Bind("0.8.1.0");
            Equal(other.BindingStatus, "limited", "other version is read-only");
            Equal(other.CanSetActive, false, "no writes on other version");
            Equal(Dict(other.SetActive(true, "inControl"))["reasonCode"], "UNSUPPORTED_VERSION", "other version rejects writes");

            // Inactive mod: no live values, no sensor polling.
            GTBikeV.DataTransceiver.SetStates("UNINITIALIZED", "UNINITIALIZED");
            GTBikeV.DataTransceiver.Power_w = -1; GTBikeV.DataTransceiver.Speed_ms = -1f;
            Dictionary<string, object> s = b.Capture("inControl", t0);
            Equal(s["modLayer"], "inactive", "inactive layer");
            Equal(Dict(s["metrics"])["powerW"], null, "power null when receivers off");
            Equal(Dict(s["metrics"])["speedMps"], null, "speed -1 sentinel");
            Equal(s["sensors"], null, "no sensor reads while !IsDTOK");
            Equal(GTBikeV.Devices.DeviceInfo.Calls, 0, "GetCurrentDeviceFor not called while !IsDTOK");

            // Live ride.
            GTBikeV.DataTransceiver.SetStates("READY", "STARTED");
            GTBikeV.DataTransceiver.Power_w = 185; GTBikeV.DataTransceiver.Speed_ms = 8.5f; GTBikeV.DataTransceiver.Cadence_rpm = 88;
            GTBikeV.DataTransceiver.HeartRate = 0; GTBikeV.DataTransceiver.TotalDistance_m = 1050f; GTBikeV.DataTransceiver.RidingTime_ms = 180000;
            GTBikeV.DataTransceiver.Slope = 0.021f; GTBikeV.DataTransceiver.WindSpeed_kmh = 36f; GTBikeV.DataTransceiver.Sport = GTBikeV.DataTransceiver.SportType.SPORT_TYPE_CYCLING;
            GTBikeV.Activity act = new GTBikeV.Activity { TotalMovingTime = 176000, ElevationGain = 12.4f, ElevationLoss = -3.5f };
            GTBikeV.Network.Multiplayer.CurrentActivity = act;
            GTBikeV.Devices.DeviceInfo.Devices[GTBikeV.Devices.DeviceFunctionType.HeartRate] =
                new GTBikeV.Devices.FakeDevice { Present = true, DeviceId = ulong.MaxValue, DataSourceType = GTBikeV.Devices.DataSource.BLE, SensorType = GTBikeV.Devices.SensorDeviceType.HR };
            s = b.Capture("inControl", t0.AddSeconds(1));
            Dictionary<string, object> m = Dict(s["metrics"]);
            Equal(s["modLayer"], "dataReady", "ready layer");
            Equal(Dict(s["receivers"])["ble"], "started", "private ReceiverState read by name");
            Equal(m["powerW"], 185, "power");
            Equal(m["cadenceRpm"], 88, "cadence");
            Equal(m["heartRateBpm"], null, "HR 0 means no data, not 0 bpm");
            Equal(Math.Round((double)m["windSpeedMps"], 3), 10.0, "wind km/h -> m/s");
            Equal(s["sport"], "cycling", "sport by enum name");
            Dictionary<string, object> a = Dict(s["activity"]);
            Equal(a["movingTimeMs"], 176000L, "moving time");
            Equal(a["elevationLossM"], 3.5, "loss normalized to magnitude");
            Equal(a["workoutPresent"], false, "no workout");
            List<Dictionary<string, object>> sensors = (List<Dictionary<string, object>>)s["sensors"];
            Equal(sensors.Count, 7, "all roles reported");
            Equal(sensors[3]["deviceId"], "18446744073709551615", "ulong id as decimal string");
            Equal(sensors[0]["present"], false, "absent power sensor");
            long generation = (long)s["sessionGeneration"];

            // Sensors throttled to 1 Hz.
            int calls = GTBikeV.Devices.DeviceInfo.Calls;
            b.Capture("inControl", t0.AddSeconds(1.25));
            Equal(GTBikeV.Devices.DeviceInfo.Calls, calls, "sensor reads throttled");

            // New Activity instance -> new session generation; unfinished workout reported.
            GTBikeV.Network.Multiplayer.CurrentActivity = new GTBikeV.Activity { Workout = new GTBikeV.Workouts.Workout { Finished = false } };
            s = b.Capture("inControl", t0.AddSeconds(3));
            Equal((long)s["sessionGeneration"], generation + 1, "session generation advances");
            Equal(Dict(s["activity"])["workoutFinished"], false, "unfinished workout visible to guards");

            // modSetActiveGuard.
            int before = GTBikeV.UI.Menus.Activations;
            Equal(Dict(b.SetActive(true, "inControl"))["reasonCode"], "MOD_TRANSITION_IN_PROGRESS", "enable while BLE starting is rejected");
            Equal(Dict(b.SetActive(false, "paused"))["reasonCode"], "GAME_NOT_IN_CONTROL", "paused game rejected");
            GTBikeV.DataTransceiver.SetStates("READY", "STOPPED");
            Equal(Dict(b.SetActive(true, "inControl"))["status"], "noop", "already active is a no-op");
            Equal(GTBikeV.UI.Menus.Activations, before, "no-op does not touch the toggle");
            Equal(Dict(b.SetActive(false, "inControl"))["status"], "dispatched", "disable dispatches once");
            Equal(GTBikeV.UI.Menus.Activations, before + 1, "toggle invoked once");
            Equal(Dict(b.SetActive(false, "inControl"))["reasonCode"], "MOD_TRANSITION_IN_PROGRESS", "second disable during STOPPING rejected");
            Equal(GTBikeV.UI.Menus.Activations, before + 1, "no double toggle");
            GTBikeV.DataTransceiver.SetStates("STOPPED", "STOPPED");
            Equal(Dict(b.SetActive(false, "inControl"))["status"], "noop", "already inactive is a no-op");
            Equal(Dict(b.SetActive(true, "inControl"))["status"], "dispatched", "enable from stopped dispatches");

            // P18 rider progress: a handler compiled for GTBike V's own (non-visible) LoopEvent type receives the lap data.
            var course = new GTBikeV.Courses.Course();
            string gotName = null; bool gotLoop = true; uint gotLap = 99; object gotData = null;
            System.Reflection.EventInfo ev;
            Delegate handler = GtbikeV1Extensions.ListenLoopEvent(course, "OnCourseCompleted", delegate(string n, bool l, uint lap, object d) { gotName = n; gotLoop = l; gotLap = lap; gotData = d; }, out ev);
            Equal(handler != null, true, "course event handler compiled for the mod's delegate type");
            Equal(course.Listeners, 1, "handler added");
            course.Finish("Coastal Loop", false, 0, new GTBikeV.LapData());
            Equal(gotName, "Coastal Loop", "course name forwarded");
            Equal(gotLoop, false, "loop flag forwarded");
            Equal(gotLap, 0u, "lap number forwarded");
            Equal(gotData is GTBikeV.LapData, true, "lap data forwarded as object");
            ev.RemoveEventHandler(course, handler);
            Equal(course.Listeners, 0, "handler removed");
            Equal(GtbikeV1Extensions.ListenLoopEvent(course, "Nope", delegate(string n, bool l, uint lap, object d) { }, out ev) == null, true, "missing event is ignored");

            Console.WriteLine("PASS (" + checks + " checks): v1 bindings. Mock GTBikeV only, not an in-game test.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e.Message); return 1; }
    }
}
