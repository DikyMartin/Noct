using System.Windows;
using System.Windows.Controls;
using Noct.App.ViewModels;

namespace Noct.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    /// <summary>The empty state uses the same add-folder action as the navigation button.</summary>
    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel main)
        {
            main.Library.AddFolderCommand.Execute(null);
        }
    }
}
