using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Noct.App.ViewModels;

namespace Noct.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    private LibraryViewModel? Model => DataContext as LibraryViewModel;

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: TrackRowViewModel row })
        {
            Model?.Play(row);
            e.Handled = true;
        }
    }

    /// <summary>Card width plus margin; the grid shows as many whole cards as the width allows.</summary>
    private const double CardSlot = 176;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        Model?.SetAlbumColumns(Math.Max(1, (int)((e.NewSize.Width - 32) / CardSlot)));

    private void OnAlbumRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: TrackRowViewModel row })
        {
            Model?.PlayFromAlbum(row);
            e.Handled = true;
        }
    }

    private void OnAlbumListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AlbumTrackList.SelectedItem is TrackRowViewModel row)
        {
            Model?.PlayFromAlbum(row);
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TrackList.SelectedItem is TrackRowViewModel row)
        {
            Model?.Play(row);
            e.Handled = true;
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Model?.Play(row);
        }
    }

    private void OnPlayNextClick(object sender, RoutedEventArgs e) => Model?.PlayNext(SelectionOrRow(sender));

    private void OnAddToQueueClick(object sender, RoutedEventArgs e) => Model?.AddToQueue(SelectionOrRow(sender));

    private static TrackRowViewModel? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as TrackRowViewModel;

    /// <summary>
    /// A context menu acts on the whole selection when the clicked row is part of it, and on just
    /// that row otherwise, which is what people expect from a multi-select list.
    /// </summary>
    private IReadOnlyList<TrackRowViewModel> SelectionOrRow(object sender)
    {
        var row = RowOf(sender);
        var selected = TrackList.SelectedItems.OfType<TrackRowViewModel>().ToList();
        if (row is not null && !selected.Contains(row))
        {
            return [row];
        }

        return selected.Count > 0 ? selected : (row is null ? [] : [row]);
    }
}
