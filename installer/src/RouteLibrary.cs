// Local GTBike V courses and personal copies. The personal library survives a refresh of the mod's remote catalog.
// Publishing uses GTBike V's native courses.json and <id>-<version>.json layout, only with GTA V closed.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GTAVRidingCompanion
{
    sealed class RouteDocument
    {
        internal Dictionary<string, object> Info, Data;
        internal string FilePath, Revision;
        internal bool IsCopy;
    }

    sealed class RouteLibrary
    {
        readonly string directory, personal, catalogFile, personalCatalog;
        readonly Func<bool> gameRunning;
        internal string DirectoryPath { get { return directory; } }
        internal string CopiesPath { get { return personal; } }
        internal string Stamp
        {
            get { return String.Join("|", new[] { catalogFile, personalCatalog }.Select(p => File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks + ":" + new FileInfo(p).Length : "missing")); }
        }
        internal HashSet<string> CopyIds()
        {
            return new HashSet<string>(Json.List(ReadCatalog(personalCatalog), "CourseList").Select(r => Convert.ToString(Json.Get(r, "Id"), CultureInfo.InvariantCulture)));
        }

        internal RouteLibrary(string directory, Func<bool> gameRunning)
        {
            this.directory = Path.GetFullPath(directory);
            this.gameRunning = gameRunning;
            personal = Path.Combine(this.directory, "CompanionRoutes");
            catalogFile = Path.Combine(this.directory, "courses.json");
            personalCatalog = Path.Combine(personal, "courses.json");
        }

        internal static RouteDocument NewRoute()
        {
            return new RouteDocument {
                Info = new Dictionary<string, object> {
                    { "Id", 0 }, { "Name", "New route" }, { "Version", "1.0" }, { "Author", new Dictionary<string, object> { { "Name", "" } } },
                    { "Description", "" }, { "Notes", "" }, { "Category", new[] { "Road" } }, { "Type", new[] { "bike" } },
                    { "Loop", false }, { "Distance", 0 }, { "ElevationGain", 0 }, { "ElevationLoss", 0 }, { "MaxElevation", 0 }
                },
                Data = new Dictionary<string, object> { { "Name", "New route" }, { "StartPoint", null }, { "Props", new object[0] }, { "WayPointList", new object[0] } }
            };
        }

        static Dictionary<string, object> Read(string path)
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("The course file is too large: " + path);
            var result = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (result == null) throw new IOException("Invalid course file: " + path);
            return result;
        }

        Dictionary<string, object> ReadCatalog(string path)
        {
            return File.Exists(path) ? Read(path) : new Dictionary<string, object> { { "ListVersion", 0 }, { "CourseList", new object[0] } };
        }

        internal List<Dictionary<string, object>> Catalog()
        {
            var all = Json.List(ReadCatalog(catalogFile), "CourseList");
            foreach (var copy in Json.List(ReadCatalog(personalCatalog), "CourseList"))
            {
                int? id = Json.Int(copy, "Id");
                all.RemoveAll(r => Json.Int(r, "Id") == id);
                all.Add(copy);
            }
            return all.Select(Summary).ToList();
        }

        internal bool IsCopy(string id)
        {
            return Json.List(ReadCatalog(personalCatalog), "CourseList").Any(r => Convert.ToString(Json.Get(r, "Id"), CultureInfo.InvariantCulture) == id);
        }

        internal static Dictionary<string, object> Summary(Dictionary<string, object> info)
        {
            return new Dictionary<string, object> {
                { "id", Convert.ToString(Json.Get(info, "Id"), CultureInfo.InvariantCulture) }, { "name", Json.Str(info, "Name") },
                { "author", Json.Str(Json.Obj(info, "Author"), "Name") }, { "version", Json.Str(info, "Version") },
                { "distanceM", (Json.Num(info, "Distance") ?? 0) * 1000 }, { "elevationGainM", Json.Num(info, "ElevationGain") },
                { "elevationLossM", Json.Num(info, "ElevationLoss") }, { "maxElevationM", Json.Num(info, "MaxElevation") },
                { "description", Json.Str(info, "Description") }, { "notes", Json.Str(info, "Notes") },
                { "categories", Json.Get(info, "Category") ?? new object[0] }, { "types", Json.Get(info, "Type") ?? new object[0] },
                { "loopAllowed", Json.Bool(info, "Loop") == true }, { "reverseAllowed", Json.Bool(info, "Loop") == true }
            };
        }

        static Dictionary<string, object> NativeInfo(Dictionary<string, object> summary)
        {
            int id;
            if (!Int32.TryParse(Json.Str(summary, "id"), out id) || id < 1 || id > 32767) throw new IOException("Invalid course id.");
            return new Dictionary<string, object> {
                { "Id", id }, { "Name", Json.Str(summary, "name") }, { "Version", Json.Str(summary, "version") ?? "1.0" },
                { "Author", new Dictionary<string, object> { { "Name", Json.Str(summary, "author") ?? "" } } },
                { "Distance", (Json.Num(summary, "distanceM") ?? 0) / 1000 }, { "ElevationGain", Json.Num(summary, "elevationGainM") ?? 0 },
                { "ElevationLoss", Json.Num(summary, "elevationLossM") ?? 0 }, { "MaxElevation", Json.Num(summary, "maxElevationM") ?? 0 },
                { "Description", Json.Str(summary, "description") ?? "" }, { "Notes", Json.Str(summary, "notes") ?? "" },
                { "Category", Json.Get(summary, "categories") ?? new object[0] }, { "Type", Json.Get(summary, "types") ?? new[] { "bike" } },
                { "Loop", Json.Bool(summary, "loopAllowed") == true }
            };
        }

        internal static Dictionary<string, object> Clone(Dictionary<string, object> value) { return Json.Parse(Serialize(value)); }
        internal static string Serialize(object value) { return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(value); }

        static string FileName(Dictionary<string, object> info)
        {
            int? id = Json.Int(info, "Id");
            string version = Json.Str(info, "Version");
            if (id == null || id < 1 || id > 32767 || version == null || !Regex.IsMatch(version, @"\A[0-9]+(?:\.[0-9]+){0,3}\z"))
                throw new IOException("The course id or version cannot be used as a file name.");
            return id.Value.ToString(CultureInfo.InvariantCulture) + "-" + version + ".json";
        }

        internal async Task<RouteDocument> OpenAsync(Dictionary<string, object> summary)
        {
            string id = Json.Str(summary, "id");
            var info = Json.List(ReadCatalog(personalCatalog), "CourseList").FirstOrDefault(r => Convert.ToString(Json.Get(r, "Id"), CultureInfo.InvariantCulture) == id);
            bool copy = info != null;
            if (info == null) info = Json.List(ReadCatalog(catalogFile), "CourseList").FirstOrDefault(r => Convert.ToString(Json.Get(r, "Id"), CultureInfo.InvariantCulture) == id && Json.Str(r, "Version") == Json.Str(summary, "version"));
            if (info == null) info = NativeInfo(summary);
            string fileName = FileName(info);
            string path = Path.Combine(copy ? personal : directory, fileName);
            if (!File.Exists(path) && !copy)
            {
                path = Path.Combine(personal, "cache", fileName);
                if (!File.Exists(path))
                {
                    // Read the same library configured in GTBike V; the downloaded preview stays in the companion cache.
                    string libraryUrl = "https://www.gtbikev.com/files/courses/";
                    string ini = Path.Combine(directory, "GTBikeVConfig.ini");
                    if (File.Exists(ini))
                    {
                        bool network = false;
                        foreach (string line in File.ReadAllLines(ini))
                        {
                            string text = line.Trim();
                            if (text.StartsWith("[")) network = text.Equals("[network]", StringComparison.OrdinalIgnoreCase);
                            int equals = text.IndexOf('=');
                            if (network && equals > 0 && text.Substring(0, equals).Trim().Equals("CourseLibraryURL", StringComparison.OrdinalIgnoreCase))
                                libraryUrl = WebUtility.UrlDecode(text.Substring(equals + 1).Trim());
                        }
                    }
                    Uri root;
                    if (!Uri.TryCreate(libraryUrl.TrimEnd('/') + "/", UriKind.Absolute, out root) || (root.Scheme != "https" && root.Scheme != "http"))
                        throw new IOException("GTBike V's course library URL is invalid.");
                    string json;
                    using (var web = new WebClient())
                    {
                        web.Encoding = Encoding.UTF8;
                        web.Headers[HttpRequestHeader.UserAgent] = "GTAVRidingCompanion";
                        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                        json = await web.DownloadStringTaskAsync(new Uri(root, id + ".json"));
                    }
                    var data = Json.Parse(json);
                    Validate(data);
                    WriteAtomic(path, data);
                }
            }
            var definition = Read(path);
            Validate(definition);
            return new RouteDocument { Info = Clone(info), Data = definition, IsCopy = copy, FilePath = path, Revision = Hash(path) };
        }

        internal RouteDocument Save(RouteDocument source, Dictionary<string, object> info, Dictionary<string, object> data, bool newCopy)
        {
            Validate(data);
            if (String.IsNullOrWhiteSpace(Json.Str(info, "Name"))) throw new IOException("Enter a route name.");
            var catalog = ReadCatalog(personalCatalog);
            var copies = Json.List(catalog, "CourseList");
            info = Clone(info);
            data = Clone(data);
            if (newCopy)
            {
                System.IO.Directory.CreateDirectory(directory);
                var used = new HashSet<int>(Catalog().Select(r => Int32.Parse(Json.Str(r, "id"), CultureInfo.InvariantCulture)));
                int id = 30000;
                while (id <= 32767 && (used.Contains(id) || System.IO.Directory.GetFiles(directory, id + "-*.json").Length != 0)) id++;
                if (id > 32767) throw new IOException("No personal course ids remain (30000–32767).");
                info["Id"] = id;
                info["Version"] = "1.0";
            }
            else
            {
                if (!source.IsCopy || !IsCopy(Convert.ToString(Json.Get(source.Info, "Id"), CultureInfo.InvariantCulture)))
                    throw new IOException("Create a copy before editing this course.");
                if (Hash(source.FilePath) != source.Revision) throw new IOException("This copy changed outside the editor. Close and reopen it before saving.");
                var current = copies.First(r => Json.Int(r, "Id") == Json.Int(source.Info, "Id"));
                if (Json.Str(current, "Version") != Json.Str(source.Info, "Version")) throw new IOException("This copy has a newer saved version. Reopen it before saving.");
                info["Id"] = Json.Get(source.Info, "Id");
                info["Version"] = (Int32.Parse(Json.Str(current, "Version").Split('.')[0], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture) + ".0";
            }
            data["Name"] = Json.Str(info, "Name");
            string path = Path.Combine(personal, FileName(info));
            WriteAtomic(path, data);
            copies.RemoveAll(r => Json.Int(r, "Id") == Json.Int(info, "Id"));
            copies.Add(info);
            catalog["CourseList"] = copies;
            WriteAtomic(personalCatalog, catalog);
            return new RouteDocument { Info = info, Data = data, IsCopy = true, FilePath = path, Revision = Hash(path) };
        }

        // Never publish while the mod owns its in-memory catalog or a loaded copy: it can rewrite both files.
        internal bool SyncToGame()
        {
            if (gameRunning()) return false;
            var copies = Json.List(ReadCatalog(personalCatalog), "CourseList");
            if (copies.Count == 0) return true;
            var catalog = ReadCatalog(catalogFile);
            var all = Json.List(catalog, "CourseList");
            foreach (var info in copies)
            {
                string file = FileName(info);
                var data = Read(Path.Combine(personal, file));
                Validate(data);
                if (gameRunning()) return false;
                WriteAtomic(Path.Combine(directory, file), data);
                all.RemoveAll(r => Json.Int(r, "Id") == Json.Int(info, "Id"));
                all.Add(Clone(info));
            }
            if (gameRunning()) return false;
            catalog["CourseList"] = all;
            // Keep ListVersion: inventing a newer remote version would prevent official library updates.
            if (File.Exists(catalogFile))
            {
                string backups = Path.Combine(personal, "backups");
                System.IO.Directory.CreateDirectory(backups);
                File.Copy(catalogFile, Path.Combine(backups, "courses-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".json"));
            }
            WriteAtomic(catalogFile, catalog);
            return true;
        }

        internal bool NeedsSync()
        {
            var native = Json.List(ReadCatalog(catalogFile), "CourseList");
            foreach (var info in Json.List(ReadCatalog(personalCatalog), "CourseList"))
            {
                var installed = native.FirstOrDefault(r => Json.Int(r, "Id") == Json.Int(info, "Id"));
                string path = Path.Combine(directory, FileName(info));
                if (installed == null || Json.Str(installed, "Version") != Json.Str(info, "Version") || !File.Exists(path)) return true;
            }
            return false;
        }

        internal static List<KeyValuePair<double, double>> Geometry(Dictionary<string, object> data, bool loop, bool reverse)
        {
            var result = new List<KeyValuePair<double, double>>();
            var start = Json.Obj(data, "StartPoint");
            if (start != null) result.Add(new KeyValuePair<double, double>(Json.Num(start, "X") ?? 0, Json.Num(start, "Y") ?? 0));
            var points = Json.List(data, "WayPointList");
            if (reverse) points.Reverse();
            foreach (var p in points) result.Add(new KeyValuePair<double, double>(Json.Num(p, "X") ?? 0, Json.Num(p, "Y") ?? 0));
            if (loop && result.Count > 1) result.Add(result[0]);
            return result;
        }

        internal static void Validate(Dictionary<string, object> data)
        {
            if (data == null || String.IsNullOrWhiteSpace(Json.Str(data, "Name"))) throw new IOException("The course needs a Name.");
            ValidatePoint(Json.Obj(data, "StartPoint"), "Start point");
            var raw = Json.Get(data, "WayPointList") as IList;
            if (raw == null || raw.Count < 1 || raw.Count > 10000) throw new IOException("A course needs 1–10000 waypoints after its start.");
            for (int i = 0; i < raw.Count; i++) ValidatePoint(raw[i] as Dictionary<string, object>, "Waypoint " + (i + 1));
            var config = Json.Obj(data, "WayPointConfig");
            if (config != null)
            {
                if (!Regex.IsMatch(Json.Str(config, "Color") ?? "", @"\A[0-9A-Fa-f]{8}\z")) throw new IOException("Waypoint color must have 8 hexadecimal digits (AARRGGBB).");
                double radius = Json.Num(config, "Radius") ?? -1;
                if (Double.IsNaN(radius) || Double.IsInfinity(radius) || radius < 0 || radius > 40) throw new IOException("Waypoint radius must be between 0 and 40 metres.");
            }
            var props = Json.Get(data, "Props") as IList;
            if (props == null) throw new IOException("The course needs a Props array (it can be empty).");
            foreach (var item in props)
            {
                var prop = item as Dictionary<string, object>;
                if (String.IsNullOrWhiteSpace(Json.Str(prop, "Name"))) throw new IOException("Each prop needs a Name.");
                ValidatePoint(Json.Obj(prop, "Position"), "Prop position");
                ValidatePoint(Json.Obj(prop, "Rotation"), "Prop rotation");
            }
        }

        static void ValidatePoint(Dictionary<string, object> point, string label)
        {
            foreach (string axis in new[] { "X", "Y", "Z" })
            {
                double? value = Json.Num(point, axis);
                if (value == null || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value) || Math.Abs(value.Value) > 100000)
                    throw new IOException(label + ": " + axis + " must be a finite coordinate between -100000 and 100000.");
            }
            object routed = Json.Get(point, "Routed");
            bool flag;
            if (routed != null && !(routed is bool) && (!(routed is string) || !Boolean.TryParse((string)routed, out flag)))
                throw new IOException(label + ": Routed must be true or false.");
            if (Json.Get(point, "CloseByRange") != null)
            {
                double range = Json.Num(point, "CloseByRange") ?? -1;
                if (Double.IsNaN(range) || Double.IsInfinity(range) || range < 0 || range > 10000) throw new IOException(label + ": CloseByRange must be between 0 and 10000 metres.");
            }
        }

        static string Hash(string path)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
        }

        static void WriteAtomic(string path, object value)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, Serialize(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else
                {
                    try { File.Move(temp, path); }
                    catch (IOException) { if (!File.Exists(path)) throw; File.Replace(temp, path, null); }
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
