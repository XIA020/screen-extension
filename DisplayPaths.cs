using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace TabletDisplay
{
    public static class DisplayPaths
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct Source
        {
            public long Adapter;
            public uint Id, Mode, Status;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct Target
        {
            public long Adapter;
            public uint Id, Mode, Technology, Rotation, Scaling;
            public uint RefreshNumerator, RefreshDenominator, Scanline, Available, Status;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct DisplayPath
        {
            public Source Source;
            public Target Target;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 4, Size = 64)]
        private struct Mode
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(4)] public uint Id;
            [FieldOffset(8)] public long Adapter;
            // DISPLAYCONFIG_SOURCE_MODE overlays the union starting at offset 16.
            [FieldOffset(16)] public int Width;
            [FieldOffset(20)] public int Height;
            [FieldOffset(28)] public int X;
            [FieldOffset(32)] public int Y;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct Header
        {
            public uint Type, Size;
            public long Adapter;
            public uint Id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        private struct TargetName
        {
            public Header Header;
            public uint Flags, Technology;
            public ushort Manufacturer, Product;
            public uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        private struct SourceName
        {
            public Header Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DisplayName;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths,
            ref uint modeCount, [Out] Mode[] modes, IntPtr topology);

        [DllImport("user32.dll")]
        private static extern int SetDisplayConfig(uint pathCount, DisplayPath[] paths, uint modeCount, Mode[] modes, uint flags);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int GetTargetName(ref TargetName name);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int GetSourceName(ref SourceName name);

        private const int InsufficientBuffer = 122;
        private const uint Active = 1, AllPaths = 1, ActivePaths = 2, InvalidMode = uint.MaxValue;
        private const uint Supplied = 0x20, Validate = 0x40, Apply = 0x80, AllowChanges = 0x400;
        private const uint SaveToDatabase = 0x200, SourceMode = 1;

        public sealed class DesktopSource
        {
            public string Name;
            public string Identity;
            public int X, Y, Width, Height;
            public bool Primary { get { return X == 0 && Y == 0; } }
        }

        public sealed class DesktopLayout
        {
            private readonly DisplayPath[] paths;
            private readonly Mode[] modes;
            private readonly Dictionary<string, int> sourceModes =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public DesktopSource[] Sources { get; private set; }
            public string Revision { get; private set; }

            internal DesktopLayout()
            {
                paths = Read(ActivePaths, out modes);
                var sources = new List<DesktopSource>();
                foreach (var group in paths.GroupBy(path => path.Source.Mode))
                {
                    if (group.Key >= modes.Length || modes[group.Key].Type != SourceMode)
                        throw new Win32Exception(1610);
                    int index = (int)group.Key;
                    var mode = modes[index];
                    string name = DisplayName(group.First().Source);
                    if (string.IsNullOrEmpty(name) || mode.Width <= 0 || mode.Height <= 0)
                        throw new Win32Exception(1610);
                    sourceModes.Add(name, index);
                    string identity = string.Join(",", group.Select(path =>
                        path.Target.Adapter + ":" + path.Target.Id).OrderBy(value => value, StringComparer.Ordinal));
                    sources.Add(new DesktopSource {
                        Name = name, Identity = mode.Adapter + ":" + mode.Id + ":" + identity,
                        X = mode.X, Y = mode.Y, Width = mode.Width, Height = mode.Height
                    });
                }
                Sources = sources.OrderByDescending(source => source.Primary)
                    .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                Revision = string.Join("|", Sources.Select(source => string.Join("/", new object[] {
                    source.Name, source.Identity, source.X, source.Y, source.Width, source.Height
                })));
            }

            public int ApplyPositions(IDictionary<string, Tuple<int, int>> positions)
            {
                if (positions.Count == 0) return 0;
                var updated = (Mode[])modes.Clone();
                foreach (var position in positions)
                {
                    int index = sourceModes[position.Key];
                    updated[index].X = position.Value.Item1;
                    updated[index].Y = position.Value.Item2;
                }
                uint pathCount = (uint)paths.Length, modeCount = (uint)updated.Length;
                int result = SetDisplayConfig(pathCount, paths, modeCount, updated, Supplied | Validate);
                return result != 0 ? result :
                    SetDisplayConfig(pathCount, paths, modeCount, updated, Supplied | Apply | SaveToDatabase);
            }
        }

        public static DesktopLayout ReadDesktopLayout()
        {
            return new DesktopLayout();
        }

        private static void Check(int result)
        {
            if (result != 0) throw new Win32Exception(result);
        }

        private static DisplayPath[] Read(uint flags, out Mode[] modes)
        {
            for (int retry = 0; retry < 4; retry++)
            {
                uint pathCount, modeCount;
                Check(GetDisplayConfigBufferSizes(flags, out pathCount, out modeCount));
                var paths = new DisplayPath[pathCount];
                modes = new Mode[modeCount];
                int result = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (result == InsufficientBuffer) continue;
                Check(result);
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
                return paths;
            }
            throw new Win32Exception(InsufficientBuffer);
        }

        private static string DisplayName(Source source)
        {
            var value = new SourceName { Header = new Header {
                Type = 1, Size = (uint)Marshal.SizeOf(typeof(SourceName)), Adapter = source.Adapter, Id = source.Id
            }};
            return GetSourceName(ref value) == 0 ? value.DisplayName : null;
        }

        private static TargetName ReadTargetName(Target target)
        {
            var value = new TargetName { Header = new Header {
                Type = 2, Size = (uint)Marshal.SizeOf(typeof(TargetName)), Adapter = target.Adapter, Id = target.Id
            }};
            return GetTargetName(ref value) == 0 ? value : new TargetName();
        }

        private static bool IsMtt(Target target)
        {
            return string.Equals(ReadTargetName(target).FriendlyName, "VDD by MTT", StringComparison.OrdinalIgnoreCase);
        }

        public static string VirtualTarget(string display, bool mtt)
        {
            Mode[] modes;
            foreach (var path in Read(ActivePaths, out modes))
                if (MatchesVirtualTarget(path.Target, mtt) &&
                    string.Equals(DisplayName(path.Source), display, StringComparison.OrdinalIgnoreCase))
                    return ReadTargetName(path.Target).DevicePath;
            return null;
        }

        public static string ActiveVirtualDisplay(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            Mode[] modes;
            foreach (var path in Read(ActivePaths, out modes))
                if (string.Equals(ReadTargetName(path.Target).DevicePath, target, StringComparison.OrdinalIgnoreCase))
                    return DisplayName(path.Source);
            return null;
        }

        private static bool MatchesVirtualTarget(Target target, bool mtt)
        {
            if (mtt) return IsMtt(target);
            string devicePath = ReadTargetName(target).DevicePath;
            return devicePath != null && devicePath.IndexOf(@"DISPLAY#PSCCDD", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Change(IList<DisplayPath> paths, Mode[] modes)
        {
            var copy = paths.ToArray();
            uint count = (uint)copy.Length, modeCount = (uint)modes.Length;
            Check(SetDisplayConfig(count, copy, modeCount, modes, Supplied | Validate | AllowChanges));
            Check(SetDisplayConfig(count, copy, modeCount, modes, Supplied | Apply | AllowChanges));
        }

        public static string EnableUnusedMtt()
        {
            return EnableVirtual(true, null, null);
        }

        public static string EnableVirtual(bool mtt, string requiredTarget, string excludedTarget)
        {
            Mode[] modes, unused;
            var active = Read(ActivePaths, out modes);
            foreach (var option in Read(AllPaths, out unused))
            {
                if (option.Target.Available == 0 || !MatchesVirtualTarget(option.Target, mtt)) continue;
                string target = ReadTargetName(option.Target).DevicePath;
                if ((!string.IsNullOrEmpty(requiredTarget) && !string.Equals(target, requiredTarget, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(excludedTarget) && string.Equals(target, excludedTarget, StringComparison.OrdinalIgnoreCase))) continue;
                var connected = active.FirstOrDefault(path => SameTarget(path.Target, option.Target));
                var retained = active;
                if ((connected.Flags & Active) != 0)
                {
                    if (!active.Any(path => !SameTarget(path.Target, connected.Target) &&
                        path.Source.Adapter == connected.Source.Adapter && path.Source.Id == connected.Source.Id))
                    {
                        if (!string.IsNullOrEmpty(requiredTarget)) return DisplayName(connected.Source);
                        continue;
                    }
                    retained = active.Where(path => !SameTarget(path.Target, option.Target)).ToArray();
                }
                if (retained.Any(path => path.Source.Adapter == option.Source.Adapter && path.Source.Id == option.Source.Id))
                    continue;

                var candidate = option;
                candidate.Flags |= Active;
                candidate.Source.Mode = candidate.Target.Mode = InvalidMode;
                Change(retained.Concat(new[] { candidate }).ToArray(), modes);
                var current = Read(ActivePaths, out unused);
                foreach (var path in current)
                {
                    if (SameTarget(path.Target, candidate.Target)) return DisplayName(path.Source);
                }
                return null;
            }
            return null;
        }

        public static bool HasClonedVirtualOutput(bool mtt, string requiredTarget, string excludedTarget)
        {
            Mode[] modes;
            var active = Read(ActivePaths, out modes);
            return active.GroupBy(path => new { path.Source.Adapter, path.Source.Id }).Any(group =>
                group.Count() > 1 && group.Any(path => {
                    if (!MatchesVirtualTarget(path.Target, mtt)) return false;
                    string name = ReadTargetName(path.Target).DevicePath;
                    return (string.IsNullOrEmpty(requiredTarget) || string.Equals(name, requiredTarget, StringComparison.OrdinalIgnoreCase)) &&
                        (string.IsNullOrEmpty(excludedTarget) || !string.Equals(name, excludedTarget, StringComparison.OrdinalIgnoreCase));
                }));
        }

        public static int AvailableMttCount()
        {
            return AvailableVirtualCount(true);
        }

        public static int AvailableVirtualCount(bool mtt)
        {
            Mode[] modes;
            return Read(AllPaths, out modes).Where(path => path.Target.Available != 0 && MatchesVirtualTarget(path.Target, mtt))
                .Select(path => new { path.Target.Adapter, path.Target.Id }).Distinct().Count();
        }

        public static bool DisableMtt(string display)
        {
            return DisableVirtual(display, true);
        }

        public static bool DisableVirtual(string display, bool mtt)
        {
            Mode[] modes;
            var active = Read(ActivePaths, out modes);
            var remaining = active.Where(path => !MatchesVirtualTarget(path.Target, mtt) ||
                !string.Equals(DisplayName(path.Source), display, StringComparison.OrdinalIgnoreCase) ||
                (path.Source.Mode < modes.Length && modes[path.Source.Mode].X == 0 && modes[path.Source.Mode].Y == 0)).ToArray();
            if (remaining.Length == active.Length || remaining.Length == 0) return false;
            Change(remaining, modes);
            return true;
        }

        private static bool SameTarget(Target first, Target second)
        {
            return first.Adapter == second.Adapter && first.Id == second.Id;
        }
    }
}
