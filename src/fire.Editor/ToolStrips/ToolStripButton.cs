using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace fire.Editor
{
    /// <summary>
    /// A compact button for a <see cref="ToolStrip"/>: an ordinary Avalonia <see cref="Button"/> (it looks like one and has <c>Click</c>, <c>Command</c>, <c>IsEnabled</c>, ...) with an
    /// <see cref="Icon"/> and an optional <see cref="Label"/> to the right of it - like the <c>CommandBarButton</c>, but smaller. Without a label it is a square icon button.
    /// <code>
    /// &lt;local:ToolStripButton Label="Start" Click="Run_Click" ToolTip.Tip="..."&gt;
    ///     &lt;local:ToolStripButton.Icon&gt;&lt;PathIcon Data="{StaticResource IconPlay}"/&gt;&lt;/local:ToolStripButton.Icon&gt;
    /// &lt;/local:ToolStripButton&gt;
    /// &lt;local:ToolStripButton IconSource="/Assets/icons/Material-Stop.png" Click="Stop_Click"/&gt;
    /// &lt;local:ToolStripButton IconData="{StaticResource IconSave}" Click="Save_Click"/&gt;
    /// </code>
    /// With items in <see cref="Menu"/> the button is a split button: a small drop-down part appears at its right edge that opens the menu, and a click on the rest of the button is the
    /// <c>Click</c> as before. Without items there is no drop-down part.
    /// <code>
    /// &lt;local:ToolStripButton IconSource="/Assets/icons/Material-ScriptOutline.png" Click="NewScript_Click"&gt;
    ///     &lt;local:ToolStripButton.Menu&gt;&lt;MenuFlyout&gt;&lt;MenuItem Header="_Markdown" Click="NewMarkdown_Click"/&gt;&lt;/MenuFlyout&gt;&lt;/local:ToolStripButton.Menu&gt;
    /// &lt;/local:ToolStripButton&gt;
    /// </code>
    /// </summary>
    public class ToolStripButton : Button
    {
        /// <summary>The icon: any control (an <c>Image</c>, a <c>PathIcon</c>); it is shown 16 x 16.</summary>
        public static readonly StyledProperty<Control?> IconProperty = AvaloniaProperty.Register<ToolStripButton, Control?>(nameof(Icon));

        /// <summary>The picture of the icon, for the common case of an image file (<c>IconSource="/Assets/icons/x.png"</c>).</summary>
        public static readonly StyledProperty<IImage?> IconSourceProperty = AvaloniaProperty.Register<ToolStripButton, IImage?>(nameof(IconSource));

        /// <summary>The outline of the icon, for the common case of a vector icon (<c>IconData="{StaticResource IconSave}"</c>).</summary>
        public static readonly StyledProperty<Geometry?> IconDataProperty = AvaloniaProperty.Register<ToolStripButton, Geometry?>(nameof(IconData));

        /// <summary>The text to the right of the icon; empty: only the icon.</summary>
        public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<ToolStripButton, string?>(nameof(Label));

        /// <summary>The menu of the drop-down part (a split button when it has items).</summary>
        public static readonly StyledProperty<MenuFlyout?> MenuProperty = AvaloniaProperty.Register<ToolStripButton, MenuFlyout?>(nameof(Menu));

        private readonly Border _drop = new()
        {
            Classes = { "dropdown" },
            IsVisible = false,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.Parse("#40FFFFFF")),
            BorderThickness = new Thickness(1, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 3, 3, 0),
            Margin = new Thickness(4, -2, -4, -2),
            Padding = new Thickness(3, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = new Avalonia.Controls.PathIcon { Data = Geometry.Parse("M2,5 L10,5 L6,9 Z"), Width = 10, Height = 14 },
        };

        private readonly ContentControl _icon = new() { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), IsVisible = false, IsHitTestVisible = false };

        // a Button takes the theme of Button (the Fluent theme looks the type up by exactly that)
        protected override System.Type StyleKeyOverride => typeof(Button);

        static ToolStripButton()
        {
            IconProperty.Changed.AddClassHandler<ToolStripButton>((b, _) => b.UpdateIcon());
            IconSourceProperty.Changed.AddClassHandler<ToolStripButton>((b, _) => b.UpdateIcon());
            IconDataProperty.Changed.AddClassHandler<ToolStripButton>((b, _) => b.UpdateIcon());
            LabelProperty.Changed.AddClassHandler<ToolStripButton>((b, _) => b.UpdateLabel());
            MenuProperty.Changed.AddClassHandler<ToolStripButton>((b, e) => b.MenuChanged(e.OldValue as MenuFlyout, e.NewValue as MenuFlyout));
        }

        public ToolStripButton()
        {
            Classes.Add("toolstrip");
            Padding = new Thickness(4, 2);
            MinHeight = 24;
            MinWidth = 24;
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalContentAlignment = HorizontalAlignment.Center;
            VerticalContentAlignment = VerticalAlignment.Center;
            Focusable = false;   // a click on a tool button does not take the keyboard focus away from the editor
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _icon, _label, _drop } };
            _drop.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(_drop).Properties.IsLeftButtonPressed) return;
                e.Handled = true;   // not a click on the button itself
                Menu?.ShowAt(this);
            };
        }

        public Control? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
        public IImage? IconSource { get => GetValue(IconSourceProperty); set => SetValue(IconSourceProperty, value); }
        public Geometry? IconData { get => GetValue(IconDataProperty); set => SetValue(IconDataProperty, value); }
        public MenuFlyout? Menu { get => GetValue(MenuProperty); set => SetValue(MenuProperty, value); }
        public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

        private void UpdateIcon()
        {
            Control? icon = Icon;
            if (icon == null && IconSource != null) icon = new Image { Source = IconSource };
            if (icon == null && IconData != null) icon = new Avalonia.Controls.PathIcon { Data = IconData };
            if (icon != null) { icon.Width = 16; icon.Height = 16; }
            _icon.Content = icon;
            _icon.IsVisible = icon != null;
        }

        private void MenuChanged(MenuFlyout? old, MenuFlyout? now)
        {
            if (old != null) old.Items.CollectionChanged -= MenuItemsChanged;
            if (now != null) now.Items.CollectionChanged += MenuItemsChanged;
            UpdateDrop();
        }

        private void MenuItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateDrop();

        private void UpdateDrop() => _drop.IsVisible = Menu is { Items.Count: > 0 };

        private void UpdateLabel()
        {
            _label.Text = Label;
            _label.IsVisible = !string.IsNullOrEmpty(Label);
        }
    }
}
