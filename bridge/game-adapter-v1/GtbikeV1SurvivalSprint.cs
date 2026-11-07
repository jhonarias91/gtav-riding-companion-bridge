// P18 add-on "Survival sprint" (keep pedalling): once the rider passes the target speed a chaser vehicle appears behind
// them. While they hold the target speed and keep pedalling it keeps its distance; below the target, or coasting for
// longer than the grace time (GTBike V power and cadence near zero, even downhill at speed), it closes in and finally
// rams them. Survive the set time to win. Made for threshold and high-intensity intervals.
// The chase is a simple gap model, not traffic AI: the chaser follows the rider's own trail at that gap, so it never
// gets lost in traffic and the rules stay exact. Runs on GtbikeV1AddonHost every frame. GTA natives only; GTBike V
// power and cadence are read from the main script's snapshot (GtbikeV1AddonContext). Allowed during a GTBike V
// course. Parameters mirror bridge/api-v1/addons.mjs. C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using GTA;
using GTA.Math;
using GTA.Native;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1SurvivalSprint : GtbikeV1Addon
    {
        public static readonly GtbikeV1AddonParam[] Params =
        {
            GtbikeV1AddonParam.Num("targetKmh", "integer", 15, 60, 1, 30, true),
            GtbikeV1AddonParam.Num("startGapM", "integer", 10, 80, 5, 30, false),
            GtbikeV1AddonParam.Num("closingKmh", "integer", 2, 30, 1, 12, true),
            GtbikeV1AddonParam.Num("coastGraceS", "integer", 1, 10, 1, 3, true),
            GtbikeV1AddonParam.Num("minPowerW", "integer", 0, 200, 5, 20, true),
            GtbikeV1AddonParam.Num("durationMin", "integer", 1, 60, 1, 10, false),
            GtbikeV1AddonParam.Choice("chaser", new[] { "phantomWedge", "police", "truck" }, "phantomWedge", false),
            GtbikeV1AddonParam.Flag("ramOnCatch", true, true),
            GtbikeV1AddonParam.Flag("showHud", true, true),
        };

        private const float ContactGapM = 4f;      // Chaser centre to rider when its front bumper touches.
        private const float TrailStepM = 1f;
        private const double TrailKeepM = 400;
        private const float TeleportJumpM = 60f;   // A larger jump in one frame (course load, teleport) restarts the trail.
        private const int ModelTimeoutMs = 6000;

        private struct TrailPoint { public Vector3 Position; public float Heading; public double S; }

        private readonly List<TrailPoint> trail = new List<TrailPoint>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private string phase = "armed";
        private double riderS, gap, maxGap, elapsedS, distanceM, coastS, maxSpeed, closestGap, accel;
        private float lastSpeed;
        private bool? pedaling;
        private string chaserKind;
        private Model model;
        private bool fallbackTried;
        private Stopwatch modelWait;
        private Vehicle chaser;
        private Ped driver;
        private float chaserLift; // chaser origin height above the rider's (bike) origin on the same ground
        private double releaseAt = -1, nextHudAt, nextHornAt;

        public GtbikeV1SurvivalSprint() { Init(); }

        public override string Id { get { return "survivalSprint"; } }
        public override bool AllowedDuringCourse { get { return true; } } // It follows the rider, wherever they ride.
        protected override GtbikeV1AddonParam[] Parameters { get { return Params; } }

        public override void Start(string id)
        {
            RemoveChaser();
            requestId = id;
            reasonCode = null;
            result = null;
            state = "running";
            phase = "armed";
            riderS = gap = elapsedS = distanceM = coastS = maxSpeed = accel = 0;
            closestGap = double.MaxValue;
            lastSpeed = 0;
            pedaling = null;
            trail.Clear();
            chaserKind = (string)values["chaser"];
            fallbackTried = false;
            RequestModel(ModelName(chaserKind));
            Notify("~o~Survival sprint~s~: reach " + Target() + " km/h to wake the " + ChaserLabel(chaserKind) + ". Then keep the speed and keep pedalling.");
        }

        public override void Frame(string gameControl, GtbikeV1AddonContext context)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (!Active)
            {
                if (releaseAt >= 0 && now >= releaseAt) RemoveChaser(); // After the ram.
                return;
            }
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists() || ped.IsDead) { End("riderLost"); return; }
            if (Game.IsPaused || gameControl == "loading") return;
            double dt = Math.Min(0.25, Game.LastFrameTime);
            if (dt <= 0) return;
            Entity body = ped.IsInVehicle() && ped.CurrentVehicle != null ? (Entity)ped.CurrentVehicle : ped;
            float speed = body.Speed;
            accel = accel * 0.8 + 0.2 * ((speed - lastSpeed) / dt); // Smoothed frame-to-frame acceleration.
            lastSpeed = speed;
            Track(body.Position, body.Heading);
            UpdatePedaling(context, dt);
            double target = Target() / 3.6;

            if (phase == "armed")
            {
                if (!StepModel()) return;
                if (speed >= target) Spawn(ped);
                Hud(now, speed);
                return;
            }

            elapsedS += dt;
            distanceM += speed * dt;
            maxSpeed = Math.Max(maxSpeed, speed);
            if (elapsedS >= (int)values["durationMin"] * 60.0) { End("completed"); return; }
            bool slow = speed < target;
            bool coasting = coastS > (int)values["coastGraceS"];
            double closing = (int)values["closingKmh"] / 3.6;
            double chaserSpeed = slow ? target + closing : coasting ? speed + closing : target;
            gap = Math.Min(maxGap, gap + (speed - chaserSpeed) * dt);
            closestGap = Math.Min(closestGap, gap);
            if (chaser == null || !chaser.Exists()) { End("internal"); return; }
            Place((float)chaserSpeed);
            if (gap < 12 && now >= nextHornAt)
            {
                nextHornAt = now + 1.5;
                Function.Call(Hash.START_VEHICLE_HORN, chaser.Handle, 800, Function.Call<int>(Hash.GET_HASH_KEY, "HELDDOWN"), false);
            }
            if (gap <= ContactGapM) { Catch(speed); return; }
            Hud(now, speed);
        }

        public override void End(string outcome)
        {
            bool wasActive = Active;
            state = outcome == "completed" || outcome == "caught" ? "finished" : "aborted";
            result = new Dictionary<string, object>
            {
                { "outcome", outcome }, { "finishedAtUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) },
                { "survivedS", Math.Round(elapsedS) }, { "distanceM", Math.Round(distanceM) }, { "maxSpeedKmh", Math.Round(maxSpeed * 3.6, 1) },
                { "closestGapM", closestGap == double.MaxValue ? null : (object)Math.Round(Math.Max(0, closestGap), 1) }, { "targetKmh", Target() },
            };
            if (outcome != "caught" || releaseAt < 0) RemoveChaser(); // A ramming chaser is removed a few seconds later.
            if (!wasActive) return;
            Sound(outcome == "completed" ? "CHECKPOINT_PERFECT" : "TIMER_STOP", "HUD_MINI_GAME_SOUNDSET");
            Notify("~o~Survival sprint~s~ " + OutcomeText(outcome) + " " + Fmt(elapsedS) + " survived, " + (distanceM / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km.");
        }

        public override void Shutdown()
        {
            if (Active) state = "aborted";
            RemoveChaser();
        }

        public override Dictionary<string, object> Report()
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["state"] = state;
            r["requestId"] = requestId;
            r["reasonCode"] = reasonCode;
            r["params"] = Values();
            r["result"] = result;
            if (!Active) { r["run"] = null; return r; }
            bool chased = phase == "chased";
            Dictionary<string, object> run = new Dictionary<string, object>();
            run["phase"] = phase;
            run["elapsedS"] = Math.Round(elapsedS, 1);
            run["timeLeftS"] = chased ? (object)Math.Round(Math.Max(0, (int)values["durationMin"] * 60.0 - elapsedS), 1) : null;
            run["gapM"] = chased ? (object)Math.Round(Math.Max(0, gap), 1) : null;
            run["stabilityPct"] = chased ? (object)Math.Round(Stability() * 100) : null;
            run["speedKmh"] = Math.Round(lastSpeed * 3.6, 1);
            run["targetKmh"] = Target();
            run["accelMps2"] = Math.Round(accel, 2);
            run["coastingS"] = Math.Round(coastS, 1);
            run["pedaling"] = pedaling.HasValue ? (object)pedaling.Value : null;
            run["distanceM"] = Math.Round(distanceM);
            run["chaser"] = ChaserLabel(chaserKind);
            Vector3 p = chased && chaser != null && chaser.Exists() ? chaser.Position : Vector3.Zero;
            run["chaserPosition"] = chased && p != Vector3.Zero ? new Dictionary<string, object> { { "x", p.X }, { "y", p.Y } } : null;
            r["run"] = run;
            return r;
        }

        // ---- chase ----

        private int Target() { return (int)values["targetKmh"]; }

        private double Stability() { return Math.Max(0, Math.Min(1, (gap - ContactGapM) / Math.Max(1, (int)values["startGapM"] - ContactGapM))); }

        /// <summary>GTBike V power and cadence: pedalling means power above the threshold or a cadence of 10 rpm or more.</summary>
        private void UpdatePedaling(GtbikeV1AddonContext context, double dt)
        {
            double? power = context.PowerW, cadence = context.CadenceRpm;
            if (!power.HasValue && !cadence.HasValue) { pedaling = null; coastS = 0; return; } // No sensor data: speed rule only.
            int minPower = (int)values["minPowerW"];
            pedaling = (power.HasValue && power.Value > Math.Max(1, minPower)) || (cadence.HasValue && cadence.Value >= 10);
            coastS = pedaling.Value ? 0 : coastS + dt;
        }

        private void Track(Vector3 position, float heading)
        {
            if (trail.Count > 0)
            {
                TrailPoint last = trail[trail.Count - 1];
                float jump = (float)Distance2D(last.Position, position);
                if (jump > TeleportJumpM) trail.Clear(); // The chaser reappears behind the new position.
                else if (jump < TrailStepM) return;
                else riderS += jump;
            }
            trail.Add(new TrailPoint { Position = position, Heading = heading, S = riderS });
            int old = 0;
            while (old < trail.Count - 2 && trail[old].S < riderS - TrailKeepM) old++;
            if (old > 0) trail.RemoveRange(0, old);
        }

        /// <summary>Point and heading on the rider's trail at distance s (extrapolated backwards past its start).</summary>
        private void TrailAt(double s, out Vector3 position, out float heading)
        {
            if (trail.Count == 0)
            {
                Vector3 here = RiderPosition();
                float h = Game.Player.Character.Heading;
                position = here - Forward(h) * (float)(riderS - s);
                heading = h;
                return;
            }
            TrailPoint first = trail[0];
            if (s <= first.S)
            {
                position = first.Position - Forward(first.Heading) * (float)(first.S - s);
                heading = first.Heading;
                return;
            }
            for (int i = trail.Count - 1; i > 0; i--)
            {
                TrailPoint a = trail[i - 1], b = trail[i];
                if (s < a.S) continue;
                float t = b.S > a.S ? (float)((s - a.S) / (b.S - a.S)) : 0f;
                position = Vector3.Lerp(a.Position, b.Position, Math.Min(1f, t));
                Vector3 dir = b.Position - a.Position;
                heading = dir.Length() > 0.01f ? (float)(Math.Atan2(-dir.X, dir.Y) * 180 / Math.PI) : b.Heading;
                return;
            }
            TrailPoint lastPoint = trail[trail.Count - 1];
            position = lastPoint.Position;
            heading = lastPoint.Heading;
        }

        private static Vector3 Forward(float heading)
        {
            double r = heading * Math.PI / 180;
            return new Vector3((float)-Math.Sin(r), (float)Math.Cos(r), 0f);
        }

        private void RequestModel(string name)
        {
            model = new Model(name);
            modelWait = Stopwatch.StartNew();
            if (model.IsInCdImage) model.Request();
        }

        /// <summary>true once the chaser model is loaded; falls back to the Phantom truck, then gives up.</summary>
        private bool StepModel()
        {
            if (model.IsInCdImage && model.IsLoaded) return true;
            if (model.IsInCdImage && modelWait.ElapsedMilliseconds < ModelTimeoutMs) { model.Request(); return false; }
            if (!fallbackTried) { fallbackTried = true; chaserKind = "truck"; RequestModel(ModelName("truck")); return false; }
            End("internal");
            return false;
        }

        private void Spawn(Ped rider)
        {
            gap = (int)values["startGapM"];
            maxGap = gap * 1.5; // Riding faster banks distance, but only up to half again the start gap.
            Vector3 position;
            float heading;
            TrailAt(riderS - gap, out position, out heading);
            Vector3 chaserMin, chaserMax, bikeMin, bikeMax;
            model.GetDimensions(out chaserMin, out chaserMax);
            Entity body = rider.IsInVehicle() && rider.CurrentVehicle != null ? (Entity)rider.CurrentVehicle : rider;
            body.Model.GetDimensions(out bikeMin, out bikeMax);
            chaserLift = bikeMin.Z - chaserMin.Z; // Both bottoms on the same ground.
            chaser = World.CreateVehicle(model, position + new Vector3(0, 0, chaserLift), heading);
            if (chaser == null || !chaser.Exists()) { End("internal"); return; }
            model.MarkAsNoLongerNeeded();
            chaser.IsInvincible = true;
            chaser.IsPersistent = true;
            driver = chaser.CreateRandomPedOnSeat(VehicleSeat.Driver);
            if (driver != null && driver.Exists()) { driver.BlockPermanentEvents = true; driver.IsInvincible = true; }
            if (chaserKind == "police" && chaser.HasSiren) chaser.IsSirenActive = true;
            phase = "chased";
            elapsedS = 0;
            distanceM = 0;
            closestGap = gap;
            Sound("CHECKPOINT_NORMAL", "HUD_MINI_GAME_SOUNDSET");
            Notify("~r~" + ChaserLabel(chaserKind) + " behind you!~s~ Hold " + Target() + " km/h and keep pedalling for " + (int)values["durationMin"] + " min.");
        }

        /// <summary>Puts the chaser on the trail at the current gap, pitched with the road, moving at its own speed.</summary>
        private void Place(float speed)
        {
            Vector3 position, ahead;
            float heading, unused;
            double s = riderS - gap;
            TrailAt(s, out position, out heading);
            TrailAt(s + 4, out ahead, out unused);
            float pitch = (float)(Math.Atan2(ahead.Z - position.Z, 4) * 180 / Math.PI);
            chaser.PositionNoOffset = position + new Vector3(0, 0, chaserLift);
            chaser.Rotation = new Vector3(pitch, 0f, heading);
            chaser.Velocity = Forward(heading) * speed;
        }

        private void Catch(float riderSpeed)
        {
            if ((bool)values["ramOnCatch"])
            {
                // Let physics take over: the chaser drives through the rider and is removed 5 s later.
                Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, chaser.Handle, riderSpeed + 7f);
                Function.Call(Hash.START_VEHICLE_HORN, chaser.Handle, 2000, Function.Call<int>(Hash.GET_HASH_KEY, "HELDDOWN"), false);
                releaseAt = clock.Elapsed.TotalSeconds + 5;
            }
            End("caught");
        }

        private void RemoveChaser()
        {
            releaseAt = -1;
            try { if (driver != null && driver.Exists()) driver.Delete(); } catch (Exception) { }
            try { if (chaser != null && chaser.Exists()) chaser.Delete(); } catch (Exception) { }
            driver = null;
            chaser = null;
        }

        // ---- text ----

        private void Hud(double now, float speed)
        {
            if (!(bool)values["showHud"] || now < nextHudAt) return;
            nextHudAt = now + 0.3;
            StringBuilder s = new StringBuilder("~o~Survival~s~ ");
            int kmh = (int)Math.Round(speed * 3.6);
            if (phase == "armed")
            {
                s.Append("Reach ").Append(Target()).Append(" km/h to start (now ").Append(kmh).Append(')');
                GTA.UI.Screen.ShowSubtitle(s.ToString(), 500);
                return;
            }
            s.Append(Fmt(Math.Max(0, (int)values["durationMin"] * 60.0 - elapsedS))).Append(" left  ");
            int bars = (int)Math.Round(Stability() * 10);
            s.Append(bars <= 3 ? "~r~" : bars <= 6 ? "~y~" : "~g~").Append('[').Append(new string('|', bars)).Append(new string('.', 10 - bars)).Append("]~s~ ");
            s.Append(Math.Round(Math.Max(0, gap)).ToString(CultureInfo.InvariantCulture)).Append(" m  ");
            s.Append(kmh < Target() ? "~r~" : "").Append(kmh).Append('/').Append(Target()).Append(" km/h~s~");
            if (pedaling == false) s.Append("  ~r~PEDAL! ").Append(coastS.ToString("0.0", CultureInfo.InvariantCulture)).Append("s~s~");
            GTA.UI.Screen.ShowSubtitle(s.ToString(), 500);
        }

        private static string Fmt(double seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string ModelName(string kind)
        {
            switch (kind)
            {
                case "police": return "police3";
                case "truck": return "phantom";
                default: return "phantom2"; // Phantom Wedge
            }
        }

        private static string ChaserLabel(string kind)
        {
            switch (kind)
            {
                case "police": return "police cruiser";
                case "truck": return "Phantom truck";
                default: return "Phantom Wedge";
            }
        }

        private static string OutcomeText(string outcome)
        {
            switch (outcome)
            {
                case "completed": return "~g~survived!~s~";
                case "caught": return "~r~caught.~s~";
                case "modInactive": return "ended: GTBike V was turned off.";
                case "riderLost": return "ended: rider lost.";
                case "internal": return "ended: the chaser could not be created.";
                default: return "stopped.";
            }
        }
    }
}
