// GTAV Riding Companion adapter v1 - P5 (device roles), P6 (workouts, read only) and P7 (activities) for gtbikev-0804.
// Static evidence: Devices/DeviceInfo.cs, UI/DeviceSelectionView.cs, GTBikeVScript.DeviceConfigHandler (1610),
// Workouts/WorkoutRetriever.cs, Workouts/Workout.cs, Activity.cs (Encode 326), GTBikeVScript.SaveCurrentActivity (1564).
// Workouts: read, plus P13 workouts.select through the native Select button path (WorkoutSelectionView.TrainingPlan
// + its OnWorkoutSelected event, run on the mod's own Tick). No pause/skip/end, ERG, target or bias members are
// resolved here; the mod itself engages ERG for the selected workout exactly as when chosen in its menu.
// Saving uses the native Save menu item; the PC then shows its own summary, which the phone never touches.
// C# 5 only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1More
    {
        private const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly string[] RoleNames = { "Power", "Resistance", "Cadence", "HeartRate", "Steering", "Controls", "RunningSpeed" };
        private static readonly string[] RoleWire = { "power", "resistance", "cadence", "heartRate", "steering", "controls", "runningSpeed" };
        private static readonly HashSet<string> SensorTypes = new HashSet<string> { "FEC", "CTRLS", "CPS", "HR", "CAD", "SPD", "FPOD", "BTS", "STZ", "KICKR" };

        private readonly bool writable;
        private readonly List<string> missing = new List<string>();

        // P5
        private readonly Type functionType, actionType, selectionConfigType;
        private readonly MethodInfo currentDevice, isAuto, isLocked, isSkippable;
        private readonly FieldInfo deviceConfig, deviceView;
        private readonly PropertyInfo sport;
        private readonly bool devicesRead, devicesWrite;
        private List<object> lastRoles;
        private DateTime nextDevicesUtc = DateTime.MinValue;

        // P11: BLE device chosen on the phone (the phone only scans; the PC connects to the sensor itself).
        // Evidence: Devices/DeviceInfo.cs ChangeDeviceConfig + InitializeReceiverDevices; Devices/BLEDeviceInfo.cs
        // (DeviceId = 48-bit Windows BluetoothAddress); BLE_Receiver.AddDevice(byte, ulong) and AdvertisingFound
        // (connects a type only to its expected address, 0 = any).
        private static readonly string[] AssignTypes = { "FEC", "CPS", "HR", "CAD", "BTS", "KICKR", "CTRLS", "FPOD" };
        // Sensor types that can serve each role (BLE DeviceFactory.DevicePreference), in RoleWire order.
        private static readonly string[][] RoleTypes =
        {
            new string[] { "CPS", "KICKR", "FEC", "CAD" },
            new string[] { "KICKR", "FEC" },
            new string[] { "CPS", "FEC", "KICKR", "CAD" },
            new string[] { "HR", "FEC", "FPOD" },
            new string[] { "BTS", "CTRLS", "KICKR" },
            new string[] { "CTRLS", "KICKR" },
            new string[] { "FPOD", "FEC", "HR" },
        };
        private const ulong MaxBleAddress = 0xFFFFFFFFFFFFUL;
        private readonly MethodInfo changeConfig, bleAddDevice;
        private readonly ConstructorInfo bleInfoCtor;
        private readonly Type bleReceiverInfo, sensorEnum;
        private readonly bool devicesAssign;
        // Disconnect: BLE_Receiver.DisconnectDevice (private, the receiver's own bookkeeping for a lost device:
        // unsubscribes its handler, drops it from CurrentDevices and raises DeviceDisconnectedEvent, which the mod
        // shows as "disconnected"), CurrentDevices.GetByAddress (internal) and BLEBaseDevice.Disconnect (public).
        // An address no sensor can have keeps the type from reconnecting until the user connects one again.
        private const ulong BlockedAddress = 0xFFFFFFFFFFFFUL;
        private readonly MethodInfo bleDisconnectDevice, bleGetByAddress;
        private readonly bool devicesDisconnect;

        // P6
        private readonly FieldInfo workoutLib;
        private readonly MethodInfo getWorkouts;
        private readonly PropertyInfo userFtp;
        private readonly bool workoutsRead;
        private object builtFrom;
        private float builtFtp = -1;
        private int building;
        private Dictionary<string, object> pendingWorkouts;
        private bool workoutsReady;

        // P13: select a workout like the PC selector's Select button (Menus.cs:522 plan item -> TrainingPlan setter;
        // WorkoutSelectionView.SelectWorkoutButton_OnRelease -> OnWorkoutSelected event -> GTBikeVScript handler).
        private readonly FieldInfo workoutView, viewSelectedEvent;
        private readonly PropertyInfo viewPlan, viewItems, viewIndex;
        private readonly Type modScript;
        private readonly MethodInfo removeWorkoutBot; // BotGang.RemoveWorkoutBot: the native replacement leaves the old bot riding.
        private readonly bool workoutsSelect;
        private object lastWorkout;     // Activity.Workout seen last Tick
        private long workoutGeneration; // +1 each time the activity gets a different Workout object (a new selection)
        private Dictionary<string, string[]> workoutKeys; // workout id -> { plan directory, index in GetWorkouts, name }

        /// <summary>Receives exceptions raised by work run on the mod's own Tick.</summary>
        public Action<Exception> DeferredError;

        // P6 progress + P7
        private readonly PropertyInfo currentActivity, activityDistance, activityFileName;
        private readonly FieldInfo activityWorkout, activitiesPath, saveItem;
        private readonly bool activitiesRead, activitiesWrite;
        private object lastActivity;
        private object lastGeneration;
        private Dictionary<string, object> lastSaved;
        private string reportedPath;
        private Dictionary<string, object> pendingPaths;

        public GtbikeV1More(Func<string, Type> typeOf, bool versionMatches)
        {
            writable = versionMatches;
            Type deviceInfo = typeOf("GTBikeV.Devices.DeviceInfo");
            functionType = typeOf("GTBikeV.Devices.DeviceFunctionType");
            actionType = typeOf("GTBikeV.Devices.DeviceSelectionAction");
            selectionConfigType = typeOf("GTBikeV.Devices.DeviceSelectionConfig");
            Type menus = typeOf("GTBikeV.UI.Menus");
            Type dt = typeOf("GTBikeV.DataTransceiver");
            if (deviceInfo != null && functionType != null)
            {
                Type[] fn = new Type[] { functionType };
                currentDevice = deviceInfo.GetMethod("GetCurrentDeviceFor", S, null, fn, null);
                isAuto = deviceInfo.GetMethod("IsDeviceFunctionAuto", S, null, fn, null);
                isLocked = deviceInfo.GetMethod("IsDeviceFunctionLocked", S, null, fn, null);
                isSkippable = deviceInfo.GetMethod("IsDeviceFunctionSkippable", S, null, fn, null);
                deviceConfig = deviceInfo.GetField("_deviceInfoConfig", S);
            }
            Need(currentDevice, "DeviceInfo.GetCurrentDeviceFor");
            Need(isAuto, "DeviceInfo.IsDeviceFunctionAuto");
            Need(isLocked, "DeviceInfo.IsDeviceFunctionLocked");
            Need(isSkippable, "DeviceInfo.IsDeviceFunctionSkippable");
            Need(deviceConfig, "DeviceInfo._deviceInfoConfig");
            deviceView = menus == null ? null : menus.GetField("deviceSelectionView", S);
            Need(deviceView, "Menus.deviceSelectionView");
            Need(actionType, "DeviceSelectionAction");
            Need(selectionConfigType, "DeviceSelectionConfig");
            sport = dt == null ? null : dt.GetProperty("Sport", S);
            devicesRead = currentDevice != null && isAuto != null && isLocked != null && isSkippable != null && deviceConfig != null;
            devicesWrite = devicesRead && writable && deviceView != null && actionType != null && selectionConfigType != null
                && deviceView.FieldType.GetField("OnDeviceConfigured", I) != null;

            // P11
            Type iDeviceInfo = typeOf("GTBikeV.Devices.IDeviceInfo");
            Type bleInfo = typeOf("GTBikeV.Devices.BLEDeviceInfo");
            sensorEnum = typeOf("GTBikeV.Devices.SensorDeviceType");
            if (deviceInfo != null && functionType != null && iDeviceInfo != null)
                changeConfig = deviceInfo.GetMethod("ChangeDeviceConfig", S, null, new Type[] { functionType, iDeviceInfo, typeof(bool) }, null);
            if (bleInfo != null)
            {
                foreach (ConstructorInfo c in bleInfo.GetConstructors(I))
                {
                    ParameterInfo[] p = c.GetParameters();
                    if (p.Length == 1 && p[0].ParameterType.IsValueType) { bleInfoCtor = c; bleReceiverInfo = p[0].ParameterType; }
                }
            }
            // BLE_Receiver lives in BLE_Receiver.dll; reach it through the struct the mod's own BLEDeviceInfo wraps.
            Type receiver = bleReceiverInfo == null ? null : bleReceiverInfo.DeclaringType;
            bleAddDevice = receiver == null ? null : receiver.GetMethod("AddDevice", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(byte), typeof(ulong) }, null);
            Need(changeConfig, "DeviceInfo.ChangeDeviceConfig");
            Need(bleInfoCtor, "BLEDeviceInfo(BLEReceiverDeviceInfo)");
            Need(bleAddDevice, "BLE_Receiver.AddDevice(byte, ulong)");
            Need(sensorEnum, "SensorDeviceType");
            bool structOk = bleReceiverInfo != null && bleReceiverInfo.GetField("DeviceAddress") != null && bleReceiverInfo.GetField("DeviceType") != null;
            if (bleReceiverInfo != null && !structOk) missing.Add("BLEReceiverDeviceInfo.DeviceAddress/DeviceType");
            devicesAssign = devicesRead && writable && changeConfig != null && bleInfoCtor != null && bleAddDevice != null && sensorEnum != null && structOk;
            if (receiver != null)
            {
                Type current = receiver.Assembly.GetType("BLEReceiver.CurrentDevices");
                bleGetByAddress = current == null ? null : current.GetMethod("GetByAddress", S, null, new Type[] { typeof(ulong) }, null);
                foreach (MethodInfo m in receiver.GetMethods(S))
                {
                    ParameterInfo[] p = m.GetParameters();
                    if (m.Name == "DisconnectDevice" && p.Length == 2 && p[1].ParameterType == typeof(ulong) && p[0].ParameterType.GetMethod("Disconnect", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) != null)
                        bleDisconnectDevice = m;
                }
            }
            Need(bleGetByAddress, "CurrentDevices.GetByAddress(ulong)");
            Need(bleDisconnectDevice, "BLE_Receiver.DisconnectDevice(BLEBaseDevice, ulong)");
            devicesDisconnect = devicesAssign && bleGetByAddress != null && bleDisconnectDevice != null;

            Type retriever = typeOf("GTBikeV.Workouts.WorkoutRetriever");
            workoutLib = retriever == null ? null : retriever.GetField("WorkoutLib", S);
            getWorkouts = retriever == null ? null : retriever.GetMethod("GetWorkouts", S, null, new Type[] { typeof(string) }, null);
            Type config = typeOf("GTBikeV.Config");
            userFtp = config == null ? null : config.GetProperty("UserFTP", S);
            Need(workoutLib, "WorkoutRetriever.WorkoutLib");
            Need(getWorkouts, "WorkoutRetriever.GetWorkouts");
            workoutsRead = workoutLib != null && getWorkouts != null && userFtp != null;

            workoutView = menus == null ? null : menus.GetField("workoutSelectionView", S);
            Type viewType = workoutView == null ? null : workoutView.FieldType;
            viewPlan = viewType == null ? null : viewType.GetProperty("TrainingPlan", I);
            if (viewPlan != null && !viewPlan.CanWrite) viewPlan = null;
            viewItems = viewType == null ? null : viewType.GetProperty("ItemList", I);
            viewIndex = viewType == null ? null : viewType.GetProperty("Index", I);
            viewSelectedEvent = viewType == null ? null : viewType.GetField("OnWorkoutSelected", I);
            if (viewSelectedEvent != null && !typeof(Delegate).IsAssignableFrom(viewSelectedEvent.FieldType)) viewSelectedEvent = null;
            modScript = typeOf("GTBikeV.GTBikeVScript");
            Type botGang = typeOf("GTBikeV.Network.BotGang");
            removeWorkoutBot = botGang == null ? null : botGang.GetMethod("RemoveWorkoutBot", S, null, Type.EmptyTypes, null);
            Need(removeWorkoutBot, "BotGang.RemoveWorkoutBot");
            Need(workoutView, "Menus.workoutSelectionView");
            Need(viewPlan, "WorkoutSelectionView.TrainingPlan");
            Need(viewItems, "WorkoutSelectionView.ItemList");
            Need(viewIndex, "WorkoutSelectionView.Index");
            Need(viewSelectedEvent, "WorkoutSelectionView.OnWorkoutSelected");
            Need(modScript, "GTBikeVScript");
            workoutsSelect = workoutsRead && writable && viewPlan != null && viewItems != null && viewIndex != null && viewSelectedEvent != null && modScript != null;

            Type multiplayer = typeOf("GTBikeV.Network.Multiplayer");
            Type activity = typeOf("GTBikeV.Activity");
            currentActivity = multiplayer == null ? null : multiplayer.GetProperty("CurrentActivity", S);
            activityDistance = activity == null ? null : activity.GetProperty("TotalDistance", I);
            activityFileName = activity == null ? null : activity.GetProperty("ActivityFileName", I);
            activityWorkout = activity == null ? null : activity.GetField("Workout", I);
            activitiesPath = config == null ? null : config.GetField("UserActivitiesPath", S);
            saveItem = menus == null ? null : menus.GetField("saveActivityMenuItem", S);
            Need(activityDistance, "Activity.TotalDistance");
            Need(activityFileName, "Activity.ActivityFileName");
            Need(activitiesPath, "Config.UserActivitiesPath");
            Need(saveItem, "Menus.saveActivityMenuItem");
            activitiesRead = currentActivity != null && activityDistance != null && activityFileName != null && activitiesPath != null;
            activitiesWrite = activitiesRead && writable && saveItem != null;
        }

        public IList<string> Missing { get { return missing; } }

        public IEnumerable<string> Commands()
        {
            if (devicesWrite) yield return "devices.configure";
            if (devicesAssign) yield return "devices.assign";
            if (devicesDisconnect) yield return "devices.disconnect";
            if (activitiesWrite) yield return "activities.finish";
            if (workoutsSelect) yield return "workouts.select";
        }

        public void Capture(Dictionary<string, object> snap, DateTime nowUtc, bool dtOk)
        {
            if (devicesRead)
            {
                if (!dtOk) lastRoles = null;
                else if (nowUtc >= nextDevicesUtc)
                {
                    nextDevicesUtc = nowUtc.AddSeconds(1); // Native receiver queries: 1 Hz like sensors.
                    try { lastRoles = ReadRoles(); } catch (Exception) { lastRoles = null; }
                }
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["roles"] = lastRoles;
                snap["devices"] = lastRoles == null ? null : d;
            }
            if (workoutsRead)
            {
                try { MaybeBuildWorkouts(); } catch (Exception) { }
                Dictionary<string, object> w = new Dictionary<string, object>();
                w["ready"] = workoutsReady;
                snap["workouts"] = w;
            }
            object act = currentActivity == null ? null : currentActivity.GetValue(null, null);
            object workout = act == null || activityWorkout == null ? null : activityWorkout.GetValue(act);
            // A route load or save re-creates the Activity but keeps the same Workout object; only a selection changes it.
            if (workout != null && !object.ReferenceEquals(workout, lastWorkout)) workoutGeneration++;
            lastWorkout = workout ?? lastWorkout;
            Dictionary<string, object> progress = Progress(workout);
            if (progress != null) progress["generation"] = workoutGeneration;
            snap["workoutProgress"] = progress;
            if (activitiesRead) CaptureActivities(snap, act);
        }

        public Dictionary<string, object> TakeWorkouts() { Dictionary<string, object> w = pendingWorkouts; pendingWorkouts = null; return w; }
        public Dictionary<string, object> TakePaths() { Dictionary<string, object> p = pendingPaths; pendingPaths = null; return p; }

        // ---------------- P5 devices ----------------

        private bool Visible(int role)
        {
            string s = sport == null ? "" : Convert.ToString(sport.GetValue(null, null), CultureInfo.InvariantCulture);
            string name = RoleNames[role];
            if (s == "SPORT_TYPE_CYCLING") return name != "RunningSpeed";
            if (s == "SPORT_TYPE_RUNNING") return name != "Power" && name != "Resistance" && name != "Steering";
            return name == "HeartRate" || name == "Controls" || name == "Cadence";
        }

        private List<object> ReadRoles()
        {
            IDictionary config = deviceConfig.GetValue(null) as IDictionary;
            List<object> roles = new List<object>();
            for (int i = 0; i < RoleNames.Length; i++)
            {
                object fn = Enum.Parse(functionType, RoleNames[i]);
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["role"] = RoleWire[i];
                r["visible"] = Visible(i);
                try
                {
                    r["current"] = Device(currentDevice.Invoke(null, new object[] { fn }), true);
                    r["isAuto"] = (bool)isAuto.Invoke(null, new object[] { fn });
                    r["isLocked"] = (bool)isLocked.Invoke(null, new object[] { fn });
                    r["skippable"] = (bool)isSkippable.Invoke(null, new object[] { fn });
                    List<object> entries = new List<object>();
                    IEnumerable set = config != null && config.Contains(fn) ? config[fn] as IEnumerable : null;
                    if (set != null) foreach (object e in set) { if (entries.Count == 8) break; Dictionary<string, object> d = Device(e, false); if (d != null) { d["excluded"] = object.Equals(Get(e, "Exclude"), true); entries.Add(d); } }
                    r["entries"] = entries;
                }
                catch (Exception) { r["current"] = null; r["isAuto"] = null; r["isLocked"] = true; r["skippable"] = false; r["entries"] = new List<object>(); }
                roles.Add(r);
            }
            return roles;
        }

        private static Dictionary<string, object> Device(object dev, bool needPresent)
        {
            if (dev == null) return null;
            if (needPresent && !object.Equals(Get(dev, "Present"), true)) return null;
            string source = Convert.ToString(Get(dev, "DataSourceType"), CultureInfo.InvariantCulture);
            object id = Get(dev, "DeviceId");
            if ((source != "ANT" && source != "BLE") || !(id is ulong)) return null;
            string type = Convert.ToString(Get(dev, "SensorType"), CultureInfo.InvariantCulture);
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["deviceId"] = ((ulong)id).ToString(CultureInfo.InvariantCulture);
            d["source"] = source;
            d["sensorType"] = SensorTypes.Contains(type) ? type : "unknown";
            // BLEDeviceInfo.Name (advertised name, with the manufacturer when known). ANT devices have none.
            // The getter throws when the device name is missing but the manufacturer is known; a name is optional.
            if (needPresent && source == "BLE")
            {
                string name = null;
                try { name = CleanName(Get(dev, "Name") as string); } catch (Exception) { }
                if (name != null) d["name"] = name;
            }
            return d;
        }

        private static string CleanName(string s)
        {
            if (s == null) return null;
            StringBuilder b = new StringBuilder();
            foreach (char c in s) if (!char.IsControl(c)) b.Append(c);
            string t = b.ToString().Trim();
            if (t.Length > 64) t = t.Substring(0, 64);
            return t.Length == 0 ? null : t;
        }

        /// <summary>devices.configure through the Device Selection view's own event (GTBikeVScript.DeviceConfigHandler).</summary>
        public Dictionary<string, object> ConfigureDevice(Dictionary<string, object> args, bool dtOk)
        {
            if (!devicesWrite) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            if (!dtOk) return Ack("rejected", "MOD_INACTIVE");
            int role = Array.IndexOf(RoleWire, args.ContainsKey("role") ? args["role"] as string : null);
            string action = args.ContainsKey("action") ? args["action"] as string : null;
            if (role < 0 || (action != "auto" && action != "lock" && action != "skip")) return Ack("rejected", "INVALID_REQUEST");
            if (!Visible(role)) return Ack("rejected", "VALUE_NOT_ALLOWED");
            object fn = Enum.Parse(functionType, RoleNames[role]);
            object dev = currentDevice.Invoke(null, new object[] { fn });
            Dictionary<string, object> seen = Device(dev, true);
            if (action != "auto")
            {
                string id = args.ContainsKey("expectedDeviceId") ? args["expectedDeviceId"] as string : null;
                string source = args.ContainsKey("expectedSource") ? args["expectedSource"] as string : null;
                if (seen == null || (string)seen["deviceId"] != id || (string)seen["source"] != source) return Ack("rejected", "DEVICE_CHANGED");
            }
            // Same enablement as the native buttons (DeviceTypeViewWidget Auto/Lock/Skip).
            bool allowed = action == "auto" ? !(bool)isAuto.Invoke(null, new object[] { fn })
                : action == "lock" ? !(bool)isLocked.Invoke(null, new object[] { fn })
                : (bool)isSkippable.Invoke(null, new object[] { fn });
            if (!allowed) return Ack("rejected", "VALUE_NOT_ALLOWED");
            object view = deviceView.GetValue(null);
            Delegate handler = view == null ? null : view.GetType().GetField("OnDeviceConfigured", I).GetValue(view) as Delegate;
            if (handler == null) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            List<byte> lockedBefore = devicesAssign && action == "auto" ? LockedBleTypes(fn) : null;
            object config = Activator.CreateInstance(selectionConfigType, true);
            selectionConfigType.GetProperty("DeviceFunction", I).SetValue(config, fn, null);
            selectionConfigType.GetProperty("DeviceInfo", I).SetValue(config, action == "auto" ? null : dev, null);
            selectionConfigType.GetProperty("Action", I).SetValue(config, Enum.Parse(actionType, action == "auto" ? "Auto" : action == "lock" ? "Lock" : "Skip"), null);
            handler.DynamicInvoke(config);
            // Native Auto clears the rule but leaves the receiver bound to the old address until the mod restarts.
            if (lockedBefore != null) ReleaseExpected(fn, lockedBefore, -1);
            nextDevicesUtc = DateTime.MinValue;
            return Ack("dispatched", null);
        }

        // ---------------- P11 BLE device chosen on the phone ----------------

        /// <summary>
        /// devices.assign: locks a BLE sensor the phone saw advertising for one role, through the mod's own
        /// ChangeDeviceConfig (same INI rule the Lock button writes), then tells the running BLE receiver to accept
        /// only that address for the sensor type. The PC connects to the sensor directly; no data goes through the phone.
        /// </summary>
        public Dictionary<string, object> AssignDevice(Dictionary<string, object> args, bool dtOk)
        {
            if (!devicesAssign) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            if (!dtOk) return Ack("rejected", "MOD_INACTIVE");
            if (args.Count != 3) return Ack("rejected", "INVALID_REQUEST");
            int role = Array.IndexOf(RoleWire, args.ContainsKey("role") ? args["role"] as string : null);
            string type = args.ContainsKey("sensorType") ? args["sensorType"] as string : null;
            string idText = args.ContainsKey("deviceId") ? args["deviceId"] as string : null;
            ulong id;
            if (role < 0 || Array.IndexOf(AssignTypes, type) < 0 || idText == null
                || !ulong.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == 0 || id > MaxBleAddress)
                return Ack("rejected", "INVALID_REQUEST");
            if (!Visible(role) || Array.IndexOf(RoleTypes[role], type) < 0) return Ack("rejected", "VALUE_NOT_ALLOWED");
            object fn = Enum.Parse(functionType, RoleNames[role]);
            byte typeByte = Convert.ToByte(Enum.Parse(sensorEnum, type), CultureInfo.InvariantCulture);
            object device = BleDevice(typeByte, id);

            List<byte> lockedBefore = LockedBleTypes(fn);
            changeConfig.Invoke(null, new object[] { fn, null, true });   // native Auto: clears the rule and type filters
            changeConfig.Invoke(null, new object[] { fn, device, true }); // native Lock with the chosen device
            ReleaseExpected(fn, lockedBefore, typeByte);
            bleAddDevice.Invoke(null, new object[] { typeByte, id });     // live: that type now connects only to this address
            nextDevicesUtc = DateTime.MinValue;
            return Ack("dispatched", null);
        }

        /// <summary>
        /// devices.disconnect: the PC drops a BLE sensor it is using now. Roles locked to that address go back to the
        /// mod's own Auto, the receiver stops accepting that sensor type (until the user connects one from the phone or
        /// the mod restarts), and the link is closed the way the receiver handles a lost device.
        /// </summary>
        public Dictionary<string, object> DisconnectDevice(Dictionary<string, object> args, bool dtOk)
        {
            if (!devicesDisconnect) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            if (!dtOk) return Ack("rejected", "MOD_INACTIVE");
            if (args.Count != 2) return Ack("rejected", "INVALID_REQUEST");
            string type = args.ContainsKey("sensorType") ? args["sensorType"] as string : null;
            string idText = args.ContainsKey("deviceId") ? args["deviceId"] as string : null;
            ulong id;
            if (Array.IndexOf(AssignTypes, type) < 0 || idText == null
                || !ulong.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == 0 || id >= MaxBleAddress)
                return Ack("rejected", "INVALID_REQUEST");
            // Only a sensor the PC uses right now, with the type the phone saw.
            bool inUse = false;
            foreach (string role in RoleNames)
            {
                Dictionary<string, object> seen = Device(currentDevice.Invoke(null, new object[] { Enum.Parse(functionType, role) }), true);
                if (seen != null && (string)seen["source"] == "BLE" && (string)seen["deviceId"] == idText && (string)seen["sensorType"] == type) inUse = true;
            }
            if (!inUse) return Ack("rejected", "DEVICE_CHANGED");
            foreach (object fn in Enum.GetValues(functionType))
                if (LockedBleAddresses(fn).Contains(id)) changeConfig.Invoke(null, new object[] { fn, null, true }); // native Auto
            byte typeByte = Convert.ToByte(Enum.Parse(sensorEnum, type), CultureInfo.InvariantCulture);
            bleAddDevice.Invoke(null, new object[] { typeByte, BlockedAddress });
            object device = bleGetByAddress.Invoke(null, new object[] { id });
            if (device != null)
            {
                // Bookkeeping first: it unsubscribes the receiver's own lost-device handler, so the event fires once.
                bleDisconnectDevice.Invoke(null, new object[] { device, id });
                device.GetType().GetMethod("Disconnect", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null).Invoke(device, null); // async; not awaited on the game thread
            }
            nextDevicesUtc = DateTime.MinValue;
            return Ack("dispatched", null);
        }

        private List<ulong> LockedBleAddresses(object fn)
        {
            List<ulong> ids = new List<ulong>();
            IDictionary config = deviceConfig.GetValue(null) as IDictionary;
            IEnumerable set = config != null && config.Contains(fn) ? config[fn] as IEnumerable : null;
            if (set == null) return ids;
            foreach (object e in set)
            {
                if (object.Equals(Get(e, "Exclude"), true)) continue;
                if (Convert.ToString(Get(e, "DataSourceType"), CultureInfo.InvariantCulture) != "BLE") continue;
                object v = Get(e, "DeviceId");
                if (v is ulong) ids.Add((ulong)v);
            }
            return ids;
        }

        /// <summary>The mod's own BLEDeviceInfo around a receiver record with that address (little endian, 6 bytes).</summary>
        private object BleDevice(byte typeByte, ulong id)
        {
            object record = Activator.CreateInstance(bleReceiverInfo);
            byte[] address = new byte[6];
            Array.Copy(BitConverter.GetBytes(id), address, 6);
            bleReceiverInfo.GetField("DeviceAddress").SetValue(record, address);
            bleReceiverInfo.GetField("DeviceType").SetValue(record, typeByte);
            return bleInfoCtor.Invoke(new object[] { record });
        }

        /// <summary>Offline check only (tests/BindingResolveCheck.cs): the IDeviceInfo AssignDevice would hand to the mod.</summary>
        public object BleDeviceForCheck(string type, ulong id)
        {
            if (!devicesAssign || Array.IndexOf(AssignTypes, type) < 0) return null;
            return BleDevice(Convert.ToByte(Enum.Parse(sensorEnum, type), CultureInfo.InvariantCulture), id);
        }

        /// <summary>Sensor types of the BLE devices locked (not excluded) for one role.</summary>
        private List<byte> LockedBleTypes(object fn)
        {
            List<byte> types = new List<byte>();
            IDictionary config = deviceConfig.GetValue(null) as IDictionary;
            IEnumerable set = config != null && config.Contains(fn) ? config[fn] as IEnumerable : null;
            if (set == null) return types;
            foreach (object e in set)
            {
                if (object.Equals(Get(e, "Exclude"), true)) continue;
                if (Convert.ToString(Get(e, "DataSourceType"), CultureInfo.InvariantCulture) != "BLE") continue;
                object t = Get(e, "SensorType");
                if (t != null) types.Add(Convert.ToByte(t, CultureInfo.InvariantCulture));
            }
            return types;
        }

        /// <summary>Returns a sensor type to "any address" when no role locks it any more (what a restart would do).</summary>
        private void ReleaseExpected(object fn, List<byte> before, int keep)
        {
            foreach (byte t in before)
            {
                if (t == keep) continue;
                bool used = false;
                foreach (object other in Enum.GetValues(functionType)) if (!object.Equals(other, fn) && LockedBleTypes(other).Contains(t)) used = true;
                if (!used) bleAddDevice.Invoke(null, new object[] { t, 0UL });
            }
        }

        // ---------------- P6 workouts (read only) ----------------

        /// <summary>Parses every plan once per library/FTP change on a worker thread (the mod's XML parser is slow on first use).</summary>
        private void MaybeBuildWorkouts()
        {
            object lib = workoutLib.GetValue(null);
            if (lib == null || !object.Equals(Get(lib, "Ready"), true)) { workoutsReady = false; return; }
            float ftp = (float)userFtp.GetValue(null, null);
            if (object.ReferenceEquals(lib, builtFrom) && ftp == builtFtp) return;
            if (Interlocked.CompareExchange(ref building, 1, 0) != 0) return;
            workoutsReady = false;
            object libRef = lib;
            Task.Run(delegate
            {
                try
                {
                    Dictionary<string, string[]> keys = new Dictionary<string, string[]>();
                    Dictionary<string, object> doc = BuildWorkouts(libRef, ftp, keys);
                    workoutKeys = keys;
                    pendingWorkouts = doc;
                    builtFrom = libRef;
                    builtFtp = ftp;
                    workoutsReady = true;
                }
                catch (Exception) { builtFrom = libRef; builtFtp = ftp; }
                finally { Interlocked.Exchange(ref building, 0); }
            });
        }

        private Dictionary<string, object> BuildWorkouts(object lib, float ftp, Dictionary<string, string[]> keys)
        {
            List<object> plans = new List<object>();
            StringBuilder key = new StringBuilder();
            IEnumerable list = Get(lib, "PlanList") as IEnumerable;
            if (list != null)
            {
                foreach (object plan in list)
                {
                    if (plans.Count == 200) break;
                    if (object.Equals(Get(plan, "Hide"), true)) continue;
                    string dir = Get(plan, "DirectoryName") as string;
                    if (string.IsNullOrEmpty(dir)) continue;
                    Dictionary<string, object> p = new Dictionary<string, object>();
                    p["id"] = "p-" + Hash(dir);
                    p["name"] = Text(Get(plan, "Name"), 120) ?? dir;
                    p["description"] = Text(Get(plan, "Description"), 1200);
                    List<object> workouts = new List<object>();
                    IEnumerable parsed = null;
                    try { parsed = getWorkouts.Invoke(null, new object[] { dir }) as IEnumerable; } catch (Exception) { }
                    int index = 0;
                    if (parsed != null) foreach (object w in parsed)
                    {
                        if (workouts.Count == 1000) break;
                        Dictionary<string, object> m = WorkoutDoc(w, dir, index);
                        if (m != null) { workouts.Add(m); keys[(string)m["id"]] = new string[] { dir, index.ToString(CultureInfo.InvariantCulture), (string)m["name"] }; }
                        index++;
                    }
                    p["workouts"] = workouts;
                    plans.Add(p);
                    key.Append(dir).Append(':').Append(workouts.Count).Append('|');
                }
            }
            Dictionary<string, object> doc = new Dictionary<string, object>();
            doc["catalogRevision"] = "workouts-" + Hash(key.ToString() + ftp.ToString(CultureInfo.InvariantCulture)).Substring(0, 12);
            doc["ftpReferenceW"] = Math.Round((double)ftp, 1);
            doc["plans"] = plans;
            return doc;
        }

        private Dictionary<string, object> WorkoutDoc(object w, string dir, int index)
        {
            string name = Text(Get(w, "Name"), 120);
            if (name == null) return null;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["id"] = "w-" + Hash(dir + "|" + index + "|" + name);
            d["name"] = name;
            d["author"] = Text(Get(w, "Author"), 120);
            d["description"] = Text(Get(w, "Description"), 2000);
            bool time = !object.Equals(Get(w, "TimeDuration"), false);
            d["durationType"] = time ? "time" : "distance";
            List<object> segments = new List<object>();
            IEnumerable list = Get(w, "SegmentList") as IEnumerable;
            long total = 0;
            if (list != null) foreach (object s in list) { if (segments.Count == 2000) break; Dictionary<string, object> seg = Segment(s, segments.Count); if (seg != null) { segments.Add(seg); total = Math.Max(total, Convert.ToInt64(seg["end"], CultureInfo.InvariantCulture)); } }
            d["total"] = total;
            d["segments"] = segments;
            return d;
        }

        private static Dictionary<string, object> Segment(object s, int index)
        {
            if (s == null) return null;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["index"] = index;
            d["type"] = Convert.ToString(Get(s, "Type"), CultureInfo.InvariantCulture);
            d["start"] = Convert.ToInt64(Get(s, "StartDurationValue") ?? 0L, CultureInfo.InvariantCulture);
            d["end"] = Convert.ToInt64(Get(s, "EndDurationValue") ?? 0L, CultureInfo.InvariantCulture);
            // Reference watts the mod computed from FTP at parse time; information only.
            d["startPowerW"] = Convert.ToInt32(Get(s, "StartTargetPower") ?? 0, CultureInfo.InvariantCulture);
            d["endPowerW"] = Convert.ToInt32(Get(s, "EndTargetPower") ?? 0, CultureInfo.InvariantCulture);
            int cad = Convert.ToInt32(Get(s, "TargetCadence") ?? 0, CultureInfo.InvariantCulture);
            d["cadenceRpm"] = cad > 0 && cad < 255 ? (object)cad : null;
            return d;
        }

        private static Dictionary<string, object> Progress(object w)
        {
            if (w == null) return null;
            Dictionary<string, object> p = new Dictionary<string, object>();
            p["name"] = Text(Get(w, "Name"), 120);
            p["started"] = Get(w, "Started");
            p["inCountdown"] = Get(w, "InCountdown");
            p["finished"] = Get(w, "Finished");
            p["durationType"] = object.Equals(Get(w, "TimeDuration"), false) ? "distance" : "time";
            p["elapsed"] = Get(w, "ElapsedDuration");
            object total = null;
            try { total = Get(w, "TotalDuration"); } catch (Exception) { } // Throws on a workout without segments.
            p["total"] = total;
            p["segmentCount"] = Get(w, "SegmentCount");
            object seg = null;
            try { seg = Get(w, "CurrentSegment"); } catch (Exception) { }
            Dictionary<string, object> segment = Segment(seg, 0);
            if (segment != null) segment["index"] = Math.Max(0, Convert.ToInt32(Get(seg, "Order") ?? 0, CultureInfo.InvariantCulture));
            p["segment"] = segment;
            return p;
        }

        /// <summary>
        /// workouts.select (P13): same path as choosing the plan in the mod menu and pressing Select in its workout
        /// selector. The plan is loaded into the selector with the current FTP (TrainingPlan setter, as Menus.cs:525),
        /// the selector points at the workout, and its OnWorkoutSelected event is raised on the mod's own Tick, where the
        /// mod's handler selects it (and engages ERG itself). The selector is not opened, so no menu pops up on the PC.
        /// With a workout still running this replaces it, as the mod's menu does (its handler unhooks the old workout and
        /// resets the new one; ERG stays on, the activity goes on, nothing is saved). The native path leaves the old
        /// workout bot riding on a route, so that bot is retired first.
        /// </summary>
        public Dictionary<string, object> StartWorkout(Dictionary<string, object> args, bool dtOk, bool replacing, string gameControl)
        {
            if (!workoutsSelect) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            object value;
            string id = args.Count == 1 && args.TryGetValue("workoutId", out value) ? value as string : null;
            if (id == null) return Ack("rejected", "INVALID_REQUEST");
            if (!dtOk) return Ack("rejected", "MOD_INACTIVE");
            if (gameControl != "inControl") return Ack("rejected", "GAME_NOT_IN_CONTROL");
            Dictionary<string, string[]> keys = workoutKeys;
            string[] key;
            if (keys == null || !keys.TryGetValue(id, out key)) return Ack("rejected", "NOT_FOUND");

            object lib = workoutLib.GetValue(null);
            object plan = null;
            IEnumerable planList = lib == null ? null : Get(lib, "PlanList") as IEnumerable;
            if (planList != null) foreach (object p in planList) if (Get(p, "DirectoryName") as string == key[0]) { plan = p; break; }
            if (plan == null) return Ack("rejected", "NOT_FOUND");

            object view = workoutView.GetValue(null);
            Delegate handlers = view == null ? null : viewSelectedEvent.GetValue(view) as Delegate;
            object script = null;
            if (handlers != null)
                foreach (Delegate h in handlers.GetInvocationList())
                    if (h.Target != null && modScript.IsInstanceOfType(h.Target)) { script = h.Target; break; }
            if (script == null) return Ack("rejected", "CAPABILITY_UNAVAILABLE");

            viewPlan.SetValue(view, plan, null); // Parses the plan with the current FTP, as the native plan item does.
            IList items = viewItems.GetValue(view, null) as IList;
            int index = int.Parse(key[1], CultureInfo.InvariantCulture);
            if (items == null || index >= items.Count || Text(Get(items[index], "Name"), 120) != key[2]) return Ack("rejected", "NOT_FOUND");
            object workout = items[index];
            viewIndex.SetValue(view, index, null);

            bool scheduled = GtbikeV1Extensions.RunOnTick(script, delegate
            {
                if (replacing && removeWorkoutBot != null) removeWorkoutBot.Invoke(null, null);
                Delegate selected = viewSelectedEvent.GetValue(view) as Delegate;
                if (selected != null) selected.DynamicInvoke(view, workout);
            }, DeferredError);
            return scheduled ? Ack("dispatched", null) : Ack("rejected", "CAPABILITY_UNAVAILABLE");
        }

        // ---------------- P7 activities ----------------

        private void CaptureActivities(Dictionary<string, object> snap, object act)
        {
            string path = activitiesPath.GetValue(null) as string;
            if (path != null && path != reportedPath)
            {
                reportedPath = path;
                Dictionary<string, object> paths = new Dictionary<string, object>();
                paths["activitiesPath"] = path; // Local file for the bridge only; never part of the phone contract.
                pendingPaths = paths;
            }
            object generation = snap.ContainsKey("sessionGeneration") ? snap["sessionGeneration"] : null;
            if (!object.ReferenceEquals(act, lastActivity))
            {
                // The previous Activity was replaced: SaveCurrentActivity, route load or deactivation encoded it (or not).
                if (lastActivity != null && lastGeneration != null)
                {
                    Dictionary<string, object> saved = new Dictionary<string, object>();
                    saved["fromSessionGeneration"] = lastGeneration;
                    saved["fileName"] = Text(activityFileName.GetValue(lastActivity, null), 120);
                    lastSaved = saved;
                }
                lastActivity = act;
            }
            lastGeneration = generation;
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["saveEnabled"] = saveItem != null && object.Equals(Get(saveItem.GetValue(null), "Enabled"), true);
            a["activityDistanceM"] = act == null ? null : (object)Math.Round((double)(float)activityDistance.GetValue(act, null), 1);
            a["lastSaved"] = lastSaved;
            snap["activities"] = a;
        }

        /// <summary>activities.finish: same path as the native Save menu item (SaveActivityHandler -> SaveCurrentActivity).</summary>
        public Dictionary<string, object> Finish(string expectedRevision, bool dtOk, bool workoutBlocks, double? speedMps, string gameControl)
        {
            if (!activitiesWrite) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            if (!dtOk) return Ack("rejected", "MOD_INACTIVE");
            if (gameControl != "inControl") return Ack("rejected", "GAME_NOT_IN_CONTROL");
            object act = currentActivity.GetValue(null, null);
            if (act == null) return Ack("rejected", "SAVE_TOO_SHORT");
            if (lastGeneration == null || expectedRevision != "session-" + Convert.ToString(lastGeneration, CultureInfo.InvariantCulture)) return Ack("rejected", "REVISION_CONFLICT");
            // SaveCurrentActivity -> CreateNewActivity re-selects an unfinished workout and re-engages ERG.
            if (workoutBlocks) return Ack("rejected", "WORKOUT_PC_CONTROL_REQUIRED");
            if (!((float)activityDistance.GetValue(act, null) > 100f)) return Ack("rejected", "SAVE_TOO_SHORT");
            if (speedMps.HasValue && speedMps.Value >= 0.5) return Ack("rejected", "RIDER_MOVING");
            object item = saveItem.GetValue(null);
            if (!object.Equals(Get(item, "Enabled"), true)) return Ack("rejected", "MOD_INACTIVE");
            MethodInfo onActivated = item.GetType().GetMethod("OnActivated", I, null, new Type[] { typeof(object) }, null);
            if (onActivated == null) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            onActivated.Invoke(item, new object[] { item });
            return Ack("dispatched", null);
        }

        // ---------------- helpers ----------------

        private void Need(object member, string name) { if (member == null) missing.Add(name); }

        private static object Get(object target, string name)
        {
            if (target == null) return null;
            FieldInfo f = target.GetType().GetField(name, I);
            if (f != null) return f.GetValue(target);
            PropertyInfo p = target.GetType().GetProperty(name, I);
            return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(target, null) : null;
        }

        private static string Text(object value, int max)
        {
            string s = value as string;
            if (s == null) return null;
            s = s.Trim();
            if (s.Length == 0) return null;
            return s.Length > max ? s.Substring(0, max) : s;
        }

        private static string Hash(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(h, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        private static Dictionary<string, object> Ack(string status, string reasonCode)
        {
            Dictionary<string, object> ack = new Dictionary<string, object>();
            ack["status"] = status;
            ack["reasonCode"] = reasonCode;
            return ack;
        }
    }
}
