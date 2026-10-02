// Collector.cs - 统一采样器: 每秒一次, 组合 NVML + 内核 API + 可选 LHM, 附带降频事件检测
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace GameMonitor
{
    internal sealed class Sample
    {
        public DateTime T;
        public float CpuLoad;        // %
        public float CpuClock;        // MHz (无 LHM 时为估算)
        public float CpuTemp = -999;  // °C (不可用=-999)
        public float CpuPower = -1;   // W
        public float MemLoad;         // %
        public long MemUsedMB, MemTotalMB;
        public float RamClock = -1;   // MHz
        public bool GpuOk;
        public float GpuLoad;          // %
        public float GpuTemp = -999;
        public float GpuClock;         // MHz
        public long GpuMemUsedMB, GpuMemTotalMB;
        public float GpuPower = -1;
        public int GpuPState = -99;
        public ulong ThrottleBits;
        public int GpuFanPct = -1;   // GPU 风扇 % (-1=不可用)
        public float CpuFanRpm = -1; // CPU 风扇 RPM (-1=不可用)
        public int MemUtilPct = -1;  // 显存带宽利用率 % (-1=不可用)
        public float SelfCpu;          // 程序自身 CPU %
        public long SelfMemMB;         // 程序自身内存
    }

    internal sealed class MonEvent
    {
        public DateTime T;
        public int Kind;       // 0=降频 1=高温 2=恢复 3=信息 4=满载 5=占用高
        public int Hw;         // 0=GPU 1=CPU 2=内存 3=系统
        public string Msg;
    }

    internal sealed class ProcInfo
    {
        public string Name;
        public int Pid;          // 稳定标识 (同名多进程区分)
        public long MemoryMB;
        public Bitmap Icon;
    }

    internal sealed class Collector : IDisposable
    {
        public readonly List<Sample> History = new List<Sample>(640);
        public readonly List<MonEvent> Events = new List<MonEvent>();
        public const int HistCap = 620;      // 620 秒窗口 (支撑 10m 曲线 + 余量)

        Nvml _nvml;
        LhmProbe _lhm;
        GpuSample _gpu = new GpuSample();

        long _idle, _krnl, _usr;
        bool _cpuBaseSet;
        float _baseMHz = 3400f;
        ulong _lastThrottle;
        bool _hotLatched;
        bool _vramLatched;
        bool _powerLatched;
        bool _cpuHotLatched;
        bool _cpuMaxLatched;
        DateTime _cpuMaxSince = DateTime.MinValue;
        bool _memLatched;
        DateTime _lastSelfT;
        TimeSpan _lastSelfCpu;
        public float SelfCpuPct;
        public long SelfMemMB;
        Process _self;

        public string GpuName = "GPU";
        public int GpuMaxClockMHz;
        public int RamSpeedMtS;      // 标称内存频率 (WMI 一次)
        public string CpuName = "CPU";

        // ---- 每物理核负载 (Zen 拓扑: 逻辑 i 与 i+半数 同物理核) ----
        public const int PhysCores = 8;
        public readonly float[] CoreLoads = new float[PhysCores];
        IntPtr _lpBuf;
        int _lpCount;
        long[] _lpIdle, _lpTotal;
        bool _lpInit;

        public List<ProcInfo> TopProcs = new List<ProcInfo>(10);
        static Dictionary<string, Bitmap> _iconCache = new Dictionary<string, Bitmap>();
        static Dictionary<int, string> _pathCache = new Dictionary<int, string>();
        int _procTick;

        public bool GpuAvailable { get { return _nvml != null; } }
        public bool CpuTempAvailable;
        public bool RealClockAvailable;   // CPU 真实频率可用 (WMI 性能计数器)

        // ---- CPU 真实频率: % Processor Performance x 基频 (用户态 WMI, 无需内核驱动) ----
        float _realCpuMHz;
        int _pppTick;

        // ---- 会话峰值 (程序启动以来) ----
        public float PeakGpuTemp = -999f, PeakGpuLoad = -1f, PeakCpuLoad = -1f, PeakCpuTemp = -999f, PeakMemLoad = -1f;
        public float PeakCpuClock = 0f;   // CPU 峰值频率 (会话内最高)
        public float PeakGpuPower = -1f;  // GPU 峰值功耗 W
        public int GpuPowerLimitW;

        // ---- 游戏识别 (进程名匹配库, 在已有的全进程枚举中零开销顺带检测) ----
        public bool GameRunning;
        public string GameName;
        public DateTime GameStart = DateTime.MinValue;
        static readonly HashSet<string> GameNames = new HashSet<string>
        {
            // 魔兽世界系
            "wow", "wowclassic", "wowclassicera", "wowclassicera_a", "wowclassic_beta", "wowt", "wowb",
            // 常见网游
            "dota2", "cs2", "csgo", "valorant", "league of legends", "leagueclient", "tslgame", "arma3", "dayz", "crossfire",
            // 单机大作
            "eldenring", "cyberpunk2077", "baldursgate3", "helldivers2", "palworld", "rdr2", "gta5",
            // 米哈游/二次元
            "genshinimpact", "starrail", "zenlesszonezero", "hd-player", "dnf", "cf"
        };

        // 每秒采一次 16 线程 -> 8 物理核 (取两 SMT 线程最大值, 反映单核满载状态)
        void SampleCoreLoads()
        {
            try
            {
                if (_lpBuf == IntPtr.Zero)
                {
                    _lpCount = Environment.ProcessorCount;
                    _lpBuf = System.Runtime.InteropServices.Marshal.AllocHGlobal(_lpCount * 48 + 64);
                    _lpIdle = new long[_lpCount];
                    _lpTotal = new long[_lpCount];
                }
                int retLen;
                // SystemProcessorPerformanceInformation = 8, x64 每项 48 字节; Win11 要求长度精确 = 线程数 x 48
                int reqLen = _lpCount * 48;
                if (Native.NtQuerySystemInformation(8, _lpBuf, reqLen, out retLen) != 0) return;
                int half = _lpCount / 2;
                if (half < 1) half = 1;
                if (_lpInit) { for (int c = 0; c < PhysCores; c++) CoreLoads[c] = 0f; }  // 清零后做 max 聚合
                for (int i = 0; i < _lpCount; i++)
                {
                    long idle = System.Runtime.InteropServices.Marshal.ReadInt64(_lpBuf, i * 48);
                    long krnl = System.Runtime.InteropServices.Marshal.ReadInt64(_lpBuf, i * 48 + 8);
                    long usr = System.Runtime.InteropServices.Marshal.ReadInt64(_lpBuf, i * 48 + 16);
                    long total = krnl + usr;
                    if (_lpInit)
                    {
                        long dI = idle - _lpIdle[i]; long dT = total - _lpTotal[i];
                        float load = dT > 0 ? (1f - (float)dI / dT) * 100f : 0f;
                        if (load < 0f) load = 0f; if (load > 100f) load = 100f;
                        int phys = i % half;   // Zen: LP 0..N-1 = 各核线程0, LP N..2N-1 = 各核线程1
                        if (load > CoreLoads[phys]) CoreLoads[phys] = load;
                    }
                    _lpIdle[i] = idle; _lpTotal[i] = total;
                }
                _lpInit = true;
            }
            catch { }
        }

        public void Start()
        {
            _nvml = new Nvml();
            if (!_nvml.TryInit()) { _nvml.Dispose(); _nvml = null; }
            else { GpuName = _nvml.Name; GpuMaxClockMHz = _nvml.MaxSmClockMHz; GpuPowerLimitW = _nvml.PowerLimitW; }

            // CPU 名称 / 基频 / 内存标称频率 (仅启动时读一次 WMI)
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher("SELECT Name,MaxClockSpeed FROM Win32_Processor"))
                    foreach (System.Management.ManagementBaseObject o in s.Get())
                    { CpuName = Convert.ToString(o["Name"]).Trim(); _baseMHz = Convert.ToSingle(o["MaxClockSpeed"]); break; }
            }
            catch { }
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher("SELECT Speed FROM Win32_PhysicalMemory"))
                    foreach (System.Management.ManagementBaseObject o in s.Get())
                    { int sp = Convert.ToInt32(o["Speed"]); if (sp > RamSpeedMtS) RamSpeedMtS = sp; break; }
            }
            catch { }

            // 可选: LibreHardwareMonitor (CPU 温度/功耗/真实频率)
            string dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LibreHardwareMonitorLib.dll");
            _lhm = new LhmProbe();
            if (_lhm.TryInit(dll)) CpuTempAvailable = true;

            _self = Process.GetCurrentProcess();
            try { _lastSelfCpu = _self.TotalProcessorTime; _lastSelfT = DateTime.UtcNow; } catch { }

            ReadCpuPerf();   // 预热 WMI 性能计数器 (首次查询较慢, 避免首秒空窗)
        }

        // 读取 Windows 官方口径的 CPU 性能百分比 (含 boost), 实际频率 = 基频 x PPP%
        // 用户态 WMI 通道, 不依赖被 HVCI 拦截的内核传感器驱动
        void ReadCpuPerf()
        {
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher("SELECT PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'"))
                    foreach (System.Management.ManagementBaseObject o in s.Get())
                    {
                        uint ppp = Convert.ToUInt32(o["PercentProcessorPerformance"]);
                        if (ppp > 0 && ppp < 1000) { _realCpuMHz = _baseMHz * ppp / 100f; RealClockAvailable = true; }
                        break;
                    }
            }
            catch { }
        }

        static void PushEvent(List<MonEvent> list, DateTime t, int kind, int hw, string msg)
        {
            MonEvent e = new MonEvent(); e.T = t; e.Kind = kind; e.Hw = hw; e.Msg = msg;
            list.Add(e);
            while (list.Count > 80) list.RemoveAt(0);
        }

        // 降频原因位 -> 中文描述 (NVML_CLOCKS_THROTTLE_REASON_*)
        static string ThrottleText(ulong bits, out bool serious)
        {
            serious = false;
            if (bits == 0) return null;
            List<string> parts = new List<string>();
            if ((bits & 0x8) != 0) { parts.Add("功耗墙"); serious = true; }
            if ((bits & 0x10) != 0) { parts.Add("硬件降速"); serious = true; }
            if ((bits & 0x40) != 0) { parts.Add("软温控"); serious = true; }
            if ((bits & 0x80) != 0) { parts.Add("硬温控"); serious = true; }
            if ((bits & 0x100) != 0) { parts.Add("功耗断路"); serious = true; }
            if ((bits & 0x2) != 0) parts.Add("应用限频");
            if ((bits & 0x4) != 0) parts.Add("自定义限频");
            if ((bits & 0x1) != 0) parts.Add("空闲节能");
            if ((bits & 0x20) != 0) parts.Add("同步加速");
            if ((bits & 0x400) != 0) parts.Add("空闲节能");
            if (parts.Count == 0) parts.Add("0x" + bits.ToString("X"));
            return string.Join("+", parts);
        }

        public Sample CollectOnce()
        {
            Sample s = new Sample();
            s.T = DateTime.Now;

            // ---- CPU 负载 (GetSystemTimes) + 每核负载 ----
            SampleCoreLoads();
            long idle, krnl, usr;
            Native.GetSystemTimes(out idle, out krnl, out usr);
            if (_cpuBaseSet)
            {
                long dSys = (krnl - _krnl) + (usr - _usr);
                long dIdle = idle - _idle;
                if (dSys > 0) s.CpuLoad = Math.Max(0f, Math.Min(100f, (1f - (float)dIdle / dSys) * 100f));
            }
            _idle = idle; _krnl = krnl; _usr = usr; _cpuBaseSet = true;

            // ---- 内存 ----
            Native.MEMORYSTATUSEX mem = new Native.MEMORYSTATUSEX();
            mem.dwLength = 64;
            Native.GlobalMemoryStatusEx(ref mem);
            s.MemLoad = mem.dwMemoryLoad;
            s.MemTotalMB = (long)(mem.ullTotalPhys / (1024 * 1024));
            s.MemUsedMB = (long)((mem.ullTotalPhys - mem.ullAvailPhys) / (1024 * 1024));

            // ---- GPU (NVML) ----
            if (_nvml != null && _nvml.Read(_gpu))
            {
                s.GpuOk = true;
                s.GpuLoad = _gpu.LoadPct;
                s.GpuTemp = _gpu.TempC;
                s.GpuClock = _gpu.SmClockMHz;
                s.GpuMemUsedMB = _gpu.MemUsedMB;
                s.GpuMemTotalMB = _gpu.MemTotalMB;
                s.GpuPower = _gpu.PowerW;
                s.GpuPState = _gpu.PState;
                s.ThrottleBits = _gpu.ThrottleBits;
                s.GpuFanPct = _gpu.FanSpeedPct;
                s.MemUtilPct = _gpu.MemUtilPct;
            }

            // ---- CPU 温度/功耗/真实频率/风扇 (可选 LHM) ----
            if (_lhm != null && _lhm.Active)
            {
                float ct = -999f, cp = -1f, cc = 0f, rc = -1f, cf = -1f;
                _lhm.Poll(ref ct, ref cp, ref cc, ref rc, ref cf);
                if (ct > -900f) s.CpuTemp = ct;
                if (cp > 0) s.CpuPower = cp;
                if (cc > 0) s.CpuClock = cc;
                if (rc > 0) s.RamClock = rc;
                if (cf > 0) s.CpuFanRpm = cf;
            }
            // ---- CPU 真实频率 (每 2 秒读一次 WMI 性能计数器, 平滑使用) ----
            _pppTick++;
            if (_pppTick >= 2) { _pppTick = 0; ReadCpuPerf(); }
            // 无 LHM 时: 优先 WMI 真实频率, 再退回负载估算
            if (s.CpuClock <= 0)
            {
                if (_realCpuMHz > 0) s.CpuClock = _realCpuMHz;
                else
                {
                    float load = s.CpuLoad;
                    if (load < 3f) s.CpuClock = _baseMHz * 0.55f;      // 空闲低频
                    else s.CpuClock = _baseMHz * (0.88f + 0.32f * Math.Min(1f, load / 100f));
                }
            }
            if (s.RamClock <= 0) s.RamClock = RamSpeedMtS / 2f;    // DDR 实际频率 = MT/s / 2
            if (s.CpuClock > PeakCpuClock) PeakCpuClock = s.CpuClock;   // 峰值频率跟踪

            // ---- 自身占用 ----
            try
            {
                TimeSpan nowCpu = _self.TotalProcessorTime;
                DateTime nowT = DateTime.UtcNow;
                double sec = (nowT - _lastSelfT).TotalSeconds;
                if (sec > 0.2)
                {
                    SelfCpuPct = (float)((nowCpu - _lastSelfCpu).TotalSeconds / sec / Environment.ProcessorCount * 100.0);
                    _lastSelfCpu = nowCpu; _lastSelfT = nowT;
                }
                _self.Refresh();
                SelfMemMB = _self.WorkingSet64 / (1024 * 1024);
            }
            catch { }
            s.SelfCpu = SelfCpuPct; s.SelfMemMB = SelfMemMB;

            // ---- 事件检测 (分类: 0=GPU 1=CPU 2=内存) ----
            ulong now = s.GpuOk ? s.ThrottleBits : 0;
            ulong appeared = now & ~_lastThrottle;
            ulong gone = _lastThrottle & ~now;
            if (appeared != 0)
            {
                bool serious;
                string txt = ThrottleText(appeared, out serious);
                if (txt != null)
                    PushEvent(Events, s.T, serious ? 0 : 3, 0, (serious ? "GPU 降频: " : "GPU 状态: ") + txt);
            }
            if (gone != 0 && (now & 0xF8) == 0)
            {
                bool was;
                ThrottleText(gone, out was);
                if (was) PushEvent(Events, s.T, 2, 0, "GPU 恢复正常频率");
            }
            _lastThrottle = now;

            // GPU 高温 / 回落
            if (s.GpuOk && s.GpuTemp >= 83f && !_hotLatched)
            {
                _hotLatched = true;
                PushEvent(Events, s.T, 1, 0, "GPU 高温 " + ((int)s.GpuTemp).ToString() + "°C");
            }
            else if (s.GpuOk && s.GpuTemp < 75f) _hotLatched = false;

            // GPU 显存高占用
            float vramPct = s.GpuMemTotalMB > 0 ? (float)s.GpuMemUsedMB / s.GpuMemTotalMB * 100f : 0;
            if (s.GpuOk && vramPct >= 90f && !_vramLatched)
            {
                _vramLatched = true;
                PushEvent(Events, s.T, 5, 0, "GPU 显存占用 " + ((int)vramPct).ToString() + "% (爆显存风险)");
            }
            else if (vramPct < 80f) _vramLatched = false;

            // GPU 功耗接近上限
            if (s.GpuOk && GpuPowerLimitW > 0 && s.GpuPower > 0)
            {
                if (s.GpuPower >= GpuPowerLimitW * 0.95f && !_powerLatched)
                {
                    _powerLatched = true;
                    PushEvent(Events, s.T, 5, 0, "GPU 功耗触顶 " + ((int)s.GpuPower).ToString() + "/" + GpuPowerLimitW + " W");
                }
                else if (s.GpuPower < GpuPowerLimitW * 0.85f) _powerLatched = false;
            }

            // CPU 高温 (LHM 可用时)
            if (s.CpuTemp > -900f)
            {
                if (s.CpuTemp >= 85f && !_cpuHotLatched)
                {
                    _cpuHotLatched = true;
                    PushEvent(Events, s.T, 1, 1, "CPU 高温 " + ((int)s.CpuTemp).ToString() + "°C");
                }
                else if (s.CpuTemp < 75f) _cpuHotLatched = false;
            }

            // CPU 持续满载
            if (s.CpuLoad >= 95f)
            {
                if (_cpuMaxSince == DateTime.MinValue) _cpuMaxSince = s.T;
                else if ((s.T - _cpuMaxSince).TotalSeconds >= 10 && !_cpuMaxLatched)
                {
                    _cpuMaxLatched = true;
                    PushEvent(Events, s.T, 4, 1, "CPU 持续满载 10s+ (可能瓶颈)");
                }
            }
            else if (s.CpuLoad < 90f) { _cpuMaxSince = DateTime.MinValue; _cpuMaxLatched = false; }

            // 内存高占用
            if (s.MemLoad >= 90f && !_memLatched)
            {
                _memLatched = true;
                PushEvent(Events, s.T, 5, 2, "内存占用 " + ((int)s.MemLoad).ToString() + "% (爆内存风险)");
            }
            else if (s.MemLoad < 80f) _memLatched = false;

            // ---- Top 10 内存进程 (每5秒采样一次, 减少开销) ----
            _procTick++;
            if (_procTick >= 5) { _procTick = 0; SampleTopProcs(); }

            // ---- 会话峰值 ----
            if (s.GpuOk)
            {
                if (s.GpuTemp > PeakGpuTemp) PeakGpuTemp = s.GpuTemp;
                if (s.GpuLoad > PeakGpuLoad) PeakGpuLoad = s.GpuLoad;
                if (s.GpuPower > 0 && s.GpuPower > PeakGpuPower) PeakGpuPower = s.GpuPower;
            }
            if (s.CpuLoad > PeakCpuLoad) PeakCpuLoad = s.CpuLoad;
            if (s.CpuTemp > -900f && s.CpuTemp > PeakCpuTemp) PeakCpuTemp = s.CpuTemp;
            if (s.MemLoad > PeakMemLoad) PeakMemLoad = s.MemLoad;

            // ---- 历史 ----
            History.Add(s);
            while (History.Count > HistCap) History.RemoveAt(0);
            return s;
        }

        void SampleTopProcs()
        {
            try
            {
                Process[] procs = Process.GetProcesses();
                // 只取 WorkingSet64 排序, 避免对每个进程都访问 MainModule
                long[] mems = new long[procs.Length];
                string foundGame = null; DateTime foundGameStart = DateTime.MinValue; bool hasGameStart = false;
                for (int i = 0; i < procs.Length; i++)
                {
                    try { mems[i] = procs[i].WorkingSet64; } catch { mems[i] = 0; }
                    // 游戏识别: 找到第一个命中名单的进程即短路 (Lower 仅在未命中前执行, 开销可忽略)
                    if (foundGame == null)
                    {
                        string pn;
                        try { pn = procs[i].ProcessName.ToLowerInvariant(); } catch { pn = null; }
                        if (pn != null && GameNames.Contains(pn))
                        {
                            foundGame = pn;
                            try { foundGameStart = procs[i].StartTime; hasGameStart = true; } catch { }
                        }
                    }
                }
                UpdateGame(foundGame, foundGameStart, hasGameStart);
                int[] idx = new int[procs.Length];
                for (int i = 0; i < idx.Length; i++) idx[i] = i;
                Array.Sort(idx, delegate(int a, int b) { return mems[b].CompareTo(mems[a]); });

                List<ProcInfo> result = new List<ProcInfo>(10);
                for (int rank = 0; rank < Math.Min(10, procs.Length); rank++)
                {
                    int i = idx[rank]; if (mems[i] <= 0) break;
                    try
                    {
                        ProcInfo pi = new ProcInfo();
                        pi.Name = procs[i].ProcessName;
                        pi.MemoryMB = mems[i] / (1024 * 1024);
                        int pid = procs[i].Id;
                        pi.Pid = pid;
                        string path = null;
                        // 先查路径缓存
                        lock (_pathCache) { if (_pathCache.ContainsKey(pid)) path = _pathCache[pid]; }
                        if (path == null)
                        {
                            try { path = procs[i].MainModule.FileName; } catch { }
                            if (path == null) try { using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = " + pid)) foreach (System.Management.ManagementBaseObject o in s.Get()) { path = Convert.ToString(o["ExecutablePath"]); break; } } catch { }
                            if (path != null) lock (_pathCache) { if (_pathCache.Count < 200) _pathCache[pid] = path; }
                        }
                        if (path != null) lock (_iconCache) { if (_iconCache.ContainsKey(path)) pi.Icon = _iconCache[path]; else if (_iconCache.Count < 60) { try { Icon ic = Icon.ExtractAssociatedIcon(path); if (ic != null) { Bitmap bmp = ic.ToBitmap(); _iconCache[path] = bmp; pi.Icon = bmp; } } catch { } } }
                        result.Add(pi);
                    }
                    catch { }
                }
                TopProcs = result;
            }
            catch { }
            finally { try { foreach (Process p in Process.GetProcesses()) try { p.Dispose(); } catch { } } catch { } }
        }

        // 游戏状态机: 启动/切换/退出 + 事件推送 (StartTime 回溯真实启动时间, 取不到时用首次检测时间)
        void UpdateGame(string game, DateTime gameStart, bool hasStart)
        {
            DateTime now = DateTime.Now;
            if (game != null)
            {
                if (!GameRunning || GameName != game)
                {
                    if (GameRunning) PushEvent(Events, now, 3, 3, "切换游戏: " + game);
                    else PushEvent(Events, now, 3, 3, "检测到游戏: " + game);
                    GameName = game;
                    TimeSpan back = hasStart ? now - gameStart : TimeSpan.Zero;
                    GameStart = (hasStart && back.TotalSeconds >= 0 && back.TotalDays < 1) ? gameStart : now;
                    GameRunning = true;
                }
            }
            else if (GameRunning)
            {
                GameRunning = false;
                TimeSpan d = now - GameStart; if (d.TotalSeconds < 0) d = TimeSpan.Zero;
                PushEvent(Events, now, 3, 3, "游戏退出: " + GameName + " (时长 " + FormatDur(d) + ")");
                GameName = null;
            }
        }

        public static string FormatDur(TimeSpan d)
        {
            if (d.TotalDays >= 1d) return ((int)d.TotalDays) + "d " + d.ToString(@"hh\:mm\:ss");
            return d.ToString(@"h\:mm\:ss");
        }

        // ---- 性能报告导出: 历史样本 + 峰值汇总 + 事件, CSV (UTF-8 BOM, Excel 直接打开不乱码) ----
        public static string ExportCsv(Collector col)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GameMonitor");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                System.Text.StringBuilder sb = new System.Text.StringBuilder(64 * 1024);
                sb.AppendLine("# GameMonitor 性能报告");
                sb.AppendLine("# 导出时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("# CPU: " + col.CpuName);
                sb.AppendLine("# GPU: " + col.GpuName + (col.GpuPowerLimitW > 0 ? " (功耗上限 " + col.GpuPowerLimitW + "W)" : ""));
                sb.AppendLine("# 样本数: " + col.History.Count);
                sb.AppendLine("# 峰值: GPU温度 " + (col.PeakGpuTemp > -900f ? ((int)col.PeakGpuTemp).ToString() + "C" : "N/A")
                    + ", GPU负载 " + (col.PeakGpuLoad >= 0 ? ((int)col.PeakGpuLoad).ToString() + "%" : "N/A")
                    + ", GPU功耗 " + (col.PeakGpuPower > 0 ? ((int)col.PeakGpuPower).ToString() + "W" : "N/A")
                    + ", CPU负载 " + (col.PeakCpuLoad >= 0 ? ((int)col.PeakCpuLoad).ToString() + "%" : "N/A")
                    + ", CPU频率峰值 " + (col.PeakCpuClock > 0 ? ((int)col.PeakCpuClock).ToString() + "MHz" : "N/A")
                    + ", 内存 " + (col.PeakMemLoad >= 0 ? ((int)col.PeakMemLoad).ToString() + "%" : "N/A"));
                if (col.GameName != null && col.GameRunning)
                {
                    TimeSpan gd = DateTime.Now - col.GameStart; if (gd.TotalSeconds < 0) gd = TimeSpan.Zero;
                    sb.AppendLine("# 游戏会话: " + col.GameName + " 已运行 " + FormatDur(gd));
                }
                sb.AppendLine("# 说明: cpu_clock_mhz 来自 Windows 性能计数器 (% Processor Performance x 基频); cpu_temp_c 不可用时为 N/A");
                sb.AppendLine("time,cpu_load_pct,cpu_clock_mhz,cpu_temp_c,mem_load_pct,mem_used_mb,mem_total_mb,gpu_load_pct,gpu_temp_c,gpu_clock_mhz,gpu_power_w,gpu_mem_used_mb,gpu_fan_pct,throttle_bits");
                lock (col.History)
                {
                    for (int i = 0; i < col.History.Count; i++)
                    {
                        Sample s = col.History[i];
                        sb.AppendLine(s.T.ToString("HH:mm:ss")
                            + "," + s.CpuLoad.ToString("0.0", CultureInfo.InvariantCulture)
                            + "," + (s.CpuClock > 0 ? ((int)s.CpuClock).ToString() : "N/A")
                            + "," + (s.CpuTemp > -900f ? ((int)s.CpuTemp).ToString() : "N/A")
                            + "," + ((int)s.MemLoad).ToString()
                            + "," + s.MemUsedMB.ToString() + "," + s.MemTotalMB.ToString()
                            + "," + (s.GpuOk ? s.GpuLoad.ToString() : "N/A")
                            + "," + (s.GpuOk && s.GpuTemp >= 0 ? s.GpuTemp.ToString() : "N/A")
                            + "," + (s.GpuOk && s.GpuClock > 0 ? s.GpuClock.ToString() : "N/A")
                            + "," + (s.GpuOk && s.GpuPower > 0 ? s.GpuPower.ToString() : "N/A")
                            + "," + (s.GpuOk ? s.GpuMemUsedMB.ToString() : "N/A")
                            + "," + (s.GpuOk && s.GpuFanPct >= 0 ? s.GpuFanPct.ToString() : "N/A")
                            + "," + (s.GpuOk ? s.ThrottleBits.ToString() : "N/A"));
                    }
                }
                sb.AppendLine();
                sb.AppendLine("# 事件记录");
                sb.AppendLine("time,hardware,message");
                lock (col.Events)
                {
                    for (int i = 0; i < col.Events.Count; i++)
                    {
                        MonEvent ev = col.Events[i];
                        string hw = ev.Hw == 0 ? "GPU" : (ev.Hw == 1 ? "CPU" : (ev.Hw == 2 ? "内存" : "系统"));
                        sb.AppendLine(ev.T.ToString("HH:mm:ss") + "," + hw + "," + CsvEscape(ev.Msg));
                    }
                }
                File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
                return path;
            }
            catch { return null; }
        }

        static string CsvEscape(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new char[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        public void Dispose()
        {
            if (_nvml != null) { _nvml.Dispose(); _nvml = null; }
            if (_lhm != null) { _lhm.Dispose(); _lhm = null; }
            if (_self != null) { try { _self.Dispose(); } catch { } _self = null; }
            if (_lpBuf != IntPtr.Zero) { System.Runtime.InteropServices.Marshal.FreeHGlobal(_lpBuf); _lpBuf = IntPtr.Zero; }
        }
    }
}
