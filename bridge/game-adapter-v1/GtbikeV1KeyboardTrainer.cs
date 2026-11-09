// Opt-in local keyboard trainer for GTBikeV 0.8.0.4. C# 5; no remote key injection.
// Run Start/Step/Stop on GTBikeVScript's own Tick. This is synthetic test input, not a physical sensor.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1KeyboardTrainer
    {
        private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Type dt, vt, timer, functionType;
        private readonly Func<string, Type> typeResolver;
        private readonly object script;
        private readonly bool supported;
        private readonly MethodInfo getDevice;
        private readonly PropertyInfo activity, course;
        private readonly Dictionary<FieldInfo, object> saved = new Dictionary<FieldInfo, object>();
        private readonly Dictionary<FieldInfo, object> timerSaved = new Dictionary<FieldInfo, object>();
        private readonly Dictionary<FieldInfo, object> gearboxSaved = new Dictionary<FieldInfo, object>();
        private object dataTimer, nativeTrainer, model, gearbox;
        private IDictionary rows;
        private string antBaseline, bleBaseline;
        private bool active, pedalling;
        private int targetPower = 200;

        public GtbikeV1KeyboardTrainer(Func<string, Type> typeOf, bool isSupported, object modScript)
            : this(typeOf, isSupported, modScript, modScript == null ? null : modScript.GetType()) { }

        private GtbikeV1KeyboardTrainer(Func<string, Type> typeOf, bool isSupported, object modScript, Type scriptType)
        {
            supported = isSupported;
            script = modScript;
            typeResolver = typeOf;
            dt = typeOf("GTBikeV.DataTransceiver");
            vt = typeOf("GTBikeV.VirtualTrainer");
            timer = typeOf("GTBikeV.Timer");
            functionType = typeOf("GTBikeV.Devices.DeviceFunctionType");
            Type device = typeOf("GTBikeV.Devices.DeviceInfo");
            getDevice = device == null || functionType == null ? null : device.GetMethod("GetCurrentDeviceFor", S, null, new Type[] { functionType }, null);
            activity = scriptType == null ? null : scriptType.GetProperty("currentActivity", I);
            course = scriptType == null ? null : scriptType.GetProperty("currentCourse", I);
        }

        // Metadata only: never creates a GTA Script or executes the mod's static initializers.
        public static string VerifyBindings(Func<string, Type> typeOf)
        {
            try
            {
                var candidate = new GtbikeV1KeyboardTrainer(typeOf, true, null, typeOf("GTBikeV.GTBikeVScript"));
                candidate.Validate();
                return null;
            }
            catch (Exception error) { return Root(error).Message; }
        }

        public bool Active { get { return active; } }
        public int TargetPowerW
        {
            get { return targetPower; }
            set { targetPower = Math.Max(50, Math.Min(1000, value)); }
        }

        // 100% Virtual Smart Trainer simulation mode.
        public string Start()
        {
            if (active) return null;
            if (!supported || dt == null || vt == null || timer == null || script == null || getDevice == null || activity == null || course == null)
                return "Keyboard trainer requires GTBikeV 0.8.0.4 and its compatible companion adapter.";
            try
            {
                Validate();
                if ((bool)Read("DTInitialized") || (bool)Get(dt, null, "IsDTOK") || !InactiveStatus(Read("ANTStatus")) || !InactiveStatus(Read("BLEStatus")))
                    return "Deactivate GTBikeV and wait for its real receivers to stop before enabling the keyboard trainer.";
                foreach (object role in Enum.GetValues(functionType))
                    if (getDevice.Invoke(null, new object[] { role }) != null)
                        return "Disconnect physical sensors before enabling the keyboard trainer.";

                IDictionary collection = (IDictionary)timer.GetField("timerCollection", S).GetValue(null);
                dataTimer = collection["DATA_UPDATE"];
                if (dataTimer == null) throw new InvalidOperationException("DATA_UPDATE timer is absent.");
                Snapshot(dt, null, S, saved);
                Snapshot(dataTimer.GetType(), dataTimer, I, timerSaved);
                antBaseline = Status(Read("ANTStatus"));
                bleBaseline = Status(Read("BLEStatus"));
                object originalTrainer = Read("_virtualTrainer");
                gearbox = Get(vt, originalTrainer, "VirtualGearBox");
                Snapshot(gearbox.GetType(), gearbox, I, gearboxSaved);
                nativeTrainer = Clone(originalTrainer);
                model = Clone(originalTrainer);
                ResetTrainer(nativeTrainer);
                ResetTrainer(model);
                rows = (IDictionary)CloneRows((IDictionary)Read("_receiverData"));

                // Suppress only the native receiver send/update timer. Other game timers keep running.
                Put(dataTimer, "interval", UInt32.MaxValue);
                Put(dataTimer, "timeOut", false);
                Put(dataTimer, "lastTimeOut", Millis());
                Write("_receiverData", rows);
                Write("_virtualTrainer", nativeTrainer);
                Write("DTInitialized", true);
                Write("ANTStatus", Enum.Parse(Read("ANTStatus").GetType(), "STOPPED"));
                Write("BLEStatus", Enum.Parse(Read("BLEStatus").GetType(), "READY"));
                Write("stopRequested", false);
                Write("AlwaysUseVirtualSpeed", true);
                Write("startTicks", DateTime.UtcNow.Ticks);
                Write("player", Get(script.GetType(), null, "CurrentPlayer"));
                Set(dt, null, "TotalDistance_m", 0f);
                Set(dt, null, "RidingTime_ms", 0L);
                active = true;
                Publish(0, 0, 0f);
                EnableMenuItems();
                return null;
            }
            catch (Exception error)
            {
                Stop();
                return "Keyboard trainer could not bind to this mod: " + Root(error).Message;
            }
        }

        // Native onTick first consumes the previous frame's sample, then polls ANT. Restore the synthetic
        // sample afterwards. A separate native physics clone prevents that poll from applying inertia at 0 W.
        public void Step(bool pedal, bool brake, bool allowed, double deltaSeconds)
        {
            if (!active) return;
            if ((bool)Read("stopRequested") || Status(Read("BLEStatus")) != "ready")
            {
                Stop();
                return;
            }
            Put(dataTimer, "timeOut", false);
            Put(dataTimer, "interval", UInt32.MaxValue);
            Put(dataTimer, "lastTimeOut", Millis());
            pedalling = pedal && allowed && !brake;
            int power = pedalling ? targetPower : 0;
            byte cadence = pedalling ? (byte)85 : (byte)0;

            object bicycle = Read("VehicleParams");
            if (bicycle != null)
            {
                float weight = Finite(Read("PlayerWeight"), 75f, 20f, 300f);
                float mass = Finite(Member(bicycle, "Mass"), 10f, 1f, 100f);
                Invoke("SetUserSettings", model, weight, mass);
                float slope = Finite(Get(dt, null, "Slope"), 0f, -1f, 1f);
                float friction = Finite(Read("TerrainFriction"), 0.004f, 0.0001f, 5f);
                Invoke("SetTerrainResistance", model, slope * 100f, friction);
                float grip = Finite(Member(bicycle, "GripFactor"), 1f, 0f, 10f);
                float traction = Finite(Read("TerrainTraction"), 0f, -1f, 1f);
                Invoke("SetTransferenceCoefficient", model, grip + traction);
                // AtmosphericCalculation reads the game's actual wind, elevation and bicycle parameters.
                object atmosphere = dt.GetMethod("AtmosphericCalculation", S, null, Type.EmptyTypes, null).Invoke(null, null);
                float coeff = Finite(Member(atmosphere, "windResCoeff"), 0.001377f, 0f, 10f);
                int wind = (int)Finite(Member(atmosphere, "realWindSpeed_kmh"), 0f, -127f, 127f);
                float drafting = Finite(Read("DraftingCoeff"), 1f, 0.01f, 2f);
                Invoke("SetWindResistance", model, coeff * 0.5f, (sbyte)wind, drafting);
            }
            Set(vt, model, "Power", (ushort)power);
            Set(vt, model, "Cadence", cadence);
            string braking = Convert.ToString(Read("BrakingState"), CultureInfo.InvariantCulture);
            if (brake || braking != "NotBraking")
            {
                // Follow native braking (including autopilot corners), without sending resistance commands.
                float divisor = 4f;
                Type config = dt.Assembly.GetType("GTBikeV.Config");
                if (config != null) divisor = Finite(Get(config, null, "BrakingDivisor"), 4f, 1f, 255f);
                float level = Convert.ToSingle(Read("brakingLevel"), CultureInfo.InvariantCulture);
                if (braking == "AutoBraking") level = 100f;
                if (braking == "OverSpeedBraking") level = 40f;
                if (brake) level = Math.Max(40f, level);
                Invoke("SetBraking", model, level / divisor);
            }
            // Do not turn a paused frame or an alt-tab gap into a jump in the trainer's wall-clock inertia.
            if (!allowed || deltaSeconds < 0 || deltaSeconds > 0.25)
                Put(model, "_lastInertiaTime", Millis());
            Invoke("Update", model, false);
            float speed = Convert.ToSingle(Invoke("GetSpeed", model), CultureInfo.InvariantCulture);
            if (Single.IsNaN(speed) || Single.IsInfinity(speed) || speed < 0f) speed = 0f;
            speed = Math.Min(25f, speed);
            // Pause/focus loss and dismounting publish a stopped sample. The private model keeps its inertia
            // for resuming, while the native mounting routine can see the zero speed/cadence it requires.
            Publish(power, cadence, allowed ? speed : 0f);
            if (allowed && deltaSeconds > 0 && deltaSeconds <= 0.25)
            {
                float distance = Convert.ToSingle(Get(dt, null, "TotalDistance_m"), CultureInfo.InvariantCulture);
                long riding = Convert.ToInt64(Get(dt, null, "RidingTime_ms"), CultureInfo.InvariantCulture);
                Set(dt, null, "TotalDistance_m", distance + speed * (float)deltaSeconds);
                Set(dt, null, "RidingTime_ms", riding + (long)(deltaSeconds * 1000));
            }
        }

        public Dictionary<string, object> Report()
        {
            if (!active) return null;
            return new Dictionary<string, object> {
                { "mode", "keyboard" }, { "targetPowerW", targetPower },
                { "cadenceRpm", 85 }, { "pedalling", pedalling }
            };
        }

        public void Overlay(Dictionary<string, object> snapshot)
        {
            if (!active) return;
            snapshot["testInput"] = Report();
            snapshot["receivers"] = new Dictionary<string, object> { { "ant", "stopped" }, { "ble", "ready" } };
            snapshot["modLayer"] = "dataReady";
            snapshot["isDtOk"] = true;

            var sensors = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "role", "power" }, { "source", "BLE" }, { "deviceId", "1001" }, { "sensorType", "FEC" }, { "present", true }, { "name", "Virtual Smart Trainer" } },
                new Dictionary<string, object> { { "role", "cadence" }, { "source", "BLE" }, { "deviceId", "1001" }, { "sensorType", "FEC" }, { "present", true }, { "name", "Virtual Smart Trainer" } },
                new Dictionary<string, object> { { "role", "resistance" }, { "source", "BLE" }, { "deviceId", "1001" }, { "sensorType", "FEC" }, { "present", true }, { "name", "Virtual Smart Trainer" } },
                new Dictionary<string, object> { { "role", "heartRate" }, { "source", "BLE" }, { "deviceId", "1002" }, { "sensorType", "HR" }, { "present", true }, { "name", "Virtual Heart Rate" } }
            };
            snapshot["sensors"] = sensors;

            object commandsObject;
            if (snapshot.TryGetValue("commands", out commandsObject))
            {
                List<string> commands = commandsObject as List<string>;
                if (commands != null)
                {
                    if (!commands.Contains("activities.finish")) commands.Add("activities.finish");
                    if (!commands.Contains("routes.apply")) commands.Add("routes.apply");
                }
            }
            object activitiesObject;
            if (snapshot.TryGetValue("activities", out activitiesObject))
            {
                Dictionary<string, object> activities = activitiesObject as Dictionary<string, object>;
                if (activities != null) activities["saveEnabled"] = true;
            }
            object metricObject;
            if (snapshot.TryGetValue("metrics", out metricObject))
            {
                Dictionary<string, object> metrics = metricObject as Dictionary<string, object>;
                if (metrics != null)
                {
                    metrics["speedVirtual"] = true;
                    byte hr = pedalling ? (byte)Math.Min(200, 80 + (int)((targetPower / 250.0) * 75)) : (byte)78;
                    metrics["heartRateBpm"] = (int)hr;
                }
            }
        }

        public void Stop()
        {
            bool hadState = saved.Count != 0;
            active = false;
            pedalling = false;
            try { Restore(gearbox, gearboxSaved); }
            finally
            {
                try { Restore(null, saved); }
                finally { Restore(dataTimer, timerSaved); }
            }
            dataTimer = nativeTrainer = model = gearbox = null;
            rows = null;
        }

        private void Validate()
        {
            foreach (string name in new string[] { "DTInitialized", "ANTStatus", "BLEStatus", "stopRequested", "AlwaysUseVirtualSpeed", "startTicks", "player", "_receiverData", "_virtualTrainer", "PlayerWeight", "VehicleParams", "TerrainFriction", "TerrainTraction", "DraftingCoeff", "BrakingState", "brakingLevel" })
                if (dt.GetField(name, S) == null) throw new MissingFieldException(dt.FullName, name);
            if (timer.GetField("timerCollection", S) == null || timer.GetMethod("MillisSinceStartup", S) == null) throw new MissingMemberException("Native timer shape differs.");
            foreach (string name in new string[] { "Power", "Cadence", "VirtualGearBox" })
                if (vt.GetProperty(name, I) == null) throw new MissingMemberException(vt.FullName, name);
            foreach (string name in new string[] { "_currentSpeed", "_targetSpeed", "_lastTargetSpeed", "_lastInertiaTime", "_lastNonZeroCadenceTime", "_autoBrakeApplied", "_currentFriction", "_inertia" })
                if (vt.GetField(name, I) == null) throw new MissingFieldException(vt.FullName, name);
            foreach (string name in new string[] { "SetUserSettings", "SetTerrainResistance", "SetTransferenceCoefficient", "SetWindResistance", "SetBraking", "Update", "GetSpeed" })
                if (vt.GetMethod(name, I) == null) throw new MissingMethodException(vt.FullName, name);
        }

        private void EnableMenuItems()
        {
            if (typeResolver == null) return;
            try
            {
                Type menus = typeResolver("GTBikeV.UI.Menus");
                if (menus == null) return;
                string[] fields = new string[] { "courseSelectionMenuItem", "autodriveMenuItem", "avoidObstaclesMenuItem", "saveActivityMenuItem" };
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo f = menus.GetField(fields[i], S);
                    if (f == null) continue;
                    object item = f.GetValue(null);
                    if (item == null) continue;
                    PropertyInfo enabled = item.GetType().GetProperty("Enabled", I);
                    if (enabled != null) enabled.SetValue(item, true, null);
                }
            }
            catch { }
        }

        private void ResetTrainer(object trainer)
        {
            Put(trainer, "_currentSpeed", 0f);
            Put(trainer, "_targetSpeed", 0f);
            Put(trainer, "_lastTargetSpeed", 0f);
            Put(trainer, "_lastInertiaTime", 0L);
            Put(trainer, "_lastNonZeroCadenceTime", Millis());
            Put(trainer, "_autoBrakeApplied", false);
            Put(trainer, "_currentFriction", 0.004f);
            FieldInfo inertia = vt.GetField("_inertia", I);
            inertia.SetValue(trainer, Activator.CreateInstance(inertia.FieldType, true));
            Set(vt, trainer, "Power", (ushort)0);
            Set(vt, trainer, "Cadence", (byte)0);
        }

        private void Publish(int power, byte cadence, float speed)
        {
            byte hr = pedalling ? (byte)Math.Min(200, 80 + (int)((targetPower / 250.0) * 75)) : (byte)78;
            foreach (DictionaryEntry entry in rows)
            {
                object row = entry.Value;
                Put(row, "Power_w", power);
                Put(row, "Cadence_rpm", cadence);
                Put(row, "Speed_ms", -1f);
                Put(row, "HeartRate", hr);
                Put(row, "SteeringAngle", 32767);
                Put(row, "BrakingValue", Byte.MaxValue);
                Put(row, "IsReceiverOK", true);
                FieldInfo sport = row.GetType().GetField("Sport", I);
                sport.SetValue(row, Enum.Parse(sport.FieldType, "SPORT_TYPE_CYCLING"));
            }
            Set(vt, nativeTrainer, "Power", (ushort)power);
            Set(vt, nativeTrainer, "Cadence", cadence);
            Put(nativeTrainer, "_currentSpeed", speed);
        }

        private object CloneRows(IDictionary original)
        {
            IDictionary copy = (IDictionary)Activator.CreateInstance(original.GetType());
            foreach (DictionaryEntry entry in original) copy.Add(entry.Key, Clone(entry.Value));
            return copy;
        }
        private static object Clone(object value) { return typeof(object).GetMethod("MemberwiseClone", I).Invoke(value, null); }
        private static void Snapshot(Type type, object target, BindingFlags flags, Dictionary<FieldInfo, object> output)
        {
            foreach (FieldInfo field in type.GetFields(flags))
                if (!field.IsInitOnly && !field.IsLiteral) output[field] = field.GetValue(target);
        }
        private static void Restore(object target, Dictionary<FieldInfo, object> state)
        {
            Exception failure = null;
            foreach (KeyValuePair<FieldInfo, object> entry in state)
                try { entry.Key.SetValue(target, entry.Value); } catch (Exception e) { if (failure == null) failure = e; }
            state.Clear();
            if (failure != null) throw failure;
        }
        private long Millis() { return (long)timer.GetMethod("MillisSinceStartup", S).Invoke(null, null); }
        private object Read(string name) { return dt.GetField(name, S).GetValue(null); }
        private void Write(string name, object value) { dt.GetField(name, S).SetValue(null, value); }
        private static void Put(object value, string name, object fieldValue) { value.GetType().GetField(name, I).SetValue(value, fieldValue); }
        private static object Get(Type type, object value, string name) { return type.GetProperty(name, value == null ? S : I).GetValue(value, null); }
        private static void Set(Type type, object value, string name, object propertyValue) { type.GetProperty(name, value == null ? S : I).SetValue(value, propertyValue, null); }
        private static object Member(object value, string name)
        {
            PropertyInfo prop = value.GetType().GetProperty(name, I);
            return prop == null ? value.GetType().GetField(name, I).GetValue(value) : prop.GetValue(value, null);
        }
        private object Invoke(string name, object value, params object[] args) { return vt.GetMethod(name, I).Invoke(value, args); }
        private static string Status(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture).ToLowerInvariant(); }
        private static bool InactiveStatus(object value) { string name = Status(value); return name == "uninitialized" || name == "stopped"; }
        private static float Finite(object value, float fallback, float minimum, float maximum)
        {
            float number = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            return Single.IsNaN(number) || Single.IsInfinity(number) || number < minimum || number > maximum ? fallback : number;
        }
        private static Exception Root(Exception error) { while (error.InnerException != null) error = error.InnerException; return error; }
    }
}
