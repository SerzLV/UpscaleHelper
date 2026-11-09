using System.Windows;

namespace UpscaleHelper.Presentation;

public partial class JobErrorWindow : Window
{
    internal JobErrorWindow(string name, string error)
    {
        InitializeComponent();
        FileLabel.Text = name;
        ErrorText.Text = error;
    }
}
