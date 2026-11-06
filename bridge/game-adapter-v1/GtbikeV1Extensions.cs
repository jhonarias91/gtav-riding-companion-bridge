// GTAV Riding Companion adapter v1 - P3 (routes) and P4 (profile, ride settings, appearance) for profile gtbikev-0804.
// Static evidence: reference/GTBikeV-0.8.0.4-decompiled (UI/Menus.cs, UI/CourseSelectionView.cs, Courses/*, Config.cs).
// Separate from GtbikeV1Binding so a missing P3/P4 member never degrades the P2 binding status: each feature
// reports its own availability and only exposes its command types when every member it needs resolved.
// Never resolved here: ERG, target power, workout selection/end, save/discard, generic keys.
// All calls run on the adapter Tick. C# 5 only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1Extensions
    {
        private const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly int[] Difficulty = { 0, 25, 50, 75, 100, 255 };
        private const int MaxRoutes = 2000;

        private readonly bool writable;
        private readonly List<string> missing = new List<string>();

        // Routes
        private readonly FieldInfo courseView, courseMenuItem;
        private readonly PropertyInfo currentCourse, courseId;
        private readonly FieldInfo courseLoop, courseReverse;
        private readonly bool routesRead, routesWrite;

        // Settings
        private readonly Type config, menus;
        private readonly PropertyInfo weight, ftp, hrMax, difficulty, roadFeel, autopilot, imperial, bikeName, pedName, weightUnit, ftpLabel;
        private readonly FieldInfo avoidObstacles, bikes, peds;
        private readonly FieldInfo weightItem, ftpItem, autopilotItem, obstaclesItem, difficultyItem, roadFeelItem, bikeItem, pedItem;
        private readonly MethodInfo listTrigger; // LemonUI NativeListItem<string>.TriggerEvent(int, Direction): raises ItemChanged.
        private readonly FieldInfo listChanged;  // NativeListItem<string>.ItemChanged backing field: finds the mod's script instance.
        private readonly Type modScript;         // GTBikeV.GTBikeVScript
        private readonly bool settingsRead, settingsWrite;

        /// <summary>Receives exceptions raised by work run on the mod's own Tick (they must never reach the mod's script).</summary>
        public Action<Exception> DeferredError;

        private object lastItemList;
        private int lastItemCount = -1;
        private string catalogRevision;
        private Dictionary<string, object> pendingCatalog;
        private Dictionary<string, object> lastSettings;
        private DateTime nextSettingsReadUtc = DateTime.MinValue;

        public GtbikeV1Extensions(Func<string, Type> typeOf, bool versionMatches)
        {
            writable = versionMatches;
            menus = typeOf("GTBikeV.UI.Menus");
            config = typeOf("GTBikeV.Config");
            Type course = typeOf("GTBikeV.Courses.Course");
            Type multiplayer = typeOf("GTBikeV.Network.Multiplayer");

            courseView = StaticField(menus, "courseSelectionView");
            courseMenuItem = StaticField(menus, "courseSelectionMenuItem");
            currentCourse = multiplayer == null || course == null ? null : multiplayer.GetProperty("CurrentCourse", S);
            if (currentCourse != null && currentCourse.PropertyType != course) currentCourse = null;
            if (currentCourse == null) missing.Add("Multiplayer.CurrentCourse");
            courseId = course == null ? null : course.GetProperty("Id", I);
            if (courseId != null && courseId.PropertyType != typeof(ushort)) courseId = null;
            if (courseId == null) missing.Add("Course.Id");
            courseLoop = InstanceField(course, "Loop", typeof(bool));
            courseReverse = InstanceField(course, "Reverse", typeof(bool));
            routesRead = courseView != null && currentCourse != null && courseId != null && courseLoop != null && courseReverse != null;
            routesWrite = routesRead && courseMenuItem != null && writable;

            weight = StaticProp(config, "UserWeight", typeof(float));
            ftp = StaticProp(config, "UserFTP", typeof(float));
            hrMax = StaticProp(config, "UserHRMax", typeof(byte));
            difficulty = StaticProp(config, "TrainerDifficulty", typeof(byte));
            roadFeel = StaticProp(config, "ActivateRoadFeel", typeof(bool));
            imperial = StaticProp(config, "Imperial", typeof(bool));
            bikeName = StaticProp(config, "SelectedBikeName", typeof(string));
            pedName = StaticProp(config, "SelectedPedName", typeof(string));
            bikes = StaticField(config, "AvailableBicycles");
            peds = StaticField(config, "AvailablePeds");
            autopilot = StaticProp(menus, "AutoPilot", typeof(bool));
            avoidObstacles = StaticField(menus, "AvoidObstacles");
            if (avoidObstacles != null && avoidObstacles.FieldType != typeof(bool)) { avoidObstacles = null; missing.Add("Menus.AvoidObstacles"); }
            weightUnit = StaticProp(menus, "userWeightUnit", typeof(string));
            ftpLabel = StaticProp(menus, "UserFTP", typeof(float));
            weightItem = StaticField(menus, "weightMenuItem");
            ftpItem = StaticField(menus, "ftpMenuItem");
            autopilotItem = StaticField(menus, "autodriveMenuItem");
            obstaclesItem = StaticField(menus, "avoidObstaclesMenuItem");
            difficultyItem = StaticField(menus, "trainerDiffMenuItem");
            roadFeelItem = StaticField(menus, "trainerRoadFeelMenuItem");
            bikeItem = StaticField(menus, "bikeListMenuItem");
            pedItem = StaticField(menus, "pedListMenuItem");
            // Needed only when the menu item already shows the wanted entry (see SelectAppearance); never gates writes.
            listTrigger = bikeItem == null ? null : bikeItem.FieldType.GetMethod("TriggerEvent", I);
            if (listTrigger != null)
            {
                ParameterInfo[] p = listTrigger.GetParameters();
                if (p.Length != 2 || p[0].ParameterType != typeof(int) || !p[1].ParameterType.IsEnum) listTrigger = null;
            }
            if (listTrigger == null) missing.Add("NativeListItem.TriggerEvent");
            listChanged = bikeItem == null ? null : bikeItem.FieldType.GetField("ItemChanged", I);
            if (listChanged != null && !typeof(Delegate).IsAssignableFrom(listChanged.FieldType)) listChanged = null;
            if (listChanged == null) missing.Add("NativeListItem.ItemChanged");
            modScript = typeOf("GTBikeV.GTBikeVScript");
            if (modScript == null || modScript.GetEvent("Tick", BindingFlags.Public | BindingFlags.Instance) == null) { modScript = null; missing.Add("GTBikeVScript.Tick"); }
            settingsRead = weight != null && ftp != null && hrMax != null && difficulty != null && roadFeel != null && autopilot != null && avoidObstacles != null
                && bikeName != null && pedName != null && bikes != null && peds != null;
            settingsWrite = settingsRead && writable && weightUnit != null && ftpLabel != null && imperial != null && weightItem != null && ftpItem != null
                && autopilotItem != null && obstaclesItem != null && difficultyItem != null && roadFeelItem != null && bikeItem != null && pedItem != null;
        }

        public IList<string> Missing { get { return missing; } }

        /// <summary>Command types this build can execute.</summary>
        public IEnumerable<string> Commands()
        {
            if (routesWrite) { yield return "routes.apply"; yield return "routes.preview"; yield return "ui.openPanel"; yield return "ui.closePanel"; }
            if (settingsWrite) { yield return "profile.update"; yield return "rideSettings.update"; yield return "appearance.select"; }
        }

        /// <summary>Adds activeRoute, routes and settings to the snapshot.</summary>
        public void Capture(Dictionary<string, object> snap, DateTime nowUtc)
        {
            if (routesRead)
            {
                try { CaptureRoutes(snap); }
                catch (Exception) { snap["routes"] = null; snap["activeRoute"] = null; }
            }
            if (settingsRead)
            {
                if (nowUtc >= nextSettingsReadUtc || lastSettings == null)
                {
                    nextSettingsReadUtc = nowUtc.AddSeconds(1); // INI-backed getters: 1 Hz is enough.
                    try { lastSettings = ReadSettings(); } catch (Exception) { lastSettings = null; }
                }
                snap["settings"] = lastSettings;
            }
        }

        /// <summary>Returns a new catalog document once per revision change, else null.</summary>
        public Dictionary<string, object> TakeCatalog()
        {
            Dictionary<string, object> c = pendingCatalog;
            pendingCatalog = null;
            return c;
        }

        // ---------------- routes ----------------

        private object View { get { return courseView.GetValue(null); } }

        private IList Items(object view)
        {
            PropertyInfo p = view == null ? null : view.GetType().GetProperty("ItemList", I);
            return p == null ? null : p.GetValue(view, null) as IList;
        }

        private static int Index(object view)
        {
            PropertyInfo p = view == null ? null : view.GetType().GetProperty("Index", I);
            object v = p == null ? null : p.GetValue(view, null);
            return v is int ? (int)v : -1;
        }

        private static bool Visible(object element)
        {
            PropertyInfo p = element == null ? null : element.GetType().GetProperty("Visible", I);
            return p != null && object.Equals(p.GetValue(element, null), true);
        }

        private void CaptureRoutes(Dictionary<string, object> snap)
        {
            object course = currentCourse.GetValue(null, null);
            if (course == null) snap["activeRoute"] = null;
            else
            {
                Dictionary<string, object> active = new Dictionary<string, object>();
                ushort id = (ushort)courseId.GetValue(course, null);
                active["id"] = ((int)(id & 0x7FFF)).ToString(CultureInfo.InvariantCulture);
                active["loop"] = (bool)courseLoop.GetValue(course);
                active["reverse"] = (bool)courseReverse.GetValue(course);
                snap["activeRoute"] = active;
            }

            object view = View;
            IList items = Items(view);
            if (items != null && (!object.ReferenceEquals(items, lastItemList) || items.Count != lastItemCount)) BuildCatalog(items);
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["catalogRevision"] = catalogRevision;
            bool open = Visible(view);
            r["panelOpen"] = open;
            r["routesEnabled"] = Enabled(courseMenuItem == null ? null : courseMenuItem.GetValue(null));
            r["previewRouteId"] = null; r["previewLoop"] = null; r["previewReverse"] = null;
            int index = Index(view);
            if (open && items != null && index > 0 && index < items.Count)
            {
                object info = items[index];
                r["previewRouteId"] = RouteId(info);
                r["previewLoop"] = Checked(Member(view, "_loopCourse"));
                r["previewReverse"] = Checked(Member(view, "_reverseCourse"));
            }
            snap["routes"] = r;
        }

        private void BuildCatalog(IList items)
        {
            lastItemList = items;
            lastItemCount = items.Count;
            List<object> routes = new List<object>();
            StringBuilder key = new StringBuilder();
            for (int n = 1; n < items.Count && routes.Count < MaxRoutes; n++) // Item 0 is the localized "No course" string.
            {
                object info = items[n];
                string id = RouteId(info);
                if (id == null) continue;
                Dictionary<string, object> route = new Dictionary<string, object>();
                route["id"] = id;
                route["name"] = Text(Get(info, "Name"), 120);
                object author = Get(info, "Author");
                route["author"] = Text(Get(author, "Name"), 120);
                route["distanceM"] = Number(Get(info, "Distance"), 1000.0); // CourseInfo.Distance is km.
                route["elevationGainM"] = Number(Get(info, "ElevationGain"), 1.0);
                route["elevationLossM"] = Number(Get(info, "ElevationLoss"), 1.0);
                route["maxElevationM"] = Number(Get(info, "MaxElevation"), 1.0);
                route["categories"] = Strings(Get(info, "Category"));
                route["types"] = Strings(Get(info, "Type"));
                bool loop = object.Equals(Get(info, "Loop"), true);
                route["loopAllowed"] = loop;
                route["reverseAllowed"] = loop; // The selector shows Reverse only for loop routes.
                route["version"] = Text(Get(info, "Version"), 32);
                route["description"] = Text(Get(info, "Description"), 1200);
                route["notes"] = Text(Get(info, "Notes"), 1200);
                routes.Add(route);
                key.Append(id).Append('|').Append(route["name"]).Append('|').Append(Convert.ToString(route["distanceM"], CultureInfo.InvariantCulture)).Append('\n');
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key.ToString()));
                catalogRevision = "catalog-" + BitConverter.ToString(hash, 0, 6).Replace("-", "").ToLowerInvariant();
            }
            Dictionary<string, object> catalog = new Dictionary<string, object>();
            catalog["catalogRevision"] = catalogRevision;
            catalog["routes"] = routes;
            pendingCatalog = catalog;
        }

        private static string RouteId(object info)
        {
            object id = Get(info, "Id");
            if (!(id is ushort)) return null;
            return ((int)((ushort)id & 0x7FFF)).ToString(CultureInfo.InvariantCulture);
        }

        private int FindIndex(IList items, string routeId)
        {
            if (routeId == null) return 0;
            for (int n = 1; n < items.Count; n++) if (RouteId(items[n]) == routeId) return n;
            return -1;
        }

        /// <summary>
        /// routes.apply: same path as the selector's Select button (CourseSelectionView.SelectCourseButton_OnRelease).
        /// Allowed during a workout, as the mod's menu is (user decision, 6 Nov 2026): InitializeCourse saves the current
        /// activity (FIT over 100 m) and CreateNewActivity re-selects the unfinished workout, which restarts from its
        /// countdown with ERG. Free ride keeps the workout running but ClearBots also removes its bot. The phone warns first.
        /// </summary>
        public Dictionary<string, object> ApplyRoute(Dictionary<string, object> args, string expectedCatalog, bool dtOk, string gameControl)
        {
            if (!routesWrite) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            if (gameControl != "inControl") return Ack("rejected", "GAME_NOT_IN_CONTROL");
            if (!dtOk) return Ack("rejected", "MOD_INACTIVE"); // InitializeCourse does nothing unless IsDTOK.
            if (expectedCatalog != null && expectedCatalog != catalogRevision) return Ack("rejected", "REVISION_CONFLICT");
            object view = View;
            IList items = Items(view);
            if (items == null) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            string routeId = args["routeId"] as string;
            bool loop = (bool)args["loop"], reverse = (bool)args["reverse"];
            int index = FindIndex(items, routeId);
            if (index < 0) return Ack("rejected", "NOT_FOUND");
            object info = index == 0 ? null : items[index];
            if (info != null)
            {
                bool loopAllowed = object.Equals(Get(info, "Loop"), true);
                if ((loop || reverse) && !loopAllowed) return Ack("rejected", "VALUE_NOT_ALLOWED");
                FieldInfo loopActive = info.GetType().GetField("LoopActive", I);
                FieldInfo reverseField = info.GetType().GetField("Reverse", I);
                if (loopActive == null || reverseField == null) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
                loopActive.SetValue(info, loop);
                reverseField.SetValue(info, reverse);
            }
            FieldInfo handlerField = view.GetType().GetField("OnCourseSelected", I);
            Delegate handler = handlerField == null ? null : handlerField.GetValue(view) as Delegate;
            if (handler == null) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            handler.DynamicInvoke(view, info); // GTBikeVScript.CourseSelectedHandler: LoadCourse or ClearCurrentCourse, then Close().
            return Ack("dispatched", null);
        }

        /// <summary>routes.preview: moves the open selector; never selects.</summary>
        public Dictionary<string, object> PreviewRoute(Dictionary<string, object> args, string expectedCatalog)
        {
            if (!routesWrite) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            if (expectedCatalog != null && expectedCatalog != catalogRevision) return Ack("rejected", "REVISION_CONFLICT");
            object view = View;
            if (!Visible(view)) return Ack("rejected", "PANEL_CLOSED");
            IList items = Items(view);
            int index = items == null ? -1 : FindIndex(items, args["routeId"] as string);
            if (index < 0) return Ack("rejected", "NOT_FOUND");
            if (Index(view) == index) return Ack("noop", null);
            view.GetType().GetProperty("Index", I).SetValue(view, index, null);
            return Ack("dispatched", null);
        }

        /// <summary>ui.openPanel / ui.closePanel for the route selector only.</summary>
        public Dictionary<string, object> Panel(bool open, string gameControl)
        {
            if (!routesWrite) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            object view = View;
            if (Visible(view) == open) return Ack("noop", null);
            if (!open)
            {
                MethodInfo close = view.GetType().GetMethod("Close", I, null, Type.EmptyTypes, null);
                if (close == null) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
                close.Invoke(view, null);
                return Ack("dispatched", null);
            }
            if (gameControl != "inControl") return Ack("rejected", "GAME_NOT_IN_CONTROL");
            // Same as choosing the menu item: its handler checks Enabled, closes menus and opens the view.
            object item = courseMenuItem.GetValue(null);
            if (!Enabled(item)) return Ack("rejected", "MOD_INACTIVE");
            MethodInfo onActivated = item.GetType().GetMethod("OnActivated", I, null, new Type[] { typeof(object) }, null);
            if (onActivated == null) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            onActivated.Invoke(item, new object[] { item });
            return Ack("dispatched", null);
        }

        // ---------------- settings ----------------

        private Dictionary<string, object> ReadSettings()
        {
            Dictionary<string, object> s = new Dictionary<string, object>();
            s["weightKg"] = Math.Round((double)(float)weight.GetValue(null, null), 1);
            s["ftpW"] = Math.Round((double)(float)ftp.GetValue(null, null), 1);
            s["hrMaxBpm"] = (int)(byte)hrMax.GetValue(null, null);
            s["autopilot"] = (bool)autopilot.GetValue(null, null);
            s["avoidObstacles"] = (bool)avoidObstacles.GetValue(null);
            int level = (byte)difficulty.GetValue(null, null);
            s["difficulty"] = Array.IndexOf(Difficulty, level) >= 0 ? (object)level : null;
            s["roadFeel"] = (bool)roadFeel.GetValue(null, null);
            s["bike"] = Choice(bikes, bikeName);
            s["character"] = Choice(peds, pedName);
            Dictionary<string, object> enabled = new Dictionary<string, object>();
            enabled["weight"] = Enabled(Value(weightItem));
            enabled["ftp"] = Enabled(Value(ftpItem));
            enabled["autopilot"] = Enabled(Value(autopilotItem));
            enabled["avoidObstacles"] = Enabled(Value(obstaclesItem));
            enabled["difficulty"] = Enabled(Value(difficultyItem));
            enabled["roadFeel"] = Enabled(Value(roadFeelItem));
            enabled["bike"] = Enabled(Value(bikeItem));
            enabled["character"] = Enabled(Value(pedItem));
            s["enabled"] = enabled;
            return s;
        }

        private static Dictionary<string, object> Choice(FieldInfo dictionaryField, PropertyInfo selected)
        {
            Dictionary<string, object> c = new Dictionary<string, object>();
            List<string> options = new List<string>();
            IDictionary dict = dictionaryField.GetValue(null) as IDictionary;
            if (dict != null) foreach (object k in dict.Keys) { if (options.Count == 128) break; string t = Text(k, 80); if (t != null) options.Add(t); }
            c["options"] = options;
            c["selected"] = Text(selected.GetValue(null, null), 80);
            return c;
        }

        public Dictionary<string, object> UpdateProfile(Dictionary<string, object> args, bool workoutBlocks)
        {
            if (!settingsWrite) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            int w = 0, f = 0, h = 0;
            bool hasW = TryInt(args, "weightKg", 40, 150, out w), hasF = TryInt(args, "ftpW", 50, 500, out f), hasH = TryInt(args, "hrMaxBpm", 50, 220, out h);
            if (args.Count == 0 || args.Count != (hasW ? 1 : 0) + (hasF ? 1 : 0) + (hasH ? 1 : 0)) return Ack("rejected", "VALUE_OUT_OF_RANGE");
            if (hasW && !Enabled(Value(weightItem))) return Ack("rejected", "MOD_INACTIVE");
            if (hasF && !Enabled(Value(ftpItem))) return Ack("rejected", "MOD_INACTIVE");
            if (hasF && workoutBlocks) return Ack("rejected", "WORKOUT_PC_CONTROL_REQUIRED"); // Workout targets are % of FTP.
            if (hasW)
            {
                weight.SetValue(null, (float)w, null);
                bool lb = (bool)imperial.GetValue(null, null);
                // Menu label refresh through the mod's own setter (it re-validates and stores the same rounded value).
                weightUnit.SetValue(null, (lb ? Math.Round(w * 2.20462) : w).ToString(CultureInfo.InvariantCulture), null);
            }
            if (hasF) { ftp.SetValue(null, (float)f, null); ftpLabel.SetValue(null, (float)f, null); }
            if (hasH) hrMax.SetValue(null, (byte)h, null);
            nextSettingsReadUtc = DateTime.MinValue;
            return Ack("dispatched", null);
        }

        public Dictionary<string, object> UpdateRide(Dictionary<string, object> args)
        {
            if (!settingsWrite) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            foreach (string key in args.Keys)
                if (key != "autopilot" && key != "avoidObstacles" && key != "difficulty" && key != "roadFeel") return Ack("rejected", "INVALID_REQUEST");
            object v;
            if (args.TryGetValue("autopilot", out v) && (!(v is bool) || !Enabled(Value(autopilotItem)))) return Ack("rejected", v is bool ? "MOD_INACTIVE" : "INVALID_REQUEST");
            if (args.TryGetValue("roadFeel", out v) && (!(v is bool) || !Enabled(Value(roadFeelItem)))) return Ack("rejected", v is bool ? "MOD_INACTIVE" : "INVALID_REQUEST");
            int level = -1;
            if (args.TryGetValue("difficulty", out v))
            {
                if (!(v is int) || Array.IndexOf(Difficulty, (int)v) < 0) return Ack("rejected", "VALUE_OUT_OF_RANGE");
                if (!Enabled(Value(difficultyItem))) return Ack("rejected", "MOD_INACTIVE");
                level = (int)v;
            }
            bool autopilotAfter = args.TryGetValue("autopilot", out v) ? (bool)v : (bool)autopilot.GetValue(null, null);
            if (args.TryGetValue("avoidObstacles", out v))
            {
                if (!(v is bool)) return Ack("rejected", "INVALID_REQUEST");
                if (autopilotAfter) return Ack("rejected", "AUTOPILOT_ON"); // Native menu disables it while autopilot is on.
                if (!Enabled(Value(autopilotItem))) return Ack("rejected", "MOD_INACTIVE");
            }
            if (args.TryGetValue("autopilot", out v)) autopilot.SetValue(null, (bool)v, null); // Setter syncs checkbox and obstacles item.
            if (args.TryGetValue("avoidObstacles", out v)) { avoidObstacles.SetValue(null, (bool)v); SetChecked(Value(obstaclesItem), (bool)v); }
            if (level >= 0)
            {
                difficulty.SetValue(null, (byte)level, null);
                SetProperty(Value(difficultyItem), "SelectedIndex", Array.IndexOf(Difficulty, level));
            }
            if (args.TryGetValue("roadFeel", out v)) { roadFeel.SetValue(null, (bool)v, null); SetChecked(Value(roadFeelItem), (bool)v); }
            nextSettingsReadUtc = DateTime.MinValue;
            return Ack("dispatched", null);
        }

        /// <summary>appearance.select through the native list item, whose ItemChanged handler swaps the bike/ped.</summary>
        public Dictionary<string, object> SelectAppearance(Dictionary<string, object> args, double? speedMps, string gameControl)
        {
            if (!settingsWrite) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            if (args.Count != 1) return Ack("rejected", "INVALID_REQUEST");
            if (gameControl != "inControl") return Ack("rejected", "GAME_NOT_IN_CONTROL");
            if (speedMps.HasValue && speedMps.Value >= 0.5) return Ack("rejected", "RIDER_MOVING");
            bool isBike = args.ContainsKey("bikeId");
            string wanted = (isBike ? args["bikeId"] : args.ContainsKey("characterId") ? args["characterId"] : null) as string;
            if (wanted == null) return Ack("rejected", "INVALID_REQUEST");
            object item = Value(isBike ? bikeItem : pedItem);
            if (!Enabled(item)) return Ack("rejected", "MOD_INACTIVE");
            IList names = Get(item, "Items") as IList;
            int index = -1;
            if (names != null) for (int n = 0; n < names.Count; n++) if (Convert.ToString(names[n], CultureInfo.InvariantCulture) == wanted) { index = n; break; }
            if (index < 0) return Ack("rejected", "VALUE_NOT_ALLOWED");
            string current = Text((isBike ? bikeName : pedName).GetValue(null, null), 80);
            if (current == wanted) return Ack("noop", null);
            if (listTrigger == null || listTrigger.DeclaringType != item.GetType()) return Ack("rejected", "CAPABILITY_UNAVAILABLE");
            // ChangeBike -> SpawnBike leaves the bike and waits frames with Script.Yield before spawning the new model.
            // Run on this adapter's Tick, the mod's own Tick kept running in between (re-entering the old bike,
            // respawning for multiplayer) and the model never changed (6 Nov 2026). The native menu raises ItemChanged
            // inside the mod's Tick, so do the same: once, on the mod's next Tick.
            int target = index;
            bool scheduled = RunOnModTick(item, delegate
            {
                // The mod's "next bike" key changes the bike without moving this menu item, and LemonUI ignores setting
                // the index it already has (set_SelectedIndex returns when equal): raise ItemChanged as the setter
                // would (Direction.Unknown = 0) so ChangeBike/ChangePed runs.
                if (object.Equals(Get(item, "SelectedIndex"), target))
                    listTrigger.Invoke(item, new object[] { target, Enum.ToObject(listTrigger.GetParameters()[1].ParameterType, 0) });
                else SetProperty(item, "SelectedIndex", target);
                nextSettingsReadUtc = DateTime.MinValue;
            });
            return scheduled ? Ack("dispatched", null) : Ack("rejected", "CAPABILITY_UNAVAILABLE");
        }

        /// <summary>
        /// Runs <paramref name="work"/> once on the next Tick of the mod's script (found as the target of the list
        /// item's own ItemChanged handler). Exceptions go to <see cref="DeferredError"/>, never to the mod's script.
        /// </summary>
        private bool RunOnModTick(object listItem, Action work)
        {
            Delegate handlers = listChanged == null || modScript == null ? null : listChanged.GetValue(listItem) as Delegate;
            object script = null;
            if (handlers != null)
                foreach (Delegate h in handlers.GetInvocationList())
                    if (h.Target != null && modScript.IsInstanceOfType(h.Target)) { script = h.Target; break; }
            return RunOnTick(script, work, DeferredError);
        }

        /// <summary>
        /// Runs <paramref name="work"/> once on the next Tick of <paramref name="script"/> (a SHVDN Script), i.e. on that
        /// script's own thread, as its menus do. Exceptions go to <paramref name="onError"/>, never to that script.
        /// </summary>
        internal static bool RunOnTick(object script, Action work, Action<Exception> onError)
        {
            EventInfo tick = script == null ? null : script.GetType().GetEvent("Tick", BindingFlags.Public | BindingFlags.Instance);
            if (tick == null) return false;
            int ran = 0;
            EventHandler once = null;
            once = delegate(object sender, EventArgs e)
            {
                if (System.Threading.Interlocked.Exchange(ref ran, 1) != 0) return;
                try { tick.RemoveEventHandler(script, once); } catch (Exception) { }
                try { work(); }
                catch (Exception ex) { if (onError != null) onError(ex); }
            };
            tick.AddEventHandler(script, once);
            return true;
        }

        // ---------------- helpers ----------------

        private static bool TryInt(Dictionary<string, object> args, string key, int min, int max, out int value)
        {
            value = 0;
            object v;
            if (!args.TryGetValue(key, out v) || !(v is int)) return false;
            value = (int)v;
            return value >= min && value <= max;
        }

        private static object Value(FieldInfo f) { return f == null ? null : f.GetValue(null); }

        private static object Get(object target, string name)
        {
            if (target == null) return null;
            FieldInfo f = target.GetType().GetField(name, I);
            if (f != null) return f.GetValue(target);
            PropertyInfo p = target.GetType().GetProperty(name, I);
            return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(target, null) : null;
        }

        private static object Member(object target, string field)
        {
            FieldInfo f = target == null ? null : target.GetType().GetField(field, I);
            return f == null ? null : f.GetValue(target);
        }

        private static bool Enabled(object item) { return object.Equals(Get(item, "Enabled"), true); }
        private static bool Checked(object item) { return object.Equals(Get(item, "Checked"), true); }

        private static void SetChecked(object item, bool value) { SetProperty(item, "Checked", value); }

        private static void SetProperty(object item, string name, object value)
        {
            PropertyInfo p = item == null ? null : item.GetType().GetProperty(name, I);
            if (p != null && p.CanWrite) p.SetValue(item, value, null);
        }

        private static string Text(object value, int max)
        {
            string s = value as string;
            if (s == null && value != null && !(value is IEnumerable)) s = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (s == null) return null;
            s = s.Trim();
            if (s.Length == 0) return null;
            return s.Length > max ? s.Substring(0, max) : s;
        }

        private static object Number(object value, double scale)
        {
            if (value == null) return null;
            double n;
            try { n = Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale; } catch (Exception) { return null; }
            return double.IsNaN(n) || double.IsInfinity(n) ? null : (object)Math.Round(n, 1);
        }

        private static List<string> Strings(object value)
        {
            List<string> list = new List<string>();
            IEnumerable items = value as IEnumerable;
            if (items == null || value is string) return list;
            foreach (object o in items) { if (list.Count == 8) break; string t = Text(o, 40); if (t != null) list.Add(t); }
            return list;
        }

        private FieldInfo StaticField(Type owner, string name)
        {
            FieldInfo f = owner == null ? null : owner.GetField(name, S);
            if (f == null) missing.Add((owner == null ? "?" : owner.Name) + "." + name);
            return f;
        }

        private PropertyInfo StaticProp(Type owner, string name, Type expected)
        {
            PropertyInfo p = owner == null ? null : owner.GetProperty(name, S);
            if (p != null && p.PropertyType != expected) p = null;
            if (p == null) missing.Add((owner == null ? "?" : owner.Name) + "." + name);
            return p;
        }

        private FieldInfo InstanceField(Type owner, string name, Type expected)
        {
            FieldInfo f = owner == null ? null : owner.GetField(name, I);
            if (f != null && f.FieldType != expected) f = null;
            if (f == null) missing.Add((owner == null ? "?" : owner.Name) + "." + name);
            return f;
        }

        private static Dictionary<string, object> Ack(string status, string reasonCode)
        {
            Dictionary<string, object> ack = new Dictionary<string, object>();
            ack["status"] = status;
            ack["reasonCode"] = reasonCode;
            return ack;
        }
    }
}
