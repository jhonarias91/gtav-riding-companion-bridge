// PC window map: right-click "Teleport here" (player.teleport). Runs on the adapter's own Tick over several ticks:
// the rider (or the bike with the rider on it) is frozen at the target while the game streams collision, then placed
// on the ground, on the nearest road when one is close. Water and timeouts put the rider back where they were.
// Only GTA natives are used; nothing of the GTBikeV mod is touched. C# 5 only (.NET Framework csc).
using System;
using System.Collections.Generic;
using System.Globalization;
using GTA;
using GTA.Math;
using GTA.Native;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1Teleport
    {
        private const int FirstProbeTick = 2;      // Let the streamer start before trusting a ground probe.
        private const int TimeoutTicks = 48;        // About 12 s at the adapter's 250 ms interval.
        private const float RoadSnapMeters = 60f;
        private const float ProbeHeight = 1000f;

        private string requestId;
        private Entity entity;
        private Vector3 origin;
        private float originHeading;
        private float targetX, targetY;
        private int ticks;
        private Dictionary<string, object> report;

        public bool Busy { get { return requestId != null; } }

        /// <summary>Last job outcome for the snapshot: requestId, status running|done|failed, final point or reasonCode.</summary>
        public Dictionary<string, object> Report { get { return report; } }

        public Dictionary<string, object> Start(string id, Dictionary<string, object> args, bool dtOk, string gameControl, bool routeActive)
        {
            if (args == null || args.Count != 2 || !IsNumber(Get(args, "x")) || !IsNumber(Get(args, "y"))) return Ack("rejected", "INVALID_REQUEST");
            float x = Convert.ToSingle(Get(args, "x"), CultureInfo.InvariantCulture);
            float y = Convert.ToSingle(Get(args, "y"), CultureInfo.InvariantCulture);
            if (float.IsNaN(x) || float.IsNaN(y) || Math.Abs(x) > 10000f || Math.Abs(y) > 10000f) return Ack("rejected", "VALUE_OUT_OF_RANGE");
            if (!dtOk) return Ack("rejected", "MOD_INACTIVE");
            if (gameControl != "inControl") return Ack("rejected", "GAME_NOT_IN_CONTROL");
            if (routeActive) return Ack("rejected", "VALUE_NOT_ALLOWED");
            if (Busy) return Ack("rejected", "BRIDGE_BUSY");
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists() || ped.IsDead) return Ack("rejected", "GAME_NOT_IN_CONTROL");
            Vehicle vehicle = ped.IsInVehicle() ? ped.CurrentVehicle : null;
            entity = vehicle != null && vehicle.Exists() ? (Entity)vehicle : ped;
            origin = entity.Position;
            originHeading = entity.Heading;
            targetX = x;
            targetY = y;
            ticks = 0;
            requestId = id;
            Freeze(true);
            // Park the rider above the target so the game streams that area; frozen, so nothing falls meanwhile.
            entity.Position = new Vector3(x, y, Math.Max(origin.Z, 150f));
            report = new Dictionary<string, object> { { "requestId", id }, { "status", "running" } };
            return Ack("dispatched", null);
        }

        /// <summary>Advances the running job; call once per adapter Tick.</summary>
        public void Step()
        {
            if (!Busy) return;
            try
            {
                if (entity == null || !entity.Exists()) { Finish(false, "GAME_NOT_IN_CONTROL", Vector3.Zero, false); return; }
                ticks++;
                Function.Call(Hash.REQUEST_COLLISION_AT_COORD, targetX, targetY, entity.Position.Z);
                if (ticks < FirstProbeTick) return;
                OutputArgument groundOut = new OutputArgument();
                bool found = Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, targetX, targetY, ProbeHeight, groundOut, false, false);
                float ground = found ? groundOut.GetResult<float>() : 0f;
                if (!found || float.IsNaN(ground) || ground < -200f)
                {
                    if (ticks >= TimeoutTicks) Restore("INTERNAL");
                    return;
                }
                OutputArgument waterOut = new OutputArgument();
                if (Function.Call<bool>(Hash.GET_WATER_HEIGHT, targetX, targetY, ground + 100f, waterOut) && waterOut.GetResult<float>() > ground + 0.3f)
                {
                    Restore("VALUE_NOT_ALLOWED"); // Open water: a bike cannot ride there.
                    return;
                }
                Vector3 final = new Vector3(targetX, targetY, ground + 0.5f);
                float heading = originHeading;
                bool snapped = false;
                OutputArgument nodeOut = new OutputArgument();
                OutputArgument nodeHeadingOut = new OutputArgument();
                if (Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING, targetX, targetY, ground, nodeOut, nodeHeadingOut, 1, 3f, 0))
                {
                    Vector3 node = nodeOut.GetResult<Vector3>();
                    float dx = node.X - targetX, dy = node.Y - targetY;
                    if (node != Vector3.Zero && Math.Sqrt(dx * dx + dy * dy) <= RoadSnapMeters)
                    {
                        final = new Vector3(node.X, node.Y, node.Z + 0.5f);
                        heading = nodeHeadingOut.GetResult<float>();
                        snapped = true;
                    }
                }
                Place(final, heading);
                Finish(true, null, final, snapped);
            }
            catch (Exception)
            {
                Restore("INTERNAL"); // The caller logs it; the rider is never left frozen.
                throw;
            }
        }

        /// <summary>Script reload or abort: never leave the rider frozen in the air.</summary>
        public void Abort()
        {
            if (Busy) Restore("INTERNAL");
        }

        private void Restore(string reasonCode)
        {
            try { if (entity != null && entity.Exists()) Place(origin, originHeading); }
            catch (Exception) { try { Freeze(false); } catch (Exception) { } }
            Finish(false, reasonCode, Vector3.Zero, false);
        }

        private void Place(Vector3 point, float heading)
        {
            entity.Position = point;
            entity.Heading = heading;
            Freeze(false);
            entity.Velocity = Vector3.Zero;
            Vehicle vehicle = entity as Vehicle;
            if (vehicle != null) Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, vehicle.Handle, 5f);
        }

        private void Freeze(bool frozen)
        {
            if (entity != null && entity.Exists()) Function.Call(Hash.FREEZE_ENTITY_POSITION, entity.Handle, frozen);
        }

        private void Finish(bool ok, string reasonCode, Vector3 point, bool snapped)
        {
            report = new Dictionary<string, object> { { "requestId", requestId }, { "status", ok ? "done" : "failed" } };
            if (ok)
            {
                report["x"] = point.X;
                report["y"] = point.Y;
                report["z"] = point.Z;
                report["snappedToRoad"] = snapped;
            }
            else report["reasonCode"] = reasonCode;
            requestId = null;
            entity = null;
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
