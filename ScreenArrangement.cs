using System;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TabletDisplay;

public static class ScreenArrangement
{
    [StructLayout(LayoutKind.Explicit, Size = 220, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(72)] public uint Fields;
        [FieldOffset(76)] public int X;
        [FieldOffset(80)] public int Y;
        [FieldOffset(168)] public int BitsPerPixel;
        [FieldOffset(172)] public int Width;
        [FieldOffset(176)] public int Height;
        [FieldOffset(184)] public int Frequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode settings);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DisplayMode settings,
        IntPtr window, uint flags, IntPtr parameters);

    private static IEnumerable<DisplayMode> AvailableModes(string display)
    {
        for (int index = 0; ; index++)
        {
            var mode = new DisplayMode { Size = 220 };
            if (!EnumDisplaySettings(display, index, ref mode)) yield break;
            if (mode.Width > 0 && mode.Height > 0 && mode.BitsPerPixel >= 32) yield return mode;
        }
    }

    public sealed class Resolution
    {
        public Size Size;
        public int[] Rates;
    }

    public static Resolution[] Resolutions(string display)
    {
        return AvailableModes(display).GroupBy(mode => new Size(mode.Width, mode.Height))
            .OrderByDescending(group => (long)group.Key.Width * group.Key.Height).ThenByDescending(group => group.Key.Width)
            .Select(group => new Resolution {
                Size = group.Key,
                Rates = group.Select(mode => mode.Frequency).Where(rate => rate > 1).Distinct().OrderBy(rate => rate).ToArray()
            }).ToArray();
    }

    public static int SetResolution(string display, int width, int height, int? refreshRate)
    {
        var current = new DisplayMode { Size = 220 };
        if (!EnumDisplaySettings(display, -1, ref current)) return -2;
        if (current.Width == width && current.Height == height &&
            (!refreshRate.HasValue || current.Frequency == refreshRate.Value)) return 0;

        const uint updateRegistry = 1, test = 2;
        const uint modeFields = 0x00040000 | 0x00080000 | 0x00100000 | 0x00400000;
        foreach (var candidate in AvailableModes(display).Where(mode => mode.Width == width && mode.Height == height &&
            (!refreshRate.HasValue || mode.Frequency == refreshRate.Value))
            .OrderBy(mode => Math.Abs((long)mode.Frequency - current.Frequency)))
        {
            var requested = candidate;
            requested.Fields &= modeFields;
            if (ChangeDisplaySettingsEx(display, ref requested, IntPtr.Zero, test, IntPtr.Zero) == 0)
                return ChangeDisplaySettingsEx(display, ref requested, IntPtr.Zero, updateRegistry, IntPtr.Zero);
        }
        return -2;
    }

    public static Rectangle Read(string device)
    {
        int ignored;
        return Read(device, out ignored);
    }

    public static Rectangle Read(string device, out int refreshRate)
    {
        refreshRate = 0;
        if (device == null) return Rectangle.Empty;
        var mode = new DisplayMode { Size = 220 };
        if (!EnumDisplaySettings(device, -1, ref mode)) return Rectangle.Empty;
        refreshRate = mode.Frequency > 1 ? mode.Frequency : 0;
        return new Rectangle(mode.X, mode.Y, mode.Width, mode.Height);
    }

    public static HashSet<string> MovableDisplays()
    {
        var devices = MonitorProbe.ExtendedVirtualDisplays("MttVDD");
        if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "managed.json")))
            devices = devices.Concat(MonitorProbe.ExtendedVirtualDisplays("Parsec"));
        return new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase);
    }

    public static string FindExtendedDisplay()
    {
        string settings = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "managed.json");
        return MonitorProbe.FindExtendedVirtualDisplay(System.IO.File.Exists(settings) ? "MttVDD" : "Parsec");
    }

    public static bool Adjacent(Rectangle main, Rectangle extended)
    {
        return ((main.Left == extended.Right || main.Right == extended.Left) &&
                Math.Min(main.Bottom, extended.Bottom) > Math.Max(main.Top, extended.Top))
            || ((main.Top == extended.Bottom || main.Bottom == extended.Top) &&
                Math.Min(main.Right, extended.Right) > Math.Max(main.Left, extended.Left));
    }

    public static bool ValidLayout(Rectangle[] rectangles, int primary)
    {
        if (primary < 0) return false;
        var joined = new HashSet<int> { primary };
        for (int i = 0; i < rectangles.Length; i++)
            for (int j = i + 1; j < rectangles.Length; j++)
                if (rectangles[i].IntersectsWith(rectangles[j])) return false;
        for (int previous = 0; previous < joined.Count; )
        {
            previous = joined.Count;
            for (int index = 0; index < rectangles.Length; index++)
                if (!joined.Contains(index) && joined.Any(other => Adjacent(rectangles[other], rectangles[index])))
                    joined.Add(index);
        }
        return joined.Count == rectangles.Length;
    }

    public static bool PreservePositions(DisplayPaths.DesktopLayout previous, string added)
    {
        var current = DisplayPaths.ReadDesktopLayout();
        var rectangles = current.Sources.Select(source => {
            var old = previous.Sources.FirstOrDefault(item => item.Identity == source.Identity);
            return new Rectangle(old == null ? source.X : old.X, old == null ? source.Y : old.Y,
                source.Width, source.Height);
        }).ToArray();
        int primary = Array.FindIndex(current.Sources, source => source.Primary);
        if (primary < 0) return false;
        if (!ValidLayout(rectangles, primary))
        {
            int index = Array.FindIndex(current.Sources, source => !source.Primary && source.Name == added);
            if (index < 0) return false;
            var occupied = rectangles.Where((rectangle, i) => i != index).ToList();
            if (!ValidLayout(occupied.ToArray(), occupied.IndexOf(rectangles[primary]))) return false;
            var relocated = ParkIdleDisplay(rectangles[index], occupied);
            if (relocated.IsEmpty) return false;
            rectangles[index] = relocated;
        }
        var positions = new Dictionary<string, Tuple<int, int>>();
        for (int index = 0; index < current.Sources.Length; index++)
        {
            var source = current.Sources[index];
            var rectangle = rectangles[index];
            if (!source.Primary && (source.X != rectangle.X || source.Y != rectangle.Y))
                positions[source.Name] = Tuple.Create(rectangle.X, rectangle.Y);
        }
        return current.ApplyPositions(positions) == 0;
    }

    public static Rectangle ParkIdleDisplay(Rectangle idle, IList<Rectangle> occupied)
    {
        Rectangle best = Rectangle.Empty;
        double distance = double.PositiveInfinity;
        foreach (Rectangle anchor in occupied)
        {
            // Keeping an entire edge aligned is sufficient because each row is
            // also tried at the exterior edges of the existing desktop.
            foreach (Point point in new[] {
                new Point(anchor.Left - idle.Width, anchor.Top),
                new Point(anchor.Right, anchor.Top),
                new Point(anchor.Left, anchor.Top - idle.Height),
                new Point(anchor.Left, anchor.Bottom)
            })
            {
                var candidate = new Rectangle(point, idle.Size);
                if (Math.Abs((long)point.X) > 30000 || Math.Abs((long)point.Y) > 30000 ||
                    occupied.Any(other => other.IntersectsWith(candidate))) continue;
                double dx = (double)point.X - idle.X, dy = (double)point.Y - idle.Y;
                double score = dx * dx + dy * dy;
                if (score >= distance) continue;
                best = candidate;
                distance = score;
            }
        }
        return best;
    }
}

