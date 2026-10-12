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
    /// <summary>The error list (severity, code, description, file, line): the errors and warnings of the last build until the next one replaces them (or, before there was a build, the live
    /// analysis of the active script). Two filter buttons show or hide the errors and the warnings.</summary>
    public partial class ErrorListPanelControl : UserControl
    {
        private IReadOnlyList<ErrorListItem> _items = Array.Empty<ErrorListItem>();

        /// <summary>Fires when a row is double clicked (the host jumps to the line).</summary>
        public event Action<ErrorListItem>? ItemActivated;

        /// <summary>Fires when the number of errors or warnings changes (errors, warnings).</summary>
        public event Action<int, int>? CountChanged;

        public ErrorListPanelControl() => InitializeComponent();

        public int ErrorCount => _items.Count(i => i.IsError);

        public int WarningCount => _items.Count(i => i.IsWarning);

        /// <summary>Where the rows come from: "Last build, 14:03:22", "Live: main.fire" (shown right in the header).</summary>
        public void SetSource(string text) => SourceText.Text = text;

        public void SetItems(IReadOnlyList<ErrorListItem> items)
        {
            _items = items;
            Apply();
            CountChanged?.Invoke(ErrorCount, WarningCount);
        }

        /// <summary>Shows the list according to the filter buttons and keeps the sorting the user chose across the constant recalculations.</summary>
        private void Apply()
        {
            int errors = ErrorCount, warnings = WarningCount;
            ErrorCountText.Text = errors == 1 ? "1 error" : $"{errors} errors";
            WarningCountText.Text = warnings == 1 ? "1 warning" : $"{warnings} warnings";

            var sorts = ErrorGrid.CollectionView?.SortDescriptions.ToList() ?? new List<DataGridSortDescription>();

            bool showErrors = ErrorFilterButton.IsChecked == true, showWarnings = WarningFilterButton.IsChecked == true;
            ErrorGrid.ItemsSource = _items.Where(i => i.IsError ? showErrors : showWarnings).ToList();

            var view = ErrorGrid.CollectionView;
            if (view == null) return;
            view.SortDescriptions.Clear();
            foreach (var sort in sorts) view.SortDescriptions.Add(sort);
        }

        private void Filter_Click(object? sender, RoutedEventArgs e) => Apply();

        private void ErrorGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            // only a double click on a ROW jumps (not one on a column header)
            if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>() == null) return;
            if (ErrorGrid.SelectedItem is ErrorListItem item) ItemActivated?.Invoke(item);
        }
    }
}
