using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MiniVpn
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);

        [STAThread]
        static void Main(string[] args)
        {
            bool quit = Array.IndexOf(args, "--quit") >= 0;
            bool created;
            using (Mutex m = new Mutex(true, "Global\\AngardVPN_single_instance", out created))
            {
                if (!created)
                {
                    // Уже запущен: просим первый экземпляр показать окно (или выйти — по --quit).
                    PostMessage((IntPtr)0xFFFF, quit ? MainForm.QuitMessage : MainForm.ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                if (quit) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Если прошлый запуск завершился аварийно, блокировка могла остаться — снимаем.
                if (KillSwitch.IsActive()) KillSwitch.Remove();

                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Journal.Add("АВАРИЯ: " + e.ExceptionObject);
                    KillSwitch.Remove();
                };
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Journal.Add("ОШИБКА: " + e.Exception);
                    KillSwitch.Remove();
                    MessageBox.Show("Непредвиденная ошибка:\n" + e.Exception.Message, "AngardVPN", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Application.Exit();
                };

                bool auto = Array.IndexOf(args, "--autoconnect") >= 0;
                Application.Run(new MainForm(auto, Array.IndexOf(args, "--tray") >= 0));
                GC.KeepAlive(m);
            }
        }
    }
}
