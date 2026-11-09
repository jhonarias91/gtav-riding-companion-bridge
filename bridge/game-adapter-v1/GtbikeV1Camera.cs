// Companion camera commands use GTA's gameplay camera, never a scripted camera or riding controls. C# 5.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1CameraReading
    {
        public string Reason, Mode;
        public float Heading, Pitch;
    }

    public interface IGtbikeV1CameraGame
    {
        GtbikeV1CameraReading Read();
        void SetMode(string mode);
        void Next();
        void Look(float heading, float pitch);
    }

    /// <summary>A finite, frame-smoothed job. Phone disconnects cannot leave a key or control held.</summary>
    public sealed class GtbikeV1Camera
    {
        public static readonly string[] Modes = { "near", "medium", "far", "firstPerson", "cinematic" };
        private readonly IGtbikeV1CameraGame game;
        private Func<bool?> modLook;
        private string contextReason = "MOD_INACTIVE";
        private DateTime contextAt;
        private string requestId, command, wantedMode, previousMode;
        private float fromHeading, fromPitch, targetHeading, targetPitch;
        private DateTime started;
        private bool sent;
        private Dictionary<string, object> result;

        public GtbikeV1Camera(IGtbikeV1CameraGame game) { this.game = game; }

        public void Context(bool active, string control, Func<bool?> lookBehind, DateTime now)
        {
            contextReason = !active ? "MOD_INACTIVE" : control != "inControl" ? "GAME_NOT_IN_CONTROL" : null;
            modLook = lookBehind;
            contextAt = now;
        }

        private string Reason(GtbikeV1CameraReading reading, DateTime now)
        {
            return now - contextAt > TimeSpan.FromSeconds(1) ? "ADAPTER_STALE" : contextReason ?? reading.Reason;
        }

        private string LookReason(GtbikeV1CameraReading reading)
        {
            bool? forced = modLook == null ? null : modLook();
            if (!forced.HasValue) return "CAPABILITY_UNAVAILABLE";
            if (forced.Value) return "CAMERA_CONTROL_IN_USE";
            // The mod/game own first-person and cinematic orientation. Only expose proven orbit control.
            return reading.Mode == "near" || reading.Mode == "medium" || reading.Mode == "far" ? null : "CAPABILITY_UNAVAILABLE";
        }

        public Dictionary<string, object> Capture(DateTime now)
        {
            GtbikeV1CameraReading r = game.Read();
            string reason = Reason(r, now);
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["reasonCode"] = reason;
            d["currentMode"] = reason == null ? r.Mode : null;
            d["modes"] = reason == null ? Modes : new string[0];
            d["headingDeg"] = reason == null ? (object)Math.Round(r.Heading, 1) : null;
            d["pitchDeg"] = reason == null ? (object)Math.Round(r.Pitch, 1) : null;
            d["lookReasonCode"] = reason ?? LookReason(r);
            d["busy"] = requestId != null;
            d["command"] = result;
            return d;
        }

        public Dictionary<string, object> Command(string type, string id, Dictionary<string, object> args, DateTime now)
        {
            GtbikeV1CameraReading r = game.Read();
            string reason = Reason(r, now);
            if (reason != null) return Ack("rejected", reason);
            if (requestId != null) return Ack("rejected", "BRIDGE_BUSY");
            object value;
            if (type == "camera.setMode")
            {
                if (args.Count != 1 || !args.TryGetValue("mode", out value) || !(value is string) || Array.IndexOf(Modes, (string)value) < 0)
                    return Ack("rejected", "VALUE_NOT_ALLOWED");
                wantedMode = (string)value;
            }
            else if (type == "camera.look")
            {
                reason = LookReason(r);
                if (reason != null) return Ack("rejected", reason);
                if (args.Count != 2 || !Number(args, "headingDeg", -180, 180, out targetHeading) || !Number(args, "pitchDeg", -45, 45, out targetPitch))
                    return Ack("rejected", "VALUE_OUT_OF_RANGE");
                fromHeading = r.Heading; fromPitch = r.Pitch;
            }
            else if (type != "camera.next" || args.Count != 0) return Ack("rejected", "INVALID_REQUEST");
            requestId = id; command = type; previousMode = r.Mode; started = now; sent = false;
            result = new Dictionary<string, object> { { "requestId", id }, { "status", "running" } };
            return Ack("dispatched", null);
        }

        public void Frame(DateTime now)
        {
            if (requestId == null) return;
            GtbikeV1CameraReading r = game.Read();
            string reason = Reason(r, now);
            if (reason == null && command == "camera.look") reason = r.Mode != previousMode ? "CAMERA_CONTROL_IN_USE" : LookReason(r);
            if (reason != null) { Finish(r, reason); return; }
            double elapsed = (now - started).TotalMilliseconds;
            if (elapsed > 2000) { Finish(r, "CAPABILITY_UNAVAILABLE"); return; }
            if (command == "camera.look")
            {
                if (sent && elapsed >= 450 && Math.Abs(Angle(targetHeading - r.Heading)) <= 3 && Math.Abs(targetPitch - r.Pitch) <= 3)
                { Finish(r, null); return; }
                double t = Math.Max(0, Math.Min(1, elapsed / 450));
                t = t * t * (3 - 2 * t);
                game.Look(Angle(fromHeading + Angle(targetHeading - fromHeading) * (float)t), fromPitch + (targetPitch - fromPitch) * (float)t);
                sent = true;
            }
            else if (!sent)
            {
                // Next() is exactly the game-control pulse used by GTBike V's NextCamKey handler.
                if (command == "camera.next") game.Next(); else game.SetMode(wantedMode);
                sent = true;
            }
            else if (command == "camera.next" ? r.Mode != previousMode : r.Mode == wantedMode) Finish(r, null);
        }

        private void Finish(GtbikeV1CameraReading r, string reason)
        {
            result = new Dictionary<string, object> { { "requestId", requestId }, { "status", reason == null ? "done" : "failed" },
                { "reasonCode", reason }, { "mode", r.Mode }, { "headingDeg", Math.Round(r.Heading, 1) }, { "pitchDeg", Math.Round(r.Pitch, 1) } };
            requestId = null;
        }

        public void Abort() { requestId = null; contextReason = "CAPABILITY_UNAVAILABLE"; }
        internal static float Angle(float degrees) { return ((degrees + 180) % 360 + 360) % 360 - 180; }
        private static bool Number(Dictionary<string, object> args, string key, float min, float max, out float number)
        {
            object v; number = 0;
            if (!args.TryGetValue(key, out v) || !(v is int || v is long || v is double || v is float || v is decimal)) return false;
            number = Convert.ToSingle(v, CultureInfo.InvariantCulture);
            return !float.IsNaN(number) && !float.IsInfinity(number) && number >= min && number <= max;
        }
        private static Dictionary<string, object> Ack(string status, string reason) { return new Dictionary<string, object> { { "status", status }, { "reasonCode", reason } }; }
    }
}
