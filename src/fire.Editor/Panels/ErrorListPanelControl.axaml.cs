using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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

        // the sort the user chose by a click on a header (the TableView only shows, it does not sort): the index of the column and the direction
        private static readonly string[] ColumnTitles = { "", "Code", "Description", "File", "Line" };
        private int _sortColumn = -1;
        private bool _sortDescending;

        public ErrorListPanelControl()
        {
            InitializeComponent();
            ErrorGrid.AddHandler(PointerReleasedEvent, Header_Released, RoutingStrategies.Bubble, handledEventsToo: true);
        }

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

            bool showErrors = ErrorFilterButton.IsChecked == true, showWarnings = WarningFilterButton.IsChecked == true;
            IEnumerable<ErrorListItem> shown = _items.Where(i => i.IsError ? showErrors : showWarnings);
            if (_sortColumn >= 0)
            {
                Func<ErrorListItem, IComparable> key = _sortColumn switch
                {
                    0 => i => i.IsError ? 0 : 1,
                    1 => i => i.Code,
                    2 => i => i.Description,
                    3 => i => i.File,
                    _ => i => i.Line,
                };
                shown = _sortDescending ? shown.OrderByDescending(key) : shown.OrderBy(key);
            }
            ErrorGrid.ItemsSource = shown.ToList();
        }

        /// <summary>A click on a column header sorts by that column (a second click turns the direction); the arrow in the title shows the sort.</summary>
        private void Header_Released(object? sender, PointerReleasedEventArgs e)
        {
            var source = e.Source as Visual;
            if (source == null || source.FindAncestorOfType<Thumb>(true) != null) return;   // the end of a resizing drag
            var header = source.FindAncestorOfType<TableViewColumnHeader>(true);
            if (header?.Column is not { } column) return;
            int index = ErrorGrid.Columns.IndexOf(column);
            if (index < 0) return;
            _sortDescending = _sortColumn == index && !_sortDescending;
            _sortColumn = index;
            for (int i = 0; i < ErrorGrid.Columns.Count && i < ColumnTitles.Length; i++)
                ErrorGrid.Columns[i].Header = i == _sortColumn ? ColumnTitles[i] + (_sortDescending ? " \u25BC" : " \u25B2") : ColumnTitles[i];
            Apply();
        }

        private void Filter_Click(object? sender, RoutedEventArgs e) => Apply();

        private void ErrorGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            // only a double click on a ROW jumps (not one on a column header)
            if ((e.Source as Visual)?.FindAncestorOfType<TableViewRow>(true) == null) return;
            if (ErrorGrid.SelectedItem is ErrorListItem item) ItemActivated?.Invoke(item);
        }
    }
}
