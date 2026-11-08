using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace CluadeX.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>คลิกข้อความข้างช่องติ๊ก "น้อง CluadeX พูด" = คลิกช่องติ๊กเอง</summary>
    private void BuddySoundsText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        BuddySoundsBox.IsChecked = BuddySoundsBox.IsChecked != true;
        e.Handled = true;
    }

    private void BuddyBubblesText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        BuddyBubblesBox.IsChecked = BuddyBubblesBox.IsChecked != true;
        e.Handled = true;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch { }
        e.Handled = true;
    }
}
