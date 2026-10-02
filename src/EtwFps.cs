// EtwFps.cs - ETW 实时跟踪 DxgKrnl Present 事件 → 游戏帧率 (PresentMon 同源原理, 无外部进程/无注入)
// 原理: 每个 Present() 调用触发 DxgKrnl ETW 事件 68(Present_Start), 按秒统计各进程的 Present 次数即为帧率
// 需要管理员权限 (主程序已提权)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace GameMonitor
{
    internal static class EtwFps
    {
        // Microsoft-Windows-DxgKrnl (从 logman query providers 确认, 末尾 87C98277BA9D)
        static readonly Guid DxgKrnl = new Guid("802EC45A-1E99-4B83-9920-87C98277BA9D");
        // 会话标识 GUID (任意固定值)
        static readonly Guid SessionGuid = new Guid("a7d43b21-9f0e-4c5a-8b3a-2e6f1d4c9b07");
        const string SessionName = "GameMonFPS";

        const int EVENT_PRESENT_START = 68;
        const int EVENT_PRESENT_STOP = 69;
        const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        const uint TRACE_LEVEL_VERBOSE = 5;
        const uint EVENT_TRACE_REAL_TIME_MODE = 0x1000;
        const uint EVENT_TRACE_NO_PER_PROCESSOR_BUFFERING = 0x10;
        const uint WNODE_FLAG_TRACED_GUID = 0x20000;
        const uint EVENT_FILTER_TYPE_EVENT_ID = 0x80000000;
        const uint CONTROL_CODE_STOP = 1;
        const int PROPS_SIZE = 120;         // EVENT_TRACE_PROPERTIES x64 (Wnode48 + 9输入ULONG + 6输出ULONG + HANDLE8 + 2偏移ULONG = 120)
        const int LOGFILE_SIZE = 512;        // EVENT_TRACE_LOGFILEW x64 = 456, 预留余量
        const int OFF_LogFileName = 0;
        const int OFF_LoggerName = 8;
        const int OFF_ProcessTraceMode = 32;
        const int OFF_EventRecordCallback = 432;
        // EVENT_TRACE_PROPERTIES x64 字段偏移 (无 Guid 字段, 会话 Guid 在 Wnode.Guid)
        const int P_BufferSize = 48, P_MinBuffers = 52, P_MaxBuffers = 56, P_MaxFileSize = 60;
        const int P_LogFileMode = 64, P_FlushTimer = 68, P_EnableFlags = 72;
        const int P_LogFileNameOffset = 112, P_LoggerNameOffset = 116;
        // EVENT_RECORD x64 内部偏移
        const int OFF_RE_ProcessId = 12;
        const int OFF_RE_EventId = 40;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr propertiesBuffer);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern uint EnableTraceEx2(ulong sessionHandle, ref Guid providerId, uint controlCode, byte level,
            ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr filterDescriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr propertiesBuffer, uint controlCode);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ulong OpenTraceW(IntPtr logfileBuffer);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern uint ProcessTrace([MarshalAs(UnmanagedType.LPArray)] ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern ulong CloseTrace(ulong traceHandle);

        delegate void EventRecordCallbackDelegate(IntPtr eventRecord);

        // ---- 状态 ----
        static readonly object _lk = new object();
        static Dictionary<uint, int> _cur = new Dictionary<uint, int>();      // 当前秒计数
        static Dictionary<uint, int> _prev = new Dictionary<uint, int>();     // 上一秒计数
        static readonly Dictionary<uint, string> _names = new Dictionary<uint, string>();      // pid -> 进程名
        static readonly Dictionary<uint, DateTime> _nameT = new Dictionary<uint, DateTime>();
        static int _selfPid;
        static ulong _session;
        static ulong _trace;
        static Thread _thread;
        static bool _running;
        static GCHandle _propsPin, _logPin;
        static IntPtr _logNamePtr;

        static readonly EventRecordCallbackDelegate _cb = OnEventRecord;      // 防 GC 回收

        public static bool Active { get { return _running; } }
        public static int DebugTotalPresents;   // 调试: 累计 Present 事件数 (验证 ETW 管道)
        public static int DebugSnapCount;       // 调试: 上次采样的进程数
        public static string DebugStatus = "未启动";  // 调试: 启动状态/错误码

        // ---- 启动: 清理残留会话 → StartTrace → Enable(DxgKrnl) → 消费线程 ----
        public static bool Start()
        {
            try
            {
                _selfPid = Process.GetCurrentProcess().Id;
                // 清理上次崩溃残留的同名会话
                uint cleanup = ControlTraceW(0, SessionName, BuildProps(), CONTROL_CODE_STOP);
                // 创建实时会话 (纯实时模式, 无日志文件)
                uint err = StartTraceW(out _session, SessionName, BuildProps());
                if (err == 183) { Thread.Sleep(200); err = StartTraceW(out _session, SessionName, BuildProps()); }   // 已存在 → 再清一次重试
                if (err != 0) { DebugStatus = "StartTrace 失败 err=" + err; try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gm_etw_debug.txt"), DebugStatus); } catch { } return false; }
                DebugStatus = "StartTrace OK handle=" + _session;
                // 启用 DxgKrnl 提供程序: 优先仅订阅 Present 事件 (68/69) 降低事件量
                bool filtered = EnableProvider(true);
                DebugStatus += filtered ? " | 过滤订阅OK" : " | 过滤订阅失败→全量";
                if (!filtered) EnableProvider(false);   // 过滤器不被支持时退化为全量订阅
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gm_etw_debug.txt"), DebugStatus); } catch { }
                // 消费线程
                _running = true;
                _thread = new Thread(ConsumeLoop);
                _thread.IsBackground = true;
                _thread.Priority = ThreadPriority.BelowNormal;
                _thread.Start();
                return true;
            }
            catch (Exception ex) { DebugStatus = "EXCEPTION: " + ex.Message; Stop(); return false; }
        }

        static bool EnableProvider(bool useIdFilter)
        {
            try
            {
                Guid dxgKrnlLocal = DxgKrnl;
                IntPtr filter = IntPtr.Zero;
                if (useIdFilter)
                {
                    // EVENT_FILTER_EVENT_ID { ULONG count; USHORT events[2]; } = {2, 68, 69}
                    byte[] fb = new byte[8];
                    BitConverter.GetBytes((uint)2).CopyTo(fb, 0);
                    BitConverter.GetBytes((ushort)EVENT_PRESENT_START).CopyTo(fb, 4);
                    BitConverter.GetBytes((ushort)EVENT_PRESENT_STOP).CopyTo(fb, 6);
                    IntPtr fbPtr = Marshal.AllocHGlobal(fb.Length);
                    Marshal.Copy(fb, 0, fbPtr, fb.Length);
                    // EVENT_FILTER_DESCRIPTOR { Ptr, Size, Type, Reserved }
                    byte[] fd = new byte[24];
                    Marshal.WriteIntPtr(fd, 0, fbPtr);
                    BitConverter.GetBytes((uint)fb.Length).CopyTo(fd, 8);
                    BitConverter.GetBytes((uint)EVENT_FILTER_TYPE_EVENT_ID).CopyTo(fd, 12);
                    IntPtr fdPtr = Marshal.AllocHGlobal(fd.Length);
                    Marshal.Copy(fd, 0, fdPtr, fd.Length);
                    filter = fdPtr;
                }
                uint rc = EnableTraceEx2(_session, ref dxgKrnlLocal, EVENT_CONTROL_CODE_ENABLE_PROVIDER, (byte)TRACE_LEVEL_VERBOSE, 0, 0, 0, filter);
                return rc == 0;
            }
            catch { return false; }
        }

        static void ConsumeLoop()
        {
            try
            {
                // EVENT_TRACE_LOGFILEW (x64, 按偏移布局): LoggerName@8, ProcessTraceMode@32, EventRecordCallback@432
                byte[] logbuf = new byte[LOGFILE_SIZE];
                _logPin = GCHandle.Alloc(logbuf, GCHandleType.Pinned);
                _logNamePtr = Marshal.StringToHGlobalUni(SessionName);
                Marshal.WriteIntPtr(logbuf, OFF_LogFileName, IntPtr.Zero);                    // 实时会话: 用 LoggerName
                Marshal.WriteIntPtr(logbuf, OFF_LoggerName, _logNamePtr);
                Marshal.WriteInt32(logbuf, OFF_ProcessTraceMode, 0x100 | 0x10000000);   // REAL_TIME | EVENT_RECORD
                // 诊断: 同时写入纯 REAL_TIME 版本看 OpenTrace 是否成功
                DebugStatus += " | PTM=0x" + (0x100 | 0x10000000).ToString("X");
                Marshal.WriteIntPtr(logbuf, OFF_EventRecordCallback, Marshal.GetFunctionPointerForDelegate(_cb));
                _trace = OpenTraceW(_logPin.AddrOfPinnedObject());
                if (_trace == 0 || _trace == ulong.MaxValue) {
                    DebugStatus = "OpenTrace 失败 trace=" + _trace;
                    try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gm_etw_debug.txt"), DebugStatus); } catch { }
                    _logPin.Free(); return;
                }
                DebugStatus += " | OpenTrace OK trace=" + _trace;
                ulong[] handles = new ulong[] { _trace };
                ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);                          // 阻塞直到会话停止
                CloseTrace(_trace);
                _trace = 0;
            }
            catch { }
        }

        // ---- ETW 事件回调 (注意: 可能多线程并发进入) ----
        static void OnEventRecord(IntPtr rec)
        {
            if (!_running) return;
            try
            {
                int id = Marshal.ReadInt16(rec, OFF_RE_EventId);
                if (id != EVENT_PRESENT_START && id != EVENT_PRESENT_STOP) return;
                uint pid = (uint)Marshal.ReadInt32(rec, OFF_RE_ProcessId);
                lock (_lk)
                {
                    int c;
                    if (id == EVENT_PRESENT_START) { _cur.TryGetValue(pid, out c); _cur[pid] = c + 1; DebugTotalPresents++; }
                }
            }
            catch { }
        }

        // ---- 每秒采样: 取上一完整秒的计数, 按实际窗口时长折算帧率, 优先已识别的游戏进程 ----
        static DateTime _lastSample = DateTime.UtcNow;
        public static float SampleFps(int preferredPid, out string procName)
        {
            procName = "";
            if (!_running) return 0f;
            DateTime now = DateTime.UtcNow;
            double window = (now - _lastSample).TotalSeconds;
            _lastSample = now;
            if (window < 0.5) window = 1.0;
            Dictionary<uint, int> snap;
            lock (_lk) { snap = _cur; _cur = new Dictionary<uint, int>(); _prev = snap; }
            DebugSnapCount = snap.Count;
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gm_etw_debug.txt"), DebugStatus + " | presents=" + DebugTotalPresents + " snapProcs=" + snap.Count); } catch { }
            if (snap.Count == 0) return 0f;
            // 优先: 已识别游戏进程的 Present 计数 (若有效)
            if (preferredPid > 0)
            {
                int cnt; if (snap.TryGetValue((uint)preferredPid, out cnt) && cnt >= 8)
                {
                    string nm = ResolveName((uint)preferredPid);
                    if (!string.IsNullOrEmpty(nm) && !IsSystemCompositor(nm) && preferredPid != _selfPid)
                    { procName = nm; return (float)(cnt / window); }
                }
            }
            // 兜底: 取计数最高的前 4 个候选 (帧率从高到低选第一个有效渲染进程)
            List<KeyValuePair<uint, int>> top = new List<KeyValuePair<uint, int>>(snap.Count);
            foreach (KeyValuePair<uint, int> kv in snap) if (kv.Value >= 8) top.Add(kv);
            top.Sort(delegate(KeyValuePair<uint, int> a, KeyValuePair<uint, int> b) { return b.Value.CompareTo(a.Value); });
            int cand = Math.Min(4, top.Count);
            for (int i = 0; i < cand; i++)
            {
                uint pid = top[i].Key;
                string name = ResolveName(pid);
                if (string.IsNullOrEmpty(name)) continue;                          // 进程已退出
                if ((int)pid == _selfPid) continue;
                if (IsSystemCompositor(name)) continue;                             // 排除 dwm/explorer 等系统合成进程
                procName = name;
                return (float)(top[i].Value / window);
            }
            return 0f;
        }

        static bool IsSystemCompositor(string name)
        {
            return name == "dwm" || name == "explorer" || name == "csrss" || name == "winlogon"
                || name == "LogonUI" || name == "sihost" || name == "ApplicationFrameHost" || name == "GameMonFPS";
        }

        static string ResolveName(uint pid)
        {
            DateTime now = DateTime.UtcNow;
            lock (_lk)
            {
                DateTime t;
                if (_nameT.TryGetValue(pid, out t) && _names.ContainsKey(pid) && (now - t).TotalSeconds < 10) return _names[pid];
            }
            string name = null;
            try { using (Process p = Process.GetProcessById((int)pid)) name = p.ProcessName; }
            catch { }
            if (name == null)
            {
                // 进程可能刚退出: 使用 10 秒内的缓存名 (帧率最后一秒仍然有效)
                lock (_lk)
                {
                    DateTime t;
                    if (_nameT.TryGetValue(pid, out t) && (now - t).TotalSeconds < 10) return _names.ContainsKey(pid) ? _names[pid] : null;
                }
                return null;
            }
            lock (_lk) { _names[pid] = name; _nameT[pid] = now; }
            return name;
        }

        // ---- 构造 EVENT_TRACE_PROPERTIES blob (x64, 实时会话 + 临时 etl 文件) ----
        // 注: Win11 StartTrace 拒绝空日志文件名 (err=161), 需提供真实路径; 实时模式下文件写入量小, 停止时清理
        static string _etlPath;
        static IntPtr BuildProps()
        {
            if (_etlPath == null) _etlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GameMonFPS.etl");
            int nameBytes = (SessionName.Length + 1) * 2;
            int fileBytes = (_etlPath.Length + 1) * 2;
            int sz = PROPS_SIZE + nameBytes + fileBytes;
            byte[] buf = new byte[sz];
            int nameOff = PROPS_SIZE;
            int fileOff = nameOff + nameBytes;
            BitConverter.GetBytes((uint)sz).CopyTo(buf, 0);
            BitConverter.GetBytes((uint)1).CopyTo(buf, 40);
            BitConverter.GetBytes((uint)WNODE_FLAG_TRACED_GUID).CopyTo(buf, 44);
            byte[] sg = SessionGuid.ToByteArray(); Buffer.BlockCopy(sg, 0, buf, 24, 16);
            BitConverter.GetBytes((uint)16).CopyTo(buf, P_BufferSize);
            BitConverter.GetBytes((uint)0).CopyTo(buf, P_MinBuffers);
            BitConverter.GetBytes((uint)0).CopyTo(buf, P_MaxBuffers);
            BitConverter.GetBytes((uint)1).CopyTo(buf, P_MaxFileSize);    // 1MB 上限 (配合循环模式) — 实时模式不强制, 留作保护
            BitConverter.GetBytes((uint)(EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_NO_PER_PROCESSOR_BUFFERING)).CopyTo(buf, P_LogFileMode);
            BitConverter.GetBytes((uint)1).CopyTo(buf, P_FlushTimer);
            BitConverter.GetBytes((uint)0).CopyTo(buf, P_EnableFlags);
            BitConverter.GetBytes((uint)fileOff).CopyTo(buf, P_LogFileNameOffset);
            BitConverter.GetBytes((uint)nameOff).CopyTo(buf, P_LoggerNameOffset);
            System.Text.Encoding.Unicode.GetBytes(SessionName).CopyTo(buf, nameOff);
            System.Text.Encoding.Unicode.GetBytes(_etlPath).CopyTo(buf, fileOff);
            IntPtr ptr = Marshal.AllocHGlobal(sz);
            Marshal.Copy(buf, 0, ptr, sz);
            return ptr;
        }

        // ---- 停止 ----
        public static void Stop()
        {
            _running = false;
            try { ControlTraceW(_session, SessionName, BuildProps(), CONTROL_CODE_STOP); } catch { }
            try { if (_trace != 0) CloseTrace(_trace); } catch { }
            if (_thread != null) { try { _thread.Join(1500); } catch { } _thread = null; }
            try { if (_logPin.IsAllocated) _logPin.Free(); } catch { }
            try { if (_logNamePtr != IntPtr.Zero) { Marshal.FreeHGlobal(_logNamePtr); _logNamePtr = IntPtr.Zero; } } catch { }
            lock (_lk) { _cur.Clear(); _names.Clear(); _nameT.Clear(); }
        }
    }
}
