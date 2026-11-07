// Web tab: downloads the GTAV Riding Companion PWA from GitHub, runs its web server with the bundled Node.js and opens
// it in the browser on the Wi-Fi address (Ethernet when there is no Wi-Fi), already paired with this PC's bridge. C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    /// <summary>What the Web tab needs from the running bridge (read from its desktop status file).</summary>
    sealed class BridgeLink
    {
        public bool Running;
        public int Port;
        public bool Https;
        public string Pin;
        public DateTime PinExpiresUtc;
    }

    sealed class WebPanel : Panel
    {
        internal const string Repository = "jhonarias91/gtav-riding-companion-pwa";
        internal const string RepositoryUrl = "https://github.com/" + Repository;
        const int FirstPort = 4173;
        static readonly string DefaultFolder = Path.Combine(Paths.Root, "pwa");
        static readonly string FolderChoiceFile = Path.Combine(Paths.Root, "pwa-folder.txt");
        static readonly string DownloadInfoFile = "downloaded-by-gtav-riding-companion.txt";

        readonly Func<BridgeLink> link;
        readonly Action startBridge, requestPin;
        readonly Label folderValue = Ui.Value(), versionValue = Ui.Value(), addressValue = Ui.Value(), serverValue = Ui.Value();
        readonly Label downloadState = new Label { AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Ui.Muted, Margin = new Padding(0, 8, 0, 0) };
        readonly Label runState = new Label { AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Ui.Muted, Margin = new Padding(0, 8, 0, 0) };
        readonly ProgressBar progress = new ProgressBar { Width = 360, Height = 14, Visible = false, Margin = new Padding(0, 8, 0, 0) };
        readonly Button download = Ui.Primary("Download"), chooseFolder = Ui.Secondary("Use a local folder…"), openFolder = Ui.Secondary("Open folder");
        readonly Button run = Ui.Primary("Start and open"), stop = Ui.Secondary("Stop"), reopen = Ui.Secondary("Open in browser");
        readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Ui.CardBack, Font = new Font("Consolas", 9f), ScrollBars = ScrollBars.Vertical, Width = 740, Height = 130 };
        readonly object logLock = new object();
        readonly List<string> logLines = new List<string>();
        Process server;
        string serverUrl;
        TaskCompletionSource<string> serverReady;
        bool busy;

        internal WebPanel(Func<BridgeLink> link, Action startBridge, Action requestPin)
        {
            this.link = link; this.startBridge = startBridge; this.requestPin = requestPin;
            Dock = DockStyle.Fill;
            var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(24, 18, 24, 18) };
            Controls.Add(page);
            page.Controls.Add(new Label { Text = "Web app (PWA)", Font = Ui.Heading, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
            page.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Ui.Muted, Margin = new Padding(0, 0, 0, 14), Text =
                "The companion also runs in a web browser, on this PC or on any phone on the same network. Download it once from GitHub; " +
                "Start and open runs it with the Node.js included in this app and opens it in your browser on the Wi-Fi address " +
                "(Ethernet when there is no Wi-Fi), already paired with this PC." });

            var files = Ui.Card("Web app files", 780);
            Ui.Row(files, "Source", new LinkLabel { Text = RepositoryUrl, AutoSize = true, Margin = new Padding(3, 5, 3, 3), LinkColor = Ui.AccentDark });
            ((LinkLabel)files.Controls[files.Controls.Count - 1]).LinkClicked += (s, e) => OpenUrl(RepositoryUrl);
            Ui.Row(files, "Folder", folderValue);
            Ui.Row(files, "Version", versionValue);
            folderValue.MaximumSize = versionValue.MaximumSize = new Size(560, 0);
            var fileButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 10, 0, 0) };
            download.Width = 150; chooseFolder.Width = 170; openFolder.Width = 120;
            download.Click += async (s, e) => await Download();
            chooseFolder.Click += (s, e) => ChooseFolder();
            openFolder.Click += (s, e) => { if (Directory.Exists(Folder)) Process.Start("explorer.exe", "\"" + Folder + "\""); };
            fileButtons.Controls.AddRange(new Control[] { download, chooseFolder, openFolder });
            Ui.Full(files, fileButtons);
            Ui.Full(files, progress);
            Ui.Full(files, downloadState);
            page.Controls.Add(files);

            var running = Ui.Card("Run", 780);
            Ui.Row(running, "Server", serverValue);
            Ui.Row(running, "Address", addressValue);
            addressValue.MaximumSize = new Size(560, 0);
            var runButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 10, 0, 0) };
            run.Width = 160; stop.Width = 100; reopen.Width = 150;
            run.Click += async (s, e) => await StartAndOpen();
            stop.Click += (s, e) => { StopServer(); runState.ForeColor = Ui.Muted; runState.Text = "Web server stopped."; };
            reopen.Click += async (s, e) => await OpenBrowser();
            runButtons.Controls.AddRange(new Control[] { run, stop, reopen });
            Ui.Full(running, runButtons);
            Ui.Full(running, runState);
            Ui.Full(running, log);
            page.Controls.Add(running);
            UpdateView();
        }

        /// <summary>The folder with server.mjs: a local copy the user picked, or the downloaded one.</summary>
        static string Folder
        {
            get
            {
                try
                {
                    if (File.Exists(FolderChoiceFile))
                    {
                        string chosen = File.ReadAllText(FolderChoiceFile, Encoding.UTF8).Trim();
                        if (chosen.Length > 0) return chosen;
                    }
                }
                catch (IOException) { }
                return DefaultFolder;
            }
        }

        static bool IsPwaFolder(string folder)
        {
            return File.Exists(Path.Combine(folder, "server.mjs")) && File.Exists(Path.Combine(folder, "dist", "index.html"));
        }

        bool ServerRunning { get { return server != null && !server.HasExited; } }

        void UpdateView()
        {
            string folder = Folder;
            bool custom = !String.Equals(folder, DefaultFolder, StringComparison.OrdinalIgnoreCase);
            bool present = IsPwaFolder(folder);
            folderValue.Text = folder + (custom ? "  (local copy)" : "");
            string info = Path.Combine(folder, DownloadInfoFile);
            versionValue.Text = !present ? "Not downloaded yet" : File.Exists(info) ? ReadInfo(info) : custom ? "Local copy (not managed by this app)" : "Downloaded";
            download.Text = custom ? "Download instead" : present ? "Update" : "Download";
            download.Enabled = !busy;
            chooseFolder.Text = custom ? "Use the download" : "Use a local folder…";
            chooseFolder.Enabled = !busy && !ServerRunning;
            openFolder.Enabled = Directory.Exists(folder);
            run.Enabled = !busy && present && !ServerRunning;
            stop.Enabled = ServerRunning;
            reopen.Enabled = !busy && ServerRunning;
            serverValue.Text = ServerRunning ? "Running" : "Stopped";
            serverValue.ForeColor = ServerRunning ? Ui.Good : Ui.Muted;
            if (ServerRunning) addressValue.Text = serverUrl ?? "starting…";
            else { string ip = LanAddress(); addressValue.Text = ip == null ? "No Wi-Fi or Ethernet address found" : "http://" + ip + ":" + FirstPort + "  (when started)"; }
        }

        static string ReadInfo(string file)
        {
            try
            {
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                string commit = lines.Where(l => l.StartsWith("commit=")).Select(l => l.Substring(7)).FirstOrDefault();
                string when = lines.Where(l => l.StartsWith("downloadedAtUtc=")).Select(l => l.Substring(16)).FirstOrDefault();
                return (commit == null || commit.Length < 7 ? "main" : commit.Substring(0, 7)) + (when == null ? "" : " · downloaded " + Fmt.LocalTime(when));
            }
            catch (IOException) { return "Downloaded"; }
        }

        void ChooseFolder()
        {
            if (!String.Equals(Folder, DefaultFolder, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(FolderChoiceFile); } catch (IOException) { }
                UpdateView();
                return;
            }
            using (var dialog = new FolderBrowserDialog { Description = "Choose a gtav-riding-companion-pwa folder (with server.mjs and dist\\index.html)" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (!IsPwaFolder(dialog.SelectedPath))
                {
                    downloadState.ForeColor = Ui.Bad;
                    downloadState.Text = "That folder has no server.mjs and dist\\index.html, so it is not the web app.";
                    return;
                }
                Directory.CreateDirectory(Paths.Root);
                File.WriteAllText(FolderChoiceFile, dialog.SelectedPath, Encoding.UTF8);
                downloadState.ForeColor = Ui.Muted;
                downloadState.Text = "Using the local copy. Press Use the download to go back.";
            }
            UpdateView();
        }

        /// <summary>Downloads the current main branch as a zip, checks it is the web app and replaces the previous download.</summary>
        async Task Download()
        {
            string zip = Path.Combine(Paths.Root, "pwa-download.zip");
            string extract = Path.Combine(Paths.Root, "pwa-download");
            busy = true;
            progress.Value = 0; progress.Visible = true;
            downloadState.ForeColor = Ui.Muted;
            downloadState.Text = "Downloading from GitHub…";
            UpdateView();
            try
            {
                Directory.CreateDirectory(Paths.Root);
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string commit = null;
                using (var web = new WebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent] = "GTAVRidingCompanion/" + BuildInfo.Version;
                    web.Headers[HttpRequestHeader.Accept] = "application/vnd.github.sha";
                    try
                    {
                        string sha = (await web.DownloadStringTaskAsync(new Uri("https://api.github.com/repos/" + Repository + "/commits/main"))).Trim();
                        if (Regex.IsMatch(sha, "^[0-9a-f]{40}$")) commit = sha;
                    }
                    catch (WebException) { /* Rate limit or no API access: take the branch as it is. */ }
                }
                using (var web = new WebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent] = "GTAVRidingCompanion/" + BuildInfo.Version;
                    web.DownloadProgressChanged += (s, e) =>
                    {
                        if (e.TotalBytesToReceive > 0) progress.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
                        downloadState.Text = "Downloading from GitHub… " + Fmt.Bytes(e.BytesReceived);
                    };
                    await web.DownloadFileTaskAsync(new Uri("https://codeload.github.com/" + Repository + "/zip/" + (commit ?? "refs/heads/main")), zip);
                }
                downloadState.Text = "Unpacking…";
                await Task.Run(() =>
                {
                    if (Directory.Exists(extract)) Directory.Delete(extract, true);
                    ZipFile.ExtractToDirectory(zip, extract);
                });
                string[] tops = Directory.GetDirectories(extract);
                if (tops.Length != 1 || !IsPwaFolder(tops[0])) throw new Exception("The download is not the web app (server.mjs or dist\\index.html is missing).");
                File.WriteAllLines(Path.Combine(tops[0], DownloadInfoFile), new[] {
                    "source=" + RepositoryUrl, "commit=" + (commit ?? "main"), "downloadedAtUtc=" + DateTime.UtcNow.ToString("o") }, Encoding.UTF8);
                bool wasRunning = ServerRunning && String.Equals(Folder, DefaultFolder, StringComparison.OrdinalIgnoreCase);
                if (wasRunning) StopServer();
                await Task.Run(() =>
                {
                    if (Directory.Exists(DefaultFolder)) Directory.Delete(DefaultFolder, true);
                    Directory.Move(tops[0], DefaultFolder);
                });
                try { File.Delete(FolderChoiceFile); } catch (IOException) { }
                downloadState.ForeColor = Ui.Good;
                downloadState.Text = "✓ Downloaded. Press Start and open." + (wasRunning ? " (The web server was stopped to update it.)" : "");
            }
            catch (Exception error)
            {
                downloadState.ForeColor = Ui.Bad;
                downloadState.Text = "The web app could not be downloaded: " + (error.InnerException ?? error).Message + " Check the internet connection and try again.";
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch (IOException) { }
                try { if (Directory.Exists(extract)) Directory.Delete(extract, true); } catch (Exception) { }
                progress.Visible = false;
                busy = false;
                UpdateView();
            }
        }

        async Task StartAndOpen()
        {
            if (ServerRunning) { await OpenBrowser(); return; }
            string node = Path.Combine(Paths.AppDir, "node.exe");
            if (!File.Exists(node)) { Fail("Install first (Setup tab): the web app runs with the Node.js included in this app."); return; }
            string folder = Folder;
            if (!IsPwaFolder(folder)) { Fail("Download the web app first."); return; }
            string ip = LanAddress();
            if (ip == null) { Fail("This PC has no private Wi-Fi or Ethernet address. Connect it to your home network and try again."); return; }
            busy = true;
            UpdateView();
            try
            {
                runState.ForeColor = Ui.Muted;
                BridgeLink bridge = await WaitForBridge();
                if (bridge == null) { Fail("The bridge is not running. Start it on the Phone tab, then try again."); return; }
                int port = FreePort(ip);
                if (port == 0) { Fail("TCP ports " + FirstPort + "–" + (FirstPort + 10) + " are all in use on " + ip + "."); return; }
                runState.Text = "Starting the web server…";
                string arguments = "server.mjs --host " + ip + " --port " + port + " --bridge-port " + bridge.Port + (bridge.Https ? "" : " --bridge-http-dev");
                var info = new ProcessStartInfo(node, arguments)
                {
                    WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                lock (logLock) logLines.Clear();
                log.Text = "";
                serverReady = new TaskCompletionSource<string>();
                serverUrl = null;
                server = new Process { StartInfo = info, EnableRaisingEvents = true };
                server.OutputDataReceived += (s, e) => OnServerLine(e.Data, false);
                server.ErrorDataReceived += (s, e) => OnServerLine(e.Data, true);
                server.Exited += (s, e) => { var ready = serverReady; if (ready != null) ready.TrySetResult(null); OnUi(() => { UpdateView(); if (runState.ForeColor != Ui.Bad) { runState.ForeColor = Ui.Muted; runState.Text = "The web server stopped."; } }); };
                server.Start(); server.BeginOutputReadLine(); server.BeginErrorReadLine();
                AddLog("> node " + arguments);
                var finished = await Task.WhenAny(serverReady.Task, Task.Delay(15000));
                string url = finished == serverReady.Task ? serverReady.Task.Result : null;
                if (url == null)
                {
                    bool exited = server.HasExited;
                    StopServer();
                    Fail(exited ? "The web server stopped right away. See the log below." : "The web server did not start within 15 seconds.");
                    return;
                }
                serverUrl = url;
                UpdateView();
                await OpenBrowser();
            }
            catch (Exception error) { StopServer(); Fail("The web server could not start: " + error.Message); }
            finally { busy = false; UpdateView(); }
        }

        void OnServerLine(string line, bool error)
        {
            if (String.IsNullOrWhiteSpace(line)) return;
            AddLog(line);
            var match = Regex.Match(line, @"GTAV Riding Companion PWA: (https?://[0-9.]+:\d+)");
            var ready = serverReady;
            if (match.Success && ready != null) ready.TrySetResult(match.Groups[1].Value);
            if (error) OnUi(() => { runState.ForeColor = Ui.Bad; runState.Text = line; });
        }

        void AddLog(string line)
        {
            string text;
            lock (logLock)
            {
                logLines.Add(line);
                if (logLines.Count > 200) logLines.RemoveRange(0, logLines.Count - 200);
                text = String.Join("\r\n", logLines);
            }
            OnUi(() => { log.Text = text; log.SelectionStart = log.TextLength; log.ScrollToCaret(); });
        }

        /// <summary>Opens the browser with a current pairing code in the address fragment; the page pairs itself and removes it.</summary>
        async Task OpenBrowser()
        {
            if (!ServerRunning || serverUrl == null) return;
            BridgeLink bridge = link();
            if (bridge != null && bridge.Running && (bridge.Pin == null || bridge.PinExpiresUtc < DateTime.UtcNow.AddSeconds(20)))
            {
                // The bridge only rotates an expired code when asked: ask, then wait for the status file to show the new one.
                string old = bridge.Pin;
                requestPin();
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(200);
                    bridge = link();
                    if (bridge == null || !bridge.Running || bridge.Pin != old && bridge.PinExpiresUtc > DateTime.UtcNow.AddSeconds(20)) break;
                }
            }
            bool withPin = bridge != null && bridge.Running && bridge.Pin != null && Regex.IsMatch(bridge.Pin, "^[0-9]{6}$") && bridge.PinExpiresUtc > DateTime.UtcNow;
            OpenUrl(serverUrl + "/" + (withPin ? "#pair=" + bridge.Pin : ""));
            runState.ForeColor = Ui.Good;
            runState.Text = withPin
                ? "✓ Opened " + serverUrl + " in your browser; it pairs itself with this PC. On a phone on the same network, open the same address and type the code from the Phone tab."
                : "Opened " + serverUrl + " in your browser. The bridge is not running, so it could not be paired automatically.";
        }

        /// <summary>Waits up to 15 s for the bridge status (port and pairing code), starting the bridge if needed.</summary>
        async Task<BridgeLink> WaitForBridge()
        {
            BridgeLink bridge = link();
            if (bridge != null && bridge.Running && bridge.Port > 0) return bridge;
            runState.Text = "Starting the bridge…";
            startBridge();
            for (int i = 0; i < 75; i++)
            {
                await Task.Delay(200);
                bridge = link();
                if (bridge != null && bridge.Running && bridge.Port > 0) return bridge;
            }
            return null;
        }

        void Fail(string text)
        {
            runState.ForeColor = Ui.Bad;
            runState.Text = text;
        }

        internal void StopServer() { StopServer(true); }

        void StopServer(bool refresh)
        {
            var process = server;
            server = null;
            serverUrl = null;
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(); process.WaitForExit(3000); } catch (Exception) { }
                process.Dispose();
            }
            if (refresh && !IsDisposed) UpdateView();
        }

        void OnUi(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
        }

        static int FreePort(string ip)
        {
            for (int port = FirstPort; port <= FirstPort + 10; port++)
            {
                try
                {
                    var probe = new TcpListener(IPAddress.Parse(ip), port);
                    probe.Start(); probe.Stop();
                    return port;
                }
                catch (SocketException) { }
            }
            return 0;
        }

        static readonly Regex VirtualAdapter = new Regex("vethernet|virtual|vmware|vbox|hyper-v|wsl|docker|loopback|bluetooth|vpn|tailscale|zerotier|hamachi|wi-fi direct", RegexOptions.IgnoreCase);

        /// <summary>The private IPv4 of the Wi-Fi adapter; otherwise the wired (Ethernet) one; null when neither is connected.</summary>
        internal static string LanAddress()
        {
            string wired = null;
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || VirtualAdapter.IsMatch(nic.Name + " " + nic.Description)) continue;
                    bool wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                    bool ethernet = nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet || nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet
                        || nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT || nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx;
                    if (!wifi && !ethernet) continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(unicast.Address)) continue;
                        if (wifi) return unicast.Address.ToString();
                        if (wired == null) wired = unicast.Address.ToString();
                    }
                }
            }
            catch (NetworkInformationException) { }
            return wired;
        }

        static bool IsPrivate(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) StopServer(false);
            base.Dispose(disposing);
        }
    }
}
