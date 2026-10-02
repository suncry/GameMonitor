// Program.cs - 入口
using System;
using System.Windows.Forms;

namespace GameMonitor
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (MonitorForm f = new MonitorForm())
            {
                f.Show();
                Application.Run(f);
            }
        }
    }
}
