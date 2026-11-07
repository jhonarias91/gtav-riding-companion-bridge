// A route preview using the same calibration and downloaded map as the Mod tab.
using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class RouteMapPanel : UserControl
    {
        internal readonly MapView Map;
        readonly MapCalibration calibration;
        readonly Button download = Ui.Secondary("Download map (15 MB)");
        readonly Label status = new Label { AutoSize = false, Dock = DockStyle.Bottom, Height = 28, ForeColor = Ui.Muted, Padding = new Padding(6, 4, 0, 0) };
        bool loading;
        string interactionHint = "Scroll to zoom · drag to pan · right-click for coordinates";
        internal string InteractionHint
        {
            set { interactionHint = value; if (File.Exists(Paths.MapImage)) status.Text = value; }
        }

        internal RouteMapPanel()
        {
            Dock = DockStyle.Fill;
            calibration = MapCalibration.Load(Paths.Resource("gta-map.calibration.json"));
            Map = new MapView(calibration) { Dock = DockStyle.Fill, ShowRouteEndpoints = true };
            Map.SetFollow(false);
            Map.HandleCreated += (s, e) => EnsureImage();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false, Padding = new Padding(4, 2, 0, 0) };
            var fit = Ui.Secondary("Fit route"); fit.Width = 100;
            var plus = Ui.Secondary("+"); plus.Width = 36;
            var minus = Ui.Secondary("−"); minus.Width = 36;
            fit.Click += (s, e) => Map.FitRoute();
            plus.Click += (s, e) => Map.ZoomBy(1.5f);
            minus.Click += (s, e) => Map.ZoomBy(1f / 1.5f);
            download.Width = 210;
            download.Click += async (s, e) => await Download();
            bar.Controls.AddRange(new Control[] { fit, plus, minus, download });
            Controls.Add(Map);
            Controls.Add(bar);
            Controls.Add(status);
            status.Text = "Scroll to zoom · drag to pan · right-click for coordinates";
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) EnsureImage();
        }

        internal void EnsureImage()
        {
            bool exists = File.Exists(Paths.MapImage);
            download.Visible = !exists;
            if (exists && !loading && Map.IsHandleCreated)
            {
                loading = true;
                Map.LoadImage(Paths.MapImage, 2400);
            }
            if (!exists) status.Text = "Download the map once to see the course on the GTA V map.";
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            EnsureImage();
        }

        async Task Download()
        {
            string temp = Paths.MapImage + "." + Guid.NewGuid().ToString("N") + ".download";
            download.Enabled = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(temp));
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var web = new WebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent] = "GTAVRidingCompanion/" + BuildInfo.Version;
                    web.DownloadProgressChanged += (s, e) => { if (!IsDisposed) status.Text = "Downloading map… " + e.ProgressPercentage + "%"; };
                    await web.DownloadFileTaskAsync(new Uri(calibration.ImageUrl), temp);
                }
                if (IsDisposed) return;
                if (calibration.ImageBytes > 0 && new FileInfo(temp).Length != calibration.ImageBytes) throw new IOException("The download is not the calibrated map image.");
                using (var image = Image.FromFile(temp))
                    if (image.Width != calibration.Width || image.Height != calibration.Height) throw new IOException("The image dimensions do not match the map calibration.");
                if (!File.Exists(Paths.MapImage)) File.Move(temp, Paths.MapImage);
                EnsureImage();
                status.Text = interactionHint;
            }
            catch (Exception error) { if (!IsDisposed) status.Text = "Map download failed: " + error.Message; }
            finally
            {
                if (!IsDisposed) download.Enabled = true;
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }
    }
}
