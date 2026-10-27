using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SparkingUnverum.UI
{
    // Field of view of the game with the SparkingFOV mod (see SparkingFov)
    public partial class FovWindow : Window
    {
        private readonly GamePaths paths;
        private bool snapping;

        public FovWindow(GamePaths paths)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            this.paths = paths;
            CreditRun.Text = Loc.T("Fov.CreditLink", SparkingFov.ModName, SparkingFov.Author);
            PercentSlider.Minimum = SparkingFov.MinPercent;
            PercentSlider.Maximum = SparkingFov.MaxPercent;
            MaximumSlider.Minimum = SparkingFov.MinMaximum;
            MaximumSlider.Maximum = SparkingFov.MaxMaximum;
            var (percent, maximum) = SparkingFov.Read(paths);
            PercentSlider.Value = percent;
            MaximumSlider.Value = maximum;
            ShowState();
        }

        public int Percent => (int)Math.Round(PercentSlider.Value);
        public int Maximum => (int)Math.Round(MaximumSlider.Value);

        private void ShowState()
        {
            var state = SparkingFov.Check(paths);
            StatusText.Text = state switch
            {
                SparkingFov.State.Installed => Loc.T("Fov.StateInstalled"),
                SparkingFov.State.OtherDll => Loc.T("Fov.StateOtherDll", SparkingFov.DllName),
                _ => Loc.T("Fov.StateMissing")
            };
            var usable = state != SparkingFov.State.OtherDll;
            PercentSlider.IsEnabled = MaximumSlider.IsEnabled = SaveButton.IsEnabled = ResetButton.IsEnabled = usable;
            RemovePanel.Visibility = state == SparkingFov.State.Installed ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var slider = (Slider)sender;
            // Dragging is pulled to the multiples of 10, the arrow keys still move one by one
            if (!snapping)
            {
                snapping = true;
                slider.Value = Mouse.LeftButton == MouseButtonState.Pressed ? SparkingFov.Snap(e.NewValue) : Math.Round(e.NewValue);
                snapping = false;
            }
            if (PercentText == null || MaximumText == null)
                return;
            PercentText.Text = $"{Percent} %";
            MaximumText.Text = $"{Maximum}°";
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            PercentSlider.Value = SparkingFov.DefaultPercent;
            MaximumSlider.Value = SparkingFov.DefaultMaximum;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SparkingFov.Install(paths) == SparkingFov.State.OtherDll)
                {
                    ShowState();
                    return;
                }
                SparkingFov.Write(paths, Percent, Maximum);
                Global.logger.WriteLine(Loc.T("Fov.Saved", Percent, Maximum), LoggerType.Info);
                StatusText.Text = Loc.T(GameProcess.IsRunning() ? "Fov.SavedRestart" : "Fov.SavedNext");
                RemovePanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Global.logger.WriteLine(Loc.T("Fov.Error", ex.Message), LoggerType.Error);
                StatusText.Text = Loc.T("Fov.Error", ex.Message);
            }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("Fov.RemoveConfirm"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            try
            {
                SparkingFov.Remove(paths);
            }
            catch (Exception ex)
            {
                Global.logger.WriteLine(Loc.T("Fov.Error", ex.Message), LoggerType.Error);
            }
            Reset_Click(sender, e);
            ShowState();
        }

        private void ModPage_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(SparkingFov.ModUrl) { UseShellExecute = true });

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
