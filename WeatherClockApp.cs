using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Weather Clock")]
[assembly: AssemblyDescription("Desktop clock with current weather and forecast.")]
[assembly: AssemblyProduct("Weather Clock")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace WeatherClock
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
            }
            catch
            {
                // Older runtimes may not expose TLS 1.2 by name, but the app can still run the UI.
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ClockWeatherForm());
        }
    }

    internal sealed class ClockWeatherForm : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PointNative
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public PointNative Reserved;
            public PointNative MaxSize;
            public PointNative MaxPosition;
            public PointNative MinTrackSize;
            public PointNative MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RectNative
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private sealed class MonitorInfo
        {
            public int Size = Marshal.SizeOf(typeof(MonitorInfo));
            public RectNative Monitor;
            public RectNative Work;
            public int Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, MonitorInfo info);

        private const int WmGetMinMaxInfo = 0x0024;
        private const uint MonitorDefaultToNearest = 0x00000002;

        private static readonly string Degree = ((char)176).ToString();
        private readonly System.Windows.Forms.Timer clockTimer;
        private readonly System.Windows.Forms.Timer weatherTimer;
        private readonly NotifyIcon trayIcon;
        private readonly ImageSet images;
        private readonly object weatherLock = new object();
        private WeatherSnapshot weather = WeatherSnapshot.Loading("Set up weather");
        private UserSettings settings;
        private string timeText = string.Empty;
        private string amPmText = string.Empty;
        private string dateText = string.Empty;
        private bool refreshInProgress;
        private bool exiting;
        private RectangleF minimizeRect;
        private RectangleF maximizeRect;
        private RectangleF closeRect;
        private RectangleF settingsRect;
        private bool minimizeHot;
        private bool maximizeHot;
        private bool closeHot;
        private bool settingsHot;
        private bool dragging;
        private Point dragStartCursor;
        private Point dragStartForm;
        private bool isCustomMaximized;
        private Rectangle restoreBounds;

        public ClockWeatherForm()
        {
            Text = "Weather Clock";
            BackColor = Color.FromArgb(2, 8, 14);
            ForeColor = Color.White;
            ClientSize = InitialClientSize();
            MinimumSize = new Size(760, 430);
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            DoubleBuffered = true;
            ResizeRedraw = true;
            KeyPreview = true;
            Padding = new Padding(1);

            Icon = AppIcon();
            images = new ImageSet();
            CenterInWorkingArea();
            restoreBounds = Bounds;

            trayIcon = new NotifyIcon
            {
                Icon = Icon,
                Text = "Weather Clock",
                Visible = false
            };
            trayIcon.DoubleClick += delegate { RestoreFromTray(); };
            trayIcon.MouseClick += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button == MouseButtons.Left)
                {
                    RestoreFromTray();
                }
            };

            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Show", null, delegate { RestoreFromTray(); });
            trayMenu.Items.Add("Exit", null, delegate
            {
                exiting = true;
                trayIcon.Visible = false;
                Close();
            });
            trayIcon.ContextMenuStrip = trayMenu;

            settings = SettingsStore.Load();

            clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            clockTimer.Tick += delegate { UpdateClockText(); };

            weatherTimer = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };
            weatherTimer.Tick += delegate { RefreshWeather(); };

            UpdateClockText();
            clockTimer.Start();
            weatherTimer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (!settings.IsComplete)
            {
                using (var setup = new SettingsForm(settings, true))
                {
                    if (setup.ShowDialog(this) == DialogResult.OK)
                    {
                        settings = setup.Settings;
                        SettingsStore.Save(settings);
                    }
                }
            }

            if (settings.IsComplete)
            {
                RefreshWeather();
            }
            else
            {
                lock (weatherLock)
                {
                    weather = WeatherSnapshot.Error("Weather setup needed");
                }
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            Rectangle bounds = ClientRectangle;
            DrawBackground(g, bounds);

            WeatherSnapshot snapshot;
            lock (weatherLock)
            {
                snapshot = weather.Clone();
            }

            LayoutRects layout = LayoutRects.From(bounds);
            DrawWindowButtons(g, snapshot);
            DrawTopWeather(g, layout.Top, snapshot);
            DrawClock(g, layout.Clock);
            DrawHourly(g, layout.Hourly, snapshot.Hourly);
            DrawDaily(g, layout.Daily, snapshot.Daily);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            if (WindowState == FormWindowState.Minimized && !exiting)
            {
                MinimizeToTray();
                return;
            }

            UpdateWindowRegion();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            bool wasMinimizeHot = minimizeHot;
            bool wasMaximizeHot = maximizeHot;
            bool wasCloseHot = closeHot;
            bool wasSettingsHot = settingsHot;
            minimizeHot = minimizeRect.Contains(e.Location);
            maximizeHot = maximizeRect.Contains(e.Location);
            closeHot = closeRect.Contains(e.Location);
            settingsHot = settingsRect.Contains(e.Location);
            Cursor = minimizeHot || maximizeHot || closeHot || settingsHot ? Cursors.Hand : Cursors.Default;

            if (dragging)
            {
                Point delta = new Point(Cursor.Position.X - dragStartCursor.X, Cursor.Position.Y - dragStartCursor.Y);
                Location = new Point(dragStartForm.X + delta.X, dragStartForm.Y + delta.Y);
            }

            if (wasMinimizeHot != minimizeHot || wasMaximizeHot != maximizeHot || wasCloseHot != closeHot || wasSettingsHot != settingsHot)
            {
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (isCustomMaximized)
            {
                return;
            }

            if (minimizeRect.Contains(e.Location) || maximizeRect.Contains(e.Location) || closeRect.Contains(e.Location) || settingsRect.Contains(e.Location))
            {
                return;
            }

            dragging = true;
            dragStartCursor = Cursor.Position;
            dragStartForm = Location;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButtons.Left)
            {
                dragging = false;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            minimizeHot = false;
            maximizeHot = false;
            closeHot = false;
            settingsHot = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Point point = PointToClient(Cursor.Position);

            if (closeRect.Contains(point))
            {
                exiting = true;
                Close();
            }
            else if (maximizeRect.Contains(point))
            {
                ToggleMaximize();
            }
            else if (minimizeRect.Contains(point))
            {
                WindowState = FormWindowState.Minimized;
            }
            else if (settingsRect.Contains(point))
            {
                ShowSettings();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Escape)
            {
                WindowState = FormWindowState.Minimized;
            }
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            Point point = PointToClient(Cursor.Position);
            if (point.Y <= Math.Max(44, ClientSize.Height * 0.08f)
                && !minimizeRect.Contains(point)
                && !maximizeRect.Contains(point)
                && !closeRect.Contains(point)
                && !settingsRect.Contains(point))
            {
                ToggleMaximize();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            MaximizedBounds = Screen.FromRectangle(Bounds).WorkingArea;
        }

        protected override void WndProc(ref Message m)
        {
            const int WmNcHitTest = 0x0084;
            const int HtClient = 1;
            const int HtLeft = 10;
            const int HtRight = 11;
            const int HtTop = 12;
            const int HtTopLeft = 13;
            const int HtTopRight = 14;
            const int HtBottom = 15;
            const int HtBottomLeft = 16;
            const int HtBottomRight = 17;

            if (m.Msg == WmGetMinMaxInfo)
            {
                ApplyMonitorMaxBounds(m.HWnd, m.LParam);
            }

            base.WndProc(ref m);

            if (m.Msg != WmNcHitTest || (int)m.Result != HtClient || WindowState != FormWindowState.Normal || isCustomMaximized)
            {
                return;
            }

            long param = m.LParam.ToInt64();
            int x = (short)(param & 0xffff);
            int y = (short)((param >> 16) & 0xffff);
            Point point = PointToClient(new Point(x, y));
            int grip = 8;
            bool left = point.X <= grip;
            bool right = point.X >= ClientSize.Width - grip;
            bool top = point.Y <= grip;
            bool bottom = point.Y >= ClientSize.Height - grip;

            if (left && top) m.Result = (IntPtr)HtTopLeft;
            else if (right && top) m.Result = (IntPtr)HtTopRight;
            else if (left && bottom) m.Result = (IntPtr)HtBottomLeft;
            else if (right && bottom) m.Result = (IntPtr)HtBottomRight;
            else if (left) m.Result = (IntPtr)HtLeft;
            else if (right) m.Result = (IntPtr)HtRight;
            else if (top) m.Result = (IntPtr)HtTop;
            else if (bottom) m.Result = (IntPtr)HtBottom;
        }

        private static void ApplyMonitorMaxBounds(IntPtr handle, IntPtr lParam)
        {
            if (lParam == IntPtr.Zero)
            {
                return;
            }

            IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return;
            }

            var info = new MonitorInfo();
            if (!GetMonitorInfo(monitor, info))
            {
                return;
            }

            MinMaxInfo mmi = (MinMaxInfo)Marshal.PtrToStructure(lParam, typeof(MinMaxInfo));
            RectNative work = info.Work;
            RectNative screen = info.Monitor;

            mmi.MaxPosition.X = work.Left - screen.Left;
            mmi.MaxPosition.Y = work.Top - screen.Top;
            mmi.MaxSize.X = work.Right - work.Left;
            mmi.MaxSize.Y = work.Bottom - work.Top;

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            exiting = true;
            trayIcon.Visible = false;
            trayIcon.Dispose();
            clockTimer.Dispose();
            weatherTimer.Dispose();
            images.Dispose();
            base.OnFormClosing(e);
        }

        private static Icon AppIcon()
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                return icon ?? SystemIcons.Application;
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private static Size InitialClientSize()
        {
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            int maxWidth = Math.Max(760, (int)(workingArea.Width * 0.94f));
            int maxHeight = Math.Max(430, (int)(workingArea.Height * 0.88f));
            int width = Math.Min(1180, maxWidth);
            int height = (int)Math.Round(width * 9d / 16d);
            if (height > maxHeight)
            {
                height = maxHeight;
                width = (int)Math.Round(height * 16d / 9d);
            }

            return new Size(Math.Max(760, width), Math.Max(430, height));
        }

        private void CenterInWorkingArea()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(
                area.Left + Math.Max(0, (area.Width - Width) / 2),
                area.Top + Math.Max(0, (area.Height - Height) / 2));
        }

        private void ToggleMaximize()
        {
            if (!isCustomMaximized)
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    WindowState = FormWindowState.Normal;
                }

                restoreBounds = Bounds;
                Point formCenter = new Point(Left + Width / 2, Top + Height / 2);
                Rectangle work = Screen.FromPoint(formCenter).WorkingArea;
                Bounds = work;
                isCustomMaximized = true;
            }
            else
            {
                Bounds = restoreBounds;
                isCustomMaximized = false;
            }

            UpdateWindowRegion();
            Invalidate();
        }

        private void ShowSettings()
        {
            using (var setup = new SettingsForm(settings, false))
            {
                if (setup.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                settings = setup.Settings;
                SettingsStore.Save(settings);
                RefreshWeather(true);
            }
        }

        private void UpdateClockText()
        {
            DateTime now = DateTime.Now;
            timeText = now.ToString("h:mm", CultureInfo.InvariantCulture);
            amPmText = now.ToString("tt", CultureInfo.InvariantCulture);
            dateText = now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);
            string trayText = timeText + " " + amPmText;
            trayIcon.Text = trayText.Length <= 63 ? trayText : "Weather Clock";
            Invalidate();
        }

        private void RefreshWeather()
        {
            RefreshWeather(false);
        }

        private void RefreshWeather(bool force)
        {
            if (!settings.IsComplete || (refreshInProgress && !force))
            {
                return;
            }

            refreshInProgress = true;
            lock (weatherLock)
            {
                weather = weather.HasData ? weather.WithRefreshing() : WeatherSnapshot.Loading("Loading weather");
                if (string.IsNullOrWhiteSpace(weather.LocationName))
                {
                    weather.LocationName = settings.SelectedCityDisplay;
                }
            }
            Invalidate();

            ThreadPool.QueueUserWorkItem(delegate
            {
                WeatherSnapshot next;
                try
                {
                    next = WeatherService.Fetch(settings);
                }
                catch (Exception ex)
                {
                    next = WeatherSnapshot.Error("Weather unavailable", ex.Message);
                }

                BeginInvoke(new MethodInvoker(delegate
                {
                    lock (weatherLock)
                    {
                        weather = next;
                    }
                    refreshInProgress = false;
                    Invalidate();
                }));
            });
        }

        private void DrawBackground(Graphics g, Rectangle bounds)
        {
            using (var brush = new LinearGradientBrush(bounds, Color.FromArgb(3, 11, 18), Color.FromArgb(0, 19, 35), 90f))
            {
                g.FillRectangle(brush, bounds);
            }

            using (var edgePen = new Pen(Color.FromArgb(135, 160, 172, 185), 1f))
            using (GraphicsPath border = RoundedRect(new RectangleF(0.5f, 0.5f, bounds.Width - 1f, bounds.Height - 1f), 22f))
            {
                g.DrawPath(edgePen, border);
            }

            using (var shade = new LinearGradientBrush(bounds, Color.FromArgb(44, 31, 73, 105), Color.Transparent, 0f))
            {
                g.FillRectangle(shade, bounds);
            }
        }

        private void DrawWindowButtons(Graphics g, WeatherSnapshot snapshot)
        {
            float size = Math.Max(28f, ClientSize.Height * 0.045f);
            float top = Math.Max(10f, ClientSize.Height * 0.022f);
            float right = ClientSize.Width - 18f;
            closeRect = new RectangleF(right - size, top, size, size);
            maximizeRect = new RectangleF(closeRect.Left - size - 10f, top, size, size);
            minimizeRect = new RectangleF(maximizeRect.Left - size - 10f, top, size, size);
            settingsRect = new RectangleF(minimizeRect.Left - size - 18f, top, size, size);

            DrawWindowGlyph(g, minimizeRect, "minimize", minimizeHot);
            DrawWindowGlyph(g, maximizeRect, isCustomMaximized || WindowState == FormWindowState.Maximized ? "restore" : "maximize", maximizeHot);
            DrawWindowGlyph(g, closeRect, "close", closeHot);
            DrawWindowGlyph(g, settingsRect, "settings", settingsHot);

            string city = !string.IsNullOrWhiteSpace(snapshot.LocationName) ? snapshot.LocationName : settings.SelectedCityDisplay;
            if (!string.IsNullOrWhiteSpace(city))
            {
                using (var cityFont = new Font("Segoe UI", Math.Max(13f, size * 0.44f), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var cityBrush = new SolidBrush(Color.FromArgb(185, 211, 221, 232)))
                using (var clipped = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                {
                    float cityRight = settingsRect.Left - 8f;
                    var cityRect = new RectangleF(Math.Max(16f, cityRight - ClientSize.Width * 0.30f), settingsRect.Top, Math.Max(80f, cityRight - Math.Max(16f, cityRight - ClientSize.Width * 0.30f)), settingsRect.Height);
                    g.DrawString(city, cityFont, cityBrush, cityRect, clipped);
                }
            }
        }

        private void DrawWindowGlyph(Graphics g, RectangleF rect, string kind, bool hot)
        {
            using (var brush = new SolidBrush(hot ? Color.FromArgb(34, 255, 255, 255) : Color.Transparent))
            using (GraphicsPath path = RoundedRect(rect, 8f))
            {
                g.FillPath(brush, path);
            }

            using (var pen = new Pen(Color.FromArgb(235, 246, 242, 237), Math.Max(2f, rect.Height * 0.07f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                if (kind == "minimize")
                {
                    float y = rect.Top + rect.Height * 0.62f;
                    g.DrawLine(pen, rect.Left + rect.Width * 0.28f, y, rect.Right - rect.Width * 0.28f, y);
                }
                else if (kind == "close")
                {
                    g.DrawLine(pen, rect.Left + rect.Width * 0.28f, rect.Top + rect.Height * 0.28f, rect.Right - rect.Width * 0.28f, rect.Bottom - rect.Height * 0.28f);
                    g.DrawLine(pen, rect.Right - rect.Width * 0.28f, rect.Top + rect.Height * 0.28f, rect.Left + rect.Width * 0.28f, rect.Bottom - rect.Height * 0.28f);
                }
                else if (kind == "maximize")
                {
                    var box = new RectangleF(rect.Left + rect.Width * 0.30f, rect.Top + rect.Height * 0.30f, rect.Width * 0.40f, rect.Height * 0.40f);
                    g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                }
                else if (kind == "restore")
                {
                    var back = new RectangleF(rect.Left + rect.Width * 0.36f, rect.Top + rect.Height * 0.25f, rect.Width * 0.34f, rect.Height * 0.34f);
                    var front = new RectangleF(rect.Left + rect.Width * 0.27f, rect.Top + rect.Height * 0.38f, rect.Width * 0.34f, rect.Height * 0.34f);
                    g.DrawRectangle(pen, back.X, back.Y, back.Width, back.Height);
                    g.DrawRectangle(pen, front.X, front.Y, front.Width, front.Height);
                }
                else if (kind == "settings")
                {
                    DrawGearGlyph(g, rect, pen.Color);
                }
            }
        }

        private void DrawGearGlyph(Graphics g, RectangleF rect, Color color)
        {
            float cx = rect.Left + rect.Width / 2f;
            float cy = rect.Top + rect.Height / 2f;
            float outer = rect.Width * 0.28f;
            float inner = rect.Width * 0.13f;
            using (var pen = new Pen(color, Math.Max(2f, rect.Width * 0.065f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                for (int i = 0; i < 8; i++)
                {
                    double angle = i * Math.PI / 4d;
                    float x1 = cx + (float)Math.Cos(angle) * outer;
                    float y1 = cy + (float)Math.Sin(angle) * outer;
                    float x2 = cx + (float)Math.Cos(angle) * (outer + rect.Width * 0.10f);
                    float y2 = cy + (float)Math.Sin(angle) * (outer + rect.Width * 0.10f);
                    g.DrawLine(pen, x1, y1, x2, y2);
                }

                g.DrawEllipse(pen, cx - outer, cy - outer, outer * 2f, outer * 2f);
                g.DrawEllipse(pen, cx - inner, cy - inner, inner * 2f, inner * 2f);
            }
        }

        private void DrawTopWeather(Graphics g, RectangleF area, WeatherSnapshot snapshot)
        {
            float clusterWidth = Math.Min(area.Width * 0.38f, 440f);
            float clusterLeft = area.Left + Math.Max(34f, area.Width * 0.045f);
            float iconSize = Math.Min(area.Height * 0.96f, clusterWidth * 0.36f);
            var iconRect = new RectangleF(clusterLeft, area.Top + area.Height * 0.02f, iconSize, iconSize);
            DrawWeatherIcon(g, snapshot.CurrentIconKey, iconRect);

            float tempX = iconRect.Right + Math.Max(20f, clusterWidth * 0.075f);
            float tempSize = area.Height * 0.66f;
            using (var tempFont = new Font("Segoe UI", tempSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var degreeFont = new Font("Segoe UI", area.Height * 0.27f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var mainBrush = new SolidBrush(Color.FromArgb(245, 250, 253, 255)))
            using (var mutedBrush = new SolidBrush(Color.FromArgb(190, 212, 219, 234)))
            {
                string temp = snapshot.CurrentTemperature.HasValue ? snapshot.CurrentTemperature.Value.ToString(CultureInfo.InvariantCulture) : "--";
                SizeF tempMeasure = g.MeasureString(temp, tempFont, PointF.Empty, StringFormat.GenericTypographic);
                SizeF degreeMeasure = g.MeasureString(Degree + "F", degreeFont, PointF.Empty, StringFormat.GenericTypographic);
                float tempY = area.Top + (area.Height - tempMeasure.Height) / 2f - area.Height * 0.02f;
                g.DrawString(temp, tempFont, mainBrush, tempX, tempY, StringFormat.GenericTypographic);
                g.DrawString(Degree + "F", degreeFont, mainBrush, tempX + tempMeasure.Width + 4f, tempY + area.Height * 0.05f, StringFormat.GenericTypographic);

                float conditionY = Math.Min(area.Bottom - area.Height * 0.30f, iconRect.Bottom + area.Height * 0.08f) + 8f;
                using (var conditionFont = new Font("Segoe UI", Math.Max(13f, area.Height * 0.145f), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var feelsFont = new Font("Segoe UI", Math.Max(14f, area.Height * 0.16f), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var clipped = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                {
                    float conditionWidth = Math.Max(iconSize + 52f, clusterWidth * 0.46f);
                    g.DrawString(snapshot.ConditionText, conditionFont, mainBrush, new RectangleF(iconRect.Left - 8f, conditionY, conditionWidth, area.Height * 0.24f), clipped);
                    string feels = snapshot.FeelsLike.HasValue ? "Feels like " + snapshot.FeelsLike.Value.ToString(CultureInfo.InvariantCulture) + Degree : snapshot.StatusText;
                    g.DrawString(feels, feelsFont, mutedBrush, new RectangleF(iconRect.Left - 8f, conditionY + area.Height * 0.22f, conditionWidth, area.Height * 0.24f), clipped);
                }

                float stackLeft = tempX + tempMeasure.Width + degreeMeasure.Width + Math.Max(24f, area.Width * 0.03f);
                float minStackLeft = area.Left + area.Width * 0.24f;
                if (stackLeft < minStackLeft)
                {
                    stackLeft = minStackLeft;
                }
                using (var labelFont = new Font("Segoe UI", Math.Max(13f, area.Height * 0.15f), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var valueFont = new Font("Segoe UI", Math.Max(15f, area.Height * 0.17f), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var labelBrush = new SolidBrush(Color.FromArgb(178, 205, 210, 221)))
                using (var highBrush = new SolidBrush(Color.FromArgb(255, 249, 203, 56)))
                using (var lowBrush = new SolidBrush(Color.FromArgb(255, 25, 190, 246)))
                {
                    DrawInlinePair(g, "High", snapshot.TodayHigh, labelFont, valueFont, labelBrush, highBrush, stackLeft, area.Top + area.Height * 0.22f);
                    DrawInlinePair(g, "Low", snapshot.TodayLow, labelFont, valueFont, labelBrush, lowBrush, stackLeft, area.Top + area.Height * 0.53f);
                }
            }
        }

        private void DrawInlinePair(Graphics g, string label, int? value, Font labelFont, Font valueFont, Brush labelBrush, Brush valueBrush, float x, float y)
        {
            string valueText = value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) + Degree : "--";
            SizeF labelSize = g.MeasureString(label + " ", labelFont, PointF.Empty, StringFormat.GenericTypographic);
            g.DrawString(label, labelFont, labelBrush, x, y, StringFormat.GenericTypographic);
            g.DrawString(valueText, valueFont, valueBrush, x + labelSize.Width + 8f, y - 2f, StringFormat.GenericTypographic);
        }

        private void DrawClock(Graphics g, RectangleF area)
        {
            float timeSize = FitScaledTimeSize(g, area, 1.3f);
            using (var timeFont = new Font("Segoe UI", timeSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var suffixFont = new Font("Segoe UI", timeSize * 0.22f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var dateFont = new Font("Segoe UI", Math.Max(22f, timeSize * 0.14f), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var timeBrush = new LinearGradientBrush(area, Color.White, Color.FromArgb(206, 222, 245), 90f))
            using (var dateBrush = new SolidBrush(Color.FromArgb(214, 181, 194, 230)))
            {
                SizeF timeSizeMeasured = g.MeasureString(timeText, timeFont, PointF.Empty, StringFormat.GenericTypographic);
                SizeF suffixSize = g.MeasureString(amPmText, suffixFont, PointF.Empty, StringFormat.GenericTypographic);
                SizeF dateSize = g.MeasureString(dateText, dateFont, PointF.Empty, StringFormat.GenericTypographic);
                float totalWidth = timeSizeMeasured.Width + suffixSize.Width + 24f;
                float dateGap = Math.Max(14f, timeSize * 0.08f);
                float visibleTimeHeight = Math.Max(timeSizeMeasured.Height, timeSize * 0.92f);
                float x = area.Left + (area.Width - totalWidth) / 2f;
                float dateY = area.Bottom - dateSize.Height - Math.Max(34f, area.Height * 0.12f);
                float y = dateY - dateGap - visibleTimeHeight;
                if (y < area.Top)
                {
                    y = area.Top;
                }

                g.DrawString(timeText, timeFont, timeBrush, x, y, StringFormat.GenericTypographic);
                g.DrawString(amPmText, suffixFont, dateBrush, x + timeSizeMeasured.Width + 18f, y + visibleTimeHeight * 0.58f, StringFormat.GenericTypographic);
                g.DrawString(dateText, dateFont, dateBrush, area.Left + (area.Width - dateSize.Width) / 2f, dateY, StringFormat.GenericTypographic);
            }
        }

        private float FindBestFontSize(Graphics g, RectangleF area, string time, string suffix)
        {
            float low = 42f;
            float high = Math.Min(area.Height * 0.84f, area.Width * 0.31f);
            if (high < low)
            {
                high = low;
            }

            for (int i = 0; i < 18; i++)
            {
                float mid = (low + high) / 2f;
                using (var timeFont = new Font("Segoe UI", mid, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var suffixFont = new Font("Segoe UI", mid * 0.22f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var dateFont = new Font("Segoe UI", Math.Max(22f, mid * 0.14f), FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    SizeF timeSize = g.MeasureString(time, timeFont, PointF.Empty, StringFormat.GenericTypographic);
                    SizeF suffixSize = g.MeasureString(suffix, suffixFont, PointF.Empty, StringFormat.GenericTypographic);
                    SizeF dateSize = g.MeasureString(dateText, dateFont, PointF.Empty, StringFormat.GenericTypographic);
                    float dateGap = Math.Max(14f, mid * 0.08f);
                    float visibleTimeHeight = Math.Max(timeSize.Height, mid * 0.92f);
                    bool fits = timeSize.Width + suffixSize.Width + 28f <= area.Width
                        && visibleTimeHeight + dateSize.Height + dateGap <= area.Height;
                    if (fits)
                    {
                        low = mid;
                    }
                    else
                    {
                        high = mid;
                    }
                }
            }

            return low;
        }

        private float FitScaledTimeSize(Graphics g, RectangleF area, float scale)
        {
            float size = FindBestFontSize(g, area, timeText, amPmText) * scale;
            for (int i = 0; i < 12; i++)
            {
                using (var timeFont = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var suffixFont = new Font("Segoe UI", size * 0.22f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var dateFont = new Font("Segoe UI", Math.Max(22f, size * 0.14f), FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    SizeF timeSizeMeasured = g.MeasureString(timeText, timeFont, PointF.Empty, StringFormat.GenericTypographic);
                    SizeF suffixSize = g.MeasureString(amPmText, suffixFont, PointF.Empty, StringFormat.GenericTypographic);
                    SizeF dateSize = g.MeasureString(dateText, dateFont, PointF.Empty, StringFormat.GenericTypographic);
                    float dateGap = Math.Max(14f, size * 0.08f);
                    float visibleTimeHeight = Math.Max(timeSizeMeasured.Height, size * 0.92f);
                    float neededWidth = timeSizeMeasured.Width + suffixSize.Width + 58f;
                    float neededHeight = visibleTimeHeight + dateGap + dateSize.Height + Math.Max(34f, area.Height * 0.12f);
                    if (neededWidth <= area.Width && neededHeight <= area.Height)
                    {
                        return size;
                    }
                }

                size *= 0.94f;
            }

            return size;
        }

        private void DrawHourly(Graphics g, RectangleF area, IList<HourlyForecast> hourly)
        {
            DrawPanel(g, area);
            int columns = Math.Max(1, hourly.Count > 0 ? hourly.Count : 6);
            float cellWidth = area.Width / columns;

            for (int i = 0; i < columns; i++)
            {
                RectangleF cell = new RectangleF(area.Left + i * cellWidth, area.Top, cellWidth, area.Height);
                if (i > 0)
                {
                    using (var pen = new Pen(Color.FromArgb(72, 128, 146, 164), 1f))
                    {
                        g.DrawLine(pen, cell.Left, cell.Top + 14f, cell.Left, cell.Bottom - 14f);
                    }
                }

                if (i < hourly.Count)
                {
                    DrawHourlyCell(g, cell, hourly[i]);
                }
            }
        }

        private void DrawHourlyCell(Graphics g, RectangleF cell, HourlyForecast item)
        {
            using (var timeFont = new Font("Segoe UI", Math.Max(15f, cell.Height * 0.18f), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var tempFont = new Font("Segoe UI Light", Math.Max(20f, cell.Height * 0.25f), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var white = new SolidBrush(Color.FromArgb(238, 247, 249, 252)))
            {
                string label = item.Start.ToString("h tt", CultureInfo.InvariantCulture);
                SizeF labelSize = g.MeasureString(label, timeFont, PointF.Empty, StringFormat.GenericTypographic);
                g.DrawString(label, timeFont, white, cell.Left + (cell.Width - labelSize.Width) / 2f, cell.Top + cell.Height * 0.12f, StringFormat.GenericTypographic);

                float iconSize = Math.Min(cell.Width * 0.36f, cell.Height * 0.34f);
                DrawWeatherIcon(g, item.IconKey, new RectangleF(cell.Left + (cell.Width - iconSize) / 2f, cell.Top + cell.Height * 0.34f, iconSize, iconSize));

                string temp = item.Temperature.HasValue ? item.Temperature.Value.ToString(CultureInfo.InvariantCulture) + Degree : "--";
                SizeF tempSize = g.MeasureString(temp, tempFont, PointF.Empty, StringFormat.GenericTypographic);
                g.DrawString(temp, tempFont, white, cell.Left + (cell.Width - tempSize.Width) / 2f, cell.Bottom - tempSize.Height - cell.Height * 0.09f, StringFormat.GenericTypographic);
            }
        }

        private void DrawDaily(Graphics g, RectangleF area, IList<DailyForecast> daily)
        {
            DrawPanel(g, area);
            int columns = Math.Max(1, daily.Count > 0 ? daily.Count : 7);
            float cellWidth = area.Width / columns;

            for (int i = 0; i < columns; i++)
            {
                RectangleF cell = new RectangleF(area.Left + i * cellWidth, area.Top, cellWidth, area.Height);
                if (i > 0)
                {
                    using (var pen = new Pen(Color.FromArgb(72, 128, 146, 164), 1f))
                    {
                        g.DrawLine(pen, cell.Left, cell.Top + 14f, cell.Left, cell.Bottom - 14f);
                    }
                }

                if (i < daily.Count)
                {
                    DrawDailyCell(g, cell, daily[i]);
                }
            }
        }

        private void DrawDailyCell(Graphics g, RectangleF cell, DailyForecast item)
        {
            using (var dayFont = new Font("Segoe UI", Math.Max(15f, cell.Height * 0.17f), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var tempFont = new Font("Segoe UI", Math.Max(16f, cell.Height * 0.17f), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var highBrush = new SolidBrush(Color.FromArgb(242, 249, 249, 249)))
            using (var lowBrush = new SolidBrush(Color.FromArgb(255, 24, 190, 246)))
            using (var slashBrush = new SolidBrush(Color.FromArgb(155, 185, 198, 214)))
            {
                string day = item.Date.ToString("ddd", CultureInfo.InvariantCulture).ToUpperInvariant();
                SizeF daySize = g.MeasureString(day, dayFont, PointF.Empty, StringFormat.GenericTypographic);
                g.DrawString(day, dayFont, highBrush, cell.Left + (cell.Width - daySize.Width) / 2f, cell.Top + cell.Height * 0.10f, StringFormat.GenericTypographic);

                float iconSize = Math.Min(cell.Width * 0.44f, cell.Height * 0.38f);
                DrawWeatherIcon(g, item.IconKey, new RectangleF(cell.Left + (cell.Width - iconSize) / 2f, cell.Top + cell.Height * 0.32f, iconSize, iconSize));

                string high = item.High.HasValue ? item.High.Value.ToString(CultureInfo.InvariantCulture) + Degree : "--";
                string low = item.Low.HasValue ? item.Low.Value.ToString(CultureInfo.InvariantCulture) + Degree : "--";
                string slash = "/";
                SizeF highSize = g.MeasureString(high, tempFont, PointF.Empty, StringFormat.GenericTypographic);
                SizeF slashSize = g.MeasureString(slash, tempFont, PointF.Empty, StringFormat.GenericTypographic);
                SizeF lowSize = g.MeasureString(low, tempFont, PointF.Empty, StringFormat.GenericTypographic);
                float totalWidth = highSize.Width + slashSize.Width + lowSize.Width + 8f;
                float x = cell.Left + (cell.Width - totalWidth) / 2f;
                float y = cell.Top + cell.Height * 0.70f;
                g.DrawString(high, tempFont, highBrush, x, y, StringFormat.GenericTypographic);
                g.DrawString(slash, tempFont, slashBrush, x + highSize.Width + 4f, y, StringFormat.GenericTypographic);
                g.DrawString(low, tempFont, lowBrush, x + highSize.Width + slashSize.Width + 8f, y, StringFormat.GenericTypographic);
            }
        }

        private void DrawPanel(Graphics g, RectangleF area)
        {
            using (GraphicsPath path = RoundedRect(area, 15f))
            using (var fill = new LinearGradientBrush(area, Color.FromArgb(70, 18, 36, 54), Color.FromArgb(46, 9, 23, 35), 90f))
            using (var outline = new Pen(Color.FromArgb(96, 124, 146, 168), 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(outline, path);
            }
        }

        private void DrawWeatherIcon(Graphics g, string key, RectangleF rect)
        {
            Image image = images.Get(key);
            if (image != null)
            {
                g.DrawImage(image, rect);
                return;
            }

            DrawFallbackWeatherIcon(g, key, rect);
        }

        private void DrawFallbackWeatherIcon(Graphics g, string key, RectangleF rect)
        {
            bool sun = key == "sunny" || key == "partly_cloudy";
            bool rain = key == "rain" || key == "storm";
            if (sun)
            {
                using (var brush = new SolidBrush(Color.FromArgb(255, 249, 202, 54)))
                {
                    g.FillEllipse(brush, rect.Left + rect.Width * 0.23f, rect.Top + rect.Height * 0.06f, rect.Width * 0.40f, rect.Height * 0.40f);
                }
            }

            using (var cloud = new SolidBrush(Color.FromArgb(245, 242, 248, 255)))
            {
                g.FillEllipse(cloud, rect.Left + rect.Width * 0.18f, rect.Top + rect.Height * 0.46f, rect.Width * 0.32f, rect.Height * 0.28f);
                g.FillEllipse(cloud, rect.Left + rect.Width * 0.38f, rect.Top + rect.Height * 0.36f, rect.Width * 0.36f, rect.Height * 0.36f);
                g.FillRectangle(cloud, rect.Left + rect.Width * 0.25f, rect.Top + rect.Height * 0.54f, rect.Width * 0.56f, rect.Height * 0.25f);
            }

            if (rain)
            {
                using (var pen = new Pen(Color.FromArgb(255, 22, 190, 246), Math.Max(2f, rect.Width * 0.045f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    for (int i = 0; i < 3; i++)
                    {
                        float x = rect.Left + rect.Width * (0.35f + i * 0.15f);
                        g.DrawLine(pen, x, rect.Top + rect.Height * 0.82f, x - rect.Width * 0.05f, rect.Bottom - rect.Height * 0.03f);
                    }
                }
            }
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            float diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void UpdateWindowRegion()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            if (isCustomMaximized || WindowState == FormWindowState.Maximized)
            {
                using (var path = new GraphicsPath())
                {
                    path.AddRectangle(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height));
                    Region = new Region(path);
                }
                return;
            }

            using (GraphicsPath path = RoundedRect(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), 24f))
            {
                Region = new Region(path);
            }
        }

        private void MinimizeToTray()
        {
            trayIcon.Visible = true;
            ShowInTaskbar = false;
            Hide();
        }

        private void RestoreFromTray()
        {
            if (IsDisposed)
            {
                return;
            }

            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            trayIcon.Visible = false;
            Activate();
            Invalidate();
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly TextBox emailText;
        private readonly ListBox cityList;
        private readonly TextBox zipText;
        private readonly Label errorLabel;
        private readonly bool requireSelection;

        public UserSettings Settings { get; private set; }

        public SettingsForm(UserSettings existing, bool requireSelection)
        {
            Text = "Settings";
            ClientSize = new Size(520, 420);
            MinimumSize = new Size(520, 420);
            MaximumSize = new Size(520, 420);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(8, 16, 24);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            this.requireSelection = requireSelection;
            Settings = existing == null ? new UserSettings() : existing.Clone();

            var emailLabel = new Label
            {
                Text = "Email",
                AutoSize = true,
                Left = 34,
                Top = 26,
                ForeColor = Color.FromArgb(220, 235, 242, 248)
            };
            emailText = new TextBox
            {
                Left = 34,
                Top = 52,
                Width = 452,
                Text = Settings.Email
            };

            var cityLabel = new Label
            {
                Text = "Saved cities",
                AutoSize = true,
                Left = 34,
                Top = 94,
                ForeColor = Color.FromArgb(220, 235, 242, 248)
            };
            cityList = new ListBox
            {
                Left = 34,
                Top = 120,
                Width = 452,
                Height = 130,
                BackColor = Color.FromArgb(14, 28, 41),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            RefreshCityList();

            var zipLabel = new Label
            {
                Text = "Add zip code",
                AutoSize = true,
                Left = 34,
                Top = 270,
                ForeColor = Color.FromArgb(220, 235, 242, 248)
            };
            zipText = new TextBox
            {
                Left = 34,
                Top = 296,
                Width = 130,
                MaxLength = 5
            };
            var addButton = new Button
            {
                Text = "Add",
                Left = 174,
                Top = 294,
                Width = 72,
                Height = 31
            };
            addButton.Click += delegate { AddCity(); };

            var removeButton = new Button
            {
                Text = "Remove",
                Left = 254,
                Top = 294,
                Width = 88,
                Height = 31
            };
            removeButton.Click += delegate { RemoveSelectedCity(); };

            var selectButton = new Button
            {
                Text = "Use Selected",
                Left = 352,
                Top = 294,
                Width = 134,
                Height = 31
            };
            selectButton.Click += delegate { SelectCurrentCity(); };

            errorLabel = new Label
            {
                AutoSize = false,
                Left = 34,
                Top = 338,
                Width = 452,
                Height = 32,
                ForeColor = Color.FromArgb(255, 248, 170, 120)
            };

            var startButton = new Button
            {
                Text = "Save",
                Left = 326,
                Top = 374,
                Width = 76,
                Height = 32,
                DialogResult = DialogResult.None
            };
            startButton.Click += delegate { TryAccept(); };

            var cancelButton = new Button
            {
                Text = "Cancel",
                Left = 410,
                Top = 374,
                Width = 76,
                Height = 32,
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(emailLabel);
            Controls.Add(emailText);
            Controls.Add(cityLabel);
            Controls.Add(cityList);
            Controls.Add(zipLabel);
            Controls.Add(zipText);
            Controls.Add(addButton);
            Controls.Add(removeButton);
            Controls.Add(selectButton);
            Controls.Add(errorLabel);
            Controls.Add(startButton);
            Controls.Add(cancelButton);
            AcceptButton = startButton;
            CancelButton = cancelButton;
        }

        private void RefreshCityList()
        {
            cityList.Items.Clear();
            foreach (SavedCity city in Settings.Cities)
            {
                cityList.Items.Add(city);
                if (city.Zip == Settings.SelectedZip)
                {
                    cityList.SelectedItem = city;
                }
            }

            if (cityList.SelectedIndex < 0 && cityList.Items.Count > 0)
            {
                cityList.SelectedIndex = 0;
            }
        }

        private void AddCity()
        {
            string zip = zipText.Text.Trim();
            if (!Regex.IsMatch(zip, @"^\d{5}$"))
            {
                errorLabel.Text = "Enter a 5-digit U.S. zip code.";
                return;
            }

            SavedCity city = SettingsStore.CityFromZip(zip);
            if (city == null)
            {
                errorLabel.Text = "That zip code was not found.";
                return;
            }

            SavedCity existing = Settings.Cities.FirstOrDefault(item => item.Zip == city.Zip);
            if (existing == null)
            {
                Settings.Cities.Add(city);
                existing = city;
            }

            Settings.SelectedZip = existing.Zip;
            zipText.Text = string.Empty;
            errorLabel.Text = string.Empty;
            RefreshCityList();
        }

        private void RemoveSelectedCity()
        {
            SavedCity city = cityList.SelectedItem as SavedCity;
            if (city == null)
            {
                return;
            }

            Settings.Cities.RemoveAll(item => item.Zip == city.Zip);
            Settings.SelectedZip = Settings.Cities.Count > 0 ? Settings.Cities[0].Zip : string.Empty;
            RefreshCityList();
        }

        private void SelectCurrentCity()
        {
            SavedCity city = cityList.SelectedItem as SavedCity;
            if (city != null)
            {
                Settings.SelectedZip = city.Zip;
                errorLabel.Text = string.Empty;
            }
        }

        private void TryAccept()
        {
            string email = emailText.Text.Trim();

            if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                errorLabel.Text = "Enter a valid email for NOAA weather requests.";
                return;
            }

            SelectCurrentCity();

            if (requireSelection && Settings.Cities.Count == 0 && Regex.IsMatch(zipText.Text.Trim(), @"^\d{5}$"))
            {
                AddCity();
            }

            if (Settings.SelectedCity == null)
            {
                errorLabel.Text = "Add and select at least one city.";
                return;
            }

            Settings.Email = email;
            if (string.IsNullOrWhiteSpace(Settings.SelectedZip))
            {
                Settings.SelectedZip = Settings.SelectedCity.Zip;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }

    internal sealed class ImageSet : IDisposable
    {
        private readonly Dictionary<string, Image> images = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        public ImageSet()
        {
            Load("sunny", "ic_weather_sunny.png");
            Load("partly_cloudy", "ic_weather_partly_cloudy.png");
            Load("cloudy", "ic_weather_cloudy.png");
            Load("rain", "ic_weather_rain.png");
            Load("snow", "ic_weather_snow.png");
            Load("storm", "ic_weather_storm.png");
            Load("fog", "ic_weather_fog.png");
            Load("wind", "ic_weather_wind.png");
        }

        public Image Get(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                key = "partly_cloudy";
            }

            Image image;
            return images.TryGetValue(key, out image) ? image : null;
        }

        public void Dispose()
        {
            foreach (Image image in images.Values)
            {
                image.Dispose();
            }
            images.Clear();
        }

        private void Load(string key, string resourceName)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return;
            }

            using (stream)
            {
                images[key] = Image.FromStream(stream);
            }
        }
    }

    internal static class WeatherService
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static WeatherSnapshot Fetch(UserSettings settings)
        {
            SavedCity selectedCity = settings.SelectedCity;
            ZipRecord zip = selectedCity == null ? null : ZipLookup.Find(selectedCity.Zip);
            if (zip == null)
            {
                return WeatherSnapshot.Error("Zip code not found");
            }

            string lat = zip.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
            string lon = zip.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
            Dictionary<string, object> point = GetJson("https://api.weather.gov/points/" + lat + "," + lon, settings.Email);
            Dictionary<string, object> pointProps = AsDictionary(Get(point, "properties"));
            string forecastUrl = GetString(pointProps, "forecast");
            string hourlyUrl = GetString(pointProps, "forecastHourly");
            string stationsUrl = GetString(pointProps, "observationStations");
            string displayName = !string.IsNullOrWhiteSpace(selectedCity.DisplayName) ? selectedCity.DisplayName : ReadDisplayName(pointProps, zip);

            List<ForecastPeriod> dailyPeriods = ReadForecastPeriods(forecastUrl, settings.Email);
            List<ForecastPeriod> hourlyPeriods = ReadForecastPeriods(hourlyUrl, settings.Email);
            CurrentConditions current = ReadCurrentConditions(stationsUrl, settings.Email);

            if (current == null)
            {
                current = new CurrentConditions();
            }

            ForecastPeriod firstHourly = hourlyPeriods.Count > 0 ? hourlyPeriods[0] : null;
            ForecastPeriod firstDaily = dailyPeriods.Count > 0 ? dailyPeriods[0] : null;
            int? currentTemp = current.TemperatureF;
            if (!currentTemp.HasValue && firstHourly != null)
            {
                currentTemp = firstHourly.Temperature;
            }

            string condition = FirstNonBlank(current.ConditionText, firstHourly == null ? null : firstHourly.ShortForecast, firstDaily == null ? null : firstDaily.ShortForecast, "Weather");
            List<DailyForecast> daily = BuildDailyForecast(dailyPeriods);
            List<HourlyForecast> hourly = hourlyPeriods.Take(6).Select(HourlyForecast.FromPeriod).ToList();

            int? high = daily.Count > 0 ? daily[0].High : null;
            int? low = daily.Count > 0 ? daily[0].Low : null;
            if (!high.HasValue)
            {
                DailyForecast nextHigh = daily.FirstOrDefault(d => d.High.HasValue);
                high = nextHigh == null ? null : nextHigh.High;
            }
            if (!low.HasValue)
            {
                DailyForecast nextLow = daily.FirstOrDefault(d => d.Low.HasValue);
                low = nextLow == null ? null : nextLow.Low;
            }

            return new WeatherSnapshot
            {
                HasData = true,
                IsRefreshing = false,
                LocationName = displayName,
                ConditionText = TitleCaseCondition(condition),
                StatusText = "Updated " + DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture),
                CurrentTemperature = currentTemp,
                FeelsLike = current.FeelsLikeF.HasValue ? current.FeelsLikeF : currentTemp,
                TodayHigh = high,
                TodayLow = low,
                CurrentIconKey = IconMapper.FromCondition(condition),
                Hourly = hourly,
                Daily = daily.Take(7).ToList()
            };
        }

        private static CurrentConditions ReadCurrentConditions(string stationsUrl, string email)
        {
            if (string.IsNullOrEmpty(stationsUrl))
            {
                return null;
            }

            try
            {
                Dictionary<string, object> stations = GetJson(stationsUrl, email);
                ArrayList features = AsArray(Get(stations, "features"));
                if (features == null || features.Count == 0)
                {
                    return null;
                }

                Dictionary<string, object> first = AsDictionary(features[0]);
                Dictionary<string, object> props = AsDictionary(Get(first, "properties"));
                string stationId = GetString(props, "stationIdentifier");
                if (string.IsNullOrEmpty(stationId))
                {
                    return null;
                }

                Dictionary<string, object> observation = GetJson("https://api.weather.gov/stations/" + stationId + "/observations/latest", email);
                Dictionary<string, object> obsProps = AsDictionary(Get(observation, "properties"));
                if (obsProps == null)
                {
                    return null;
                }

                int? temp = ReadTemperature(Get(obsProps, "temperature"));
                int? heat = ReadTemperature(Get(obsProps, "heatIndex"));
                int? chill = ReadTemperature(Get(obsProps, "windChill"));
                return new CurrentConditions
                {
                    TemperatureF = temp,
                    FeelsLikeF = heat.HasValue ? heat : (chill.HasValue ? chill : temp),
                    ConditionText = GetString(obsProps, "textDescription")
                };
            }
            catch
            {
                return null;
            }
        }

        private static List<ForecastPeriod> ReadForecastPeriods(string url, string email)
        {
            var list = new List<ForecastPeriod>();
            if (string.IsNullOrEmpty(url))
            {
                return list;
            }

            Dictionary<string, object> response = GetJson(url, email);
            Dictionary<string, object> props = AsDictionary(Get(response, "properties"));
            ArrayList periods = props == null ? null : AsArray(Get(props, "periods"));
            if (periods == null)
            {
                return list;
            }

            foreach (object item in periods)
            {
                Dictionary<string, object> period = AsDictionary(item);
                if (period == null)
                {
                    continue;
                }

                DateTime start;
                DateTime end;
                if (!DateTime.TryParse(GetString(period, "startTime"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out start))
                {
                    start = DateTime.Now;
                }
                if (!DateTime.TryParse(GetString(period, "endTime"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out end))
                {
                    end = start.AddHours(1);
                }

                list.Add(new ForecastPeriod
                {
                    Name = GetString(period, "name"),
                    Start = start.ToLocalTime(),
                    End = end.ToLocalTime(),
                    IsDaytime = GetBool(period, "isDaytime"),
                    Temperature = GetInt(period, "temperature"),
                    TemperatureUnit = GetString(period, "temperatureUnit"),
                    ShortForecast = GetString(period, "shortForecast")
                });
            }

            return list;
        }

        private static List<DailyForecast> BuildDailyForecast(List<ForecastPeriod> periods)
        {
            var days = new List<DailyForecast>();
            foreach (ForecastPeriod period in periods)
            {
                DateTime date = period.Start.Date;
                DailyForecast existing = days.FirstOrDefault(d => d.Date == date);
                if (existing == null)
                {
                    existing = new DailyForecast { Date = date, IconKey = IconMapper.FromCondition(period.ShortForecast) };
                    days.Add(existing);
                }

                if (period.IsDaytime)
                {
                    existing.High = period.Temperature;
                    existing.IconKey = IconMapper.FromCondition(period.ShortForecast);
                }
                else
                {
                    existing.Low = period.Temperature;
                    if (string.IsNullOrEmpty(existing.IconKey))
                    {
                        existing.IconKey = IconMapper.FromCondition(period.ShortForecast);
                    }
                }
            }

            return days.OrderBy(d => d.Date).Take(8).ToList();
        }

        private static int? ReadTemperature(object value)
        {
            Dictionary<string, object> quantitative = AsDictionary(value);
            if (quantitative == null)
            {
                return null;
            }

            double? raw = GetDouble(quantitative, "value");
            if (!raw.HasValue)
            {
                return null;
            }

            string unit = GetString(quantitative, "unitCode");
            double result = raw.Value;
            if (!string.IsNullOrEmpty(unit) && unit.IndexOf("degC", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result = result * 9d / 5d + 32d;
            }

            return (int)Math.Round(result);
        }

        private static string ReadDisplayName(Dictionary<string, object> pointProps, ZipRecord zip)
        {
            Dictionary<string, object> relative = AsDictionary(Get(pointProps, "relativeLocation"));
            Dictionary<string, object> props = relative == null ? null : AsDictionary(Get(relative, "properties"));
            string city = props == null ? null : GetString(props, "city");
            string state = props == null ? null : GetString(props, "state");
            if (!string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(state))
            {
                return city + ", " + state;
            }

            return zip.City + ", " + zip.State;
        }

        private static Dictionary<string, object> GetJson(string url, string email)
        {
            IOException lastError = null;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "WeatherClock/1.0 (" + email + ")";
                    client.Headers[HttpRequestHeader.Accept] = "application/geo+json";
                    try
                    {
                        string json = client.DownloadString(url);
                        return AsDictionary(Serializer.DeserializeObject(json));
                    }
                    catch (WebException ex)
                    {
                        lastError = new IOException(FriendlyWebError(ex), ex);
                        if (!ShouldRetry(ex) || attempt == 4)
                        {
                            throw lastError;
                        }
                    }
                }

                Thread.Sleep(350 * attempt * attempt);
            }

            throw lastError ?? new IOException("NWS request failed.");
        }

        private static bool ShouldRetry(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (response == null)
            {
                return ex.Status == WebExceptionStatus.Timeout
                    || ex.Status == WebExceptionStatus.ConnectFailure
                    || ex.Status == WebExceptionStatus.NameResolutionFailure
                    || ex.Status == WebExceptionStatus.ReceiveFailure
                    || ex.Status == WebExceptionStatus.SendFailure;
            }

            int code = (int)response.StatusCode;
            return code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
        }

        private static string FriendlyWebError(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (response == null)
            {
                return ex.Message;
            }

            string detail = string.Empty;
            try
            {
                using (var stream = response.GetResponseStream())
                using (var reader = stream == null ? null : new StreamReader(stream))
                {
                    detail = reader == null ? string.Empty : reader.ReadToEnd();
                }
            }
            catch
            {
                detail = string.Empty;
            }

            detail = Regex.Replace(detail ?? string.Empty, "\\s+", " ").Trim();
            if (detail.Length > 140)
            {
                detail = detail.Substring(0, 140) + "...";
            }

            return "HTTP " + (int)response.StatusCode + " " + response.StatusDescription
                + (string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail);
        }

        private static object Get(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary == null)
            {
                return null;
            }

            object value;
            return dictionary.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            return value as Dictionary<string, object>;
        }

        private static ArrayList AsArray(object value)
        {
            if (value == null)
            {
                return null;
            }

            var arrayList = value as ArrayList;
            if (arrayList != null)
            {
                return arrayList;
            }

            var objectArray = value as object[];
            if (objectArray != null)
            {
                return new ArrayList(objectArray);
            }

            return null;
        }

        private static string GetString(Dictionary<string, object> dictionary, string key)
        {
            object value = Get(dictionary, key);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int? GetInt(Dictionary<string, object> dictionary, string key)
        {
            object value = Get(dictionary, key);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static double? GetDouble(Dictionary<string, object> dictionary, string key)
        {
            object value = Get(dictionary, key);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static bool GetBool(Dictionary<string, object> dictionary, string key)
        {
            object value = Get(dictionary, key);
            if (value == null)
            {
                return false;
            }

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }
        }

        private static string FirstNonBlank(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static string TitleCaseCondition(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition))
            {
                return "Weather";
            }

            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(condition.ToLowerInvariant());
        }
    }

    internal static class ZipLookup
    {
        public static ZipRecord Find(string zip)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("us_zipcodes.tsv");
            if (stream == null)
            {
                return null;
            }

            using (stream)
            using (var reader = new StreamReader(stream))
            {
                string line = reader.ReadLine();
                while ((line = reader.ReadLine()) != null)
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 5 || parts[0] != zip)
                    {
                        continue;
                    }

                    double lat;
                    double lon;
                    if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
                        || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out lon))
                    {
                        return null;
                    }

                    return new ZipRecord
                    {
                        Zip = parts[0],
                        City = parts[1],
                        State = parts[2],
                        Latitude = lat,
                        Longitude = lon
                    };
                }
            }

            return null;
        }
    }

    internal static class SettingsStore
    {
        private static string SettingsPath
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(root, "WeatherClock", "settings.txt");
            }
        }

        public static UserSettings Load()
        {
            var settings = new UserSettings();
            string path = SettingsPath;
            if (!File.Exists(path))
            {
                return settings;
            }

            string legacyZip = string.Empty;
            foreach (string line in File.ReadAllLines(path))
            {
                int index = line.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, index);
                string value = line.Substring(index + 1);
                if (key == "email")
                {
                    settings.Email = value;
                }
                else if (key == "selected")
                {
                    settings.SelectedZip = value;
                }
                else if (key == "zip")
                {
                    legacyZip = value;
                }
                else if (key == "city")
                {
                    SavedCity city = ParseCity(value);
                    if (city != null && !settings.Cities.Any(existing => existing.Zip == city.Zip))
                    {
                        settings.Cities.Add(city);
                    }
                }
            }

            if (settings.Cities.Count == 0 && Regex.IsMatch(legacyZip ?? string.Empty, @"^\d{5}$"))
            {
                SavedCity legacyCity = CityFromZip(legacyZip);
                if (legacyCity != null)
                {
                    settings.Cities.Add(legacyCity);
                    settings.SelectedZip = legacyZip;
                }
            }

            if (string.IsNullOrWhiteSpace(settings.SelectedZip) && settings.Cities.Count > 0)
            {
                settings.SelectedZip = settings.Cities[0].Zip;
            }

            return settings;
        }

        public static void Save(UserSettings settings)
        {
            string path = SettingsPath;
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var lines = new List<string>
            {
                "email=" + settings.Email,
                "selected=" + settings.SelectedZip
            };

            foreach (SavedCity city in settings.Cities ?? new List<SavedCity>())
            {
                lines.Add("city=" + city.Zip + "|" + city.DisplayName.Replace("|", " "));
            }

            File.WriteAllLines(path, lines.ToArray());
        }

        private static SavedCity ParseCity(string value)
        {
            string[] parts = (value ?? string.Empty).Split(new[] { '|' }, 2);
            if (parts.Length == 0 || !Regex.IsMatch(parts[0], @"^\d{5}$"))
            {
                return null;
            }

            return new SavedCity
            {
                Zip = parts[0],
                DisplayName = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : parts[0]
            };
        }

        public static SavedCity CityFromZip(string zip)
        {
            ZipRecord record = ZipLookup.Find(zip);
            if (record == null)
            {
                return null;
            }

            return new SavedCity
            {
                Zip = record.Zip,
                DisplayName = record.City + ", " + record.State
            };
        }
    }

    internal static class IconMapper
    {
        public static string FromCondition(string condition)
        {
            string text = (condition ?? string.Empty).ToLowerInvariant();

            if (text.Contains("thunder") || text.Contains("storm"))
            {
                return "storm";
            }
            if (text.Contains("snow") || text.Contains("sleet") || text.Contains("ice") || text.Contains("freezing"))
            {
                return "snow";
            }
            if (text.Contains("rain") || text.Contains("drizzle") || text.Contains("shower"))
            {
                return "rain";
            }
            if (text.Contains("fog") || text.Contains("haze") || text.Contains("smoke"))
            {
                return "fog";
            }
            if (text.Contains("wind") || text.Contains("breezy"))
            {
                return "wind";
            }
            if (text.Contains("partly") || text.Contains("mostly") || text.Contains("few clouds"))
            {
                return "partly_cloudy";
            }
            if (text.Contains("cloud") || text.Contains("overcast"))
            {
                return "cloudy";
            }
            if (text.Contains("sun") || text.Contains("clear") || text.Contains("fair"))
            {
                return "sunny";
            }

            return "partly_cloudy";
        }
    }

    internal sealed class WeatherSnapshot
    {
        public bool HasData;
        public bool IsRefreshing;
        public string LocationName = string.Empty;
        public string ConditionText = "Loading";
        public string StatusText = string.Empty;
        public int? CurrentTemperature;
        public int? FeelsLike;
        public int? TodayHigh;
        public int? TodayLow;
        public string CurrentIconKey = "partly_cloudy";
        public List<HourlyForecast> Hourly = new List<HourlyForecast>();
        public List<DailyForecast> Daily = new List<DailyForecast>();

        public static WeatherSnapshot Loading(string message)
        {
            return new WeatherSnapshot
            {
                HasData = false,
                ConditionText = message,
                StatusText = "Waiting for forecast",
                CurrentIconKey = "partly_cloudy",
                Hourly = HourlyForecast.Placeholders(),
                Daily = DailyForecast.Placeholders()
            };
        }

        public static WeatherSnapshot Error(string message)
        {
            return Error(message, string.Empty);
        }

        public static WeatherSnapshot Error(string message, string detail)
        {
            return new WeatherSnapshot
            {
                HasData = false,
                ConditionText = message,
                StatusText = string.IsNullOrWhiteSpace(detail) ? "Check setup or connection" : CleanError(detail),
                CurrentIconKey = "cloudy",
                Hourly = HourlyForecast.Placeholders(),
                Daily = DailyForecast.Placeholders()
            };
        }

        public WeatherSnapshot WithRefreshing()
        {
            WeatherSnapshot clone = Clone();
            clone.IsRefreshing = true;
            clone.StatusText = "Refreshing";
            return clone;
        }

        public WeatherSnapshot Clone()
        {
            return new WeatherSnapshot
            {
                HasData = HasData,
                IsRefreshing = IsRefreshing,
                LocationName = LocationName,
                ConditionText = ConditionText,
                StatusText = StatusText,
                CurrentTemperature = CurrentTemperature,
                FeelsLike = FeelsLike,
                TodayHigh = TodayHigh,
                TodayLow = TodayLow,
                CurrentIconKey = CurrentIconKey,
                Hourly = new List<HourlyForecast>(Hourly),
                Daily = new List<DailyForecast>(Daily)
            };
        }

        private static string CleanError(string detail)
        {
            if (detail.Length <= 42)
            {
                return detail;
            }

            return detail.Substring(0, 42) + "...";
        }
    }

    internal sealed class UserSettings
    {
        public string Email = string.Empty;
        public string SelectedZip = string.Empty;
        public List<SavedCity> Cities = new List<SavedCity>();

        public bool IsComplete
        {
            get
            {
                return Regex.IsMatch(Email ?? string.Empty, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")
                    && SelectedCity != null;
            }
        }

        public SavedCity SelectedCity
        {
            get
            {
                if (Cities == null)
                {
                    Cities = new List<SavedCity>();
                }

                SavedCity selected = Cities.FirstOrDefault(city => city.Zip == SelectedZip);
                return selected ?? Cities.FirstOrDefault();
            }
        }

        public string SelectedCityDisplay
        {
            get
            {
                SavedCity city = SelectedCity;
                return city == null ? string.Empty : city.DisplayName;
            }
        }

        public UserSettings Clone()
        {
            var clone = new UserSettings
            {
                Email = Email,
                SelectedZip = SelectedZip,
                Cities = new List<SavedCity>()
            };

            foreach (SavedCity city in Cities ?? new List<SavedCity>())
            {
                clone.Cities.Add(new SavedCity { Zip = city.Zip, DisplayName = city.DisplayName });
            }

            return clone;
        }
    }

    internal sealed class SavedCity
    {
        public string Zip = string.Empty;
        public string DisplayName = string.Empty;

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(DisplayName) ? Zip : DisplayName + "  " + Zip;
        }
    }

    internal sealed class ZipRecord
    {
        public string Zip;
        public string City;
        public string State;
        public double Latitude;
        public double Longitude;
    }

    internal sealed class CurrentConditions
    {
        public int? TemperatureF;
        public int? FeelsLikeF;
        public string ConditionText;
    }

    internal sealed class ForecastPeriod
    {
        public string Name;
        public DateTime Start;
        public DateTime End;
        public bool IsDaytime;
        public int? Temperature;
        public string TemperatureUnit;
        public string ShortForecast;
    }

    internal sealed class HourlyForecast
    {
        public DateTime Start;
        public int? Temperature;
        public string IconKey;

        public static HourlyForecast FromPeriod(ForecastPeriod period)
        {
            return new HourlyForecast
            {
                Start = period.Start,
                Temperature = period.Temperature,
                IconKey = IconMapper.FromCondition(period.ShortForecast)
            };
        }

        public static List<HourlyForecast> Placeholders()
        {
            var list = new List<HourlyForecast>();
            DateTime now = DateTime.Now;
            for (int i = 0; i < 6; i++)
            {
                list.Add(new HourlyForecast { Start = now.AddHours(i + 1), Temperature = null, IconKey = i % 3 == 1 ? "sunny" : "partly_cloudy" });
            }
            return list;
        }
    }

    internal sealed class DailyForecast
    {
        public DateTime Date;
        public int? High;
        public int? Low;
        public string IconKey;

        public static List<DailyForecast> Placeholders()
        {
            var list = new List<DailyForecast>();
            DateTime today = DateTime.Today;
            string[] icons = new[] { "partly_cloudy", "sunny", "rain", "storm", "cloudy", "partly_cloudy", "sunny" };
            for (int i = 0; i < 7; i++)
            {
                list.Add(new DailyForecast { Date = today.AddDays(i), IconKey = icons[i] });
            }
            return list;
        }
    }

    internal sealed class LayoutRects
    {
        public RectangleF Top;
        public RectangleF Clock;
        public RectangleF Hourly;
        public RectangleF Daily;

        public static LayoutRects From(Rectangle bounds)
        {
            float marginX = Math.Max(18f, bounds.Width * 0.016f);
            float topY = Math.Max(40f, bounds.Height * 0.055f);
            float topHeight = Math.Max(78f, bounds.Height * 0.125f);
            float hourlyHeight = Math.Max(90f, bounds.Height * 0.148f);
            float dailyHeight = Math.Max(92f, bounds.Height * 0.145f);
            float bottomMargin = Math.Max(12f, bounds.Height * 0.018f);
            float gap = Math.Max(10f, bounds.Height * 0.017f);
            float width = bounds.Width - marginX * 2f;
            float dailyTop = bounds.Bottom - bottomMargin - dailyHeight;
            float hourlyTop = dailyTop - gap - hourlyHeight;
            float clockTop = topY + topHeight + Math.Max(12f, bounds.Height * 0.018f);
            float clockBottom = hourlyTop - Math.Max(8f, bounds.Height * 0.012f);

            return new LayoutRects
            {
                Top = new RectangleF(marginX, topY, width, topHeight),
                Clock = new RectangleF(marginX, clockTop, width, Math.Max(140f, clockBottom - clockTop)),
                Hourly = new RectangleF(marginX + width * 0.047f, hourlyTop, width * 0.906f, hourlyHeight),
                Daily = new RectangleF(marginX + width * 0.047f, dailyTop, width * 0.906f, dailyHeight)
            };
        }
    }
}
