// PC window map: right-click "Route here" / "Clear destination" (player.waypoint / player.clearWaypoint). Sets GTA's
// own map waypoint, so the game draws its GPS route on the minimap like a waypoint chosen in the pause map. The
// snapshot reports the active waypoint (whoever set it) and the GPS route length for the PC map.
// Only GTA natives are used; nothing of the GTBikeV mod is touched. C# 5 only (.NET Framework csc).
using System;
using System.Collections.Generic;
using System.Globalization;
using GTA;
using GTA.Math;
using GTA.Native;

namespace CompanionBridge.V1
{
    public static class GtbikeV1Destination
    {
        /// <summary>{x, y, routeFound, routeLengthM} of the active waypoint, or null when there is none.</summary>
        public static Dictionary<string, object> Read()
        {
            if (!Game.IsWaypointActive) return null;
            Vector3 p = World.WaypointPosition;
            if (float.IsNaN(p.X) || float.IsNaN(p.Y) || (p.X == 0f && p.Y == 0f)) return null;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["x"] = p.X;
            d["y"] = p.Y;
            bool found = Function.Call<bool>(Hash.GET_GPS_BLIP_ROUTE_FOUND);
            d["routeFound"] = found;
            int length = found ? Function.Call<int>(Hash.GET_GPS_BLIP_ROUTE_LENGTH) : 0;
            d["routeLengthM"] = found && length > 0 ? (object)length : null;
            return d;
        }

        public static Dictionary<string, object> Set(Dictionary<string, object> args, string gameControl, bool routeActive)
        {
            if (args == null || args.Count != 2 || !IsNumber(Get(args, "x")) || !IsNumber(Get(args, "y"))) return Ack("rejected", "INVALID_REQUEST");
            float x = Convert.ToSingle(Get(args, "x"), CultureInfo.InvariantCulture);
            float y = Convert.ToSingle(Get(args, "y"), CultureInfo.InvariantCulture);
            if (float.IsNaN(x) || float.IsNaN(y) || Math.Abs(x) > 10000f || Math.Abs(y) > 10000f) return Ack("rejected", "VALUE_OUT_OF_RANGE");
            if (gameControl != "inControl") return Ack("rejected", "GAME_NOT_IN_CONTROL");
            if (routeActive) return Ack("rejected", "VALUE_NOT_ALLOWED"); // A course has its own guidance.
            Function.Call(Hash.SET_NEW_WAYPOINT, x, y);
            return Ack("dispatched", null);
        }

        public static Dictionary<string, object> Clear(Dictionary<string, object> args, string gameControl, bool routeActive)
        {
            if (args == null || args.Count != 0) return Ack("rejected", "INVALID_REQUEST");
            if (gameControl != "inControl") return Ack("rejected", "GAME_NOT_IN_CONTROL");
            if (routeActive) return Ack("rejected", "VALUE_NOT_ALLOWED");
            if (!Game.IsWaypointActive) return Ack("noop", null);
            Function.Call(Hash.SET_WAYPOINT_OFF);
            return Ack("dispatched", null);
        }

        private static bool IsNumber(object value)
        {
            return value is int || value is long || value is decimal || value is double || value is float;
        }

        private static object Get(Dictionary<string, object> d, string key)
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
