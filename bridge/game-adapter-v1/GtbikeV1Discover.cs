// P18 add-on "Discover": find a hidden spot with only a hint, the zone it is in and the straight-line distance, plus
// hot/cold as you get closer. Spots are your own places saved on the PC window map, or random street spots around
// you. Each find earns XP (distance, speed, first time, new zone, daily streak) kept in the rider's local progress file
// (GtbikeV1Progress: %LOCALAPPDATA%\GTBikeVCompanion\v1\rider-progress.json, shared with completed routes).
// Places: v1\discover-places.json. Runs on GtbikeV1AddonHost every frame. GTA natives only. C# 5 only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using GTA;
using GTA.Math;
using GTA.Native;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1Discover : GtbikeV1Addon
    {
        public static readonly GtbikeV1AddonParam[] Params =
        {
            GtbikeV1AddonParam.Choice("source", new[] { "mixed", "myPlaces", "random" }, "mixed", false),
            GtbikeV1AddonParam.Num("rounds", "integer", 1, 20, 1, 3, false),
            GtbikeV1AddonParam.Num("minDistanceKm", "number", 0.5, 10, 0.5, 1.0, false),
            GtbikeV1AddonParam.Num("maxDistanceKm", "number", 1, 20, 0.5, 4.0, false),
            GtbikeV1AddonParam.Num("roundTimeMin", "integer", 5, 90, 5, 30, false),
            GtbikeV1AddonParam.Num("searchAreaM", "integer", 0, 1000, 50, 300, true),
            GtbikeV1AddonParam.Num("discoverRadiusM", "integer", 10, 60, 5, 25, true),
            GtbikeV1AddonParam.Flag("showHud", true, true),
        };

        public const int MaxPlaces = 100;
        private const int LocateTimeoutMs = 8000;
        private const double FoundPauseS = 6;

        private sealed class Place
        {
            public string Id, Name;
            public float X, Y;
        }

        private readonly List<Place> places = new List<Place>();
        private readonly GtbikeV1Progress progress = GtbikeV1Progress.Shared;
        private readonly Random random = new Random();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private bool placesDirty;

        // Run.
        private string phase = "locating";
        private int round, rounds, foundThisRun, missedThisRun;
        private long xpThisRun;
        private double roundS, foundAt, nextHudAt, trendAt, trendDistance;
        private string trend = "steady";
        private Vector3 target, roundStart;
        private bool hasTarget;
        private string targetName, targetZone, targetZoneName, targetPlaceId, lastTargetId;
        private double startDistance;
        private Stopwatch locating;
        private Vector3 candidate;
        private bool hasCandidate;
        private Stopwatch candidateFor;
        private Blip area;

        public GtbikeV1Discover() { Init(); }

        public override string Id { get { return "discover"; } }
        public override bool AllowedDuringCourse { get { return false; } } // A course has its own route; free ride only.
        protected override GtbikeV1AddonParam[] Parameters { get { return Params; } }

        // ---- files ----

        public override void Load(string directory)
        {
            JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            try
            {
                string path = Path.Combine(directory, "discover-places.json");
                if (File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024)
                {
                    IEnumerable list = GtbikeV1Addons.Get(json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8)), "places") as IEnumerable;
                    if (list != null)
                        foreach (object item in list)
                        {
                            Dictionary<string, object> d = item as Dictionary<string, object>;
                            Place p = d == null ? null : FromDict(d);
                            if (p != null && places.Count < MaxPlaces) places.Add(p);
                        }
                }
            }
            catch (Exception) { places.Clear(); }
        }

        public override void TakeFiles(Dictionary<string, object> files)
        {
            if (placesDirty)
            {
                placesDirty = false;
                List<object> list = new List<object>();
                foreach (Place p in places) list.Add(new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "point", Point(p.X, p.Y) } });
                files["discover-places.json"] = new Dictionary<string, object> { { "places", list } };
            }
        }

        // ---- place commands (PC map, phones) ----

        public override Dictionary<string, object> ItemCommand(string type, Dictionary<string, object> args)
        {
            if (type != "addons.savePlace" && type != "addons.removePlace") return null;
            string id = GtbikeV1Addons.Get(args, "placeId") as string;
            if (!ValidId(id)) return Ack("rejected", "INVALID_REQUEST");
            Place existing = places.Find(p => p.Id == id);
            if (type == "addons.removePlace")
            {
                if (args.Count != 2) return Ack("rejected", "INVALID_REQUEST");
                if (existing == null) return Ack("noop", null);
                if (Active && targetPlaceId == id) return Ack("rejected", "VALUE_NOT_ALLOWED"); // It is the spot being searched.
                places.Remove(existing);
                placesDirty = true;
                return Ack("dispatched", null);
            }
            Dictionary<string, object> point = GtbikeV1Addons.Get(args, "point") as Dictionary<string, object>;
            string name = GtbikeV1Addons.Get(args, "name") as string;
            float x, y;
            if (args.Count != 4 || name == null || name.Trim().Length == 0 || name.Length > 40 || point == null || !Coord(point, out x, out y)) return Ack("rejected", "INVALID_REQUEST");
            if (existing == null)
            {
                if (places.Count >= MaxPlaces) return Ack("rejected", "VALUE_NOT_ALLOWED");
                existing = new Place { Id = id };
                places.Add(existing);
            }
            else if (Active && targetPlaceId == id && (Math.Abs(existing.X - x) > 1 || Math.Abs(existing.Y - y) > 1)) return Ack("rejected", "VALUE_NOT_ALLOWED");
            existing.Name = name.Trim();
            existing.X = x;
            existing.Y = y;
            placesDirty = true;
            return Ack("dispatched", null);
        }

        // ---- lifecycle ----

        public override void Start(string id)
        {
            CleanUp();
            requestId = id;
            reasonCode = null;
            result = null;
            state = "preparing";
            rounds = (int)values["rounds"];
            round = 0;
            foundThisRun = missedThisRun = 0;
            xpThisRun = 0;
            lastTargetId = null;
            NextRound();
            Notify("~y~Discover~s~: " + rounds + (rounds == 1 ? " spot" : " spots") + " to find with only a hint. Watch the distance: hot or cold?");
        }

        public override void Frame(string gameControl, GtbikeV1AddonContext context)
        {
            if (!Active) return;
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists() || ped.IsDead) { End("riderLost"); return; }
            if (Game.IsPaused || gameControl == "loading") return;
            double dt = Math.Min(0.25, Game.LastFrameTime);
            if (dt <= 0) return;
            double now = clock.Elapsed.TotalSeconds;
            Vector3 position = RiderPosition();

            if (phase == "locating") { StepLocate(position); return; }
            if (phase == "found")
            {
                if (now - foundAt >= FoundPauseS) { if (round >= rounds) End("completed"); else NextRound(); }
                return;
            }

            roundS += dt;
            double distance = Distance2D(position, target);
            if (now >= trendAt)
            {
                trend = distance < trendDistance - 15 ? "closer" : distance > trendDistance + 15 ? "farther" : "steady";
                trendDistance = distance;
                trendAt = now + 4;
            }
            if (distance <= 60)
            {
#pragma warning disable 618 // Renamed Cylinder in SHVDN 3.9; the old name keeps older ScriptHookVDotNet3 builds working.
                float radius = (int)values["discoverRadiusM"];
                World.DrawMarker(MarkerType.VerticalCylinder, new Vector3(target.X, target.Y, target.Z - 1f), Vector3.Zero, Vector3.Zero, new Vector3(radius * 2, radius * 2, 4f), Color.FromArgb(70, 255, 170, 30));
#pragma warning restore 618
            }
            if (distance <= (int)values["discoverRadiusM"]) { Found(position); return; }
            if (roundS >= (int)values["roundTimeMin"] * 60.0) { Missed(); return; }
            Hud(now, distance);
        }

        public override void End(string outcome)
        {
            bool wasActive = Active;
            bool failedStart = state == "preparing" && outcome == "noPoints";
            state = failedStart ? "failed" : outcome == "completed" ? "finished" : "aborted";
            if (failedStart) reasonCode = "ADDON_NO_POINTS";
            result = new Dictionary<string, object>
            {
                { "outcome", outcome }, { "finishedAtUtc", Utc() }, { "found", foundThisRun }, { "missed", missedThisRun }, { "rounds", rounds },
                { "xp", xpThisRun }, { "level", Level(progress.Xp) },
            };
            CleanUp();
            if (!wasActive) return;
            Sound(outcome == "completed" ? "CHECKPOINT_PERFECT" : "TIMER_STOP", "HUD_MINI_GAME_SOUNDSET");
            Notify(outcome == "noPoints" ? "~y~Discover~s~: no spot found in the distance range. Save places on the PC map or change the distances."
                : "~y~Discover~s~ " + (outcome == "completed" ? "done! " : "stopped. ") + foundThisRun + "/" + rounds + " found, +" + xpThisRun + " XP. Level " + Level(progress.Xp) + " " + LevelName(Level(progress.Xp)) + ".");
        }

        public override void Shutdown()
        {
            if (Active) state = "aborted";
            CleanUp();
        }

        public override Dictionary<string, object> Report()
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["state"] = state;
            r["requestId"] = requestId;
            r["reasonCode"] = reasonCode;
            r["params"] = Values();
            r["result"] = result;
            List<object> list = new List<object>();
            foreach (Place p in places)
            {
                Dictionary<string, object> found = progress.PlaceStats(p.Id);
                list.Add(new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "point", Point(p.X, p.Y) },
                    { "found", found == null ? 0 : (int)Num(found, "count") }, { "bestTimeS", found == null ? null : GtbikeV1Addons.Get(found, "bestTimeS") },
                    { "lastFoundAtUtc", found == null ? null : GtbikeV1Addons.Get(found, "lastAtUtc") } });
            }
            r["places"] = list;
            r["progress"] = progress.Report();
            if (!Active) { r["run"] = null; return r; }
            Dictionary<string, object> run = new Dictionary<string, object>();
            double distance = hasTarget ? Distance2D(RiderPosition(), target) : 0;
            run["phase"] = phase == "found" ? "found" : hasTarget ? "searching" : "locating";
            run["round"] = round;
            run["rounds"] = rounds;
            run["elapsedS"] = Math.Round(roundS, 1);
            run["timeLeftS"] = hasTarget && phase == "searching" ? (object)Math.Round(Math.Max(0, (int)values["roundTimeMin"] * 60.0 - roundS), 1) : null;
            run["hintZone"] = hasTarget ? targetZoneName : null;
            run["distanceM"] = hasTarget && phase == "searching" ? (object)Math.Round(distance) : null;
            run["heat"] = hasTarget && phase == "searching" ? Heat(distance) : null;
            run["trend"] = hasTarget && phase == "searching" ? trend : null;
            run["targetSource"] = hasTarget ? (targetPlaceId != null ? "place" : "random") : null;
            run["targetPlaceId"] = targetPlaceId; // For the bridge's checks only; never sent to clients.
            run["found"] = foundThisRun;
            run["missed"] = missedThisRun;
            run["xp"] = xpThisRun;
            r["run"] = run;
            return r;
        }

        // ---- rounds ----

        private void NextRound()
        {
            round++;
            phase = "locating";
            hasTarget = false;
            hasCandidate = false;
            targetPlaceId = null;
            roundS = 0;
            RemoveArea();
            locating = Stopwatch.StartNew();
        }

        private void StepLocate(Vector3 rider)
        {
            string source = (string)values["source"];
            double min = Convert.ToDouble(values["minDistanceKm"], CultureInfo.InvariantCulture) * 1000, max = Math.Max(min + 500, Convert.ToDouble(values["maxDistanceKm"], CultureInfo.InvariantCulture) * 1000);
            if (source != "random")
            {
                Place place = PickPlace(rider, min, max, source == "myPlaces");
                if (place != null)
                {
                    float ground;
                    float z = World.GetGroundHeight(new Vector3(place.X, place.Y, 1000f), out ground, GetGroundHeightMode.Normal) ? ground : rider.Z;
                    Begin(new Vector3(place.X, place.Y, z), place.Name, place.Id, rider);
                    return;
                }
                if (source == "myPlaces") { End("noPoints"); return; }
            }
            if (locating.ElapsedMilliseconds > LocateTimeoutMs) { if (round == 1) End("noPoints"); else { Notify("No new spot found nearby; the run ends here."); End("completed"); } return; }
            // A random street spot: pick a bearing and distance, let the game stream the road nodes there, then snap to a street.
            if (!hasCandidate || candidateFor.ElapsedMilliseconds > 1500)
            {
                double bearing = random.NextDouble() * Math.PI * 2, d = min + random.NextDouble() * (max - min);
                candidate = new Vector3(rider.X + (float)(Math.Sin(bearing) * d), rider.Y + (float)(Math.Cos(bearing) * d), rider.Z);
                hasCandidate = Math.Abs(candidate.X) < 4500 && candidate.Y > -4200 && candidate.Y < 8200; // Roughly the island.
                candidateFor = Stopwatch.StartNew();
                if (!hasCandidate) return;
            }
            Function.Call(Hash.REQUEST_PATH_NODES_IN_AREA_THIS_FRAME, candidate.X - 200f, candidate.Y - 200f, candidate.X + 200f, candidate.Y + 200f);
            if (!Function.Call<bool>(Hash.ARE_NODES_LOADED_FOR_AREA, candidate.X - 200f, candidate.Y - 200f, candidate.X + 200f, candidate.Y + 200f)) return;
            Vector3 node = World.GetNextPositionOnStreet(new Vector2(candidate.X, candidate.Y), false);
            hasCandidate = false;
            if (node == Vector3.Zero || Distance2D(node, candidate) > 300) return;
            double fromRider = Distance2D(node, rider);
            if (fromRider < min * 0.7 || fromRider > max * 1.3) return;
            string zoneName = World.GetZoneLocalizedName(node);
            Begin(node, "A spot in " + zoneName, null, rider);
        }

        /// <summary>Saved place in the distance range, never found ones first, never the previous target.</summary>
        private Place PickPlace(Vector3 rider, double min, double max, bool anyDistance)
        {
            List<Place> fresh = new List<Place>(), seen = new List<Place>(), far = new List<Place>();
            foreach (Place p in places)
            {
                if (p.Id == lastTargetId && places.Count > 1) continue;
                double d = Distance2D(rider, new Vector3(p.X, p.Y, 0));
                if (d < 200) continue;
                if (d < min || d > max) { far.Add(p); continue; }
                if (progress.PlaceFound(p.Id)) seen.Add(p); else fresh.Add(p);
            }
            List<Place> pool = fresh.Count > 0 ? fresh : seen.Count > 0 ? seen : anyDistance ? far : null;
            return pool == null || pool.Count == 0 ? null : pool[random.Next(pool.Count)];
        }

        private void Begin(Vector3 point, string name, string placeId, Vector3 rider)
        {
            target = point;
            targetName = name;
            targetPlaceId = placeId;
            lastTargetId = placeId;
            targetZone = Function.Call<string>(Hash.GET_NAME_OF_ZONE, point.X, point.Y, point.Z);
            targetZoneName = World.GetZoneLocalizedName(point);
            roundStart = rider;
            startDistance = Distance2D(rider, point);
            trendDistance = startDistance;
            trendAt = clock.Elapsed.TotalSeconds + 4;
            trend = "steady";
            hasTarget = true;
            phase = "searching";
            if (state == "preparing") state = "running";
            int areaM = (int)values["searchAreaM"];
            if (areaM > 0)
            {
                // A search circle that contains the spot, but not at its centre.
                double angle = random.NextDouble() * Math.PI * 2, offset = random.NextDouble() * areaM * 0.6;
                area = World.CreateBlip(new Vector3(point.X + (float)(Math.Sin(angle) * offset), point.Y + (float)(Math.Cos(angle) * offset), point.Z), areaM);
                area.Color = BlipColor.Yellow;
                area.Alpha = 90;
                area.Name = "Discover search area";
            }
            Sound("CHECKPOINT_NORMAL", "HUD_MINI_GAME_SOUNDSET");
            Notify("~y~Discover " + round + "/" + rounds + "~s~: somewhere in ~b~" + targetZoneName + "~s~, " + Km(startDistance) + " away" + (areaM > 0 ? " (yellow circle on the map)." : "."));
        }

        private void Found(Vector3 position)
        {
            double km = startDistance / 1000;
            double xpBase = 100 + 40 * km;
            double expectedS = startDistance / (18 / 3.6) * 1.6 + 60; // Generous: winding roads at 18 km/h.
            double speedBonus = roundS < expectedS ? xpBase * 0.5 * (1 - roundS / expectedS) : 0;
            double noHint = (int)values["searchAreaM"] == 0 ? xpBase * 0.25 : 0;
            int firstPlace = targetPlaceId != null && !progress.PlaceFound(targetPlaceId) ? 150 : 0;
            int newZone = targetZone != null && !progress.ZoneFound(targetZone) ? 50 : 0;
            double streak = progress.TouchStreak();
            long xp = (long)Math.Round((xpBase + speedBonus + noHint) * streak) + firstPlace + newZone;

            Dictionary<string, object> find = new Dictionary<string, object>
            {
                { "atUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) }, { "name", targetName }, { "zone", targetZoneName },
                { "timeS", Math.Round(roundS, 1) }, { "distanceM", Math.Round(startDistance) },
                { "xp", xp }, { "baseXp", Math.Round(xpBase) }, { "speedBonusXp", Math.Round(speedBonus) }, { "noHintBonusXp", Math.Round(noHint) },
                { "firstTimeBonusXp", firstPlace }, { "newZoneBonusXp", newZone }, { "streakDays", progress.StreakDays },
            };
            int levelBefore = progress.AddFind(xp, find, targetPlaceId, targetZone, roundS, startDistance);
            xpThisRun += xp;
            foundThisRun++;

            int levelAfter = Level(progress.Xp);
            phase = "found";
            foundAt = clock.Elapsed.TotalSeconds;
            RemoveArea();
            Sound("CHECKPOINT_PERFECT", "HUD_MINI_GAME_SOUNDSET");
            StringBuilder text = new StringBuilder("~g~Found: ").Append(targetName).Append("!~s~ +").Append(xp).Append(" XP");
            if (firstPlace > 0) text.Append(" (first visit)");
            else if (newZone > 0) text.Append(" (new zone)");
            if (progress.StreakDays > 1) text.Append(", ").Append(progress.StreakDays).Append("-day streak");
            Notify(text.ToString());
            if (levelAfter > levelBefore) { Sound("RANK_UP", "HUD_AWARDS"); Notify("~y~Level " + levelAfter + ": " + LevelName(levelAfter) + "!~s~"); }
        }

        private void Missed()
        {
            missedThisRun++;
            progress.AddMiss();
            phase = "found"; // Same pause, then the next round.
            foundAt = clock.Elapsed.TotalSeconds;
            RemoveArea();
            Sound("TIMER_STOP", "HUD_MINI_GAME_SOUNDSET");
            Notify("~r~Time is up.~s~ It was " + targetName + " in " + targetZoneName + ".");
        }

        private static int Level(long xp) { return GtbikeV1Progress.Level(xp); }
        private static string LevelName(int level) { return GtbikeV1Progress.LevelName(level); }

        private static string Heat(double metres)
        {
            if (metres < 150) return "burning";
            if (metres < 500) return "hot";
            if (metres < 1500) return "warm";
            if (metres < 4000) return "cool";
            return "cold";
        }

        // ---- HUD and helpers ----

        private void Hud(double now, double distance)
        {
            if (!(bool)values["showHud"] || now < nextHudAt) return;
            nextHudAt = now + 0.3;
            string heat = Heat(distance);
            string color = heat == "burning" ? "~r~" : heat == "hot" ? "~o~" : heat == "warm" ? "~y~" : "~b~";
            StringBuilder s = new StringBuilder("~y~Discover~s~ ").Append(round).Append('/').Append(rounds).Append("  ");
            s.Append(targetZoneName).Append("  ").Append(Km(distance)).Append("  ").Append(color).Append(heat.ToUpperInvariant()).Append("~s~");
            if (trend == "closer") s.Append(" ~g~closer~s~"); else if (trend == "farther") s.Append(" ~r~farther~s~");
            TimeSpan left = TimeSpan.FromSeconds(Math.Max(0, (int)values["roundTimeMin"] * 60.0 - roundS));
            s.Append("  ").Append(((int)left.TotalMinutes).ToString(CultureInfo.InvariantCulture)).Append(':').Append(left.Seconds.ToString("00", CultureInfo.InvariantCulture));
            s.Append("  Lv ").Append(Level(progress.Xp));
            GTA.UI.Screen.ShowSubtitle(s.ToString(), 400);
        }

        private void RemoveArea()
        {
            if (area == null) return;
            try { area.Delete(); } catch (Exception) { }
            area = null;
        }

        private void CleanUp()
        {
            RemoveArea();
            hasTarget = false;
            targetPlaceId = null;
        }

        private static string Km(double metres)
        {
            return metres >= 1000 ? (metres / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km" : Math.Round(metres).ToString(CultureInfo.InvariantCulture) + " m";
        }

        private static string Utc() { return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }

        private static Place FromDict(Dictionary<string, object> d)
        {
            string id = GtbikeV1Addons.Get(d, "id") as string, name = GtbikeV1Addons.Get(d, "name") as string;
            Dictionary<string, object> point = GtbikeV1Addons.Get(d, "point") as Dictionary<string, object>;
            float x, y;
            if (!ValidId(id) || name == null || point == null || !Coord(point, out x, out y)) return null;
            return new Place { Id = id, Name = name.Length > 40 ? name.Substring(0, 40) : name, X = x, Y = y };
        }

        private static double Num(Dictionary<string, object> d, string key)
        {
            object v = GtbikeV1Addons.Get(d, key);
            return v is int || v is long || v is decimal || v is double || v is float ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;
        }

        private static bool ValidId(string id)
        {
            if (id == null || id.Length < 11 || id.Length > 23 || !id.StartsWith("pl-", StringComparison.Ordinal)) return false;
            for (int i = 3; i < id.Length; i++) if (!Uri.IsHexDigit(id[i]) || char.IsUpper(id[i])) return false;
            return true;
        }

        private static bool Coord(Dictionary<string, object> d, out float x, out float y)
        {
            x = y = 0;
            object vx = GtbikeV1Addons.Get(d, "x"), vy = GtbikeV1Addons.Get(d, "y");
            bool numbers = (vx is int || vx is long || vx is decimal || vx is double || vx is float) && (vy is int || vy is long || vy is decimal || vy is double || vy is float);
            if (d.Count != 2 || !numbers) return false;
            x = Convert.ToSingle(vx, CultureInfo.InvariantCulture);
            y = Convert.ToSingle(vy, CultureInfo.InvariantCulture);
            return !float.IsNaN(x) && !float.IsNaN(y) && Math.Abs(x) <= 10000 && Math.Abs(y) <= 10000;
        }

        private static Dictionary<string, object> Point(float x, float y)
        {
            return new Dictionary<string, object> { { "x", Math.Round(x, 2) }, { "y", Math.Round(y, 2) } };
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
