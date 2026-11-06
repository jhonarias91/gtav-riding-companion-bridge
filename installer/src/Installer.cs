// Install, repair and uninstall. The game adapter is compiled on the user's PC against their own
// ScriptHookVDotNet3.dll, so the elevated helper (game folder + firewall) is the only part that needs admin rights.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GTAVRidingCompanion
{
    /// <summary>What the setup page shows for a candidate GTA V folder.</summary>
    sealed class GameCheck
    {
        public bool FolderExists, ScriptHook, Mod, ModVersionOk, GameClosed;
        public string ModVersion;
        public bool Ready { get { return FolderExists && ScriptHook && Mod && ModVersionOk && GameClosed; } }

        public string FirstProblem
        {
            get
            {
                if (!FolderExists) return "Choose the main GTA V folder (the one with GTA5.exe or GTA5_Enhanced.exe).";
                if (!ScriptHook) return "ScriptHookVDotNet3.dll is missing in that folder. Install ScriptHookV and ScriptHookVDotNet first.";
                if (!Mod) return "scripts\\GTBikeV.dll is missing. Install GTBike V first.";
                if (!ModVersionOk) return "GTBikeV.dll is version " + ModVersion + "; this companion needs " + Installer.SupportedModVersion + ".";
                if (!GameClosed) return "Close GTA V before installing or removing the game adapter.";
                return null;
            }
        }
    }

    static class Installer
    {
        internal const string SupportedModVersion = "0.8.0.4";
        internal const int BridgePort = 8788;
        const string AdapterDll = "GTBikeVCompanionV1.dll";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GTAVRidingCompanion";
        static readonly string[] GameFolderNames = { "Grand Theft Auto V Enhanced", "Grand Theft Auto V", "Grand Theft Auto V Legacy" };

        static string Q(string text) { return "\"" + text.Replace("\"", "\\\"") + "\""; }

        internal static bool IsInstalled { get { return File.Exists(Path.Combine(Paths.AppDir, "bridge-v1.mjs")) && File.Exists(Paths.GameFile); } }

        internal static string InstalledVersion
        {
            get { try { return File.Exists(Paths.VersionFile) ? File.ReadAllText(Paths.VersionFile).Trim() : null; } catch (IOException) { return null; } }
        }

        internal static string SavedGameFolder
        {
            get { try { return File.Exists(Paths.GameFile) ? File.ReadAllText(Paths.GameFile, Encoding.UTF8).Trim() : null; } catch (IOException) { return null; } }
        }

        internal static bool GameRunning
        {
            get { return Process.GetProcessesByName("GTA5").Length != 0 || Process.GetProcessesByName("GTA5_Enhanced").Length != 0; }
        }

        internal static GameCheck Check(string game)
        {
            var check = new GameCheck();
            check.GameClosed = !GameRunning;
            if (String.IsNullOrWhiteSpace(game)) return check;
            try { game = Path.GetFullPath(game.Trim()); } catch (Exception) { return check; }
            check.FolderExists = Directory.Exists(game);
            if (!check.FolderExists) return check;
            check.ScriptHook = File.Exists(Path.Combine(game, "ScriptHookVDotNet3.dll"));
            string mod = Path.Combine(game, "scripts", "GTBikeV.dll");
            check.Mod = File.Exists(mod);
            if (check.Mod)
            {
                try { check.ModVersion = AssemblyName.GetAssemblyName(mod).Version.ToString(); }
                catch (Exception) { check.ModVersion = "unknown"; }
                check.ModVersionOk = check.ModVersion == SupportedModVersion;
            }
            return check;
        }

        internal static bool AdapterInstalled(string game)
        {
            return !String.IsNullOrEmpty(game) && File.Exists(Path.Combine(game, "scripts", AdapterDll));
        }

        /// <summary>GTA V folders found in Steam libraries, the Rockstar launcher and Epic, best candidates first.</summary>
        internal static List<string> DetectGameFolders()
        {
            var found = new List<string>();
            Action<string> add = folder =>
            {
                if (String.IsNullOrWhiteSpace(folder)) return;
                try
                {
                    folder = Path.GetFullPath(folder.Trim().TrimEnd('\\'));
                    if (Directory.Exists(folder) && !found.Contains(folder, StringComparer.OrdinalIgnoreCase)) found.Add(folder);
                }
                catch (Exception) { }
            };
            add(SavedGameFolder);
            foreach (string library in SteamLibraries())
                foreach (string name in GameFolderNames) add(Path.Combine(library, "steamapps", "common", name));
            foreach (string key in new[] { @"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V", @"SOFTWARE\WOW6432Node\Rockstar Games\GTA V Enhanced", @"SOFTWARE\WOW6432Node\Rockstar Games\GTAV Enhanced", @"SOFTWARE\Rockstar Games\Grand Theft Auto V" })
                add(ReadRegistry(Registry.LocalMachine, key, "InstallFolder"));
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string name in new[] { "GTAV", "GTAVEnhanced", "GTAV Enhanced" }) add(Path.Combine(programFiles, "Epic Games", name));
            foreach (string name in GameFolderNames) add(Path.Combine(programFiles, "Rockstar Games", name));
            return found.OrderByDescending(f => Score(Check(f))).ToList();
        }

        static int Score(GameCheck c) { return (c.ModVersionOk ? 8 : 0) + (c.Mod ? 4 : 0) + (c.ScriptHook ? 2 : 0) + (c.FolderExists ? 1 : 0); }

        static IEnumerable<string> SteamLibraries()
        {
            var libraries = new List<string>();
            string steam = ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                ?? ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            if (String.IsNullOrEmpty(steam)) return libraries;
            steam = steam.Replace('/', '\\');
            libraries.Add(steam);
            try
            {
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch (Exception) { }
            return libraries;
        }

        static string ReadRegistry(RegistryKey hive, string path, string name)
        {
            try
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    using (var root = RegistryKey.OpenBaseKey(hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view))
                    using (var key = root.OpenSubKey(path))
                    {
                        string value = key == null ? null : key.GetValue(name) as string;
                        if (!String.IsNullOrEmpty(value)) return value;
                    }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Full install or repair. Returns a warning to show (firewall), or an empty string.</summary>
        internal static string Install(string game, bool desktopShortcut, Action<string> progress)
        {
            game = Path.GetFullPath(game.Trim());
            string problem = Check(game).FirstProblem;
            if (problem != null) throw new Exception(problem);
            progress("Copying the app files…");
            Directory.CreateDirectory(Paths.AppDir);
            ExtractPayload();
            string launcher = Paths.InstalledExe;
            if (!String.Equals(Path.GetFullPath(Application.ExecutablePath), launcher, StringComparison.OrdinalIgnoreCase))
                File.Copy(Application.ExecutablePath, launcher, true);
            progress("Waiting for Windows permission (game adapter and firewall)…");
            string[] lines = RunElevated(launcher, "--install-helper", game);
            if (lines.Length == 0 || lines[0] != "OK") throw new Exception(String.Join("\n", lines.Skip(1).ToArray()));
            File.WriteAllText(Paths.GameFile, game, new UTF8Encoding(false));
            progress("Creating shortcuts…");
            try { CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.Name + ".lnk"), launcher); }
            catch (Exception) { /* The app works without a Start menu entry. */ }
            string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.Name + ".lnk");
            try { if (desktopShortcut) CreateShortcut(desktopLink, launcher); else if (File.Exists(desktopLink)) File.Delete(desktopLink); }
            catch (Exception) { }
            try { Register(launcher); } catch (Exception) { /* Apps & features entry is optional. */ }
            RefreshShellIcon(launcher);
            return String.Join("\n", lines.Skip(1).ToArray());
        }

        /// <summary>Asks for confirmation, removes everything this app added and schedules removal of its own folder.</summary>
        internal static bool UninstallInteractive()
        {
            var answer = MessageBox.Show(
                "Remove " + Program.Name + " from this PC?\n\n" +
                "This removes the game adapter (scripts\\" + AdapterDll + "), the Windows Firewall rule, the shortcuts and the app files.\n\n" +
                "GTBike V, your rides and your phone pairings are kept.",
                Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return false;
            if (GameRunning)
            {
                MessageBox.Show("Close GTA V first, then uninstall again.", Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            string warning = "";
            try
            {
                string game = SavedGameFolder;
                string helper = File.Exists(Paths.InstalledExe) ? Paths.InstalledExe : Application.ExecutablePath;
                string[] lines = RunElevated(helper, "--uninstall-helper", String.IsNullOrEmpty(game) ? "-" : game);
                if (lines.Length == 0 || lines[0] != "OK") warning = String.Join("\n", lines.Skip(1).ToArray());
            }
            catch (Exception error) { warning = error.Message; }
            foreach (string link in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.Name + ".lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "GTAV Riding Companion Bridge.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.Name + ".lnk") })
                try { if (File.Exists(link)) File.Delete(link); } catch (Exception) { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
            if (Directory.Exists(Paths.AppDir))
            {
                // The running executable may live in that folder: remove it a moment after this process exits.
                var info = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q " + Q(Paths.AppDir));
                info.CreateNoWindow = true; info.UseShellExecute = false; info.WorkingDirectory = Path.GetTempPath();
                Process.Start(info);
            }
            MessageBox.Show(warning.Length == 0
                ? Program.Name + " was removed."
                : Program.Name + " was removed, but something needs a manual check:\n\n" + warning,
                Program.Name, MessageBoxButtons.OK, warning.Length == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return true;
        }

        static string[] RunElevated(string exe, string verb, string game)
        {
            string request = Path.Combine(Path.GetTempPath(), "gtav-companion-" + Guid.NewGuid().ToString("N") + ".txt");
            string result = request + ".result";
            try
            {
                File.WriteAllLines(request, new[] { game, Paths.AppDir }, Encoding.UTF8);
                var info = new ProcessStartInfo(exe, verb + " " + Q(request));
                info.UseShellExecute = true;
                info.Verb = "runas";
                try { using (var helper = Process.Start(info)) helper.WaitForExit(); }
                catch (System.ComponentModel.Win32Exception) { throw new Exception("Windows permission was not granted, so nothing was changed in the game folder."); }
                if (!File.Exists(result)) throw new Exception("The elevated step did not report a result.");
                return File.ReadAllLines(result, Encoding.UTF8);
            }
            finally
            {
                if (File.Exists(request)) File.Delete(request);
                if (File.Exists(result)) File.Delete(result);
            }
        }

        static void ExtractPayload()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BridgePayload.zip"))
            {
                if (stream == null) throw new Exception("This build has no app files inside. Rebuild the installer with Build-Installer.ps1.");
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                    foreach (var entry in archive.Entries)
                    {
                        if (String.IsNullOrEmpty(entry.Name)) continue;
                        string target = Path.GetFullPath(Path.Combine(Paths.AppDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(Paths.AppDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new Exception("Invalid path in the package.");
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = entry.Open())
                        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write)) input.CopyTo(output);
                    }
            }
            string manifest = Path.Combine(Paths.AppDir, "payload-sha256.txt");
            foreach (string line in File.ReadAllLines(manifest))
            {
                string[] fields = line.Split(new[] { ' ' }, 2);
                if (fields.Length != 2) throw new Exception("Invalid file manifest.");
                string file = Path.GetFullPath(Path.Combine(Paths.AppDir, fields[1]));
                if (!file.StartsWith(Paths.AppDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Invalid path in the manifest.");
                using (var hash = SHA256.Create())
                using (var source = File.OpenRead(file))
                {
                    string actual = BitConverter.ToString(hash.ComputeHash(source)).Replace("-", "").ToLowerInvariant();
                    if (actual != fields[0]) throw new Exception("A file in the package is damaged: " + fields[1]);
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

        /// <summary>
        /// Updates replace the executable at the same path. Windows keeps icons cached by path, so the taskbar can go on
        /// showing an older build's (blank) icon until the shell is told the file changed.
        /// </summary>
        internal static void RefreshShellIcon(string path)
        {
            try
            {
                SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, path, IntPtr.Zero);
                SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, null, IntPtr.Zero);
            }
            catch (Exception) { /* Cosmetic only. */ }
        }

        static void CreateShortcut(string link, string launcher)
        {
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            object shell = Activator.CreateInstance(type);
            object shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { launcher });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Paths.AppDir });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Phone bridge, mod settings and live map for GTBike V" });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { launcher + ",0" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            // The old Spanish-era Start menu entry pointed at the same executable.
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "GTAV Riding Companion Bridge.lnk");
            if (File.Exists(old) && !String.Equals(old, link, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
        }

        /// <summary>Per-user Apps &amp; features entry, so Windows Settings can uninstall it.</summary>
        static void Register(string launcher)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", Program.Name);
                key.SetValue("DisplayVersion", BuildInfo.Version);
                key.SetValue("Publisher", Program.Name);
                key.SetValue("DisplayIcon", launcher + ",0");
                key.SetValue("InstallLocation", Paths.AppDir);
                key.SetValue("UninstallString", Q(launcher) + " --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long bytes = new DirectoryInfo(Paths.AppDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
        }

        /// <summary>Elevated part: request file holds the game folder and the app folder.</summary>
        internal static void RunHelper(string request, bool uninstall)
        {
            try
            {
                string[] lines = File.ReadAllLines(request, Encoding.UTF8);
                if (lines.Length != 2) throw new Exception("Invalid install request.");
                string warning = uninstall ? RemoveAdapterAndFirewall(lines[0], lines[1]) : InstallAdapterAndFirewall(lines[0], lines[1]);
                File.WriteAllText(request + ".result", "OK\n" + warning, Encoding.UTF8);
            }
            catch (Exception error)
            {
                File.WriteAllText(request + ".result", "ERROR\n" + error.Message, Encoding.UTF8);
            }
        }

        static string InstallAdapterAndFirewall(string game, string app)
        {
            string problem = Check(game).FirstProblem;
            if (problem != null) throw new Exception(problem);
            string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
            if (!File.Exists(compiler)) throw new Exception("The 64-bit .NET Framework 4.8 compiler is missing. Install .NET Framework 4.8 and try again.");
            string source = Path.Combine(app, "adapter-src");
            string[] files = Directory.GetFiles(source, "*.cs").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            if (files.Length == 0) throw new Exception("The adapter sources are missing from the app folder.");
            // File.Replace needs the replacement on the same volume; this folder is outside what GTA loads from scripts.
            string temporaryDirectory = Path.Combine(game, "scripts", ".companion-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            string output = Path.Combine(temporaryDirectory, AdapterDll);
            string args = "/nologo /warnaserror+ /target:library /platform:x64 /optimize+ /out:" + Q(output)
                + " /reference:" + Q(Path.Combine(game, "ScriptHookVDotNet3.dll")) + " /reference:System.Web.Extensions.dll "
                + String.Join(" ", files.Select(Q).ToArray());
            try
            {
                var build = Run(compiler, args);
                if (build.Item1 != 0) throw new Exception("The game adapter did not compile:\n" + build.Item2);
                string target = Path.Combine(game, "scripts", AdapterDll);
                if (File.Exists(target))
                {
                    string backup = target + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".bak";
                    File.Replace(output, target, backup);
                    PruneBackups(target);
                }
                else File.Move(output, target);
            }
            finally
            {
                if (File.Exists(output)) File.Delete(output);
                Directory.Delete(temporaryDirectory);
            }
            var firewall = Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Q(Path.Combine(app, "Enable-LanAccess.ps1"))
                + " -Port " + BridgePort + " -NodeExecutable " + Q(Path.Combine(app, "node.exe")));
            return firewall.Item1 == 0 ? "" : "The game adapter was installed, but Windows Firewall could not be opened for the phone:\n" + firewall.Item2;
        }

        /// <summary>Keeps the three newest adapter backups so reinstalls do not pile up files in scripts.</summary>
        static void PruneBackups(string target)
        {
            var backups = Directory.GetFiles(Path.GetDirectoryName(target), Path.GetFileName(target) + ".*.bak")
                .OrderByDescending(f => f, StringComparer.Ordinal).Skip(3);
            foreach (string old in backups) try { File.Delete(old); } catch (Exception) { }
        }

        static string RemoveAdapterAndFirewall(string game, string app)
        {
            var problems = new List<string>();
            if (game != "-" && Directory.Exists(game))
            {
                if (GameRunning) throw new Exception("Close GTA V first.");
                string target = Path.Combine(game, "scripts", AdapterDll);
                try { if (File.Exists(target)) File.Delete(target); }
                catch (Exception error) { problems.Add("Could not delete " + target + ": " + error.Message); }
            }
            string script = Path.Combine(app, "Enable-LanAccess.ps1");
            if (File.Exists(script))
            {
                var firewall = Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Q(script) + " -Port " + BridgePort + " -Remove");
                if (firewall.Item1 != 0) problems.Add("Windows Firewall rule GTBikeVCompanion-LAN-" + BridgePort + " could not be removed:\n" + firewall.Item2);
            }
            return String.Join("\n", problems.ToArray());
        }

        internal static Tuple<int, string> Run(string exe, string args)
        {
            var info = new ProcessStartInfo(exe, args);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using (var process = Process.Start(info))
            {
                var error = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return Tuple.Create(process.ExitCode, (output + "\n" + error.Result).Trim());
            }
        }
    }
}
