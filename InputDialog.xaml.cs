using System.Windows;
using System.Windows.Input;

namespace StarCitizenJapaneseTextCreater;

public partial class InputDialog : Window
{
    public string ResponseText => txtInput.Text.Trim();

    public InputDialog(string message)
    {
        InitializeComponent();
        txtMessage.Text = message;
        txtInput.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(txtInput.Text))
            DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TxtInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(txtInput.Text))
            DialogResult = true;
    }
}
