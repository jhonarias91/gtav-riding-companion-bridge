// GTAV Riding Companion adapter v1 - ScriptHookVDotNet 3 script.
// Separate from the legacy adapter (GTBikeVCompanion.dll): own directory, own command queue, no ERG paths.
// All mod reads and the single invocation run on this script's Tick. Disk writes run on a worker task.
// C# 5 only (.NET Framework csc).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using GTA;
using GTA.Math;
using GTA.Native;

namespace CompanionBridge.V1
{
    public sealed class GTBikeVCompanionV1 : Script
    {
        private const int MaxCommandBytes = 4096;
        private readonly string instanceId = "adapter-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTBikeVCompanion", "v1");
        private readonly string commandsDirectory;
        private readonly object logLock = new object();
        private GtbikeV1Binding binding;
        private GtbikeV1Extensions extensions;
        private GtbikeV1More more;
        private GtbikeV1World world;
        private Dictionary<string, object> lastSnapshot;
        private DateTime nextLookupUtc = DateTime.MinValue;
        private long sequence;
        private int writing;

        public GTBikeVCompanionV1()
        {
            commandsDirectory = Path.Combine(directory, "commands");
            Directory.CreateDirectory(commandsDirectory);
            Interval = 250;
            Tick += OnTick;
        }

        private void OnTick(object sender, EventArgs args)
        {
            if (Interlocked.CompareExchange(ref writing, 1, 0) != 0) return; // Previous snapshot still being written.
            Dictionary<string, object> snapshot;
            string gameControl = GameControl();
            DateTime now = DateTime.UtcNow;
            try
            {
                FindMod(now);
                ProcessOneCommand(gameControl, now);
                snapshot = binding == null ? NotLoaded() : binding.Capture(gameControl, now);
                if (binding != null && extensions != null && (string)snapshot["binding"] != "incompatible")
                {
                    extensions.Capture(snapshot, now);
                    List<string> commands = (List<string>)snapshot["commands"];
                    commands.AddRange(extensions.Commands());
                    snapshot["extensionIssues"] = extensions.Missing.Count > 10 ? new List<string>(extensions.Missing).GetRange(0, 10) : new List<string>(extensions.Missing);
                }
                if (binding != null && more != null && (string)snapshot["binding"] != "incompatible")
                {
                    more.Capture(snapshot, now, object.Equals(Get(snapshot, "isDtOk"), true));
                    ((List<string>)snapshot["commands"]).AddRange(more.Commands());
                    snapshot["moreIssues"] = more.Missing.Count > 10 ? new List<string>(more.Missing).GetRange(0, 10) : new List<string>(more.Missing);
                }
                if (binding != null && binding.CanSetActive && world != null)
                    world.Capture(snapshot, now, () => ReadPlayerPosition(gameControl), value => MapPoint((Vector3)value));
            }
            catch (Exception e)
            {
                Log(e);
                binding = null; // Re-resolve on the next lookup instead of trusting a half-broken binding.
                extensions = null;
                more = null;
                world = null;
                snapshot = NotLoaded();
                snapshot["adapterError"] = e.GetBaseException().GetType().Name;
            }
            snapshot["adapterInstanceId"] = instanceId;
            snapshot["sequence"] = ++sequence;
            snapshot["capturedAtUtc"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            snapshot["gameControl"] = gameControl;
            lastSnapshot = snapshot;
            Dictionary<string, object> toWrite = snapshot;
            Dictionary<string, object> catalog = extensions == null ? null : extensions.TakeCatalog();
            Dictionary<string, object> workouts = more == null ? null : more.TakeWorkouts();
            Dictionary<string, object> paths = more == null ? null : more.TakePaths();
            Task.Run(delegate
            {
                try
                {
                    if (catalog != null) AtomicWrite(Path.Combine(directory, "catalog.json"), catalog); // Before the snapshot that quotes its revision.
                    if (workouts != null) AtomicWrite(Path.Combine(directory, "workouts.json"), workouts);
                    if (paths != null) AtomicWrite(Path.Combine(directory, "paths.json"), paths);
                    AtomicWrite(Path.Combine(directory, "telemetry.json"), toWrite);
                }
                catch (Exception e) { Log(e); }
                finally { Interlocked.Exchange(ref writing, 0); }
            });
        }

        private void FindMod(DateTime now)
        {
            if (binding != null || now < nextLookupUtc) return;
            nextLookupUtc = now.AddSeconds(2);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetType("GTBikeV.DataTransceiver", false) == null) continue;
                Assembly mod = assembly;
                string version = mod.GetName().Version.ToString();
                binding = new GtbikeV1Binding(delegate(string name) { return mod.GetType(name, false); }, version);
                extensions = new GtbikeV1Extensions(delegate(string name) { return mod.GetType(name, false); }, version == GtbikeV1Binding.SupportedVersion);
                extensions.DeferredError = Log; // Work run on the mod's own Tick (bike/character change).
                more = new GtbikeV1More(delegate(string name) { return mod.GetType(name, false); }, version == GtbikeV1Binding.SupportedVersion);
                more.DeferredError = Log; // Workout selection runs on the mod's own Tick.
                if (version == GtbikeV1Binding.SupportedVersion)
                {
                    try { world = new GtbikeV1World(name => mod.GetType(name, false), typeof(Vector3)); }
                    catch (Exception) { world = null; } // Optional bindings must never hide the core telemetry.
                }
                return;
            }
        }

        private static string GameControl()
        {
            try
            {
                // Game.IsLoading is obsolete in SHVDN3; the native it wraps still reports in-game loading screens.
                if (Function.Call<bool>(Hash.GET_IS_LOADING_SCREEN_ACTIVE)) return "loading";
                if (Game.IsPaused) return "paused";
                Player player = Game.Player;
                if (player == null || !player.IsPlaying || !player.CanControlCharacter || Game.IsCutsceneActive) return "notInControl";
                return "inControl";
            }
            catch (Exception) { return "unknown"; }
        }

        private static object ReadPlayerPosition(string gameControl)
        {
            if (gameControl != "inControl") return null;
            Ped character = Game.Player.Character;
            if (character == null || !character.Exists()) return null;
            Entity body = character.IsInVehicle() && character.CurrentVehicle != null ? (Entity)character.CurrentVehicle : character;
            Vector3 position = body.Position;
            Dictionary<string, object> point = position == Vector3.Zero ? null : MapPoint(position);
            if (point == null) return null;
            point["z"] = position.Z; // Extra fields for the PC window map; phones only read x/y.
            point["heading"] = body.Heading;
            return point;
        }

        private static Dictionary<string, object> MapPoint(Vector3 position)
        {
            if (float.IsNaN(position.X) || float.IsInfinity(position.X) || float.IsNaN(position.Y) || float.IsInfinity(position.Y)) return null;
            return new Dictionary<string, object> { { "x", position.X }, { "y", position.Y } };
        }

        private static Dictionary<string, object> NotLoaded()
        {
            Dictionary<string, object> s = new Dictionary<string, object>();
            s["schema"] = "gtbikev-adapter-v1";
            s["modLoaded"] = false;
            s["modVersion"] = null;
            s["binding"] = "unknown";
            s["bindingProfile"] = null;
            s["commands"] = new List<string>();
            return s;
        }

        /// <summary>One command per Tick. The bridge sets expiresAtUtc; stale or foreign commands are rejected, never run.</summary>
        private void ProcessOneCommand(string gameControl, DateTime now)
        {
            foreach (string path in Directory.GetFiles(commandsDirectory, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                Guid parsed;
                if (!Guid.TryParse(id, out parsed)) continue; // Skips acks (*.ack.json) and temp files.
                string claimed = path + ".processing";
                try { File.Move(path, claimed); } catch (IOException) { continue; }
                Dictionary<string, object> ack;
                try { ack = Execute(claimed, id, gameControl, now); }
                catch (Exception e)
                {
                    Log(e);
                    ack = new Dictionary<string, object>();
                    ack["status"] = "rejected";
                    ack["reasonCode"] = "INTERNAL";
                }
                ack["requestId"] = id;
                ack["adapterInstanceId"] = instanceId;
                ack["observedSequence"] = sequence;
                ack["handledAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                try { AtomicWrite(Path.Combine(commandsDirectory, id + ".ack.json"), ack); }
                finally { File.Delete(claimed); }
                return;
            }
        }

        private Dictionary<string, object> Execute(string claimed, string id, string gameControl, DateTime now)
        {
            if (new FileInfo(claimed).Length > MaxCommandBytes) return Rejected("INVALID_REQUEST");
            Dictionary<string, object> command = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(claimed, Encoding.UTF8));
            object value;
            if (!command.TryGetValue("requestId", out value) || !id.Equals(value as string, StringComparison.OrdinalIgnoreCase)) return Rejected("INVALID_REQUEST");
            if (!command.TryGetValue("adapterInstanceId", out value) || (value as string) != instanceId) return Rejected("INSTANCE_CHANGED");
            if (!command.TryGetValue("expiresAtUtc", out value) || !(value is string)) return Rejected("INVALID_REQUEST");
            DateTime expires = DateTime.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            if (expires < now || expires > now.AddSeconds(5)) return Rejected("OPERATION_UNKNOWN");
            if (binding == null) return Rejected("ADAPTER_ABSENT");
            string type = command.TryGetValue("type", out value) ? value as string : null;
            Dictionary<string, object> args = command.TryGetValue("args", out value) ? value as Dictionary<string, object> : null;
            if (type == null || args == null) return Rejected("INVALID_REQUEST");
            if (type == "mod.setActive")
            {
                if (args.Count != 1 || !args.TryGetValue("enabled", out value) || !(value is bool)) return Rejected("INVALID_REQUEST");
                return binding.SetActive((bool)value, gameControl);
            }
            // P3/P4: the bridge already validated arguments and revisions; the adapter re-checks what only the game knows.
            if (extensions == null || lastSnapshot == null) return Rejected("CAPABILITY_UNAVAILABLE");
            bool dtOk = object.Equals(Get(lastSnapshot, "isDtOk"), true);
            Dictionary<string, object> activity = Get(lastSnapshot, "activity") as Dictionary<string, object>;
            bool workoutBlocks = activity != null && object.Equals(Get(activity, "workoutPresent"), true) && !object.Equals(Get(activity, "workoutFinished"), true);
            Dictionary<string, object> metrics = Get(lastSnapshot, "metrics") as Dictionary<string, object>;
            object speed = metrics == null ? null : Get(metrics, "speedMps");
            double? speedMps = speed == null ? (double?)null : Convert.ToDouble(speed, CultureInfo.InvariantCulture);
            if (type == "routes.apply")
            {
                if (args.Count != 3 || !args.ContainsKey("routeId") || !(Get(args, "loop") is bool) || !(Get(args, "reverse") is bool)) return Rejected("INVALID_REQUEST");
                return extensions.ApplyRoute(args, null, dtOk, gameControl);
            }
            if (type == "routes.preview")
            {
                if (args.Count != 1 || !args.ContainsKey("routeId")) return Rejected("INVALID_REQUEST");
                return extensions.PreviewRoute(args, null);
            }
            if (type == "ui.openPanel" || type == "ui.closePanel")
            {
                if (args.Count != 1 || (Get(args, "panel") as string) != "routes") return Rejected("CAPABILITY_UNAVAILABLE");
                return extensions.Panel(type == "ui.openPanel", gameControl);
            }
            if (type == "profile.update") return extensions.UpdateProfile(args, workoutBlocks);
            if (type == "rideSettings.update") return extensions.UpdateRide(args);
            if (type == "appearance.select") return extensions.SelectAppearance(args, speedMps, gameControl);
            if (more == null) return Rejected("CAPABILITY_UNAVAILABLE");
            if (type == "devices.configure") return more.ConfigureDevice(args, dtOk);
            if (type == "devices.assign") return more.AssignDevice(args, dtOk);
            if (type == "devices.disconnect") return more.DisconnectDevice(args, dtOk);
            if (type == "workouts.select") return more.StartWorkout(args, dtOk, workoutBlocks, gameControl);
            if (type == "activities.finish")
            {
                if (args.Count != 0) return Rejected("INVALID_REQUEST");
                return more.Finish(command.TryGetValue("expectedRevision", out value) ? value as string : null, dtOk, workoutBlocks, speedMps, gameControl);
            }
            return Rejected("CAPABILITY_UNAVAILABLE");
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        private static Dictionary<string, object> Rejected(string reasonCode)
        {
            Dictionary<string, object> ack = new Dictionary<string, object>();
            ack["status"] = "rejected";
            ack["reasonCode"] = reasonCode;
            return ack;
        }

        private static void AtomicWrite(string path, object value)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        /// <summary>
        /// Exception type, message and the first stack frames as method names only (" in file:line" removed); no tokens,
        /// paths or arguments. A type and message alone could not say where a NullReferenceException came from.
        /// </summary>
        private void Log(Exception e)
        {
            try
            {
                Exception root = e.GetBaseException();
                StringBuilder line = new StringBuilder();
                line.Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append(' ').Append(root.GetType().Name).Append(": ").Append(root.Message);
                string[] frames = (root.StackTrace ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int n = 0; n < frames.Length && n < 8; n++)
                {
                    string frame = frames[n].Trim();
                    int file = frame.IndexOf(" in ", StringComparison.Ordinal);
                    line.Append(Environment.NewLine).Append("    ").Append(file > 0 ? frame.Substring(0, file) : frame);
                }
                lock (logLock) // The mod's Tick (DeferredError) and this script's Tick can both log.
                {
                    string path = Path.Combine(directory, "adapter.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.WriteAllText(path, "");
                    File.AppendAllText(path, line.Append(Environment.NewLine).ToString());
                }
            }
            catch (Exception) { }
        }
    }
}
