// P18 rider progress: one local file with the rider's XP and level, earned by Discover finds and by completed GTBike V
// courses (routes), plus which routes were completed, how often, and the best time:
// %LOCALAPPDATA%\GTBikeVCompanion\v1\rider-progress.json. Routes are seen through Course.OnCourseCompleted, a public
// event GTBike V raises at the finish line (and each lap of a loop); the adapter only listens (GtbikeV1Extensions).
// Shared by the add-on host and the main script; SHVDN runs them one after another, the lock documents it. C# 5 only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using GTA.Native;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1Progress
    {
        public static readonly GtbikeV1Progress Shared = new GtbikeV1Progress();
        public const string FileName = "rider-progress.json";
        private const int HistoryKeep = 50;

        private readonly object gate = new object();
        public long Xp;
        public int Discoveries, RandomDiscoveries, Missed, StreakDays, BestStreakDays, RouteCompletions;
        public string LastPlayedDate; // local yyyy-MM-dd of the last XP
        public double TotalDistanceM;
        public readonly Dictionary<string, Dictionary<string, object>> Places = new Dictionary<string, Dictionary<string, object>>(); // place id -> count, firstAtUtc, lastAtUtc, bestTimeS
        public readonly Dictionary<string, int> Zones = new Dictionary<string, int>();
        public readonly Dictionary<string, Dictionary<string, object>> Routes = new Dictionary<string, Dictionary<string, object>>(); // route id -> record
        public readonly List<Dictionary<string, object>> History = new List<Dictionary<string, object>>(); // newest first, kind discover|route
        public Dictionary<string, object> LastFind;  // newest Discover find with its XP breakdown
        public Dictionary<string, object> LastRoute; // newest completed route with its XP breakdown
        public bool Dirty;

        // ---- file ----

        public void Load(string directory)
        {
            lock (gate)
            {
                try
                {
                    string path = Path.Combine(directory, FileName);
                    if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return;
                    Dictionary<string, object> d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                    if (d == null) return;
                    Xp = (long)Num(d, "xp");
                    Discoveries = (int)Num(d, "discoveries");
                    RandomDiscoveries = (int)Num(d, "randomDiscoveries");
                    Missed = (int)Num(d, "missed");
                    StreakDays = (int)Num(d, "streakDays");
                    BestStreakDays = (int)Num(d, "bestStreakDays");
                    RouteCompletions = (int)Num(d, "routeCompletions");
                    TotalDistanceM = Num(d, "totalDistanceM");
                    LastPlayedDate = Get(d, "lastPlayedDate") as string;
                    Copy(Get(d, "places") as Dictionary<string, object>, Places);
                    Copy(Get(d, "routes") as Dictionary<string, object>, Routes);
                    Dictionary<string, object> zones = Get(d, "zones") as Dictionary<string, object>;
                    if (zones != null) foreach (KeyValuePair<string, object> e in zones) if (e.Value is int) Zones[e.Key] = (int)e.Value;
                    IEnumerable history = Get(d, "history") as IEnumerable;
                    if (history != null) foreach (object h in history) { Dictionary<string, object> v = h as Dictionary<string, object>; if (v != null && History.Count < HistoryKeep) History.Add(v); }
                    LastFind = Get(d, "lastFind") as Dictionary<string, object>;
                    LastRoute = Get(d, "lastRoute") as Dictionary<string, object>;
                }
                catch (Exception) { } // A broken file starts a fresh profile; it is overwritten only on the next XP.
            }
        }

        /// <summary>Adds the progress file when it changed (written off the game thread by the main script).</summary>
        public void TakeFile(Dictionary<string, object> files)
        {
            lock (gate)
            {
                if (!Dirty) return;
                Dirty = false;
                Dictionary<string, object> places = new Dictionary<string, object>(), zones = new Dictionary<string, object>(), routes = new Dictionary<string, object>();
                foreach (KeyValuePair<string, Dictionary<string, object>> e in Places) places[e.Key] = e.Value;
                foreach (KeyValuePair<string, int> e in Zones) zones[e.Key] = e.Value;
                foreach (KeyValuePair<string, Dictionary<string, object>> e in Routes) routes[e.Key] = e.Value;
                int level = Level(Xp);
                files[FileName] = new Dictionary<string, object>
                {
                    { "format", "gtav-riding-companion-rider-progress-v1" }, { "updatedAtUtc", Utc() },
                    { "xp", Xp }, { "level", level }, { "levelName", LevelName(level) },
                    { "discoveries", Discoveries }, { "randomDiscoveries", RandomDiscoveries }, { "missed", Missed },
                    { "routeCompletions", RouteCompletions }, { "streakDays", StreakDays }, { "bestStreakDays", BestStreakDays },
                    { "lastPlayedDate", LastPlayedDate }, { "totalDistanceM", Math.Round(TotalDistanceM) },
                    { "places", places }, { "zones", zones }, { "routes", routes }, { "lastFind", LastFind }, { "lastRoute", LastRoute },
                    { "history", new List<Dictionary<string, object>>(History) },
                };
            }
        }

        /// <summary>Snapshot "riderProgress" (and Discover's "progress"): XP, level, totals, routes, last events.</summary>
        public Dictionary<string, object> Report()
        {
            lock (gate)
            {
                int level = Level(Xp);
                List<object> recent = new List<object>();
                for (int i = 0; i < History.Count && i < 5; i++) recent.Add(History[i]);
                List<object> routes = new List<object>();
                foreach (Dictionary<string, object> r in Routes.Values) routes.Add(r);
                return new Dictionary<string, object>
                {
                    { "xp", Xp }, { "level", level }, { "levelName", LevelName(level) }, { "levelStartXp", LevelXp(level) }, { "nextLevelXp", LevelXp(level + 1) },
                    { "discoveries", Discoveries }, { "placesFound", Places.Count }, { "zonesFound", Zones.Count },
                    { "streakDays", StreakDays }, { "bestStreakDays", BestStreakDays }, { "missed", Missed },
                    { "routeCompletions", RouteCompletions }, { "routesCompleted", Routes.Count },
                    { "totalDistanceM", Math.Round(TotalDistanceM) }, { "recent", recent }, { "lastFind", LastFind }, { "lastRoute", LastRoute }, { "routes", routes },
                };
            }
        }

        // ---- XP events ----

        /// <summary>Records a Discover find (XP already computed by the add-on) and returns the level before it.</summary>
        public int AddFind(long xp, Dictionary<string, object> find, string placeId, string zone, double timeS, double distanceM)
        {
            lock (gate)
            {
                int before = Level(Xp);
                Xp += xp;
                Discoveries++;
                if (placeId == null) RandomDiscoveries++;
                TotalDistanceM += distanceM;
                if (zone != null) { int n; Zones.TryGetValue(zone, out n); Zones[zone] = n + 1; }
                if (placeId != null)
                {
                    Dictionary<string, object> stats;
                    if (!Places.TryGetValue(placeId, out stats)) { stats = new Dictionary<string, object> { { "count", 0 }, { "firstAtUtc", Utc() } }; Places[placeId] = stats; }
                    stats["count"] = (int)Num(stats, "count") + 1;
                    stats["lastAtUtc"] = Utc();
                    object best = Get(stats, "bestTimeS");
                    if (best == null || timeS < Convert.ToDouble(best, CultureInfo.InvariantCulture)) stats["bestTimeS"] = Math.Round(timeS, 1);
                }
                find["kind"] = "discover";
                LastFind = find;
                Push(find);
                return before;
            }
        }

        public void AddMiss()
        {
            lock (gate) { Missed++; Dirty = true; }
        }

        /// <summary>A GTBike V course reached its finish (or a lap of a loop). Awards XP and marks the route completed.</summary>
        public void RouteCompleted(int routeId, string name, bool isLoop, uint lap, long elapsedMs, int distanceM, int avgPowerW, double avgSpeedMps)
        {
            if (elapsedMs < 30000 || distanceM < 200) return; // Not a real ride (glitch or instant finish).
            long xp;
            int before, after;
            bool first, best;
            lock (gate)
            {
                string key = routeId.ToString(CultureInfo.InvariantCulture);
                Dictionary<string, object> r;
                first = !Routes.TryGetValue(key, out r);
                if (first) { r = new Dictionary<string, object> { { "routeId", key }, { "completions", 0 }, { "laps", 0 }, { "firstAtUtc", Utc() } }; Routes[key] = r; }
                double timeS = elapsedMs / 1000.0;
                object previous = Get(r, "bestTimeS");
                best = !first && previous != null && timeS < Convert.ToDouble(previous, CultureInfo.InvariantCulture);
                double km = distanceM / 1000.0;
                double xpBase = (100 + 60 * km) * (isLoop ? 0.6 : 1.0); // A lap of a loop earns less than a whole course.
                double streak = TouchStreak();
                int firstBonus = first ? 300 : 0, bestBonus = best ? 100 : 0;
                xp = (long)Math.Round(xpBase * streak) + firstBonus + bestBonus;
                before = Level(Xp);
                Xp += xp;
                RouteCompletions++;
                TotalDistanceM += distanceM;
                r["name"] = name ?? r["routeId"];
                if (isLoop) r["laps"] = (int)Num(r, "laps") + 1; else r["completions"] = (int)Num(r, "completions") + 1;
                r["lastAtUtc"] = Utc();
                r["distanceM"] = distanceM;
                if (previous == null || timeS < Convert.ToDouble(previous, CultureInfo.InvariantCulture))
                {
                    r["bestTimeS"] = Math.Round(timeS, 1);
                    r["bestAvgPowerW"] = avgPowerW > 0 ? (object)avgPowerW : null;
                    r["bestAvgSpeedKmh"] = Math.Round(avgSpeedMps * 3.6, 1);
                }
                Dictionary<string, object> entry = new Dictionary<string, object>
                {
                    { "kind", "route" }, { "atUtc", Utc() }, { "name", name }, { "routeId", r["routeId"] }, { "loop", isLoop }, { "lap", (int)lap },
                    { "timeS", Math.Round(timeS, 1) }, { "distanceM", distanceM }, { "avgPowerW", avgPowerW }, { "xp", xp },
                    { "baseXp", Math.Round(xpBase) }, { "firstTimeBonusXp", firstBonus }, { "personalBestBonusXp", bestBonus }, { "streakDays", StreakDays },
                };
                LastRoute = entry;
                Push(entry);
                after = Level(Xp);
            }
            Notify("~y~+" + xp + " XP~s~ " + (isLoop ? "lap " + lap + " of " : "") + (name ?? "route") + (first ? " · first time!" : best ? " · personal best!" : "") + "  Level " + after);
            if (after > before) Notify("~y~Level " + after + ": " + LevelName(after) + "!~s~");
        }

        /// <summary>Daily streak: the first XP of a day after a day with XP adds one. Returns the XP multiplier (up to +50%).</summary>
        public double TouchStreak()
        {
            lock (gate)
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (LastPlayedDate != today)
                {
                    string yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    StreakDays = LastPlayedDate == yesterday ? StreakDays + 1 : 1;
                    BestStreakDays = Math.Max(BestStreakDays, StreakDays);
                    LastPlayedDate = today;
                    Dirty = true;
                }
                return 1 + Math.Min(0.5, 0.1 * Math.Max(0, StreakDays - 1));
            }
        }

        public bool PlaceFound(string placeId) { lock (gate) return placeId != null && Places.ContainsKey(placeId); }
        public bool ZoneFound(string zone) { lock (gate) return zone != null && Zones.ContainsKey(zone); }

        public Dictionary<string, object> PlaceStats(string placeId)
        {
            lock (gate) { Dictionary<string, object> s; return Places.TryGetValue(placeId, out s) ? s : null; }
        }

        private void Push(Dictionary<string, object> entry)
        {
            History.Insert(0, entry);
            if (History.Count > HistoryKeep) History.RemoveRange(HistoryKeep, History.Count - HistoryKeep);
            Dirty = true;
        }

        // ---- levels ----

        /// <summary>Level L starts at 250 * (L - 1)^2 XP: 0, 250, 1000, 2250, 4000...</summary>
        public static int Level(long xp) { return (int)Math.Floor(Math.Sqrt(Math.Max(0, xp) / 250.0)) + 1; }
        public static long LevelXp(int level) { return 250L * (level - 1) * (level - 1); }

        public static string LevelName(int level)
        {
            if (level >= 15) return "Legend of Los Santos";
            if (level >= 11) return "Cartographer";
            if (level >= 8) return "Navigator";
            if (level >= 5) return "Pathfinder";
            if (level >= 3) return "Explorer";
            return "Tourist";
        }

        // ---- helpers ----

        private static void Copy(Dictionary<string, object> from, Dictionary<string, Dictionary<string, object>> to)
        {
            if (from == null) return;
            foreach (KeyValuePair<string, object> e in from) { Dictionary<string, object> v = e.Value as Dictionary<string, object>; if (v != null) to[e.Key] = v; }
        }

        private static void Notify(string text)
        {
#pragma warning disable 618 // PostTicker exists only in SHVDN 3.9+; Show works in every ScriptHookVDotNet3 build.
            try { GTA.UI.Notification.Show(text); } catch (Exception) { }
#pragma warning restore 618
            try { Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "RANK_UP", "HUD_AWARDS", false); } catch (Exception) { }
        }

        private static string Utc() { return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        private static double Num(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v is int || v is long || v is decimal || v is double || v is float ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;
        }
    }
}
