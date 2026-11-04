using System.Windows;

namespace Testpilot.Operator;

/// <summary>Asks for short name and password; the check itself is done by <see cref="MainViewModel.LoginDebugAsync"/>.</summary>
public partial class DebugLoginWindow : Window
{
    private readonly MainViewModel viewModel;

    public DebugLoginWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Loaded += (_, _) => ShortNameBox.Focus();
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        ErrorBlock.Text = "";
        var error = await viewModel.LoginDebugAsync(ShortNameBox.Text, PasswordBox.Password);
        PasswordBox.Clear();
        LoginButton.IsEnabled = true;
        if (error is null)
        {
            DialogResult = true;
            return;
        }
        ErrorBlock.Text = error;
        PasswordBox.Focus();
    }
}
