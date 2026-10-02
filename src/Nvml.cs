// Nvml.cs - NVIDIA 官方 NVML 直连 (P/Invoke nvml.dll, 微秒级, 无进程开销)
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GameMonitor
{
    internal sealed class GpuSample
    {
        public bool Ok;
        public int LoadPct;          // GPU 计算负载 %
        public int MemUtilPct;       // 显存带宽利用率 %
        public int TempC;
        public int SmClockMHz;       // 当前核心频率
        public int MaxSmClockMHz;    // 理论最大频率
        public long MemUsedMB, MemTotalMB;
        public int PowerW;
        public int PState;
        public ulong ThrottleBits;   // 降频原因位掩码
        public int FanSpeedPct = -1; // 风扇转速 % (-1=不可用)
    }

    internal sealed class Nvml : IDisposable
    {
        const int NVML_SUCCESS = 0;
        const uint NVML_TEMPERATURE_GPU = 0;
        const uint NVML_CLOCK_SM = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct nvmlUtilization_t { public uint gpu; public uint memory; }

        [StructLayout(LayoutKind.Sequential)]
        struct nvmlMemory_t { public ulong total; public ulong used; public ulong free; }

        [DllImport("nvml.dll")] static extern int nvmlInit();
        [DllImport("nvml.dll")] static extern int nvmlShutdown();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex(uint index, out IntPtr device);
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out nvmlUtilization_t utilization);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out nvmlMemory_t memory);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetClockInfo(IntPtr device, uint type, out uint clock);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, uint type, out uint clock);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint power);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limit);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint speed);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPerformanceState(IntPtr device, out int pState);
        // 降频原因 API (新驱动导出名)
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);

        IntPtr _dev = IntPtr.Zero;
        bool _ok;

        public string Name = "";
        public int MaxSmClockMHz;
        public int PowerLimitW;     // TDP 上限 (启动时读一次)

        public bool TryInit()
        {
            try
            {
                if (nvmlInit() != NVML_SUCCESS) return false;
                if (nvmlDeviceGetHandleByIndex(0, out _dev) != NVML_SUCCESS) return false;
                StringBuilder sb = new StringBuilder(160);
                if (nvmlDeviceGetName(_dev, sb, 160) == NVML_SUCCESS) Name = sb.ToString();
                uint maxSm;
                if (nvmlDeviceGetMaxClockInfo(_dev, NVML_CLOCK_SM, out maxSm) == NVML_SUCCESS && maxSm > 0)
                    MaxSmClockMHz = (int)maxSm;
                uint pl;
                if (nvmlDeviceGetPowerManagementLimit(_dev, out pl) == NVML_SUCCESS) PowerLimitW = (int)(pl / 1000);
                _ok = true;
                return true;
            }
            catch { return false; }
        }

        public bool Read(GpuSample s)
        {
            if (!_ok) { s.Ok = false; return false; }
            try
            {
                nvmlUtilization_t util;
                if (nvmlDeviceGetUtilizationRates(_dev, out util) == NVML_SUCCESS)
                { s.LoadPct = (int)util.gpu; s.MemUtilPct = (int)util.memory; }
                else { s.Ok = false; return false; }

                uint t;
                if (nvmlDeviceGetTemperature(_dev, NVML_TEMPERATURE_GPU, out t) == NVML_SUCCESS) s.TempC = (int)t;

                uint sm;
                if (nvmlDeviceGetClockInfo(_dev, NVML_CLOCK_SM, out sm) == NVML_SUCCESS) s.SmClockMHz = (int)sm;

                nvmlMemory_t m;
                if (nvmlDeviceGetMemoryInfo(_dev, out m) == NVML_SUCCESS)
                {
                    s.MemTotalMB = (long)(m.total / (1024 * 1024));
                    s.MemUsedMB = (long)(m.used / (1024 * 1024));
                }

                uint mw;
                if (nvmlDeviceGetPowerUsage(_dev, out mw) == NVML_SUCCESS) s.PowerW = (int)(mw / 1000);

                uint fan;
                if (nvmlDeviceGetFanSpeed(_dev, out fan) == NVML_SUCCESS) s.FanSpeedPct = (int)fan;

                int ps;
                if (nvmlDeviceGetPerformanceState(_dev, out ps) == NVML_SUCCESS) s.PState = ps;

                try
                {
                    ulong thr = 0;
                    if (nvmlDeviceGetCurrentClocksThrottleReasons(_dev, out thr) == NVML_SUCCESS) s.ThrottleBits = thr;
                }
                catch { }

                s.Ok = true;
                return true;
            }
            catch { s.Ok = false; return false; }
        }

        public void Dispose()
        {
            if (_ok) { try { nvmlShutdown(); } catch { } _ok = false; }
        }
    }
}
