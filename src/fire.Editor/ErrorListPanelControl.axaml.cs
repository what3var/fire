using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace fire.Editor
{
    /// <summary>The error list of the active script (severity, description, file, line); the filter button shows or hides the errors.</summary>
    public partial class ErrorListPanelControl : UserControl
    {
        private IReadOnlyList<ErrorListItem> _items = Array.Empty<ErrorListItem>();

        /// <summary>Fires when a row is double clicked (the host jumps to the line).</summary>
        public event Action<ErrorListItem>? ItemActivated;

        /// <summary>Fires when the number of errors changes.</summary>
        public event Action<int>? CountChanged;

        public ErrorListPanelControl() => InitializeComponent();

        public void SetItems(IReadOnlyList<ErrorListItem> items)
        {
            _items = items;
            Apply();
            CountChanged?.Invoke(items.Count);
        }

        /// <summary>Shows the list according to the filter button and keeps the sorting the user chose across the constant recalculations.</summary>
        private void Apply()
        {
            int count = _items.Count;
            ErrorCountText.Text = count == 1 ? "1 error" : $"{count} errors";

            var sorts = ErrorGrid.CollectionView?.SortDescriptions.ToList() ?? new List<DataGridSortDescription>();

            ErrorGrid.ItemsSource = ErrorFilterButton.IsChecked == true ? _items.ToList() : new List<ErrorListItem>();

            var view = ErrorGrid.CollectionView;
            if (view == null) return;
            view.SortDescriptions.Clear();
            foreach (var sort in sorts) view.SortDescriptions.Add(sort);
        }

        private void ErrorFilter_Click(object? sender, RoutedEventArgs e) => Apply();

        private void ErrorGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            // only a double click on a ROW jumps (not one on a column header)
            if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>() == null) return;
            if (ErrorGrid.SelectedItem is ErrorListItem item) ItemActivated?.Invoke(item);
        }
    }
}
