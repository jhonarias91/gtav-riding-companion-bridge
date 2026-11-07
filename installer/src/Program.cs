// GTAV Riding Companion for Windows: one executable that installs itself, runs the phone bridge and offers the
// mod settings and a live map of the rider. C# 5 only (.NET Framework 4.8 csc).
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    static class Paths
    {
        internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTBikeVCompanion");
        internal static readonly string AppDir = Path.Combine(Root, "app");
        internal static readonly string ResourcesDir = Path.Combine(AppDir, "resources");
        internal static readonly string GameFile = Path.Combine(AppDir, "game-folder.txt");
        internal static readonly string VersionFile = Path.Combine(AppDir, "version.txt");
        internal static readonly string StatusFile = Path.Combine(Root, "desktop-status.json");
        internal static readonly string AdapterDir = Path.Combine(Root, "v1");
        internal static readonly string AdapterLog = Path.Combine(AdapterDir, "adapter.log");
        internal static readonly string BridgeLog = Path.Combine(Root, "bridge.log");
        internal static readonly string Settings = Path.Combine(Root, "desktop-settings.ini");
        internal static readonly string CoursesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rockstar Games", "GTA V", "ModSettings");
        /// <summary>Downloaded from the Mod tab; outside the app folder so updates keep it.</summary>
        internal static readonly string MapImage = Path.Combine(Root, "map", "gta-map.jpg");
        internal static readonly string ExeName = "GTAVRidingCompanion.exe";
        internal static string InstalledExe { get { return Path.Combine(AppDir, ExeName); } }

        /// <summary>Map resources: the installed copy, or installer\resources next to a development build.</summary>
        internal static string Resource(string name)
        {
            string installed = Path.Combine(ResourcesDir, name);
            if (File.Exists(installed)) return installed;
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            for (int i = 0; i < 4 && dir != null; i++, dir = Path.GetDirectoryName(dir))
            {
                string candidate = Path.Combine(dir, "installer", "resources", name);
                if (File.Exists(candidate)) return candidate;
            }
            return installed;
        }
    }

    static class Program
    {
        internal const string Name = "GTAV Riding Companion";
        static Mutex instance;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--install-helper") { Installer.RunHelper(args[1], false); return 0; }
            if (args.Length == 2 && args[0] == "--uninstall-helper") { Installer.RunHelper(args[1], true); return 0; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // The window is in English: show numbers the same way (0.70, not 0,70) whatever the Windows region is.
            var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Thread.CurrentThread.CurrentCulture = english;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = english;
            bool first;
            instance = new Mutex(true, "Local\\GTAVRidingCompanionDesktop", out first);
            if (!first)
            {
                MessageBox.Show(Name + " is already running. Close that window first (check the taskbar), then try again.", Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            try
            {
                if (args.Contains("--uninstall")) return Installer.UninstallInteractive() ? 0 : 1;
                Application.Run(new MainForm(args.Contains("--after-install")));
                return 0;
            }
            finally { ReleaseInstance(); }
        }

        /// <summary>Lets the freshly installed copy start while this one closes.</summary>
        internal static void ReleaseInstance()
        {
            if (instance == null) return;
            try { instance.ReleaseMutex(); } catch (ApplicationException) { }
            instance.Dispose();
            instance = null;
        }
    }
}
