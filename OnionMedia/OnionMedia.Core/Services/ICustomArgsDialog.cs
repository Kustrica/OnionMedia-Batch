/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using System.Threading.Tasks;

namespace OnionMedia.Core.Services
{
    /// <summary>
    /// Dialog to edit, validate and test the custom yt-dlp parameters (stored in AppSettings).
    /// </summary>
    public interface ICustomArgsDialog
    {
        /// <param name="testUrl">URL used by "Test with selected video" (may be null).</param>
        /// <returns>True if the user saved.</returns>
        Task<bool> ShowAsync(string testUrl);
    }
}
