using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

class Program
{
    static int Main(string[] args)
    {
        try
        {
            var outPath = Path.Combine("..", "..", "sample_addons", "SimpleHello", "textures", "frame9.png");
            outPath = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            using var bmp = new Bitmap(64, 64);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(255, 240, 240, 240));
            using (var pen = new Pen(Color.FromArgb(255, 60, 60, 60), 4))
            {
                g.DrawRectangle(pen, 2, 2, 59, 59);
            }
            using (var brush = new SolidBrush(Color.FromArgb(255, 100, 180, 255)))
            {
                g.FillRectangle(brush, 8, 8, 48, 48);
            }
            bmp.Save(outPath, ImageFormat.Png);
            Console.WriteLine("Wrote: " + outPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex);
            return 2;
        }
    }
}
