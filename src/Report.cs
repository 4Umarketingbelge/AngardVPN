using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MiniVpn
{
    /// Журнал событий приложения и попыток подключения; из него собирается отчёт об ошибке.
    public static class Journal
    {
        static readonly List<string> events = new List<string>();
        static readonly List<string> attempts = new List<string>();
        static readonly object sync = new object();

        public static void Add(string text)
        {
            lock (sync)
            {
                string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
                events.Add(line);
                if (events.Count > 300) events.RemoveAt(0);
                // Копия на диске (%LOCALAPPDATA%\AngardVPN\journal.txt): по ней разбираются проблемы без отчёта.
                try
                {
                    string f = Path.Combine(Paths.DataDir, "journal.txt");
                    FileInfo fi = new FileInfo(f);
                    if (fi.Exists && fi.Length > 1000000) File.Delete(f);
                    File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd ") + line + "\r\n", Encoding.UTF8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// Запоминает итог попытки и хвост журнала OpenVPN по ней.
        public static void AddAttempt(string header, string openVpnLog)
        {
            lock (sync)
            {
                attempts.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + header + "\r\n" + Tail(openVpnLog, 60));
                if (attempts.Count > 15) attempts.RemoveAt(0);
            }
            Add(header);
        }

        public static string Tail(string text, int lines)
        {
            if (string.IsNullOrEmpty(text)) return "    (журнал пуст)";
            string[] all = text.Replace("\r", "").Split('\n');
            StringBuilder sb = new StringBuilder();
            int count = 0;
            for (int i = all.Length - 1; i >= 0 && count < lines; i--)
            {
                if (all[i].Length == 0) continue;
                sb.Insert(0, "    " + all[i] + "\r\n");
                count++;
            }
            return sb.Length == 0 ? "    (журнал пуст)" : sb.ToString().TrimEnd();
        }

        public static string ReadLog()
        {
            try
            {
                using (FileStream fs = new FileStream(Paths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (IOException) { return ""; }
        }

        public static string Build(string reason, string[] facts)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Отчёт AngardVPN");
            sb.AppendLine("Создан:   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Причина:  " + reason);
            sb.AppendLine();
            sb.AppendLine("== Система ==");
            foreach (string f in facts) sb.AppendLine(f);
            sb.AppendLine();
            lock (sync)
            {
                sb.AppendLine("== События (" + events.Count + ") ==");
                foreach (string e in events) sb.AppendLine(e);
                sb.AppendLine();
                sb.AppendLine("== Попытки подключения (" + attempts.Count + ") ==");
                if (attempts.Count == 0) sb.AppendLine("(попыток не было)");
                foreach (string a in attempts) { sb.AppendLine(a); sb.AppendLine(); }
            }
            sb.AppendLine("== Текущий журнал OpenVPN ==");
            sb.AppendLine(Tail(ReadLog(), 80));
            return sb.ToString();
        }

        /// Сохраняет отчёт в «Документы\AngardVPN» и возвращает путь, либо null.
        public static string Save(string text)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AngardVPN");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(path, text, new UTF8Encoding(true));
                return path;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
