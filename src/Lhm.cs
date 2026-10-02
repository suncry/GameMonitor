// Lhm.cs - LibreHardwareMonitor 可选加载 (反射, 无编译期依赖)
// DLL 不存在或无管理员权限时自动降级, CPU 温度显示 N/A
using System;
using System.Collections;
using System.IO;
using System.Reflection;

namespace GameMonitor
{
    internal sealed class LhmProbe : IDisposable
    {
        object _computer;
        Type _sensorType;
        object _typeTemperature, _typePower, _typeClock, _typeFan;
        PropertyInfo _hwProp;

        public bool Active { get { return _computer != null; } }

        public bool TryInit(string dllPath)
        {
            try
            {
                if (!File.Exists(dllPath)) return false;
                Assembly asm = Assembly.LoadFrom(dllPath);
                Type t = asm.GetType("LibreHardwareMonitor.Hardware.Computer");
                if (t == null) return false;
                _computer = Activator.CreateInstance(t);
                SetBool("IsCpuEnabled", true);
                SetBool("IsMemoryEnabled", false);
                SetBool("IsGpuEnabled", false);
                SetBool("IsMotherboardEnabled", true);   // 主板 SuperIO: CPU 风扇转速
                SetBool("IsStorageEnabled", false);
                SetBool("IsNetworkEnabled", false);
                SetBool("IsControllerEnabled", false);
                SetBool("IsPsuEnabled", false);
                SetBool("IsBatteryEnabled", false);
                t.InvokeMember("Open", BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, _computer, null);

                _sensorType = asm.GetType("LibreHardwareMonitor.Hardware.SensorType");
                if (_sensorType == null) { _computer = null; return false; }
                _typeTemperature = Enum.Parse(_sensorType, "Temperature");
                _typePower = Enum.Parse(_sensorType, "Power");
                _typeClock = Enum.Parse(_sensorType, "Clock");
                _typeFan = Enum.Parse(_sensorType, "Fan");
                _hwProp = t.GetProperty("Hardware");
                if (_hwProp == null) { _computer = null; return false; }
                return true;
            }
            catch
            {
                _computer = null; return false;
            }
        }

        void SetBool(string name, bool v)
        {
            PropertyInfo p = _computer.GetType().GetProperty(name);
            if (p != null && p.CanWrite) p.SetValue(_computer, v, null);
        }

        static object GetProp(object o, string name)
        {
            try
            {
                PropertyInfo p = o.GetType().GetProperty(name);
                if (p == null) return null;
                return p.GetValue(o, null);
            }
            catch { return null; }
        }

        public void Poll(ref float cpuTemp, ref float cpuPower, ref float cpuClock, ref float ramClock, ref float cpuFanRpm)
        {
            if (_computer == null) return;
            try
            {
                // LHM 0.9.6: Open() 后需逐硬件 Update 刷新传感器
                IEnumerable hws = _hwProp.GetValue(_computer, null) as IEnumerable;
                if (hws == null) return;
                float bestClock = 0f; bool gotClock = false;
                foreach (object hw in hws)
                {
                    try { hw.GetType().InvokeMember("Update", BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, hw, null); }
                    catch { }
                    object hwt = GetProp(hw, "HardwareType");
                    if (hwt == null) continue;
                    string kind = hwt.ToString();
                    if (kind == "Motherboard")
                    {
                        // 主板风扇在 SubHardware (SuperIO) 上
                        IEnumerable subs = GetProp(hw, "SubHardware") as IEnumerable;
                        if (subs == null) continue;
                        float cpuFan = 0f, maxFan = 0f;
                        foreach (object sub in subs)
                        {
                            try { sub.GetType().InvokeMember("Update", BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, sub, null); }
                            catch { }
                            IEnumerable ss = GetProp(sub, "Sensors") as IEnumerable;
                            if (ss == null) continue;
                            foreach (object s in ss)
                            {
                                object st = GetProp(s, "SensorType");
                                if (st == null || !st.Equals(_typeFan)) continue;
                                object v = GetProp(s, "Value");
                                if (v == null) continue;
                                float val;
                                try { val = Convert.ToSingle(v); } catch { continue; }
                                if (val <= 0f) continue;   // 过滤无效 0
                                string nm = GetProp(s, "Name") as string;
                                if (nm == null) nm = "";
                                if (nm.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0) cpuFan = val;   // CPU_FAN 优先
                                else if (val > maxFan) maxFan = val;                                          // 否则记最大转速备选
                            }
                        }
                        if (cpuFan > 0f) cpuFanRpm = cpuFan;
                        else if (maxFan > 0f && cpuFanRpm <= 0f) cpuFanRpm = maxFan;
                        continue;
                    }
                    if (kind != "Cpu" && kind != "Memory") continue;
                    IEnumerable sensors = GetProp(hw, "Sensors") as IEnumerable;
                    if (sensors == null) continue;
                    foreach (object s in sensors)
                    {
                        object st = GetProp(s, "SensorType");
                        if (st == null) continue;
                        string nm = GetProp(s, "Name") as string;
                        if (nm == null) nm = "";
                        object v = GetProp(s, "Value");
                        if (v == null) continue;
                        float val;
                        try { val = Convert.ToSingle(v); } catch { continue; }

                        if (kind == "Cpu")
                        {
                            if (st.Equals(_typeTemperature) && (nm.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0 || nm.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0))
                            { if (val > 0f) cpuTemp = val; }   // 过滤无效 0 (无权限时读 0)
                            else if (st.Equals(_typeTemperature) && nm.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0)
                            { if (cpuTemp <= -900f && val > 0f) cpuTemp = val; }
                            else if (st.Equals(_typePower) && nm.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0)
                            { if (val > 0f) cpuPower = val; }
                            else if (st.Equals(_typeClock) && nm.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                            { if (val > bestClock) bestClock = val; gotClock = true; }
                        }
                        else
                        {
                            if (st.Equals(_typeClock)) ramClock = val;
                        }
                    }
                }
                if (gotClock) cpuClock = bestClock;
            }
            catch { }
        }

        public void Dispose()
        {
            if (_computer != null)
            {
                try { _computer.GetType().InvokeMember("Close", BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, _computer, null); }
                catch { }
                _computer = null;
            }
        }
    }
}
