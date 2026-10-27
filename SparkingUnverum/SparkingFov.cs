using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace SparkingUnverum
{
    // Field of view with SparkingFOV, a mod by another author (Nexus Mods 630): a version.dll next to the game
    // that reads DBSZ_SparkingFOV.ini. To take it out of the program remove this file, UI/FovWindow,
    // Resources/SparkingFOV with its lines in the .csproj, the FOV tab of MainWindow, the credit in Settings and the Fov.* texts
    public static class SparkingFov
    {
        public const string ModName = "SparkingFOV";
        public const string Author = "Zett";
        public const string ModUrl = "https://www.nexusmods.com/dragonballsparkingzero/mods/630";
        public const string IniName = "DBSZ_SparkingFOV.ini";
        public const string DllName = "version.dll";
        // Text inside the mod's dll, tells it apart from other version.dll proxies
        private const string Marker = "DBSZ_SparkingFOV";
        private const string Section = "SETTINGS";
        private const string MultiplierKey = "FOVMultiplier";
        private const string MaximumKey = "MaximumFOV";

        // The slider shows the multiplier as a percentage, 100 % is the game's own view
        public const int DefaultPercent = 100;
        public const int MinPercent = 50;
        public const int MaxPercent = 250;
        public const int DefaultMaximum = 120;
        public const int MinMaximum = 60;
        public const int MaxMaximum = 170;
        // Multiples of 10 pull the slider when it gets this close
        public const int SnapStep = 10;
        public const int SnapRange = 3;

        public enum State { Missing, Installed, OtherDll }

        public static string DllPath(GamePaths paths) => Path.Combine(paths.Win64, DllName);
        public static string IniPath(GamePaths paths) => Path.Combine(paths.Win64, IniName);

        public static State Check(GamePaths paths)
        {
            var dll = DllPath(paths);
            if (!File.Exists(dll))
                return State.Missing;
            return IsOurs(dll) ? (File.Exists(IniPath(paths)) ? State.Installed : State.Missing) : State.OtherDll;
        }

        public static bool IsOurs(string dll)
        {
            try
            {
                var bytes = File.ReadAllBytes(dll);
                var marker = Encoding.ASCII.GetBytes(Marker);
                return bytes.AsSpan().IndexOf(marker) >= 0;
            }
            catch
            {
                return false;
            }
        }

        // Puts the mod's files in the game if they aren't there, never replaces another mod's version.dll
        public static State Install(GamePaths paths)
        {
            var state = Check(paths);
            if (state != State.Missing)
                return state;
            Directory.CreateDirectory(paths.Win64);
            if (!File.Exists(DllPath(paths)))
                Extract(DllName, DllPath(paths));
            if (!File.Exists(IniPath(paths)))
                Extract(IniName, IniPath(paths));
            Global.logger?.WriteLine(Loc.T("Fov.Installed", paths.Win64), LoggerType.Info);
            return Check(paths);
        }

        private static void Extract(string name, string target)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var resource = assembly.GetManifestResourceStream("SparkingFOV/" + name);
            using var stream = new FileStream(target, FileMode.Create, FileAccess.Write);
            resource.CopyTo(stream);
        }

        // Takes the mod out of the game, only its own dll
        public static void Remove(GamePaths paths)
        {
            var dll = DllPath(paths);
            if (File.Exists(dll) && IsOurs(dll))
                File.Delete(dll);
            if (File.Exists(IniPath(paths)))
                File.Delete(IniPath(paths));
            Global.logger?.WriteLine(Loc.T("Fov.Removed"), LoggerType.Info);
        }

        // Values of the ini, the defaults when it's missing or can't be read
        public static (int Percent, int Maximum) Read(GamePaths paths)
        {
            int percent = DefaultPercent, maximum = DefaultMaximum;
            try
            {
                if (File.Exists(IniPath(paths)))
                    foreach (var line in File.ReadAllLines(IniPath(paths)))
                    {
                        if (TryValue(line, MultiplierKey, out var multiplier))
                            percent = (int)Math.Round(multiplier * 100);
                        else if (TryValue(line, MaximumKey, out var value))
                            maximum = (int)Math.Round(value);
                    }
            }
            catch { }
            return (Math.Clamp(percent, MinPercent, MaxPercent), Math.Clamp(maximum, MinMaximum, MaxMaximum));
        }

        // Changes only the two values, comments and anything else in the ini stay
        public static void Write(GamePaths paths, int percent, int maximum)
        {
            var multiplier = (Math.Clamp(percent, MinPercent, MaxPercent) / 100.0).ToString("0.##", CultureInfo.InvariantCulture);
            var maximumText = Math.Clamp(maximum, MinMaximum, MaxMaximum).ToString(CultureInfo.InvariantCulture);
            var lines = File.Exists(IniPath(paths)) ? new List<string>(File.ReadAllLines(IniPath(paths))) : new List<string>();
            bool hasMultiplier = false, hasMaximum = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (IsKey(lines[i], MultiplierKey))
                {
                    lines[i] = $"{MultiplierKey}={multiplier}";
                    hasMultiplier = true;
                }
                else if (IsKey(lines[i], MaximumKey))
                {
                    lines[i] = $"{MaximumKey}={maximumText}";
                    hasMaximum = true;
                }
            }
            var section = lines.FindIndex(l => l.Trim().Equals($"[{Section}]", StringComparison.OrdinalIgnoreCase));
            if (section < 0)
            {
                lines.Insert(0, $"[{Section}]");
                section = 0;
            }
            if (!hasMaximum)
                lines.Insert(section + 1, $"{MaximumKey}={maximumText}");
            if (!hasMultiplier)
                lines.Insert(section + 1, $"{MultiplierKey}={multiplier}");
            Directory.CreateDirectory(paths.Win64);
            File.WriteAllText(IniPath(paths), string.Join("\r\n", lines) + "\r\n");
        }

        private static bool IsKey(string line, string key)
        {
            var equals = line.IndexOf('=');
            return equals > 0 && !line.TrimStart().StartsWith(";") && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryValue(string line, string key, out double value)
        {
            value = 0;
            if (!IsKey(line, key))
                return false;
            var text = line[(line.IndexOf('=') + 1)..];
            var comment = text.IndexOf(';');
            if (comment >= 0)
                text = text[..comment];
            return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // Whole numbers, pulled to the nearest multiple of 10 when close to it
        public static int Snap(double value)
        {
            var nearest = Math.Round(value / SnapStep) * SnapStep;
            return (int)(Math.Abs(value - nearest) <= SnapRange ? nearest : Math.Round(value));
        }
    }
}
