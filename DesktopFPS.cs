// DesktopFPS.cs — 桌面帧率悬浮窗（单文件、免安装、删除即卸载）
//
// 原理：调用 dwmapi!DwmGetCompositionTimingInfo 读取 DWM（桌面窗口管理器）合成计时，
//       对 cFrame（已合成帧计数）做差分得到实时桌面合成帧率；rateRefresh 为显示器刷新率上限。
// 结构体布局必须与 Win10/11 SDK dwmapi.h 完全一致：整体 pack(1)，cFramesOutstanding 为 UINT（cbSize=292）。
// 注意：Windows 8.1 起 hwnd 参数必须为 NULL。
//
// 编译：build.bat（使用 Windows 自带 .NET Framework 编译器，无需安装任何东西）
// 卸载：运行 uninstall.bat，或直接删除 DesktopFPS.exe（右键菜单里的“开机自启”也会同步清理）
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DesktopFPS
{
    internal static class Native
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO info);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x20;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct UNSIGNED_RATIO
        {
            public uint uiNumerator;
            public uint uiDenominator;
        }

        // 与 Win10/11 SDK dwmapi.h 中的 DWM_TIMING_INFO 逐字段一致（pshpack1.h => Pack=1）
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct DWM_TIMING_INFO
        {
            public uint cbSize;
            public UNSIGNED_RATIO rateRefresh;   // 显示器刷新率
            public ulong qpcRefreshPeriod;
            public UNSIGNED_RATIO rateCompose;   // 合成帧率
            public ulong qpcVBlank;
            public ulong cRefresh;               // DWM vsync 计数
            public uint cDXRefresh;
            public ulong qpcCompose;
            public ulong cFrame;                 // 已合成帧计数（测 FPS 用它差分）
            public uint cDXPresent;
            public ulong cRefreshFrame;
            public ulong cFrameSubmitted;
            public uint cDXPresentSubmitted;
            public ulong cFrameConfirmed;
            public uint cDXPresentConfirmed;
            public ulong cRefreshConfirmed;
            public uint cDXRefreshConfirmed;
            public ulong cFramesLate;
            public uint cFramesOutstanding;      // SDK 头文件里是 UINT，不是 64 位
            public ulong cFrameDisplayed;
            public ulong qpcFrameDisplayed;
            public ulong cRefreshFrameDisplayed;
            public ulong cFrameComplete;
            public ulong qpcFrameComplete;
            public ulong cFramePending;
            public ulong qpcFramePending;
            public ulong cFramesDisplayed;
            public ulong cFramesComplete;
            public ulong cFramesPending;
            public ulong cFramesAvailable;
            public ulong cFramesDropped;
            public ulong cFramesMissed;
            public ulong cRefreshNextDisplayed;
            public ulong cRefreshNextPresented;
            public ulong cRefreshesDisplayed;
            public ulong cRefreshesPresented;
            public ulong cRefreshStarted;
            public ulong cPixelsReceived;
            public ulong cPixelsDrawn;
            public ulong cBuffersEmpty;
        }
    }

    internal sealed class FpsWidget : Form
    {
        private const int Radius = 14;

        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private static readonly Font NumFont = new Font("Segoe UI", 26f, FontStyle.Bold, GraphicsUnit.Pixel);
        private static readonly Font CapFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        private NotifyIcon _tray;
        private ToolStripMenuItem _miClickThrough;
        private ToolStripMenuItem _miAutoStart;

        private double _lastTime;
        private bool _hasLast;
        private ulong _lastFrame;
        private double _fps = -1;
        private double _refreshHz;
        private int _shownFps = int.MinValue;
        private string _shownCaption;
        private bool _dragging;
        private Point _dragOffset;
        private bool _clickThrough;

        public FpsWidget()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            Opacity = 0.92;
            BackColor = Color.FromArgb(17, 20, 28);
            Cursor = Cursors.SizeAll;
            Size = new Size(140, 62);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 24, wa.Top + 24);
            Region = new Region(RoundPath(ClientRectangle, Radius));

            _tray = new NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "桌面帧率（DWM 合成）",
                Visible = true
            };
            ContextMenuStrip menu = BuildMenu();
            ContextMenuStrip = menu;
            _tray.ContextMenuStrip = menu;
            menu.Opening += delegate { _miAutoStart.Checked = AutoStartEnabled(); };

            _timer.Tick += OnTick;
            _timer.Start();
        }

        // ---------- 采样 ----------

        private void OnTick(object sender, EventArgs e)
        {
            var ti = new Native.DWM_TIMING_INFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(Native.DWM_TIMING_INFO))
            };
            int hr = Native.DwmGetCompositionTimingInfo(IntPtr.Zero, ref ti);
            if (hr == 0)
            {
                if (ti.rateRefresh.uiDenominator != 0)
                    _refreshHz = (double)ti.rateRefresh.uiNumerator / ti.rateRefresh.uiDenominator;

                double now = _sw.Elapsed.TotalSeconds;
                if (_hasLast)
                {
                    double dt = now - _lastTime;
                    if (dt >= 0.05)
                    {
                        long delta = unchecked((long)(ti.cFrame - _lastFrame));
                        if (delta < 0) delta = 0;                       // DWM 重启时计数会复位
                        double cap = _refreshHz > 1 ? _refreshHz * 2 + 60 : 600;
                        double fps = Math.Min(delta / dt, cap);
                        _fps = _fps < 0 ? fps : _fps + 0.3 * (fps - _fps); // EMA 平滑
                        _lastTime = now;
                    }
                }
                else
                {
                    _lastTime = now;
                    _hasLast = true;
                }
                _lastFrame = ti.cFrame;
            }

            int shown = _fps < 0 ? int.MinValue : (int)Math.Round(_fps);
            string caption = _refreshHz > 1 ? "桌面合成 · 上限 " + Math.Round(_refreshHz) + "Hz" : "桌面合成帧率";
            if (shown != _shownFps || caption != _shownCaption)
            {
                _shownFps = shown;
                _shownCaption = caption;
                Invalidate();
            }
        }

        private Color ColorFor(double fps)
        {
            if (fps < 0) return Color.FromArgb(156, 163, 175);          // 尚无数据
            if (_refreshHz > 1)
            {
                double ratio = fps / _refreshHz;
                if (ratio >= 0.85) return Color.FromArgb(52, 211, 153); // 绿：接近刷新率
                if (ratio >= 0.5) return Color.FromArgb(251, 191, 36);  // 黄：中等
                if (ratio > 0.05) return Color.FromArgb(248, 113, 113); // 红：偏低
                return Color.FromArgb(156, 163, 175);                   // 灰：桌面静止，几乎无合成
            }
            return Color.FromArgb(52, 211, 153);
        }

        // ---------- 绘制 ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var bg = new SolidBrush(BackColor))
                g.FillPath(bg, RoundPath(ClientRectangle, Radius));

            string num = _fps < 0 ? "--" : Math.Round(_fps).ToString();
            using (var b = new SolidBrush(ColorFor(_fps)))
                g.DrawString(num, NumFont, b, 14, 7);

            string cap = _refreshHz > 1
                ? "桌面合成 · 上限 " + Math.Round(_refreshHz) + "Hz"
                : "桌面合成帧率";
            using (var b2 = new SolidBrush(Color.FromArgb(148, 158, 172)))
                g.DrawString(cap, CapFont, b2, 14, 36);

            base.OnPaint(e);
        }

        private static GraphicsPath RoundPath(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, rad, rad, 180, 90);
            p.AddArc(r.Right - rad - 1, r.Y, rad, rad, 270, 90);
            p.AddArc(r.Right - rad - 1, r.Bottom - rad - 1, rad, rad, 0, 90);
            p.AddArc(r.X, r.Bottom - rad - 1, rad, rad, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------- 窗口行为 ----------

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;   // 不进 Alt+Tab
                cp.ExStyle |= Native.WS_EX_NOACTIVATE;   // 点击不抢焦点
                return cp;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragOffset = e.Location;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                Point p = PointToScreen(e.Location);
                Location = new Point(p.X - _dragOffset.X, p.Y - _dragOffset.Y);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            base.OnMouseUp(e);
        }

        private void SetClickThrough(bool on)
        {
            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            ex = on ? (ex | Native.WS_EX_TRANSPARENT) : (ex & ~Native.WS_EX_TRANSPARENT);
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);
            _clickThrough = on;
            _miClickThrough.Checked = on;
        }

        // ---------- 菜单 / 托盘 ----------

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            _miClickThrough = new ToolStripMenuItem("鼠标穿透（不挡点击）") { CheckOnClick = true };
            _miClickThrough.Click += delegate { SetClickThrough(!_clickThrough); };
            menu.Items.Add(_miClickThrough);

            _miAutoStart = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
            _miAutoStart.Checked = AutoStartEnabled();
            _miAutoStart.Click += delegate { SetAutoStart(_miAutoStart.Checked); };
            menu.Items.Add(_miAutoStart);

            menu.Items.Add(new ToolStripSeparator());
            var miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { Close(); };
            menu.Items.Add(miExit);
            return menu;
        }

        private static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(16, 16))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Color.FromArgb(52, 211, 153)))
                    g.FillEllipse(b, 2, 2, 12, 12);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            base.OnFormClosed(e);
        }

        // ---------- 开机自启（Startup 文件夹快捷方式，删掉即关闭） ----------

        private static string LnkPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DesktopFPS.lnk"); }
        }

        private static bool AutoStartEnabled()
        {
            return File.Exists(LnkPath);
        }

        private static void SetAutoStart(bool on)
        {
            if (on)
            {
                Type shellType = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8")); // WScript.Shell
                object shell = Activator.CreateInstance(shellType);
                try
                {
                    object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null,
                        shell, new object[] { LnkPath });
                    try
                    {
                        Type lt = lnk.GetType();
                        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk,
                            new object[] { Application.ExecutablePath });
                        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk,
                            new object[] { AppDomain.CurrentDomain.BaseDirectory });
                        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                    }
                    finally { Marshal.ReleaseComObject(lnk); }
                }
                finally { Marshal.ReleaseComObject(shell); }
            }
            else if (File.Exists(LnkPath))
            {
                File.Delete(LnkPath);
            }
        }

        // ---------- 入口 ----------

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "DesktopFPS_Widget_Mutex", out createdNew))
            {
                if (!createdNew) return; // 已在运行
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new FpsWidget());
            }
        }
    }
}
