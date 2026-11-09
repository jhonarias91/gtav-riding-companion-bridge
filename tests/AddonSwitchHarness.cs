// Production GtbikeV1Addons lifecycle with fake game/add-on surfaces, not an in-game test.
using System;
using System.Collections.Generic;
using CompanionBridge.V1;

namespace GTA
{
    public class Script { public int Interval; public event EventHandler Tick; public event EventHandler Aborted; }
    public class Ped
    {
        public bool Exists() { return true; }
        public bool IsInVehicle() { return true; }
        public Vehicle CurrentVehicle = new Vehicle();
        public GTA.Math.Vector3 Position;
    }
    public class Vehicle { public bool Exists() { return true; } public GTA.Math.Vector3 Position; public Model Model = new Model(); }
    public class Model { public int Hash; }
    public class Player { public Ped Character = new Ped(); }
    public static class Game { public static Player Player = new Player(); }
}
namespace GTA.Math
{
    public struct Vector3 { public float X, Y, Z; public static Vector3 Zero { get { return new Vector3(); } } }
}
namespace GTA.Native
{
    public enum Hash { PLAY_SOUND_FRONTEND, IS_THIS_MODEL_A_BICYCLE }
    public static class Function
    {
        public static bool Bicycle = true;
        public static void Call(Hash hash, params object[] args) { }
        public static T Call<T>(Hash hash, params object[] args) { return (T)(object)Bicycle; }
    }
}
namespace GTA.UI { public static class Notification { public static void Show(string message) { } } }

namespace CompanionBridge.V1
{
    public class GtbikeV1KeyboardHost
    {
        public static readonly GtbikeV1KeyboardHost Shared = new GtbikeV1KeyboardHost();
        public void Watchdog() { } public void Abort() { }
    }
    public class GtbikeV1CameraNative
    {
        public static readonly GtbikeV1CameraNative Shared = new GtbikeV1CameraNative();
        public void Frame(DateTime now) { } public void Abort() { }
    }
    public class GtbikeV1Progress
    {
        public static readonly GtbikeV1Progress Shared = new GtbikeV1Progress();
        public void Load(string directory) { } public void TakeFile(Dictionary<string, object> files) { }
    }

    public abstract class FakeAddon : GtbikeV1Addon
    {
        public static readonly List<FakeAddon> All = new List<FakeAddon>();
        public static readonly List<string> Events = new List<string>();
        public static bool CourseLoaded;
        public bool Guidance;
        protected FakeAddon() { All.Add(this); }
        public override bool AllowedDuringCourse { get { return false; } }
        protected override GtbikeV1AddonParam[] Parameters { get { return new GtbikeV1AddonParam[0]; } }
        public override void Start(string id)
        {
            if (CourseLoaded || All.Exists(a => a != this && a.Guidance)) throw new Exception("The previous route still exists at Start");
            state = "preparing"; requestId = id; Guidance = true; result = null;
            Events.Add("start:" + Id);
        }
        public void BeginRunning() { state = "running"; }
        public override void End(string outcome)
        {
            state = "aborted"; Guidance = false;
            result = new Dictionary<string, object> { { "outcome", outcome } };
            Events.Add("stop:" + Id);
        }
        public override void Shutdown() { Guidance = false; if (Active) state = "aborted"; Events.Add("cleanup:" + Id); }
        public override void Frame(string gameControl, GtbikeV1AddonContext context) { }
        public override Dictionary<string, object> Report()
        {
            return new Dictionary<string, object> { { "state", state }, { "requestId", requestId }, { "result", result } };
        }
    }
    public sealed class GtbikeV1Alleycat : FakeAddon { public override string Id { get { return "alleycat"; } } }
    public sealed class GtbikeV1SurvivalSprint : FakeAddon { public override string Id { get { return "survivalSprint"; } } }
    public sealed class GtbikeV1Ghost : FakeAddon { public override string Id { get { return "ghost"; } } }
    public sealed class GtbikeV1Discover : FakeAddon { public override string Id { get { return "discover"; } } }
}

public static class AddonSwitchHarness
{
    private static int checks, cancellations;
    private static void Equal(object actual, object expected, string label)
    {
        checks++;
        if (!object.Equals(actual, expected)) throw new Exception(label + ": " + actual + " != " + expected);
    }
    private static string CancelCourse() { cancellations++; FakeAddon.Events.Add("cancelCourse"); FakeAddon.CourseLoaded = false; return null; }
    private static Dictionary<string, object> Switch(GtbikeV1Addons host, string id, Func<string> cancel)
    {
        return host.Command("addons.setEnabled", "request-" + id, new Dictionary<string, object> { { "addonId", id }, { "enabled", true } }, cancel);
    }
    public static int Main()
    {
        try
        {
            var host = GtbikeV1Addons.Shared;
            host.Context(true, true, "inControl", 200, 80);
            FakeAddon.CourseLoaded = true;
            Equal(Switch(host, "alleycat", CancelCourse)["status"], "dispatched", "course to add-on");
            Equal(FakeAddon.CourseLoaded, false, "old course cancelled");
            Equal(cancellations, 1, "single cancellation");
            host.Frame();
            Equal(FakeAddon.All.Find(a => a.Id == "alleycat").Active, true, "cleared course does not abort the new run on its next frame");

            foreach (bool running in new[] { false, true })
            {
                string oldId = running ? "discover" : "alleycat";
                string nextId = running ? "ghost" : "discover";
                if (running) FakeAddon.All.Find(a => a.Id == oldId).BeginRunning();
                FakeAddon.Events.Clear();
                Equal(Switch(host, nextId, CancelCourse)["status"], "dispatched", "replace preparing/running mission");
                var old = FakeAddon.All.Find(a => a.Id == oldId);
                Equal(old.Active, false, "old mission stopped");
                Equal(old.Guidance, false, "old guidance removed");
                Equal(((Dictionary<string, object>)old.Report()["result"])["outcome"], "stopped", "old result reports cancellation");
                Equal(FakeAddon.Events.IndexOf("stop:" + oldId) < FakeAddon.Events.IndexOf("start:" + nextId), true, "stop before start");
                Equal(FakeAddon.All.FindAll(a => a.Active).Count, 1, "only one mission active");
            }

            int before = cancellations;
            Equal(Switch(host, "ghost", CancelCourse)["status"], "noop", "same add-on is not restarted");
            Equal(cancellations, before, "no cancellation on a no-op");

            host.Context(true, false, "paused", 200, 80);
            Equal(Switch(host, "alleycat", CancelCourse)["reasonCode"], "GAME_NOT_IN_CONTROL", "paused start refused");
            Equal(cancellations, before, "paused start preserves current activity");
            GTA.Native.Function.Bicycle = false;
            host.Context(true, false, "inControl", 200, 80);
            Equal(Switch(host, "alleycat", CancelCourse)["reasonCode"], "RIDER_NOT_ON_BIKE", "not on bike refused");
            Equal(cancellations, before, "failed validation never cancels current activity");
            GTA.Native.Function.Bicycle = true;
            host.Context(true, false, "inControl", 200, 80);
            Equal(Switch(host, "alleycat", () => "CAPABILITY_UNAVAILABLE")["reasonCode"], "CAPABILITY_UNAVAILABLE", "failed cancellation stops transition");
            Equal(Switch(host, "alleycat", null)["reasonCode"], "CAPABILITY_UNAVAILABLE", "cancellation support required");
            Equal(FakeAddon.All.Find(a => a.Id == "ghost").Active, true, "old mission survives refused transition");
            Equal(FakeAddon.All.Find(a => a.Id == "alleycat").Active, false, "new mission never starts after refusal");

            // A finished sprint can still have a delayed chaser to remove before another mission starts.
            FakeAddon.All.Find(a => a.Id == "survivalSprint").Guidance = true;
            Equal(Switch(host, "alleycat", CancelCourse)["status"], "dispatched", "finished run cleaned before switch");
            Console.WriteLine("Add-on switch harness passed: " + checks + " checks.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
