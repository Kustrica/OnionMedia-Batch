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
    /// Target codecs for downloaded videos.
    /// </summary>
    public enum VideoCompatibilityMode
    {
        /// <summary>Default. Keep whatever codec the site delivers (AV1/VP9/Opus allowed), only remuxed into MP4. Fastest, no conversion.</summary>
        [Display(Name = "Original")]
        Original,

        /// <summary>H.264 (AVC) video + AAC audio in MP4. Plays on every device (iPhone gallery, TVs, old PCs). Converted only when needed.</summary>
        [Display(Name = "Universal")]
        Universal,

        /// <summary>H.265 (HEVC, tagged hvc1) + AAC in MP4. Smaller files, plays on iPhone/macOS and modern devices. Converted only when needed.</summary>
        [Display(Name = "Compact")]
        Compact
    }
}
