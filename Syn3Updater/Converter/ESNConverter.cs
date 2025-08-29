using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Cyanlabs.Syn3Updater.Converter
{
    public class ESNConverter : IValueConverter
    {
        // Convert bool to string
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Handle null values
            if (value == null)
            {
                return ""; // Default value
            }

            // Convert bool to string
            if (value is bool boolValue)
            {

                if (targetType == typeof(string))
                {
                    return boolValue ? "ESN Locked (Pre MY20 only as requires REFORMAT)" : "Not ESN locked (no reformat required)";
                }
                else if (targetType == typeof(Brush))
                {
                    return boolValue ? Brushes.Red : Brushes.Green;
                }
                else if (targetType == typeof(Visibility))
                {
                    return boolValue ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    throw new NotSupportedException($"Target type {targetType} is not supported.");
                }
            }

            // Handle unexpected types
            throw new ArgumentException("Value must be a boolean.", nameof(value));
        }

        // Convert string back to bool (optional, for two-way binding)
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string stringValue)
            {
                return stringValue == "ESN Locked (Pre MY20 only as requires REFORMAT)";
            }

            return false; // Default value
        }
    }

    public class InvertedESNConverter : IValueConverter
    {
        // Convert bool to string
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Handle null values
            if (value == null)
            {
                return ""; // Default value
            }

            // Convert bool to string
            if (value is bool boolValue)
            {

                if (targetType == typeof(string))
                {
                    return boolValue ? "Not ESN locked (no reformat required)" : "ESN Locked (Pre MY20 only as requires REFORMAT)";
                }
                else if (targetType == typeof(Brush))
                {
                    return boolValue ? Brushes.Green : Brushes.Red;
                }
                else if (targetType == typeof(Visibility))
                {
                    return boolValue ? Visibility.Collapsed : Visibility.Visible;
                }
                else
                {
                    throw new NotSupportedException($"Target type {targetType} is not supported.");
                }
            }

            // Handle unexpected types
            throw new ArgumentException("Value must be a boolean.", nameof(value));
        }

        // Convert string back to bool (optional, for two-way binding)
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string stringValue)
            {
                return stringValue == "Not ESN locked (no reformat required)";
            }

            return false; // Default value
        }
    }
}