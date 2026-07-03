/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using OnionMedia.Core.Services;
using OnionMedia.Views.Dialogs;

namespace OnionMedia.Services
{
    sealed class CookieViewerDialogService : ICookieViewerDialog
    {
        public async Task ShowCookiesAsync(string title, string content)
        {
            var dlg = new CookieViewerDialog(title, content) { XamlRoot = UIResources.XamlRoot };
            await dlg.ShowAsync();
        }
    }
}