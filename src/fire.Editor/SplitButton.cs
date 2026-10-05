using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace fire.Editor
{
    public class SplitButton : ItemsControl
    {
        static SplitButton()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SplitButton),
            new FrameworkPropertyMetadata(typeof(SplitButton)));
        }

        private Button? _button;
        private ToggleButton? _toggleButton;
        private ContextMenu? _contextMenu;

        #region Content

        public object Content
        {
            get => GetValue(ContentProperty);
            set => SetValue(ContentProperty, value);
        }

        public static readonly DependencyProperty ContentProperty =
        DependencyProperty.Register(
        nameof(Content),
        typeof(object),
        typeof(SplitButton));

        #endregion

        #region Command

        public ICommand? Command
        {
            get => (ICommand?)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(
        nameof(Command),
        typeof(ICommand),
        typeof(SplitButton));

        #endregion

        #region CommandParameter

        public object? CommandParameter
        {
            get => GetValue(CommandParameterProperty);
            set => SetValue(CommandParameterProperty, value);
        }

        public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(
        nameof(CommandParameter),
        typeof(object),
        typeof(SplitButton));

        #endregion

        #region Click Event

        public static readonly RoutedEvent ClickEvent =
        EventManager.RegisterRoutedEvent(
        nameof(Click),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(SplitButton));

        public event RoutedEventHandler Click
        {
            add => AddHandler(ClickEvent, value);
            remove => RemoveHandler(ClickEvent, value);
        }

        #endregion

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_button != null)
                _button.Click -= MainButton_Click;

            if (_toggleButton != null)
                _toggleButton.Click -= ToggleButton_Click;

            _button = GetTemplateChild("PART_Button") as Button;
            _toggleButton = GetTemplateChild("PART_ToggleButton") as ToggleButton;

            if (_button != null)
                _button.Click += MainButton_Click;

            if (_toggleButton != null)
                _toggleButton.Click += ToggleButton_Click;
        }

        private void MainButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseEvent(new RoutedEventArgs(ClickEvent, this));

            if (Command?.CanExecute(CommandParameter) == true)
            {
                Command.Execute(CommandParameter);
            }
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_toggleButton == null)
                return;

            if (Items.Count == 0)
            {
                _toggleButton.IsChecked = false;
                return;
            }

            _contextMenu = new ContextMenu();

            foreach (var item in Items)
            {
                if (item is MenuItem menuItem)
                {
                    
                    var mItem = new MenuItem
                    {
                        Header = menuItem.Header,
                        Icon = menuItem.Icon,
                        InputGestureText = menuItem.InputGestureText,
                        Command = menuItem.Command,
                        CommandParameter = menuItem.CommandParameter
                    };

                    mItem.Click += (s, args) => menuItem.RaiseEvent(args);

                    _contextMenu.Items.Add(mItem);
                }
                else if (item is Separator)
                {
                    _contextMenu.Items.Add(new Separator());
                }
                else
                {
                    var mItem = new MenuItem
                    {
                        Header = item,
                    };
                    _contextMenu.Items.Add(mItem);
                }
            }

            //_contextMenu.ItemsSource = this.Items;

            _contextMenu.PlacementTarget = this;
            _contextMenu.Placement = PlacementMode.Bottom;

            _contextMenu.Closed += ContextMenu_Closed;

            _contextMenu.IsOpen = true;
        }

        private void ContextMenu_Closed(object? sender, RoutedEventArgs e)
        {
            if (_toggleButton != null)
            {
                _toggleButton.IsChecked = false;
            }

            if (_contextMenu != null)
            {
                _contextMenu.Closed -= ContextMenu_Closed;
            }
        }
    }
}
