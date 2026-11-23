using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MiniVpn;

static class Tests
{
    static int failed, passed;

    static void Eq(object expected, object actual, string name)
    {
        if (object.Equals(expected, actual)) { passed++; return; }
        failed++;
        Console.WriteLine("FAIL " + name + ": ожидалось [" + expected + "], получено [" + actual + "]");
    }

    static string Names(List<VpnServer> l)
    {
        List<string> n = new List<string>();
        foreach (VpnServer s in l) n.Add(s.HostName);
        return string.Join(",", n.ToArray());
    }

    static string B64(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }

    const string Cfg = "# comment\r\ndev tun\r\nproto tcp\r\nremote 219.100.37.192 443\r\ncipher AES-128-CBC\r\nauth SHA1\r\nresolv-retry infinite\r\nnobind\r\npersist-key\r\npersist-tun\r\nclient\r\nverb 3\r\n<ca>\r\nAAAA\r\n</ca>\r\n";

    static int Main()
    {
        string pad = new string('x', 120);
        string good = B64(Cfg + "# " + pad);
        string csv =
            "*vpn_servers\r\n" +
            "#HostName,IP,Score,Ping,Speed,CountryLong,CountryShort,NumVpnSessions,Uptime,TotalUsers,TotalTraffic,LogType,Operator,Message,OpenVPN_ConfigData_Base64\r\n" +
            "public-vpn-261,219.100.37.192,2980352,18,286408933,Japan,jp,65,1,2,3,2weeks,Op,," + good + "\r\n" +
            "short,1.2.3.4,1,1\r\n" +                                              // мало полей
            "badip,not-an-ip,1,1,1,X,XX,1,1,1,1,l,o,," + good + "\r\n" +            // плохой адрес
            "badb64,5.6.7.8,1,1,1,X,XX,1,1,1,1,l,o,," + new string('!', 150) + "\r\n" + // битый base64
            "noremote,5.6.7.9,1,1,1,X,XX,1,1,1,1,l,o,," + B64(pad + pad) + "\r\n" + // конфиг без remote
            "empty,9.9.9.9,,,,Korea Republic of,KR,,1,1,1,l,o,," + good + "\r\n" +   // пустые числа
            "*\r\n";

        List<VpnServer> list = ServerList.Parse(csv);
        Eq(2, list.Count, "Parse: только годные строки");
        Eq("public-vpn-261", list[0].HostName, "Parse: имя");
        Eq("219.100.37.192", list[0].Ip, "Parse: адрес");
        Eq(2980352L, list[0].Score, "Parse: рейтинг");
        Eq(18, list[0].Ping, "Parse: пинг");
        Eq("JP", list[0].CountryShort, "Parse: код страны в верхнем регистре");
        Eq("Япония", list[0].CountryName, "Parse: название страны");
        Eq("286 Мбит/с", list[0].SpeedText, "Parse: скорость");
        Eq(0, list[1].Ping, "Parse: пустые числа = 0");
        Eq(0, ServerList.Parse("").Count, "Parse: пустой текст");
        Eq(0, ServerList.Parse(null).Count, "Parse: null");
        Eq("Wakanda", Countries.Name("WK", "Wakanda"), "Страна: неизвестный код");

        Eq("219.100.37.192", ConfigBuilder.GetRemoteIp(Cfg), "GetRemoteIp");
        Eq(null, ConfigBuilder.GetRemoteIp("dev tun\nclient\n"), "GetRemoteIp: нет remote");

        string built = ConfigBuilder.Build(Cfg, 7505, "netsh");
        Eq(true, built.Contains("ip-win32 netsh\n"), "Build: способ выдачи адреса");
        Eq(false, ConfigBuilder.Build(Cfg, 1, "").Contains("ip-win32"), "Build: без способа");
        Eq(false, ConfigBuilder.Build(Cfg + "ip-win32 dynamic\n", 1, "ipapi").Contains("ip-win32 dynamic"), "Build: чужой ip-win32 убран");
        Eq("ipapi", ConfigBuilder.NextIpMethod("netsh"), "IpMethod: netsh → ipapi");
        Eq("dynamic", ConfigBuilder.NextIpMethod("ipapi"), "IpMethod: ipapi → dynamic");
        Eq("netsh", ConfigBuilder.NextIpMethod("dynamic"), "IpMethod: по кругу");
        Eq("netsh", ConfigBuilder.NextIpMethod("мусор"), "IpMethod: неизвестный → первый");

        string rip; int rport; bool rtcp;
        ConfigBuilder.GetRemote(Cfg, out rip, out rport, out rtcp);
        Eq("219.100.37.192", rip, "GetRemote: адрес");
        Eq(443, rport, "GetRemote: порт");
        Eq(true, rtcp, "GetRemote: tcp");
        ConfigBuilder.GetRemote("proto udp\nremote 1.2.3.4 1195\n", out rip, out rport, out rtcp);
        Eq(false, rtcp, "GetRemote: udp");
        Eq(1195, rport, "GetRemote: порт udp");
        Eq(true, list[0].IsTcp, "Server: IsTcp");
        Eq(443, list[0].RemotePort, "Server: RemotePort");
        Eq("", list[0].LatencyText, "Server: задержка не измерена");

        // Порядок перебора
        List<VpnServer> pool = new List<VpnServer>();
        string[] names = { "far", "near", "dead", "udp", "bad", "good", "kr" };
        int[] lat = { 300, 40, -1, -2, 30, 320, 20 };
        long[] score = { 900, 100, 999, 500, 800, 50, 700 };
        for (int i = 0; i < names.Length; i++)
        {
            VpnServer v = new VpnServer();
            v.HostName = names[i]; v.Latency = lat[i]; v.Speed = score[i];
            v.CountryShort = names[i] == "kr" ? "KR" : "JP";
            pool.Add(v);
        }
        List<string> bad = new List<string>(); bad.Add("bad");
        Eq("kr,near,far,good,udp,bad,dead", Names(ServerPicker.Order(pool, null, bad, "good")), "Order: отклик, затем скорость; последний удачный без приоритета");
        Eq("kr,near,far,good,udp,bad,dead", Names(ServerPicker.Order(pool, null, bad, "")), "Order: без удачного");
        Eq("far,near,good,udp,bad,dead", Names(ServerPicker.Order(pool, pool[0], bad, "good")), "Order: выбран сервер, его страна");
        Eq("dead", Names(ServerPicker.Order(pool, pool[2], bad, "")).Split(',')[0], "Order: выбранный всегда первый");
        pool[1].Latency = 45; pool[4].Latency = 48; bad.Clear();
        Eq("bad,kr,near", Names(ServerPicker.Order(pool, null, bad, "")).Substring(0, 11), "Order: при близком отклике — по скорости");

        // Сортировка по близости, когда замеру задержки нельзя верить
        foreach (VpnServer v in pool) v.Latency = 24;
        pool[0].CountryShort = "FR"; pool[1].CountryShort = "JP"; pool[2].CountryShort = "UA"; pool[3].CountryShort = "US";
        pool[4].CountryShort = "RU"; pool[5].CountryShort = "KR"; pool[6].CountryShort = "ZZ";
        Eq("dead,bad,far,good,near,udp,kr", Names(ServerPicker.Order(pool, null, bad, "", false, "GE")), "Order: по близости к Грузии");
        Eq("near", Names(ServerPicker.Order(pool, null, bad, "", false, "KR")).Split(',')[1], "Order: по близости к Корее");
        Eq(true, Countries.Distance("GE", "UA") < Countries.Distance("GE", "JP"), "Distance: Украина ближе Японии");
        Eq(0, Countries.Distance("JP", "JP"), "Distance: та же страна");
        Eq(-1, Countries.Distance("GE", "ZZ"), "Distance: неизвестная страна");
        Eq(-1, Countries.Distance(null, "JP"), "Distance: null");
        int tokyoSeoul = Countries.Distance("JP", "KR");
        Eq(true, tokyoSeoul > 700 && tokyoSeoul < 1300, "Distance: Япония—Корея около 900 км");

        string probe = ConfigBuilder.BuildProbe(Cfg, 7600);
        Eq(true, probe.Contains("dev null\n"), "Probe: без адаптера");
        Eq(false, probe.Contains("dev tun"), "Probe: dev tun убран");
        Eq(true, probe.Contains("route-noexec\n") && probe.Contains("ifconfig-noexec\n"), "Probe: без маршрутов и адреса");
        Eq(false, probe.Contains("redirect-gateway") || probe.Contains("block-outside-dns") || probe.Contains("ip-win32"), "Probe: систему не трогает");
        Eq(true, probe.Contains("management 127.0.0.1 7600\n") && probe.Contains("<ca>\nAAAA\n</ca>"), "Probe: управление и сертификат");
        Eq(true, ConfigBuilder.Build(Cfg, 1, "netsh").Contains("dev tun"), "Build: обычный конфиг сохраняет dev tun");

        Eq(true, IpRanges.InternetExcept(new string[] { "1.2.3.4", "5.6.7.8" }).Contains("1.2.3.5-5.6.7.7,5.6.7.9-9.255.255.255"), "Ranges: несколько серверов");

        // Накопление серверов VPN Gate за три дня
        Dictionary<string, long> seen = new Dictionary<string, long>();
        seen["old"] = 10; seen["gone"] = 1;
        string merged = ServerList.Merge("*vpn_servers\n#Head\nnew,1.1.1.1,x\nold,2.2.2.2,fresh\n*\n", "old,2.2.2.2,stale\ngone,3.3.3.3,x\nunknown,4.4.4.4,x\n", seen, 100, 95);
        Eq("#Head\r\nnew,1.1.1.1,x\r\nold,2.2.2.2,fresh\r\n", merged, "Merge: свежие целиком, устаревший и незнакомый отброшены");
        Eq(false, seen.ContainsKey("gone"), "Merge: давно не виденный забыт");
        Eq(100L, seen["new"], "Merge: время свежего сервера");
        seen["kept"] = 50;
        Eq(true, ServerList.Merge("a,1.1.1.1,x\n", "kept,5.5.5.5,x\n", seen, 100, 95).Contains("kept,5.5.5.5,x"), "Merge: недавно виденный сохранён");
        Eq(false, ServerList.Merge("a,1.1.1.1,x\na,1.1.1.1,y\n", "", seen, 100, 95).Contains(",y"), "Merge: повтор убран");

        string withAuth = ConfigBuilder.Build("client\ndev tun1\nproto tcp\nremote 5.6.7.8 443\nauth-user-pass\nfast-io\nroute-delay 2\ncipher AES-256-GCM\n", 1, "netsh");
        Eq(true, withAuth.Contains("auth-user-pass \"") && withAuth.Contains("auth.txt\"\n"), "Build: логин берётся из файла");
        Eq(false, withAuth.Contains("auth-user-pass\n"), "Build: запрос логина в консоли убран");
        Eq(false, withAuth.Contains("\\"), "Build: путь к файлу без обратных косых");
        Eq(false, withAuth.Contains("fast-io") || withAuth.Contains("route-delay"), "Build: лишние параметры убраны");
        Eq(false, built.Contains("auth-user-pass"), "Build: серверу без логина файл не подставляется");
        Eq(true, ConfigBuilder.BuildProbe("remote 5.6.7.8 443\nauth-user-pass\n", 1).Contains("auth-user-pass \""), "Probe: логин и в гонке");
        VpnServer nb = new VpnServer(); nb.Source = "VLESS";
        Eq("VLESS", nb.SpeedText, "Server: без данных о скорости показан источник");

        // Подписка VLESS
        string nl = char.ConvertFromUtf32(0x1F1F3) + char.ConvertFromUtf32(0x1F1F1);     // флаг Нидерландов
        string link1 = "vless://11111111-2222-3333-4444-555555555555@nl1.example.com:443?type=tcp&security=reality&pbk=PUBKEY&sid=ab12&sni=www.example.org&fp=chrome&flow=xtls-rprx-vision#" + Uri.EscapeDataString(nl + " Нидерланды 1");
        VlessLink vl = Vless.Parse(link1);
        Eq(true, vl != null, "Vless: ссылка разобрана");
        Eq("nl1.example.com", vl.Host, "Vless: адрес");
        Eq(443, vl.Port, "Vless: порт");
        Eq("11111111-2222-3333-4444-555555555555", vl.Uuid, "Vless: идентификатор");
        Eq("reality", vl.Security, "Vless: защита");
        Eq("PUBKEY", vl.Pbk, "Vless: ключ Reality");
        Eq("xtls-rprx-vision", vl.Flow, "Vless: flow");
        Eq(nl + " Нидерланды 1", vl.Name, "Vless: название");
        Eq("NL", Vless.CountryFromName(vl.Name), "Vless: страна по флагу");
        Eq("Нидерланды 1", Vless.CleanName(vl.Name), "Vless: название без флага");
        Eq(null, Vless.CountryFromName("Без флага"), "Vless: флага нет");
        Eq("xtls-rprx-vision", Vless.Parse("vless://id@h:443?security=tls&flow=xtls-rprx-vision-udp443").Flow, "Vless: flow -udp443 приводится к vision");
        Eq("", Vless.Parse("vless://id@h:443?security=tls&flow=none").Flow, "Vless: flow none — без flow");
        Eq(null, Vless.Parse("vless://id@h:443?security=tls&flow=xtls-rprx-direct"), "Vless: устаревший flow отбрасывается");
        Eq("chrome", Vless.Parse("vless://id@h:443?security=tls&fp=chrome_auto").Fp, "Vless: незнакомый отпечаток → chrome");
        Eq("firefox", Vless.Parse("vless://id@h:443?security=tls&fp=Firefox").Fp, "Vless: отпечаток без учёта регистра");
        Eq(null, Vless.Parse("vmess://abc"), "Vless: чужой протокол");
        Eq(null, Vless.Parse("vless://id@host:443?type=xhttp&security=tls"), "Vless: транспорт xhttp не поддерживается");
        Eq(null, Vless.Parse("vless://id@host:443?security=reality"), "Vless: Reality без ключа");
        Eq(null, Vless.Parse("vless://id@host"), "Vless: нет порта");
        Eq(null, Vless.Parse("vless://@host:443"), "Vless: нет идентификатора");
        VlessLink ws = Vless.Parse("vless://id@1.2.3.4:8443?type=ws&security=tls&host=cdn.example.org&path=%2Fws%3Fed%3D2048&alpn=h2%2Chttp%2F1.1");
        Eq("/ws?ed=2048", ws.Path, "Vless: путь WebSocket");
        string wsJson = Vless.Outbound(ws, "s1", "");
        Eq(true, wsJson.Contains("\"transport\":{\"type\":\"ws\",\"path\":\"/ws\",\"headers\":{\"Host\":\"cdn.example.org\"},\"max_early_data\":2048"), "Vless: транспорт WebSocket");
        Eq(true, wsJson.Contains("\"alpn\":[\"h2\",\"http/1.1\"]") && wsJson.Contains("\"server_name\":\"cdn.example.org\""), "Vless: TLS для WebSocket");
        Eq(false, wsJson.Contains("\"flow\""), "Vless: flow только для tcp");
        string rJson = Vless.Outbound(vl, "s0", "9.9.9.9");
        Eq(true, rJson.StartsWith("{\"type\":\"vless\",\"tag\":\"s0\",\"server\":\"9.9.9.9\",\"server_port\":443,"), "Vless: адрес заменён на IP");
        Eq(true, rJson.Contains("\"reality\":{\"enabled\":true,\"public_key\":\"PUBKEY\",\"short_id\":\"ab12\"}") && rJson.Contains("\"flow\":\"xtls-rprx-vision\""), "Vless: Reality и flow");
        Eq("\"a\\\"b\\\\c\"", Vless.J("a\"b\\c"), "Vless: экранирование JSON");
        string subBody = Convert.ToBase64String(Encoding.UTF8.GetBytes(link1 + "\r\nvmess://x\r\n" + link1 + "\r\n")).TrimEnd('=');
        List<string> subLines = Vless.SplitSubscription(subBody);
        Eq(3, subLines.Count, "Vless: подписка в base64 без дополнения");
        Eq(2, Vless.SplitSubscription("vless://a@b:1\nvless://c@d:2").Count, "Vless: подписка открытым текстом");
        List<VpnServer> vs = Vless.ToServers(subLines);
        Eq(1, vs.Count, "Vless: повторы и чужие протоколы отброшены");
        Eq("NL", vs[0].CountryShort, "Vless: страна сервера");
        Eq("Нидерланды", vs[0].CountryName, "Vless: название страны");
        Eq("VLESS", vs[0].SpeedText, "Vless: вместо скорости источник");
        vs[0].Ip = "9.9.9.9";
        Eq(443, vs[0].RemotePort, "Vless: порт для замера");
        Eq(true, vs[0].IsTcp, "Vless: замер по TCP");
        string cfgJson = Vless.Config(vs, 19090, true, 0);
        Eq(true, cfgJson.Contains("\"type\":\"tun\"") && cfgJson.Contains("\"outbounds\":[\"s0\"]") && cfgJson.Contains("127.0.0.1:19090"), "Vless: конфигурация с туннелем");
        Eq(true, Vless.Config(vs, 1, false, 2080).Contains("\"type\":\"mixed\"") && !Vless.Config(vs, 1, false, 2080).Contains("\"type\":\"tun\""), "Vless: конфигурация без туннеля");
        Eq("s3", SingBox.ParseNow("{\"type\":\"URLTest\",\"now\":\"s3\",\"all\":[\"s0\"]}"), "SingBox: выбранный сервер");
        Eq(null, SingBox.ParseNow(null), "SingBox: нет ответа");
        long sd, su;
        Eq(true, SingBox.ParseTraffic("{\"downloadTotal\":1500,\"uploadTotal\":200,\"connections\":[]}", out sd, out su), "SingBox: трафик");
        Eq(1500L, sd, "SingBox: загружено");
        Eq(200L, su, "SingBox: отдано");

        // Выбор адреса источника: первый отвечающий, при отказе всех — null
        string[] bases = { "https://a/", "https://b/" };
        Eq("https://b/", Vless.ChooseBase(bases, delegate(string b) { return b == "https://b/"; }), "ChooseBase: запасной адрес");
        Eq("https://a/", Vless.ChooseBase(bases, delegate(string b) { return true; }), "ChooseBase: основной, если отвечает");
        Eq(null, Vless.ChooseBase(bases, delegate(string b) { return false; }), "ChooseBase: ни один не отвечает");
        // Группа серверов: не больше N из одной страны, остальное добирается
        List<VpnServer> dv = new List<VpnServer>();
        foreach (string cc in new string[] { "US", "US", "US", "NL", "US", "NL", "DE" })
        {
            VpnServer dvs = new VpnServer();
            dvs.CountryShort = cc;
            dvs.HostName = cc + dv.Count;
            dv.Add(dvs);
        }
        Eq("US0,US1,NL3,NL5,DE6", Names(ServerPicker.Diverse(dv, 5, 2)), "Diverse: не больше двух из страны");
        Eq("US0,US1,NL3,NL5,DE6,US2,US4", Names(ServerPicker.Diverse(dv, 7, 2)), "Diverse: места больше — добираются пропущенные");
        Eq("US0,US1,US2", Names(ServerPicker.Diverse(dv, 3, int.MaxValue)), "Diverse: без ограничения порядок прежний");
        Eq(0, ServerPicker.Diverse(new List<VpnServer>(), 5, 2).Count, "Diverse: пусто");

        Eq("author.today", NetCheck.CleanHost(" https://Author.Today/work/1?x=2 "), "CleanHost: адрес со схемой и путём");
        Eq("vk.ru", NetCheck.CleanHost("vk.ru:443"), "CleanHost: порт");
        Eq("google.com", NetCheck.CleanHost("google.com"), "CleanHost: просто имя");
        Eq("", NetCheck.CleanHost("bad_host!"), "CleanHost: недопустимые знаки");
        Eq("bad", NetCheck.CleanHost("bad host"), "CleanHost: после пробела отбрасывается");
        Eq("", NetCheck.CleanHost(null), "CleanHost: null");

        Eq(true, Tunnels.IsForeign("sing-tun Tunnel"), "Tunnels: sing-tun");
        Eq(true, Tunnels.IsForeign("Windscribe VPN"), "Tunnels: Windscribe");
        Eq(true, Tunnels.IsForeign("WireGuard Tunnel"), "Tunnels: WireGuard");
        Eq(false, Tunnels.IsForeign("TAP-Windows Adapter V9 #2"), "Tunnels: наш адаптер");
        Eq(false, Tunnels.IsForeign("Famatech Radmin VPN Ethernet Adapter"), "Tunnels: Radmin");
        Eq(false, Tunnels.IsForeign("Realtek PCIe GbE Family Controller"), "Tunnels: сетевая карта");
        Eq(false, Tunnels.IsForeign(null), "Tunnels: null");

        Eq("    b\r\n    c", Journal.Tail("a\nb\r\nc\n", 2), "Journal: хвост");
        Eq("    (журнал пуст)", Journal.Tail("", 5), "Journal: пусто");
        Eq(true, built.Contains("management 127.0.0.1 7505\n"), "Build: management");
        Eq(true, built.Contains("management-hold\n"), "Build: hold");
        Eq(true, built.Contains("redirect-gateway def1\n"), "Build: весь трафик в туннель");
        Eq(true, built.Contains("block-outside-dns\n"), "Build: защита DNS");
        Eq(true, built.Contains("data-ciphers-fallback AES-128-CBC\n"), "Build: шифр из конфига");
        Eq(true, built.Contains("resolv-retry 0\n"), "Build: resolv-retry 0");
        Eq(false, built.Contains("resolv-retry infinite"), "Build: старый resolv-retry убран");
        Eq(false, built.Contains("persist-key"), "Build: persist-key убран");
        Eq(true, built.Contains("persist-tun"), "Build: persist-tun сохранён");
        Eq(true, built.Contains("<ca>\nAAAA\n</ca>"), "Build: сертификат сохранён");
        Eq(false, built.Contains("\r"), "Build: без CR");
        Eq(false, ConfigBuilder.Build("dev tun\nremote 1.2.3.4 1\n", 1, "netsh").Contains("data-ciphers-fallback"), "Build: без cipher нет fallback");

        string n, ip;
        Eq(true, Mgmt.ParseState(">STATE:1700000000,CONNECTED,SUCCESS,10.211.1.5,219.100.37.192,443,,", out n, out ip), "State: разбор");
        Eq("CONNECTED", n, "State: имя");
        Eq("10.211.1.5", ip, "State: адрес туннеля");
        Eq(true, Mgmt.ParseState(">STATE:1,WAIT", out n, out ip), "State: короткая строка");
        Eq("", ip, "State: нет адреса");
        Eq(false, Mgmt.ParseState(">HOLD:Waiting", out n, out ip), "State: чужая строка");
        Eq(false, Mgmt.ParseState(null, out n, out ip), "State: null");
        long bi, bo;
        Eq(true, Mgmt.ParseBytes(">BYTECOUNT:123,456", out bi, out bo), "Bytes: разбор");
        Eq(123L, bi, "Bytes: in");
        Eq(456L, bo, "Bytes: out");
        Eq(false, Mgmt.ParseBytes(">BYTECOUNT:abc", out bi, out bo), "Bytes: мусор");

        Eq("0.0.0.0-10.0.0.4,10.0.0.6-255.255.255.255", IpRanges.AllExcept("10.0.0.5"), "Ranges: всё кроме одного");
        Eq("0.0.0.1-255.255.255.255", IpRanges.AllExcept("0.0.0.0"), "Ranges: край снизу");
        Eq("0.0.0.0-255.255.255.254", IpRanges.AllExcept("255.255.255.255"), "Ranges: край сверху");
        List<uint[]> ov = new List<uint[]>();
        ov.Add(IpRanges.R("10.0.0.0", "10.0.0.255"));
        ov.Add(IpRanges.R("10.0.0.100", "10.0.1.0"));
        ov.Add(IpRanges.R("0.0.0.0", "0.0.0.0"));
        Eq("0.0.0.1-9.255.255.255,10.0.1.1-255.255.255.255", IpRanges.Complement(ov), "Ranges: пересечения");
        Eq("1.0.0.0-9.255.255.255,11.0.0.0-126.255.255.255,128.0.0.0-169.253.255.255,169.255.0.0-172.15.255.255,172.32.0.0-192.167.255.255,192.169.0.0-219.100.37.191,219.100.37.193-223.255.255.255",
           IpRanges.InternetExcept("219.100.37.192"), "Ranges: интернет без сервера");
        Eq(true, IpRanges.InternetExcept((string)null).Contains("192.169.0.0-223.255.255.255"), "Ranges: без сервера");

        Eq("512 Б", Fmt.Bytes(512), "Fmt: байты");
        Eq("1.5 МБ", Fmt.Bytes(1572864), "Fmt: мегабайты");

        // Настоящая проверка VLESS: медленные и неработающие не попадают в автовыбор, порядок — по скорости.
        VlessLink vlk = Vless.Parse("vless://u@1.2.3.4:443?security=tls&sni=a.b#x");
        VpnServer fast = new VpnServer(), slow = new VpnServer(), dead = new VpnServer(), fresh = new VpnServer();
        foreach (VpnServer v in new VpnServer[] { fast, slow, dead, fresh }) { v.Vless = vlk; v.Ip = "1.2.3.4"; v.CountryShort = "NL"; v.HostName = "h"; v.Latency = 20; }
        VlessTester.Apply(fast, 300, 25.0);
        VlessTester.Apply(slow, 200, 0.6);
        VlessTester.Apply(dead, -1, 0);
        VlessTester.Apply(fresh, 400, 8.0);
        Eq(false, fast.TestedBad, "Проверка VLESS: быстрый годен");
        Eq(true, slow.TestedBad, "Проверка VLESS: медленнее порога негоден");
        Eq(true, dead.TestedBad, "Проверка VLESS: не ответивший негоден");
        Eq(25000000L, fast.Speed, "Проверка VLESS: скорость в бит/с");
        List<VpnServer> four = new List<VpnServer>(new VpnServer[] { slow, fresh, dead, fast });
        List<VpnServer> goodV = VlessTester.Good(four);
        Eq(2, goodV.Count, "Проверка VLESS: годных два");
        Eq(true, goodV[0] == fast && goodV[1] == fresh, "Проверка VLESS: быстрее раньше");
        List<VpnServer> ord = ServerPicker.Order(four, null, null, "");
        Eq(true, ord.IndexOf(slow) > ord.IndexOf(fresh) && ord.IndexOf(dead) > ord.IndexOf(fast), "Порядок: не прошедшие проверку в конце, хоть отклик и мал");
        slow.TestedAt = DateTime.Now.AddHours(-2);
        Eq(false, slow.TestedFresh, "Проверка VLESS: старая проверка не считается");
        Eq(1, VlessTester.Good(new List<VpnServer>(new VpnServer[] { slow, fast })).Count, "Проверка VLESS: устаревшие не годны");
        List<VpnServer> grp = new List<VpnServer>();
        foreach (double mb in new double[] { 52, 39, 42, 33, 12, 9, 8, 30, 25, 50 }) { VpnServer g = new VpnServer(); VlessTester.Apply(g, 60, mb); grp.Add(g); }
        List<VpnServer> gsel = VlessTester.Group(grp, 8);
        Eq(7, gsel.Count, "Группа: только не медленнее 40% от лучшего");
        Eq(true, !gsel.Contains(grp[4]) && !gsel.Contains(grp[5]) && gsel[0] == grp[0], "Группа: медленные отброшены, порядок сохранён");
        Eq(3, VlessTester.Group(grp.GetRange(4, 3), 8).Count + 0 * VlessTester.Group(new List<VpnServer>(new VpnServer[] { grp[0], grp[4], grp[5] }), 8).Count, "Группа: не меньше трёх");
        Eq(3, VlessTester.Group(new List<VpnServer>(new VpnServer[] { grp[0], grp[4], grp[5], grp[6] }), 8).Count, "Группа: запас дополняется до трёх");
        Eq(4, VlessTester.Group(grp, 4).Count, "Группа: не больше предела");
        string bound = Vless.Config(new List<VpnServer>(new VpnServer[] { fast }), 9090, true, 0, "Ethernet");
        Eq(true, bound.Contains("\"default_interface\":\"Ethernet\""), "Конфиг: обход чужого VPN через сетевую карту");
        Eq(false, Vless.Config(new List<VpnServer>(new VpnServer[] { fast }), 9090, true, 0).Contains("default_interface"), "Конфиг: без обхода по умолчанию");

        // Другие протоколы: Trojan, VMess, Shadowsocks, Hysteria2.
        VlessLink tj = Vless.Parse("trojan://p%40ss@5.6.7.8:443?sni=t.example&type=ws&path=%2Ftj&host=cdn.t#TJ");
        Eq("trojan", tj == null ? null : tj.Proto, "Trojan: протокол");
        Eq("p@ss", tj.Uuid, "Trojan: пароль раскодирован");
        Eq("tls", tj.Security, "Trojan: TLS по умолчанию");
        string tjo = Vless.Outbound(tj, "s0", "5.6.7.8");
        Eq(true, tjo.Contains("\"type\":\"trojan\"") && tjo.Contains("\"password\":\"p@ss\"") && tjo.Contains("\"server_name\":\"t.example\"") && tjo.Contains("\"type\":\"ws\""), "Trojan: исходящее");
        string vmJson = "{\"v\":\"2\",\"ps\":\"VM 1\",\"add\":\"9.8.7.6\",\"port\":\"8080\",\"id\":\"abcd-ef\",\"aid\":0,\"scy\":\"auto\",\"net\":\"ws\",\"type\":\"none\",\"host\":\"h.example\",\"path\":\"/vm\",\"tls\":\"tls\",\"sni\":\"s.example\"}";
        VlessLink vm = Vless.Parse("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(vmJson)));
        Eq("vmess", vm == null ? null : vm.Proto, "VMess: протокол");
        Eq(8080, vm.Port, "VMess: порт строкой");
        Eq("VM 1", vm.Name, "VMess: имя");
        string vmo = Vless.Outbound(vm, "s1", null);
        Eq(true, vmo.Contains("\"type\":\"vmess\"") && vmo.Contains("\"uuid\":\"abcd-ef\"") && vmo.Contains("\"path\":\"/vm\"") && vmo.Contains("\"server_name\":\"s.example\""), "VMess: исходящее");
        Eq(null, Vless.Parse("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"add\":\"a\",\"port\":1,\"id\":\"x\",\"net\":\"tcp\",\"type\":\"http\"}"))), "VMess: tcp с HTTP-маскировкой не поддерживается");
        VlessLink ss1 = Vless.Parse("ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:secret")) + "@1.1.1.1:8388#SS");
        Eq("chacha20-ietf-poly1305", ss1 == null ? null : ss1.Method, "SS: шифр (SIP002)");
        Eq("secret", ss1.Uuid, "SS: пароль");
        VlessLink ss2 = Vless.Parse("ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pw@2.2.2.2:443")) + "#old");
        Eq(443, ss2 == null ? 0 : ss2.Port, "SS: старый формат целиком в base64");
        Eq(null, Vless.Parse("ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("rc4-md5:pw")) + "@1.1.1.1:1"), "SS: устаревший шифр отбрасывается");
        Eq(null, Vless.Parse("ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pw")) + "@1.1.1.1:1?plugin=obfs-local"), "SS: плагины не поддерживаются");
        string sso = Vless.Outbound(ss1, "s2", "1.1.1.1");
        Eq(true, sso.Contains("\"type\":\"shadowsocks\"") && sso.Contains("\"method\":\"chacha20-ietf-poly1305\"") && !sso.Contains("tls"), "SS: исходящее");
        VlessLink hy = Vless.Parse("hysteria2://pw@3.3.3.3:443?sni=h.example&insecure=1&obfs=salamander&obfs-password=zz#HY");
        Eq("hysteria2", hy == null ? null : hy.Proto, "Hysteria2: протокол");
        Eq(true, hy.Udp, "Hysteria2: UDP");
        string hyo = Vless.Outbound(hy, "s3", "3.3.3.3");
        Eq(true, hyo.Contains("\"type\":\"hysteria2\"") && hyo.Contains("\"salamander\"") && hyo.Contains("\"insecure\":true") && hyo.Contains("\"h3\""), "Hysteria2: исходящее");
        Eq("hysteria2", Vless.Parse("hy2://pw@3.3.3.3:8443").Proto, "Hysteria2: короткая схема hy2");
        Eq(true, Vless.IsLink("trojan://x@y:1") && Vless.IsLink(" ss://abc") && !Vless.IsLink("http://x"), "IsLink");
        VpnServer udpS = new VpnServer(); udpS.Vless = hy; udpS.Ip = "3.3.3.3";
        Eq(false, udpS.IsTcp, "Hysteria2: не замеряется по TCP");

        // Несколько источников: зеркала и разбор готовых списков.
        List<string> mir = Vless.Mirrors("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/BLACK_SS%2BAll_RUS.txt");
        Eq(3, mir.Count, "Источники: три адреса");
        Eq("https://cdn.jsdelivr.net/gh/igareck/vpn-configs-for-russia@main/BLACK_SS%2BAll_RUS.txt", mir[1], "Источники: адрес jsDelivr");
        Eq(1, Vless.Mirrors("https://example.org/list.txt").Count, "Источники: не GitHub — без зеркал");
        string nlFlag = char.ConvertFromUtf32(0x1F1F3) + char.ConvertFromUtf32(0x1F1F1);
        List<string> fl = Vless.FlatLines("# profile-update-interval: 1\nvless://u@1.2.3.4:443?security=tls#" + Uri.EscapeDataString(nlFlag + " NL") +
            "\nhttp://1.2.3.4:80\ntrojan://p@5.6.7.8:443#noflag\nvless://broken\n", 10);
        Eq(2, fl.Count, "Источники: комментарии, http и битые ссылки отброшены");
        Eq(true, fl[0].StartsWith("NL|") && fl[1].StartsWith("??|"), "Источники: страна по флагу, иначе ??");
        Eq(1, Vless.FlatLines("trojan://p@5.6.7.8:443\ntrojan://p@5.6.7.9:443", 1).Count, "Источники: предел строк");
        Eq(2, Vless.FreeServers(fl).Count, "Источники: строки ?? принимаются");

        // Проверка настроек ядром: находит исходящий, который валит запуск.
        string sbExe = SingBox.FindExe();
        if (sbExe != null)
        {
            string cf = Path.Combine(Path.GetTempPath(), "angard-check-test.json");
            string okOb = "{\"type\":\"vless\",\"tag\":\"s0\",\"server\":\"1.2.3.4\",\"server_port\":443,\"uuid\":\"11111111-2222-3333-4444-555555555555\"}";
            string badOb = "{\"type\":\"vless\",\"tag\":\"s1\",\"server\":\"1.2.3.5\",\"server_port\":443,\"uuid\":\"11111111-2222-3333-4444-555555555555\",\"flow\":\"xtls-rprx-vision-udp443\"}";
            string why;
            File.WriteAllText(cf, "{\"outbounds\":[" + okOb + "," + badOb + "]}");
            Eq(1, SingBox.BadOutbound(sbExe, cf, out why), "Ядро: найден непринятый сервер");
            File.WriteAllText(cf, "{\"outbounds\":[" + okOb + "]}");
            Eq(-1, SingBox.BadOutbound(sbExe, cf, out why), "Ядро: верные настройки приняты");
        }
        else Console.WriteLine("ПРОПУСК: sing-box не найден, проверка ядром не выполнялась");

        Console.WriteLine("Пройдено: " + passed + ", провалено: " + failed);
        return failed == 0 ? 0 : 1;
    }
}
