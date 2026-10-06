using Avalonia.Controls;

namespace fire.Editor
{
    /// <summary>A group of tool buttons with a grip on its left side (the look of the WPF tool bar). The items are any controls: buttons, split buttons, combo boxes, texts, separators.
    /// The bar has no background of its own, so it shows what is behind it - the acrylic of the window.
    /// Own controls with the names of the ones in the package Tulesha.ToolBarControls.Avalonia, which is built for Avalonia 11 and does not load on Avalonia 12.</summary>
    public class ToolBar : ItemsControl
    {
    }

    /// <summary>Holds <see cref="ToolBar"/>s side by side and wraps the ones that do not fit into the next row (the bands of the WPF <c>ToolBarTray</c>).
    /// Like <see cref="ToolBar"/> it is see-through.</summary>
    public class ToolBarTray : ItemsControl
    {
    }
}
