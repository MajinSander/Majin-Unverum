using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SparkingUnverum.UI;

namespace SparkingUnverum
{
    public enum ModState
    {
        None,
        UpdateAvailable,
        Unavailable
    }

    // An update found for an installed mod, shown in the updates window
    public class PendingUpdate : INotifyPropertyChanged
    {
        public string Folder { get; init; }
        public Metadata Metadata { get; init; }
        public GameBananaSubmission Submission { get; init; }
        // A file of the page newer than the downloaded ones, null when only an update post says there is news
        public GameBananaItemFile NewerFile { get; init; }
        // The update post, unless the news is a file uploaded after it
        public GameBananaItemUpdate Update => Submission.Updates is { Count: > 0 } updates
            && (NewerFile == null || updates[0].DateAdded >= NewerFile.DateAdded.AddHours(-1)) ? updates[0] : null;
        public string Name => Path.GetFileName(Folder);
        public Uri Image => Submission.Image ?? Metadata.preview;
        public string Title => Update != null ? $"{Update.Title} {Update.Version}".Trim()
            : string.IsNullOrWhiteSpace(NewerFile.Version) ? Loc.T("Update.NewFile", NewerFile.FileName) : Loc.T("Update.NewFileVersion", NewerFile.FileName, NewerFile.Version);
        public string When => StringConverters.FormatTimeAgo(DateTime.UtcNow - (Update?.DateAdded ?? NewerFile.DateAdded));
        public string Changes
        {
            get
            {
                if (Update == null)
                    return NewerFile.Description?.Trim() ?? string.Empty;
                var lines = (Update.Changes ?? new()).Where(x => !string.IsNullOrWhiteSpace(x.Text)).Select(x => $"• [{x.Category}] {x.Text}").ToList();
                var text = StringConverters.ConvertHtmlToText(Update.Text ?? string.Empty);
                if (text.Length > 0)
                    lines.Add(text);
                return string.Join("\n", lines);
            }
        }
        public bool HasChanges => Changes.Length > 0;
        // Updates hosted outside GameBanana can only be downloaded by hand
        public bool ManualOnly => !Submission.HasFiles;
        private bool isSelected = true;
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class UpdateCheckResult
    {
        public List<PendingUpdate> Pending = new();
        public List<string> Unavailable = new();
        public bool Failed;
    }

    public static class ModUpdater
    {
        // Mod folder name -> state shown with an icon in the mod list
        private static Dictionary<string, ModState> states = new(StringComparer.OrdinalIgnoreCase);
        public static ModState GetState(string name) => name != null && states.TryGetValue(name, out var state) ? state : ModState.None;

        // Looks for updates of every mod downloaded from GameBanana, without asking anything
        public static async Task<UpdateCheckResult> CheckForUpdates()
        {
            var result = new UpdateCheckResult();
            if (!Directory.Exists(Global.ModsPath))
                return result;
            // Mod folder -> (model, id) taken from the homepage in mod.json
            var installed = new List<(string Folder, Metadata Metadata, string Model, long Id)>();
            foreach (var folder in Directory.GetDirectories(Global.ModsPath))
            {
                var metadata = ModDownloader.ReadMetadata(folder);
                if (metadata?.homepage != null && GameBananaApi.TryParseProfileUrl(metadata.homepage.ToString(), out var model, out var id))
                    installed.Add((folder, metadata, model, id));
            }
            var submissions = new Dictionary<(string, long), GameBananaSubmission>();
            var errors = new List<Exception>();
            foreach (var group in installed.GroupBy(x => x.Model))
                foreach (var submission in await GameBananaApi.GetSubmissions(group.Key, group.Select(x => x.Id), errors))
                    submissions[(group.Key, submission.Id)] = submission;
            // Some requests failed, check the mods that did arrive
            foreach (var error in errors.Select(x => x.Message).Distinct())
                Global.logger.WriteLine(Loc.T("Update.CheckError", error), LoggerType.Error);
            if (submissions.Count == 0 && errors.Count > 0)
            {
                result.Failed = true;
                return result;
            }

            var newStates = new Dictionary<string, ModState>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in installed)
            {
                if (!submissions.TryGetValue((mod.Model, mod.Id), out var submission))
                    continue;
                var name = Path.GetFileName(mod.Folder);
                // Deleted by its author or hidden by the moderators, nothing to update
                if (submission.IsUnavailable)
                {
                    result.Unavailable.Add(name);
                    newStates[name] = ModState.Unavailable;
                    continue;
                }
                // Mods without a date yet, remember the current state and check next time
                if (mod.Metadata.lastupdate == null)
                {
                    mod.Metadata.lastupdate = submission.LatestUpdateDate ?? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    ModDownloader.SaveMetadata(mod.Folder, mod.Metadata);
                    continue;
                }
                if (HasUpdate(mod.Metadata, submission, out var newerFile))
                {
                    result.Pending.Add(new PendingUpdate { Folder = mod.Folder, Metadata = mod.Metadata, Submission = submission, NewerFile = newerFile });
                    newStates[name] = ModState.UpdateAvailable;
                }
            }
            states = newStates;
            if (result.Unavailable.Count > 0)
                Global.logger.WriteLine(Loc.T("Update.Unavailable", result.Unavailable.Count, string.Join(", ", result.Unavailable)), LoggerType.Info);
            Global.logger.WriteLine(result.Pending.Count == 0 ? Loc.T("Update.None") : Loc.T("Update.Found", result.Pending.Count), LoggerType.Info);
            return result;
        }

        // The classic Unverum saved UTC dates without a time zone
        private static DateTime AsUtc(DateTime date) =>
            date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date, DateTimeKind.Utc);

        // An update post newer than the mod, or a file of the page newer than the downloaded ones
        public static bool HasUpdate(Metadata metadata, GameBananaSubmission submission, out GameBananaItemFile newerFile)
        {
            newerFile = NewerFile(metadata, submission);
            var latest = submission.LatestUpdateDate;
            return newerFile != null || (latest != null && metadata.lastupdate != null && AsUtc(metadata.lastupdate.Value) < latest.Value);
        }

        // A file of the page newer than the downloaded ones: one uploaded after the download (authors don't always
        // write an update post) or a higher version than the one chosen (an old file picked when a new one was there).
        // Mods of the classic Unverum only remember the description of their file: just the version is compared
        public static GameBananaItemFile NewerFile(Metadata metadata, GameBananaSubmission submission)
        {
            var files = submission.AllFiles;
            if (files.Count == 0)
                return null;
            List<GameBananaItemFile> mine;
            if (metadata.files is { Count: > 0 })
                mine = files.Where(f => metadata.files.Contains(f.FileName, StringComparer.OrdinalIgnoreCase)).ToList();
            else if (ModMerger.MatchFile(metadata.filedescription, files) is GameBananaItemFile described)
                mine = new() { described };
            else
                return null;
            var seen = metadata.filesseen is DateTime skipped ? AsUtc(skipped) : DateTime.MinValue;
            var others = files.Where(f => !mine.Contains(f) && f.DateAdded > seen).ToList();
            var newestMine = mine.Count > 0 ? mine.Max(f => f.DateAdded) : DateTime.MinValue;

            if (metadata.installed is DateTime installed && metadata.files is { Count: > 0 })
            {
                var since = AsUtc(installed) > newestMine ? AsUtc(installed) : newestMine;
                var uploaded = others.Where(f => f.DateAdded > since.AddMinutes(2)).OrderByDescending(f => f.DateAddedLong).FirstOrDefault();
                if (uploaded != null)
                    return uploaded;
            }

            var myVersion = mine.Select(f => f.Version).Where(v => ParseVersion(v) != null).OrderByDescending(v => v, VersionComparer).FirstOrDefault();
            if (myVersion == null)
                return null;
            // Several downloads of a mod (Base, SSJ...) have their own versions: compare with the same file when the page has it.
            // A file with another name only counts when it is the version the page announces (its author renamed the download)
            var sameFile = new HashSet<string>(mine.Select(f => BaseName(f.FileName)));
            var announced = new[] { submission.Version, submission.Updates?.FirstOrDefault()?.Version }.Where(v => ParseVersion(v) != null).ToList();
            var candidates = others.Any(f => sameFile.Contains(BaseName(f.FileName)))
                ? others.Where(f => sameFile.Contains(BaseName(f.FileName)))
                : others.Where(f => announced.Any(v => VersionComparer.Compare(f.Version, v) == 0));
            return candidates.Where(f => f.DateAdded >= newestMine && VersionComparer.Compare(f.Version, myVersion) > 0)
                .OrderByDescending(f => f.DateAddedLong).FirstOrDefault();
        }

        // "v1.4", "1.4.2", "V1.3 - fixes" -> numbers to compare, null without any
        private static int[] ParseVersion(string text)
        {
            var match = text == null ? null : Regex.Match(text, @"\d+(\.\d+)*");
            return match is { Success: true } ? match.Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray() : null;
        }

        private static readonly Comparer<string> VersionComparer = Comparer<string>.Create((a, b) =>
        {
            var x = ParseVersion(a);
            var y = ParseVersion(b);
            if (x == null || y == null)
                return x == null ? (y == null ? 0 : -1) : 1;
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var difference = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
                if (difference != 0)
                    return difference;
            }
            return 0;
        });

        // Marks the updates as seen without downloading them
        public static void Skip(IEnumerable<PendingUpdate> updates)
        {
            foreach (var pending in updates)
            {
                if (pending.Submission.LatestUpdateDate is DateTime post)
                    pending.Metadata.lastupdate = post;
                if (pending.Submission.HasFiles)
                    pending.Metadata.filesseen = pending.Submission.AllFiles.Max(f => f.DateAdded);
                ModDownloader.SaveMetadata(pending.Folder, pending.Metadata);
                states.Remove(pending.Name);
                Global.logger.WriteLine(Loc.T("Update.Skipped", pending.Name), LoggerType.Info);
            }
        }

        // Downloads and installs the updates one after another, returns how many were installed
        public static async Task<int> Apply(IEnumerable<PendingUpdate> updates)
        {
            var installed = 0;
            foreach (var pending in updates)
            {
                if (pending.ManualOnly)
                {
                    if (pending.Submission.HasAltLinks)
                        new AltLinkWindow(pending.Submission.AlternateFileSources, pending.Name, Global.ModsPath, pending.Submission.ProfileUrl, true).ShowDialog();
                    else
                        Global.logger.WriteLine(Loc.T("Update.NoFiles", pending.Name), LoggerType.Warning);
                    continue;
                }
                var files = ChooseFiles(pending);
                if (files == null)
                {
                    Global.logger.WriteLine(Loc.T("Update.Cancelled", pending.Name), LoggerType.Info);
                    continue;
                }
                var result = await Install(pending, files);
                if (result == null)
                    break;
                if (result == true)
                    installed++;
            }
            return installed;
        }

        // Same file name without versions: "szmodlib126_release.zip" and "szmodlib127_release.zip" are the same file
        public static string BaseName(string fileName) =>
            Regex.Replace(Path.GetFileNameWithoutExtension(fileName ?? string.Empty).ToLowerInvariant(), @"(v(?=\d)|[\d\s._\-()\[\]])+", string.Empty);

        // Picks the files to download: the ones it can tell by itself, or asks with the new file chosen
        private static List<GameBananaItemFile> ChooseFiles(PendingUpdate pending)
        {
            if (AutoChoose(pending) is List<GameBananaItemFile> chosen)
                return chosen;
            var fileBox = new UpdateFileBox(pending.Submission, pending.Submission.AllFiles, pending.NewerFile?.Id ?? 0, false);
            return fileBox.ShowDialog() == true && fileBox.SelectedFiles.Count > 0 ? fileBox.SelectedFiles : null;
        }

        // The only file, or the ones that match what was downloaded last time; null when the user has to choose
        public static List<GameBananaItemFile> AutoChoose(PendingUpdate pending)
        {
            var files = pending.Submission.AllFiles;
            if (files.Count == 1)
                return files;
            var previous = pending.Metadata.files;
            // When the news is a file with another name (its author renamed it), the same old file again would change nothing
            bool HasNews(IEnumerable<GameBananaItemFile> chosen) => pending.NewerFile == null || chosen.Any(f => f.Id == pending.NewerFile.Id);
            if (previous != null && previous.Count > 0)
            {
                var matches = previous.Select(p => files.Where(f => BaseName(f.FileName) == BaseName(p)).ToList()).ToList();
                if (matches.All(m => m.Count == 1) && HasNews(matches.Select(m => m[0])))
                    return matches.Select(m => m[0]).Distinct().ToList();
            }
            // Mods installed by the classic Unverum only remember the description of the downloaded file
            else if (ModMerger.MatchFile(pending.Metadata.filedescription, files) is GameBananaItemFile byDescription && HasNews(new[] { byDescription }))
                return new List<GameBananaItemFile> { byDescription };
            return null;
        }

        // true = installed, false = failed, null = cancelled by the user (stop the batch)
        private static async Task<bool?> Install(PendingUpdate pending, List<GameBananaItemFile> files)
        {
            var downloader = new ModDownloader();
            var cancellation = new CancellationTokenSource();
            var archives = new List<string>();
            try
            {
                // Download everything first, the mod folder is only touched when all files arrived
                foreach (var file in files)
                {
                    var archive = await downloader.DownloadFile(file.DownloadUrl, file.FileName, cancellation);
                    if (archive == null)
                        return cancellation.IsCancellationRequested ? null : false;
                    archives.Add(archive);
                }
                await Task.Run(() =>
                {
                    // Settings the game saved in the mod are put back after extracting
                    var kept = ArchiveHelper.SetAside(pending.Folder);
                    try
                    {
                        foreach (var archive in archives)
                            ArchiveHelper.Extract(archive, pending.Folder);
                        ArchiveHelper.RestoreUserFiles(pending.Folder, kept, pending.Metadata.installed);
                    }
                    catch
                    {
                        // Extraction failed: everything goes back as it was
                        ArchiveHelper.RestoreUserFiles(pending.Folder, kept, DateTime.MinValue);
                        throw;
                    }
                });
                ModDownloader.WriteMetadata(pending.Folder, pending.Submission,
                    string.Join("\n", files.Select(x => x.Description).Where(x => !string.IsNullOrWhiteSpace(x))), files.Select(x => x.FileName).ToList());
                states.Remove(pending.Name);
                Global.logger.WriteLine(Loc.T("Update.Updated", pending.Name), LoggerType.Info);
                return true;
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Download.ExtractError", pending.Name, e.Message), LoggerType.Error);
                return false;
            }
            finally
            {
                foreach (var archive in archives)
                    try { File.Delete(archive); } catch { }
            }
        }
    }
}
