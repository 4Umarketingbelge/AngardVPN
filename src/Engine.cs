using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace MiniVpn
{
    /// Один запуск openvpn.exe: процесс + управление через management-интерфейс.
    public class OpenVpnEngine
    {
        public event Action<OpenVpnEngine, string, string> StateChanged;   // имя состояния, адрес туннеля
        public event Action<OpenVpnEngine, long, long> BytesChanged;
        public event Action<OpenVpnEngine> Exited;

        Process proc;
        TcpClient client;
        StreamWriter writer;
        StreamWriter log;
        readonly object sync = new object();
        volatile bool stopping;
        int holds;

        /// Следить за журналом и сообщать состоянием "PUSH_REPLY", когда сервер прислал настройки.
        /// Нужно гонке: без сетевого адаптера OpenVPN не доходит до состояния CONNECTED.
        public bool WatchLog;

        public static string FindExe()
        {
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\OpenVPN"))
                {
                    if (k != null)
                    {
                        string dir = k.GetValue("") as string;
                        if (!string.IsNullOrEmpty(dir))
                        {
                            string p = Path.Combine(dir, @"bin\openvpn.exe");
                            if (File.Exists(p)) return p;
                        }
                    }
                }
            }
            catch (Exception) { }
            string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"OpenVPN\bin\openvpn.exe");
            if (File.Exists(def)) return def;
            def = @"C:\Program Files\OpenVPN\bin\openvpn.exe";
            return File.Exists(def) ? def : null;
        }

        public static int FreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public bool Start(string exe, string configPath, int managementPort)
        {
            return Start(exe, configPath, managementPort, Paths.Log);
        }

        public bool Start(string exe, string configPath, int managementPort, string logPath)
        {
            try
            {
                log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
                log.AutoFlush = true;
            }
            catch (IOException) { log = null; }

            ProcessStartInfo si = new ProcessStartInfo(exe, "--config \"" + configPath + "\"");
            si.UseShellExecute = false;
            si.CreateNoWindow = true;
            si.RedirectStandardOutput = true;
            si.RedirectStandardError = true;
            si.WorkingDirectory = Path.GetDirectoryName(configPath);
            proc = new Process();
            proc.StartInfo = si;
            proc.EnableRaisingEvents = true;
            proc.OutputDataReceived += OnOutput;
            proc.ErrorDataReceived += OnOutput;
            proc.Exited += delegate
            {
                Action<OpenVpnEngine> h = Exited;
                if (h != null) h(this);
            };
            try
            {
                if (!proc.Start()) return false;
            }
            catch (Exception) { return false; }
            ChildGuard.Attach(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            Thread t = new Thread(delegate() { ManagementLoop(managementPort); });
            t.IsBackground = true;
            t.Start();
            return true;
        }

        void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (sync)
            {
                try { if (log != null) log.WriteLine(e.Data); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        void ManagementLoop(int port)
        {
            try
            {
                TcpClient c = null;
                for (int i = 0; i < 60 && !stopping; i++)
                {
                    if (proc.HasExited) return;
                    try
                    {
                        c = new TcpClient();
                        c.Connect(IPAddress.Loopback, port);
                        break;
                    }
                    catch (SocketException) { c = null; Thread.Sleep(150); }
                }
                if (c == null) return;
                NetworkStream ns = c.GetStream();
                StreamReader reader = new StreamReader(ns, Encoding.ASCII);
                lock (sync)
                {
                    client = c;
                    writer = new StreamWriter(ns, Encoding.ASCII);
                    writer.NewLine = "\n";
                    writer.AutoFlush = true;
                    writer.WriteLine("state on");
                    writer.WriteLine("bytecount 1");
                    if (WatchLog) writer.WriteLine("log on");
                    // "hold release" отправляется только в ответ на >HOLD: если послать раньше,
                    // OpenVPN примет команду ещё до входа в ожидание и зависнет в нём навсегда.
                }
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string name, ip;
                    long bi, bo;
                    if (Mgmt.ParseState(line, out name, out ip))
                    {
                        Action<OpenVpnEngine, string, string> h = StateChanged;
                        if (h != null) h(this, name, ip);
                    }
                    else if (Mgmt.ParseBytes(line, out bi, out bo))
                    {
                        Action<OpenVpnEngine, long, long> h = BytesChanged;
                        if (h != null) h(this, bi, bo);
                    }
                    else if (WatchLog && line.StartsWith(">LOG:", StringComparison.Ordinal))
                    {
                        if (line.IndexOf("PUSH_REPLY", StringComparison.Ordinal) >= 0)
                        {
                            Action<OpenVpnEngine, string, string> h = StateChanged;
                            if (h != null) h(this, "PUSH_REPLY", "");
                        }
                    }
                    else if (line.StartsWith(">HOLD:", StringComparison.Ordinal))
                    {
                        // Первый HOLD — штатный старт: отпускаем. Повторный означает, что туннель упал и OpenVPN перезапускается.
                        if (++holds == 1)
                        {
                            lock (sync) { if (writer != null) writer.WriteLine("hold release"); }
                        }
                        else
                        {
                            Action<OpenVpnEngine, string, string> h = StateChanged;
                            if (h != null) h(this, "RECONNECTING", "");
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// Корректно завершает OpenVPN; если не вышел за 4 секунды — убивает процесс.
        public void Stop()
        {
            stopping = true;
            lock (sync)
            {
                try { if (writer != null) writer.WriteLine("signal SIGTERM"); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
            try
            {
                if (proc != null && !proc.HasExited && !proc.WaitForExit(4000))
                {
                    proc.Kill();
                    proc.WaitForExit(2000);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            lock (sync)
            {
                try { if (client != null) client.Close(); } catch (Exception) { }
                try { if (log != null) log.Dispose(); } catch (Exception) { }
                client = null; writer = null; log = null;
            }
        }
    }

    /// «Гонка»: несколько серверов проверяются одновременно, без сетевого адаптера и без изменений в системе.
    /// Побеждают те, кто довёл обмен ключами до конца и прислал настройки, то есть реально принимает подключение.
    public static class Race
    {
        /// Возвращает ответивших в порядке финиша. После первого победителя ждёт ещё graceMs, затем останавливает остальных.
        public static List<VpnServer> Run(List<VpnServer> wave, string exe, int timeoutMs, int graceMs, Func<bool> keepGoing, out string note)
        {
            string dir = Path.Combine(Paths.DataDir, "race");
            Directory.CreateDirectory(dir);
            List<VpnServer> winners = new List<VpnServer>();
            List<string> times = new List<string>();
            List<OpenVpnEngine> engines = new List<OpenVpnEngine>();
            object sync = new object();
            Stopwatch sw = Stopwatch.StartNew();
            long firstWin = -1;
            int started = 0, ended = 0;

            for (int i = 0; i < wave.Count; i++)
            {
                VpnServer s = wave[i];
                string cfg;
                try { cfg = s.DecodeConfig(); }
                catch (FormatException) { continue; }
                int port = OpenVpnEngine.FreePort();
                string path = Path.Combine(dir, i + ".ovpn");
                try { File.WriteAllText(path, ConfigBuilder.BuildProbe(cfg, port), new UTF8Encoding(false)); }
                catch (IOException) { continue; }
                OpenVpnEngine e = new OpenVpnEngine();
                e.WatchLog = true;
                e.StateChanged += delegate(OpenVpnEngine x, string name, string ip)
                {
                    if (name != "PUSH_REPLY" && name != "CONNECTED") return;
                    lock (sync)
                    {
                        if (winners.Contains(s)) return;
                        winners.Add(s);
                        times.Add(s.CountryShort + " " + s.RemoteIp + " за " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " с");
                        if (firstWin < 0) firstWin = sw.ElapsedMilliseconds;
                    }
                };
                e.Exited += delegate { Interlocked.Increment(ref ended); };
                if (e.Start(exe, path, port, Path.Combine(dir, i + ".log"))) { engines.Add(e); started++; }
            }

            while (keepGoing() && sw.ElapsedMilliseconds < timeoutMs && Thread.VolatileRead(ref ended) < started)
            {
                lock (sync) { if (firstWin >= 0 && sw.ElapsedMilliseconds - firstWin >= graceMs) break; }
                Thread.Sleep(30);
            }

            List<Thread> stops = new List<Thread>();
            foreach (OpenVpnEngine e in engines)
            {
                OpenVpnEngine x = e;
                Thread t = new Thread(delegate() { x.Stop(); });
                t.IsBackground = true;
                t.Start();
                stops.Add(t);
            }
            foreach (Thread t in stops) t.Join(7000);

            lock (sync)
            {
                note = "проверено " + started + " за " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                       " с, ответили " + winners.Count + (winners.Count > 0 ? ": " + string.Join("; ", times.ToArray()) : "");
                return new List<VpnServer>(winners);
            }
        }
    }

    /// Привязывает дочерние процессы к приложению: если приложение убито, openvpn.exe завершается вместе с ним.
    static class ChildGuard
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimit
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimit
        {
            public BasicLimit Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, int length);
        [DllImport("kernel32.dll")]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static IntPtr job = IntPtr.Zero;

        public static void Attach(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    ExtendedLimit info = new ExtendedLimit();
                    info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(ExtendedLimit)));
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch (Exception) { }
        }
    }
}
