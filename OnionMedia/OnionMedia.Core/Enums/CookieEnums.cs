/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using System.ComponentModel.DataAnnotations;

namespace OnionMedia.Core.Enums
{
    /// <summary>
    /// The source from which cookies are read for yt-dlp.
    /// </summary>
    public enum CookieSource
    {
        None,
        Browser,
        File,
        Pasted
    }

    /// <summary>
    /// Severity of an inline cookie notification. Maps to WinUI InfoBarSeverity in the UI layer,
    /// keeping OnionMedia.Core free of any UI dependency.
    /// </summary>
    public enum CookieNoticeSeverity
    {
        Informational,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// Browser keys supported by yt-dlp for --cookies-from-browser.
    /// The Display name shows the base browser plus known forks that share the same key.
    /// The enum NAME (lowercased) is the exact key passed to yt-dlp.
    /// <see cref="Custom"/> means the user typed a custom key (stored separately).
    /// </summary>
    public enum CookieBrowser
    {
        [Display(Name = "Chrome (+ Arc, Thorium, Ungoogled…)")]
        Chrome,
        [Display(Name = "Brave")]
        Brave,
        [Display(Name = "Chromium")]
        Chromium,
        [Display(Name = "Edge (+ Beta, Dev, Canary)")]
        Edge,
        [Display(Name = "Firefox (+ LibreWolf, Waterfox, Floorp, Zen…)")]
        Firefox,
        [Display(Name = "Opera (+ Opera GX)")]
        Opera,
        [Display(Name = "Vivaldi")]
        Vivaldi,
        [Display(Name = "Whale")]
        Whale,
        [Display(Name = "Safari")]
        Safari,
        [Display(Name = "Custom key…")]
        Custom
    }
}
