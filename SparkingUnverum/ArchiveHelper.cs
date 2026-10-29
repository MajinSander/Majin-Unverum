using System;
using System.IO;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SparkingUnverum
{
    public static class ArchiveHelper
    {
        // Files GameBanana sometimes serves without an archive around them
        private static readonly string[] LooseExtensions = { ".pak", ".utoc", ".ucas", ".sig", ".json", ".lua", ".dll", ".txt" };

        // Extracts zip, rar and 7z archives into destination, returns false if nothing was extracted.
        // progress: bytes written of all the files, and the file being extracted
        public static bool Extract(string archivePath, string destination, IProgress<DownloadProgress> progress = null)
        {
            Directory.CreateDirectory(destination);
            if (LooseExtensions.Contains(Path.GetExtension(archivePath).ToLowerInvariant()))
            {
                File.Copy(archivePath, Path.Combine(destination, Path.GetFileName(archivePath)), true);
                return true;
            }
            var options = new ExtractionOptions()
            {
                ExtractFullPath = true,
                Overwrite = true
            };
            var count = 0;
            using (var archive = ArchiveFactory.OpenArchive(archivePath))
            {
                var total = archive.Entries.Where(x => !x.IsDirectory).Sum(x => Math.Max(0, x.Size));
                long done = 0;
                void Report(string name) =>
                    progress?.Report(new DownloadProgress(total > 0 ? (float)done / total : 0, done, total, name));
                // Solid archives (most 7z) keep their files in one compressed block: opening a file on its own decompresses
                // the block again from the start. Anime Accurate Aura (1.8 GB) took a minute per small file after the big
                // one and seemed frozen. They are read once from start to end
                if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
                {
                    using var reader = archive.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                    {
                        if (reader.Entry.IsDirectory)
                            continue;
                        Report(Path.GetFileName(reader.Entry.Key));
                        reader.WriteEntryToDirectory(destination, options);
                        done += Math.Max(0, reader.Entry.Size);
                        count++;
                    }
                }
                else
                {
                    foreach (var entry in archive.Entries.Where(x => !x.IsDirectory))
                    {
                        Report(Path.GetFileName(entry.Key));
                        entry.WriteToDirectory(destination, options);
                        done += Math.Max(0, entry.Size);
                        count++;
                    }
                }
            }
            return count > 0;
        }

        // Mod content, used to tell it apart from settings the game wrote when the install date is unknown
        private static readonly string[] ContentExtensions = { ".pak", ".utoc", ".ucas", ".sig", ".uasset", ".uexp", ".ubulk", ".dll", ".asi", ".exe",
            ".uplugin", ".usm", ".awb", ".bk2", ".bmp", ".mp4", ".lua", ".png", ".jpg", ".jpeg" };

        // Moves the files of a mod aside before replacing it with a new version, returns where they are
        public static string SetAside(string modFolder)
        {
            var kept = Path.Combine(Global.DownloadsPath, "kept_" + Guid.NewGuid().ToString("N"));
            if (!Directory.Exists(modFolder))
                return kept;
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            foreach (var file in Directory.GetFiles(modFolder, "*", options))
            {
                if (Path.GetFileName(file).Equals("mod.json", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(file) == modFolder)
                    continue;
                var target = Path.Combine(kept, Path.GetRelativePath(modFolder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Move(file, target);
            }
            foreach (var dir in Directory.GetDirectories(modFolder))
                Directory.Delete(dir, true);
            return kept;
        }

        // After installing the new version, puts back the files the game or the user created or edited after the
        // previous install (settings, caches...). If the new version has its own copy, the old one goes to Backups.
        // Old mod files are discarded. Returns how many files were kept
        public static int RestoreUserFiles(string modFolder, string kept, DateTime? installed)
        {
            if (!Directory.Exists(kept))
                return 0;
            var restored = 0;
            var backupRoot = Path.Combine(Global.BackupsPath, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"), Path.GetFileName(modFolder));
            foreach (var file in Directory.GetFiles(kept, "*", SearchOption.AllDirectories))
            {
                var userFile = installed != null
                    ? File.GetLastWriteTimeUtc(file) > DateTime.SpecifyKind(installed.Value, DateTimeKind.Utc).AddMinutes(1)
                    : !ContentExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());
                if (!userFile)
                    continue;
                var relative = Path.GetRelativePath(kept, file);
                var target = Path.Combine(modFolder, relative);
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(file, target);
                    restored++;
                }
                else
                {
                    var backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Move(file, backup, true);
                    Global.logger.WriteLine(Loc.T("Update.UserFileBackedUp", relative, backup), LoggerType.Info);
                }
            }
            Directory.Delete(kept, true);
            if (restored > 0)
                Global.logger.WriteLine(Loc.T("Update.UserFilesKept", restored, Path.GetFileName(modFolder)), LoggerType.Info);
            return restored;
        }

        // Same bytes in both files
        public static bool SameContent(string a, string b)
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            if (infoA.Length != infoB.Length)
                return false;
            using var streamA = infoA.OpenRead();
            using var streamB = infoB.OpenRead();
            var bufferA = new byte[81920];
            var bufferB = new byte[81920];
            int read;
            while ((read = streamA.Read(bufferA, 0, bufferA.Length)) > 0)
            {
                var readB = 0;
                while (readB < read)
                {
                    var n = streamB.Read(bufferB, readB, read - readB);
                    if (n == 0)
                        return false;
                    readB += n;
                }
                if (!bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read)))
                    return false;
            }
            return true;
        }

        public static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        public static string SafeFolderName(string name)
        {
            var clean = string.Concat((name ?? string.Empty).Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(clean) ? "Mod" : clean;
        }
    }
}
