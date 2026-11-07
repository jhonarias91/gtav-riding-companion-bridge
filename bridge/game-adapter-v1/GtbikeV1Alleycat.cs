// P18 add-on "Alleycat courier": pick up and deliver fragile packages at random street points in Downtown Los Santos.
// Runs on GtbikeV1AddonHost every frame (see GtbikeV1Addons.cs). GTA natives only; GTBike V reads the wind and weather
// set here itself (trainer resistance, grip). Parameters mirror bridge/api-v1/addons.mjs. C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using GTA;
using GTA.Math;
using GTA.Native;

namespace CompanionBridge.V1
{
    /// <summary>
    /// Alleycat courier: pick up and deliver fragile packages at random street points in Downtown Los Santos against the
    /// clock (it starts at the first pickup). Hits damage the carried package; heavy ones bring a storm and a headwind.
    /// </summary>
    public sealed class GtbikeV1Alleycat : GtbikeV1Addon
    {
        public static readonly GtbikeV1AddonParam[] Params =
        {
            GtbikeV1AddonParam.Num("packages", "integer", 1, 10, 1, 5, false),
            GtbikeV1AddonParam.Num("timeLimitMin", "integer", 5, 60, 1, 20, false),
            GtbikeV1AddonParam.Choice("fragility", new[] { "low", "normal", "high" }, "normal", true),
            GtbikeV1AddonParam.Num("heavyChancePct", "integer", 0, 100, 10, 40, false),
            GtbikeV1AddonParam.Flag("stormWhenHeavy", true, true),
            GtbikeV1AddonParam.Num("headwindMps", "number", 0, 12, 0.5, 10.0, true),
            GtbikeV1AddonParam.Flag("showHud", true, true),
        };

        // Downtown Los Santos: candidate box (GTA world metres) and the zone codes (GET_NAME_OF_ZONE) that count as Downtown.
        private const float BoxMinX = -800f, BoxMaxX = 550f, BoxMinY = -1250f, BoxMaxY = -350f;
        private static readonly string[] DowntownZones = { "PBOX", "LEGSQU", "SKID", "TEXTI", "DOWNT", "ALTA", "KOREAT" };
        private static readonly string[] WeatherNames = { "EXTRASUNNY", "CLEAR", "CLOUDS", "SMOG", "FOGGY", "OVERCAST", "RAIN", "THUNDER", "CLEARING", "NEUTRAL", "SNOW", "BLIZZARD", "SNOWLIGHT", "XMAS", "HALLOWEEN" };
        private const float ReachRadius = 9f;
        private const int LocateTimeoutMs = 8000;
        private const double HitCooldownS = 0.6;

        private readonly Random random = new Random();
        private readonly Stopwatch clock = Stopwatch.StartNew(); // Real time for cooldowns, HUD and effect pacing.

        // Run.
        private int total, delivered, damaged;
        private bool carrying, heavy, clockStarted;
        private double integrity, score, elapsedS, timeLimitS;
        private string phase = "locating";
        private string nextKind = "pickup";
        private bool hasNext;
        private Vector3 next, previous;
        private Blip blip;
        private Stopwatch locating;
        private string zone, zoneName;
        private double nextZoneAt, nextHudAt, nextWindAt, hitCooldownUntil;

        // Damage baselines.
        private float lastBody;
        private int lastHealth;
        private bool wasRagdoll;
        private readonly float[] speedSamples = new float[8];
        private readonly double[] speedTimes = new double[8];
        private int sampleIndex;

        // Weather and wind overrides (restored at the end of the run).
        private bool weatherOverridden;
        private int originalWeather = -1;
        private string weatherNow; // null = not forced
        private double clearWeatherAt = -1;
        private bool windOverridden;
        private float originalWind;
        private float windNow;
        // SET_WIND_DIRECTION takes a number whose unit and origin are not documented; two settings read back through
        // GET_WIND_DIRECTION give origin and scale, so the wind can be aimed against the rider's heading.
        private static int calibrationStep;
        private static float calibrationA0, calibrationScale;
        private static bool calibrationOk;
        private double calibrationAt;

        public GtbikeV1Alleycat() { Init(); }

        public override string Id { get { return "alleycat"; } }
        public override bool AllowedDuringCourse { get { return false; } } // The course has its own route and guidance.
        protected override GtbikeV1AddonParam[] Parameters { get { return Params; } }

        public override void Start(string id)
        {
            CleanUp();
            requestId = id;
            reasonCode = null;
            result = null;
            state = "preparing";
            total = (int)values["packages"];
            timeLimitS = (int)values["timeLimitMin"] * 60.0;
            delivered = damaged = 0;
            carrying = heavy = clockStarted = false;
            integrity = 0;
            score = 0;
            elapsedS = 0;
            zone = zoneName = null;
            nextZoneAt = 0;
            previous = RiderPosition();
            Locate("pickup");
            Notify("~y~Alleycat~s~: " + total + (total == 1 ? " package" : " packages") + " to deliver in Downtown Los Santos. Head to the ~y~pickup~s~.");
        }

        public override void Frame(string gameControl, GtbikeV1AddonContext context)
        {
            StepEffectsRestore();
            if (!Active) return;
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists() || ped.IsDead) { End("riderLost"); return; }
            bool paused = Game.IsPaused || gameControl == "loading";
            if (!paused && clockStarted)
            {
                elapsedS += Math.Min(0.5, Math.Max(0, Game.LastFrameTime));
                if (elapsedS >= timeLimitS) { End("timeUp"); return; }
            }
            double now = clock.Elapsed.TotalSeconds;
            Vector3 position = RiderPosition();
            if (now >= nextZoneAt)
            {
                nextZoneAt = now + 1;
                zone = Function.Call<string>(Hash.GET_NAME_OF_ZONE, position.X, position.Y, position.Z);
                zoneName = World.GetZoneLocalizedName(position);
            }
            if (!hasNext) { StepLocate(position); if (!Active) return; }
            else
            {
#pragma warning disable 618 // Renamed Cylinder in SHVDN 3.9; the old name keeps older ScriptHookVDotNet3 builds working.
                World.DrawMarker(MarkerType.VerticalCylinder, new Vector3(next.X, next.Y, next.Z - 1f), Vector3.Zero, Vector3.Zero, new Vector3(ReachRadius, ReachRadius, 2.5f),
                    nextKind == "pickup" ? Color.FromArgb(110, 255, 205, 0) : Color.FromArgb(110, 80, 220, 90));
#pragma warning restore 618
                float dx = position.X - next.X, dy = position.Y - next.Y;
                if (dx * dx + dy * dy <= ReachRadius * ReachRadius && Math.Abs(position.Z - next.Z) < 8f) { Reached(); if (!Active) return; }
            }
            if (carrying && ped.IsInVehicle()) StepDamage(ped, now);
            if (!Active) return;
            StepEffects(ped, now);
            if ((bool)values["showHud"] && now >= nextHudAt)
            {
                nextHudAt = now + 0.5;
                GTA.UI.Screen.ShowSubtitle(HudText(position), 800);
            }
        }

        public override void End(string outcome)
        {
            bool wasActive = Active;
            bool noPoints = outcome == "noPoints";
            if (state == "preparing" && noPoints) { state = "failed"; reasonCode = "ADDON_NO_POINTS"; }
            else state = outcome == "completed" || outcome == "timeUp" ? "finished" : "aborted";
            if (outcome == "timeUp" && carrying) damaged++; // The package in hand was not delivered.
            carrying = heavy = false;
            if (outcome == "completed") score += Math.Max(0, timeLimitS - elapsedS) / 10.0; // Time bonus.
            result = new Dictionary<string, object>
            {
                { "outcome", outcome }, { "finishedAtUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) },
                { "delivered", delivered }, { "damaged", damaged }, { "packagesTotal", total }, { "score", Math.Round(score) }, { "elapsedS", Math.Round(elapsedS) },
            };
            CleanUp();
            if (!wasActive) return;
            Sound("TIMER_STOP", "HUD_MINI_GAME_SOUNDSET");
            Notify("~y~Alleycat~s~ " + OutcomeText(outcome) + " Delivered " + delivered + "/" + total + ", score " + Math.Round(score).ToString(CultureInfo.InvariantCulture) + ".");
        }

        /// <summary>Script reload: undo every override at once.</summary>
        public override void Shutdown()
        {
            if (Active) state = "aborted";
            CleanUp();
            if (weatherOverridden)
            {
                if (originalWeather >= 0 && originalWeather < WeatherNames.Length) Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, WeatherNames[originalWeather]);
                Function.Call(Hash.CLEAR_WEATHER_TYPE_PERSIST);
                weatherOverridden = false;
            }
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
            Vector3 position = RiderPosition();
            Dictionary<string, object> run = new Dictionary<string, object>();
            run["phase"] = hasNext ? phase : "locating";
            run["elapsedS"] = Math.Round(elapsedS, 1);
            run["timeLeftS"] = clockStarted ? (object)Math.Round(Math.Max(0, timeLimitS - elapsedS), 1) : null;
            run["packagesTotal"] = total;
            run["delivered"] = delivered;
            run["damaged"] = damaged;
            run["carrying"] = carrying;
            run["heavy"] = heavy;
            run["integrityPct"] = carrying ? (object)Math.Round(integrity, 1) : null;
            run["score"] = Math.Round(score);
            run["zone"] = zone;
            run["zoneName"] = zoneName;
            run["windMps"] = windOverridden ? (object)windNow : null;
            run["windAligned"] = windOverridden && calibrationOk;
            run["weather"] = weatherNow;
            run["next"] = hasNext ? new Dictionary<string, object> { { "x", next.X }, { "y", next.Y }, { "kind", nextKind }, { "distanceM", Math.Round(Distance2D(position, next)) } } : null;
            r["run"] = run;
            return r;
        }

        // ---- points ----

        private void Locate(string kind)
        {
            nextKind = kind;
            hasNext = false;
            phase = "locating";
            if (blip != null) { try { blip.Delete(); } catch (Exception) { } blip = null; }
            locating = Stopwatch.StartNew();
        }

        /// <summary>Tries a few random street points per frame inside Downtown; relaxes the rules in the second half of the timeout.</summary>
        private void StepLocate(Vector3 rider)
        {
            long ms = locating.ElapsedMilliseconds;
            if (ms > LocateTimeoutMs) { End("noPoints"); return; }
            bool relaxed = ms > LocateTimeoutMs / 2;
            Function.Call(Hash.REQUEST_PATH_NODES_IN_AREA_THIS_FRAME, BoxMinX, BoxMinY, BoxMaxX, BoxMaxY);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                float x = BoxMinX + (float)random.NextDouble() * (BoxMaxX - BoxMinX);
                float y = BoxMinY + (float)random.NextDouble() * (BoxMaxY - BoxMinY);
                Vector3 node = World.GetNextPositionOnStreet(new Vector2(x, y), false);
                if (node == Vector3.Zero || node.X < BoxMinX - 50 || node.X > BoxMaxX + 50 || node.Y < BoxMinY - 50 || node.Y > BoxMaxY + 50) continue;
                if (!relaxed && Array.IndexOf(DowntownZones, Function.Call<string>(Hash.GET_NAME_OF_ZONE, node.X, node.Y, node.Z)) < 0) continue;
                double fromPrevious = Distance2D(previous, node);
                double fromRider = Distance2D(rider, node);
                bool first = nextKind == "pickup" && delivered + damaged == 0;
                if (fromRider < 120) continue;
                if (!first && (fromPrevious < (relaxed ? 150 : 300) || fromPrevious > (relaxed ? 2500 : 1400))) continue;
                SetNext(node);
                return;
            }
        }

        private void SetNext(Vector3 point)
        {
            next = point;
            hasNext = true;
            phase = nextKind == "pickup" ? "toPickup" : "toDropoff";
            if (state == "preparing") state = "running";
            blip = World.CreateBlip(point);
            blip.Sprite = nextKind == "pickup" ? BlipSprite.Package : BlipSprite.Standard;
            blip.Color = nextKind == "pickup" ? BlipColor.Yellow : BlipColor.Green;
            blip.Name = nextKind == "pickup" ? "Alleycat pickup" : "Alleycat drop-off";
            blip.ShowRoute = true;
        }

        private void Reached()
        {
            previous = next;
            Sound("CHECKPOINT_NORMAL", "HUD_MINI_GAME_SOUNDSET");
            if (nextKind == "pickup")
            {
                carrying = true;
                heavy = random.Next(100) < (int)values["heavyChancePct"];
                integrity = 100;
                hitCooldownUntil = clock.Elapsed.TotalSeconds + 1.0; // Stopping at the marker is not a crash.
                ResetDamageBaseline();
                clockStarted = true;
                Notify("Package " + (delivered + damaged + 1) + "/" + total + (heavy ? " ~r~HEAVY~s~: storm and headwind ahead." : " picked up.") + " Ride to the ~g~drop-off~s~, it is fragile.");
                Locate("dropoff");
                return;
            }
            delivered++;
            score += integrity * (heavy ? 1.5 : 1.0);
            Notify("~g~Delivered~s~ at " + Math.Round(integrity).ToString(CultureInfo.InvariantCulture) + "% integrity.");
            carrying = heavy = false;
            if (delivered + damaged >= total) { End("completed"); return; }
            Locate("pickup");
        }

        // ---- damage ----

        private void ResetDamageBaseline()
        {
            Ped ped = Game.Player.Character;
            Vehicle bike = ped != null && ped.IsInVehicle() ? ped.CurrentVehicle : null;
            lastBody = bike != null ? bike.BodyHealth : 1000f;
            lastHealth = ped != null ? ped.Health : 0;
            wasRagdoll = false;
            for (int i = 0; i < speedSamples.Length; i++) { speedSamples[i] = bike != null ? bike.Speed : 0f; speedTimes[i] = clock.Elapsed.TotalSeconds; }
            if (bike != null) Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, bike.Handle);
            if (ped != null) Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, ped.Handle);
        }

        /// <summary>
        /// Hits on the carried package: damage from a vehicle, body or rider health dropping, a collision with a sudden
        /// speed loss (kerb, wall, pole) and falling off. The strongest source of the frame counts, then a short cooldown.
        /// </summary>
        private void StepDamage(Ped ped, double now)
        {
            Vehicle bike = ped.CurrentVehicle;
            if (bike == null || !bike.Exists()) return;
            float speed = bike.Speed;
            float peak = speed;
            for (int i = 0; i < speedSamples.Length; i++) if (now - speedTimes[i] <= 0.3 && speedSamples[i] > peak) peak = speedSamples[i];
            speedSamples[sampleIndex] = speed;
            speedTimes[sampleIndex] = now;
            sampleIndex = (sampleIndex + 1) % speedSamples.Length;

            double hit = 0;
            bool byVehicle = Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ANY_VEHICLE, bike.Handle) || Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ANY_VEHICLE, ped.Handle);
            if (byVehicle) hit = 25;
            float body = bike.BodyHealth;
            if (body < lastBody - 0.5f) hit = Math.Max(hit, Math.Min(30, 3 + (lastBody - body) * 0.3));
            int health = ped.Health;
            if (health < lastHealth) hit = Math.Max(hit, Math.Min(40, 5 + (lastHealth - health) * 0.8));
            float drop = peak - speed;
            if (bike.HasCollided && drop >= 2.5f) hit = Math.Max(hit, Math.Min(25, Math.Max(5, drop * 4)));
            bool ragdoll = ped.IsRagdoll;
            if (ragdoll && !wasRagdoll) hit = Math.Max(hit, 35);
            wasRagdoll = ragdoll;
            lastBody = body;
            lastHealth = health;
            Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, bike.Handle);
            Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, ped.Handle);
            if (hit <= 0 || now < hitCooldownUntil) return;
            hitCooldownUntil = now + HitCooldownS;
            string fragility = (string)values["fragility"];
            double loss = hit * (fragility == "low" ? 0.5 : fragility == "high" ? 1.75 : 1.0);
            integrity = Math.Max(0, integrity - loss);
            Sound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            if (integrity > 0) return;
            damaged++;
            carrying = heavy = false;
            Notify("~r~Package destroyed.~s~");
            if (delivered + damaged >= total) { End("completed"); return; }
            Locate("pickup");
        }

        // ---- weather and wind ----

        private void StepEffects(Ped ped, double now)
        {
            bool heavyNow = carrying && heavy;
            bool downtown = zone != null && Array.IndexOf(DowntownZones, zone) >= 0;
            string wantedWeather = heavyNow && (bool)values["stormWhenHeavy"] ? (downtown ? "THUNDER" : "RAIN") : null;
            if (wantedWeather != weatherNow)
            {
                if (wantedWeather != null)
                {
                    if (!weatherOverridden) { originalWeather = (int)World.Weather; weatherOverridden = true; }
                    Function.Call(Hash.SET_WEATHER_TYPE_OVERTIME_PERSIST, wantedWeather, 12f);
                    clearWeatherAt = -1;
                }
                else ReleaseWeather(now);
                weatherNow = wantedWeather;
            }
            double windWanted = heavyNow ? Convert.ToDouble(values["headwindMps"], CultureInfo.InvariantCulture) : 0;
            if (windWanted <= 0) { ReleaseWind(); return; }
            if (!windOverridden) { originalWind = Function.Call<float>(Hash.GET_WIND_SPEED); windOverridden = true; }
            if (now < nextWindAt) return;
            nextWindAt = now + 0.25;
            windNow = (float)windWanted;
            Function.Call(Hash.SET_WIND_SPEED, windNow);
            if (!Calibrate(now)) return;
            if (!calibrationOk) return; // Unknown direction mapping: the wind blows, but not aimed.
            // GTBike V: headwind = -cos(atan2(dir.X, dir.Y) + heading in radians) * speed, largest when the angle is pi.
            double target = Math.PI - ped.Heading * Math.PI / 180.0;
            Function.Call(Hash.SET_WIND_DIRECTION, (float)(Wrap(target - calibrationA0) / calibrationScale));
        }

        /// <summary>Two-point calibration of SET_WIND_DIRECTION, one step every 0.2 s. true once done.</summary>
        private bool Calibrate(double now)
        {
            if (calibrationStep >= 3) return true;
            if (now < calibrationAt) return false;
            calibrationAt = now + 0.2;
            if (calibrationStep == 0) { Function.Call(Hash.SET_WIND_DIRECTION, 0f); calibrationStep = 1; return false; }
            if (calibrationStep == 1)
            {
                calibrationA0 = WindAngle();
                Function.Call(Hash.SET_WIND_DIRECTION, 1f);
                calibrationStep = 2;
                return false;
            }
            calibrationScale = (float)Wrap(WindAngle() - calibrationA0);
            calibrationOk = Math.Abs(calibrationScale) > 0.005f && !float.IsNaN(calibrationScale);
            calibrationStep = 3;
            return true;
        }

        private static float WindAngle()
        {
            Vector3 dir = Function.Call<Vector3>(Hash.GET_WIND_DIRECTION);
            return (float)Math.Atan2(dir.X, dir.Y);
        }

        private void ReleaseWeather(double now)
        {
            if (!weatherOverridden || weatherNow == null) return;
            string back = originalWeather >= 0 && originalWeather < WeatherNames.Length ? WeatherNames[originalWeather] : "CLEAR";
            Function.Call(Hash.SET_WEATHER_TYPE_OVERTIME_PERSIST, back, 12f);
            clearWeatherAt = now + 13; // Then hand the weather back to the game's own cycle.
        }

        private void ReleaseWind()
        {
            if (!windOverridden) return;
            Function.Call(Hash.SET_WIND_SPEED, originalWind);
            windOverridden = false;
            windNow = 0;
        }

        /// <summary>After a run: finish the weather transition back, then clear the persisted weather.</summary>
        private void StepEffectsRestore()
        {
            if (!weatherOverridden || weatherNow != null || clearWeatherAt < 0) return;
            if (clock.Elapsed.TotalSeconds < clearWeatherAt) return;
            Function.Call(Hash.CLEAR_WEATHER_TYPE_PERSIST);
            weatherOverridden = false;
            clearWeatherAt = -1;
        }

        // ---- helpers ----

        private void CleanUp()
        {
            hasNext = false;
            if (blip != null) { try { blip.Delete(); } catch (Exception) { } blip = null; }
            ReleaseWind();
            if (weatherNow != null) { ReleaseWeather(clock.Elapsed.TotalSeconds); weatherNow = null; }
        }

        private string HudText(Vector3 position)
        {
            StringBuilder s = new StringBuilder("~y~Alleycat~s~ ");
            s.Append(Math.Min(total, delivered + damaged + 1)).Append('/').Append(total);
            if (carrying)
            {
                s.Append("  Package ").Append(Math.Round(integrity).ToString(CultureInfo.InvariantCulture)).Append('%');
                if (heavy) s.Append(" ~r~HEAVY~s~");
            }
            if (clockStarted)
            {
                TimeSpan left = TimeSpan.FromSeconds(Math.Max(0, timeLimitS - elapsedS));
                s.Append("  ").Append(((int)left.TotalMinutes).ToString(CultureInfo.InvariantCulture)).Append(':').Append(left.Seconds.ToString("00", CultureInfo.InvariantCulture)).Append(" left");
            }
            if (hasNext)
            {
                double metres = Distance2D(position, next);
                s.Append(nextKind == "pickup" ? "  Pickup " : "  Drop-off ");
                s.Append(metres >= 1000 ? (metres / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km" : Math.Round(metres).ToString(CultureInfo.InvariantCulture) + " m");
            }
            else s.Append("  Finding the next point...");
            return s.ToString();
        }

        private static string OutcomeText(string outcome)
        {
            switch (outcome)
            {
                case "completed": return "~g~complete!~s~";
                case "timeUp": return "~r~time is up.~s~";
                case "courseLoaded": return "ended: a course was loaded.";
                case "modInactive": return "ended: GTBike V was turned off.";
                case "riderLost": return "ended: rider lost.";
                case "noPoints": return "could not find Downtown street points. Ride closer to Downtown and try again.";
                case "internal": return "ended by an error.";
                default: return "stopped.";
            }
        }

        private static double Wrap(double angle)
        {
            while (angle > Math.PI) angle -= 2 * Math.PI;
            while (angle < -Math.PI) angle += 2 * Math.PI;
            return angle;
        }
    }
}
