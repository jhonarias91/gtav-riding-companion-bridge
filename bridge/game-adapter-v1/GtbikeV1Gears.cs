// Native VirtualGearBox only. Never raises GearUpKey/GearDownKey (those also change workout bias/ERG).
// C# 5; reflection shapes are restricted to the GTBikeV 0.8.0.4 profile.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1Gears
    {
        private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly object script;
        private readonly PropertyInfo boxProperty, dtReady, modeProperty, enabledProperty, gearProperty, playerControl;
        private readonly FieldInfo ratiosField;
        private readonly MethodInfo up, down;
        private readonly EventInfo tick;
        private volatile bool busy;
        private DateTime expires;
        private string pendingId;
        private Dictionary<string, object> result;

        public GtbikeV1Gears(Func<string, Type> typeOf, bool supported, object modScript)
        {
            script = modScript;
            Type dt = typeOf("GTBikeV.DataTransceiver"), box = typeOf("GTBikeV.VirtualGearBox");
            if (!supported || dt == null || box == null || script == null) return;
            boxProperty = dt.GetProperty("VirtualGearBox", S);
            dtReady = dt.GetProperty("IsDTOK", S);
            modeProperty = box.GetProperty("CurrentGearingType", I);
            enabledProperty = box.GetProperty("Enabled", I);
            gearProperty = box.GetProperty("GearIndex", I);
            ratiosField = box.GetField("_gearRatioArray", I);
            up = box.GetMethod("GearUp", I, null, new Type[] { typeof(bool) }, null);
            down = box.GetMethod("GearDown", I, null, new Type[] { typeof(bool) }, null);
            playerControl = script.GetType().GetProperty("IsPlayerInControl", I);
            tick = script.GetType().GetEvent("Tick", BindingFlags.Public | BindingFlags.Instance);
        }
        public bool Supported
        {
            get { return boxProperty != null && dtReady != null && dtReady.PropertyType == typeof(bool)
                && modeProperty != null && modeProperty.PropertyType.IsEnum && enabledProperty != null && enabledProperty.PropertyType == typeof(bool)
                && gearProperty != null && gearProperty.PropertyType == typeof(byte) && ratiosField != null && ratiosField.FieldType == typeof(float[])
                && up != null && up.ReturnType == typeof(bool) && down != null && down.ReturnType == typeof(bool)
                && playerControl != null && playerControl.PropertyType == typeof(bool) && tick != null; }
        }

        public Dictionary<string, object> Capture(string control)
        {
            if (busy && DateTime.UtcNow > expires) Complete(pendingId, "failed", "OPERATION_UNKNOWN", null);
            Dictionary<string, object> value = Read();
            string reason = !Supported ? "UNSUPPORTED_VERSION" : value == null ? "CAPABILITY_UNAVAILABLE" : null;
            if (value == null) value = new Dictionary<string, object>();
            value["reasonCode"] = reason;
            value["shiftReasonCode"] = reason ?? (!(bool)dtReady.GetValue(null, null) ? "MOD_INACTIVE" : control != "inControl" ? "GAME_NOT_IN_CONTROL"
                : !object.Equals(value["enabled"], true) ? "GEARS_DISABLED" : null);
            value["busy"] = busy;
            value["command"] = result;
            return value;
        }

        public Dictionary<string, object> Command(string type, string id, Dictionary<string, object> args, string control)
        {
            if ((type != "gears.shiftUp" && type != "gears.shiftDown") || args.Count != 0) return Ack("rejected", "INVALID_REQUEST");
            Dictionary<string, object> current = Capture(control);
            string reason = current["shiftReasonCode"] as string;
            if (reason != null) return Ack("rejected", reason);
            if (busy) return Ack("rejected", "BRIDGE_BUSY");
            bool shiftingUp = type == "gears.shiftUp";
            int before = (int)current["gear"];
            if (shiftingUp ? before >= (int)current["gearCount"] : before <= 1) return Ack("rejected", "GEAR_LIMIT");
            pendingId = id;
            expires = DateTime.UtcNow.AddSeconds(4);
            busy = true;
            int ran = 0;
            EventHandler once = null;
            once = delegate(object sender, EventArgs e)
            {
                if (Interlocked.Exchange(ref ran, 1) != 0) return;
                try { tick.RemoveEventHandler(script, once); } catch (Exception) { }
                if (!busy || pendingId != id) return;
                try
                {
                    if (DateTime.UtcNow > expires) { Complete(id, "failed", "OPERATION_UNKNOWN", null); return; }
                    if (!(bool)dtReady.GetValue(null, null)) { Complete(id, "failed", "MOD_INACTIVE", null); return; }
                    if (!(bool)playerControl.GetValue(script, null)) { Complete(id, "failed", "GAME_NOT_IN_CONTROL", null); return; }
                    object box = boxProperty.GetValue(null, null);
                    if (!(bool)enabledProperty.GetValue(box, null)) { Complete(id, "failed", "GEARS_DISABLED", null); return; }
                    bool changed = (bool)(shiftingUp ? up : down).Invoke(box, new object[] { true });
                    Complete(id, changed ? "done" : "failed", changed ? null : "GEAR_LIMIT", Read());
                }
                catch (Exception) { Complete(id, "failed", "INTERNAL", null); }
            };
            try { tick.AddEventHandler(script, once); }
            catch (Exception) { busy = false; pendingId = null; return Ack("rejected", "CAPABILITY_UNAVAILABLE"); }
            return Ack("dispatched", null);
        }

        private Dictionary<string, object> Read()
        {
            if (!Supported) return null;
            object box = boxProperty.GetValue(null, null);
            if (box == null) return null;
            float[] ratios = (float[])ratiosField.GetValue(box);
            int index = (byte)gearProperty.GetValue(box, null);
            if (ratios == null || ratios.Length < 1 || ratios.Length > 255 || index >= ratios.Length) return null;
            float ratio = ratios[index];
            if (Single.IsNaN(ratio) || Single.IsInfinity(ratio) || ratio <= 0f) return null;
            string mode = Convert.ToString(modeProperty.GetValue(box, null));
            mode = mode == "TempManual" ? "tempManual" : mode.ToLowerInvariant();
            if (mode != "none" && mode != "manual" && mode != "auto" && mode != "tempManual" && mode != "native") return null;
            return new Dictionary<string, object> { { "enabled", (bool)enabledProperty.GetValue(box, null) }, { "mode", mode },
                { "gear", index + 1 }, { "gearCount", ratios.Length }, { "ratio", Math.Round((double)ratio, 3) } };
        }
        private void Complete(string id, string status, string reason, Dictionary<string, object> observed)
        {
            result = new Dictionary<string, object> { { "requestId", id }, { "status", status }, { "reasonCode", reason },
                { "gear", observed == null ? null : observed["gear"] }, { "mode", observed == null ? null : observed["mode"] } };
            busy = false;
            pendingId = null;
        }
        private static Dictionary<string, object> Ack(string status, string reason) { return new Dictionary<string, object> { { "status", status }, { "reasonCode", reason } }; }
    }
}
