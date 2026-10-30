using System;
using System.Windows;

namespace SparkingUnverum.UI
{
    // Name and notes of a ReShade slot, when saving one or changing them
    public partial class ReShadeSlotWindow : Window
    {
        private readonly Func<string, string> check;
        public string SlotName { get; private set; }
        public string Notes { get; private set; }

        public ReShadeSlotWindow(string title, string intro, string name, string notes, Func<string, string> check)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            Title = title;
            IntroText.Text = intro;
            IntroText.Visibility = string.IsNullOrEmpty(intro) ? Visibility.Collapsed : Visibility.Visible;
            this.check = check;
            NameBox.Text = name ?? string.Empty;
            NotesBox.Text = notes ?? string.Empty;
            NameBox.SelectAll();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            var error = check?.Invoke(NameBox.Text);
            if (error != null)
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            SlotName = NameBox.Text.Trim();
            Notes = NotesBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
