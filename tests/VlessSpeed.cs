using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using MiniVpn;

// Живая проверка серверов VLESS без прав администратора: один sing-box, у каждого сервера свой локальный
// прокси-порт; через каждый замеряется отклик (generate_204) и скорость скачивания.
// Аргументы: <sing-box.exe> <файл отчёта> <папка с CC.b64 | файл CC|vless строк> [предел серверов]
static class VlessSpeed
{
    static int BasePort = 41000;

    static int Main(string[] a)
    {
        string exe = a[0], report = a[1], src = a[2];
        int limit = a.Length > 3 ? int.Parse(a[3]) : 400;
        ServicePointManager.DefaultConnectionLimit = 200;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        List<string> lines = new List<string>();
        if (Directory.Exists(src))
        {
            foreach (string f in Directory.GetFiles(src, "*.b64"))
                foreach (string l in Vless.SplitSubscription(File.ReadAllText(f)))
                    if (l.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) lines.Add(Path.GetFileNameWithoutExtension(f) + "|" + l);
        }
        else lines.AddRange(File.ReadAllLines(src, Encoding.UTF8));
        List<VpnServer> all = Vless.FreeServers(lines);
        foreach (VpnServer s in all) Vless.ResolvePublic(s);
        all.RemoveAll(delegate(VpnServer s) { return string.IsNullOrEmpty(s.Ip); });
        if (all.Count > limit) all = all.GetRange(0, limit);
        Console.WriteLine("серверов: " + all.Count);

        StringBuilder ob = new StringBuilder(), ib = new StringBuilder(), rules = new StringBuilder();
        for (int i = 0; i < all.Count; i++)
        {
            string c = i > 0 ? "," : "";
            string one = Vless.Outbound(all[i].Vless, "s" + i, all[i].Ip);
            string bind = Environment.GetEnvironmentVariable("BIND");
            if (!string.IsNullOrEmpty(bind)) one = "{\"bind_interface\":\"" + bind + "\"," + one.Substring(1);
            ob.Append(c + one);
            ib.Append(c + "{\"type\":\"mixed\",\"tag\":\"in" + i + "\",\"listen\":\"127.0.0.1\",\"listen_port\":" + (BasePort + i) + "}");
            rules.Append(c + "{\"inbound\":\"in" + i + "\",\"outbound\":\"s" + i + "\"}");
        }
        string cfg = "{\"log\":{\"level\":\"warn\"},\"inbounds\":[" + ib + "],\"outbounds\":[" + ob + ",{\"type\":\"direct\",\"tag\":\"direct\"}],\"route\":{\"rules\":[" + rules + "],\"final\":\"direct\"}}";
        string cfgFile = Path.Combine(Path.GetTempPath(), "angard-speed.json");
        File.WriteAllText(cfgFile, cfg, new UTF8Encoding(false));
        ProcessStartInfo psi = new ProcessStartInfo(exe, "run -c \"" + cfgFile + "\"");
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardError = true; psi.RedirectStandardOutput = true;
        Process p = Process.Start(psi);
        StringBuilder err = new StringBuilder();
        p.ErrorDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null) lock (err) if (err.Length < 4000) err.AppendLine(e.Data); };
        p.BeginErrorReadLine(); p.BeginOutputReadLine();
        Thread.Sleep(3000);
        if (p.HasExited) { Console.WriteLine("sing-box упал: " + err); return 2; }

        int[] delay = new int[all.Count]; double[] speed = new double[all.Count]; string[] note = new string[all.Count];
        int next = 0;
        List<Thread> ts = new List<Thread>();
        for (int t = 0; t < 24; t++)
        {
            Thread th = new Thread(delegate()
            {
                while (true)
                {
                    int i = Interlocked.Increment(ref next) - 1;
                    if (i >= all.Count) return;
                    WebProxy px = new WebProxy("127.0.0.1", BasePort + i);
                    delay[i] = Get(px, "https://www.gstatic.com/generate_204", 8000, 0, out note[i]);
                    if (delay[i] > 0)
                    {
                        string n2;
                        Stopwatch sw = Stopwatch.StartNew();
                        int got = Get(px, "https://speed.cloudflare.com/__down?bytes=4000000", 15000, 1, out n2);
                        speed[i] = got > 0 ? got * 8.0 / 1e6 / Math.Max(0.05, sw.Elapsed.TotalSeconds) : 0;
                        if (got <= 0) note[i] = "скачивание: " + n2;
                    }
                }
            });
            th.IsBackground = true; th.Start(); ts.Add(th);
        }
        foreach (Thread th in ts) th.Join();
        try { p.Kill(); } catch (Exception) { }

        List<int> idx = new List<int>();
        for (int i = 0; i < all.Count; i++) idx.Add(i);
        idx.Sort(delegate(int x, int y) { return speed[y].CompareTo(speed[x]); });
        StringBuilder r = new StringBuilder();
        int ok = 0, fast = 0;
        foreach (int i in idx) { if (delay[i] > 0) ok++; if (speed[i] >= 5) fast++; }
        r.AppendLine("серверов " + all.Count + ", отвечают через VLESS " + ok + ", быстрее 5 Мбит/с " + fast);
        r.AppendLine("страна;мбит/с;отклик мс;tcp-задержка;адрес;sni;тип;замечание");
        foreach (int i in idx)
        {
            VlessLink l = all[i].Vless;
            r.AppendLine(string.Join(";", new string[] { all[i].CountryShort, speed[i].ToString("0.0", CultureInfo.InvariantCulture), delay[i].ToString(),
                "", all[i].Ip + ":" + l.Port, l.Sni, l.Security + "/" + l.Type, note[i] ?? "" }));
        }
        File.WriteAllText(report, r.ToString(), new UTF8Encoding(true));
        Console.WriteLine(r.ToString().Substring(0, Math.Min(3000, r.Length)));
        return 0;
    }

    /// mode 0 — время до ответа (мс), mode 1 — сколько байт скачано. Не удалось — -1.
    static int Get(IWebProxy px, string url, int timeout, int mode, out string note)
    {
        note = null;
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            HttpWebRequest q = (HttpWebRequest)WebRequest.Create(url);
            q.Proxy = px; q.Timeout = timeout; q.ReadWriteTimeout = timeout; q.KeepAlive = false;
            using (HttpWebResponse resp = (HttpWebResponse)q.GetResponse())
            {
                if (mode == 0) return Math.Max(1, (int)sw.ElapsedMilliseconds);
                using (Stream st = resp.GetResponseStream())
                {
                    byte[] buf = new byte[65536]; int total = 0, n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0) { total += n; if (sw.ElapsedMilliseconds > timeout) break; }
                    return total;
                }
            }
        }
        catch (WebException e)
        {
            if (e.Response != null && mode == 0) return Math.Max(1, (int)sw.ElapsedMilliseconds);
            note = e.Status.ToString(); return -1;
        }
        catch (Exception e) { note = e.GetType().Name; return -1; }
    }
}
