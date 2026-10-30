using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using FontAwesome5;

namespace SparkingUnverum.UI
{
    // A row of the list: one slot, or the first one that leaves the game without ReShade
    public class ReShadeSlotRow
    {
        public ReShadeSlot Slot { get; init; }
        public bool IsNone => Slot == null;
        public bool IsActive { get; init; }
        public string Name { get; init; }
        public string Notes { get; init; }
        public List<string> Chips { get; init; } = new();
        public string Details { get; init; }
        public string Health { get; init; }
        public Brush HealthColor { get; init; }
        public EFontAwesomeIcon Icon { get; init; }
        public Brush IconColor { get; init; }
        public string UseText => Loc.T(IsNone ? "ReShade.UseNone" : "ReShade.Use");
        public string UseTooltip => Loc.T(IsNone ? "ReShade.UseNoneTooltip" : "ReShade.UseTooltip");
    }

    // ReShade slots (see ReShadeSlots): what the game has now, take it out to a slot, use one slot, another or none
    public partial class ReShadeWindow : Window
    {
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xF2, 0x8C, 0x1E));
        private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x7C, 0xC4, 0x7C));
        private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));
        private static readonly Brush Quiet = new SolidColorBrush(Color.FromRgb(0xA2, 0xA2, 0xA2));

        private readonly GamePaths paths;
        private ReShadeStatus status;

        public ReShadeWindow(GamePaths paths)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            this.paths = paths;
            Show(ReShadeSlots.GetStatus(paths), ReShadeSlots.List());
        }

        public ReShadeStatus Status => status;

        private async Task Reload(string message = null)
        {
            var (newStatus, slots) = await Task.Run(() => (ReShadeSlots.GetStatus(paths), ReShadeSlots.List()));
            Show(newStatus, slots);
            if (message != null)
                StatusText.Text = message;
        }

        private void Show(ReShadeStatus newStatus, List<ReShadeSlot> slots)
        {
            status = newStatus;
            StatusText.Text = string.Empty;
            switch (status.State)
            {
                case ReShadeState.None:
                    GameTitle.Text = Loc.T("ReShade.GameNone");
                    GameDetail.Text = Loc.T("ReShade.GameNoneDetail");
                    GameIcon.Icon = EFontAwesomeIcon.Solid_Ban;
                    GameIcon.Foreground = Quiet;
                    break;
                case ReShadeState.Slot:
                    GameTitle.Text = status.Slot.Name;
                    GameDetail.Text = string.Join("  ·  ", Chips(status.Info))
                        + (status.Changed.Count > 0 ? "\n" + Loc.T("ReShade.GameChanged", status.Changed.Count) : "");
                    GameIcon.Icon = EFontAwesomeIcon.Solid_CheckCircle;
                    GameIcon.Foreground = Accent;
                    break;
                default:
                    GameTitle.Text = Loc.T(status.Info.HasReShade ? "ReShade.GameUnknown" : "ReShade.GameUnknownUpscaler");
                    GameDetail.Text = string.Join("  ·  ", Chips(status.Info)) + "\n" + Loc.T("ReShade.GameUnknownDetail", status.Found.Count);
                    GameIcon.Icon = EFontAwesomeIcon.Solid_ExclamationTriangle;
                    GameIcon.Foreground = Accent;
                    break;
            }
            CapturePanel.Visibility = status.State == ReShadeState.None ? Visibility.Collapsed : Visibility.Visible;
            // A saved slot is taken out by choosing "No ReShade", copying it still makes a variant
            TakeOutBorder.Visibility = status.State == ReShadeState.Unknown ? Visibility.Visible : Visibility.Collapsed;

            var rows = new List<ReShadeSlotRow>
            {
                new()
                {
                    Name = Loc.T("ReShade.None"),
                    Notes = Loc.T("ReShade.NoneNotes"),
                    IsActive = status.State == ReShadeState.None,
                    Icon = EFontAwesomeIcon.Solid_Ban,
                    IconColor = Quiet
                }
            };
            foreach (var slot in slots)
                rows.Add(new ReShadeSlotRow
                {
                    Slot = slot,
                    Name = slot.Name,
                    Notes = slot.Notes,
                    IsActive = status.State == ReShadeState.Slot && status.Slot.Id == slot.Id,
                    Chips = Chips(slot.Info),
                    Details = Loc.T("ReShade.Details", slot.Files.Count, StringConverters.FormatSize(slot.Size), slot.Created.ToString("g"),
                        slot.LastUsed?.ToString("g") ?? Loc.T("ReShade.Never"), slot.Fingerprint),
                    Health = slot.Healthy switch
                    {
                        true => Loc.T("ReShade.HealthOk", slot.LastCheck?.ToString("g")),
                        false => Loc.T("ReShade.HealthBad"),
                        _ => null
                    },
                    HealthColor = slot.Healthy == false ? Bad : Good,
                    Icon = EFontAwesomeIcon.Solid_Magic,
                    IconColor = slot.Healthy == false ? Bad : Accent
                });
            SlotList.ItemsSource = rows;
        }

        // Short labels that tell slots apart: version, how it loads, DLSS, preset...
        public static List<string> Chips(ReShadeInfo info)
        {
            var chips = new List<string>();
            if (info == null)
                return chips;
            if (info.HasReShade)
                chips.Add(Loc.T("ReShade.ChipVersion", info.Version ?? "?"));
            if (info.Dll != null)
                chips.Add(info.Dll);
            if (info.Dlss != null)
                chips.Add(Loc.T("ReShade.ChipDlss", info.Dlss));
            if (info.DlssFrameGen != null)
                chips.Add(Loc.T("ReShade.ChipFrameGen", info.DlssFrameGen));
            if (info.OptiScaler != null)
                chips.Add(Loc.T("ReShade.ChipOptiScaler", info.OptiScaler));
            if (info.Preset != null)
                chips.Add(Loc.T("ReShade.ChipPreset", Path.GetFileNameWithoutExtension(info.Preset), info.Techniques));
            if (info.Presets.Count > 1)
                chips.Add(Loc.T("ReShade.ChipPresets", info.Presets.Count));
            if (info.Effects > 0)
                chips.Add(Loc.T("ReShade.ChipEffects", info.Effects));
            if (info.Addons.Count > 0)
                chips.Add(Loc.T("ReShade.ChipAddons", string.Join(", ", info.Addons)));
            return chips;
        }

        // Changing the game's files with it open would fail halfway or leave it half done
        private bool GameClosed()
        {
            if (!GameProcess.IsRunning())
                return true;
            MessageBox.Show(Loc.T("ReShade.CloseGame"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        // Runs a change with the window locked, errors shown and logged
        private async Task Run(Func<string> work)
        {
            IsEnabled = false;
            StatusText.Text = Loc.T("ReShade.Working");
            string message = null;
            try
            {
                message = await Task.Run(work);
            }
            catch (Exception ex)
            {
                Global.logger.WriteLine(Loc.T("ReShade.Error", ex.Message), LoggerType.Error);
                MessageBox.Show(Loc.T("ReShade.Error", ex.Message), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsEnabled = true;
            }
            await Reload(message);
        }

        private async void Use_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ReShadeSlotRow row || !GameClosed())
                return;
            var unknown = status.State == ReShadeState.Unknown;
            await Run(() =>
            {
                var notes = new List<string>();
                if (row.IsNone)
                {
                    var kept = ReShadeSlots.Deactivate(paths);
                    notes.Add(Loc.T("ReShade.NowNone"));
                    if (unknown && kept != null)
                        notes.Add(Loc.T("ReShade.AutoSaved", kept.Name));
                }
                else
                {
                    var replaced = ReShadeSlots.Activate(paths, row.Slot);
                    notes.Add(Loc.T("ReShade.NowUsing", row.Slot.Name));
                    if (unknown)
                        notes.Add(Loc.T("ReShade.AutoSavedShort"));
                    if (replaced.Count > 0)
                        notes.Add(Loc.T("ReShade.Replaced", string.Join(", ", replaced)));
                }
                notes.Add(Loc.T(GameProcess.IsRunning() ? "Fov.SavedRestart" : "ReShade.NextLaunch"));
                return string.Join(" ", notes);
            });
        }

        private async Task Capture(bool remove)
        {
            if (remove && !GameClosed())
                return;
            var suggested = status.Info?.HasReShade == true ? Loc.T("ReShade.DefaultName", status.Info.Version ?? "") : Loc.T("ReShade.DefaultNameUpscaler");
            for (int n = 2; ReShadeSlots.CheckName(suggested) != null; n++)
                suggested = (status.Info?.HasReShade == true ? Loc.T("ReShade.DefaultName", status.Info.Version ?? "") : Loc.T("ReShade.DefaultNameUpscaler")) + " " + n;
            var dialog = new ReShadeSlotWindow(Loc.T(remove ? "ReShade.TakeOut" : "ReShade.Copy"), Loc.T(remove ? "ReShade.TakeOutIntro" : "ReShade.CopyIntro"),
                suggested, null, name => ReShadeSlots.CheckName(name)) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;
            await Run(() =>
            {
                var slot = ReShadeSlots.Capture(paths, dialog.SlotName, dialog.Notes, remove, out var duplicate);
                var text = duplicate ? Loc.T("ReShade.AlreadySaved", slot.Name) : Loc.T("ReShade.SavedIn", slot.Name);
                return remove ? text + " " + Loc.T("ReShade.NowNone") : text;
            });
        }

        private async void TakeOut_Click(object sender, RoutedEventArgs e) => await Capture(true);
        private async void Copy_Click(object sender, RoutedEventArgs e) => await Capture(false);

        private async void Verify_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ReShadeSlotRow { Slot: not null } row)
                return;
            List<string> problems = null;
            await Run(() =>
            {
                problems = ReShadeSlots.Verify(row.Slot);
                return problems.Count == 0 ? Loc.T("ReShade.VerifyOk", row.Slot.Name, row.Slot.Files.Count) : Loc.T("ReShade.VerifyBad", row.Slot.Name, problems.Count);
            });
            if (problems?.Count > 0)
                MessageBox.Show(Loc.T("ReShade.VerifyBadDetail", row.Slot.Name, string.Join("\n", problems.Take(15))), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void Edit_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ReShadeSlotRow { Slot: not null } row)
                return;
            var dialog = new ReShadeSlotWindow(Loc.T("ReShade.Edit"), null, row.Slot.Name, row.Slot.Notes, name => ReShadeSlots.CheckName(name, row.Slot.Id)) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;
            await Run(() =>
            {
                ReShadeSlots.Rename(row.Slot, dialog.SlotName, dialog.Notes);
                return null;
            });
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ReShadeSlotRow { Slot: not null } row && Directory.Exists(row.Slot.FilesFolder))
                Process.Start(new ProcessStartInfo(row.Slot.FilesFolder) { UseShellExecute = true });
        }

        private void OpenAll_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(ReShadeSlots.RootPath);
            Process.Start(new ProcessStartInfo(ReShadeSlots.RootPath) { UseShellExecute = true });
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ReShadeSlotRow { Slot: not null } row)
                return;
            if (row.IsActive)
            {
                MessageBox.Show(Loc.T("ReShade.DeleteActive"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(Loc.T("ReShade.DeleteConfirm", row.Slot.Name, StringConverters.FormatSize(row.Slot.Size)), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                // Windows asks itself if the folder is too big for the Recycle Bin
                ReShadeSlots.Delete(paths, row.Slot, folder => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder,
                    Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await Reload();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
