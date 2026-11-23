using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using MiniVpn;

// Сравнение источников бесплатных серверов: из каждого файла берутся до N ближайших отвечающих серверов
// и проходят настоящую проверку скорости (VlessTester). Аргументы: <папка> <N> <файл1> [<файл2> ...]
static class SourceBench
{
    static int Main(string[] a)
    {
        string dir = a[0];
        int n = int.Parse(a[1]);
        List<string> others = Tunnels.Active();
        string bind = others.Count > 0 ? Tunnels.PhysicalInterface() : null;
        string exe = SingBox.FindExe();
        StringBuilder sum = new StringBuilder();
        for (int f = 2; f < a.Length; f++)
        {
            string path = Path.Combine(dir, a[f]);
            List<string> lines = Vless.SplitSubscription(File.ReadAllText(path, Encoding.UTF8));
            if (lines.Count > 1500) lines = lines.GetRange(0, 1500);      // как в приложении: из огромных сборников берётся начало
            List<VpnServer> all = Vless.ToServers(lines);
            int ri = -1;
            List<System.Threading.Thread> rts = new List<System.Threading.Thread>();
            for (int t = 0; t < 32; t++)
            {
                System.Threading.Thread th = new System.Threading.Thread(delegate() { int k; while ((k = System.Threading.Interlocked.Increment(ref ri)) < all.Count) Vless.ResolvePublic(all[k]); });
                th.IsBackground = true; th.Start(); rts.Add(th);
            }
            foreach (System.Threading.Thread th in rts) th.Join(60000);
            all.RemoveAll(delegate(VpnServer s) { return string.IsNullOrEmpty(s.Ip); });
            ServerPicker.Measure(all, 2000);
            int alive = 0;
            foreach (VpnServer s in all) if (s.Latency != -1) alive++;
            List<VpnServer> vl = new List<VpnServer>();
            foreach (VpnServer s in ServerPicker.Order(all, null, null, "")) if (s.Latency != -1) vl.Add(s);
            vl = ServerPicker.Diverse(vl, n, 30);
            Stopwatch sw = Stopwatch.StartNew();
            string note;
            VlessTester.Run(exe, vl, bind, 120000, 6, delegate { return true; }, null, out note);
            List<VpnServer> good = VlessTester.Good(vl);
            int fast = 0, nearFast = 0;
            foreach (VpnServer s in good) { if (s.Speed >= 10000000) fast++; if (s.Speed >= 10000000 && s.TestMs <= 120) nearFast++; }
            StringBuilder top = new StringBuilder();
            for (int i = 0; i < Math.Min(4, good.Count); i++)
                top.Append((i > 0 ? "; " : "") + good[i].CountryShort + " " + good[i].Source + " " + (good[i].Speed / 1000000) + " Мбит/с " + good[i].TestMs + " мс");
            string line = a[f] + ": серверов " + all.Count + ", отвечают по TCP " + alive + ", проверено " + vl.Count + ", годных " + good.Count +
                ", от 10 Мбит/с " + fast + " (из них с пингом до 120 мс " + nearFast + "), " + (sw.ElapsedMilliseconds / 1000) + " с | " + top;
            Console.WriteLine(line);
            sum.AppendLine(line);
            File.AppendAllText(Path.Combine(dir, "..", "live", "bench.txt"), line + Environment.NewLine, new UTF8Encoding(false));
        }
        return 0;
    }
}
