// MonitorForm.cs - 主界面 v4
// 核心改动: 卡片动态光效 / 内存Top10进程 / 时间范围可调 / 标题栏按钮 / 全屏切换 / 四周缩放 / 事件弹窗
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace GameMonitor
{
    internal static class Theme
    {
        public static readonly Color Bg       = Color.FromArgb(9, 12, 17);
        public static readonly Color Card     = Color.FromArgb(16, 21, 29);
        public static readonly Color Edge     = Color.FromArgb(36, 48, 63);
        public static readonly Color Grid     = Color.FromArgb(26, 34, 46);
        public static readonly Color Main     = Color.FromArgb(233, 239, 248);
        public static readonly Color Sub      = Color.FromArgb(122, 136, 155);
        public static readonly Color Gpu      = Color.FromArgb(94, 236, 158);
        public static readonly Color Cpu      = Color.FromArgb(96, 170, 255);
        public static readonly Color Ram      = Color.FromArgb(178, 148, 255);
        public static readonly Color Danger   = Color.FromArgb(255, 99, 99);
        public static readonly Color Warn     = Color.FromArgb(255, 190, 60);
    }

    // ---- 事件详情弹窗 ----
    internal sealed class EventPopup : Form
    {
        Collector _col; Timer _timer; float _sf;
        Font _fTitle, _fItem;

        public EventPopup(Collector col, float sf, Point loc)
        {
            _col = col; _sf = sf;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = loc; TopMost = true; ShowInTaskbar = false;
            BackColor = Theme.Card; DoubleBuffered = true;
            Width = (int)(440 * sf); Height = (int)(400 * sf);
            _fTitle = new Font("Microsoft YaHei", 10f * sf, FontStyle.Bold);
            _fItem = new Font("Microsoft YaHei", 8.5f * sf);
            _timer = new Timer(); _timer.Interval = 1000;
            _timer.Tick += delegate { Invalidate(); }; _timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Theme.Card);
            using (GraphicsPath p = MonitorForm.RoundRect(0, 0, Width, Height, 12f * _sf))
            using (Pen pen = new Pen(Theme.Edge)) g.DrawPath(pen, p);
            g.DrawString("事件记录 (全部)", _fTitle, new SolidBrush(Theme.Main), (int)(14 * _sf), (int)(10 * _sf));
            List<MonEvent> evs = _col.Events;
            int y0 = (int)(40 * _sf), rowH = (int)(22 * _sf);
            int max = (Height - y0 - (int)(10 * _sf)) / rowH;
            for (int idx = 0; idx < Math.Min(max, evs.Count); idx++)
            {
                int i = evs.Count - 1 - idx;
                MonEvent ev = evs[i];
                int ry = y0 + idx * rowH;
                Color dotC = ev.Hw == 0 ? Theme.Gpu : (ev.Hw == 1 ? Theme.Cpu : (ev.Hw == 2 ? Theme.Ram : Theme.Sub));
                float dot = Math.Max(4f, 5f * _sf);
                bool serious = ev.Kind == 0 || ev.Kind == 1;
                if (serious) { float ring = dot + 4f * _sf; using (SolidBrush ob = new SolidBrush(Theme.Danger)) g.FillEllipse(ob, (int)(14 * _sf) + dot / 2 - ring / 2, ry + rowH / 2 - ring / 2, ring, ring); }
                g.FillEllipse(new SolidBrush(dotC), (int)(14 * _sf), ry + rowH / 2 - dot / 2, dot, dot);
                Color tc = ev.Kind == 0 ? Theme.Danger : (ev.Kind == 1 ? Theme.Warn : Theme.Main);
                g.DrawString(ev.T.ToString("HH:mm:ss") + "  " + ev.Msg, _fItem, new SolidBrush(tc), (int)(26 * _sf), ry);
            }
            if (evs.Count == 0) g.DrawString("—— 暂无事件 ——", _fItem, new SolidBrush(Color.FromArgb(140, Theme.Sub)), (int)(14 * _sf), y0);
        }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); _timer.Stop(); Close(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); }
    }

    internal sealed class MonitorForm : Form
    {
        Collector _col; Sample _cur;
        Timer _timer, _animTimer, _saveTimer;
        NotifyIcon _tray; ContextMenuStrip _menu;
        bool _clickThrough; int _tick;
        string _cfgPath;
        DateTime _sessionStart = DateTime.Now;

        // ---- 动画 ----
        readonly float[] _dispLoads = new float[8];
        float _dispCpuLoad;

        // ---- 内存 Top10 显示层 (数值+位置双重平滑动画, 按PID标识进程) ----
        internal sealed class DispProcEntry
        {
            public int Pid; public string Name; public Bitmap Icon;
            public float DispMem, TargetMem;     // 显示内存 -> 目标内存
            public float DispPos, TargetPos;    // 显示行位 -> 目标行位 (排名变化时平滑滑动)
            public long ShowMem;                // 显示用的真实数值
        }
        readonly List<DispProcEntry> _dispProcs = new List<DispProcEntry>();

        // ---- 缩放 ----
        const float BW = 420f, BH = 1280f;
        float _sx = 1f, _sy = 1f, _sf = 1f;

        // ---- 行高 ----
        int _hTitle, _hVal, _hMicro, _hBig, _hClock, _hTiny, _hLabel;
        int _cPad, _cGap, _rBar, _rTitleRow, _rMetricRow, _rBigRow, _rEvtRow;

        // ---- 布局 Y ----
        int _padX, _cardW, _titleBarH, _footH;
        int _gpuY, _cpuY, _ramY, _loadY, _evtY, _footY;
        int _gpuH, _cpuH, _ramH, _loadH, _evtH, _gpuTempChartH;

        // ---- 字体 ----
        Font _fTitle, _fBig, _fBigUnit, _fVal, _fLabel, _fTiny, _fMicro, _fClock;

        // ---- 标题栏按钮 ----
        Rectangle _btnMin, _btnFull, _btnClose;
        bool _hoverMin, _hoverFull, _hoverClose;

        // ---- 全屏 ----
        bool _fullscreen; Rectangle _savedBounds;

        // ---- 负载曲线时间范围 ----
        string[] _rangeLabels = { "1m", "2m", "5m", "10m" };
        int[] _rangeValues = { 60, 120, 300, 600 };
        Rectangle[] _rangeBtnRects = new Rectangle[4];
        int _chartRangeSec = 120;
        bool _hoverRange;

        // ---- 事件弹窗 ----
        EventPopup _evtPopup;

        public MonitorForm()
        {
            Text = "游戏性能监控";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bg; TopMost = true; DoubleBuffered = true;
            MinimumSize = new Size(340, 780);
            ClientSize = new Size(540, 1440);

            string cfgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameMonitor");
            Directory.CreateDirectory(cfgDir);
            _cfgPath = Path.Combine(cfgDir, "config.txt");
            LoadConfig();

            _saveTimer = new Timer(); _saveTimer.Interval = 800;
            _saveTimer.Tick += delegate { _saveTimer.Stop(); SaveConfig(); };

            RebuildMetrics();
            _col = new Collector(); _col.Start();
            if (!ScreenConfigured()) LocateSecondary();
            BuildTray();

            _timer = new Timer(); _timer.Interval = 1000; _timer.Tick += OnTick; _timer.Start();
            _animTimer = new Timer(); _animTimer.Interval = 80; _animTimer.Tick += OnAnimTick;
            OnTick(null, null);
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x8 | 0x80 | 0x08000000; return cp; }
        }

        // ---- 四周缩放 ----
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            if (m.Msg == WM_NCHITTEST && !_clickThrough && !_fullscreen)
            {
                base.WndProc(ref m);
                if ((int)m.Result == 1)
                {
                    Point p = PointToClient(Cursor.Position);
                    int e = Math.Max(6, (int)(8 * _sf));
                    int w = ClientSize.Width, h = ClientSize.Height;
                    // 标题栏按钮区域不缩放
                    if (p.Y < _titleBarH && p.X >= _btnMin.X - (int)(4 * _sf)) return;
                    // 四角
                    if (p.X <= e && p.Y <= e) { m.Result = (IntPtr)13; return; }
                    if (p.X >= w - e && p.Y <= e) { m.Result = (IntPtr)14; return; }
                    if (p.X <= e && p.Y >= h - e) { m.Result = (IntPtr)16; return; }
                    if (p.X >= w - e && p.Y >= h - e) { m.Result = (IntPtr)17; return; }
                    // 四边
                    if (p.Y <= e) { m.Result = (IntPtr)12; return; }
                    if (p.X <= e) { m.Result = (IntPtr)10; return; }
                    if (p.X >= w - e) { m.Result = (IntPtr)11; return; }
                    if (p.Y >= h - e) { m.Result = (IntPtr)15; return; }
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); ApplyClickThrough(); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e); RebuildMetrics(); Invalidate();
            if (!_fullscreen && _saveTimer != null) { _saveTimer.Stop(); _saveTimer.Start(); }
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (!_fullscreen && _saveTimer != null) { _saveTimer.Stop(); _saveTimer.Start(); }
        }

        // ================= 布局引擎 =================
        void RebuildMetrics()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w < 50 || h < 50) return;
            _sx = w / BW; _sy = h / BH;
            float sfTry = Math.Max(0.8f, Math.Min(2.4f, Math.Min(_sx, _sy * 1.15f)));
            for (int i = 0; i < 4; i++)
            {
                _sf = sfTry; float overflow;
                if (ComputeLayout(w, h, out overflow)) break;
                sfTry = sfTry * (float)h / (h + overflow) * 0.97f;
                if (sfTry < 0.55f) { _sf = 0.55f; ComputeLayout(w, h, out overflow); break; }
            }
            if (Region != null) Region.Dispose();
            Region = new Region(RoundRect(0, 0, w, h, 14f * _sf));
        }

        bool ComputeLayout(int w, int h, out float overflow)
        {
            overflow = 0; RebuildFonts();
            using (Graphics mg = CreateGraphics())
            {
                mg.TextRenderingHint = TextRenderingHint.AntiAlias;
                _hTitle = (int)mg.MeasureString("游戏性能监控Ag", _fTitle).Height;
                _hVal = (int)mg.MeasureString("1980 MHz", _fVal).Height;
                _hMicro = (int)mg.MeasureString("核心频率", _fMicro).Height;
                _hBig = (int)mg.MeasureString("88", _fBig).Height;
                _hClock = (int)mg.MeasureString("00:00:00", _fClock).Height;
                _hTiny = (int)mg.MeasureString("事件记录行", _fTiny).Height;
                _hLabel = (int)mg.MeasureString("负载曲线", _fLabel).Height;
            }
            _padX = (int)(16 * _sf); _cardW = w - _padX * 2;
            _cPad = (int)(14 * _sf); _cGap = Math.Max(5, (int)(8 * _sf));
            _rBar = Math.Max(4, (int)(5 * _sf));
            _rTitleRow = _hTitle + (int)(14 * _sf);
            _rMetricRow = _hMicro + _hVal + Math.Max(2, (int)(2 * _sf));
            _rBigRow = _hBig + (int)(8 * _sf);
            _rEvtRow = _hTiny + Math.Max(2, (int)(3 * _sf));

            // 标题栏: 仅标题+按钮, 无副标题
            int btnSize = (int)(22 * _sf);
            _titleBarH = Math.Max(_hTitle, btnSize) + (int)(16 * _sf);

            int dataRows = Math.Max(_rBigRow, _rMetricRow * 2 + _cGap);       // 2 行指标 (CPU/内存卡)
            int dataRows3 = Math.Max(_rBigRow, _rMetricRow * 3 + _cGap * 2);    // 3 行指标 (GPU卡)

            // GPU 卡: 无底部进度条
            _gpuTempChartH = _hLabel + (int)(8 * _sf) + (int)(58 * _sf);
            _gpuH = _cPad * 2 + _rTitleRow + _cGap + dataRows3 + _cGap + _gpuTempChartH + (int)(6 * _sf);

            // CPU 卡: 无核心序号行
            int coreChart = _hMicro + Math.Max(3, (int)(3 * _sf)) + _hMicro + Math.Max(2, (int)(2 * _sf)) + (int)(40 * _sf);
            _cpuH = _cPad * 2 + _rTitleRow + _cGap + dataRows + _cGap + coreChart + Math.Max(4, (int)(6 * _sf)) + _hMicro;

            // 内存卡: Top10 进程
            int memRowH = Math.Max(14, (int)(16 * _sf));
            int memLabelH = _hLabel + (int)(4 * _sf);
            _ramH = _cPad * 2 + _rTitleRow + _cGap + dataRows + _cGap + memLabelH + 10 * memRowH + (int)(4 * _sf);

            // 事件卡: 仅 1 行
            _evtH = _cPad * 2 + _rTitleRow + (int)(4 * _sf) + _rEvtRow;
            _footH = _hMicro + (int)(6 * _sf);

            // 堆叠
            int y = _titleBarH;
            int gap = Math.Max(6, (int)(10 * _sf));
            _gpuY = y; y += _gpuH + gap;
            _cpuY = y; y += _cpuH + gap;
            _ramY = y; y += _ramH + gap;
            _loadY = y;
            _loadH = _gpuH;
            _evtY = _loadY + _loadH + gap;
            _footY = _evtY + _evtH + Math.Max(4, (int)(6 * _sf));

            int totalBottom = _footY + _footH;
            if (totalBottom > h) { overflow = totalBottom - h; return false; }
            return true;
        }

        void RebuildFonts()
        {
            DisposeFonts();
            _fTitle = new Font("Microsoft YaHei", 10.5f * _sf, FontStyle.Bold);
            _fBig = new Font("Segoe UI", 30f * _sf, FontStyle.Bold);
            _fBigUnit = new Font("Segoe UI", 13f * _sf, FontStyle.Bold);
            _fVal = new Font("Segoe UI", 10.5f * _sf, FontStyle.Bold);
            _fLabel = new Font("Microsoft YaHei", 8.5f * _sf);
            _fTiny = new Font("Microsoft YaHei", 8.5f * _sf);
            _fMicro = new Font("Microsoft YaHei", 7.5f * _sf);
            _fClock = new Font("Segoe UI", 9.5f * _sf, FontStyle.Bold);
        }

        void DisposeFonts()
        {
            if (_fTitle != null) _fTitle.Dispose(); _fTitle = null;
            if (_fBig != null) _fBig.Dispose();
            if (_fBigUnit != null) _fBigUnit.Dispose();
            if (_fVal != null) _fVal.Dispose();
            if (_fLabel != null) _fLabel.Dispose();
            if (_fTiny != null) _fTiny.Dispose();
            if (_fMicro != null) _fMicro.Dispose();
            if (_fClock != null) _fClock.Dispose();
        }

        // ================= 数据 =================
        void OnTick(object sender, EventArgs e)
        {
            if (!Visible) return;
            _tick++;
            if (_col != null) { _cur = _col.CollectOnce(); NotifyThrottle(); UpdateProcTargets(); }
            Invalidate();
            if (_animTimer != null && !_animTimer.Enabled) _animTimer.Start();
        }

        void OnAnimTick(object sender, EventArgs e)
        {
            if (_col == null) { _animTimer.Stop(); return; }
            bool moved = false;
            for (int i = 0; i < _dispLoads.Length; i++)
            {
                float target = _col.CoreLoads[i]; float d = target - _dispLoads[i];
                if (Math.Abs(d) > 0.4f) { _dispLoads[i] += d * 0.28f; moved = true; } else _dispLoads[i] = target;
            }
            float dc = _cur != null ? _cur.CpuLoad : 0f; float dcl = dc - _dispCpuLoad;
            if (Math.Abs(dcl) > 0.3f) { _dispCpuLoad += dcl * 0.28f; moved = true; } else _dispCpuLoad = dc;
            // 内存 Top10: 数值 + 位置双平滑 (排名变化时条目交叉滑动, 柱长同步过渡)
            for (int i = _dispProcs.Count - 1; i >= 0; i--)
            {
                DispProcEntry dp = _dispProcs[i];
                float dm = dp.TargetMem - dp.DispMem;
                if (Math.Abs(dm) > 0.5f) { dp.DispMem += dm * 0.28f; moved = true; } else dp.DispMem = dp.TargetMem;
                float dpz = dp.TargetPos - dp.DispPos;
                if (Math.Abs(dpz) > 0.02f) { dp.DispPos += dpz * 0.25f; moved = true; } else dp.DispPos = dp.TargetPos;
                if (dp.TargetPos > 10f && dp.DispPos > 10.2f) _dispProcs.RemoveAt(i);   // 完全滑出后移除
            }
            if (moved) Invalidate(new Rectangle(0, _ramY, ClientSize.Width, ClientSize.Height - _ramY));
            else _animTimer.Stop();
        }

        // 同步显示层目标: 新采样到来时按 PID 匹配条目, 更新目标排名/数值 (同名多进程互不干扰)
        void UpdateProcTargets()
        {
            List<ProcInfo> procs = _col.TopProcs;
            for (int i = 0; i < _dispProcs.Count; i++) _dispProcs[i].TargetPos = 10.5f;   // 先全部标记离场
            bool firstFill = _dispProcs.Count == 0;
            for (int rank = 0; rank < procs.Count && rank < 10; rank++)
            {
                ProcInfo pi = procs[rank];
                DispProcEntry e = null;
                for (int j = 0; j < _dispProcs.Count; j++) if (_dispProcs[j].Pid == pi.Pid) { e = _dispProcs[j]; break; }
                if (e == null)
                {
                    e = new DispProcEntry();
                    e.Pid = pi.Pid;
                    e.Name = pi.Name;
                    e.Icon = pi.Icon;
                    e.DispMem = pi.MemoryMB;
                    e.DispPos = firstFill ? rank : 10.5f;   // 首次填充直接就位, 后续新条目从底部滑入
                    _dispProcs.Add(e);
                }
                else if (pi.Icon != null) e.Icon = pi.Icon;
                e.TargetMem = pi.MemoryMB;
                e.TargetPos = rank;
                e.ShowMem = pi.MemoryMB;
            }
        }

        DateTime _lastBalloon = DateTime.MinValue;
        void NotifyThrottle()
        {
            if (_cur == null || !_cur.GpuOk) return;
            if ((_cur.ThrottleBits & 0x1D8) != 0 && (DateTime.Now - _lastBalloon).TotalSeconds > 120)
            {
                _lastBalloon = DateTime.Now;
                try { _tray.ShowBalloonTip(3000, "GPU 降频提醒", "检测到 GPU 降频, 性能受限制", ToolTipIcon.Warning); } catch { }
            }
        }

        // ================= 绘制 =================
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Theme.Bg);
            DrawTitleBar(g);
            if (_cur == null)
            {
                using (StringFormat cf = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("正在初始化...", _fLabel, new SolidBrush(Theme.Sub), new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), cf);
                return;
            }
            DrawGpuCard(g); DrawCpuCard(g); DrawRamCard(g); DrawLoadChart(g); DrawEvents(g); DrawFoot(g);
        }

        // ---- 标题栏: 状态灯 + 标题 + 按钮 ----
        void DrawTitleBar(Graphics g)
        {
            bool throttling = _cur != null && _cur.GpuOk && (_cur.ThrottleBits & 0x1D8) != 0;
            bool hot = _cur != null && _cur.GpuOk && _cur.GpuTemp >= 83f;
            Color lamp = throttling ? Theme.Danger : (hot ? Theme.Warn : Theme.Gpu);
            int rowCenter = _titleBarH / 2;
            float pulse = 0.55f + 0.45f * (float)Math.Abs(Math.Sin(_tick / 4.0));
            if (throttling) pulse = 0.9f + 0.1f * (float)Math.Abs(Math.Sin(_tick * 1.2));
            int lr = Math.Max(4, (int)(6 * _sf));
            using (GraphicsPath lp = new GraphicsPath())
            {
                lp.AddEllipse(_padX + (int)(4 * _sf) - lr, rowCenter - lr, lr * 2, lr * 2);
                using (PathGradientBrush pg = new PathGradientBrush(lp))
                { pg.CenterColor = Color.FromArgb((int)(255 * pulse), lamp); pg.SurroundColors = new Color[] { Color.FromArgb(0, lamp) }; g.FillPath(pg, lp); }
            }
            g.DrawString("游戏性能监控", _fTitle, new SolidBrush(Theme.Main), _padX + (int)(16 * _sf), rowCenter - _hTitle / 2);

            // 按钮
            int bs = (int)(22 * _sf), gap = (int)(4 * _sf);
            int rx = ClientSize.Width - _padX - bs;
            int by = rowCenter - bs / 2;
            _btnClose = new Rectangle(rx, by, bs, bs); rx -= bs + gap;
            _btnFull = new Rectangle(rx, by, bs, bs); rx -= bs + gap;
            _btnMin = new Rectangle(rx, by, bs, bs);
            DrawBtn(g, _btnMin, 0, _hoverMin);
            DrawBtn(g, _btnFull, 1, _hoverFull);
            DrawBtn(g, _btnClose, 2, _hoverClose);
        }

        void DrawBtn(Graphics g, Rectangle r, int type, bool hover)
        {
            Color bg = hover ? Color.FromArgb(45, 255, 255, 255) : Color.FromArgb(15, 255, 255, 255);
            using (GraphicsPath p = RoundRect(r.X, r.Y, r.Width, r.Height, 5f * _sf))
            using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, p);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, sz = r.Width * 0.28f;
            Color sc = hover ? Theme.Main : Color.FromArgb(160, Theme.Main);
            if (type == 2) sc = hover ? Theme.Danger : Color.FromArgb(180, Theme.Danger);
            using (Pen pen = new Pen(sc, 1.6f * _sf))
            {
                if (type == 0) g.DrawLine(pen, cx - sz, cy, cx + sz, cy);
                else if (type == 1) g.DrawRectangle(pen, cx - sz, cy - sz, sz * 2, sz * 2);
                else { g.DrawLine(pen, cx - sz, cy - sz, cx + sz, cy + sz); g.DrawLine(pen, cx + sz, cy - sz, cx - sz, cy + sz); }
            }
        }

        // ---- 卡片动态光效: 绿→红随负载过渡 ----
        static Color LoadColor(float load)
        {
            float n = Math.Max(0f, Math.Min(1f, load / 100f));
            if (n < 0.5f) { float t = n * 2f; return Color.FromArgb((int)(94 + 161 * t), (int)(236 - 46 * t), (int)(158 - 98 * t)); }
            float t2 = (n - 0.5f) * 2f; return Color.FromArgb(255, (int)(190 * (1f - t2 * 0.5f)), (int)(60 * (1f - t2 * 0.7f)));
        }

        void DrawCardGlow(Graphics g, int x, int y, int w, int h, float load)
        {
            if (load < 0) load = 0; if (load > 100) load = 100;
            Color c = LoadColor(load);
            float intensity = 0.3f + 0.5f * (load / 100f);
            for (int i = 5; i >= 1; i--)
            {
                int exp = i * 2, a = (int)(intensity * (1f - (float)i / 6f) * 90f);
                if (a <= 0) continue;
                using (GraphicsPath p = RoundRect(x - exp, y - exp, w + exp * 2, h + exp * 2, (12f + exp) * _sf))
                using (Pen pen = new Pen(Color.FromArgb(a, c), 1.5f * _sf)) g.DrawPath(pen, p);
            }
        }

        void Card(Graphics g, int y, int h, string title, Color accent, float load, int maxTitleW)
        {
            int x = _padX, w = _cardW;
            DrawCardGlow(g, x, y, w, h, load);
            using (GraphicsPath p = RoundRect(x, y, w, h, 12f * _sf))
            { g.FillPath(new SolidBrush(Theme.Card), p); using (Pen pe = new Pen(Theme.Edge)) g.DrawPath(pe, p); }
            using (SolidBrush b = new SolidBrush(accent))
                g.FillRectangle(b, x + _cPad, y + _cPad + (int)(3 * _sf), Math.Max(2f, 3f * _sf), _hTitle - (int)(6 * _sf));
            g.DrawString(FitTitle(g, title, _fTitle, maxTitleW), _fTitle, new SolidBrush(Theme.Main), x + _cPad + (int)(10 * _sf), y + _cPad + (int)(3 * _sf));
        }

        string FitTitle(Graphics g, string s, Font f, int maxW)
        {
            if (maxW <= 0 || string.IsNullOrEmpty(s)) return s;
            if (g.MeasureString(s, f).Width <= maxW) return s;
            for (int len = s.Length - 1; len > 1; len--) { string t = s.Substring(0, len) + "…"; if (g.MeasureString(t, f).Width <= maxW) return t; }
            return "…";
        }

        string FitString(Graphics g, string s, Font f, int maxW)
        {
            if (maxW <= 0 || string.IsNullOrEmpty(s)) return s;
            if (g.MeasureString(s, f).Width <= maxW) return s;
            for (int len = s.Length - 1; len > 1; len--) { string t = s.Substring(0, len) + "…"; if (g.MeasureString(t, f).Width <= maxW) return t; }
            return "…";
        }

        void Metric(Graphics g, int x, int y, string label, string val, Color c)
        {
            g.DrawString(label, _fMicro, new SolidBrush(Theme.Sub), x, y);
            g.DrawString(val, _fVal, new SolidBrush(c), x, y + _hMicro + Math.Max(2, (int)(2 * _sf)));
        }

        static Color TempColor(float t)
        {
            if (t < 0) return Theme.Sub;
            if (t < 55f) return Theme.Gpu;
            if (t < 70f) return Color.FromArgb(178, 232, 110);
            if (t < 80f) return Theme.Warn;
            if (t < 88f) return Color.FromArgb(255, 128, 69);
            return Theme.Danger;
        }

        static Color FanColor(float p)   // 风扇转速 %
        {
            if (p < 0) return Theme.Sub;
            if (p < 65f) return Theme.Gpu;
            if (p < 85f) return Theme.Warn;
            return Theme.Danger;
        }

        void Bar(Graphics g, int x, int y, int w, float pct, Color c)
        {
            using (GraphicsPath slot = RoundRect(x, y, w, _rBar, _rBar / 2f)) g.FillPath(new SolidBrush(Color.FromArgb(235, 29, 37, 50)), slot);
            int fw = (int)(w * Math.Max(0f, Math.Min(100f, pct)) / 100f);
            if (fw > _rBar) using (GraphicsPath fill = RoundRect(x, y, fw, _rBar, _rBar / 2f))
                using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(x, y, fw, _rBar), Color.FromArgb(150, c), c, 0f)) g.FillPath(lg, fill);
        }

        void BigPct(Graphics g, int x, int y, float pct, Color c)
        {
            string v = ((int)Math.Round(pct)).ToString();
            g.DrawString(v, _fBig, new SolidBrush(c), x, y);
            SizeF vs = g.MeasureString(v, _fBig);
            g.DrawString("%", _fBigUnit, new SolidBrush(Color.FromArgb(170, c)), x + vs.Width - 5 * _sf, y + _hBig * 0.42f);
        }

        int MetricCol1X(int cardX) { return cardX + _cardW - (int)(280 * _sf); }
        int MetricCol2X(int cardX) { return cardX + _cardW - (int)(140 * _sf); }

        // ---- GPU 卡片 ----
        void DrawGpuCard(Graphics g)
        {
            string gpuTitle = _col.GpuName.Replace("NVIDIA ", "").Replace("GeForce ", "");
            Card(g, _gpuY, _gpuH, "GPU  " + gpuTitle, Theme.Gpu, _cur.GpuOk ? _cur.GpuLoad : 0, _cardW - _cPad * 2 - (int)(120 * _sf));
            int x = _padX, y = _gpuY;
            if (!_cur.GpuOk) { g.DrawString("GPU 不可用", _fLabel, new SolidBrush(Theme.Sub), x + _cPad, y + _cPad + _rTitleRow + _cGap); return; }

            bool throttling = (_cur.ThrottleBits & 0x1D8) != 0;
            string thrTxt = "";
            if (throttling) { ulong b = _cur.ThrottleBits; if ((b & 0x8) != 0) thrTxt = "功耗墙"; else if ((b & 0x40) != 0 || (b & 0x80) != 0) thrTxt = "温控"; else if ((b & 0x10) != 0) thrTxt = "硬件降速"; else thrTxt = "降频"; }
            int badgeRight = x + _cardW - _cPad, badgeY = y + _cPad + (int)(3 * _sf), badgeH = _hTitle + (int)(6 * _sf);
            if (thrTxt.Length > 0)
            {
                SizeF tw = g.MeasureString(thrTxt, _fMicro); int bw2 = (int)(tw.Width + 14 * _sf);
                using (GraphicsPath bp = RoundRect(badgeRight - bw2, badgeY, bw2, badgeH, 5 * _sf))
                { g.FillPath(new SolidBrush(Color.FromArgb(200, Theme.Danger)), bp); g.DrawString(thrTxt, _fMicro, new SolidBrush(Color.White), badgeRight - bw2 + 7 * _sf, badgeY + (badgeH - _hMicro) / 2); }
                badgeRight -= bw2 + (int)(8 * _sf);
            }
            string ps = _cur.GpuPState > -90 ? "P" + _cur.GpuPState : "P-";
            SizeF pw = g.MeasureString(ps, _fMicro); int pbw = (int)(pw.Width + 14 * _sf);
            using (GraphicsPath bp = RoundRect(badgeRight - pbw, badgeY, pbw, badgeH, 5 * _sf))
            { g.FillPath(new SolidBrush(Color.FromArgb(48, Theme.Gpu)), bp); using (Pen pe = new Pen(Color.FromArgb(100, Theme.Gpu))) g.DrawPath(pe, bp); g.DrawString(ps, _fMicro, new SolidBrush(Theme.Gpu), badgeRight - pbw + 7 * _sf, badgeY + (badgeH - _hMicro) / 2); }

            int dataY = y + _cPad + _rTitleRow + _cGap;
            BigPct(g, x + _cPad, dataY, _cur.GpuLoad, Theme.Gpu);
            float memPct = _cur.GpuMemTotalMB > 0 ? (float)_cur.GpuMemUsedMB / _cur.GpuMemTotalMB * 100f : 0;
            int col1 = MetricCol1X(x), col2 = MetricCol2X(x);
            Metric(g, col1, dataY, "温度", _cur.GpuTemp >= 0 ? ((int)_cur.GpuTemp).ToString() + " °C" : "N/A", TempColor(_cur.GpuTemp));
            Metric(g, col2, dataY, "核心频率", _cur.GpuClock > 0 ? ((int)_cur.GpuClock).ToString() + " MHz" : "-", Theme.Main);
            int rowGap = Math.Max(3, (int)(_cGap * 0.7f)), row2 = dataY + _rMetricRow + rowGap;
            string powerTxt = _cur.GpuPower > 0 ? (_col.GpuPowerLimitW > 0 ? ((int)_cur.GpuPower).ToString() + " / " + _col.GpuPowerLimitW + " W" : ((int)_cur.GpuPower).ToString() + " W") : "N/A";
            Color powerColor = (_col.GpuPowerLimitW > 0 && _cur.GpuPower > _col.GpuPowerLimitW * 0.92f) ? Theme.Warn : Theme.Main;
            Metric(g, col1, row2, "功耗", powerTxt, powerColor);
            Metric(g, col2, row2, "显存 " + ((int)memPct).ToString() + "%", (_cur.GpuMemUsedMB / 1024.0).ToString("0.0") + "/" + (_cur.GpuMemTotalMB / 1024.0).ToString("0.0") + " GB", memPct >= 90f ? Theme.Warn : Theme.Main);
            int row3 = row2 + _rMetricRow + rowGap;
            bool hasFan = _cur.GpuFanPct >= 0;
            if (hasFan) Metric(g, col1, row3, "风扇", _cur.GpuFanPct.ToString() + " %", FanColor(_cur.GpuFanPct));
            Metric(g, col2, row3, "带宽", _cur.MemUtilPct >= 0 ? _cur.MemUtilPct.ToString() + " %" : "N/A", Theme.Main);

            // GPU 温度/功耗双曲线 (起始 Y 需覆盖 3 行指标)
            int tY = dataY + Math.Max(_rBigRow, _rMetricRow * 3 + rowGap * 2) + _cGap;
            g.DrawString("GPU 温度 / 功耗", _fLabel, new SolidBrush(Theme.Main), x + _cPad, tY);
            // 图例从右往左: 峰值功耗 -> 当前功耗% -> 峰值温度 -> 当前温度
            float powPct = (_col.GpuPowerLimitW > 0 && _cur.GpuPower > 0) ? Math.Min(100f, _cur.GpuPower / _col.GpuPowerLimitW * 100f) : 0f;
            Color powC = Color.FromArgb(255, 150, 60);
            float lx = x + _cardW - _cPad;
            if (_col.PeakGpuPower > 0)
            { string pk2 = "峰值功耗 " + ((int)_col.PeakGpuPower).ToString() + "W"; SizeF p2s = g.MeasureString(pk2, _fMicro); lx -= p2s.Width; g.DrawString(pk2, _fMicro, new SolidBrush(Color.FromArgb(200, powC)), lx, tY + (int)(3 * _sf)); lx -= 10 * _sf; }
            if (_cur.GpuPower > 0)
            {
                string pt = "功耗 " + (powPct > 0 ? ((int)powPct).ToString() + "%" : ((int)_cur.GpuPower).ToString() + "W");
                SizeF pts = g.MeasureString(pt, _fMicro); lx -= pts.Width;
                g.FillEllipse(new SolidBrush(powC), lx, tY + (int)(6 * _sf), Math.Max(4, (int)(5 * _sf)), Math.Max(4, (int)(5 * _sf)));
                g.DrawString(pt, _fMicro, new SolidBrush(powC), lx + Math.Max(4, (int)(5 * _sf)) + 4, tY + (int)(3 * _sf));
                lx -= Math.Max(4, (int)(5 * _sf)) + 8;
            }
            string peakTxt = _col.PeakGpuTemp > -900f ? ("峰值 " + ((int)_col.PeakGpuTemp).ToString() + "°C") : "";
            if (peakTxt.Length > 0) { SizeF pks = g.MeasureString(peakTxt, _fMicro); lx -= pks.Width; g.DrawString(peakTxt, _fMicro, new SolidBrush(Color.FromArgb(200, Theme.Danger)), lx, tY + (int)(3 * _sf)); lx -= 14 * _sf; }
            string curTxt = (_cur.GpuTemp >= 0 ? ((int)_cur.GpuTemp).ToString() + "°C" : "-");
            SizeF cus = g.MeasureString(curTxt, _fMicro); lx -= cus.Width;
            g.FillEllipse(new SolidBrush(TempColor(_cur.GpuTemp)), lx, tY + (int)(6 * _sf), Math.Max(4, (int)(5 * _sf)), Math.Max(4, (int)(5 * _sf)));
            g.DrawString(curTxt, _fMicro, new SolidBrush(Theme.Main), lx + Math.Max(4, (int)(5 * _sf)) + 4, tY + (int)(3 * _sf));
            int gy2 = tY + _hLabel + (int)(8 * _sf), gh2 = (int)(58 * _sf), gx2 = x + _cPad, gw2 = _cardW - _cPad * 2;
            float wy = gy2 + gh2 * (1f - 85f / 95f);
            using (Pen wp = new Pen(Color.FromArgb(160, Theme.Danger)) { DashStyle = DashStyle.Dash }) g.DrawLine(wp, gx2, wy, gx2 + gw2, wy);
            using (Pen gp = new Pen(Theme.Grid)) { g.DrawLine(gp, gx2, gy2, gx2 + gw2, gy2); g.DrawLine(gp, gx2, gy2 + gh2, gx2 + gw2, gy2 + gh2); }
            Series(g, _col.History, delegate(Sample s) { return s.GpuOk && s.GpuTemp >= 0 ? s.GpuTemp : 0f; }, TempColor(_cur.GpuTemp), gx2, gy2, gw2, gh2, 95f, true);
            // 功耗曲线: 归一化到 TDP 上限百分比 (无上限时按 250W 折算), 与温度共用 0-95 刻度
            float powDenom = _col.GpuPowerLimitW > 0 ? (float)_col.GpuPowerLimitW : 250f;
            Series(g, _col.History, delegate(Sample s) { return s.GpuOk && s.GpuPower > 0 ? Math.Min(100f, s.GpuPower / powDenom * 100f) : 0f; }, powC, gx2, gy2, gw2, gh2, 95f, false);
            DrawEventLines(g, gx2, gy2, gw2, gh2);
        }

        // ---- CPU 卡片 ----
        void DrawCpuCard(Graphics g)
        {
            Card(g, _cpuY, _cpuH, "CPU  " + CleanCpuName(_col.CpuName), Theme.Cpu, _cur.CpuLoad, _cardW - _cPad * 2 - (int)(120 * _sf));
            int x = _padX, y = _cpuY;
            bool hasTemp = _cur.CpuTemp > -900f, hasPower = _cur.CpuPower > 0;
            string tag = hasTemp ? "LHM" : (_col.CpuTempAvailable ? "LHM 驱动受限" : "基础传感器");
            Color tagColor = hasTemp ? Theme.Gpu : Color.FromArgb(170, Theme.Sub);
            SizeF tw = g.MeasureString(tag, _fMicro); int bw2 = (int)(tw.Width + 14 * _sf);
            int badgeY = y + _cPad + (int)(3 * _sf), badgeH = _hTitle + (int)(6 * _sf);
            using (GraphicsPath bp = RoundRect(x + _cardW - _cPad - bw2, badgeY, bw2, badgeH, 5 * _sf))
            { g.FillPath(new SolidBrush(Color.FromArgb(hasTemp ? 48 : 30, tagColor)), bp); g.DrawString(tag, _fMicro, new SolidBrush(tagColor), x + _cardW - _cPad - bw2 + 7 * _sf, badgeY + (badgeH - _hMicro) / 2); }

            int dataY = y + _cPad + _rTitleRow + _cGap;
            BigPct(g, x + _cPad, dataY, _dispCpuLoad, Theme.Cpu);
            int col1 = MetricCol1X(x), col2 = MetricCol2X(x);
            Metric(g, col1, dataY, "温度", hasTemp ? ((int)_cur.CpuTemp).ToString() + " °C" : "N/A", hasTemp ? TempColor(_cur.CpuTemp) : Theme.Sub);
            Metric(g, col2, dataY, "核心频率", _cur.CpuClock > 0 ? ((int)_cur.CpuClock).ToString() + " MHz" : "-", Theme.Main);
            int rowGap = Math.Max(3, (int)(_cGap * 0.7f)), row2 = dataY + _rMetricRow + rowGap;
            Metric(g, col1, row2, "封装功耗", hasPower ? ((int)_cur.CpuPower).ToString() + " W" : "N/A", hasPower ? Theme.Main : Theme.Sub);
            bool hasCpuFan = _cur.CpuFanRpm > 0;
            if (hasCpuFan) Metric(g, col2, row2, "风扇", ((int)_cur.CpuFanRpm).ToString() + " RPM", Theme.Main);
            else if (_col.PeakCpuClock > 0) Metric(g, col2, row2, "峰值频率", ((int)_col.PeakCpuClock).ToString() + " MHz", Theme.Main);

            int chartTop = dataY + Math.Max(_rBigRow, _rMetricRow * 2 + rowGap) + _cGap;
            DrawCoreBars(g, x, chartTop);
            int hintY = y + _cpuH - _cPad - _hMicro;
            string hint = hasTemp ? "LHM 实时读数"
                : (_col.CpuTempAvailable
                    ? "频率实时读数 · 温度驱动被系统拦截 (HVCI)"
                    : "频率实时读数 · 温度需 LHM 组件");
            g.DrawString(hint, _fMicro, new SolidBrush(Color.FromArgb(150, Theme.Sub)), x + _cPad, hintY);
        }

        // ---- 8 核柱状图 (无序号) ----
        void DrawCoreBars(Graphics g, int x, int chartTop)
        {
            g.DrawString("核心负载", _fMicro, new SolidBrush(Theme.Sub), x + _cPad, chartTop);
            int numTop = chartTop + _hMicro + Math.Max(3, (int)(3 * _sf));
            int barZoneTop = numTop + _hMicro + Math.Max(2, (int)(2 * _sf));
            int barZoneH = (int)(40 * _sf);
            int cells = Math.Min(8, _col.CoreLoads.Length); if (cells <= 0) return;
            int gapC = Math.Max(4, (int)(7 * _sf));
            int bw = (_cardW - _cPad * 2 - gapC * (cells - 1)) / cells;
            for (int c = 0; c < cells; c++)
            {
                float load = _dispLoads[c]; if (load < 0f) load = 0f; if (load > 100f) load = 100f;
                int bx = x + _cPad + c * (bw + gapC);
                string num = ((int)Math.Round(load)).ToString();
                int barH = (int)(barZoneH * load / 100f);
                int numY = barZoneTop + (barZoneH - barH) - _hMicro - Math.Max(2, (int)(2 * _sf));
                using (StringFormat cf = new StringFormat() { Alignment = StringAlignment.Center })
                    g.DrawString(num, _fMicro, new SolidBrush(load > 85f ? Theme.Warn : Theme.Main), new RectangleF(bx, numY, bw, _hMicro), cf);
                if (barH >= 2) { int barTop = barZoneTop + barZoneH - barH; using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(bx, barTop, bw, barH), Color.FromArgb(120, CoreColor(load)), CoreColor(load), 90f)) g.FillRectangle(lb, bx, barTop, bw, barH); }
                else using (SolidBrush sb = new SolidBrush(Color.FromArgb(70, CoreColor(load)))) g.FillRectangle(sb, bx, barZoneTop + barZoneH - 2, bw, 2);
            }
        }

        static Color CoreColor(float p) { float n = Math.Max(0f, Math.Min(1f, p / 100f)); return FromHsl(120f * (1f - n) / 360f, 0.72f, 0.22f + 0.20f * n); }
        static Color FromHsl(float h, float s, float l) { float r, g2, b; if (s <= 0f) r = g2 = b = l; else { float q = l < 0.5f ? l * (1f + s) : l + s - l * s, p2 = 2f * l - q; r = Hue2Rgb(p2, q, h + 1f / 3f); g2 = Hue2Rgb(p2, q, h); b = Hue2Rgb(p2, q, h - 1f / 3f); } return Color.FromArgb(Math.Min(255, (int)(r * 255)), Math.Min(255, (int)(g2 * 255)), Math.Min(255, (int)(b * 255))); }
        static float Hue2Rgb(float p, float q, float t) { if (t < 0f) t += 1f; if (t > 1f) t -= 1f; if (t < 1f / 6f) return p + (q - p) * 6f * t; if (t < 1f / 2f) return q; if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f; return p; }

        // ---- 内存卡: Top10 进程 ----
        void DrawRamCard(Graphics g)
        {
            string subtitle = _col.RamSpeedMtS > 0 ? ("DDR4 " + (_col.RamSpeedMtS / 2).ToString() + " MHz") : "";
            Card(g, _ramY, _ramH, "内存  " + (_cur.MemTotalMB / 1024.0).ToString("0") + " GB  " + subtitle, Theme.Ram, _cur.MemLoad, _cardW - _cPad * 2);
            int x = _padX, y = _ramY;
            int dataY = y + _cPad + _rTitleRow + _cGap;
            BigPct(g, x + _cPad, dataY, _cur.MemLoad, Theme.Ram);
            int col1 = MetricCol1X(x);
            Metric(g, col1, dataY, "已用 / 总量", (_cur.MemUsedMB / 1024.0).ToString("0.0") + " / " + (_cur.MemTotalMB / 1024.0).ToString("0") + " GB", Theme.Main);
            int rowGap = Math.Max(3, (int)(_cGap * 0.7f));
            Metric(g, col1, dataY + _rMetricRow + rowGap, "等效频率", _col.RamSpeedMtS > 0 ? _col.RamSpeedMtS.ToString() + " MT/s" : "-", Theme.Main);

            int labelY = dataY + Math.Max(_rBigRow, _rMetricRow * 2 + rowGap) + _cGap;
            g.DrawString("内存占用 Top 10", _fLabel, new SolidBrush(Theme.Main), x + _cPad, labelY);
            int listY = labelY + _hLabel + (int)(4 * _sf);
            DrawMemApps(g, x + _cPad, listY, _cardW - _cPad * 2);
        }

        void DrawMemApps(Graphics g, int x, int y, int w)
        {
            if (_dispProcs.Count == 0) { g.DrawString("采样中...", _fMicro, new SolidBrush(Theme.Sub), x, y); return; }
            float maxMem = 0;
            for (int i = 0; i < _dispProcs.Count; i++) if (_dispProcs[i].DispMem > maxMem) maxMem = _dispProcs[i].DispMem;
            if (maxMem <= 0) return;
            int rowH = Math.Max(14, (int)(16 * _sf));
            int iconSz = (int)(14 * _sf);
            int nameW = (int)(100 * _sf);
            int valW = (int)(55 * _sf);
            int barH = Math.Max(3, (int)(5 * _sf));
            int barX = x + iconSz + (int)(6 * _sf) + nameW + (int)(6 * _sf);
            int barW = w - (iconSz + (int)(6 * _sf) + nameW + (int)(6 * _sf) + valW + (int)(6 * _sf));
            if (barW < 20) barW = 20;
            // 裁剪到列表区域, 滑动条目不会溢出卡片
            GraphicsState clipState = g.Save();
            g.SetClip(new Rectangle(x - 2, y - 2, w + 4, 10 * rowH + 4));
            for (int i = 0; i < _dispProcs.Count; i++)
            {
                DispProcEntry dp = _dispProcs[i];
                if (dp.DispPos >= 9.98f) continue;
                int ry = y + (int)(dp.DispPos * rowH);
                if (dp.Icon != null) { try { g.DrawImage(dp.Icon, x, ry + (rowH - iconSz) / 2, iconSz, iconSz); } catch { } }
                int textY = ry + (rowH - _hMicro) / 2;
                string name = FitString(g, dp.Name, _fMicro, nameW);
                g.DrawString(name, _fMicro, new SolidBrush(Theme.Sub), x + iconSz + (int)(6 * _sf), textY);
                float pct = dp.DispMem / maxMem * 100f; Color c = LoadColor(pct);
                using (GraphicsPath slot = RoundRect(barX, ry + (rowH - barH) / 2, barW, barH, barH / 2f)) g.FillPath(new SolidBrush(Color.FromArgb(235, 29, 37, 50)), slot);
                int fw = (int)(barW * Math.Max(0f, Math.Min(100f, pct)) / 100f);
                if (fw > barH) using (GraphicsPath fill = RoundRect(barX, ry + (rowH - barH) / 2, fw, barH, barH / 2f))
                    using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(barX, ry + (rowH - barH) / 2, fw, barH), Color.FromArgb(150, c), c, 0f)) g.FillPath(lg, fill);
                string val = dp.ShowMem >= 1024 ? (dp.ShowMem / 1024.0).ToString("0.0") + " GB" : dp.ShowMem.ToString() + " MB";
                SizeF vs = g.MeasureString(val, _fMicro);
                g.DrawString(val, _fMicro, new SolidBrush(Theme.Main), x + w - vs.Width, textY);
            }
            g.Restore(clipState);
        }

        // ---- 折线图 ----
        void ChartFrame(Graphics g, int y, int h, string title, string extra, Color extraColor)
        {
            int x = _padX, w = _cardW;
            DrawCardGlow(g, x, y, w, h, 0);
            using (GraphicsPath p = RoundRect(x, y, w, h, 12f * _sf)) { g.FillPath(new SolidBrush(Theme.Card), p); using (Pen pe = new Pen(Theme.Edge)) g.DrawPath(pe, p); }
            g.DrawString(title, _fLabel, new SolidBrush(Theme.Main), x + _cPad, y + _cPad + (int)(2 * _sf));
        }
        int ChartGy(int cardY) { return cardY + _cPad + _hLabel + Math.Max(6, (int)(8 * _sf)); }
        int ChartGh(int cardH) { return cardH - _cPad - _hLabel - Math.Max(6, (int)(8 * _sf)) - (int)(10 * _sf); }
        int ChartGx() { return _padX + _cPad; }
        int ChartGw() { return _cardW - _cPad * 2; }

        void DrawGrid(Graphics g, int gy, int gh, float yMax, float[] gridLines)
        {
            int gx = ChartGx(), gw = ChartGw();
            for (int i = 0; i < gridLines.Length; i++) { float ly = gy + gh * (1f - gridLines[i] / yMax); using (Pen gp = new Pen(Theme.Grid)) g.DrawLine(gp, gx, ly, gx + gw, ly); }
            using (Pen vp = new Pen(Theme.Grid)) { int step = Math.Max(15, _chartRangeSec / 4); for (int sec = step; sec < _chartRangeSec; sec += step) { float px = gx + gw * (1f - (float)sec / _chartRangeSec); g.DrawLine(vp, px, gy, px, gy + gh); } }
        }

        internal delegate float FuncValue(Sample s);

        void Series(Graphics g, List<Sample> hist, FuncValue getter, Color c, int gx, int gy, int gw, int gh, float yMax, bool fill)
        {
            if (hist.Count < 2) return;
            List<PointF> pts = new List<PointF>(hist.Count); DateTime nowT = DateTime.Now;
            lock (hist) { for (int i = 0; i < hist.Count; i++) { Sample s = hist[i]; double age = (nowT - s.T).TotalSeconds; if (age > _chartRangeSec) continue; float px = gx + gw * (1f - (float)age / _chartRangeSec); float v = getter(s); if (v < -900f) v = 0f; float py = gy + gh * (1f - Math.Max(0f, Math.Min(yMax, v)) / yMax); pts.Add(new PointF(px, py)); } }
            if (pts.Count < 2) return;
            using (GraphicsPath lp = new GraphicsPath()) { lp.AddLines(pts.ToArray()); if (fill) { using (GraphicsPath fp = (GraphicsPath)lp.Clone()) { fp.AddLine(pts[pts.Count - 1].X, gy + gh, pts[0].X, gy + gh); fp.CloseFigure(); using (LinearGradientBrush fb = new LinearGradientBrush(new Rectangle(gx, gy, gw, gh), Color.FromArgb(52, c), Color.FromArgb(0, c), 90f)) g.FillPath(fb, fp); } } using (Pen pen = new Pen(c, Math.Max(1.2f, 1.8f * _sf))) { pen.LineJoin = LineJoin.Round; pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawPath(pen, lp); } }
            PointF last = pts[pts.Count - 1]; float r = 2.5f * _sf; using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, last.X - r, last.Y - r, r * 2, r * 2);
        }

        void DrawLoadChart(Graphics g)
        {
            int gx = ChartGx(), gw = ChartGw(), gy = ChartGy(_loadY), gh = ChartGh(_loadH);
            ChartFrame(g, _loadY, _loadH, "负载曲线", null, Theme.Main);
            // 时间范围按钮: 紧跟标题文字后面 (左侧)
            SizeF titleSz = g.MeasureString("负载曲线", _fLabel);
            int rx = _padX + _cPad + (int)titleSz.Width + (int)(10 * _sf);
            int ry = _loadY + _cPad + (int)(3 * _sf);
            int btnH = _hLabel, gap = (int)(3 * _sf);
            // 按钮宽度按最长标签实测自适应 (防止 "10m" 触发换行被裁)
            int btnW = (int)(34 * _sf);
            for (int i = 0; i < _rangeLabels.Length; i++)
            { int w = (int)g.MeasureString(_rangeLabels[i], _fMicro).Width + (int)(14 * _sf); if (w > btnW) btnW = w; }
            for (int i = 0; i < _rangeLabels.Length; i++)
            {
                _rangeBtnRects[i] = new Rectangle(rx, ry, btnW, btnH);
                bool active = _chartRangeSec == _rangeValues[i];
                bool hover = _hoverRange && _rangeBtnRects[i].Contains(PointToClient(Cursor.Position));
                Color bg = active ? Color.FromArgb(60, Theme.Gpu) : (hover ? Color.FromArgb(35, 255, 255, 255) : Color.FromArgb(15, 255, 255, 255));
                using (GraphicsPath bp = RoundRect(rx, ry, btnW, btnH, 4 * _sf)) using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, bp);
                Color tc = active ? Theme.Gpu : (hover ? Theme.Main : Theme.Sub);
                using (StringFormat sf2 = new StringFormat())
                { sf2.Alignment = StringAlignment.Center; sf2.LineAlignment = StringAlignment.Center; sf2.FormatFlags = StringFormatFlags.NoWrap; sf2.Trimming = StringTrimming.None; g.DrawString(_rangeLabels[i], _fMicro, new SolidBrush(tc), new RectangleF(rx, ry, btnW, btnH), sf2); }
                rx += btnW + gap;
            }
            DrawGrid(g, gy, gh, 100f, new float[] { 25f, 50f, 75f });
            string l1 = "GPU " + (_cur.GpuOk ? ((int)_cur.GpuLoad).ToString() + "%" : "-"), l2 = "CPU " + ((int)_cur.CpuLoad).ToString() + "%", l3 = "内存 " + ((int)_cur.MemLoad).ToString() + "%";
            DrawLegend(g, _loadY, new string[] { l1, l2, l3 }, new Color[] { Theme.Gpu, Theme.Cpu, Theme.Ram });
            Series(g, _col.History, delegate(Sample s) { return s.MemLoad; }, Theme.Ram, gx, gy, gw, gh, 100f, false);
            Series(g, _col.History, delegate(Sample s) { return s.CpuLoad; }, Theme.Cpu, gx, gy, gw, gh, 100f, false);
            Series(g, _col.History, delegate(Sample s) { return s.GpuOk ? s.GpuLoad : 0f; }, Theme.Gpu, gx, gy, gw, gh, 100f, true);
            DrawEventLines(g, gx, gy, gw, gh);
        }

        void DrawLegend(Graphics g, int cardY, string[] labels, Color[] colors)
        {
            float dot = Math.Max(4f, 5f * _sf), cy = cardY + _cPad + (int)(6 * _sf), lx = _padX + _cardW - _cPad;
            for (int i = labels.Length - 1; i >= 0; i--) { SizeF s = g.MeasureString(labels[i], _fMicro); lx -= s.Width; g.DrawString(labels[i], _fMicro, new SolidBrush(Theme.Main), lx, cy - s.Height / 2); lx -= dot + 5; g.FillEllipse(new SolidBrush(colors[i]), lx, cy - dot / 2, dot, dot); lx -= 10; }
        }

        void DrawEventLines(Graphics g, int gx, int gy, int gw, int gh)
        {
            DateTime nowT = DateTime.Now; List<MonEvent> evs = _col.Events;
            lock (evs) { for (int i = 0; i < evs.Count; i++) { MonEvent ev = evs[i]; if (ev.Kind == 2 || ev.Kind == 3) continue; double age = (nowT - ev.T).TotalSeconds; if (age < 0 || age > _chartRangeSec) continue; float px = gx + gw * (1f - (float)age / _chartRangeSec); Color c = ev.Kind == 0 ? Theme.Danger : Theme.Warn; using (Pen vp = new Pen(Color.FromArgb(120, c)) { DashStyle = DashStyle.Dot }) g.DrawLine(vp, px, gy, px, gy + gh); float ts = 4f * _sf; using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, new PointF[] { new PointF(px - ts, gy), new PointF(px + ts, gy), new PointF(px, gy + ts * 1.5f) }); } }
        }

        // ---- 事件卡: 1 行 + 点击弹窗 ----
        void DrawEvents(Graphics g)
        {
            Card(g, _evtY, _evtH, "事件记录", Theme.Danger, 0, _cardW - _cPad * 2 - (int)(150 * _sf));
            DrawEvtLegend(g);
            int x = _padX, y = _evtY;
            List<MonEvent> evs = _col.Events;
            int listTop = y + _cPad + _rTitleRow + (int)(4 * _sf);
            if (evs.Count > 0)
            {
                MonEvent ev = evs[evs.Count - 1];
                Color dotC = ev.Hw == 0 ? Theme.Gpu : (ev.Hw == 1 ? Theme.Cpu : (ev.Hw == 2 ? Theme.Ram : Theme.Sub));
                float dot = Math.Max(4f, 5f * _sf); bool serious = ev.Kind == 0 || ev.Kind == 1;
                if (serious) { float ring = dot + Math.Max(3f, 4f * _sf); using (SolidBrush ob = new SolidBrush(Theme.Danger)) g.FillEllipse(ob, x + _cPad + dot / 2 - ring / 2, listTop + _rEvtRow / 2 - ring / 2, ring, ring); }
                g.FillEllipse(new SolidBrush(dotC), x + _cPad, listTop + _rEvtRow / 2 - dot / 2, dot, dot);
                Color tc = ev.Kind == 0 ? Theme.Danger : (ev.Kind == 1 ? Theme.Warn : Theme.Main);
                g.DrawString(ev.T.ToString("HH:mm:ss") + "  " + ev.Msg, _fTiny, new SolidBrush(tc), x + _cPad + (int)(12 * _sf), listTop);
            }
            else g.DrawString("—— 一切正常 ——", _fTiny, new SolidBrush(Color.FromArgb(140, Theme.Sub)), x + _cPad, listTop + _rEvtRow / 2 - _hTiny / 2);
            string hint = "点击查看详情 (" + evs.Count + ")";
            SizeF hs = g.MeasureString(hint, _fMicro);
            g.DrawString(hint, _fMicro, new SolidBrush(Color.FromArgb(100, Theme.Sub)), x + _cardW - _cPad - hs.Width, listTop + _rEvtRow / 2 - _hMicro / 2);
        }

        void DrawEvtLegend(Graphics g)
        {
            float dot = Math.Max(4f, 5f * _sf), cy = _evtY + _cPad + (int)(3 * _sf) + _hTitle / 2, lx = _padX + _cardW - _cPad;
            string[] names = { "GPU", "CPU", "内存" }; Color[] cs = { Theme.Gpu, Theme.Cpu, Theme.Ram };
            for (int i = names.Length - 1; i >= 0; i--) { SizeF s = g.MeasureString(names[i], _fMicro); lx -= s.Width; g.DrawString(names[i], _fMicro, new SolidBrush(Theme.Sub), lx, cy - s.Height / 2); lx -= dot + 4f; g.FillEllipse(new SolidBrush(cs[i]), lx, cy - dot / 2, dot, dot); lx -= 8f * _sf; }
        }

        void DrawFoot(Graphics g)
        {
            int ty = _footY + (int)(7 * _sf);
            string self = string.Format("CPU {0:0.0}%  内存 {1}MB", _cur.SelfCpu, _cur.SelfMemMB);
            g.DrawString(self, _fMicro, new SolidBrush(Theme.Sub), _padX, ty);
            // 右侧: 游戏识别 + 时长 (无游戏时显示会话时长)
            string right; Color rc;
            if (_col.GameRunning)
            {
                TimeSpan d = DateTime.Now - _col.GameStart; if (d.TotalSeconds < 0) d = TimeSpan.Zero;
                right = TitleCase(_col.GameName) + " · " + Collector.FormatDur(d);
                rc = Theme.Gpu;
            }
            else
            {
                TimeSpan d = DateTime.Now - _sessionStart;
                right = "会话 " + Collector.FormatDur(d);
                rc = Color.FromArgb(150, Theme.Sub);
            }
            SizeF rs = g.MeasureString(right, _fMicro);
            g.DrawString(right, _fMicro, new SolidBrush(rc), ClientSize.Width - _padX - rs.Width, ty);
        }

        static string TitleCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length == 1) return s.ToUpperInvariant();
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        static string CleanCpuName(string s) { if (string.IsNullOrEmpty(s)) return "CPU"; s = s.Replace("with Radeon Graphics", ""); s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\d+-Core", ""); s = s.Replace(" Processor", "").Replace("AMD ", ""); while (s.IndexOf("  ") >= 0) s = s.Replace("  ", " "); return s.Trim(); }

        internal static GraphicsPath RoundRect(int x, int y, int w, int h, float r) { GraphicsPath p = new GraphicsPath(); float rr = Math.Max(2f, r), d = rr * 2; if (w < d) d = w; if (h < d) d = h; p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90); p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90); p.CloseFigure(); return p; }

        // ================= 交互 =================
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                if (_btnMin.Contains(e.Location)) { Visible = false; return; }
                if (_btnFull.Contains(e.Location)) { ToggleFullscreen(); return; }
                if (_btnClose.Contains(e.Location)) { Close(); return; }
                for (int i = 0; i < _rangeBtnRects.Length; i++) if (_rangeBtnRects[i].Contains(e.Location)) { _chartRangeSec = _rangeValues[i]; Invalidate(); _saveTimer.Stop(); _saveTimer.Start(); return; }
                if (e.Y >= _evtY && e.Y < _evtY + _evtH) { ShowEventPopup(); return; }
                if (e.Y < _titleBarH && !_clickThrough && !_fullscreen) { Native.ReleaseCapture(); Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero); }
            }
            if (e.Button == MouseButtons.Right && !_clickThrough) { Point pt = PointToScreen(e.Location); _menu.Show(pt); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hMin = _btnMin.Contains(e.Location), hFull = _btnFull.Contains(e.Location), hClose = _btnClose.Contains(e.Location);
            bool hRange = false; for (int i = 0; i < _rangeBtnRects.Length; i++) if (_rangeBtnRects[i].Contains(e.Location)) { hRange = true; break; }
            bool hEvt = e.Y >= _evtY && e.Y < _evtY + _evtH;
            if (hMin != _hoverMin || hFull != _hoverFull || hClose != _hoverClose || hRange != _hoverRange)
            { _hoverMin = hMin; _hoverFull = hFull; _hoverClose = hClose; _hoverRange = hRange; Invalidate(new Rectangle(0, 0, ClientSize.Width, _titleBarH)); Invalidate(new Rectangle(0, _loadY, ClientSize.Width, _loadH)); }
            Cursor = (hMin || hFull || hClose || hRange || hEvt) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Right && !_clickThrough) _menu.Show(PointToScreen(e.Location)); }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            Point p = PointToClient(Cursor.Position);
            if (p.Y < _titleBarH && p.X < _btnMin.X - (int)(4 * _sf)) ToggleFullscreen();
        }

        void ToggleFullscreen()
        {
            if (_fullscreen) { if (_savedBounds.Width > 0) Bounds = _savedBounds; _fullscreen = false; SaveConfig(); }
            else { _savedBounds = new Rectangle(Left, Top, Width, Height); Screen scr = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2)); Bounds = scr.Bounds; _fullscreen = true; }
            RebuildMetrics(); Invalidate();
        }

        void ShowEventPopup()
        {
            if (_evtPopup != null) { try { _evtPopup.Close(); } catch { } _evtPopup = null; }
            // 弹窗往上展示 (避免超出屏幕底部)
            int popupH = (int)(400 * _sf);
            int popupW = (int)(440 * _sf);
            Point screenPt = PointToScreen(new Point(_padX, _evtY));
            int px = screenPt.X;
            int py = screenPt.Y - popupH;
            if (py < 0) py = 0;
            // 右侧不超出屏幕
            Screen scr = Screen.FromPoint(screenPt);
            if (px + popupW > scr.Bounds.Right) px = scr.Bounds.Right - popupW;
            _evtPopup = new EventPopup(_col, _sf, new Point(px, py));
            _evtPopup.FormClosed += delegate { _evtPopup = null; };
            _evtPopup.Show();
        }

        void BuildTray()
        {
            _menu = new ContextMenuStrip();
            _menu.Items.Add("导出性能报告 (CSV)", null, delegate { ExportReport(); });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("切换点击穿透", null, delegate { ToggleThrough(); });
            _menu.Items.Add("重新定位到副屏", null, delegate { LocateSecondary(); SaveConfig(); });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("退出", null, delegate { Close(); });
            _tray = new NotifyIcon(); _tray.Text = "游戏性能监控"; _tray.Icon = BuildIcon(); _tray.ContextMenuStrip = _menu; _tray.Visible = true;
            _tray.DoubleClick += delegate { Visible = !Visible; };
        }

        void ExportReport()
        {
            string p = Collector.ExportCsv(_col);
            try { _tray.ShowBalloonTip(4000, "性能报告", p != null ? "已导出到:\n" + p : "导出失败", p != null ? ToolTipIcon.Info : ToolTipIcon.Error); } catch { }
        }

        void ToggleThrough() { _clickThrough = !_clickThrough; ApplyClickThrough(); SaveConfig(); try { _tray.ShowBalloonTip(2000, "点击穿透" + (_clickThrough ? "已开启" : "已关闭"), _clickThrough ? "窗口不接收鼠标" : "可拖动/缩放", ToolTipIcon.Info); } catch { } Invalidate(); }
        void ApplyClickThrough() { if (!IsHandleCreated) return; int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE); if (_clickThrough) ex |= Native.WS_EX_TRANSPARENT; else ex &= ~Native.WS_EX_TRANSPARENT; Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex); }

        static Icon BuildIcon()
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp)) { g.Clear(Color.FromArgb(10, 13, 18)); using (SolidBrush b = new SolidBrush(Theme.Gpu)) g.FillEllipse(b, 3, 9, 4, 4); using (Pen p = new Pen(Theme.Gpu, 1.6f)) { g.DrawLine(p, 2, 12, 5, 7); g.DrawLine(p, 5, 7, 8, 9); g.DrawLine(p, 8, 9, 12, 3); } }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        void LocateSecondary() { Screen[] all = Screen.AllScreens; Screen best = null; for (int i = 0; i < all.Length; i++) if (!all[i].Primary) { best = all[i]; break; } if (best == null) best = Screen.PrimaryScreen; Rectangle b = best.Bounds; Left = b.Right - ClientSize.Width - (int)(40 * _sf); Top = b.Top + Math.Max(20, (b.Height - ClientSize.Height) / 4); }
        bool ScreenConfigured() { return _cfgHasPos; } bool _cfgHasPos;

        void SaveConfig() { if (_fullscreen) return; try { File.WriteAllLines(_cfgPath, new string[] { "left=" + Left, "top=" + Top, "w=" + ClientSize.Width, "h=" + ClientSize.Height, "through=" + (_clickThrough ? "1" : "0"), "range=" + _chartRangeSec }); } catch { } }

        void LoadConfig()
        {
            try { if (!File.Exists(_cfgPath)) return; string[] lines = File.ReadAllLines(_cfgPath); int l = -99999, t = -99999, w = 0, h = 0; for (int i = 0; i < lines.Length; i++) { if (lines[i].StartsWith("left=")) int.TryParse(lines[i].Substring(5), out l); if (lines[i].StartsWith("top=")) int.TryParse(lines[i].Substring(4), out t); if (lines[i].StartsWith("w=")) int.TryParse(lines[i].Substring(2), out w); if (lines[i].StartsWith("h=")) int.TryParse(lines[i].Substring(2), out h); if (lines[i].StartsWith("through=1")) _clickThrough = true; if (lines[i].StartsWith("range=")) { int r; if (int.TryParse(lines[i].Substring(6), out r)) _chartRangeSec = r; } } if (l > -99990 && t > -99990) { Left = l; Top = t; _cfgHasPos = true; if (w >= MinimumSize.Width && h >= MinimumSize.Height) ClientSize = new Size(w, h); bool onScreen = false; for (int i = 0; i < Screen.AllScreens.Length; i++) if (Screen.AllScreens[i].Bounds.IntersectsWith(new Rectangle(l, t, 60, 60))) { onScreen = true; break; } if (!onScreen) _cfgHasPos = false; } } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { base.OnFormClosing(e); SaveConfig(); if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } if (_col != null) { _col.Dispose(); _col = null; } DisposeFonts(); }
    }
}
