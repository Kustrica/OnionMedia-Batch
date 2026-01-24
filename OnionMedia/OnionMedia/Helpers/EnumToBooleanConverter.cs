using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace OnionMedia.Helpers
{
    public partial class EnumToBooleanConverter : IValueConverter
    {
        public EnumToBooleanConverter()
        {
        }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (parameter is string enumString && value != null && value.GetType().IsEnum)
            {
                if (!Enum.IsDefined(value.GetType(), value))
                {
                    return false;
                }

                try
                {
                    var enumValue = Enum.Parse(value.GetType(), enumString);
                    return enumValue.Equals(value);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (parameter is string enumString)
            {
                if (value is bool isChecked && isChecked)
                {
                    try
                    {
                        return Enum.Parse(targetType, enumString);
                    }
                    catch
                    {
                         return DependencyProperty.UnsetValue;
                    }
                }
                return DependencyProperty.UnsetValue;
            }

            return DependencyProperty.UnsetValue;
        }
    }
}
