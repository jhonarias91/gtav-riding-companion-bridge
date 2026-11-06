// Optional P8 reads. All reflection and position reads run on the adapter Tick.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1World
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int MaxWaypoints = 512;
        private const int MaxRiders = 64;

        private readonly PropertyInfo currentCourse, courseId, waypointCount, connected, name, wattsPerKg, speed, isPlayer;
        private readonly MethodInfo waypointAt, rank, playerCount, busiest, anyRemoteUsers;
        private readonly Type localCyclist, bot, remoteCyclist;
        private object lastCourse;
        private int lastWaypointCount;
        private DateTime nextReadUtc;
        private DateTime nextGeometryUtc;
        private Dictionary<string, object> lastRiders;
        private List<object> lastWaypoints;

        public GtbikeV1World(Func<string, Type> typeOf, Type vectorType)
        {
            Type course = typeOf("GTBikeV.Courses.Course");
            Type multiplayer = typeOf("GTBikeV.Network.Multiplayer");
            Type remotePlayers = typeOf("GTBikeV.Network.RemotePlayers");
            Type cyclist = typeOf("GTBikeV.Network.Cyclist");
            localCyclist = typeOf("GTBikeV.Network.LocalCyclist");
            bot = typeOf("GTBikeV.Network.Bot");
            remoteCyclist = typeOf("GTBikeV.Network.RemoteCyclist");
            currentCourse = Property(multiplayer, "CurrentCourse", course, Static);
            courseId = Property(course, "Id", typeof(ushort), Instance);
            waypointCount = Property(course, "WaypointCount", typeof(int), Instance);
            waypointAt = Method(course, "GetWaypointAt", vectorType, Instance, typeof(int));
            connected = Property(multiplayer, "IsConnected", typeof(bool), Static);
            name = Property(cyclist, "Name", typeof(string), Instance);
            wattsPerKg = Property(cyclist, "WattPerKg", typeof(float), Instance);
            speed = Property(cyclist, "Speed", typeof(float), Instance);
            isPlayer = Property(remoteCyclist, "IsPlayer", typeof(bool), Instance);
            rank = Method(remotePlayers, "GetSortedRankingCyclistList",
                cyclist == null ? null : typeof(List<>).MakeGenericType(cyclist), Static, typeof(ushort));
            playerCount = Method(remotePlayers, "PlayerCount", typeof(int), Static, typeof(ushort));
            busiest = Method(remotePlayers, "CourseIdWithMostPlayers", typeof(ushort), Static);
            anyRemoteUsers = Method(remotePlayers, "AreRemoteUsersInCourses", typeof(bool), Static);
        }

        public bool CanReadRiders
        {
            get
            {
                return currentCourse != null && courseId != null && connected != null && rank != null && playerCount != null
                    && name != null && wattsPerKg != null && speed != null && localCyclist != null && bot != null
                    && isPlayer != null && busiest != null && anyRemoteUsers != null;
            }
        }

        public bool CanReadWaypoints { get { return currentCourse != null && courseId != null && waypointCount != null && waypointAt != null; } }

        public void Capture(Dictionary<string, object> snapshot, DateTime now, Func<object> playerPosition, Func<object, object> point)
        {
            if (!object.Equals(snapshot["modLayer"], "dataReady"))
            {
                lastCourse = null;
                lastRiders = null;
                lastWaypoints = null;
                nextReadUtc = DateTime.MinValue;
                return;
            }
            if (now >= nextReadUtc)
            {
                nextReadUtc = now.AddSeconds(1);
                try { lastRiders = CanReadRiders ? ReadRiders() : null; }
                catch (Exception) { lastRiders = null; }
            }
            snapshot["p8Riders"] = lastRiders;

            object position;
            try { position = playerPosition(); }
            catch (Exception) { return; }
            if (position == null) return;

            object route = null;
            try { if (CanReadWaypoints) route = RouteGeometry(now, point); }
            catch (Exception) { lastCourse = null; lastWaypoints = null; }
            snapshot["p8Map"] = new Dictionary<string, object> { { "position", position }, { "route", route } };
        }

        private Dictionary<string, object> ReadRiders()
        {
            bool online = (bool)connected.GetValue(null, null);
            object course = currentCourse.GetValue(null, null);
            ushort activeId = course == null ? (ushort)0 : (ushort)courseId.GetValue(course, null);
            ushort candidate = online && course == null && (bool)anyRemoteUsers.Invoke(null, null)
                ? (ushort)busiest.Invoke(null, null) : (ushort)0;
            if ((candidate & 0x7fff) == 0 || Count(candidate) == 0) candidate = 0;
            ushort rankingId = activeId != 0 ? activeId : candidate;
            List<object> rows = new List<object>();
            bool hasRemoteHuman = false;
            if (rankingId != 0 && Count(rankingId) > 0)
            {
                int place = 0;
                foreach (object rider in (IEnumerable)rank.Invoke(null, new object[] { rankingId }))
                {
                    string kind = RiderKind(rider, online);
                    if (kind == "human") hasRemoteHuman = true;
                    if (++place > MaxRiders)
                    {
                        if (candidate == 0 || hasRemoteHuman) break;
                        continue;
                    }
                    string displayName = (string)name.GetValue(rider, null);
                    if (displayName != null && displayName.Length > 80) displayName = displayName.Substring(0, 80);
                    rows.Add(new Dictionary<string, object> {
                        { "rank", place }, { "kind", kind }, { "name", displayName },
                        { "wattsPerKg", NonNegative(wattsPerKg, rider) }, { "speedMps", NonNegative(speed, rider) }
                    });
                }
            }
            return new Dictionary<string, object> {
                { "connected", online }, { "courseKey", rankingId == 0 ? null : (object)rankingId },
                { "joinCourseKey", candidate != 0 && hasRemoteHuman ? (object)candidate : null }, { "riders", rows }
            };
        }

        private int Count(ushort id) { return (int)playerCount.Invoke(null, new object[] { id }); }

        private string RiderKind(object rider, bool online)
        {
            if (localCyclist.IsInstanceOfType(rider)) return "local";
            if (bot.IsInstanceOfType(rider)) return "bot";
            if (!online || !remoteCyclist.IsInstanceOfType(rider)) return "unknown";
            // RemoteCyclist uses this same distinction for its name, position and colour.
            return (bool)isPlayer.GetValue(rider, null) ? "human" : "bot";
        }

        private object RouteGeometry(DateTime now, Func<object, object> point)
        {
            object course = currentCourse.GetValue(null, null);
            if (course == null)
            {
                lastCourse = null;
                lastWaypoints = null;
                return null;
            }
            int count = (int)waypointCount.GetValue(course, null);
            if (count < 1) return null;
            if (!object.ReferenceEquals(course, lastCourse) || count != lastWaypointCount || now >= nextGeometryUtc)
            {
                int samples = Math.Min(count, MaxWaypoints);
                List<object> points = new List<object>(samples);
                for (int i = 0; i < samples; i++)
                {
                    int index = samples == 1 ? 0 : (int)((long)i * (count - 1) / (samples - 1));
                    points.Add(point(waypointAt.Invoke(course, new object[] { index })));
                }
                lastCourse = course;
                lastWaypointCount = count;
                lastWaypoints = points;
                nextGeometryUtc = now.AddSeconds(5); // The mod can edit a waypoint in place.
            }
            return new Dictionary<string, object> { { "courseKey", courseId.GetValue(course, null) }, { "waypoints", lastWaypoints } };
        }

        private static object NonNegative(PropertyInfo property, object target)
        {
            float value = (float)property.GetValue(target, null);
            return float.IsNaN(value) || float.IsInfinity(value) || value < 0 ? null : (object)value;
        }

        private static PropertyInfo Property(Type owner, string name, Type expected, BindingFlags flags)
        {
            PropertyInfo property = owner == null ? null : owner.GetProperty(name, flags);
            return property != null && property.PropertyType == expected && property.GetGetMethod(true) != null ? property : null;
        }

        private static MethodInfo Method(Type owner, string name, Type expected, BindingFlags flags, params Type[] parameters)
        {
            MethodInfo method = owner == null ? null : owner.GetMethod(name, flags, null, parameters, null);
            return method != null && method.ReturnType == expected ? method : null;
        }
    }
}
