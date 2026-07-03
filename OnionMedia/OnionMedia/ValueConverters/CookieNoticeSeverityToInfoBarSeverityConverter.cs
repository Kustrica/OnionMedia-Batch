/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using OnionMedia.Core.Enums;
using System;

namespace OnionMedia.ValueConverters
{
    /// <summary>
    /// Maps the UI-independent <see cref="CookieNoticeSeverity"/> from OnionMedia.Core
    /// onto the WinUI <see cref="InfoBarSeverity"/> used by the cookie notification bar.
    /// </summary>
    sealed class CookieNoticeSeverityToInfoBarSeverityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is CookieNoticeSeverity severity)
            {
                return severity switch
                {
                    CookieNoticeSeverity.Success => InfoBarSeverity.Success,
                    CookieNoticeSeverity.Warning => InfoBarSeverity.Warning,
                    CookieNoticeSeverity.Error => InfoBarSeverity.Error,
                    _ => InfoBarSeverity.Informational
                };
            }
            return InfoBarSeverity.Informational;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
