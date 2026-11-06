// GTAV Riding Companion adapter v1 - binding for profile gtbikev-0804.
// See bridge/compatibility/gtbikev-0804.bindings.json and contracts/v1/policy.json.
// Read-only reflection plus ONE invocation: the "Activate GTBike V" menu item, behind modSetActiveGuard.
// ERG, target power, workout selection and PowerAdjust members are never resolved here.
// C# 5 only (compiled with the .NET Framework csc). No dependency on ScriptHookVDotNet: testable with a harness.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1Binding
    {
        public const string Profile = "gtbikev-0804";
        public const string SupportedVersion = "0.8.0.4";
        private const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly string[] RoleNames = { "Power", "Resistance", "Cadence", "HeartRate", "Steering", "Controls", "RunningSpeed" };
        private static readonly string[] RoleWire = { "power", "resistance", "cadence", "heartRate", "steering", "controls", "runningSpeed" };
        private static readonly HashSet<string> ReceiverNames = new HashSet<string> { "uninitialized", "initialized", "started", "complete", "ready", "stopping", "stopped", "error" };
        private static readonly HashSet<string> Transitional = new HashSet<string> { "initialized", "started", "complete", "stopping" };
        private static readonly HashSet<string> SensorTypes = new HashSet<string> { "FEC", "CTRLS", "CPS", "HR", "CAD", "SPD", "FPOD", "BTS", "STZ", "KICKR" };
        private static readonly HashSet<string> Sports = new HashSet<string> { "SPORT_TYPE_CYCLING", "SPORT_TYPE_RUNNING", "SPORT_TYPE_ROWING" };

        private readonly string modVersion;
        private readonly bool versionMatches;
        private readonly List<string> missingRequired = new List<string>();
        private readonly List<string> missingOptional = new List<string>();

        private readonly PropertyInfo power, speed, speedVirtual, cadence, heartRate, distance, ridingTime, slope, windSpeed, windDir, sport, isDtOk;
        private readonly FieldInfo antStatus, bleStatus;
        private readonly PropertyInfo currentActivity, movingTime, elevationGain, elevationLoss, workoutFinished;
        private readonly FieldInfo workoutField;
        private readonly MethodInfo getCurrentDevice;
        private readonly Type functionType;
        private readonly PropertyInfo devicePresent, deviceId, deviceSource, deviceSensorType;
        private readonly FieldInfo activateItem;

        private object lastActivity;
        private long sessionGeneration;
        private List<Dictionary<string, object>> lastSensors;
        private DateTime nextSensorReadUtc = DateTime.MinValue;

        public GtbikeV1Binding(Func<string, Type> typeOf, string assemblyVersion)
        {
            modVersion = assemblyVersion;
            versionMatches = assemblyVersion == SupportedVersion;

            Type dt = typeOf("GTBikeV.DataTransceiver");
            power = Prop(dt, "Power_w", typeof(int), S, true);
            speed = Prop(dt, "Speed_ms", typeof(float), S, true);
            speedVirtual = Prop(dt, "IsSpeedVirtual", typeof(bool), S, false);
            cadence = Prop(dt, "Cadence_rpm", typeof(byte), S, true);
            heartRate = Prop(dt, "HeartRate", typeof(byte), S, true);
            distance = Prop(dt, "TotalDistance_m", typeof(float), S, true);
            ridingTime = Prop(dt, "RidingTime_ms", typeof(long), S, true);
            slope = Prop(dt, "Slope", typeof(float), S, false);
            windSpeed = Prop(dt, "WindSpeed_kmh", typeof(float), S, false);
            windDir = Prop(dt, "WindDir_rad", typeof(float), S, false);
            sport = EnumProp(dt, "Sport", S, false);
            isDtOk = Prop(dt, "IsDTOK", typeof(bool), S, true);
            antStatus = ReceiverField(dt, "ANTStatus");
            bleStatus = ReceiverField(dt, "BLEStatus");

            Type multiplayer = typeOf("GTBikeV.Network.Multiplayer");
            Type activity = typeOf("GTBikeV.Activity");
            Type workout = typeOf("GTBikeV.Workouts.Workout");
            currentActivity = Prop(multiplayer, "CurrentActivity", activity, S, true);
            movingTime = Prop(activity, "TotalMovingTime", typeof(long), I, false);
            elevationGain = Prop(activity, "ElevationGain", typeof(float), I, false);
            elevationLoss = Prop(activity, "ElevationLoss", typeof(float), I, false);
            workoutField = Field(activity, "Workout", workout, I, true);
            workoutFinished = Prop(workout, "Finished", typeof(bool), I, true);

            Type deviceInfo = typeOf("GTBikeV.Devices.DeviceInfo");
            Type iDevice = typeOf("GTBikeV.Devices.IDeviceInfo");
            functionType = typeOf("GTBikeV.Devices.DeviceFunctionType");
            if (deviceInfo != null && iDevice != null && functionType != null && functionType.IsEnum)
            {
                getCurrentDevice = deviceInfo.GetMethod("GetCurrentDeviceFor", S, null, new Type[] { functionType }, null);
                if (getCurrentDevice != null && getCurrentDevice.ReturnType != iDevice) getCurrentDevice = null;
            }
            if (getCurrentDevice == null) missingOptional.Add("DeviceInfo.GetCurrentDeviceFor");
            devicePresent = Prop(iDevice, "Present", typeof(bool), I, false);
            deviceId = Prop(iDevice, "DeviceId", typeof(ulong), I, false);
            deviceSource = EnumProp(iDevice, "DataSourceType", I, false);
            deviceSensorType = EnumProp(iDevice, "SensorType", I, false);

            Type menus = typeOf("GTBikeV.UI.Menus");
            activateItem = menus == null ? null : menus.GetField("activateModMenuItem", S);
            if (activateItem == null) missingRequired.Add("Menus.activateModMenuItem");
        }

        public string BindingStatus
        {
            get
            {
                if (missingRequired.Count > 0) return "incompatible";
                if (!versionMatches || missingOptional.Count > 0) return "limited";
                return "compatible";
            }
        }

        /// <summary>Writes need the exact version and every required binding; unknown versions are read-only.</summary>
        public bool CanSetActive { get { return versionMatches && missingRequired.Count == 0; } }

        public Dictionary<string, object> Capture(string gameControl, DateTime nowUtc)
        {
            Dictionary<string, object> snap = new Dictionary<string, object>();
            snap["schema"] = "gtbikev-adapter-v1";
            snap["modLoaded"] = true;
            snap["modVersion"] = modVersion;
            snap["binding"] = BindingStatus;
            snap["bindingProfile"] = CanSetActive ? Profile : null;
            snap["bindingIssues"] = Issues();
            snap["gameControl"] = gameControl;
            snap["commands"] = CanSetActive ? new List<string> { "mod.setActive" } : new List<string>();
            if (missingRequired.Count > 0)
            {
                snap["receivers"] = Receivers("unknown", "unknown");
                snap["modLayer"] = "unknown";
                return snap;
            }

            string ant = ReceiverName(antStatus);
            string ble = ReceiverName(bleStatus);
            bool ok = (bool)isDtOk.GetValue(null, null);
            snap["receivers"] = Receivers(ant, ble);
            snap["isDtOk"] = ok;
            snap["modLayer"] = ModLayer(ant, ble, ok);
            snap["sport"] = SportName();

            Dictionary<string, object> m = new Dictionary<string, object>();
            int p = (int)power.GetValue(null, null);
            m["powerW"] = ok && p >= 0 && p <= 5000 ? (object)p : null;
            float v = (float)speed.GetValue(null, null);
            m["speedMps"] = ok && Finite(v) && v >= 0 && v <= 40 ? (object)(double)v : null;
            m["speedVirtual"] = speedVirtual == null ? null : speedVirtual.GetValue(null, null);
            byte c = (byte)cadence.GetValue(null, null);
            m["cadenceRpm"] = ok && c != 255 ? (object)(int)c : null;
            byte h = (byte)heartRate.GetValue(null, null);
            m["heartRateBpm"] = ok && h != 0 && h != 255 ? (object)(int)h : null;
            float d = (float)distance.GetValue(null, null);
            m["distanceM"] = Finite(d) && d >= 0 ? (object)(double)d : null;
            long t = (long)ridingTime.GetValue(null, null);
            m["ridingTimeMs"] = t >= 0 ? (object)t : null;
            m["slopeFraction"] = ok ? StaticValue(slope, -1, 1, 1) : null;
            m["windSpeedMps"] = ok ? StaticValue(windSpeed, 0, 100, 1 / 3.6) : null;
            m["windDirRad"] = ok ? StaticValue(windDir, -7, 7, 1) : null;
            snap["metrics"] = m;

            object act = currentActivity.GetValue(null, null);
            if (act != null && !object.ReferenceEquals(act, lastActivity)) sessionGeneration++;
            lastActivity = act;
            snap["sessionGeneration"] = act == null ? null : (object)sessionGeneration;
            snap["activity"] = act == null ? null : ActivityInfo(act);

            if (!ok || getCurrentDevice == null)
            {
                lastSensors = null;
            }
            else if (nowUtc >= nextSensorReadUtc)
            {
                // Same condition the mod itself uses (DataTransceiver.Update while receivers run). 1 Hz max.
                nextSensorReadUtc = nowUtc.AddSeconds(1);
                lastSensors = ReadSensors();
            }
            snap["sensors"] = lastSensors;
            return snap;
        }

        /// <summary>policy.json modSetActiveGuard. Returns status dispatched | noop | rejected with reasonCode.</summary>
        public Dictionary<string, object> SetActive(bool enabled, string gameControl)
        {
            if (!CanSetActive) return Ack("rejected", versionMatches ? "CAPABILITY_UNAVAILABLE" : "UNSUPPORTED_VERSION");
            if (gameControl != "inControl") return Ack("rejected", "GAME_NOT_IN_CONTROL");
            string ant = ReceiverName(antStatus);
            string ble = ReceiverName(bleStatus);
            bool ok = (bool)isDtOk.GetValue(null, null);
            if (enabled)
            {
                if (Transitional.Contains(ant) || Transitional.Contains(ble)) return Ack("rejected", "MOD_TRANSITION_IN_PROGRESS");
                if (ok) return Ack("noop", null);
            }
            else
            {
                if (ant == "stopping" || ble == "stopping") return Ack("rejected", "MOD_TRANSITION_IN_PROGRESS");
                if (!ok) return Ack("noop", null);
            }
            object item = activateItem.GetValue(null);
            if (item == null) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            PropertyInfo itemEnabled = item.GetType().GetProperty("Enabled", I);
            if (itemEnabled == null || !(bool)itemEnabled.GetValue(item, null)) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            MethodInfo onActivated = item.GetType().GetMethod("OnActivated", I, null, new Type[] { typeof(object) }, null);
            if (onActivated == null || HandlerCount(item) < 2) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            onActivated.Invoke(item, new object[] { item });
            return Ack("dispatched", null);
        }

        // ---- helpers ----

        private Dictionary<string, object> ActivityInfo(object act)
        {
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["movingTimeMs"] = movingTime == null ? null : (object)Math.Max(0L, (long)movingTime.GetValue(act, null));
            a["elevationGainM"] = elevationGain == null ? null : InstanceValue(elevationGain, act, 0, 1e6);
            // Activity accumulates loss with a negative sign; the contract carries the magnitude.
            object loss = elevationLoss == null ? null : InstanceValue(elevationLoss, act, -1e6, 1e6);
            a["elevationLossM"] = loss == null ? null : (object)Math.Abs((double)loss);
            object workout = workoutField.GetValue(act);
            a["workoutPresent"] = workout != null;
            a["workoutFinished"] = workout == null ? null : workoutFinished.GetValue(workout, null);
            return a;
        }

        private List<Dictionary<string, object>> ReadSensors()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            for (int i = 0; i < RoleNames.Length; i++)
            {
                Dictionary<string, object> s = new Dictionary<string, object>();
                s["role"] = RoleWire[i];
                s["present"] = null; s["source"] = "unknown"; s["deviceId"] = null; s["sensorType"] = "unknown";
                try
                {
                    object fn = Enum.Parse(functionType, RoleNames[i]);
                    object dev = getCurrentDevice.Invoke(null, new object[] { fn });
                    if (dev == null) { s["present"] = false; }
                    else
                    {
                        bool present = devicePresent != null && (bool)devicePresent.GetValue(dev, null);
                        string source = deviceSource == null ? "unknown" : deviceSource.GetValue(dev, null).ToString();
                        s["source"] = source == "ANT" || source == "BLE" ? source : "unknown";
                        if (present && deviceId != null && (string)s["source"] != "unknown")
                        {
                            s["present"] = true;
                            s["deviceId"] = ((ulong)deviceId.GetValue(dev, null)).ToString(CultureInfo.InvariantCulture);
                        }
                        else s["present"] = false;
                        string type = deviceSensorType == null ? "unknown" : deviceSensorType.GetValue(dev, null).ToString();
                        s["sensorType"] = SensorTypes.Contains(type) ? type : "unknown";
                        // P12: BLEDeviceInfo.Name (the getter throws for a sensor with a manufacturer but no name).
                        if (true.Equals(s["present"]) && (string)s["source"] == "BLE")
                        {
                            try
                            {
                                PropertyInfo nameProp = dev.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                string name = nameProp == null ? null : nameProp.GetValue(dev, null) as string;
                                if (name != null) { name = name.Trim(); if (name.Length > 64) name = name.Substring(0, 64); if (name.Length > 0) s["name"] = name; }
                            }
                            catch (Exception) { }
                        }
                    }
                }
                catch (Exception) { s["present"] = null; } // One role failing never hides the others.
                list.Add(s);
            }
            return list;
        }

        private static Dictionary<string, object> Receivers(string ant, string ble)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ant"] = ant; r["ble"] = ble;
            return r;
        }

        private static string ModLayer(string ant, string ble, bool ok)
        {
            if (ant == "stopping" || ble == "stopping") return "stopping";
            if (ant == "ready" || ble == "ready") return "dataReady";
            if (Transitional.Contains(ant) || Transitional.Contains(ble)) return "initializing";
            if (ant == "error" || ble == "error") return "error";
            if (ant == "unknown" || ble == "unknown") return "unknown";
            return ok ? "dataReady" : "inactive";
        }

        private string SportName()
        {
            if (sport == null) return "unknown";
            string name = sport.GetValue(null, null).ToString();
            if (!Sports.Contains(name)) return "unknown";
            return name.Substring("SPORT_TYPE_".Length).ToLowerInvariant();
        }

        private static string ReceiverName(FieldInfo field)
        {
            if (field == null) return "unknown";
            object value = field.GetValue(null);
            string name = value == null ? "unknown" : value.ToString().ToLowerInvariant();
            return ReceiverNames.Contains(name) ? name : "unknown";
        }

        /// <summary>Static member scaled to canonical units; [min,max] apply to the scaled value.</summary>
        private static object StaticValue(PropertyInfo p, double min, double max, double scale)
        {
            if (p == null) return null;
            double n = Convert.ToDouble(p.GetValue(null, null), CultureInfo.InvariantCulture) * scale;
            return double.IsNaN(n) || double.IsInfinity(n) || n < min || n > max ? null : (object)n;
        }

        private static object InstanceValue(PropertyInfo p, object target, double min, double max)
        {
            double n = Convert.ToDouble(p.GetValue(target, null), CultureInfo.InvariantCulture);
            return double.IsNaN(n) || double.IsInfinity(n) || n < min || n > max ? null : (object)n;
        }

        private static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }

        private static int HandlerCount(object item)
        {
            Type type = item.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField("Activated", I | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    Delegate handler = field.GetValue(item) as Delegate;
                    return handler == null ? 0 : handler.GetInvocationList().Length;
                }
                type = type.BaseType;
            }
            return 0;
        }

        private Dictionary<string, object> Ack(string status, string reasonCode)
        {
            Dictionary<string, object> ack = new Dictionary<string, object>();
            ack["status"] = status;
            ack["reasonCode"] = reasonCode;
            return ack;
        }

        private List<string> Issues()
        {
            List<string> all = new List<string>();
            if (!versionMatches) all.Add("version:" + (modVersion ?? "unknown"));
            all.AddRange(missingRequired);
            all.AddRange(missingOptional);
            return all.Count > 10 ? all.GetRange(0, 10) : all;
        }

        private PropertyInfo Prop(Type owner, string name, Type expected, BindingFlags flags, bool required)
        {
            PropertyInfo p = owner == null || expected == null ? null : owner.GetProperty(name, flags);
            if (p != null && (p.PropertyType != expected || p.GetGetMethod(true) == null)) p = null;
            if (p == null) (required ? missingRequired : missingOptional).Add(Short(owner, name));
            return p;
        }

        private PropertyInfo EnumProp(Type owner, string name, BindingFlags flags, bool required)
        {
            PropertyInfo p = owner == null ? null : owner.GetProperty(name, flags);
            if (p != null && (!p.PropertyType.IsEnum || p.GetGetMethod(true) == null)) p = null;
            if (p == null) (required ? missingRequired : missingOptional).Add(Short(owner, name));
            return p;
        }

        private FieldInfo Field(Type owner, string name, Type expected, BindingFlags flags, bool required)
        {
            FieldInfo f = owner == null || expected == null ? null : owner.GetField(name, flags);
            if (f != null && f.FieldType != expected) f = null;
            if (f == null) (required ? missingRequired : missingOptional).Add(Short(owner, name));
            return f;
        }

        private FieldInfo ReceiverField(Type owner, string name)
        {
            FieldInfo f = owner == null ? null : owner.GetField(name, S);
            if (f != null && !(f.FieldType.IsEnum && Enum.IsDefined(f.FieldType, "READY") && Enum.IsDefined(f.FieldType, "STOPPING"))) f = null;
            if (f == null) missingRequired.Add(Short(owner, name));
            return f;
        }

        private static string Short(Type owner, string name)
        {
            return (owner == null ? "?" : owner.Name) + "." + name;
        }
    }
}
