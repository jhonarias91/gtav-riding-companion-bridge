// P18 add-on "Ghost segments": timed segments marked on the PC window map (start and finish, a loop when both are the
// same point). With the add-on on, riding through a start flag starts the clock and a see-through ghost rider replays
// your best (or last) attempt on that segment; the subtitle shows the live gap to it. The finish saves the attempt
// and, when faster, the new record and its trace.
// Files (adapter directory): ghost-segments.json (segments + records) and ghosts\<segment>-best.json / -last.json
// (samples [t, x, y, z, heading] at 4 Hz). Runs on GtbikeV1AddonHost every frame. GTA natives only; GTBike V power is
// read from the main script's snapshot for the average power. Allowed during a course. C# 5 only.
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
    public sealed class GtbikeV1Ghost : GtbikeV1Addon
    {
        public static readonly GtbikeV1AddonParam[] Params =
        {
            GtbikeV1AddonParam.Choice("ghostSource", new[] { "best", "last" }, "best", true),
            GtbikeV1AddonParam.Flag("showGhost", true, true),
            GtbikeV1AddonParam.Num("ghostOpacityPct", "integer", 20, 100, 10, 60, true),
            GtbikeV1AddonParam.Num("gateRadiusM", "integer", 8, 30, 1, 12, true),
            GtbikeV1AddonParam.Num("maxAttemptMin", "integer", 5, 120, 5, 60, true),
            GtbikeV1AddonParam.Flag("showHud", true, true),
        };

        public const int MaxSegments = 50;
        private const double SampleEveryS = 0.25;
        private const int MaxSamples = 30000;       // About 2 h at 4 Hz.
        private const float TeleportJumpM = 60f;
        private const float MarkerRangeM = 250f;
        private const string GhostBike = "tribike", GhostRider = "a_m_y_roadcyc_01";

        private sealed class Segment
        {
            public string Id, Name;
            public float StartX, StartY, FinishX, FinishY;
            public int Attempts;
            public double? LastTimeS;
            public Dictionary<string, object> Best; // timeS, setAtUtc, avgPowerW, avgSpeedKmh, distanceM
            public float? StartZ, FinishZ;          // ground heights, found once the area is loaded
            public Blip Blip;
        }

        /// <summary>One attempt: time, x, y, z, heading per sample, and the distance ridden at each sample.</summary>
        private sealed class Trace
        {
            public readonly List<double[]> Samples = new List<double[]>();
            public readonly List<double> Distance = new List<double>();
            public double TimeS;
        }

        private readonly List<Segment> segments = new List<Segment>();
        private readonly Dictionary<string, Trace> traces = new Dictionary<string, Trace>(); // "<id>-best" / "<id>-last"
        private readonly Dictionary<string, object> pendingFiles = new Dictionary<string, object>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private string directory;
        private bool segmentsDirty;

        // Run state.
        private string phase = "waiting";
        private Segment current;
        private string insideStart;      // segment whose start circle the rider is in (no restart until they leave it)
        private bool leftFinishZone;     // loops: the finish counts only after leaving its circle once
        private double elapsedS, distanceM, powerSum, powerTime, nextSampleAt, nextHudAt, deltaS;
        private bool deltaKnown;
        private Trace recording, ghostTrace;
        private Vector3 lastPosition;
        private Blip finishBlip;
        private Vehicle ghostBike;
        private Ped ghostRider;
        private int attemptsThisRun, recordsThisRun;
        private Dictionary<string, object> lastAttempt;

        public GtbikeV1Ghost() { Init(); }

        public override string Id { get { return "ghost"; } }
        public override bool AllowedDuringCourse { get { return true; } }
        protected override GtbikeV1AddonParam[] Parameters { get { return Params; } }
        public override bool BlocksTeleport { get { return Active && phase == "onSegment"; } } // Only an attempt.

        // ---- files ----

        public override void Load(string adapterDirectory)
        {
            directory = adapterDirectory;
            string path = Path.Combine(directory, "ghost-segments.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return;
            Dictionary<string, object> saved = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            IEnumerable list = saved == null ? null : GtbikeV1Addons.Get(saved, "segments") as IEnumerable;
            if (list == null) return;
            segments.Clear();
            foreach (object item in list)
            {
                Dictionary<string, object> d = item as Dictionary<string, object>;
                if (d == null || segments.Count >= MaxSegments) continue;
                string id = GtbikeV1Addons.Get(d, "id") as string, name = GtbikeV1Addons.Get(d, "name") as string;
                Dictionary<string, object> start = GtbikeV1Addons.Get(d, "start") as Dictionary<string, object>, finish = GtbikeV1Addons.Get(d, "finish") as Dictionary<string, object>;
                if (!ValidId(id) || name == null || start == null || finish == null) continue;
                Segment s = new Segment { Id = id, Name = name.Length > 40 ? name.Substring(0, 40) : name };
                if (!Coord(start, out s.StartX, out s.StartY) || !Coord(finish, out s.FinishX, out s.FinishY)) continue;
                object attempts = GtbikeV1Addons.Get(d, "attempts");
                s.Attempts = attempts is int ? (int)attempts : 0;
                object last = GtbikeV1Addons.Get(d, "lastTimeS");
                s.LastTimeS = IsNumber(last) ? (double?)Convert.ToDouble(last, CultureInfo.InvariantCulture) : null;
                Dictionary<string, object> best = GtbikeV1Addons.Get(d, "best") as Dictionary<string, object>;
                s.Best = best != null && IsNumber(GtbikeV1Addons.Get(best, "timeS")) ? best : null;
                segments.Add(s);
            }
        }

        public override void TakeFiles(Dictionary<string, object> files)
        {
            if (segmentsDirty)
            {
                segmentsDirty = false;
                List<object> list = new List<object>();
                foreach (Segment s in segments)
                    list.Add(new Dictionary<string, object> { { "id", s.Id }, { "name", s.Name }, { "start", Point(s.StartX, s.StartY) }, { "finish", Point(s.FinishX, s.FinishY) },
                        { "attempts", s.Attempts }, { "lastTimeS", s.LastTimeS }, { "best", s.Best } });
                files["ghost-segments.json"] = new Dictionary<string, object> { { "segments", list } };
            }
            foreach (KeyValuePair<string, object> f in pendingFiles) files[f.Key] = f.Value;
            pendingFiles.Clear();
        }

        private Trace TraceFor(string id, string kind)
        {
            string key = id + "-" + kind;
            Trace t;
            if (traces.TryGetValue(key, out t)) return t;
            t = null;
            try
            {
                string path = directory == null ? null : Path.Combine(directory, "ghosts", key + ".json");
                if (path != null && File.Exists(path) && new FileInfo(path).Length <= 8 * 1024 * 1024)
                {
                    JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    Dictionary<string, object> d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                    IEnumerable samples = GtbikeV1Addons.Get(d, "samples") as IEnumerable;
                    if (samples != null)
                    {
                        t = new Trace();
                        foreach (object row in samples)
                        {
                            IList values = row as IList;
                            if (values == null || values.Count < 5) continue;
                            double[] sample = new double[5];
                            for (int i = 0; i < 5; i++) sample[i] = Convert.ToDouble(values[i], CultureInfo.InvariantCulture);
                            Add(t, sample);
                        }
                        t.TimeS = t.Samples.Count == 0 ? 0 : t.Samples[t.Samples.Count - 1][0];
                        if (t.Samples.Count < 2) t = null;
                    }
                }
            }
            catch (Exception) { t = null; } // A broken trace only means no ghost.
            traces[key] = t;
            return t;
        }

        private void StoreTrace(string id, string kind, Trace t)
        {
            traces[id + "-" + kind] = t;
            List<object> rows = new List<object>();
            foreach (double[] s in t.Samples)
                rows.Add(new object[] { Math.Round(s[0], 2), Math.Round(s[1], 2), Math.Round(s[2], 2), Math.Round(s[3], 2), Math.Round(s[4], 1) });
            pendingFiles["ghosts/" + id + "-" + kind + ".json"] = new Dictionary<string, object> { { "segmentId", id }, { "timeS", Math.Round(t.TimeS, 2) }, { "samples", rows } };
        }

        private void DropTraces(string id)
        {
            foreach (string kind in new[] { "best", "last" })
            {
                traces.Remove(id + "-" + kind);
                pendingFiles["ghosts/" + id + "-" + kind + ".json"] = null;
            }
        }

        // ---- segment commands (PC map, phones) ----

        public override Dictionary<string, object> ItemCommand(string type, Dictionary<string, object> args)
        {
            string id = GtbikeV1Addons.Get(args, "segmentId") as string;
            if (!ValidId(id)) return Ack("rejected", "INVALID_REQUEST");
            Segment existing = segments.Find(x => x.Id == id);
            if (existing != null && current == existing) return Ack("rejected", "VALUE_NOT_ALLOWED"); // Not during its own attempt.
            if (type == "addons.removeSegment")
            {
                if (args.Count != 2) return Ack("rejected", "INVALID_REQUEST");
                if (existing == null) return Ack("noop", null);
                RemoveBlip(existing);
                segments.Remove(existing);
                DropTraces(id);
                segmentsDirty = true;
                return Ack("dispatched", null);
            }
            string name = GtbikeV1Addons.Get(args, "name") as string;
            Dictionary<string, object> start = GtbikeV1Addons.Get(args, "start") as Dictionary<string, object>, finish = GtbikeV1Addons.Get(args, "finish") as Dictionary<string, object>;
            float sx, sy, fx, fy;
            if (args.Count != 5 || name == null || name.Trim().Length == 0 || name.Length > 40 || start == null || finish == null
                || !Coord(start, out sx, out sy) || !Coord(finish, out fx, out fy)) return Ack("rejected", "INVALID_REQUEST");
            if (existing == null)
            {
                if (segments.Count >= MaxSegments) return Ack("rejected", "VALUE_NOT_ALLOWED");
                existing = new Segment { Id = id };
                segments.Add(existing);
            }
            else
            {
                bool moved = Math.Abs(existing.StartX - sx) > 5 || Math.Abs(existing.StartY - sy) > 5 || Math.Abs(existing.FinishX - fx) > 5 || Math.Abs(existing.FinishY - fy) > 5;
                if (moved) { existing.Best = null; existing.Attempts = 0; existing.LastTimeS = null; existing.StartZ = existing.FinishZ = null; DropTraces(id); }
                RemoveBlip(existing);
            }
            existing.Name = name.Trim();
            existing.StartX = sx; existing.StartY = sy; existing.FinishX = fx; existing.FinishY = fy;
            if (Active) AddBlip(existing);
            segmentsDirty = true;
            return Ack("dispatched", null);
        }

        // ---- lifecycle ----

        public override void Start(string id)
        {
            RemoveAll();
            requestId = id;
            reasonCode = null;
            result = null;
            state = "running";
            phase = "waiting";
            current = null;
            insideStart = null;
            attemptsThisRun = recordsThisRun = 0;
            lastAttempt = null;
            new Model(GhostBike).Request();
            new Model(GhostRider).Request();
            foreach (Segment s in segments) AddBlip(s);
            lastPosition = Vector3.Zero;
            insideStart = StartCircleAt(RiderPosition()); // Enabling inside a start circle does not start an attempt.
            Notify(segments.Count == 0 ? "~b~Ghost segments~s~: no segments yet. Mark a start and a finish on the PC map (right-click)."
                : "~b~Ghost segments~s~: " + segments.Count + (segments.Count == 1 ? " segment" : " segments") + " on the map. Ride through a start flag to race your ghost.");
        }

        public override void Frame(string gameControl, GtbikeV1AddonContext context)
        {
            if (!Active) return;
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists() || ped.IsDead) { if (current != null) Abandon("rider lost"); return; }
            if (Game.IsPaused || gameControl == "loading") return;
            double dt = Math.Min(0.25, Game.LastFrameTime);
            if (dt <= 0) return;
            Entity body = ped.IsInVehicle() && ped.CurrentVehicle != null ? (Entity)ped.CurrentVehicle : ped;
            Vector3 position = body.Position;
            float jump = lastPosition == Vector3.Zero ? 0 : (float)Distance2D(lastPosition, position);
            lastPosition = position;
            double now = clock.Elapsed.TotalSeconds;
            int radius = (int)values["gateRadiusM"];

            DrawGates(position, radius);
            if (current == null)
            {
                // Start gate: entering a start circle (nearest one) starts an attempt.
                Segment nearest = null;
                double nearestDistance = double.MaxValue;
                foreach (Segment s in segments)
                {
                    double d = Distance2D(position, new Vector3(s.StartX, s.StartY, 0));
                    if (d <= radius && d < nearestDistance) { nearest = s; nearestDistance = d; }
                }
                string inside = nearest == null ? null : nearest.Id;
                if (nearest != null && inside != insideStart && ped.IsInVehicle()) Begin(nearest, position, body.Heading);
                insideStart = inside;
                Hud(now, position);
                return;
            }

            if (jump > TeleportJumpM) { Abandon("teleported"); return; }
            elapsedS += dt;
            distanceM += jump;
            if (context.PowerW.HasValue) { powerSum += context.PowerW.Value * dt; powerTime += dt; }
            if (elapsedS >= nextSampleAt && recording.Samples.Count < MaxSamples)
            {
                nextSampleAt += SampleEveryS;
                Add(recording, new[] { elapsedS, position.X, position.Y, position.Z, body.Heading });
            }
            if (elapsedS > (int)values["maxAttemptMin"] * 60.0) { Abandon("too long"); return; }
            PlaceGhost();
            double toFinish = Distance2D(position, new Vector3(current.FinishX, current.FinishY, 0));
            if (toFinish > radius * 1.5) leftFinishZone = true;
            if (toFinish <= radius && leftFinishZone) { Complete(position, body.Heading); return; }
            Hud(now, position);
        }

        public override void End(string outcome)
        {
            bool wasActive = Active;
            state = outcome == "completed" ? "finished" : "aborted";
            result = new Dictionary<string, object>
            {
                { "outcome", outcome }, { "finishedAtUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) },
                { "attempts", attemptsThisRun }, { "records", recordsThisRun },
            };
            current = null;
            RemoveAll();
            if (wasActive) Notify("~b~Ghost segments~s~ off. " + attemptsThisRun + (attemptsThisRun == 1 ? " attempt, " : " attempts, ") + recordsThisRun + (recordsThisRun == 1 ? " new record." : " new records."));
        }

        public override void Shutdown()
        {
            if (Active) state = "aborted";
            current = null;
            RemoveAll();
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
            foreach (Segment s in segments)
                list.Add(new Dictionary<string, object> { { "id", s.Id }, { "name", s.Name }, { "start", Point(s.StartX, s.StartY) }, { "finish", Point(s.FinishX, s.FinishY) },
                    { "attempts", s.Attempts }, { "lastTimeS", s.LastTimeS }, { "best", s.Best } });
            r["segments"] = list;
            if (!Active) { r["run"] = null; return r; }
            Vector3 position = RiderPosition();
            Dictionary<string, object> run = new Dictionary<string, object>();
            run["phase"] = phase;
            run["segmentId"] = current == null ? null : current.Id;
            run["segment"] = current == null ? null : current.Name;
            run["elapsedS"] = Math.Round(elapsedS, 1);
            run["deltaS"] = current != null && deltaKnown ? (object)Math.Round(deltaS, 1) : null;
            run["bestS"] = current != null && current.Best != null ? GtbikeV1Addons.Get(current.Best, "timeS") : null;
            run["ghost"] = ghostBike != null && ghostBike.Exists();
            run["distanceM"] = Math.Round(distanceM);
            run["last"] = lastAttempt;
            Segment target = current;
            string kind = "finish";
            if (target == null)
            {
                kind = "start";
                double best = double.MaxValue;
                foreach (Segment s in segments)
                {
                    double d = Distance2D(position, new Vector3(s.StartX, s.StartY, 0));
                    if (d < best) { best = d; target = s; }
                }
            }
            run["next"] = target == null ? null : new Dictionary<string, object>
            {
                { "x", kind == "finish" ? target.FinishX : target.StartX }, { "y", kind == "finish" ? target.FinishY : target.StartY }, { "kind", kind },
                { "distanceM", Math.Round(Distance2D(position, new Vector3(kind == "finish" ? target.FinishX : target.StartX, kind == "finish" ? target.FinishY : target.StartY, 0))) },
            };
            r["run"] = run;
            return r;
        }

        // ---- attempts ----

        private void Begin(Segment s, Vector3 position, float heading)
        {
            current = s;
            phase = "onSegment";
            elapsedS = distanceM = powerSum = powerTime = 0;
            nextSampleAt = SampleEveryS;
            deltaKnown = false;
            leftFinishZone = false;
            recording = new Trace();
            Add(recording, new double[] { 0, position.X, position.Y, position.Z, heading });
            ghostTrace = TraceFor(s.Id, (string)values["ghostSource"]);
            if (ghostTrace == null && (string)values["ghostSource"] == "last") ghostTrace = TraceFor(s.Id, "best");
            finishBlip = World.CreateBlip(new Vector3(s.FinishX, s.FinishY, s.FinishZ ?? position.Z));
            finishBlip.Sprite = BlipSprite.RaceFinish;
            finishBlip.Name = s.Name + " finish";
            finishBlip.ShowRoute = true;
            Sound("CHECKPOINT_NORMAL", "HUD_MINI_GAME_SOUNDSET");
            Notify("~b~" + s.Name + "~s~: go!" + (s.Best != null ? " Best " + Clock(Num(s.Best, "timeS")) + "." : " First attempt: this one sets the ghost."));
        }

        private void Complete(Vector3 position, float heading)
        {
            Segment s = current;
            Add(recording, new double[] { elapsedS, position.X, position.Y, position.Z, heading });
            recording.TimeS = elapsedS;
            double? previous = s.Best == null ? (double?)null : Num(s.Best, "timeS");
            bool record = !previous.HasValue || elapsedS < previous.Value;
            s.Attempts++;
            s.LastTimeS = Math.Round(elapsedS, 2);
            attemptsThisRun++;
            StoreTrace(s.Id, "last", recording);
            if (record)
            {
                recordsThisRun++;
                s.Best = new Dictionary<string, object>
                {
                    { "timeS", Math.Round(elapsedS, 2) }, { "setAtUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) },
                    { "avgPowerW", powerTime > elapsedS * 0.5 ? (object)Math.Round(powerSum / powerTime) : null },
                    { "avgSpeedKmh", elapsedS > 0 ? (object)Math.Round(distanceM / elapsedS * 3.6, 1) : null }, { "distanceM", Math.Round(distanceM) },
                };
                StoreTrace(s.Id, "best", recording);
            }
            segmentsDirty = true;
            double? delta = previous.HasValue ? elapsedS - previous.Value : (double?)null;
            lastAttempt = new Dictionary<string, object>
            {
                { "segmentId", s.Id }, { "segment", s.Name }, { "timeS", Math.Round(elapsedS, 2) }, { "deltaS", delta.HasValue ? (object)Math.Round(delta.Value, 2) : null }, { "record", record },
            };
            Sound(record ? "CHECKPOINT_PERFECT" : "CHECKPOINT_NORMAL", "HUD_MINI_GAME_SOUNDSET");
            Notify("~b~" + s.Name + "~s~ " + Clock(elapsedS) + (record ? (delta.HasValue ? "  ~g~NEW RECORD " + Signed(delta.Value) + "~s~" : "  ~g~first time set~s~") : "  ~r~" + Signed(delta.Value) + "~s~ vs best"));
            EndAttempt();
        }

        private void Abandon(string why)
        {
            if (current != null) Notify("~b~" + current.Name + "~s~ attempt cancelled (" + why + ").");
            EndAttempt();
        }

        private void EndAttempt()
        {
            current = null;
            phase = "waiting";
            recording = ghostTrace = null;
            RemoveGhost();
            if (finishBlip != null) { try { finishBlip.Delete(); } catch (Exception) { } finishBlip = null; }
            // The rider is usually still inside a start circle (loops, back-to-back segments): no restart until they leave it.
            insideStart = StartCircleAt(RiderPosition());
        }

        private string StartCircleAt(Vector3 position)
        {
            foreach (Segment s in segments)
                if (Distance2D(position, new Vector3(s.StartX, s.StartY, 0)) <= (int)values["gateRadiusM"]) return s.Id;
            return null;
        }

        // ---- ghost ----

        private void PlaceGhost()
        {
            if (ghostTrace == null || !(bool)values["showGhost"]) { RemoveGhost(); deltaKnown = ghostTrace != null && Delta(); return; }
            deltaKnown = Delta();
            if (ghostBike == null || !ghostBike.Exists())
            {
                if (!SpawnGhost()) return;
            }
            double[] p = At(ghostTrace, Math.Min(elapsedS, ghostTrace.TimeS));
            ghostBike.PositionNoOffset = new Vector3((float)p[1], (float)p[2], (float)p[3]);
            ghostBike.Heading = (float)p[4];
            int alpha = (int)Math.Round((int)values["ghostOpacityPct"] * 2.55);
            if (ghostBike.Opacity != alpha) { ghostBike.Opacity = alpha; if (ghostRider != null && ghostRider.Exists()) ghostRider.Opacity = alpha; }
        }

        /// <summary>Live gap: own time now minus the ghost's time at the same distance ridden (negative = ahead).</summary>
        private bool Delta()
        {
            if (ghostTrace == null || ghostTrace.Distance.Count < 2) return false;
            List<double> d = ghostTrace.Distance;
            double ghostTime;
            if (distanceM >= d[d.Count - 1]) ghostTime = ghostTrace.TimeS;
            else
            {
                int lo = 0, hi = d.Count - 1;
                while (hi - lo > 1) { int mid = (lo + hi) / 2; if (d[mid] <= distanceM) lo = mid; else hi = mid; }
                double span = d[hi] - d[lo];
                double f = span > 0 ? (distanceM - d[lo]) / span : 0;
                ghostTime = ghostTrace.Samples[lo][0] + f * (ghostTrace.Samples[hi][0] - ghostTrace.Samples[lo][0]);
            }
            deltaS = elapsedS - ghostTime;
            return true;
        }

        private bool SpawnGhost()
        {
            Model bike = new Model(GhostBike), rider = new Model(GhostRider);
            if (!bike.IsLoaded || !rider.IsLoaded) { bike.Request(); rider.Request(); return false; }
            double[] p = At(ghostTrace, Math.Min(elapsedS, ghostTrace.TimeS));
            ghostBike = World.CreateVehicle(bike, new Vector3((float)p[1], (float)p[2], (float)p[3]), (float)p[4]);
            if (ghostBike == null || !ghostBike.Exists()) { ghostBike = null; return false; }
            ghostBike.IsInvincible = true;
            Function.Call(Hash.SET_ENTITY_COLLISION, ghostBike.Handle, false, false);
            Function.Call(Hash.FREEZE_ENTITY_POSITION, ghostBike.Handle, true); // Moved by hand every frame; no physics.
            ghostRider = ghostBike.CreatePedOnSeat(VehicleSeat.Driver, rider);
            if (ghostRider != null && ghostRider.Exists())
            {
                ghostRider.IsInvincible = true;
                ghostRider.BlockPermanentEvents = true;
                Function.Call(Hash.SET_ENTITY_COLLISION, ghostRider.Handle, false, false);
            }
            return true;
        }

        private void RemoveGhost()
        {
            try { if (ghostRider != null && ghostRider.Exists()) ghostRider.Delete(); } catch (Exception) { }
            try { if (ghostBike != null && ghostBike.Exists()) ghostBike.Delete(); } catch (Exception) { }
            ghostRider = null;
            ghostBike = null;
        }

        private static double[] At(Trace t, double time)
        {
            List<double[]> s = t.Samples;
            if (time <= s[0][0]) return s[0];
            if (time >= s[s.Count - 1][0]) return s[s.Count - 1];
            int lo = 0, hi = s.Count - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (s[mid][0] <= time) lo = mid; else hi = mid; }
            double span = s[hi][0] - s[lo][0];
            double f = span > 0 ? (time - s[lo][0]) / span : 0;
            double dh = s[hi][4] - s[lo][4];
            while (dh > 180) dh -= 360;
            while (dh < -180) dh += 360;
            return new[] { time, s[lo][1] + f * (s[hi][1] - s[lo][1]), s[lo][2] + f * (s[hi][2] - s[lo][2]), s[lo][3] + f * (s[hi][3] - s[lo][3]), s[lo][4] + f * dh };
        }

        private static void Add(Trace t, double[] sample)
        {
            double d = 0;
            if (t.Samples.Count > 0)
            {
                double[] last = t.Samples[t.Samples.Count - 1];
                double dx = sample[1] - last[1], dy = sample[2] - last[2];
                d = t.Distance[t.Distance.Count - 1] + Math.Sqrt(dx * dx + dy * dy);
            }
            t.Samples.Add(sample);
            t.Distance.Add(d);
        }

        // ---- map, markers, HUD ----

        private void AddBlip(Segment s)
        {
            RemoveBlip(s);
            s.Blip = World.CreateBlip(new Vector3(s.StartX, s.StartY, s.StartZ ?? 0f));
            s.Blip.Sprite = BlipSprite.RaceBike;
            s.Blip.Color = BlipColor.Blue;
            s.Blip.Scale = 0.8f;
            s.Blip.Name = "Segment: " + s.Name;
        }

        private static void RemoveBlip(Segment s)
        {
            if (s.Blip == null) return;
            try { s.Blip.Delete(); } catch (Exception) { }
            s.Blip = null;
        }

        private void RemoveAll()
        {
            RemoveGhost();
            if (finishBlip != null) { try { finishBlip.Delete(); } catch (Exception) { } finishBlip = null; }
            foreach (Segment s in segments) RemoveBlip(s);
        }

        private void DrawGates(Vector3 rider, int radius)
        {
            foreach (Segment s in segments)
            {
                bool own = current == s;
                if (current == null || own) Gate(rider, s.StartX, s.StartY, ref s.StartZ, radius, Color.FromArgb(90, 60, 140, 255), current == null);
                if (own) Gate(rider, s.FinishX, s.FinishY, ref s.FinishZ, radius, Color.FromArgb(110, 255, 255, 255), true);
            }
        }

        private static void Gate(Vector3 rider, float x, float y, ref float? z, int radius, Color color, bool show)
        {
            if (!show || Distance2D(rider, new Vector3(x, y, 0)) > MarkerRangeM) return;
            if (!z.HasValue)
            {
                float ground;
                if (World.GetGroundHeight(new Vector3(x, y, rider.Z + 50f), out ground, GetGroundHeightMode.Normal) && ground != 0f) z = ground;
                else return; // Area not loaded yet.
            }
#pragma warning disable 618 // Renamed Cylinder in SHVDN 3.9; the old name keeps older ScriptHookVDotNet3 builds working.
            World.DrawMarker(MarkerType.VerticalCylinder, new Vector3(x, y, z.Value - 0.5f), Vector3.Zero, Vector3.Zero, new Vector3(radius * 2f, radius * 2f, 3f), color);
#pragma warning restore 618
        }

        private void Hud(double now, Vector3 position)
        {
            if (!(bool)values["showHud"] || now < nextHudAt) return;
            nextHudAt = now + 0.25;
            StringBuilder s = new StringBuilder("~b~Ghost~s~ ");
            if (current == null)
            {
                if (segments.Count == 0) { s.Append("Mark a segment on the PC map (right-click)"); GTA.UI.Screen.ShowSubtitle(s.ToString(), 400); return; }
                Segment nearest = null;
                double best = double.MaxValue;
                foreach (Segment g in segments) { double d = Distance2D(position, new Vector3(g.StartX, g.StartY, 0)); if (d < best) { best = d; nearest = g; } }
                if (best > 2000) return; // Free ride far from every segment: keep the screen clean.
                s.Append(nearest.Name).Append(" start ").Append(Math.Round(best).ToString(CultureInfo.InvariantCulture)).Append(" m");
                if (nearest.Best != null) s.Append("  Best ").Append(Clock(Num(nearest.Best, "timeS")));
                GTA.UI.Screen.ShowSubtitle(s.ToString(), 400);
                return;
            }
            s.Append(current.Name).Append("  ").Append(Clock(elapsedS));
            if (deltaKnown) s.Append(deltaS <= 0 ? "  ~g~" : "  ~r~").Append(Signed(deltaS)).Append("~s~");
            else s.Append("  no ghost yet");
            double toFinish = Distance2D(position, new Vector3(current.FinishX, current.FinishY, 0));
            s.Append("  Finish ").Append(toFinish >= 1000 ? (toFinish / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km" : Math.Round(toFinish).ToString(CultureInfo.InvariantCulture) + " m");
            GTA.UI.Screen.ShowSubtitle(s.ToString(), 400);
        }

        // ---- helpers ----

        private static string Clock(double seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture) + "." + (t.Milliseconds / 100).ToString(CultureInfo.InvariantCulture);
        }

        private static string Signed(double seconds)
        {
            return (seconds <= 0 ? "-" : "+") + Math.Abs(seconds).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        private static double Num(Dictionary<string, object> d, string key)
        {
            object v = GtbikeV1Addons.Get(d, key);
            return IsNumber(v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;
        }

        private static bool IsNumber(object v)
        {
            return v is int || v is long || v is decimal || v is double || v is float;
        }

        private static bool ValidId(string id)
        {
            if (id == null || id.Length < 5 || id.Length > 24 || !id.StartsWith("seg-", StringComparison.Ordinal)) return false;
            for (int i = 4; i < id.Length; i++) if (!Uri.IsHexDigit(id[i]) || char.IsUpper(id[i])) return false;
            return true;
        }

        private static bool Coord(Dictionary<string, object> d, out float x, out float y)
        {
            x = y = 0;
            object vx = GtbikeV1Addons.Get(d, "x"), vy = GtbikeV1Addons.Get(d, "y");
            if (d.Count != 2 || !IsNumber(vx) || !IsNumber(vy)) return false;
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
