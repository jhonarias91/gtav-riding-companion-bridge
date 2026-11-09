// Main window: Phone (bridge + QR pairing), Mod (settings + live map), Setup (install, repair, uninstall) and Web (the PWA).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class MainForm : Form
    {
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 6), Multiline = true };
        readonly TabPage phoneTab = new TabPage("Phone"), modTab = new TabPage("Mod"), cameraTab = new TabPage("Camera"), trainerTab = new TabPage("Virtual gears"), routesTab = new TabPage("Routes"), trainingTab = new TabPage("Training"),
            sensorsTab = new TabPage("Sensors"), ridersTab = new TabPage("Riders"), activitiesTab = new TabPage("Activities"), addonsTab = new TabPage("Add-ons"), optionsTab = new TabPage("Game options"), diagnosticsTab = new TabPage("Diagnostics"), setupTab = new TabPage("Setup"), webTab = new TabPage("Web");
        readonly Label headerStatus = new Label { AutoSize = true, ForeColor = Color.FromArgb(203, 213, 225), Font = new Font("Segoe UI", 10f), Anchor = AnchorStyles.Right };
        readonly Label footer = new Label { Dock = DockStyle.Fill, ForeColor = Ui.Muted, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0) };
        // Phone tab
        readonly PictureBox qr = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
        readonly Label pairingCode = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 22f), ForeColor = Ui.Text };
        readonly Label pairingExpiry = new Label { AutoSize = true, ForeColor = Ui.Muted };
        readonly TextBox details = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Ui.CardBack, Font = new Font("Consolas", 9.5f), Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        readonly Button start = Ui.Primary("Start bridge"), stop = Ui.Secondary("Stop"), refresh = Ui.Secondary("New QR code");
        // Setup tab
        readonly TextBox gamePath = new TextBox { Width = 520 };
        readonly Label[] checks = Enumerable.Range(0, 5).Select(i => new Label { AutoSize = true, Margin = new Padding(0, 3, 0, 3) }).ToArray();
        readonly CheckBox desktopShortcut = new CheckBox { Text = "Create a desktop shortcut", AutoSize = true, Checked = true, Margin = new Padding(0, 10, 0, 6) };
        readonly Button install = Ui.Primary("Install"), uninstall = Ui.Secondary("Uninstall…");
        readonly Label installState = new Label { AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(0, 10, 0, 0) };
        readonly Label versionInfo = new Label { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 0, 0, 10) };
        readonly ModPanel modPanel;
        readonly WebPanel webPanel;
        readonly Dictionary<TabPage, BridgePanel> panels = new Dictionary<TabPage, BridgePanel>();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly bool afterInstall;
        Process bridge;
        BridgeClient client;
        DateTime statusStamp;
        string lastError = "";
        bool installing;
        int pairedCount;
        BridgeLink bridgeLink;
        bool portBusy;

        internal MainForm(bool afterInstall)
        {
            this.afterInstall = afterInstall;
            Text = Program.Name;
            Font = Ui.Body;
            BackColor = Ui.Surface;
            Width = 1240; Height = 820; MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = AppIcon();

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Controls.Add(root);
            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(tabs, 0, 1);
            root.Controls.Add(footer, 0, 2);

            modPanel = new ModPanel(() => client, () => bridge != null && !bridge.HasExited);
            BuildPhoneTab();
            modTab.Controls.Add(modPanel);
            panels[cameraTab] = new CameraPanel(() => client);
            panels[trainerTab] = new TrainerPanel(() => client);
            panels[routesTab] = new RoutesPanel(() => client);
            panels[trainingTab] = new TrainingPanel(() => client);
            panels[sensorsTab] = new SensorsPanel(() => client);
            panels[ridersTab] = new RidersPanel(() => client);
            panels[activitiesTab] = new ActivitiesPanel(() => client);
            panels[addonsTab] = new AddonsPanel(() => client);
            panels[optionsTab] = new GameOptionsPanel(() => client);
            panels[diagnosticsTab] = new DiagnosticsPanel(() => client);
            foreach (var pair in panels) pair.Key.Controls.Add(pair.Value);
            BuildSetupTab();
            webPanel = new WebPanel(CurrentBridgeLink, StartBridge, RefreshPairing);
            webTab.Controls.Add(webPanel);
            tabs.TabPages.AddRange(new[] { phoneTab, modTab, cameraTab, trainerTab, routesTab, trainingTab, sensorsTab, ridersTab, activitiesTab, addonsTab, optionsTab, diagnosticsTab, setupTab, webTab });
            modPanel.NavigateAction = id => {
                TabPage target = id == "camera" ? cameraTab : id == "gears" ? trainerTab : id == "routes" ? routesTab : id == "training" ? trainingTab : id == "devices" ? sensorsTab : id == "activities" ? activitiesTab : id == "riders" ? ridersTab : id == "addons" ? addonsTab : null;
                if (target != null) tabs.SelectedTab = target;
            };
            foreach (TabPage page in tabs.TabPages) page.BackColor = Ui.Surface;
            tabs.SelectedIndexChanged += (s, e) => ActivateTab();

            timer.Tick += (s, e) => Poll();
            Shown += (s, e) => OnShown();
            FormClosing += (s, e) => { DeactivateTabs(); webPanel.StopServer(); StopBridge(); };
        }

        /// <summary>All sizes of the app icon (embedded .ico), so the taskbar and title bar each get a sharp one.</summary>
        static Icon AppIcon()
        {
            try
            {
                using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("AppIcon.ico"))
                    if (stream != null) return new Icon(stream);
            }
            catch (Exception) { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { return null; }
        }

        /// <summary>Only the visible tab polls the bridge.</summary>
        void ActivateTab()
        {
            modPanel.Active = tabs.SelectedTab == modTab;
            foreach (var pair in panels) pair.Value.Active = tabs.SelectedTab == pair.Key;
        }

        void DeactivateTabs()
        {
            modPanel.Active = false;
            foreach (var panel in panels.Values) panel.Active = false;
        }

        Control BuildHeader()
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Header, ColumnCount = 3, Padding = new Padding(18, 0, 18, 0), Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = new Label { Text = Program.Name, Font = Ui.Title, ForeColor = Color.White, AutoSize = true, Anchor = AnchorStyles.Left };
            var version = new Label { Text = "v" + BuildInfo.Version + " · for GTBike V " + Installer.SupportedModVersion, ForeColor = Color.FromArgb(148, 163, 184), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(10, 10, 0, 0) };
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(version, 1, 0);
            header.Controls.Add(headerStatus, 2, 0);
            return header;
        }

        void BuildPhoneTab()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(16) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            phoneTab.Controls.Add(layout);

            var qrFrame = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14), Margin = new Padding(0, 0, 16, 0) };
            qrFrame.Controls.Add(qr);
            layout.Controls.Add(qrFrame, 0, 0);

            var info = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Ui.CardBack, Padding = new Padding(18, 14, 18, 14) };
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            info.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            info.Controls.Add(new Label { Text = "Connect your phone", Font = Ui.Heading, AutoSize = true }, 0, 0);
            info.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Ui.Muted, Margin = new Padding(0, 6, 0, 10),
                Text = "In the GTAV Riding Companion Android app, tap Scan QR code and point the camera at the code. " +
                       "The phone and this PC must be on the same home network. If you type the address instead, use this code:" }, 0, 1);
            info.Controls.Add(pairingCode, 0, 2);
            info.Controls.Add(pairingExpiry, 0, 3);
            info.Controls.Add(details, 0, 4);
            layout.Controls.Add(info, 1, 0);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 12, 0, 0) };
            start.Click += (s, e) => StartBridge();
            stop.Click += (s, e) =>
            {
                StopBridge();
                footer.Text = "Bridge stopped. The phone cannot connect until you start it again.";
                FreeBridgePort("Stop it so the bridge can start again?");
            };
            refresh.Click += (s, e) => RefreshPairing();
            var log = Ui.Secondary("Open bridge log");
            log.Width = 150;
            log.Click += (s, e) => OpenPath(Paths.BridgeLog);
            actions.Controls.AddRange(new Control[] { start, stop, refresh, log });
            layout.Controls.Add(actions, 0, 1);
            layout.SetColumnSpan(actions, 2);
            details.Text = "The QR code appears when the bridge is running.";
        }

        void BuildSetupTab()
        {
            var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(24, 18, 24, 18) };
            setupTab.Controls.Add(page);
            page.Controls.Add(new Label { Text = "Install, update or remove", Font = Ui.Heading, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
            page.Controls.Add(versionInfo);
            page.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Ui.Muted, Margin = new Padding(0, 0, 0, 14), Text =
                "Installing copies this app to your user folder, compiles the small game adapter into GTA V\\scripts " +
                "(Windows asks for permission once) and lets your phone reach this PC on TCP port " + Installer.BridgePort + " on private networks only. " +
                "Node.js is included; nothing else needs to be installed." });
            page.Controls.Add(new Label { Text = "GTA V folder (the one with ScriptHookVDotNet3.dll)", AutoSize = true });
            var pathRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 8) };
            var browse = Ui.Secondary("Browse…"); browse.Width = 100; browse.Height = 30;
            var detect = Ui.Secondary("Find it for me"); detect.Width = 130; detect.Height = 30;
            gamePath.Margin = new Padding(0, 3, 8, 0);
            browse.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog { Description = "Choose the main GTA V folder", SelectedPath = gamePath.Text })
                    if (dialog.ShowDialog(this) == DialogResult.OK) gamePath.Text = dialog.SelectedPath;
            };
            detect.Click += (s, e) => Detect(true);
            pathRow.Controls.AddRange(new Control[] { gamePath, browse, detect });
            page.Controls.Add(pathRow);
            foreach (var label in checks) page.Controls.Add(label);
            page.Controls.Add(desktopShortcut);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 0) };
            install.Width = 160; uninstall.Width = 130;
            install.Click += async (s, e) => await InstallClicked();
            uninstall.Click += (s, e) => UninstallClicked();
            var folder = Ui.Secondary("Open app folder"); folder.Width = 150; folder.Click += (s, e) => OpenPath(Paths.AppDir);
            var adapterLog = Ui.Secondary("Open game adapter log"); adapterLog.Width = 190; adapterLog.Click += (s, e) => OpenPath(Paths.AdapterLog);
            buttons.Controls.AddRange(new Control[] { install, uninstall, folder, adapterLog });
            page.Controls.Add(buttons);
            page.Controls.Add(installState);
            gamePath.TextChanged += (s, e) => UpdateChecks();
        }

        void OnShown()
        {
            Detect(false);
            UpdateChecks();
            timer.Start();
            if (Installer.IsInstalled)
            {
                tabs.SelectedTab = afterInstall ? phoneTab : (IsNewerSetup ? setupTab : phoneTab);
                if (IsNewerSetup) footer.Text = NewerSetupHint;
                else StartBridge();
                if (afterInstall) footer.Text = "Installed. Scan the QR code with your phone, then use the Mod tab while you ride.";
            }
            else
            {
                tabs.SelectedTab = setupTab;
                footer.Text = "Welcome! Check the GTA V folder below and press Install.";
            }
            UpdateButtons();
        }

        bool RunningInstalledCopy { get { return String.Equals(Path.GetFullPath(Application.ExecutablePath), Paths.InstalledExe, StringComparison.OrdinalIgnoreCase); } }
        bool IsNewerSetup { get { return !RunningInstalledCopy && Installer.InstalledVersion != BuildInfo.Version; } }
        // A newer setup passes options the installed (older) bridge does not know, so it never starts that bridge.
        string NewerSetupHint { get { return "This setup is v" + BuildInfo.Version + "; the installed app is " + (Installer.InstalledVersion == null ? "an earlier version" : "v" + Installer.InstalledVersion)
            + ". Press Update on the Setup tab first, then the bridge starts."; } }

        void Detect(bool userAsked)
        {
            if (!userAsked && !String.IsNullOrWhiteSpace(gamePath.Text)) return;
            var found = Installer.DetectGameFolders();
            if (found.Count > 0) gamePath.Text = found[0];
            if (userAsked) footer.Text = found.Count == 0 ? "No GTA V folder found automatically. Use Browse… to choose it." : "Found: " + String.Join("  ·  ", found.ToArray());
        }

        void UpdateChecks()
        {
            GameCheck check = Installer.Check(gamePath.Text);
            SetCheck(checks[0], check.FolderExists, "GTA V folder found");
            SetCheck(checks[1], check.ScriptHook, "ScriptHookVDotNet3 installed");
            SetCheck(checks[2], check.Mod && check.ModVersionOk, check.Mod ? "GTBike V " + check.ModVersion + (check.ModVersionOk ? "" : " (needs " + Installer.SupportedModVersion + ")") : "GTBike V installed");
            SetCheck(checks[3], check.GameClosed, check.GameClosed ? "GTA V is closed" : "GTA V is running: close it to install");
            bool adapter = Installer.AdapterInstalled(gamePath.Text);
            checks[4].Text = (adapter ? "●  Game adapter installed in this folder" : "○  Game adapter not installed yet");
            checks[4].ForeColor = adapter ? Ui.Good : Ui.Muted;
            string installed = Installer.InstalledVersion;
            versionInfo.Text = !Installer.IsInstalled ? "Not installed yet · this setup: v" + BuildInfo.Version
                : "Installed: " + (installed == null ? "an earlier version" : "v" + installed) + (RunningInstalledCopy ? "" : " · this setup: v" + BuildInfo.Version);
            install.Text = !Installer.IsInstalled ? "Install" : IsNewerSetup ? "Update to v" + BuildInfo.Version : "Repair";
            install.Enabled = check.Ready && !installing;
            uninstall.Enabled = Installer.IsInstalled && !installing;
            if (!installing) { installState.ForeColor = check.Ready ? Ui.Muted : Ui.Warn; installState.Text = check.FirstProblem ?? "Ready."; }
        }

        static void SetCheck(Label label, bool ok, string text)
        {
            label.Text = Ui.Mark(ok) + "  " + text;
            label.ForeColor = ok ? Ui.Good : Ui.Bad;
        }

        async Task InstallClicked()
        {
            string game = gamePath.Text;
            bool shortcut = desktopShortcut.Checked;
            DeactivateTabs();
            StopBridge();
            installing = true;
            install.Enabled = uninstall.Enabled = false;
            installState.ForeColor = Ui.Text;
            Action<string> progress = text => BeginInvoke(new Action(() => installState.Text = text));
            try
            {
                string warning = await Task.Run(() => Installer.Install(game, shortcut, progress));
                if (!String.IsNullOrEmpty(warning)) MessageBox.Show(this, warning, "Check Windows Firewall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (!RunningInstalledCopy)
                {
                    // Hand over to the installed copy so shortcuts, updates and uninstall all use the same executable.
                    timer.Stop();
                    Program.ReleaseInstance();
                    Process.Start(new ProcessStartInfo(Paths.InstalledExe, "--after-install") { WorkingDirectory = Paths.AppDir });
                    Close();
                    return;
                }
                installState.ForeColor = Ui.Good;
                installState.Text = "✓ Repaired. Start GTA V in Story Mode; the game adapter loads with it.";
                tabs.SelectedTab = phoneTab;
                StartBridge();
            }
            catch (Exception error)
            {
                installState.ForeColor = Ui.Bad;
                installState.Text = "Not installed: " + error.Message;
            }
            finally
            {
                installing = false;
                if (!IsDisposed) { UpdateChecks(); UpdateButtons(); }
            }
        }

        void UninstallClicked()
        {
            DeactivateTabs();
            StopBridge();
            if (Installer.UninstallInteractive()) { timer.Stop(); Close(); }
            else { UpdateChecks(); UpdateButtons(); }
        }

        void StartBridge()
        {
            if (bridge != null && !bridge.HasExited) return;
            string node = Path.Combine(Paths.AppDir, "node.exe");
            string script = Path.Combine(Paths.AppDir, "bridge-v1.mjs");
            if (!File.Exists(node) || !File.Exists(script)) { footer.Text = "Install first (Setup tab) to start the bridge."; UpdateButtons(); return; }
            if (IsNewerSetup) { tabs.SelectedTab = setupTab; footer.Text = NewerSetupHint; UpdateButtons(); return; }
            if (!FreeBridgePort("Stop it and start the bridge?")) { UpdateButtons(); return; }
            try
            {
                if (File.Exists(Paths.StatusFile)) File.Delete(Paths.StatusFile);
                statusStamp = DateTime.MinValue; lastError = ""; client = null; bridgeLink = null;
                var info = new ProcessStartInfo(node, "\"" + script + "\" --desktop-status-file \"" + Paths.StatusFile + "\" --mod-config \"" + GameOptionsPanel.ModConfigPath + "\"");
                info.WorkingDirectory = Paths.AppDir; info.UseShellExecute = false; info.CreateNoWindow = true;
                info.RedirectStandardInput = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.UTF8; info.StandardErrorEncoding = Encoding.UTF8;
                bridge = new Process { StartInfo = info, EnableRaisingEvents = true };
                bridge.OutputDataReceived += (s, e) => Log(e.Data);
                bridge.ErrorDataReceived += (s, e) => { if (!String.IsNullOrWhiteSpace(e.Data)) { lastError = e.Data; Log(e.Data); } };
                StartLog();
                bridge.Start(); bridge.BeginOutputReadLine(); bridge.BeginErrorReadLine();
                footer.Text = "Starting the bridge…";
            }
            catch (Exception error) { footer.Text = "The bridge could not start: " + error.Message; }
            UpdateButtons();
        }

        readonly object logLock = new object();

        void StartLog()
        {
            lock (logLock)
            {
                try
                {
                    Directory.CreateDirectory(Paths.Root);
                    if (File.Exists(Paths.BridgeLog) && new FileInfo(Paths.BridgeLog).Length > 1024 * 1024) File.Delete(Paths.BridgeLog);
                    File.AppendAllText(Paths.BridgeLog, Environment.NewLine + "=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " bridge started (app v" + BuildInfo.Version + ")" + Environment.NewLine);
                }
                catch (IOException) { }
            }
        }

        void Log(string line)
        {
            if (line == null) return;
            // The console QR is drawn with block characters; it is noise in a log file.
            if (line.IndexOf('█') >= 0 || line.IndexOf('▀') >= 0 || line.IndexOf('▄') >= 0) return;
            lock (logLock) { try { File.AppendAllText(Paths.BridgeLog, line + Environment.NewLine); } catch (IOException) { } }
        }

        /// <summary>Port, transport and current pairing code of the running bridge, for the Web tab.</summary>
        BridgeLink CurrentBridgeLink()
        {
            var known = bridgeLink;
            bool running = bridge != null && !bridge.HasExited;
            if (known == null) return new BridgeLink { Running = false };
            return new BridgeLink { Running = running, Port = known.Port, Https = known.Https, Pin = known.Pin, PinExpiresUtc = known.PinExpiresUtc };
        }

        /// <summary>When another process holds the bridge port (an old bridge left running, for example), offers to end it.
        /// Returns true when the port is free.</summary>
        bool FreeBridgePort(string question)
        {
            var users = PortOwner.Find(Installer.BridgePort);
            portBusy = users.Count > 0;
            if (users.Count == 0) return true;
            var answer = MessageBox.Show(this, "TCP port " + Installer.BridgePort + " is already in use by:\n\n"
                + String.Join("\n\n", users.Select(u => u.Describe()).ToArray()) + "\n\n" + question
                + "\n\nOnly do this if it is not something else you need: the process is ended without saving.",
                "Bridge port in use", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) { footer.Text = "TCP port " + Installer.BridgePort + " is in use by " + users[0].Name + " (PID " + users[0].Pid + "). Press Stop to end it."; return false; }
            Cursor = Cursors.WaitCursor;
            string problem;
            try { problem = PortOwner.Free(Installer.BridgePort, users); }
            finally { Cursor = Cursors.Default; }
            portBusy = problem != null;
            if (problem != null)
            {
                MessageBox.Show(this, "The port could not be freed:\n\n" + problem + "\n\nIf it is an administrator process, close it from Task Manager.",
                    "Bridge port in use", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                footer.Text = "TCP port " + Installer.BridgePort + " is still in use.";
                return false;
            }
            footer.Text = "Freed TCP port " + Installer.BridgePort + ".";
            UpdateButtons();
            return true;
        }

        void StopBridge()
        {
            client = null;
            bridgeLink = null;
            if (bridge != null)
            {
                try { if (!bridge.HasExited) bridge.Kill(); bridge.WaitForExit(3000); } catch (Exception) { }
                bridge.Dispose(); bridge = null;
            }
            if (File.Exists(Paths.StatusFile)) { try { File.Delete(Paths.StatusFile); } catch (Exception) { } }
            if (qr.Image != null) { var old = qr.Image; qr.Image = null; old.Dispose(); }
            details.Text = "The bridge is stopped.";
            pairingCode.Text = ""; pairingExpiry.Text = "";
            UpdateButtons();
        }

        void RefreshPairing()
        {
            try { if (bridge != null && !bridge.HasExited) { bridge.StandardInput.WriteLine(); bridge.StandardInput.Flush(); footer.Text = "New QR code and pairing code requested."; } }
            catch (Exception error) { footer.Text = error.Message; }
        }

        void Poll()
        {
            if (!installing && tabs.SelectedTab == setupTab) UpdateChecks();
            UpdateHeader();
            if (bridge == null) { portBusy = Installer.IsInstalled && PortOwner.Find(Installer.BridgePort).Count > 0; UpdateButtons(); return; }
            if (bridge.HasExited)
            {
                footer.Text = "The bridge stopped (code " + bridge.ExitCode + "). " + lastError;
                StopBridge();
                portBusy = PortOwner.Find(Installer.BridgePort).Count > 0;
                if (portBusy) footer.Text += " Port " + Installer.BridgePort + " is held by another process: press Stop to end it.";
                UpdateButtons();
                return;
            }
            if (!File.Exists(Paths.StatusFile)) return;
            DateTime stamp = File.GetLastWriteTimeUtc(Paths.StatusFile);
            if (stamp == statusStamp) return;
            try
            {
                var data = Json.Parse(File.ReadAllText(Paths.StatusFile, Encoding.UTF8));
                client = BridgeClient.FromStatus(data);
                string[] hosts = ((System.Collections.IList)Json.Get(data, "hosts") ?? new object[0]).Cast<object>().Select(x => Convert.ToString(x)).ToArray();
                var paired = Json.List(data, "paired");
                pairedCount = paired.Count;
                pairingCode.Text = FormatPin(Json.Str(data, "pin"));
                DateTime expires;
                bool hasExpiry = DateTime.TryParse(Json.Str(data, "pinExpiresAtUtc"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out expires);
                pairingExpiry.Text = hasExpiry ? "Valid until " + expires.ToLocalTime().ToString("HH:mm") + " · press New QR code for a fresh one" : "";
                int bridgePort;
                bridgeLink = new BridgeLink { Running = true, Https = Json.Str(data, "transport") != "http", Pin = Json.Str(data, "pin"),
                    PinExpiresUtc = hasExpiry ? expires : DateTime.MinValue,
                    Port = Int32.TryParse(Convert.ToString(Json.Get(data, "port"), System.Globalization.CultureInfo.InvariantCulture), out bridgePort) ? bridgePort : Installer.BridgePort };
                details.Text = "Address:   " + (hosts.Length == 0 ? "no private network address found" : String.Join(", ", hosts)) + "\r\n"
                    + "Port:      " + Json.Get(data, "port") + " (" + (Json.Str(data, "transport") == "http" ? "HTTP, development" : "HTTPS") + ")\r\n"
                    + "Bridge ID: " + Json.Str(data, "bridgeId") + "\r\n"
                    + "Key:       " + Json.Str(data, "fingerprint") + "\r\n\r\n"
                    + "Paired phones:\r\n" + (paired.Count == 0 ? "  none yet" : String.Join("\r\n", paired
                        .GroupBy(p => Json.Str(p, "clientName") ?? "Android")
                        .Select(g => "  " + g.Key + (g.Count() > 1 ? "  (paired " + g.Count() + " times)" : "") + "  until "
                            + g.Select(p => (Json.Str(p, "expiresAtUtc") ?? "").Split('T')[0]).Max()).ToArray()));
                string url = Json.Str(data, "qrDataUrl");
                if (qr.Image != null) { var old = qr.Image; qr.Image = null; old.Dispose(); }
                if (!String.IsNullOrEmpty(url))
                {
                    byte[] bytes = Convert.FromBase64String(url.Substring(url.IndexOf(',') + 1));
                    using (var memory = new MemoryStream(bytes))
                    using (var image = Image.FromStream(memory)) qr.Image = new Bitmap(image);
                }
                statusStamp = stamp;
                footer.Text = hosts.Length == 0 ? "Connect this PC to your home network (private) and restart the bridge." : "Bridge running. Scan the QR code with the Android app.";
            }
            catch (IOException) { /* The bridge is replacing the file. */ }
            catch (Exception error) { footer.Text = "Could not read the bridge status: " + error.Message; }
            UpdateButtons();
        }

        static string FormatPin(string pin) { return pin != null && pin.Length == 6 ? pin.Substring(0, 3) + " " + pin.Substring(3) : (pin ?? ""); }

        void UpdateHeader()
        {
            bool running = bridge != null && !bridge.HasExited;
            headerStatus.Text = (running ? "●  Bridge running" : "○  Bridge stopped") + (running ? "  ·  " + pairedCount + (pairedCount == 1 ? " phone paired" : " phones paired") : "");
            headerStatus.ForeColor = running ? Color.FromArgb(134, 239, 172) : Color.FromArgb(203, 213, 225);
        }

        void UpdateButtons()
        {
            bool running = bridge != null && !bridge.HasExited;
            start.Enabled = !running && Installer.IsInstalled && !installing;
            stop.Enabled = running || portBusy;
            refresh.Enabled = running;
            UpdateHeader();
        }

        void OpenPath(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
                else footer.Text = "Not created yet: " + path;
            }
            catch (Exception error) { footer.Text = error.Message; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
