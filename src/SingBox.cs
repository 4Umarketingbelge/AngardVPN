using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MiniVpn
{
    /// Один запуск sing-box (движок для серверов VLESS).
    public class SingBox
    {
        Process proc;
        StreamWriter log;
        readonly object sync = new object();
        volatile bool started;

        /// sing-box сообщил, что запустился (входящие и исходящие подняты).
        public bool Started { get { return started; } }

        public bool HasExited
        {
            get
            {
                try { return proc == null || proc.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        /// sing-box.exe лежит в папке engine рядом с приложением (для проверочных утилит — уровнем выше).
        public static string FindExe()
        {
            try
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
                foreach (string d in new string[] { dir, Path.GetDirectoryName(dir) })
                {
                    if (d == null) continue;
                    string p = Path.Combine(d, "engine", "sing-box.exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// Проверка настроек ядром («sing-box check»): -1 — приняты; номер исходящего, на котором ядро споткнулось;
        /// -2 — иная ошибка (текст в why). Нужна, потому что один сервер с незнакомым ядру параметром валит весь запуск.
        public static int BadOutbound(string exe, string configPath, out string why)
        {
            why = "";
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, "check --disable-color -c \"" + configPath + "\"");
                si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardError = true; si.RedirectStandardOutput = true;
                using (Process p = Process.Start(si))
                {
                    string o = null;
                    Thread t = new Thread(delegate() { try { o = p.StandardOutput.ReadToEnd(); } catch (Exception) { } });
                    t.IsBackground = true; t.Start();
                    string e = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (Exception) { } return -1; }
                    t.Join(2000);
                    if (p.ExitCode == 0) return -1;
                    why = (e + " " + o).Trim();
                    if (why.Length > 300) why = why.Substring(0, 300);
                    Match m = Regex.Match(why, @"outbound\[(\d+)\]");
                    return m.Success ? int.Parse(m.Groups[1].Value) : -2;
                }
            }
            catch (Exception ex) { why = ex.Message; return -1; }   // проверить не удалось — запускаем как есть
        }

        public bool Start(string exe, string configPath)
        {
            try
            {
                log = new StreamWriter(new FileStream(Paths.SingLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
                log.AutoFlush = true;
            }
            catch (IOException) { log = null; }

            ProcessStartInfo si = new ProcessStartInfo(exe, "run --disable-color -c \"" + configPath + "\"");
            si.UseShellExecute = false;
            si.CreateNoWindow = true;
            si.RedirectStandardOutput = true;
            si.RedirectStandardError = true;
            si.WorkingDirectory = Path.GetDirectoryName(configPath);
            proc = new Process();
            proc.StartInfo = si;
            proc.OutputDataReceived += OnOutput;
            proc.ErrorDataReceived += OnOutput;
            try
            {
                if (!proc.Start()) return false;
            }
            catch (Exception) { return false; }
            ChildGuard.Attach(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return true;
        }

        void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            if (e.Data.IndexOf("sing-box started", StringComparison.Ordinal) >= 0) started = true;
            lock (sync)
            {
                try { if (log != null) log.WriteLine(e.Data); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        [DllImport("kernel32.dll")] static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll")] static extern bool FreeConsole();
        [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint evt, uint group);
        static readonly object consoleLock = new object();

        /// Останавливает sing-box. Сначала мягко (Ctrl+C), чтобы он сам убрал адаптер, маршруты и фильтры;
        /// если не вышел за 5 секунд — завершает процесс.
        public void Stop()
        {
            try
            {
                if (proc != null && !proc.HasExited)
                {
                    lock (consoleLock)
                    {
                        if (AttachConsole((uint)proc.Id))
                        {
                            SetConsoleCtrlHandler(IntPtr.Zero, true);      // чтобы Ctrl+C не закрыл само приложение
                            GenerateConsoleCtrlEvent(0, 0);
                            proc.WaitForExit(5000);
                            FreeConsole();
                            SetConsoleCtrlHandler(IntPtr.Zero, false);
                        }
                    }
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                        proc.WaitForExit(3000);
                    }
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            lock (sync)
            {
                try { if (log != null) log.Dispose(); } catch (Exception) { }
                log = null;
            }
        }

        public static string ReadLog()
        {
            try
            {
                using (FileStream fs = new FileStream(Paths.SingLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (IOException) { return ""; }
        }

        /// Настоящая проверка сервера: sing-box запрашивает через него страницу и сообщает время, мс; -1 — не работает.
        public static int Delay(int port, string tag, int timeoutMs)
        {
            Match m = Regex.Match(Api(port, "/proxies/" + tag + "/delay?url=https%3A%2F%2Fwww.gstatic.com%2Fgenerate_204&timeout=" + timeoutMs, timeoutMs + 2000) ?? "", "\"delay\"\\s*:\\s*(\\d+)");
            int ms;
            return m.Success && int.TryParse(m.Groups[1].Value, out ms) && ms > 0 ? ms : -1;
        }

        static string Api(int port, string path)
        {
            return Api(port, path, 2000);
        }

        static string Api(int port, string path, int timeoutMs)
        {
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
                r.Timeout = timeoutMs;
                r.Proxy = null;
                using (WebResponse resp = r.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (Exception) { return null; }
        }

        /// Тег сервера, который группа «auto» выбрала сейчас ("s3"), либо null.
        public static string Now(int port)
        {
            return ParseNow(Api(port, "/proxies/auto"));
        }

        public static string ParseNow(string json)
        {
            Match m = Regex.Match(json ?? "", "\"now\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// Сколько байт прошло через sing-box с момента запуска.
        public static bool Traffic(int port, out long down, out long up)
        {
            return ParseTraffic(Api(port, "/connections"), out down, out up);
        }

        public static bool ParseTraffic(string json, out long down, out long up)
        {
            down = 0; up = 0;
            Match d = Regex.Match(json ?? "", "\"downloadTotal\"\\s*:\\s*(\\d+)"), u = Regex.Match(json ?? "", "\"uploadTotal\"\\s*:\\s*(\\d+)");
            return d.Success && u.Success && long.TryParse(d.Groups[1].Value, out down) && long.TryParse(u.Groups[1].Value, out up);
        }
    }

    /// Настоящая проверка серверов VLESS: один sing-box в режиме прокси (права администратора не нужны),
    /// у каждого сервера свой локальный порт; через каждый запрашивается страница и скачивается ~1.5 МБ.
    /// Замер по TCP до адреса сервера тут не помогает: прокладки на Cloudflare отвечают за 20 мс, а качают 0.1 Мбит/с.
    public static class VlessTester
    {
        public const double MinMbps = 2.0;         // медленнее — сервер не берётся в автовыбор
        const int Parallel = 16;
        // Файлы для замера скорости на разных площадках: одна площадка при частых замерах начинает отказывать
        // (Cloudflare отвечает 429), поэтому серверы раскидываются по площадкам по кругу. Качается кусок (Range).
        static readonly string[] SpeedUrls = {
            "https://cachefly.cachefly.net/10mb.test",
            "http://ipv4.download.thinkbroadband.com/10MB.zip",
            "https://proof.ovh.net/files/10Mb.dat",
            "https://ash-speed.hetzner.com/100MB.bin",
            "https://speed.cloudflare.com/__down?bytes=10000000" };
        const int SmallBytes = 1500000, BigBytes = 8000000;

        public const int MaxPingMs = 700;          // пинг через сервер больше — сервер не берётся, если есть другие

        /// Итог одного сервера: Speed (бит/с) и TestMs (пинг через сервер, мс); TestedBad — не работает или медленнее порога.
        public static void Apply(VpnServer s, int delayMs, double mbps)
        {
            s.TestedAt = DateTime.Now;
            s.TestMs = delayMs;
            s.TestedBad = delayMs <= 0 || mbps < MinMbps;
            s.Speed = delayMs > 0 ? (long)(mbps * 1e6) : 0;
        }

        /// Оценка для выбора: скорость, урезанная за большой пинг (пинг 300 мс — вдвое хуже, чем 0).
        public static double Score(VpnServer s)
        {
            return s.Speed / 1e6 / (1.0 + Math.Max(0, s.TestMs) / 300.0);
        }

        /// Годные по свежей проверке серверы, лучшие раньше. С пингом больше MaxPingMs — в конце.
        public static List<VpnServer> Good(List<VpnServer> list)
        {
            List<VpnServer> res = new List<VpnServer>();
            foreach (VpnServer s in list) if (s.Vless != null && s.TestedFresh && !s.TestedBad) res.Add(s);
            res.Sort(delegate(VpnServer a, VpnServer b)
            {
                int c = (a.TestMs > MaxPingMs ? 1 : 0).CompareTo(b.TestMs > MaxPingMs ? 1 : 0);
                return c != 0 ? c : Score(b).CompareTo(Score(a));
            });
            return res;
        }

        /// Группа для автовыбора sing-box из годных (уже упорядоченных) серверов. sing-box выбирает внутри группы
        /// по пингу, а не по скорости, и с медленным сервером в группе мог выбрать его (10 Мбит/с при лучшем 52).
        /// Поэтому берутся серверы не медленнее 40% от лучшего, но не меньше трёх (запас на случай обрыва) и не больше max.
        public static List<VpnServer> Group(List<VpnServer> good, int max)
        {
            long top = 0;
            foreach (VpnServer s in good) if (s.Speed > top) top = s.Speed;
            List<VpnServer> res = good.FindAll(delegate(VpnServer s) { return s.Speed >= top * 0.4; });
            foreach (VpnServer s in good) { if (res.Count >= 3) break; if (!res.Contains(s)) res.Add(s); }
            return res.Count > max ? res.GetRange(0, max) : res;
        }

        /// Проверяет серверы; bindInterface — имя сетевой карты, через которую идти мимо чужого VPN (null — как обычно).
        /// progress(проверено, всего). Возвращает число годных серверов; -1 — sing-box не запустился.
        public static int Run(string exe, List<VpnServer> list, string bindInterface, int budgetMs, int recheck, Func<bool> keepGoing, Action<int, int> progress, out string note)
        {
            note = "";
            if (exe == null || list.Count == 0) return 0;
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }
            if (ServicePointManager.DefaultConnectionLimit < 64) ServicePointManager.DefaultConnectionLimit = 64;
            list = new List<VpnServer>(list);
            exitRejected = 0;
            int[] ports = null;
            string file = Path.Combine(Paths.DataDir, "singbox-test.json");
            // Сервер с параметром, которого ядро не знает, валит запуск целиком: такие отбрасываются по одному.
            for (int round = 0; ; round++)
            {
                if (list.Count == 0) { note = "ни один сервер не принят ядром"; return 0; }
                ports = new int[list.Count];
                StringBuilder ob = new StringBuilder(), ib = new StringBuilder(), rules = new StringBuilder();
                for (int i = 0; i < list.Count; i++)
                {
                    ports[i] = OpenVpnEngine.FreePort();
                    string c = i > 0 ? "," : "";
                    ob.Append(c + Vless.Outbound(list[i].Vless, "s" + i, list[i].Ip));
                    ib.Append(c + "{\"type\":\"mixed\",\"tag\":\"in" + i + "\",\"listen\":\"127.0.0.1\",\"listen_port\":" + ports[i] + "}");
                    rules.Append(c + "{\"inbound\":\"in" + i + "\",\"outbound\":\"s" + i + "\"}");
                }
                string route = bindInterface != null ? "\"default_interface\":" + Vless.J(bindInterface) : "\"auto_detect_interface\":true";
                string cfg = "{\"log\":{\"level\":\"warn\"},\"inbounds\":[" + ib + "],\"outbounds\":[" + ob + ",{\"type\":\"direct\",\"tag\":\"direct\"}]," +
                    "\"route\":{" + route + ",\"rules\":[" + rules + "],\"final\":\"direct\"}}";
                try { File.WriteAllText(file, cfg, new UTF8Encoding(false)); } catch (IOException) { return -1; }
                string why;
                int bad = SingBox.BadOutbound(exe, file, out why);
                if (bad == -1 || round >= 40) break;
                if (bad < 0 || bad >= list.Count) { note = "ядро не приняло настройки: " + why; return -1; }
                Apply(list[bad], -1, 0);
                list.RemoveAt(bad);
            }

            Process p;
            StringBuilder err = new StringBuilder();
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, "run --disable-color -c \"" + file + "\"");
                si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardError = true; si.RedirectStandardOutput = true;
                si.WorkingDirectory = Paths.DataDir;
                p = Process.Start(si);
                ChildGuard.Attach(p);
                p.ErrorDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null) lock (err) if (err.Length < 2000) err.AppendLine(e.Data); };
                p.OutputDataReceived += delegate { };
                p.BeginErrorReadLine(); p.BeginOutputReadLine();
            }
            catch (Exception e) { note = "sing-box не запустился: " + e.Message; return -1; }
            try
            {
                // Ждём, пока поднимется последний входящий порт.
                Stopwatch up = Stopwatch.StartNew();
                while (up.ElapsedMilliseconds < 8000 && !p.HasExited && !PortOpen(ports[ports.Length - 1])) Thread.Sleep(100);
                if (p.HasExited) { lock (err) note = "sing-box завершился: " + err.ToString().Trim(); return -1; }

                Stopwatch total = Stopwatch.StartNew();
                int next = 0, done = 0, good = 0;
                List<Thread> ts = new List<Thread>();
                for (int t = 0; t < Math.Min(Parallel, list.Count); t++)
                {
                    Thread th = new Thread(delegate()
                    {
                        while (true)
                        {
                            int i = Interlocked.Increment(ref next) - 1;
                            if (i >= list.Count || !keepGoing() || total.ElapsedMilliseconds > budgetMs) return;
                            double mbps;
                            int ms = Measure(ports[i], i, SmallBytes, 9000, out mbps);
                            if (ms > 0)
                            {
                                string loc = ExitCountry(new WebProxy("127.0.0.1", ports[i]));
                                if (loc != null && Array.IndexOf(CensoredExits, loc) >= 0) { ms = -1; Interlocked.Increment(ref exitRejected); }
                            }
                            Apply(list[i], ms, mbps);
                            if (!list[i].TestedBad) Interlocked.Increment(ref good);
                            int d = Interlocked.Increment(ref done);
                            if (progress != null) progress(d, list.Count);
                        }
                    });
                    th.IsBackground = true;
                    th.Start();
                    ts.Add(th);
                }
                foreach (Thread th in ts) th.Join(budgetMs + 20000);

                // Второй этап: при общем замере серверы мешают друг другу, поэтому лучших перемеряем по двое,
                // качая побольше — так скорость и пинг ближе к тому, что будет в туннеле.
                List<int> best = new List<int>();
                for (int i = 0; i < list.Count; i++) if (list[i].TestedFresh && !list[i].TestedBad) best.Add(i);
                best.Sort(delegate(int x, int y) { return Score(list[y]).CompareTo(Score(list[x])); });
                if (best.Count > recheck) best.RemoveRange(recheck, best.Count - recheck);
                int pos = -1;
                ts.Clear();
                for (int t = 0; t < 2; t++)
                {
                    Thread th = new Thread(delegate()
                    {
                        while (true)
                        {
                            int k = Interlocked.Increment(ref pos);
                            if (k >= best.Count || !keepGoing() || total.ElapsedMilliseconds > budgetMs + 40000) return;
                            int i = best[k];
                            double mbps;
                            int ms = Measure(ports[i], i + 1, BigBytes, 8000, out mbps);
                            // Сервер, ответивший в первый раз, мог захлебнуться — берём лучшее из двух замеров скорости.
                            if (ms > 0) Apply(list[i], Math.Min(ms, Math.Max(1, list[i].TestMs)), Math.Max(mbps, list[i].Speed / 1e6));
                        }
                    });
                    th.IsBackground = true;
                    th.Start();
                    ts.Add(th);
                }
                foreach (Thread th in ts) th.Join(60000);
                good = 0;
                foreach (VpnServer v in list) if (v.TestedFresh && !v.TestedBad) good++;
                note = "проверено " + done + " из " + list.Count + " за " + (total.ElapsedMilliseconds / 1000) + " с, годных (от " + MinMbps + " Мбит/с) " + good +
                    (exitRejected > 0 ? ", отброшено с выходом в стране с блокировками: " + exitRejected : "") +
                    (bindInterface != null ? ", в обход чужого VPN через «" + bindInterface + "»" : "");
                return good;
            }
            finally
            {
                try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
            }
        }

        /// Пинг через сервер (мс, -1 — не работает) и скорость скачивания (Мбит/с). site — с какой площадки начать.
        static int Measure(int port, int site, int bytes, int downloadMs, out double mbps)
        {
            mbps = 0;
            WebProxy px = new WebProxy("127.0.0.1", port);
            // Первый запрос открывает соединение (TCP, TLS, протокол сервера), второй и третий идут по нему — это и есть пинг.
            int first = Request(px, "https://www.gstatic.com/generate_204", 6000, 0, true);
            if (first <= 0) return -1;
            int ping = first;
            for (int k = 0; k < 2; k++)
            {
                int r = Request(px, "https://www.gstatic.com/generate_204", 3000, 0, true);
                if (r > 0 && r < ping) ping = r;
            }
            // Две попытки с разных площадок: отказ или придержка одной площадки (бывает после частых замеров)
            // не делает сервер плохим. Вторая — только если первая дала мало; берётся лучшая.
            for (int k = 0; k < 2; k++)
            {
                string u = SpeedUrls[(site + k) % SpeedUrls.Length];
                Stopwatch sw = Stopwatch.StartNew();
                int got = Request(px, u, downloadMs, bytes, false);
                if (got > 100000) mbps = Math.Max(mbps, got * 8.0 / 1e6 / Math.Max(0.05, sw.Elapsed.TotalSeconds));
                if (mbps >= 8) break;
            }
            return ping;
        }

        /// Страны выхода, где сайты блокируются: сервер с таким выходом не обходит блокировки (YouTube, Instagram и т. п.),
        /// даже если в названии у него флаг другой страны (так и было: «Бразилия» с выходом в России).
        public static readonly string[] CensoredExits = { "RU", "BY", "IR", "CN", "TM" };
        static int exitRejected;

        /// Настоящая страна выхода через сервер (по ответу Cloudflare, «loc=XX»); null — узнать не удалось.
        static string ExitCountry(IWebProxy px)
        {
            try
            {
                HttpWebRequest q = (HttpWebRequest)WebRequest.Create("https://www.cloudflare.com/cdn-cgi/trace");
                q.Proxy = px; q.Timeout = 5000; q.ReadWriteTimeout = 5000; q.KeepAlive = true;
                q.ConnectionGroupName = ((WebProxy)px).Address.Port.ToString() + "t";
                q.UserAgent = "Mozilla/5.0 AngardVPN";
                using (WebResponse r = q.GetResponse())
                {
                    Match m = Regex.Match(Http.ReadAll(r, 6000), @"loc=([A-Z]{2})");
                    return m.Success ? m.Groups[1].Value : null;
                }
            }
            catch (Exception) { return null; }
        }

        static bool PortOpen(int port)
        {
            try { using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient()) { c.Connect(IPAddress.Loopback, port); return true; } }
            catch (Exception) { return false; }
        }

        /// download == 0 — время до ответа, мс; иначе скачивает столько байт (Range) и возвращает, сколько скачано. Не удалось — -1.
        static int Request(IWebProxy px, string url, int timeoutMs, int download, bool keepAlive)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                HttpWebRequest q = (HttpWebRequest)WebRequest.Create(url);
                q.Proxy = px; q.Timeout = timeoutMs; q.ReadWriteTimeout = timeoutMs; q.KeepAlive = keepAlive;
                // Своё имя группы соединений на каждый порт: иначе .NET может пустить запрос по соединению другого сервера.
                q.ConnectionGroupName = ((WebProxy)px).Address.Port.ToString();
                q.UserAgent = "Mozilla/5.0 AngardVPN";
                if (download > 0) q.AddRange(0, download - 1);
                using (HttpWebResponse r = (HttpWebResponse)q.GetResponse())
                {
                    if (download == 0) return Math.Max(1, (int)sw.ElapsedMilliseconds);
                    if ((int)r.StatusCode != 200 && (int)r.StatusCode != 206) return -1;
                    using (Stream st = r.GetResponseStream())
                    {
                        byte[] buf = new byte[65536];
                        int total = 0, n;
                        while ((n = st.Read(buf, 0, buf.Length)) > 0) { total += n; if (total >= download || sw.ElapsedMilliseconds > timeoutMs) break; }
                        return total;
                    }
                }
            }
            catch (WebException e)
            {
                // Любой ответ сервера (например, 403) значит, что туннель работает; для скорости он не годится.
                if (download == 0 && e.Response != null) return Math.Max(1, (int)sw.ElapsedMilliseconds);
                return -1;
            }
            catch (Exception) { return -1; }
        }
    }
}
