using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using MiniVpn;

// Рисует значок приложения тем же кодом, что и значок в трее (Theme.DrawLogo).
// Аргументы: <папка>. Создаёт app.ico и картинки для просмотра: icon-preview.png, tray-preview.png.
static class IconGen
{
    static byte[] Png(int size, VState state, bool plate)
    {
        using (Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                Theme.DrawLogo(g, new RectangleF(size * 0.03f, 0, size * 0.94f, size), state);
            }
            using (MemoryStream ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    static int Main(string[] a)
    {
        string dir = a[0];
        int[] sizes = { 16, 20, 24, 32, 48, 64, 256 };
        List<byte[]> pngs = new List<byte[]>();
        foreach (int s in sizes) pngs.Add(Png(s, VState.On, false));
        using (BinaryWriter w = new BinaryWriter(File.Create(Path.Combine(dir, "app.ico"))))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte s = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(s); w.Write(s); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write((uint)pngs[i].Length); w.Write((uint)offset);
                offset += pngs[i].Length;
            }
            foreach (byte[] p in pngs) w.Write(p);
        }

        // Просмотр: большой значок и размеры трея на тёмной и светлой панели задач, во всех состояниях.
        using (Bitmap b = new Bitmap(760, 300))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(0x10, 0x13, 0x18));
                g.FillRectangle(Brushes.Gainsboro, 380, 0, 380, 300);
                Theme.DrawLogo(g, new RectangleF(20, 20, 240, 256), VState.On);
                VState[] states = { VState.Off, VState.Connecting, VState.On, VState.Blocked };
                int[] tray = { 16, 20, 24, 32, 48 };
                for (int half = 0; half < 2; half++)
                    for (int r = 0; r < states.Length; r++)
                    {
                        int x = (half == 0 ? 280 : 400);
                        for (int c = 0; c < tray.Length; c++)
                        {
                            Theme.DrawLogo(g, new RectangleF(x, 30 + r * 64, tray[c], tray[c]), states[r]);
                            x += tray[c] + 14;
                        }
                    }
            }
            b.Save(Path.Combine(dir, "icon-preview.png"), ImageFormat.Png);
        }
        return 0;
    }
}
