// Zoomable GTA V map: mouse wheel zooms around the cursor, left drag pans, double-click zooms in, right-click offers
// teleporting the rider there (P16, player.teleport), a GPS destination (P17, the game's own map waypoint and route),
// centring and copying coordinates. World <-> image uses the linear calibration in gta-map.calibration.json (no rotation: the
// satellite image is north-up). Downscaled copies keep painting fast at low zoom.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTAVRidingCompanion
{
    sealed class MapCalibration
    {
        // World X/Y (metres) to image pixels: px = ScaleX * x + OffsetX, py = ScaleY * y + OffsetY.
        public double ScaleX = 0.66304, OffsetX = 2629.6, ScaleY = -0.656, OffsetY = 5471.1;
        public string Image = "gta-map.jpg";
        public string ImageUrl = "https://i.redd.it/8qfr3xqfbw671.jpg";
        public long ImageBytes = 15094127;
        public int Width = 5944, Height = 8075;

        internal static MapCalibration Load(string path)
        {
            var calibration = new MapCalibration();
            try
            {
                if (!File.Exists(path)) return calibration;
                var root = Json.Parse(File.ReadAllText(path));
                var world = Json.Obj(root, "worldToPixel");
                calibration.ScaleX = Json.Num(world, "scaleX") ?? calibration.ScaleX;
                calibration.OffsetX = Json.Num(world, "offsetX") ?? calibration.OffsetX;
                calibration.ScaleY = Json.Num(world, "scaleY") ?? calibration.ScaleY;
                calibration.OffsetY = Json.Num(world, "offsetY") ?? calibration.OffsetY;
                calibration.Image = Json.Str(root, "image") ?? calibration.Image;
                calibration.ImageUrl = Json.Str(root, "imageUrl") ?? calibration.ImageUrl;
                calibration.ImageBytes = (long)(Json.Num(root, "imageBytes") ?? calibration.ImageBytes);
                calibration.Width = Json.Int(root, "width") ?? calibration.Width;
                calibration.Height = Json.Int(root, "height") ?? calibration.Height;
            }
            catch (Exception) { /* Defaults match the bundled image. */ }
            return calibration;
        }

        public PointF ToImage(double x, double y) { return new PointF((float)(ScaleX * x + OffsetX), (float)(ScaleY * y + OffsetY)); }
        public double WorldX(double px) { return (px - OffsetX) / ScaleX; }
        public double WorldY(double py) { return (py - OffsetY) / ScaleY; }
    }

    /// <summary>P18: one saved Discover place as the map draws it.</summary>
    sealed class PlaceMark
    {
        public string Id, Name;
        public double X, Y;
        public int Found;
    }

    /// <summary>P18: one ghost segment as the map draws it.</summary>
    sealed class SegmentMark
    {
        public string Id, Name;
        public double StartX, StartY, FinishX, FinishY;
        public double? BestTimeS;
        public bool Loop;
    }

    sealed class MapView : Control
    {
        const float MaxZoom = 4f;
        static readonly Color Ocean = Color.FromArgb(20, 61, 107); // The image's own sea colour at its edges.
        readonly MapCalibration calibration;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem copyItem = new ToolStripMenuItem("Copy coordinates");
        readonly ToolStripMenuItem centerItem = new ToolStripMenuItem("Center on rider");
        readonly ToolStripMenuItem teleportItem = new ToolStripMenuItem("Teleport here") { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        readonly ToolStripMenuItem routeItem = new ToolStripMenuItem("Route here (GPS)");
        readonly ToolStripMenuItem clearRouteItem = new ToolStripMenuItem("Clear destination");
        readonly ToolStripSeparator teleportSeparator = new ToolStripSeparator();
        readonly ToolStripMenuItem movePointItem = new ToolStripMenuItem("Move selected point here");
        readonly ToolStripMenuItem addPointItem = new ToolStripMenuItem("Add waypoint here");
        // P18 ghost segments.
        readonly ToolStripMenuItem segmentStartItem = new ToolStripMenuItem("Ghost segment: start here");
        readonly ToolStripMenuItem segmentFinishItem = new ToolStripMenuItem("Ghost segment: finish here");
        readonly ToolStripMenuItem segmentCancelItem = new ToolStripMenuItem("Cancel the new segment");
        readonly ToolStripMenuItem segmentRenameItem = new ToolStripMenuItem("Rename segment");
        readonly ToolStripMenuItem segmentRemoveItem = new ToolStripMenuItem("Remove segment");
        readonly ToolStripSeparator segmentSeparator = new ToolStripSeparator();
        // P18 discover: saved places.
        readonly ToolStripMenuItem placeSaveItem = new ToolStripMenuItem("Discover: save place here");
        readonly ToolStripMenuItem placeRenameItem = new ToolStripMenuItem("Rename place");
        readonly ToolStripMenuItem placeRemoveItem = new ToolStripMenuItem("Remove place");
        List<PlaceMark> places = new List<PlaceMark>();
        PlaceMark menuPlace;
        List<SegmentMark> segments = new List<SegmentMark>();
        PointF? pendingStart;     // image coordinates of a new segment's start
        SegmentMark menuSegment;  // segment under the right-click
        Image full;
        float fullScale = 1f;
        readonly List<KeyValuePair<float, Bitmap>> levels = new List<KeyValuePair<float, Bitmap>>(); // scale -> copy
        int imageWidth, imageHeight;
        float zoom = 0.1f, centerX, centerY;
        bool viewSet, dragging, moved;
        Point dragStart, lastMouse;
        PointF menuImagePoint;
        string loadError;

        PointF? rider;           // image coordinates
        float riderHeading;      // GTA heading in degrees (0 = north, counter-clockwise)
        List<PointF> route = new List<PointF>();
        PointF? target;          // image coordinates of a teleport in progress
        PointF? destination;     // image coordinates of the game's map waypoint
        string destinationText;  // "2.4 km by road" or null
        bool fitRoutePending;
        bool routeClosed, addingRoutePoints, pointDragMoved;
        int pointDragIndex = -1;
        Point pointDragMouse;
        PointF pointDragOrigin;

        public event Action FollowChanged;
        /// <summary>World X/Y (metres) the user chose with "Teleport here".</summary>
        public event Action<double, double> TeleportRequested;
        /// <summary>false hides "Teleport here" (bridge older than P16).</summary>
        public bool TeleportVisible { get; set; }
        /// <summary>Why "Teleport here" is unavailable right now; null when it is allowed.</summary>
        public string TeleportBlock { get; set; }
        /// <summary>World X/Y (metres) the user chose with "Route here (GPS)".</summary>
        public event Action<double, double> RouteRequested;
        public event Action ClearRouteRequested;
        /// <summary>false hides the destination items (bridge older than P17).</summary>
        public bool DestinationVisible { get; set; }
        /// <summary>Why the destination cannot be set or cleared right now; null when it can.</summary>
        public string DestinationBlock { get; set; }
        public bool HasDestination { get { return destination.HasValue; } }
        public bool Follow { get; private set; }
        public string Overlay { get; set; }
        public bool ShowRouteEndpoints { get; set; }
        public bool EditPoints { get; set; }
        public int SelectedRoutePoint { get; set; }
        /// <summary>Click empty map space to place the start and successive waypoints; dragging still pans.</summary>
        public bool AddRoutePoints
        {
            get { return addingRoutePoints; }
            set { addingRoutePoints = value; Cursor = value ? Cursors.Cross : Cursors.Hand; Invalidate(); }
        }
        /// <summary>Index 0 is the start, followed by the actual waypoints (no duplicate loop endpoint).</summary>
        public event Action<int> RoutePointSelected;
        /// <summary>Committed once on mouse release, in GTA world metres. Mouse moves only update the preview.</summary>
        public event Action<int, double, double> RoutePointMoved;
        public event Action<double, double> RoutePointAdded;
        /// <summary>World X/Y, and true to append a waypoint (context-menu alternative to dragging).</summary>
        public event Action<double, double, bool> EditPointRequested;
        /// <summary>P18: false hides the segment items (bridge older than ghost segments).</summary>
        public bool SegmentsVisible { get; set; }
        /// <summary>Why segments cannot be changed right now; null when they can.</summary>
        public string SegmentBlock { get; set; }
        /// <summary>Start X/Y and finish X/Y (GTA metres) of a new segment.</summary>
        public event Action<double, double, double, double> SegmentCreateRequested;
        public event Action<SegmentMark> SegmentRenameRequested;
        public event Action<SegmentMark> SegmentRemoveRequested;
        /// <summary>P18: false hides the place items (bridge older than Discover).</summary>
        public bool PlacesVisible { get; set; }
        public event Action<double, double> PlaceSaveRequested;
        public event Action<PlaceMark> PlaceRenameRequested;
        public event Action<PlaceMark> PlaceRemoveRequested;

        public MapView(MapCalibration calibration)
        {
            this.calibration = calibration;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Ocean;
            Cursor = Cursors.Hand;
            Follow = true;
            SelectedRoutePoint = -1;
            copyItem.Click += (s, e) =>
            {
                try { Clipboard.SetText(String.Format(CultureInfo.InvariantCulture, "{0:0.0}, {1:0.0}", calibration.WorldX(menuImagePoint.X), calibration.WorldY(menuImagePoint.Y))); }
                catch (Exception) { }
            };
            centerItem.Click += (s, e) => SetFollow(true);
            teleportItem.Click += (s, e) =>
            {
                if (TeleportRequested != null) TeleportRequested(calibration.WorldX(menuImagePoint.X), calibration.WorldY(menuImagePoint.Y));
            };
            routeItem.Click += (s, e) =>
            {
                if (RouteRequested != null) RouteRequested(calibration.WorldX(menuImagePoint.X), calibration.WorldY(menuImagePoint.Y));
            };
            clearRouteItem.Click += (s, e) => { if (ClearRouteRequested != null) ClearRouteRequested(); };
            movePointItem.Click += (s, e) => PickPoint(false);
            addPointItem.Click += (s, e) => PickPoint(true);
            segmentStartItem.Click += (s, e) => { pendingStart = menuImagePoint; Invalidate(); };
            segmentFinishItem.Click += (s, e) =>
            {
                if (!pendingStart.HasValue) return;
                PointF start = pendingStart.Value;
                pendingStart = null;
                Invalidate();
                if (SegmentCreateRequested != null)
                    SegmentCreateRequested(calibration.WorldX(start.X), calibration.WorldY(start.Y), calibration.WorldX(menuImagePoint.X), calibration.WorldY(menuImagePoint.Y));
            };
            segmentCancelItem.Click += (s, e) => { pendingStart = null; Invalidate(); };
            segmentRenameItem.Click += (s, e) => { if (menuSegment != null && SegmentRenameRequested != null) SegmentRenameRequested(menuSegment); };
            segmentRemoveItem.Click += (s, e) => { if (menuSegment != null && SegmentRemoveRequested != null) SegmentRemoveRequested(menuSegment); };
            placeSaveItem.Click += (s, e) => { if (PlaceSaveRequested != null) PlaceSaveRequested(calibration.WorldX(menuImagePoint.X), calibration.WorldY(menuImagePoint.Y)); };
            placeRenameItem.Click += (s, e) => { if (menuPlace != null && PlaceRenameRequested != null) PlaceRenameRequested(menuPlace); };
            placeRemoveItem.Click += (s, e) => { if (menuPlace != null && PlaceRemoveRequested != null) PlaceRemoveRequested(menuPlace); };
            menu.Items.AddRange(new ToolStripItem[] { movePointItem, addPointItem, teleportItem, routeItem, clearRouteItem, teleportSeparator,
                segmentStartItem, segmentFinishItem, segmentCancelItem, segmentRenameItem, segmentRemoveItem, placeSaveItem, placeRenameItem, placeRemoveItem, segmentSeparator, centerItem, copyItem });
        }

        void PickPoint(bool append)
        {
            if (EditPointRequested != null) EditPointRequested(calibration.WorldX(menuImagePoint.X), calibration.WorldY(menuImagePoint.Y), append);
        }

        /// <summary>Loads the image and builds the downscaled copies off the UI thread.</summary>
        public void LoadImage(string path) { LoadImage(path, 0); }

        // Previews use a smaller bitmap but retain coordinates in the original calibrated image.
        public void LoadImage(string path, int maxDimension)
        {
            Task.Run(() =>
            {
                Image image = null;
                var built = new List<KeyValuePair<float, Bitmap>>();
                try
                {
                    int originalWidth, originalHeight;
                    float baseScale;
                    using (var stream = File.OpenRead(path))
                    using (var original = Image.FromStream(stream))
                    {
                        originalWidth = original.Width; originalHeight = original.Height;
                        baseScale = maxDimension > 0 ? Math.Min(1f, maxDimension / (float)Math.Max(originalWidth, originalHeight)) : 1f;
                        image = new Bitmap(Math.Max(1, (int)(originalWidth * baseScale)), Math.Max(1, (int)(originalHeight * baseScale)), PixelFormat.Format32bppPArgb);
                        using (var g = Graphics.FromImage(image))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.DrawImage(original, new Rectangle(0, 0, image.Width, image.Height));
                        }
                    }
                    Image source = image;
                    for (float scale = 0.5f; scale >= 0.06f; scale /= 2)
                    {
                        var copy = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)), PixelFormat.Format32bppPArgb);
                        using (var g = Graphics.FromImage(copy))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(source, new Rectangle(0, 0, copy.Width, copy.Height));
                        }
                        built.Add(new KeyValuePair<float, Bitmap>(baseScale * scale, copy));
                        source = copy;
                    }
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) { image.Dispose(); foreach (var level in built) level.Value.Dispose(); return; }
                        if (full != null) full.Dispose();
                        foreach (var level in levels) level.Value.Dispose();
                        full = image;
                        fullScale = baseScale;
                        imageWidth = originalWidth;
                        imageHeight = originalHeight;
                        loadError = null;
                        levels.Clear();
                        levels.AddRange(built);
                        if (fitRoutePending && route.Count > 0) FitRoute(); else FitAll();
                    }));
                }
                catch (Exception error)
                {
                    if (image != null) image.Dispose();
                    foreach (var level in built) level.Value.Dispose();
                    try { BeginInvoke(new Action(() => { loadError = "The map image could not be loaded: " + error.Message; Invalidate(); })); }
                    catch (InvalidOperationException) { }
                }
            });
        }

        public void SetRider(double? x, double? y, double? heading)
        {
            rider = x.HasValue && y.HasValue ? (PointF?)calibration.ToImage(x.Value, y.Value) : null;
            riderHeading = (float)(heading ?? 0);
            if (rider.HasValue && Follow && full != null)
            {
                if (!viewSet) { zoom = Math.Max(MinZoom, 0.5f); viewSet = true; }
                centerX = rider.Value.X;
                centerY = rider.Value.Y;
            }
            Invalidate();
        }

        public void SetRoute(IEnumerable<KeyValuePair<double, double>> points) { SetRoute(points, false); }

        public void SetRoute(IEnumerable<KeyValuePair<double, double>> points, bool closeLoop)
        {
            CancelPointDrag();
            routeClosed = closeLoop;
            route = new List<PointF>();
            if (points != null) foreach (var p in points) route.Add(calibration.ToImage(p.Key, p.Value));
            Invalidate();
        }

        public void FitRoute()
        {
            fitRoutePending = true;
            if (full == null || route.Count == 0 || Width < 1 || Height < 1) return;
            SetFollow(false);
            float left = Single.MaxValue, right = Single.MinValue, top = Single.MaxValue, bottom = Single.MinValue;
            foreach (var p in route) { left = Math.Min(left, p.X); right = Math.Max(right, p.X); top = Math.Min(top, p.Y); bottom = Math.Max(bottom, p.Y); }
            centerX = (left + right) / 2f; centerY = (top + bottom) / 2f;
            zoom = Math.Max(MinZoom, Math.Min(MaxZoom, Math.Min(Math.Max(1, Width - 80) / Math.Max(60f, right - left), Math.Max(1, Height - 80) / Math.Max(60f, bottom - top))));
            viewSet = true;
            fitRoutePending = false;
            Invalidate();
        }

        /// <summary>Shows the game's map waypoint (null clears it) and its GPS route length by road.</summary>
        public void SetDestination(double? x, double? y, double? routeLengthM, bool routeFound)
        {
            PointF? next = x.HasValue && y.HasValue ? (PointF?)calibration.ToImage(x.Value, y.Value) : null;
            string text = !next.HasValue ? null
                : routeFound && routeLengthM.HasValue ? FormatDistance(routeLengthM.Value) + " by road"
                : routeFound ? "GPS route in the game" : "no road route found";
            if (next == destination && text == destinationText) return;
            destination = next;
            destinationText = text;
            Invalidate();
        }

        static string FormatDistance(double metres)
        {
            return metres >= 1000 ? (metres / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km" : Math.Round(metres) + " m";
        }

        /// <summary>Marks where a teleport is going (null clears it).</summary>
        /// <summary>P18: the saved ghost segments to draw (start, finish, name, best time).</summary>
        public void SetSegments(IEnumerable<SegmentMark> marks)
        {
            segments = new List<SegmentMark>(marks);
            Invalidate();
        }

        /// <summary>P18: the saved Discover places to draw.</summary>
        public void SetPlaces(IEnumerable<PlaceMark> marks)
        {
            places = new List<PlaceMark>(marks);
            Invalidate();
        }

        public void SetTarget(double? x, double? y)
        {
            target = x.HasValue && y.HasValue ? (PointF?)calibration.ToImage(x.Value, y.Value) : null;
            Invalidate();
        }

        public void SetFollow(bool follow)
        {
            if (Follow == follow) return;
            Follow = follow;
            if (follow && rider.HasValue) { centerX = rider.Value.X; centerY = rider.Value.Y; if (zoom < 0.5f) zoom = Math.Max(MinZoom, 0.5f); }
            Invalidate();
            if (FollowChanged != null) FollowChanged();
        }

        public void ZoomBy(float factor) { ZoomAt(new Point(Width / 2, Height / 2), factor); }

        public void FitAll()
        {
            if (full == null) return;
            zoom = MinZoom;
            centerX = imageWidth / 2f;
            centerY = imageHeight / 2f;
            viewSet = false;
            fitRoutePending = false;
            Invalidate();
        }

        float MinZoom { get { return imageWidth == 0 ? 0.05f : Math.Min(Math.Max(1, Width) / (float)imageWidth, Math.Max(1, Height) / (float)imageHeight); } }

        PointF ToScreen(PointF image) { return new PointF((image.X - centerX) * zoom + Width / 2f, (image.Y - centerY) * zoom + Height / 2f); }
        PointF ToImage(Point screen) { return new PointF((screen.X - Width / 2f) / zoom + centerX, (screen.Y - Height / 2f) / zoom + centerY); }

        void ZoomAt(Point screen, float factor)
        {
            if (full == null) return;
            PointF anchor = ToImage(screen);
            float next = Math.Max(MinZoom, Math.Min(MaxZoom, zoom * factor));
            if (Follow && rider.HasValue)
            {
                centerX = rider.Value.X; // Keep the rider centred while following.
                centerY = rider.Value.Y;
            }
            else
            {
                centerX = anchor.X - (screen.X - Width / 2f) / next; // The point under the cursor stays put.
                centerY = anchor.Y - (screen.Y - Height / 2f) / next;
            }
            zoom = next;
            viewSet = true;
            Clamp();
            Invalidate();
        }

        void Clamp()
        {
            centerX = Math.Max(0, Math.Min(imageWidth, centerX));
            centerY = Math.Max(0, Math.Min(imageHeight, centerY));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ZoomAt(e.Location, e.Delta > 0 ? 1.25f : 0.8f);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left || full == null) return;
            if (EditPoints && !AddRoutePoints)
            {
                int index = HitRoutePoint(e.Location);
                if (index >= 0)
                {
                    SelectedRoutePoint = index;
                    if (RoutePointSelected != null) RoutePointSelected(index);
                    pointDragIndex = index;
                    pointDragOrigin = route[index];
                    pointDragMouse = e.Location;
                    pointDragMoved = false;
                    fitRoutePending = false;
                    if (IsHandleCreated) Capture = true;
                    Cursor = Cursors.SizeAll;
                    Invalidate();
                    return;
                }
            }
            dragging = true; moved = false; dragStart = lastMouse = e.Location;
            if (IsHandleCreated) Capture = true;
            Cursor = AddRoutePoints ? Cursors.Cross : Cursors.SizeAll;
        }

        // Prefer a nearby marker; otherwise select the closest endpoint of the segment under the cursor.
        int HitRoutePoint(Point screen)
        {
            int nearest = -1;
            double best = 14 * 14;
            for (int i = 0; i < route.Count; i++)
            {
                double distance = DistanceSquared(screen, ToScreen(route[i]));
                if (distance < best) { best = distance; nearest = i; }
            }
            if (nearest >= 0) return nearest;
            best = 10 * 10;
            int edges = routeClosed && route.Count > 1 ? route.Count : route.Count - 1;
            for (int i = 0; i < edges; i++)
            {
                int next = (i + 1) % route.Count;
                PointF a = ToScreen(route[i]), b = ToScreen(route[next]);
                double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
                double t = length == 0 ? 0 : Math.Max(0, Math.Min(1, ((screen.X - a.X) * dx + (screen.Y - a.Y) * dy) / length));
                var foot = new PointF((float)(a.X + t * dx), (float)(a.Y + t * dy));
                double distance = DistanceSquared(screen, foot);
                if (distance < best) { best = distance; nearest = DistanceSquared(screen, a) <= DistanceSquared(screen, b) ? i : next; }
            }
            return nearest;
        }

        static double DistanceSquared(PointF a, PointF b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        void CancelPointDrag()
        {
            if (pointDragIndex < 0) return;
            if (pointDragIndex < route.Count) route[pointDragIndex] = pointDragOrigin;
            pointDragIndex = -1; pointDragMoved = false;
            if (IsHandleCreated) Capture = false;
            Cursor = AddRoutePoints ? Cursors.Cross : Cursors.Hand;
            Invalidate();
        }

        void PreviewPointDrag(Point screen)
        {
            if (!pointDragMoved && Math.Abs(screen.X - pointDragMouse.X) + Math.Abs(screen.Y - pointDragMouse.Y) < 4) return;
            pointDragMoved = true;
            // Relative movement prevents a waypoint from jumping when its segment was clicked farther away.
            route[pointDragIndex] = new PointF(
                Math.Max(0, Math.Min(imageWidth - 1, pointDragOrigin.X + (screen.X - pointDragMouse.X) / zoom)),
                Math.Max(0, Math.Min(imageHeight - 1, pointDragOrigin.Y + (screen.Y - pointDragMouse.Y) / zoom)));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (pointDragIndex >= 0) { PreviewPointDrag(e.Location); return; }
            if (!dragging)
            {
                Cursor = AddRoutePoints ? Cursors.Cross : EditPoints && HitRoutePoint(e.Location) >= 0 ? Cursors.SizeAll : Cursors.Hand;
                return;
            }
            if (!moved && Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) < 4) return;
            if (!moved) { moved = true; SetFollow(false); Cursor = Cursors.SizeAll; }
            fitRoutePending = false;
            centerX -= (e.X - lastMouse.X) / zoom;
            centerY -= (e.Y - lastMouse.Y) / zoom;
            lastMouse = e.Location;
            Clamp();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                if (pointDragIndex >= 0)
                {
                    PreviewPointDrag(e.Location);
                    int index = pointDragIndex;
                    PointF position = route[index];
                    bool commit = pointDragMoved && position != pointDragOrigin;
                    pointDragIndex = -1; pointDragMoved = false;
                    if (IsHandleCreated) Capture = false;
                    Cursor = Cursors.SizeAll;
                    Invalidate();
                    if (commit && RoutePointMoved != null) RoutePointMoved(index, calibration.WorldX(position.X), calibration.WorldY(position.Y));
                    return;
                }
                bool place = dragging && !moved && EditPoints && AddRoutePoints && e.Clicks == 1;
                dragging = false;
                if (IsHandleCreated) Capture = false;
                Cursor = AddRoutePoints ? Cursors.Cross : Cursors.Hand;
                Invalidate();
                if (place)
                {
                    PointF p = ToImage(e.Location);
                    if (p.X >= 0 && p.Y >= 0 && p.X < imageWidth && p.Y < imageHeight && RoutePointAdded != null)
                        RoutePointAdded(calibration.WorldX(p.X), calibration.WorldY(p.Y));
                }
            }
            if (e.Button == MouseButtons.Right && full != null)
            {
                menuImagePoint = ToImage(e.Location);
                copyItem.Text = String.Format(CultureInfo.InvariantCulture, "Copy coordinates  ({0:0}, {1:0})", calibration.WorldX(menuImagePoint.X), calibration.WorldY(menuImagePoint.Y));
                centerItem.Enabled = rider.HasValue;
                centerItem.Visible = !ShowRouteEndpoints;
                bool insideMap = menuImagePoint.X >= 0 && menuImagePoint.Y >= 0 && menuImagePoint.X <= imageWidth && menuImagePoint.Y <= imageHeight;
                movePointItem.Visible = addPointItem.Visible = EditPoints;
                movePointItem.Enabled = insideMap && SelectedRoutePoint >= 0;
                addPointItem.Enabled = insideMap;
                string block = !insideMap ? "outside the map" : TeleportBlock;
                teleportItem.Visible = TeleportVisible;
                teleportSeparator.Visible = TeleportVisible || DestinationVisible;
                teleportItem.Enabled = block == null;
                teleportItem.Text = block == null ? "Teleport here" : "Teleport here (" + block + ")";
                string routeBlock = !insideMap ? "outside the map" : DestinationBlock;
                routeItem.Visible = DestinationVisible;
                routeItem.Enabled = routeBlock == null;
                routeItem.Text = routeBlock == null ? (destination.HasValue ? "Route here instead (GPS)" : "Route here (GPS)") : "Route here (" + routeBlock + ")";
                clearRouteItem.Visible = DestinationVisible && destination.HasValue;
                clearRouteItem.Enabled = DestinationBlock == null;
                // P18 ghost segments: start, then finish (the same point twice is a loop); rename/remove the one under the cursor.
                menuSegment = SegmentAt(e.Location);
                bool segmentsOn = SegmentsVisible && !ShowRouteEndpoints && !EditPoints;
                string segmentBlock = !insideMap ? "outside the map" : SegmentBlock;
                segmentStartItem.Visible = segmentsOn;
                segmentStartItem.Enabled = segmentBlock == null;
                segmentStartItem.Text = segmentBlock != null ? "Ghost segment (" + segmentBlock + ")" : pendingStart.HasValue ? "Ghost segment: move the start here" : "Ghost segment: start here";
                segmentFinishItem.Visible = segmentCancelItem.Visible = segmentsOn && pendingStart.HasValue;
                segmentFinishItem.Enabled = segmentBlock == null;
                bool hit = segmentsOn && menuSegment != null;
                segmentRenameItem.Visible = segmentRemoveItem.Visible = hit;
                segmentRenameItem.Enabled = segmentRemoveItem.Enabled = SegmentBlock == null;
                if (hit) { segmentRenameItem.Text = "Rename “" + menuSegment.Name + "”…"; segmentRemoveItem.Text = "Remove “" + menuSegment.Name + "”…"; }
                // P18 discover places (same block as segments: both need fresh game data).
                bool placesOn = PlacesVisible && !ShowRouteEndpoints && !EditPoints;
                menuPlace = placesOn ? PlaceAt(e.Location) : null;
                placeSaveItem.Visible = placesOn;
                placeSaveItem.Enabled = segmentBlock == null;
                placeSaveItem.Text = segmentBlock == null ? "Discover: save place here" : "Discover: save place (" + segmentBlock + ")";
                placeRenameItem.Visible = placeRemoveItem.Visible = menuPlace != null;
                placeRenameItem.Enabled = placeRemoveItem.Enabled = SegmentBlock == null;
                if (menuPlace != null) { placeRenameItem.Text = "Rename place “" + menuPlace.Name + "”…"; placeRemoveItem.Text = "Remove place “" + menuPlace.Name + "”…"; }
                segmentSeparator.Visible = segmentsOn || placesOn;
                menu.Show(this, e.Location);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || EditPoints) return;
            SetFollow(false);
            ZoomAt(e.Location, 2f);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) { CancelPointDrag(); dragging = false; }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && pointDragIndex >= 0)
            {
                CancelPointDrag(); e.Handled = e.SuppressKeyPress = true; return;
            }
            base.OnKeyDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return ((keyData & Keys.KeyCode) == Keys.Escape && pointDragIndex >= 0) || base.IsInputKey(keyData);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (full != null && zoom < MinZoom) zoom = MinZoom;
            if (fitRoutePending) FitRoute();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Ocean);
            if (full == null)
            {
                Banner(g, loadError ?? (!String.IsNullOrEmpty(Overlay) ? Overlay : "Download the map to see the route preview."));
                return;
            }
            DrawImage(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (route.Count > 1)
            {
                var line = route.ConvertAll(ToScreen);
                if (routeClosed) line.Add(line[0]);
                var points = line.ToArray();
                using (var shadow = new Pen(Color.FromArgb(160, 0, 0, 0), 6f) { LineJoin = LineJoin.Round })
                using (var pen = new Pen(Color.FromArgb(255, 255, 140, 40), 3.5f) { LineJoin = LineJoin.Round })
                {
                    g.DrawLines(shadow, points);
                    g.DrawLines(pen, points);
                }
            }
            if (ShowRouteEndpoints && route.Count > 0)
            {
                if (EditPoints)
                {
                    for (int i = 0; i < route.Count; i++)
                    {
                        if (i == SelectedRoutePoint) continue;
                        Color color = i == 0 ? Color.FromArgb(34, 197, 94) : Ui.Accent;
                        DrawRoutePoint(g, ToScreen(route[i]), i == 0 ? "Start" : i == route.Count - 1 ? (routeClosed ? "Last point" : "Finish") : null, color);
                    }
                }
                else
                {
                    DrawRoutePoint(g, ToScreen(route[0]), "Start", Color.FromArgb(34, 197, 94));
                    if (route.Count > 1) DrawRoutePoint(g, ToScreen(route[route.Count - 1]), "Finish", Ui.Accent);
                }
                if (SelectedRoutePoint >= 0 && SelectedRoutePoint < route.Count)
                {
                    PointF p = ToScreen(route[SelectedRoutePoint]);
                    using (var halo = new SolidBrush(Color.FromArgb(100, Color.DodgerBlue))) g.FillEllipse(halo, p.X - 14, p.Y - 14, 28, 28);
                    DrawRoutePoint(g, p, (SelectedRoutePoint == 0 ? "Start" : "Point " + SelectedRoutePoint) + " · drag to move", Color.DodgerBlue);
                }
            }
            if (!ShowRouteEndpoints) { DrawSegments(g); DrawPlaces(g); }
            if (destination.HasValue) DrawDestination(g, ToScreen(destination.Value), rider.HasValue ? (PointF?)ToScreen(rider.Value) : null);
            if (target.HasValue) DrawTarget(g, ToScreen(target.Value));
            if (rider.HasValue) DrawRider(g, ToScreen(rider.Value));
            DrawScale(g);
            if (destinationText != null) Chip(g, "Destination: " + destinationText);
            if (!String.IsNullOrEmpty(Overlay)) Banner(g, Overlay);
        }

        void DrawImage(Graphics g)
        {
            // Visible part of the image, in image pixels.
            float left = Math.Max(0, centerX - Width / 2f / zoom), top = Math.Max(0, centerY - Height / 2f / zoom);
            float right = Math.Min(imageWidth, centerX + Width / 2f / zoom), bottom = Math.Min(imageHeight, centerY + Height / 2f / zoom);
            if (right <= left || bottom <= top) return;
            Image source = full;
            float scale = fullScale;
            foreach (var level in levels) if (level.Key >= zoom) { source = level.Value; scale = level.Key; } // Smallest copy still sharp enough.
            g.InterpolationMode = dragging ? InterpolationMode.Bilinear : (zoom > 1.5f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear);
            g.PixelOffsetMode = PixelOffsetMode.Half;
            PointF topLeft = ToScreen(new PointF(left, top)), bottomRight = ToScreen(new PointF(right, bottom));
            g.DrawImage(source, RectangleF.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y),
                RectangleF.FromLTRB(left * scale, top * scale, right * scale, bottom * scale), GraphicsUnit.Pixel);
        }

        void DrawRider(Graphics g, PointF p)
        {
            // GTA heading: 0 = north (+Y), counter-clockwise. World direction (-sin, cos) mapped through the image axes.
            double radians = riderHeading * Math.PI / 180;
            double dx = -Math.Sin(radians) * Math.Sign(calibration.ScaleX), dy = Math.Cos(radians) * Math.Sign(calibration.ScaleY);
            float angle = (float)(Math.Atan2(dy, dx) * 180 / Math.PI) + 90; // Arrow drawn pointing up.
            var state = g.Save();
            g.TranslateTransform(p.X, p.Y);
            using (var halo = new SolidBrush(Color.FromArgb(70, 30, 144, 255))) g.FillEllipse(halo, -22, -22, 44, 44);
            g.RotateTransform(angle);
            var arrow = new[] { new PointF(0, -15), new PointF(10, 10), new PointF(0, 4), new PointF(-10, 10) };
            using (var fill = new SolidBrush(Color.FromArgb(255, 30, 144, 255)))
            using (var outline = new Pen(Color.White, 2.5f) { LineJoin = LineJoin.Round })
            {
                g.FillPolygon(fill, arrow);
                g.DrawPolygon(outline, arrow);
            }
            g.Restore(state);
        }

        static void DrawRoutePoint(Graphics g, PointF p, string text, Color color)
        {
            using (var fill = new SolidBrush(color)) g.FillEllipse(fill, p.X - 6, p.Y - 6, 12, 12);
            using (var outline = new Pen(Color.White, 2)) g.DrawEllipse(outline, p.X - 6, p.Y - 6, 12, 12);
            if (text == null) return;
            using (var font = new Font("Segoe UI", 9f, FontStyle.Bold))
            {
                SizeF size = g.MeasureString(text, font);
                using (var back = new SolidBrush(Color.FromArgb(210, 15, 23, 42))) g.FillRectangle(back, p.X + 9, p.Y - 10, size.Width + 8, size.Height + 4);
                g.DrawString(text, font, Brushes.White, p.X + 13, p.Y - 8);
            }
        }

        static readonly Color SegmentStart = Color.FromArgb(255, 59, 130, 246), SegmentFinish = Color.FromArgb(255, 15, 23, 42);

        void DrawSegments(Graphics g)
        {
            foreach (var mark in segments)
            {
                PointF a = ToScreen(calibration.ToImage(mark.StartX, mark.StartY)), b = ToScreen(calibration.ToImage(mark.FinishX, mark.FinishY));
                string label = mark.Name + (mark.BestTimeS.HasValue ? "  " + SegmentClock(mark.BestTimeS.Value) : "");
                if (mark.Loop) { DrawRoutePoint(g, a, "⟳ " + label, SegmentStart); continue; }
                using (var guide = new Pen(Color.FromArgb(190, SegmentStart), 2.5f) { DashStyle = DashStyle.Dot }) g.DrawLine(guide, a, b);
                DrawRoutePoint(g, b, "Finish", SegmentFinish);
                DrawRoutePoint(g, a, "▶ " + label, SegmentStart);
            }
            if (pendingStart.HasValue) DrawRoutePoint(g, ToScreen(pendingStart.Value), "New segment start: right-click the finish", SegmentStart);
        }

        static string SegmentClock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture) + "." + (t.Milliseconds / 100).ToString(CultureInfo.InvariantCulture);
        }

        static readonly Color PlaceColor = Color.FromArgb(255, 245, 158, 11);

        void DrawPlaces(Graphics g)
        {
            foreach (var mark in places)
            {
                PointF p = ToScreen(calibration.ToImage(mark.X, mark.Y));
                var diamond = new[] { new PointF(p.X, p.Y - 8), new PointF(p.X + 8, p.Y), new PointF(p.X, p.Y + 8), new PointF(p.X - 8, p.Y) };
                using (var fill = new SolidBrush(mark.Found > 0 ? Color.FromArgb(255, 34, 197, 94) : PlaceColor)) g.FillPolygon(fill, diamond);
                using (var outline = new Pen(Color.White, 2)) g.DrawPolygon(outline, diamond);
                string text = (mark.Found > 0 ? "✓ " : "") + mark.Name;
                using (var font = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                {
                    SizeF size = g.MeasureString(text, font);
                    using (var back = new SolidBrush(Color.FromArgb(200, 15, 23, 42))) g.FillRectangle(back, p.X + 11, p.Y - 9, size.Width + 6, size.Height + 2);
                    g.DrawString(text, font, Brushes.White, p.X + 14, p.Y - 8);
                }
            }
        }

        PlaceMark PlaceAt(Point screen)
        {
            PlaceMark best = null;
            double bestDistance = 14;
            foreach (var mark in places)
            {
                PointF p = ToScreen(calibration.ToImage(mark.X, mark.Y));
                double d = Math.Sqrt((p.X - screen.X) * (p.X - screen.X) + (p.Y - screen.Y) * (p.Y - screen.Y));
                if (d < bestDistance) { bestDistance = d; best = mark; }
            }
            return best;
        }

        /// <summary>The segment whose start or finish is within 14 px of the screen point.</summary>
        SegmentMark SegmentAt(Point screen)
        {
            SegmentMark best = null;
            double bestDistance = 14;
            foreach (var mark in segments)
            {
                foreach (var end in new[] { calibration.ToImage(mark.StartX, mark.StartY), calibration.ToImage(mark.FinishX, mark.FinishY) })
                {
                    PointF p = ToScreen(end);
                    double d = Math.Sqrt((p.X - screen.X) * (p.X - screen.X) + (p.Y - screen.Y) * (p.Y - screen.Y));
                    if (d < bestDistance) { bestDistance = d; best = mark; }
                }
            }
            return best;
        }

        static readonly Color Waypoint = Color.FromArgb(255, 168, 85, 247); // GTA's waypoint is purple too.

        static void DrawDestination(Graphics g, PointF p, PointF? from)
        {
            if (from.HasValue)
            {
                // Straight guide only: the road route itself is the GPS line the game draws on its minimap.
                using (var guide = new Pen(Color.FromArgb(200, Waypoint), 2f) { DashStyle = DashStyle.Dash })
                    g.DrawLine(guide, from.Value, p);
            }
            // Map pin: circle on a point, white outline.
            var pin = new GraphicsPath();
            pin.AddArc(p.X - 10, p.Y - 34, 20, 20, 150, 240);
            pin.AddLine(p.X + 8.7f, p.Y - 19, p.X, p.Y);
            pin.CloseFigure();
            using (var shadow = new Pen(Color.FromArgb(150, 0, 0, 0), 4.5f)) g.DrawPath(shadow, pin);
            using (var fill = new SolidBrush(Waypoint)) g.FillPath(fill, pin);
            using (var outline = new Pen(Color.White, 2f)) g.DrawPath(outline, pin);
            g.FillEllipse(Brushes.White, p.X - 4, p.Y - 28, 8, 8);
            pin.Dispose();
        }

        void Chip(Graphics g, string text)
        {
            using (var font = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            {
                SizeF size = g.MeasureString(text, font);
                var box = new RectangleF(12, 12, size.Width + 30, size.Height + 10);
                using (var back = new SolidBrush(Color.FromArgb(210, 15, 23, 42))) g.FillRectangle(back, box);
                using (var dot = new SolidBrush(Waypoint)) g.FillEllipse(dot, box.X + 9, box.Y + box.Height / 2 - 5, 10, 10);
                g.DrawString(text, font, Brushes.White, box.X + 24, box.Y + 5);
            }
        }

        static void DrawTarget(Graphics g, PointF p)
        {
            using (var shadow = new Pen(Color.FromArgb(160, 0, 0, 0), 5f))
            using (var pen = new Pen(Color.FromArgb(255, 250, 204, 21), 2.5f))
            {
                foreach (var stroke in new[] { shadow, pen })
                {
                    g.DrawEllipse(stroke, p.X - 11, p.Y - 11, 22, 22);
                    g.DrawLine(stroke, p.X - 17, p.Y, p.X - 5, p.Y); g.DrawLine(stroke, p.X + 5, p.Y, p.X + 17, p.Y);
                    g.DrawLine(stroke, p.X, p.Y - 17, p.X, p.Y - 5); g.DrawLine(stroke, p.X, p.Y + 5, p.X, p.Y + 17);
                }
            }
        }

        void DrawScale(Graphics g)
        {
            // 1 km bar (or 100/500 m when zoomed in), bottom left.
            double pixelsPerMetre = Math.Abs(calibration.ScaleX) * zoom;
            int metres = pixelsPerMetre * 1000 < 60 ? 2000 : pixelsPerMetre * 500 > 200 ? (pixelsPerMetre * 100 > 200 ? 50 : 100) : pixelsPerMetre * 1000 > 200 ? 500 : 1000;
            float length = (float)(pixelsPerMetre * metres);
            float x = 14, y = Height - 18;
            using (var back = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(back, x - 6, y - 20, length + 12, 28);
            using (var pen = new Pen(Color.White, 2f)) { g.DrawLine(pen, x, y, x + length, y); g.DrawLine(pen, x, y - 5, x, y); g.DrawLine(pen, x + length, y - 5, x + length, y); }
            using (var font = new Font("Segoe UI", 8.5f))
                g.DrawString(metres >= 1000 ? (metres / 1000) + " km" : metres + " m", font, Brushes.White, x, y - 19);
        }

        void Banner(Graphics g, string text)
        {
            using (var font = new Font("Segoe UI", 11f, FontStyle.Bold))
            {
                SizeF size = g.MeasureString(text, font, Math.Max(200, Width - 80));
                var box = new RectangleF((Width - size.Width) / 2 - 16, (Height - size.Height) / 2 - 12, size.Width + 32, size.Height + 24);
                using (var back = new SolidBrush(Color.FromArgb(200, 15, 23, 42))) g.FillRectangle(back, box);
                g.DrawString(text, font, Brushes.White, new RectangleF(box.X + 16, box.Y + 12, size.Width + 1, size.Height + 1));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var level in levels) level.Value.Dispose();
                if (full != null) full.Dispose();
                menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
