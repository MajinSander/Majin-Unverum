using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SparkingUnverum
{
    // What a file of a ReShade install is, for the info of a slot and to know what can be left alone
    public enum ReShadeFileKind { Dll, Config, Preset, Shader, Addon, Upscaler, Log }

    public class ReShadeFile
    {
        // Relative to Binaries/Win64, with \ like Windows writes it
        public string Path { get; set; }
        public ReShadeFileKind Kind { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
        // Last write time when it was saved, files in the game with the same size and time are not read again
        public long Written { get; set; }
    }

    // A saved ReShade: its files in Majin Unverum's folder, and what was found inside to tell slots apart
    public class ReShadeSlot
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Notes { get; set; }
        public DateTime Created { get; set; }
        public DateTime Updated { get; set; }
        public DateTime? LastUsed { get; set; }
        public DateTime? LastCheck { get; set; }
        public bool? Healthy { get; set; }
        public List<ReShadeFile> Files { get; set; } = new();
        public ReShadeInfo Info { get; set; } = new();

        [JsonIgnore]
        public string Folder => System.IO.Path.Combine(ReShadeSlots.SlotsPath, Id);
        [JsonIgnore]
        public string FilesFolder => System.IO.Path.Combine(Folder, "files");
        [JsonIgnore]
        public long Size => Files.Sum(f => f.Size);
        // Same files, same fingerprint: shows when two slots are the same ReShade
        [JsonIgnore]
        public string Fingerprint => ReShadeSlots.Fingerprint(Files);
    }

    public class ReShadeInfo
    {
        public string Version { get; set; }
        // Name of the dll the game loads (dxgi.dll, d3d12.dll...) or ReShade64.dll when another mod loads it
        public string Dll { get; set; }
        public string Preset { get; set; }
        public List<string> Presets { get; set; } = new();
        public int Techniques { get; set; }
        public int Effects { get; set; }
        public List<string> Addons { get; set; } = new();
        public string Dlss { get; set; }
        public string DlssFrameGen { get; set; }
        public string OptiScaler { get; set; }
        public bool HasReShade { get; set; }
    }

    public enum ReShadeState
    {
        // Nothing of ReShade in the game
        None,
        // The game has a saved slot, maybe with settings changed since (Changed)
        Slot,
        // The game has a ReShade that no slot has
        Unknown
    }

    public class ReShadeStatus
    {
        public ReShadeState State { get; init; }
        public ReShadeSlot Slot { get; init; }
        // Files of the game that differ from the slot or are new, saved into it before switching
        public List<string> Changed { get; init; } = new();
        public List<ReShadeFile> Found { get; init; } = new();
        public ReShadeInfo Info { get; init; }
    }

    // ReShade slots: takes ReShade (and the upscaler files that usually come with it, like a newer DLSS or OptiScaler)
    // out of the game into a named slot, and puts back one slot, another or none.
    // Files of other things the program puts in Win64 (UTOC bypass, UE4SS, SparkingFOV) are never touched
    public static class ReShadeSlots
    {
        public static string RootPath => System.IO.Path.Combine(Global.assemblyLocation, "ReShade");
        public static string SlotsPath => System.IO.Path.Combine(RootPath, "Slots");
        // Game files a slot had to replace, put back when no slot is used
        public static string OriginalsPath => System.IO.Path.Combine(RootPath, "Originals");
        private static string StatePath => System.IO.Path.Combine(RootPath, "state.json");
        private const string ManifestName = "slot.json";

        // Names the game can load ReShade with, and the name it has when OptiScaler or an ASI loader loads it
        private static readonly string[] ProxyDlls = { "dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "ReShade64.dll" };
        private static readonly string[] UpscalerFiles =
        {
            "nvngx_dlss.dll", "nvngx_dlssg.dll", "nvngx_dlssd.dll", "nvngx.dll", "nvngx.ini", "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll",
            "amd_fidelityfx_dx12.dll", "amd_fidelityfx_vk.dll", "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll",
            "OptiScaler.ini", "OptiScaler.log", "OptiScaler.dll", "nvapi64.dll", "fakenvapi.ini", "fakenvapi.log", "dlssg_to_fsr3_amd_is_better.dll"
        };
        // Put in Win64 by Majin Unverum or the game itself, never part of a slot
        private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        {
            "dsound.dll", "version.dll", "dwmapi.dll", "ue4ss.dll", "opengl32.dll", "patternsleuth_bind.dll", "UE4SS-settings.ini",
            SparkingFov.IniName, "UE4SS.log", "UE4SS_ObjectDump.txt"
        };
        private static readonly HashSet<string> ProtectedFolders = new(StringComparer.OrdinalIgnoreCase) { "Mods", "plugins" };
        private static readonly string[] ShaderFolders = { "reshade-shaders", "reshade-presets", "reshade-addons" };

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private class SavedState
        {
            public string ActiveSlot { get; set; }
            public List<string> Originals { get; set; } = new();
        }

        // ---------- What is in the game ----------

        // ReShade files in Win64, empty when there is no ReShade (nor upscaler files)
        public static List<ReShadeFile> Scan(GamePaths paths)
        {
            var win64 = paths.Win64;
            var found = new Dictionary<string, ReShadeFileKind>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(win64))
                return new();
            var hasReShade = false;
            foreach (var name in ProxyDlls)
            {
                var file = System.IO.Path.Combine(win64, name);
                if (!File.Exists(file))
                    continue;
                var kind = DllKind(file);
                if (kind != null)
                {
                    found[name] = kind.Value;
                    hasReShade |= kind == ReShadeFileKind.Dll;
                }
            }
            foreach (var name in UpscalerFiles)
                if (File.Exists(System.IO.Path.Combine(win64, name)))
                    found[name] = name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ? ReShadeFileKind.Log : ReShadeFileKind.Upscaler;
            var reshadeIni = System.IO.Path.Combine(win64, "ReShade.ini");
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(win64))
            {
                var name = System.IO.Path.GetFileName(file);
                if (Protected.Contains(name) || found.ContainsKey(name))
                    continue;
                var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
                if (name.StartsWith("ReShade", StringComparison.OrdinalIgnoreCase) && (ext == ".ini" || ext == ".log" || ext == ".json"))
                    found[name] = ext == ".log" ? ReShadeFileKind.Log : name.Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase) ? ReShadeFileKind.Config
                        : IsPreset(file) ? ReShadeFileKind.Preset : ReShadeFileKind.Config;
                else if (ext == ".addon" || ext == ".addon64" || ext == ".addon32")
                    found[name] = ReShadeFileKind.Addon;
                else if (ext == ".ini" && (hasReShade || File.Exists(reshadeIni)) && IsPreset(file))
                    found[name] = ReShadeFileKind.Preset;
            }
            foreach (var dir in Directory.GetDirectories(win64))
                if (ShaderFolders.Contains(System.IO.Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
                    folders.Add(System.IO.Path.GetFileName(dir));
            // Folders and presets named in ReShade.ini, only the ones inside Win64
            if (File.Exists(reshadeIni))
                foreach (var line in ReadLines(reshadeIni))
                {
                    var (key, value) = KeyValue(line);
                    if (key == null)
                        continue;
                    if (key.Equals("PresetPath", StringComparison.OrdinalIgnoreCase))
                    {
                        var relative = Inside(win64, value);
                        if (relative != null && File.Exists(System.IO.Path.Combine(win64, Native(relative))) && !Protected.Contains(System.IO.Path.GetFileName(Native(relative))))
                            found.TryAdd(relative, ReShadeFileKind.Preset);
                    }
                    else if (key.Equals("EffectSearchPaths", StringComparison.OrdinalIgnoreCase) || key.Equals("TextureSearchPaths", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("PresetSearchPaths", StringComparison.OrdinalIgnoreCase))
                        foreach (var part in value.Split(','))
                        {
                            var relative = Inside(win64, part.Trim().TrimEnd('*').TrimEnd('\\', '/'));
                            var top = relative?.Split('\\')[0];
                            if (!string.IsNullOrEmpty(top) && !ProtectedFolders.Contains(top) && Directory.Exists(System.IO.Path.Combine(win64, top)))
                                folders.Add(top);
                        }
                }
            foreach (var folder in folders)
                foreach (var file in Directory.GetFiles(System.IO.Path.Combine(win64, folder), "*", SearchOption.AllDirectories))
                    found.TryAdd(System.IO.Path.GetRelativePath(win64, file).Replace('/', '\\'), ReShadeFileKind.Shader);
            // Only upscaler files without ReShade, OptiScaler or a config of ReShade: still worth a slot
            return found.Select(f =>
            {
                var info = new FileInfo(System.IO.Path.Combine(win64, Native(f.Key)));
                return new ReShadeFile { Path = f.Key, Kind = f.Value, Size = info.Length, Written = info.LastWriteTimeUtc.Ticks };
            }).OrderBy(f => f.Kind).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Dll for ReShade or OptiScaler (shown as upscaler), null for any other dll with that name
        private static ReShadeFileKind? DllKind(string file)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch { return null; }
            var product = VersionString(bytes, "ProductName") ?? "";
            if (product.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) || Contains(bytes, "OptiScaler"))
                return ReShadeFileKind.Upscaler;
            if (product.Contains("ReShade", StringComparison.OrdinalIgnoreCase) || Contains(bytes, "ReShade"))
                return ReShadeFileKind.Dll;
            return null;
        }

        private static bool Contains(byte[] bytes, string text) =>
            bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0 || bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;

        // A value of the version resource of a dll (ProductVersion, FileVersion, ProductName), read from the bytes
        // so it works the same everywhere
        public static string VersionString(byte[] bytes, string key)
        {
            var pattern = Encoding.Unicode.GetBytes(key + "\0");
            var at = bytes.AsSpan().IndexOf(pattern);
            if (at < 0)
                return null;
            var i = at + pattern.Length;
            // Values start aligned to 4 bytes after some padding
            while (i + 1 < bytes.Length && bytes[i] == 0 && bytes[i + 1] == 0)
                i += 2;
            var text = new StringBuilder();
            for (; i + 1 < bytes.Length && text.Length < 64; i += 2)
            {
                var c = (char)(bytes[i] | bytes[i + 1] << 8);
                if (c == '\0')
                    break;
                text.Append(c);
            }
            var value = text.ToString().Trim();
            return value.Length == 0 ? null : value;
        }

        private static string DllVersion(string file)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                var version = VersionString(bytes, "ProductVersion") ?? VersionString(bytes, "FileVersion");
                return version?.Replace(',', '.').Replace(" ", "");
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPreset(string file) => ReadLines(file).Any(l => l.TrimStart().StartsWith("Techniques=", StringComparison.OrdinalIgnoreCase)
            || l.TrimStart().StartsWith("TechniqueSorting=", StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<string> ReadLines(string file)
        {
            try
            {
                return File.ReadAllLines(file);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static (string Key, string Value) KeyValue(string line)
        {
            var equals = line.IndexOf('=');
            if (equals <= 0 || line.TrimStart().StartsWith(";") || line.TrimStart().StartsWith("#"))
                return (null, null);
            return (line[..equals].Trim(), line[(equals + 1)..].Trim().Trim('"'));
        }

        // A path from ReShade.ini relative to Win64, null when it points outside
        private static string Inside(string win64, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            try
            {
                var normalized = value.Replace('/', '\\');
                var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(normalized) || normalized.Length > 1 && normalized[1] == ':'
                    ? normalized : System.IO.Path.Combine(win64, normalized.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
                var relative = System.IO.Path.GetRelativePath(win64, full);
                if (relative == "." || relative.StartsWith("..") || System.IO.Path.IsPathRooted(relative))
                    return null;
                return relative.Replace('/', '\\');
            }
            catch
            {
                return null;
            }
        }

        // What can be seen in a set of files (in the game or in a slot folder)
        public static ReShadeInfo Describe(string root, IEnumerable<ReShadeFile> files)
        {
            var list = files.ToList();
            var info = new ReShadeInfo();
            string Full(ReShadeFile f) => System.IO.Path.Combine(root, Native(f.Path));
            var dll = list.FirstOrDefault(f => f.Kind == ReShadeFileKind.Dll);
            if (dll != null)
            {
                info.HasReShade = true;
                info.Dll = System.IO.Path.GetFileName(dll.Path);
                info.Version = DllVersion(Full(dll));
            }
            info.Presets = list.Where(f => f.Kind == ReShadeFileKind.Preset).Select(f => f.Path).ToList();
            var ini = list.FirstOrDefault(f => f.Path.Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase));
            string preset = null;
            if (ini != null)
                foreach (var line in ReadLines(Full(ini)))
                {
                    var (key, value) = KeyValue(line);
                    if (key != null && key.Equals("PresetPath", StringComparison.OrdinalIgnoreCase))
                        preset = info.Presets.FirstOrDefault(p => p.Equals(Inside(root, value), StringComparison.OrdinalIgnoreCase));
                }
            preset ??= info.Presets.FirstOrDefault(p => p.Equals("ReShadePreset.ini", StringComparison.OrdinalIgnoreCase)) ?? info.Presets.FirstOrDefault();
            info.Preset = preset;
            if (preset != null)
                foreach (var line in ReadLines(System.IO.Path.Combine(root, Native(preset))))
                {
                    var (key, value) = KeyValue(line);
                    if (key != null && key.Equals("Techniques", StringComparison.OrdinalIgnoreCase))
                        info.Techniques = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
                }
            info.Effects = list.Count(f => f.Path.EndsWith(".fx", StringComparison.OrdinalIgnoreCase));
            info.Addons = list.Where(f => f.Kind == ReShadeFileKind.Addon).Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Path)).ToList();
            var dlss = list.FirstOrDefault(f => f.Path.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
            if (dlss != null)
                info.Dlss = DllVersion(Full(dlss)) ?? "?";
            var frameGen = list.FirstOrDefault(f => f.Path.Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase));
            if (frameGen != null)
                info.DlssFrameGen = DllVersion(Full(frameGen)) ?? "?";
            var opti = list.FirstOrDefault(f => f.Kind == ReShadeFileKind.Upscaler && f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && ProxyDlls.Contains(f.Path, StringComparer.OrdinalIgnoreCase));
            if (opti != null || list.Any(f => f.Path.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)))
                info.OptiScaler = opti != null ? DllVersion(Full(opti)) ?? "?" : "?";
            return info;
        }

        private static string Native(string relative) => relative.Replace('\\', System.IO.Path.DirectorySeparatorChar);

        public static string Hash(string file)
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        public static string Fingerprint(IEnumerable<ReShadeFile> files)
        {
            // Logs change every time the game opens, they don't make two ReShades different
            var text = string.Join("\n", files.Where(f => f.Kind != ReShadeFileKind.Log).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.Path.ToLowerInvariant() + ":" + f.Sha256));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
        }

        // ---------- Slots ----------

        public static List<ReShadeSlot> List()
        {
            if (!Directory.Exists(SlotsPath))
                return new();
            var slots = new List<ReShadeSlot>();
            foreach (var dir in Directory.GetDirectories(SlotsPath))
            {
                var manifest = System.IO.Path.Combine(dir, ManifestName);
                if (!File.Exists(manifest))
                    continue;
                try
                {
                    var slot = JsonSerializer.Deserialize<ReShadeSlot>(File.ReadAllText(manifest), Json);
                    slot.Id = System.IO.Path.GetFileName(dir);
                    slot.Files ??= new();
                    slot.Info ??= new();
                    slots.Add(slot);
                }
                catch (Exception e)
                {
                    Global.logger?.WriteLine(Loc.T("ReShade.SlotReadError", System.IO.Path.GetFileName(dir), e.Message), LoggerType.Warning);
                }
            }
            return slots.OrderBy(s => s.Created).ToList();
        }

        public static ReShadeSlot Get(string id) => List().FirstOrDefault(s => s.Id == id);

        private static void Save(ReShadeSlot slot)
        {
            Directory.CreateDirectory(slot.Folder);
            var temp = System.IO.Path.Combine(slot.Folder, ManifestName + ".tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(slot, Json));
            File.Move(temp, System.IO.Path.Combine(slot.Folder, ManifestName), true);
        }

        public static string CheckName(string name, string exceptId = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Loc.T("Edit.Empty");
            if (List().Any(s => s.Id != exceptId && s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                return Loc.T("ReShade.NameTaken");
            return null;
        }

        public static void Rename(ReShadeSlot slot, string name, string notes)
        {
            slot.Name = name.Trim();
            slot.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            Save(slot);
        }

        // Copies the ReShade of the game into a new slot. With remove, the game stays without it (after checking the copy).
        // Returns the slot that has it: an existing one when it was already saved (duplicate true)
        public static ReShadeSlot Capture(GamePaths paths, string name, string notes, bool remove, out bool duplicate)
        {
            duplicate = false;
            var status = GetStatus(paths);
            if (status.State == ReShadeState.None)
                throw new InvalidOperationException(Loc.T("ReShade.NothingInGame"));
            var files = status.Found;
            foreach (var file in files)
                file.Sha256 = Hash(System.IO.Path.Combine(paths.Win64, Native(file.Path)));
            var fingerprint = Fingerprint(files);
            var same = List().FirstOrDefault(s => s.Fingerprint == fingerprint);
            ReShadeSlot slot;
            if (same != null)
            {
                duplicate = true;
                slot = same;
            }
            else
            {
                var id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                for (int n = 2; Directory.Exists(System.IO.Path.Combine(SlotsPath, id)); n++)
                    id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + n;
                slot = new ReShadeSlot { Id = id, Name = name.Trim(), Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), Created = DateTime.Now, Updated = DateTime.Now };
                var building = slot.Folder + ".tmp";
                if (Directory.Exists(building))
                    Directory.Delete(building, true);
                try
                {
                    foreach (var file in files)
                    {
                        var target = System.IO.Path.Combine(building, "files", Native(file.Path));
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                        File.Copy(System.IO.Path.Combine(paths.Win64, Native(file.Path)), target);
                        File.SetLastWriteTimeUtc(target, new DateTime(file.Written, DateTimeKind.Utc));
                        if (Hash(target) != file.Sha256)
                            throw new IOException(Loc.T("ReShade.CopyMismatch", file.Path));
                    }
                    slot.Files = files;
                    slot.Info = Describe(System.IO.Path.Combine(building, "files"), files);
                    slot.Healthy = true;
                    slot.LastCheck = DateTime.Now;
                    File.WriteAllText(System.IO.Path.Combine(building, ManifestName), JsonSerializer.Serialize(slot, Json));
                    Directory.Move(building, slot.Folder);
                }
                catch
                {
                    try { Directory.Delete(building, true); } catch { }
                    throw;
                }
                Global.logger?.WriteLine(Loc.T("ReShade.Captured", slot.Name, files.Count, StringConverters.FormatSize(slot.Size)), LoggerType.Info);
            }
            var state = ReadState();
            if (remove)
            {
                // The game's settings may be newer than what a duplicate had (only logs can differ, the fingerprint matched)
                DeleteFromGame(paths, files.Select(f => f.Path));
                RestoreOriginals(paths, state);
                state.ActiveSlot = null;
                Global.logger?.WriteLine(Loc.T("ReShade.RemovedFromGame", slot.Name), LoggerType.Info);
            }
            else
                state.ActiveSlot = slot.Id;
            WriteState(state);
            return slot;
        }

        // Reads all the files of a slot again: the ones missing or changed in Majin Unverum's folder
        public static List<string> Verify(ReShadeSlot slot)
        {
            var problems = new List<string>();
            foreach (var file in slot.Files)
            {
                var path = System.IO.Path.Combine(slot.FilesFolder, Native(file.Path));
                if (!File.Exists(path))
                    problems.Add(Loc.T("ReShade.FileMissing", file.Path));
                else if (Hash(path) != file.Sha256)
                    problems.Add(Loc.T("ReShade.FileChanged", file.Path));
            }
            slot.Healthy = problems.Count == 0;
            slot.LastCheck = DateTime.Now;
            Save(slot);
            return problems;
        }

        // What the game has now: none, a slot (with the files changed since) or a ReShade not saved anywhere
        public static ReShadeStatus GetStatus(GamePaths paths)
        {
            var found = Scan(paths);
            if (found.Count == 0)
                return new ReShadeStatus { State = ReShadeState.None };
            var state = ReadState();
            var slots = List();
            ReShadeSlot match = null;
            List<string> changed = null;
            // The slot the program put first, then any slot that is exactly what the game has
            foreach (var slot in slots.OrderByDescending(s => s.Id == state.ActiveSlot))
            {
                var diff = Differences(paths, slot, found, out var binariesMatch);
                if (slot.Id == state.ActiveSlot && binariesMatch || diff.Count == 0)
                {
                    match = slot;
                    changed = diff;
                    break;
                }
            }
            var info = Describe(paths.Win64, found);
            if (match == null)
                return new ReShadeStatus { State = ReShadeState.Unknown, Found = found, Info = info };
            return new ReShadeStatus { State = ReShadeState.Slot, Slot = match, Changed = changed, Found = found, Info = info };
        }

        // Files of the game not equal to the slot (changed or new); the dlls must be the same for it to be that slot
        private static List<string> Differences(GamePaths paths, ReShadeSlot slot, List<ReShadeFile> found, out bool binariesMatch)
        {
            var byPath = slot.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var changed = new List<string>();
            binariesMatch = slot.Files.Where(IsBinary).All(f => found.Any(g => g.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase)));
            foreach (var file in found)
            {
                if (!byPath.TryGetValue(file.Path, out var saved))
                {
                    if (IsBinary(file))
                        binariesMatch = false;
                    if (file.Kind != ReShadeFileKind.Log)
                        changed.Add(file.Path);
                    continue;
                }
                if (Same(paths, file, saved))
                    continue;
                if (IsBinary(file))
                    binariesMatch = false;
                if (file.Kind != ReShadeFileKind.Log)
                    changed.Add(file.Path);
            }
            return changed;
        }

        private static bool IsBinary(ReShadeFile file) => file.Kind == ReShadeFileKind.Dll || file.Kind == ReShadeFileKind.Addon
            || file.Kind == ReShadeFileKind.Upscaler && file.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        private static bool Same(GamePaths paths, ReShadeFile inGame, ReShadeFile saved)
        {
            if (inGame.Size != saved.Size)
                return false;
            if (inGame.Written == saved.Written)
                return true;
            inGame.Sha256 ??= Hash(System.IO.Path.Combine(paths.Win64, Native(inGame.Path)));
            return inGame.Sha256 == saved.Sha256;
        }

        // Keeps in the slot what changed in the game while it was used (presets touched in the game, new shaders)
        private static void SyncBack(GamePaths paths, ReShadeStatus status)
        {
            var slot = status.Slot;
            var toCopy = status.Found.Where(f => f.Kind == ReShadeFileKind.Log || status.Changed.Contains(f.Path, StringComparer.OrdinalIgnoreCase)).ToList();
            if (toCopy.Count == 0)
                return;
            foreach (var file in toCopy)
            {
                var source = System.IO.Path.Combine(paths.Win64, Native(file.Path));
                var target = System.IO.Path.Combine(slot.FilesFolder, Native(file.Path));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                File.Copy(source, target, true);
                File.SetLastWriteTimeUtc(target, new DateTime(file.Written, DateTimeKind.Utc));
                file.Sha256 = Hash(target);
                slot.Files.RemoveAll(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
                slot.Files.Add(file);
            }
            slot.Files = slot.Files.OrderBy(f => f.Kind).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            slot.Info = Describe(slot.FilesFolder, slot.Files);
            if (status.Changed.Count > 0)
            {
                slot.Updated = DateTime.Now;
                Global.logger?.WriteLine(Loc.T("ReShade.SyncedBack", slot.Name, status.Changed.Count), LoggerType.Info);
            }
            Save(slot);
        }

        // Leaves the game without ReShade. What it had is kept first: in its slot, or in a new one when it wasn't saved
        public static ReShadeSlot Deactivate(GamePaths paths)
        {
            var status = GetStatus(paths);
            ReShadeSlot kept = null;
            var state = ReadState();
            if (status.State == ReShadeState.Slot)
            {
                SyncBack(paths, status);
                DeleteFromGame(paths, status.Found.Select(f => f.Path).Concat(status.Slot.Files.Select(f => f.Path)));
                kept = status.Slot;
                Global.logger?.WriteLine(Loc.T("ReShade.RemovedFromGame", kept.Name), LoggerType.Info);
            }
            else if (status.State == ReShadeState.Unknown)
            {
                kept = Capture(paths, Loc.T("ReShade.AutoName", DateTime.Now.ToString("g")), Loc.T("ReShade.AutoNotes"), true, out _);
                state = ReadState();
            }
            RestoreOriginals(paths, state);
            state.ActiveSlot = null;
            WriteState(state);
            return kept;
        }

        // Puts a slot in the game. The slot is checked first; whatever the game had is kept like in Deactivate.
        // Returns the files of other mods it had to set aside (put back when no slot is used)
        public static List<string> Activate(GamePaths paths, ReShadeSlot slot)
        {
            var problems = Verify(slot);
            if (problems.Count > 0)
                throw new InvalidOperationException(Loc.T("ReShade.SlotDamaged", slot.Name, string.Join("\n", problems.Take(5))));
            Deactivate(paths);
            var state = ReadState();
            var written = new List<string>();
            var replaced = new List<string>();
            try
            {
                foreach (var file in slot.Files)
                {
                    var target = System.IO.Path.Combine(paths.Win64, Native(file.Path));
                    if (File.Exists(target))
                    {
                        KeepOriginal(paths, state, file.Path);
                        replaced.Add(file.Path);
                    }
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    File.Copy(System.IO.Path.Combine(slot.FilesFolder, Native(file.Path)), target, true);
                    File.SetLastWriteTimeUtc(target, new DateTime(file.Written, DateTimeKind.Utc));
                    written.Add(file.Path);
                }
                foreach (var file in slot.Files.Where(IsBinary))
                    if (Hash(System.IO.Path.Combine(paths.Win64, Native(file.Path))) != file.Sha256)
                        throw new IOException(Loc.T("ReShade.CopyMismatch", file.Path));
            }
            catch
            {
                DeleteFromGame(paths, written);
                RestoreOriginals(paths, state);
                WriteState(state);
                throw;
            }
            state.ActiveSlot = slot.Id;
            WriteState(state);
            slot.LastUsed = DateTime.Now;
            Save(slot);
            Global.logger?.WriteLine(Loc.T("ReShade.Activated", slot.Name), LoggerType.Info);
            return replaced;
        }

        // Deletes a slot that is not in the game. recycle: to the Recycle Bin (only the window asks for it)
        public static void Delete(GamePaths paths, ReShadeSlot slot, Action<string> recycle = null)
        {
            if (paths != null)
            {
                var status = GetStatus(paths);
                if (status.State == ReShadeState.Slot && status.Slot.Id == slot.Id)
                    throw new InvalidOperationException(Loc.T("ReShade.DeleteActive"));
            }
            if (recycle != null)
                recycle(slot.Folder);
            else
                Directory.Delete(slot.Folder, true);
            var state = ReadState();
            if (state.ActiveSlot == slot.Id)
            {
                state.ActiveSlot = null;
                WriteState(state);
            }
            Global.logger?.WriteLine(Loc.T("ReShade.Deleted", slot.Name), LoggerType.Info);
        }

        // ---------- Files of the game ----------

        private static void DeleteFromGame(GamePaths paths, IEnumerable<string> files)
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Protected.Contains(System.IO.Path.GetFileName(relative)))
                    continue;
                var path = System.IO.Path.Combine(paths.Win64, Native(relative));
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                for (var dir = System.IO.Path.GetDirectoryName(relative.Replace('\\', System.IO.Path.DirectorySeparatorChar)); !string.IsNullOrEmpty(dir); dir = System.IO.Path.GetDirectoryName(dir))
                    folders.Add(dir);
            }
            // Folders left empty, deepest first
            foreach (var dir in folders.OrderByDescending(d => d.Length))
            {
                var path = System.IO.Path.Combine(paths.Win64, dir);
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                    Directory.Delete(path);
            }
        }

        private static void KeepOriginal(GamePaths paths, SavedState state, string relative)
        {
            var target = System.IO.Path.Combine(OriginalsPath, Native(relative));
            var source = System.IO.Path.Combine(paths.Win64, Native(relative));
            if (state.Originals.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                // One original already kept: this other file goes to Backups so nothing is lost
                var backup = System.IO.Path.Combine(Global.BackupsPath, "ReShade " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"), Native(relative));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup));
                File.Move(source, backup, true);
                return;
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            File.Move(source, target, true);
            state.Originals.Add(relative);
            WriteState(state);
        }

        private static void RestoreOriginals(GamePaths paths, SavedState state)
        {
            foreach (var relative in state.Originals.ToList())
            {
                var source = System.IO.Path.Combine(OriginalsPath, Native(relative));
                var target = System.IO.Path.Combine(paths.Win64, Native(relative));
                if (File.Exists(source) && !File.Exists(target))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    File.Move(source, target);
                }
                if (!File.Exists(source))
                    state.Originals.Remove(relative);
            }
            if (Directory.Exists(OriginalsPath) && !Directory.EnumerateFiles(OriginalsPath, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(OriginalsPath, true);
        }

        // Game files kept aside while a slot replaces them
        public static List<string> KeptOriginals() => ReadState().Originals.ToList();

        private static SavedState ReadState()
        {
            try
            {
                if (File.Exists(StatePath))
                    return JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath), Json) ?? new();
            }
            catch { }
            return new();
        }

        private static void WriteState(SavedState state)
        {
            Directory.CreateDirectory(RootPath);
            state.Originals ??= new();
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Json));
        }
    }
}
