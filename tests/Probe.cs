using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using MiniVpn;

// Диагностика без запуска приложения: прогоняет настоящий движок (Engine.cs) на серверах из списка
// с разными вариантами настроек и пишет результат в файл.
// Аргументы: <файл отчёта> <секунд на попытку> <вариант> [<вариант> ...]
// Вариант — дополнительные строки конфига через ';' (пустая строка "-" — без дополнений).
static class Probe
{
    static StringBuilder o = new StringBuilder();
    static string outFile;

    static void W(string s)
    {
        o.AppendLine(s);
        try { File.WriteAllText(outFile, o.ToString(), new UTF8Encoding(true)); } catch (IOException) { }
    }

    static string Ip()
    {
        try
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create("http://api.ipify.org");
            r.Timeout = 8000; r.Proxy = null;
            using (WebResponse resp = r.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd().Trim();
        }
        catch (Exception e) { return "нет ответа (" + e.Message + ")"; }
    }

    static int Main(string[] a)
    {
        outFile = a[0];
        int seconds = int.Parse(a[1]);
        W("внешний IP до: " + Ip());
        string net;
        bool works = NetCheck.Works(6000, delegate { return true; }, out net);
        W("проверка интернета (та же, что в приложении): " + works + " — " + net);
        foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                W("  активен: " + n.Name + " [" + n.Description + "]");

        bool cached;
        List<VpnServer> list = ServerList.Load(out cached);
        W("серверов: " + list.Count + (cached ? " (кэш)" : ""));
        if (list.Count == 0) return 2;
        Stopwatch scan = Stopwatch.StartNew();
        ServerPicker.Measure(list, 2000);
        int alive = 0, dead = 0, udp = 0;
        foreach (VpnServer s in list) { if (s.Latency >= 0) alive++; else if (s.Latency == -1) dead++; else udp++; }
        W("замер задержки за " + scan.ElapsedMilliseconds + " мс: отвечают " + alive + ", молчат " + dead + ", UDP (не измеряются) " + udp);
        list = ServerPicker.Order(list, null, new List<string>(), "");
        for (int i = 0; i < Math.Min(6, list.Count); i++)
            W("  " + (i + 1) + ". " + list[i].CountryShort + " " + list[i].RemoteIp + ":" + list[i].RemotePort + " " + list[i].LatencyText + ", рейтинг " + list[i].Score);
        string exe = OpenVpnEngine.FindExe();
        int next = 0;

        // Гонка: та же, что в приложении. Адаптер не трогает, поэтому работает без прав администратора.
        string home = Countries.Home();
        W("регион Windows: " + (home ?? "неизвестен"));
        List<VpnServer> geo = ServerPicker.Order(list, null, new List<string>(), "", false, home);
        string g = "";
        for (int i = 0; i < Math.Min(10, geo.Count); i++) g += geo[i].CountryShort + " ";
        W("порядок по близости (если мешает другой VPN): " + g);
        for (int wave = 0; wave < 3; wave++)
        {
            List<VpnServer> part = geo.GetRange(wave * 8, Math.Min(8, geo.Count - wave * 8));
            string note;
            List<VpnServer> winners = Race.Run(part, exe, 7000, 700, delegate { return true; }, out note);
            W("гонка " + (wave + 1) + ": " + note);
        }
        // sing-box: принимает ли он конфигурацию, которую собирает приложение, запускается ли и останавливается ли мягко.
        string sing = SingBox.FindExe();
        W("sing-box: " + (sing ?? "не найден"));
        if (sing != null)
        {
            List<string> links = new List<string>();
            links.Add("vless://11111111-2222-3333-4444-555555555555@192.0.2.10:443?type=tcp&security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=0123abcd&sni=www.example.org&fp=chrome&flow=xtls-rprx-vision#A");
            links.Add("vless://11111111-2222-3333-4444-555555555555@192.0.2.11:443?type=ws&security=tls&host=cdn.example.org&path=%2Fws%3Fed%3D2048&alpn=h2%2Chttp%2F1.1#B");
            links.Add("vless://11111111-2222-3333-4444-555555555555@192.0.2.12:443?type=grpc&security=tls&serviceName=svc&sni=g.example.org#C");
            links.Add("vless://11111111-2222-3333-4444-555555555555@192.0.2.13:443?type=httpupgrade&security=tls&host=h.example.org&path=%2Fup#D");
            links.Add("vless://11111111-2222-3333-4444-555555555555@192.0.2.14:80?type=tcp&security=none#E");
            List<VpnServer> vs = Vless.ToServers(links);
            foreach (VpnServer s in vs) s.Ip = s.Vless.Host;
            foreach (bool tun in new bool[] { true, false })
            {
                int clash = OpenVpnEngine.FreePort(), proxy = OpenVpnEngine.FreePort();
                string path = Path.Combine(Paths.DataDir, "probe-singbox.json");
                File.WriteAllText(path, Vless.Config(vs, clash, tun, proxy), new UTF8Encoding(false));
                ProcessStartInfo si = new ProcessStartInfo(sing, "check -c \"" + path + "\"");
                si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardError = true; si.RedirectStandardOutput = true;
                using (Process p = Process.Start(si))
                {
                    string err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                    p.WaitForExit(20000);
                    W("  проверка конфигурации (" + (tun ? "туннель" : "прокси") + ", серверов " + vs.Count + "): код " + p.ExitCode + " " + err.Trim());
                }
                if (tun) continue;
                // Запуск без туннеля (права не нужны): сообщает ли о старте, отвечает ли API, выходит ли по мягкой остановке.
                SingBox sb = new SingBox();
                Stopwatch t = Stopwatch.StartNew();
                bool ok = sb.Start(sing, path);
                while (ok && !sb.Started && !sb.HasExited && t.ElapsedMilliseconds < 10000) Thread.Sleep(50);
                W("  запуск: " + ok + ", сообщил о старте: " + sb.Started + " за " + t.ElapsedMilliseconds + " мс");
                Thread.Sleep(1500);
                long d, u;
                W("  API: выбран " + (SingBox.Now(clash) ?? "(нет ответа)") + ", счётчик трафика отвечает: " + SingBox.Traffic(clash, out d, out u));
                t.Restart();
                sb.Stop();
                W("  остановка за " + t.ElapsedMilliseconds + " мс, процесс завершён: " + sb.HasExited);
                string log = SingBox.ReadLog();
                W("  хвост журнала:\r\n" + Journal.Tail(log, 4));
            }
        }
        // Общедоступные серверы VLESS: сколько их, какие страны, и сколько реально пропускают трафик.
        if (sing != null)
        {
            Stopwatch fw = Stopwatch.StartNew();
            string fnote;
            List<VpnServer> free = Vless.LoadFree(out fnote);
            W("общедоступные VLESS: " + fnote + ", загрузка " + (fw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " с");
            ServerPicker.Measure(free, 2500);
            int falive = 0;
            foreach (VpnServer s in free) if (s.Latency >= 0) falive++;
            W("  отвечают на соединение: " + falive + " из " + free.Count);
            List<VpnServer> near = ServerPicker.Order(free, null, null, "");
            near.RemoveAll(delegate(VpnServer s) { return s.Latency < 0; });
            if (near.Count > 60) near = near.GetRange(0, 60);
            if (near.Count > 0)
            {
                int clash = OpenVpnEngine.FreePort(), proxy = OpenVpnEngine.FreePort();
                string path = Path.Combine(Paths.DataDir, "probe-singbox.json");
                File.WriteAllText(path, Vless.Config(near, clash, false, proxy), new UTF8Encoding(false));
                SingBox sb = new SingBox();
                bool ok = sb.Start(sing, path);
                Stopwatch t = Stopwatch.StartNew();
                while (ok && !sb.Started && !sb.HasExited && t.ElapsedMilliseconds < 15000) Thread.Sleep(50);
                W("  sing-box с " + near.Count + " ближайшими серверами: запущен " + sb.Started);
                if (sb.Started)
                {
                    int[] delay = new int[near.Count];
                    int nextI = 0;
                    List<Thread> ths = new List<Thread>();
                    for (int k = 0; k < 12; k++)
                    {
                        Thread th = new Thread(delegate()
                        {
                            while (true)
                            {
                                int i = Interlocked.Increment(ref nextI) - 1;
                                if (i >= near.Count) return;
                                delay[i] = SingBox.Delay(clash, "s" + i, 5000);
                            }
                        });
                        th.IsBackground = true; th.Start(); ths.Add(th);
                    }
                    foreach (Thread th in ths) th.Join(60000);
                    int passing = 0;
                    Dictionary<string, List<int>> by = new Dictionary<string, List<int>>();
                    for (int i = 0; i < near.Count; i++)
                    {
                        if (delay[i] <= 0) continue;
                        passing++;
                        List<int> l;
                        if (!by.TryGetValue(near[i].CountryShort, out l)) { l = new List<int>(); by[near[i].CountryShort] = l; }
                        l.Add(delay[i]);
                    }
                    W("  реально пропускают трафик: " + passing + " из " + near.Count);
                    foreach (KeyValuePair<string, List<int>> p in by)
                    {
                        p.Value.Sort();
                        W("    " + p.Key + ": рабочих " + p.Value.Count + ", лучший " + p.Value[0] + " мс, медиана " + p.Value[p.Value.Count / 2] + " мс");
                    }
                    W("  автовыбор sing-box: " + (SingBox.Now(clash) ?? "(нет ответа)"));
                }
                else W("  хвост журнала:\r\n" + Journal.Tail(SingBox.ReadLog(), 6));
                sb.Stop();
            }
        }
        if (a.Length < 3) { W(""); W("ГОТОВО"); return 0; }

        for (int v = 2; v < a.Length; v++)
        {
            string extra = a[v] == "-" ? "" : a[v].Replace(';', '\n') + "\n";
            W("");
            W("===== вариант: " + a[v] + " =====");
            bool judged = false;
            for (int tries = 0; tries < 6 && !judged && next < list.Count; tries++)
            {
                VpnServer s = list[next++];
                string cfg = s.DecodeConfig();
                int port = OpenVpnEngine.FreePort();
                File.WriteAllText(Paths.Config, ConfigBuilder.Build(cfg, port, "netsh") + extra, new UTF8Encoding(false));
                Stopwatch sw = Stopwatch.StartNew();
                bool connected = false, ended = false, assigned = false;
                StringBuilder states = new StringBuilder();
                OpenVpnEngine e = new OpenVpnEngine();
                e.StateChanged += delegate(OpenVpnEngine x, string name, string ip)
                {
                    states.Append(sw.Elapsed.TotalSeconds.ToString("0.0") + "s " + name + " ");
                    if (name == "ASSIGN_IP") assigned = true;
                    if (name == "CONNECTED") connected = true;
                };
                e.Exited += delegate { ended = true; };
                e.Start(exe, Paths.Config, port);
                while (!ended && !connected && sw.Elapsed.TotalSeconds < seconds) Thread.Sleep(100);
                W(s.CountryShort + " " + s.Ip + ": " + states);
                if (connected)
                {
                    Thread.Sleep(1500);
                    W("  ПОДКЛЮЧЕНО за " + sw.Elapsed.TotalSeconds.ToString("0.0") + " с; внешний IP через туннель: " + Ip());
                }
                e.Stop();
                Thread.Sleep(800);
                if (assigned || connected)
                {
                    judged = true;
                    if (!connected) W("  адрес выдан, но туннель не поднялся за " + seconds + " с. Хвост журнала:\r\n" + Journal.Tail(Journal.ReadLog(), 14));
                }
                else W("  до выдачи адреса не дошло (сервер отказал или недоступен)");
            }
            W("внешний IP после отключения: " + Ip());
        }
        W("");
        W("ГОТОВО");
        return 0;
    }
}
