using System.Windows;
using System.Windows.Input;

namespace VidSort.App.Views;

public partial class TextInputDialog : Window
{
    private Func<string, string?>? _validator;

    public TextInputDialog()
    {
        InitializeComponent();
    }

    public string InitialValue
    {
        get => NameTextBox.Text;
        set
        {
            NameTextBox.Text = value ?? string.Empty;
            NameTextBox.CaretIndex = NameTextBox.Text.Length;
            ValidateInput();
        }
    }

    public string? Value { get; private set; }

    public static string? Show(
        Window owner,
        string title,
        string initialValue,
        Func<string, string?> validator)
    {
        var dialog = new TextInputDialog
        {
            Owner = owner,
            _validator = validator
        };
        dialog.Title = title;
        dialog.InitialValue = initialValue;
        dialog.Loaded += (_, _) =>
        {
            dialog.NameTextBox.Focus();
            dialog.NameTextBox.SelectAll();
        };

        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void ValidateInput()
    {
        if (NameTextBox is null || ErrorText is null || OkButton is null)
        {
            return;
        }

        string? error = _validator?.Invoke(NameTextBox.Text);
        if (error is null)
        {
            ErrorText.Text = string.Empty;
            ErrorText.Visibility = Visibility.Collapsed;
            OkButton.IsEnabled = true;
        }
        else
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            OkButton.IsEnabled = false;
        }
    }

    private void NameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ValidateInput();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Confirm();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Cancel();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    private void Confirm()
    {
        if (!OkButton.IsEnabled)
        {
            return;
        }

        Value = NameTextBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel()
    {
        Value = null;
        DialogResult = false;
    }
}
