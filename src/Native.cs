// Native.cs - Win32 API 封装 (全部微秒级调用, 保证低占用)
using System;
using System.Runtime.InteropServices;

namespace GameMonitor
{
    internal static class Native
    {
        // ---- CPU 总占用: GetSystemTimes 差值, 比 WMI/PDH 更轻 ----
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        // ---- 内存 ----
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // ---- 窗口置顶 / 穿透 / 拖动 ----
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOPMOST = 0x8;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_LAYERED = 0x80000;
        public const int WS_EX_TRANSPARENT = 0x20;

        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
        public const uint LWA_ALPHA = 0x2;

        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        public const uint WM_NCLBUTTONDOWN = 0xA1;
        public static IntPtr HTCAPTION = (IntPtr)0x2;

        // ---- Snap Layout: 非客户端 hover 追踪 ----
        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }
        public const uint TME_LEAVE = 0x00000002;
        public const uint TME_NONCLIENT = 0x80000000;
        [DllImport("user32.dll")]
        public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        // ---- 使窗口支持 Snap Layout (不再隐身) ----
        public static void ApplyOverlayStyle(IntPtr handle)
        {
            int ex = GetWindowLong(handle, GWL_EXSTYLE);
            ex |= WS_EX_TOPMOST;
            ex &= ~WS_EX_NOACTIVATE;
            ex &= ~WS_EX_TOOLWINDOW;
            SetWindowLong(handle, GWL_EXSTYLE, ex);
        }

        // ---- 每逻辑处理器负载: NtQuerySystemInformation(8) 一次拿全部线程, 微秒级 ----
        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);
    }
}
