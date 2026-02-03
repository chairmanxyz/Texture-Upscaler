using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ImageScaler;

namespace TextureUpscaler
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            DpiAwareness.TryEnablePerMonitorV2();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }

    internal static class DpiAwareness
    {
        // Win10+: Per-Monitor (V2) awareness context value = -4
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        public static void TryEnablePerMonitorV2()
        {
            try
            {
                // Must be called before any HWNDs are created.
                // If a manifest already set DPI awareness, this may fail with access denied — that's fine.
                if (Environment.OSVersion.Version.Major >= 10)
                {
                    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                }
                else
                {
                    SetProcessDPIAware();
                }
            }
            catch
            {
                // Swallow — worst case, Windows will bitmap-scale and be slightly blurry.
            }
        }
    }

    }
}
