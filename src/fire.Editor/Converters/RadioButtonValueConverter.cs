using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Markup;

namespace fire.Editor.Converters
{
    public class RadioButtonValueConverter : MarkupExtension, IValueConverter
    {

        public RadioButtonValueConverter(object optionValue)
            => OptionValue = optionValue;

        public object OptionValue { get; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value.Equals(OptionValue);

        public object ConvertBack(object isChecked, Type targetType, object parameter, CultureInfo culture)
            => (bool)isChecked        // Is this the checked RadioButton? If so...
                ? OptionValue         // Send 'OptionValue' back to update the associated binding. Otherwise...
                : Binding.DoNothing;  // Return Binding.DoNothing, telling the binding 'ignore this change'

        public override object ProvideValue(IServiceProvider serviceProvider)
            => this;
    }
}
