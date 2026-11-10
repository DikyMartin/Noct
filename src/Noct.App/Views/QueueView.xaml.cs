using System.Windows.Controls;
using System.Windows.Input;
using Noct.App.ViewModels;

namespace Noct.App.Views;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: QueueItemViewModel item } &&
            DataContext is PlaybackViewModel playback)
        {
            playback.PlayQueueItemCommand.Execute(item);
            e.Handled = true;
        }
    }
}
