using System;
using System.Collections.Generic;
using System.Diagnostics;
using MiniVpn;

// Живая проверка кода приложения без прав администратора: загрузка общедоступного списка VLESS
// и настоящая проверка скорости (VlessTester) — те же функции, что вызывает окно. Аргумент: сколько серверов проверить.
static class LiveCheck
{
    static int Main(string[] a)
    {
        int n = a.Length > 0 ? int.Parse(a[0]) : 120;
        List<string> others = Tunnels.Active();
        Console.WriteLine("другие VPN: " + (others.Count == 0 ? "нет" : string.Join(", ", others.ToArray())) + "; сетевая карта: " + (Tunnels.PhysicalInterface() ?? "не найдена") + " " + Tunnels.PhysicalIp());
        Stopwatch sw = Stopwatch.StartNew();
        string note;
        List<VpnServer> all = Vless.LoadFree(out note);
        Console.WriteLine("загрузка списка за " + sw.ElapsedMilliseconds / 1000 + " с: " + note + ", с сети: " + Vless.LastFreeOk);
        ServerPicker.Measure(all, 2000);
        List<VpnServer> vl = new List<VpnServer>();
        foreach (VpnServer s in ServerPicker.Order(all, null, null, "")) if (s.Latency != -1) vl.Add(s);
        vl = ServerPicker.Diverse(vl, n, 25);
        string exe = SingBox.FindExe();
        int good = VlessTester.Run(exe, vl, others.Count > 0 ? Tunnels.PhysicalInterface() : null, 150000, 12, delegate { return true; }, null, out note);
        Console.WriteLine("проверка: " + note);
        List<VpnServer> g = VlessTester.Good(vl);
        for (int i = 0; i < Math.Min(15, g.Count); i++) Console.WriteLine("  " + g[i].CountryShort + " " + g[i].SpeedText + " отклик " + g[i].TestMs + " мс " + g[i].Ip);
        int jp = 0, jpGood = 0;
        foreach (VpnServer s in vl) if (s.CountryShort == "JP") { jp++; if (!s.TestedBad) jpGood++; }
        Console.WriteLine("Япония VLESS: проверено " + jp + ", годных " + jpGood);
        return good > 0 ? 0 : 1;
    }
}
