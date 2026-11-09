// P18 add-ons: optional game modes next to GTBike V (Add-ons tab of the PC window; contract also for the PWA and
// Android). Commands arrive on the main adapter script (addons.setEnabled / addons.configure); the add-ons run on
// GtbikeV1AddonHost, a second script of this assembly with Interval 0, because collisions last a frame and markers
// and chasers move every frame. SHVDN runs the scripts of one domain one after another, never at once; the lock only
// documents the shared state. Starting an add-on cancels the previous course and stops any other add-on first.
// Missions use GTA natives. Course cancellation uses the native route selector via the main script; power and
// cadence come from its snapshot. GTBike V itself reads the game wind and weather (trainer resistance, grip).
// Add-ons: GtbikeV1Alleycat.cs, GtbikeV1SurvivalSprint.cs, GtbikeV1Ghost.cs, GtbikeV1Discover.cs. Parameter ids, ranges and defaults mirror
// bridge/api-v1/addons.mjs. C# 5 only (.NET Framework csc).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using GTA;
using GTA.Math;
using GTA.Native;

namespace CompanionBridge.V1
{
    /// <summary>Runs the add-ons every frame.</summary>
    public sealed class GtbikeV1AddonHost : Script
    {
        public GtbikeV1AddonHost()
        {
            Interval = 0;
            Tick += delegate {
                GtbikeV1KeyboardHost.Shared.Watchdog();
                GtbikeV1Addons.Shared.Frame();
                try { GtbikeV1CameraNative.Shared.Frame(DateTime.UtcNow); }
                catch (Exception) { GtbikeV1CameraNative.Shared.Abort(); }
            };
            Aborted += delegate { GtbikeV1CameraNative.Shared.Abort(); };
            Aborted += delegate { GtbikeV1KeyboardHost.Shared.Abort(); };
            Aborted += delegate { GtbikeV1Addons.Shared.Shutdown(); }; // Script reload: undo weather, wind, blips, vehicles.
        }
    }

    /// <summary>One typed add-on parameter (mirror of a descriptor in addons.mjs).</summary>
    public sealed class GtbikeV1AddonParam
    {
        public string Id;
        public string Type; // integer | number | boolean | enum
        public double Min, Max, Step;
        public string[] Options;
        public object Default;
        public bool Live;

        public static GtbikeV1AddonParam Num(string id, string type, double min, double max, double step, object def, bool live)
        {
            return new GtbikeV1AddonParam { Id = id, Type = type, Min = min, Max = max, Step = step, Default = def, Live = live };
        }

        public static GtbikeV1AddonParam Flag(string id, bool def, bool live)
        {
            return new GtbikeV1AddonParam { Id = id, Type = "boolean", Default = def, Live = live };
        }

        public static GtbikeV1AddonParam Choice(string id, string[] options, string def, bool live)
        {
            return new GtbikeV1AddonParam { Id = id, Type = "enum", Options = options, Default = def, Live = live };
        }

        /// <summary>null when acceptable (normalized is the stored value), else a reason code.</summary>
        public string Check(object value, out object normalized)
        {
            normalized = null;
            if (Type == "boolean")
            {
                if (!(value is bool)) return "INVALID_REQUEST";
                normalized = value;
                return null;
            }
            if (Type == "enum")
            {
                string text = value as string;
                if (text == null) return "INVALID_REQUEST";
                if (Array.IndexOf(Options, text) < 0) return "VALUE_NOT_ALLOWED";
                normalized = text;
                return null;
            }
            if (!(value is int || value is long || value is decimal || value is double || value is float)) return "INVALID_REQUEST";
            double v = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(v) || double.IsInfinity(v)) return "INVALID_REQUEST";
            if (Type == "integer" && v != Math.Floor(v)) return "INVALID_REQUEST";
            if (v < Min || v > Max) return "VALUE_OUT_OF_RANGE";
            double steps = (v - Min) / Step;
            if (Math.Abs(steps - Math.Round(steps)) > 1e-6) return "VALUE_NOT_ALLOWED";
            normalized = Type == "integer" ? (object)(int)v : (object)v;
            return null;
        }
    }

    /// <summary>What GTBike V and the game report, refreshed by the main script every Tick (250 ms).</summary>
    public sealed class GtbikeV1AddonContext
    {
        public bool ModActive;
        public bool CourseLoaded;
        public string GameControl = "unknown";
        public double? PowerW;
        public double? CadenceRpm;
    }

    /// <summary>Shared parameter handling and lifecycle of one add-on.</summary>
    public abstract class GtbikeV1Addon
    {
        protected readonly Dictionary<string, object> values = new Dictionary<string, object>();
        protected string state = "idle";
        protected string requestId;
        protected string reasonCode;
        protected Dictionary<string, object> result;

        public abstract string Id { get; }
        /// <summary>false: a GTBike V course refuses the start and ends a run (courseLoaded).</summary>
        public abstract bool AllowedDuringCourse { get; }
        protected abstract GtbikeV1AddonParam[] Parameters { get; }

        /// <summary>Call from the derived constructor: every parameter at its default.</summary>
        protected void Init()
        {
            foreach (GtbikeV1AddonParam p in Parameters) values[p.Id] = p.Default;
        }

        public bool Active { get { return state == "preparing" || state == "running"; } }

        public void Restore(Dictionary<string, object> saved)
        {
            foreach (GtbikeV1AddonParam p in Parameters)
            {
                object value, normalized;
                if (saved.TryGetValue(p.Id, out value) && p.Check(value, out normalized) == null) values[p.Id] = normalized;
            }
        }

        public Dictionary<string, object> Values() { return new Dictionary<string, object>(values); }

        /// <summary>Applies all values or none. Returns whether anything changed; reason is set when refused.</summary>
        public bool Configure(Dictionary<string, object> changes, out string reason)
        {
            reason = null;
            Dictionary<string, object> accepted = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> change in changes)
            {
                GtbikeV1AddonParam p = Array.Find(Parameters, x => x.Id == change.Key);
                if (p == null) { reason = "INVALID_REQUEST"; return false; }
                object normalized;
                reason = p.Check(change.Value, out normalized);
                if (reason != null) return false;
                if (Active && !p.Live) { reason = "VALUE_NOT_ALLOWED"; return false; }
                accepted[p.Id] = normalized;
            }
            bool changed = false;
            foreach (KeyValuePair<string, object> a in accepted)
            {
                if (object.Equals(values[a.Key], a.Value)) continue;
                values[a.Key] = a.Value;
                changed = true;
            }
            return changed;
        }

        /// <summary>Stops a running add-on. false when nothing was running.</summary>
        public bool Stop()
        {
            if (!Active) return false;
            End("stopped");
            return true;
        }

        public abstract void Start(string id);
        public abstract void End(string outcome);
        /// <summary>Every frame, also while inactive (to finish restoring what a run changed).</summary>
        public abstract void Frame(string gameControl, GtbikeV1AddonContext context);
        public abstract Dictionary<string, object> Report();
        /// <summary>Script reload: undo every override at once.</summary>
        public abstract void Shutdown();

        /// <summary>Whether the PC teleport must wait (it would skip the run). Default: while active.</summary>
        public virtual bool BlocksTeleport { get { return Active; } }

        /// <summary>Loads the add-on's own files from the adapter directory (besides the shared parameters).</summary>
        public virtual void Load(string directory) { }

        /// <summary>Add-on specific item commands (addons.saveSegment / removeSegment, addons.savePlace / removePlace); null when not supported.</summary>
        public virtual Dictionary<string, object> ItemCommand(string type, Dictionary<string, object> args) { return null; }

        /// <summary>Adds the add-on's changed files (path relative to the adapter directory; null = delete).</summary>
        public virtual void TakeFiles(Dictionary<string, object> files) { }

        protected static Vector3 RiderPosition()
        {
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists()) return Vector3.Zero;
            return ped.IsInVehicle() && ped.CurrentVehicle != null ? ped.CurrentVehicle.Position : ped.Position;
        }

        protected static double Distance2D(Vector3 a, Vector3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        protected static void Notify(string text)
        {
#pragma warning disable 618 // PostTicker exists only in SHVDN 3.9+; Show works in every ScriptHookVDotNet3 build.
            try { GTA.UI.Notification.Show(text); } catch (Exception) { }
#pragma warning restore 618
        }

        protected static void Sound(string name, string set)
        {
            try { Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, name, set, false); } catch (Exception) { }
        }
    }

    public sealed class GtbikeV1Addons
    {
        public static readonly GtbikeV1Addons Shared = new GtbikeV1Addons();

        /// <summary>Set by the main script: writes to adapter.log.</summary>
        public Action<Exception> Error;

        private readonly object gate = new object();
        private readonly GtbikeV1Addon[] addons = { new GtbikeV1Alleycat(), new GtbikeV1SurvivalSprint(), new GtbikeV1Ghost(), new GtbikeV1Discover() };
        private readonly GtbikeV1AddonContext context = new GtbikeV1AddonContext();
        private bool settingsDirty;
        private bool? onBicycle;
        private readonly Stopwatch modOffFor = new Stopwatch();

        /// <summary>Loads the saved parameters (v1\addons-settings.json); anything invalid falls back to the default.</summary>
        public void Load(string directory)
        {
            GtbikeV1Progress.Shared.Load(directory); // XP from Discover finds and completed routes.
            try
            {
                string path = Path.Combine(directory, "addons-settings.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) { lock (gate) foreach (GtbikeV1Addon addon in addons) addon.Load(directory); return; }
                Dictionary<string, object> saved = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                lock (gate) foreach (GtbikeV1Addon addon in addons) addon.Load(directory);
                if (saved == null) return;
                lock (gate)
                {
                    foreach (GtbikeV1Addon addon in addons)
                    {
                        Dictionary<string, object> stored = Get(saved, addon.Id) as Dictionary<string, object>;
                        if (stored != null) addon.Restore(stored);
                    }
                }
            }
            catch (Exception e) { Report(e); }
        }

        /// <summary>Main script, every Tick: what the add-ons need to know about GTBike V and the game.</summary>
        public void Context(bool modActive, bool courseLoaded, string control, double? powerW, double? cadenceRpm)
        {
            lock (gate)
            {
                context.ModActive = modActive;
                context.CourseLoaded = courseLoaded;
                context.GameControl = control;
                context.PowerW = powerW;
                context.CadenceRpm = cadenceRpm;
                onBicycle = OnBicycle();
                if (modActive) modOffFor.Reset(); else if (!modOffFor.IsRunning) modOffFor.Start();
            }
        }

        public bool BlocksTeleport
        {
            get
            {
                lock (gate)
                {
                    foreach (GtbikeV1Addon addon in addons) if (addon.BlocksTeleport) return true;
                    return false;
                }
            }
        }

        public Dictionary<string, object> Command(string type, string requestId, Dictionary<string, object> args, Func<string> cancelCourse)
        {
            lock (gate)
            {
                string id = Get(args, "addonId") as string;
                if (args == null || id == null) return Ack("rejected", "INVALID_REQUEST");
                GtbikeV1Addon addon = Array.Find(addons, a => a.Id == id);
                if (addon == null) return Ack("rejected", "NOT_FOUND");
                if (type == "addons.setEnabled")
                {
                    if (args.Count != 2 || !(Get(args, "enabled") is bool)) return Ack("rejected", "INVALID_REQUEST");
                    if (!(bool)Get(args, "enabled")) return addon.Stop() ? Ack("dispatched", null) : Ack("noop", null);
                    if (addon.Active) return Ack("noop", null);
                    if (!context.ModActive) return Ack("rejected", "MOD_INACTIVE");
                    if (context.GameControl != "inControl") return Ack("rejected", "GAME_NOT_IN_CONTROL");
                    if (onBicycle != true) return Ack("rejected", "RIDER_NOT_ON_BIKE");
                    // Validate first so a refused start leaves the current mission alone. The callback reads the
                    // live course, clears it through the mod's own handler and verifies it really disappeared.
                    if (cancelCourse == null) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
                    string reason = cancelCourse();
                    if (reason != null) return Ack("rejected", reason);
                    context.CourseLoaded = false;
                    foreach (GtbikeV1Addon previous in addons)
                    {
                        if (previous == addon) continue;
                        previous.Stop(); // End removes its GPS route, markers, ghost or chaser before Start.
                        previous.Shutdown(); // Also remove delayed cleanup left by an already finished run.
                    }
                    addon.Start(requestId);
                    return Ack("dispatched", null);
                }
                if (type == "addons.saveSegment" || type == "addons.removeSegment" || type == "addons.savePlace" || type == "addons.removePlace")
                {
                    Dictionary<string, object> ack = addon.ItemCommand(type, args);
                    return ack ?? Ack("rejected", "CAPABILITY_UNAVAILABLE");
                }
                if (type == "addons.configure")
                {
                    Dictionary<string, object> changes = Get(args, "params") as Dictionary<string, object>;
                    if (args.Count != 2 || changes == null || changes.Count == 0) return Ack("rejected", "INVALID_REQUEST");
                    string reason;
                    bool changed = addon.Configure(changes, out reason);
                    if (reason != null) return Ack("rejected", reason);
                    if (!changed) return Ack("noop", null);
                    settingsDirty = true;
                    return Ack("dispatched", null);
                }
                return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            }
        }

        /// <summary>Snapshot "addons": per add-on state, requestId, params, run and result.</summary>
        public Dictionary<string, object> Report()
        {
            lock (gate)
            {
                Dictionary<string, object> report = new Dictionary<string, object>();
                foreach (GtbikeV1Addon addon in addons)
                {
                    Dictionary<string, object> one = addon.Report();
                    one["blocksTeleport"] = addon.BlocksTeleport;
                    report[addon.Id] = one;
                }
                return report;
            }
        }

        /// <summary>Snapshot "addonContext": what the bridge needs for the start precheck.</summary>
        public Dictionary<string, object> ContextReport()
        {
            lock (gate) return new Dictionary<string, object> { { "onBicycle", onBicycle.HasValue ? (object)onBicycle.Value : null } };
        }

        /// <summary>
        /// Files to write (path relative to the adapter directory -> content; null content = delete), or null when nothing
        /// changed: the shared parameters and each add-on's own files. The main script writes them off the game thread.
        /// </summary>
        public Dictionary<string, object> TakeFiles()
        {
            lock (gate)
            {
                Dictionary<string, object> files = new Dictionary<string, object>();
                if (settingsDirty)
                {
                    settingsDirty = false;
                    Dictionary<string, object> all = new Dictionary<string, object>();
                    foreach (GtbikeV1Addon addon in addons) all[addon.Id] = addon.Values();
                    files["addons-settings.json"] = all;
                }
                foreach (GtbikeV1Addon addon in addons) addon.TakeFiles(files);
                GtbikeV1Progress.Shared.TakeFile(files);
                return files.Count == 0 ? null : files;
            }
        }

        public void Frame()
        {
            lock (gate)
            {
                foreach (GtbikeV1Addon addon in addons)
                {
                    try
                    {
                        string reason = null;
                        if (addon.Active)
                        {
                            if (context.CourseLoaded && !addon.AllowedDuringCourse) reason = "courseLoaded";
                            else if (!context.ModActive && modOffFor.ElapsedMilliseconds > 3000) reason = "modInactive";
                        }
                        if (reason != null) addon.End(reason);
                        else addon.Frame(context.GameControl, context);
                    }
                    catch (Exception e)
                    {
                        Report(e);
                        try { if (addon.Active) addon.End("internal"); } catch (Exception) { }
                    }
                }
            }
        }

        public void Shutdown()
        {
            lock (gate)
            {
                foreach (GtbikeV1Addon addon in addons)
                {
                    try { addon.Shutdown(); } catch (Exception e) { Report(e); }
                }
            }
        }

        private void Report(Exception e)
        {
            Action<Exception> sink = Error;
            if (sink != null) sink(e);
        }

        private static bool? OnBicycle()
        {
            try
            {
                Ped ped = Game.Player.Character;
                if (ped == null || !ped.Exists()) return null;
                Vehicle vehicle = ped.IsInVehicle() ? ped.CurrentVehicle : null;
                return vehicle != null && vehicle.Exists() && Function.Call<bool>(Hash.IS_THIS_MODEL_A_BICYCLE, vehicle.Model.Hash);
            }
            catch (Exception) { return null; }
        }

        internal static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        private static Dictionary<string, object> Ack(string status, string reasonCode)
        {
            Dictionary<string, object> ack = new Dictionary<string, object>();
            ack["status"] = status;
            if (reasonCode != null) ack["reasonCode"] = reasonCode;
            return ack;
        }
    }
}
