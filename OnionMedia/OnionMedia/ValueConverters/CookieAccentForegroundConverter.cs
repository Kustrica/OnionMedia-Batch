/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace OnionMedia.ValueConverters
{
    /// <summary>
    /// Converts the "cookies enabled" flag into a foreground brush for the cookie button label:
    /// the app accent brush when cookies are active, the default text brush otherwise.
    /// Falls back gracefully if a theme resource is missing.
    /// </summary>
    sealed class CookieAccentForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool enabled = value is bool b && b;
            string resourceKey = enabled ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush";

            if (Application.Current.Resources.TryGetValue(resourceKey, out object brush) && brush is Brush themeBrush)
                return themeBrush;

            // Safe fallback so the UI never crashes if a theme resource is unavailable.
            return new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
