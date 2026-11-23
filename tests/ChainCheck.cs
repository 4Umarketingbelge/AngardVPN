using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using MiniVpn;

// Проверка полной цепочки приложения без прав администратора: тот же конфиг sing-box, что и в работе
// (группа автовыбора из лучших по проверке серверов, DNS, маршруты), только вход — локальный прокси вместо адаптера.
// Через него меряются пинг, скорость и открытие сайтов. Аргументы: <сколько серверов проверить> [<файл с источниками CC|ссылка>]
static class ChainCheck
{
    static int Main(string[] a)
    {
        int n = a.Length > 0 ? int.Parse(a[0]) : 200;
        List<string> others = Tunnels.Active();
        string bind = others.Count > 0 ? Tunnels.PhysicalInterface() : null;
        Console.WriteLine("другие VPN: " + (others.Count == 0 ? "нет" : string.Join(", ", others.ToArray())) + "; обход через: " + (bind ?? "-"));
        string note;
        Stopwatch sw = Stopwatch.StartNew();
        List<VpnServer> all;
        if (a.Length > 1) { all = Vless.FreeServers(new List<string>(File.ReadAllLines(a[1], Encoding.UTF8))); foreach (VpnServer s in all) Vless.ResolvePublic(s); note = "из файла " + a[1]; }
        else all = Vless.LoadFree(out note);
        Console.WriteLine("список за " + sw.ElapsedMilliseconds / 1000 + " с: " + note + ", серверов " + all.Count);
        Dictionary<string, int> proto = new Dictionary<string, int>();
        foreach (VpnServer s in all) { int c; proto.TryGetValue(s.Source, out c); proto[s.Source] = c + 1; }
        foreach (KeyValuePair<string, int> p in proto) Console.WriteLine("  " + p.Key + ": " + p.Value);

        ServerPicker.Measure(all, 2000);
        List<VpnServer> vl = new List<VpnServer>();
        foreach (VpnServer s in ServerPicker.Order(all, null, null, "")) if (s.Latency != -1) vl.Add(s);
        vl = ServerPicker.Diverse(vl, n, 30);
        string exe = SingBox.FindExe();
        sw.Restart();
        VlessTester.Run(exe, vl, bind, 150000, 12, delegate { return true; }, null, out note);
        Console.WriteLine("проверка за " + sw.ElapsedMilliseconds / 1000 + " с: " + note);
        List<VpnServer> good = VlessTester.Good(vl);
        Dictionary<string, int> gp = new Dictionary<string, int>();
        foreach (VpnServer s in good) { int c; gp.TryGetValue(s.Source, out c); gp[s.Source] = c + 1; }
        foreach (KeyValuePair<string, int> p in gp) Console.WriteLine("  годных " + p.Key + ": " + p.Value);
        for (int i = 0; i < Math.Min(12, good.Count); i++)
            Console.WriteLine("  " + good[i].CountryShort + " " + good[i].SpeedText + ", пинг " + good[i].TestMs + " мс, " + good[i].Ip + ":" + good[i].Vless.Port);
        if (good.Count == 0) return 1;

        // Для проверки стабильности: лучшие 12 по отдельности, каждый на своём порту (файл multi-test.json, порты в multi-ports.txt).
        {
            List<VpnServer> top = good.GetRange(0, Math.Min(12, good.Count));
            StringBuilder ob = new StringBuilder(), ib = new StringBuilder(), rules = new StringBuilder(), ports = new StringBuilder();
            for (int i = 0; i < top.Count; i++)
            {
                int pt = OpenVpnEngine.FreePort();
                string c = i > 0 ? "," : "";
                ob.Append(c + Vless.Outbound(top[i].Vless, "s" + i, top[i].Ip));
                ib.Append(c + "{\"type\":\"mixed\",\"tag\":\"in" + i + "\",\"listen\":\"127.0.0.1\",\"listen_port\":" + pt + "}");
                rules.Append(c + "{\"inbound\":\"in" + i + "\",\"outbound\":\"s" + i + "\"}");
                ports.AppendLine(pt + " " + top[i].CountryShort + " " + top[i].Source + " " + top[i].Ip + ":" + top[i].Vless.Port + " " + top[i].SpeedText);
            }
            string route = bind != null ? "\"default_interface\":" + Vless.J(bind) : "\"auto_detect_interface\":true";
            File.WriteAllText(Path.Combine(Paths.DataDir, "multi-test.json"), "{\"log\":{\"level\":\"warn\"},\"inbounds\":[" + ib + "],\"outbounds\":[" + ob +
                ",{\"type\":\"direct\",\"tag\":\"direct\"}],\"route\":{" + route + ",\"rules\":[" + rules + "],\"final\":\"direct\"}}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Paths.DataDir, "multi-ports.txt"), ports.ToString(), new UTF8Encoding(false));
        }

        // Полная цепочка: конфиг приложения с группой автовыбора.
        List<VpnServer> use = good.GetRange(0, Math.Min(8, good.Count));
        int clash = OpenVpnEngine.FreePort(), port = OpenVpnEngine.FreePort();
        string cfg = Vless.Config(use, clash, false, port, bind);
        string file = Path.Combine(Paths.DataDir, "chain-test.json");
        File.WriteAllText(file, cfg, new UTF8Encoding(false));
        ProcessStartInfo si = new ProcessStartInfo(exe, "run --disable-color -c \"" + file + "\"");
        si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardError = true; si.RedirectStandardOutput = true;
        Process pr = Process.Start(si);
        StringBuilder log = new StringBuilder();
        pr.ErrorDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        pr.OutputDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        pr.BeginErrorReadLine(); pr.BeginOutputReadLine();
        Thread.Sleep(9000);     // группа автовыбора делает первый URL-тест
        try
        {
            if (pr.HasExited) { Console.WriteLine("sing-box завершился:\n" + log); return 2; }
            Console.WriteLine("автовыбор выбрал: " + SingBox.Now(clash));
            WebProxy px = new WebProxy("127.0.0.1", port);
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            List<int> pings = new List<int>();
            for (int k = 0; k < 6; k++) { int ms = Time(px, "https://www.gstatic.com/generate_204", false); if (ms > 0) pings.Add(ms); }
            pings.Sort();
            Console.WriteLine("пинг через цепочку (повторные запросы): " + string.Join(", ", pings.ConvertAll(delegate(int x) { return x.ToString(); }).ToArray()) + " мс");
            for (int k = 0; k < 2; k++)
            {
                Stopwatch d = Stopwatch.StartNew();
                int bytes = Download(px, "https://speed.cloudflare.com/__down?bytes=15000000", 20000);
                Console.WriteLine("скорость через цепочку, попытка " + (k + 1) + ": " + (bytes * 8.0 / 1e6 / d.Elapsed.TotalSeconds).ToString("0.0") + " Мбит/с (" + bytes / 1000000 + " МБ)");
            }
            foreach (string u in new string[] { "https://www.youtube.com/", "https://www.instagram.com/", "https://web.telegram.org/", "https://www.google.com/", "https://author.today/", "https://api.ipify.org/" })
            {
                int ms = Time(px, u, true);
                Console.WriteLine("  " + u + " → " + (ms > 0 ? ms + " мс" : "НЕ ОТКРЫЛСЯ"));
            }
            Console.WriteLine("внешний IP через цепочку: " + Text(px, "https://api.ipify.org/"));
            Console.WriteLine("автовыбор сейчас: " + SingBox.Now(clash));
        }
        finally { try { pr.Kill(); } catch (Exception) { } }
        string errs = log.ToString();
        int fatal = errs.IndexOf("FATAL", StringComparison.Ordinal);
        if (fatal >= 0) Console.WriteLine("в журнале ядра FATAL: " + errs.Substring(fatal, Math.Min(300, errs.Length - fatal)));
        return 0;
    }

    static int Time(IWebProxy px, string url, bool fresh)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            HttpWebRequest q = (HttpWebRequest)WebRequest.Create(url);
            q.Proxy = px; q.Timeout = 12000; q.ReadWriteTimeout = 12000; q.KeepAlive = !fresh;
            q.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130 Safari/537.36";
            if (fresh) q.ConnectionGroupName = Guid.NewGuid().ToString();
            using (WebResponse r = q.GetResponse()) { }
            return Math.Max(1, (int)sw.ElapsedMilliseconds);
        }
        catch (WebException e) { return e.Response != null ? Math.Max(1, (int)sw.ElapsedMilliseconds) : -1; }
        catch (Exception) { return -1; }
    }

    static int Download(IWebProxy px, string url, int ms)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            HttpWebRequest q = (HttpWebRequest)WebRequest.Create(url);
            q.Proxy = px; q.Timeout = ms; q.ReadWriteTimeout = ms;
            using (WebResponse r = q.GetResponse())
            using (Stream st = r.GetResponseStream())
            {
                byte[] buf = new byte[65536]; int total = 0, n;
                while ((n = st.Read(buf, 0, buf.Length)) > 0) { total += n; if (sw.ElapsedMilliseconds > ms) break; }
                return total;
            }
        }
        catch (Exception) { return 0; }
    }

    static string Text(IWebProxy px, string url)
    {
        try
        {
            HttpWebRequest q = (HttpWebRequest)WebRequest.Create(url);
            q.Proxy = px; q.Timeout = 10000;
            using (WebResponse r = q.GetResponse()) using (StreamReader sr = new StreamReader(r.GetResponseStream())) return sr.ReadToEnd().Trim();
        }
        catch (Exception e) { return "нет (" + e.Message + ")"; }
    }
}
