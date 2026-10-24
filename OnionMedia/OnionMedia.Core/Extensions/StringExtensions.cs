/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using OnionMedia.Core.Services;

namespace OnionMedia.Core.Extensions
{
    public static class StringExtensions
    {
        /// <summary>
        /// Removes forbidden characters to use the string as a filename (see <see cref="ToSafeFileName"/>).
        /// </summary>
        /// <param name="input">The string to trim.</param>
        /// <returns>The trimmed filename string.</returns>
        public static string TrimToFilename(this string input, int maxLength)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            return input.ToSafeFileName(maxLength, "video");
        }

        // Invisible "blank" letters often used in titles (Hangul fillers, braille blank, Mongolian vowel separator, ...).
        private static readonly HashSet<char> invisibleChars = new()
        {
            '\u115F', '\u1160', '\u3164', '\uFFA0', '\u2800', '\u180E', '\u17B4', '\u17B5', '\u034F', '\u00AD', '\u061C', '\uFEFF'
        };
        private static readonly HashSet<string> reservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// Makes a title safe as a Windows file name that every app (Explorer, Photos, players) can open:
        /// Unicode NFC, no forbidden/control/format characters (zero-width, RTL marks), no invisible filler letters,
        /// no emoji/variation selectors, collapsed whitespace, no trailing dots/spaces, no reserved device names,
        /// at most <paramref name="maxLength"/> characters (never cutting a character in half).
        /// </summary>
        public static string ToSafeFileName(this string input, int maxLength = 120, string fallback = "video")
        {
            if (string.IsNullOrWhiteSpace(input)) return fallback;
            string text = input.Normalize(System.Text.NormalizationForm.FormC);
            var sb = new System.Text.StringBuilder(text.Length);
            char[] forbidden = @"<>:/\|?""*".ToCharArray();
            var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext())
            {
                string element = (string)e.Current;
                char c = element[0];
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(element, 0);
                bool isEmoji = char.IsSurrogate(c) && cat is System.Globalization.UnicodeCategory.OtherSymbol
                               || c is >= '\u2600' and <= '\u27BF' || c is >= '\uFE00' and <= '\uFE0F';
                if (isEmoji || invisibleChars.Contains(c)
                    || cat is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                        or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned
                        or System.Globalization.UnicodeCategory.Surrogate)
                    continue;
                if (forbidden.Contains(c)) { sb.Append(' '); continue; }
                if (char.IsWhiteSpace(c) || cat is System.Globalization.UnicodeCategory.SpaceSeparator)
                {
                    if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                    continue;
                }
                // Drop emoji modifiers / ZWJ sequences that survived as part of an element.
                foreach (char ch in element)
                    if (ch != '\u200D' && !(ch is >= '\uFE00' and <= '\uFE0F') && !invisibleChars.Contains(ch))
                        sb.Append(ch);
            }
            string result = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), " {2,}", " ").Trim().TrimEnd('.', ' ');

            if (maxLength > 0 && result.Length > maxLength)
            {
                var cut = new System.Text.StringBuilder();
                var e2 = System.Globalization.StringInfo.GetTextElementEnumerator(result);
                while (e2.MoveNext() && cut.Length + ((string)e2.Current).Length <= maxLength)
                    cut.Append((string)e2.Current);
                result = cut.ToString().Trim().TrimEnd('.', ' ');
            }
            if (result.Length == 0) return fallback;
            string stem = result.Split('.')[0].Trim();
            if (reservedNames.Contains(stem)) result = "_" + result;
            return result;
        }

        /// <summary>
        /// An extensionmethod for string.IsNullOrEmpty().
        /// </summary>
        /// <param name="input">The string to check</param>
        /// <returns>Indicates whetever the specified string is null or an empty string("").</returns>
        public static bool IsNullOrEmpty(this string input) => string.IsNullOrEmpty(input);

        /// <summary>
        /// An extensionmethod for string.IsNullOrWhiteSpace().
        /// </summary>
        /// <param name="input">The string to check</param>
        /// <returns>Indicates whetever the specified string is null, empty or contains only whitespaces.</returns>
        public static bool IsNullOrWhiteSpace(this string input) => string.IsNullOrWhiteSpace(input);

        public static string ShortString(this string input, int maxLength)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            if (input.Length <= maxLength)
                return input;

            int maxStringLen = maxLength - 3;
            if (maxStringLen < 0)
                maxStringLen = 0;

            string range = input[0..maxStringLen];
            return range + "...";
        }

        public static string GetLocalized(this string resourceName, string resourceSection = null)
        {
            return resourceService.GetLocalized(resourceName, resourceSection);
        }

        private static readonly IStringResourceService resourceService = IoC.Default.GetService<IStringResourceService>() ?? throw new ArgumentNullException();
    }
}
