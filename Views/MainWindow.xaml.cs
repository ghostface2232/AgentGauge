using Microsoft.UI.Xaml;

namespace Gauge.Views;

/// <summary>
/// Hidden host window. Created at startup but never activated; future steps
/// (tray icon, popover) will build on top of this.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
