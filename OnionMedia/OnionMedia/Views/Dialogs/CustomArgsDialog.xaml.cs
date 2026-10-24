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
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using OnionMedia.Core;
using OnionMedia.Core.Classes;
using OnionMedia.Core.Models;
using OnionMedia.Core.Services;

namespace OnionMedia.Views.Dialogs
{
    public sealed partial class CustomArgsDialog : ContentDialog
    {
        private readonly string testUrl;
        private CancellationTokenSource runCts;
        private bool initializing = true;
        private bool userToggled;

        public CustomArgsDialog(string testUrl)
        {
            InitializeComponent();
            this.testUrl = testUrl;

            var s = AppSettings.Instance;
            enableToggle.IsOn = s.UseCustomYtDlpArgs;
            argsBox.Text = s.CustomYtDlpArgs ?? string.Empty;
            autoUpdateCheck.IsChecked = s.AutoUpdateYtDlp;
            initializing = false;

            BuildCatalog();
            testButton.IsEnabled = !string.IsNullOrWhiteSpace(testUrl);
            ToolTipService.SetToolTip(testButton, string.IsNullOrWhiteSpace(testUrl)
                ? "Add a video to the queue (or select one) to test the parameters with it."
                : "Run yt-dlp --simulate with these parameters on: " + testUrl);

            Loaded += async (_, _) =>
            {
                FitToWindow();
                if (XamlRoot != null) XamlRoot.Changed += (_, _) => FitToWindow();
                ShowValidation();
                await RefreshVersionAsync();
            };
            Opened += (_, _) => HookLightDismiss();
            Closed += (_, _) =>
            {
                UnhookLightDismiss();
                runCts?.Cancel();
            };
        }

        #region Light dismiss (click outside = Cancel)

        // ContentDialog has no light dismiss. A click on the dimmed area closes the dialog like Cancel:
        // Hide() returns ContentDialogResult.None and OnSaveClick is not called, so nothing is saved.
        private FrameworkElement dialogSurface;
        private readonly List<UIElement> smokeLayers = new();
        private PointerEventHandler dialogPointerHandler;

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            dialogSurface = GetTemplateChild("BackgroundElement") as FrameworkElement;
        }

        private void HookLightDismiss()
        {
            // 1) The smoke layer: WinUI puts a full-window Rectangle into its own popup behind the dialog.
            if (XamlRoot != null)
            {
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
                {
                    if (popup.Child is Rectangle smoke && !smokeLayers.Contains(smoke))
                    {
                        smoke.PointerPressed += OnSmokePointerPressed;
                        smokeLayers.Add(smoke);
                    }
                }
            }

            // 2) The dialog's own full-window layer (in case it is hit-testable instead of the smoke rectangle).
            dialogPointerHandler ??= OnDialogPointerPressed;
            AddHandler(PointerPressedEvent, dialogPointerHandler, true);
        }

        private void UnhookLightDismiss()
        {
            foreach (var smoke in smokeLayers) smoke.PointerPressed -= OnSmokePointerPressed;
            smokeLayers.Clear();
            if (dialogPointerHandler != null) RemoveHandler(PointerPressedEvent, dialogPointerHandler);
        }

        private void OnSmokePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            e.Handled = true;
            Hide();
        }

        private void OnDialogPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (dialogSurface == null || !IsOutsideDialogSurface(e.OriginalSource as DependencyObject)) return;
            e.Handled = true;
            Hide();
        }

        /// <summary>
        /// True only when the click reached the dialog's full-window layer without passing through the dialog box itself.
        /// Clicks in flyouts / ComboBox drop-downs come from popups whose visual tree does not lead back to the dialog,
        /// so they never count as "outside".
        /// </summary>
        private bool IsOutsideDialogSurface(DependencyObject source)
        {
            for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (ReferenceEquals(current, dialogSurface)) return false;
                if (ReferenceEquals(current, this)) return true;
            }
            return false;
        }

        #endregion

        /// <summary>Keeps the dialog inside the window: two columns when there is room, one column otherwise; scrolls vertically if needed.</summary>
        private void FitToWindow()
        {
            if (XamlRoot == null) return;
            double w = XamlRoot.Size.Width, h = XamlRoot.Size.Height;
            double width = Math.Max(300, Math.Min(960, w - 96));
            rootGrid.Width = width;
            bool twoColumns = width >= 760;
            rightColumn.Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(rightPanel, twoColumns ? 1 : 0);
            Grid.SetRow(rightPanel, twoColumns ? 0 : 1);
            Grid.SetRow(bottomBar, twoColumns ? 1 : 2);
            rightPanel.Margin = twoColumns ? new Thickness(0) : new Thickness(0, 14, 0, 0);
            rootGrid.ColumnSpacing = twoColumns ? 24 : 0;
            // Title + Save/Cancel + dialog padding take ~190 px; the yt-dlp bar ~60 px.
            double available = Math.Max(240, h - 190);
            rootScroll.MaxHeight = available;
            if (twoColumns)
            {
                // The catalog defines the row height; the left column stretches to it and splits it equally between
                // Parameters and Output (star rows), so both boxes are the same size and the columns end on one line.
                catalogScroll.Height = Math.Max(300, Math.Min(560, available - 60 - 34));
                leftPanel.Height = double.NaN;
            }
            else
            {
                catalogScroll.Height = 320;
                leftPanel.Height = 520;
            }
        }

        private async Task RefreshVersionAsync()
        {
            try
            {
                string version = await YtDlpUpdater.GetVersionAsync();
                versionText.Text = FormatVersion(version);
            }
            catch (Exception ex) { versionText.Text = "yt-dlp: " + ex.Message; }
        }

        /// <summary>"2026.10.21" -> "yt-dlp 2026.10.21 (released 21 Oct 2026)". yt-dlp versions are release dates.</summary>
        internal static string FormatVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "yt-dlp not found";
            version = version.Trim();
            var m = Regex.Match(version, @"^(\d{4})\.(\d{1,2})\.(\d{1,2})");
            if (m.Success && DateTime.TryParseExact($"{m.Groups[1].Value}-{int.Parse(m.Groups[2].Value):00}-{int.Parse(m.Groups[3].Value):00}",
                    "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return $"yt-dlp {version} (released {date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)})";
            return "yt-dlp " + version;
        }

        #region Parameter catalog

        private sealed record CatalogOption(string Name, string Description, string[] Values = null, string ValuesInfo = null, bool FreeText = false, string Placeholder = null);

        private static readonly CatalogOption[] Catalog =
        {
            new("-S", "Format sort: prefer a resolution, codec or smaller size. Works together with Max Quality.",
                new[] { "res:1080", "res:720", "res:2160", "vcodec:h264", "vcodec:hevc", "vcodec:av1", "acodec:aac", "+size", "res:1080,vcodec:h264,acodec:aac" },
                "Comma-separated sort fields, e.g. res:1080, vcodec:h264 / hevc / av1 / vp9, acodec:aac / opus, fps, +size (smallest first), br, ext:mp4. You can type your own combination."),
            new("--impersonate", "Pretend to be a real browser (TLS fingerprint). Helps with Cloudflare-protected sites.",
                new[] { "chrome", "edge", "safari", "firefox", "chrome-116", "safari-17" },
                "Browser targets: chrome, edge, safari, firefox (optionally with a version, e.g. chrome-116). OnionMedia already uses chrome by default."),
            new("--sleep-requests", "Wait between requests (seconds). Avoids rate limits on large playlists.",
                new[] { "1", "2", "5" }, "Number of seconds (decimals allowed, e.g. 0.5)."),
            new("--limit-rate", "Limit the download speed.",
                new[] { "500K", "1M", "5M", "10M" }, "Bytes per second with K / M / G suffix, e.g. 500K, 2.5M."),
            new("--retries", "How often to retry a failed download.",
                new[] { "3", "10", "20", "infinite" }, "A number or 'infinite' (default is 10)."),
            new("--concurrent-fragments", "Download several fragments in parallel (HLS/DASH). Faster on slow servers.",
                new[] { "4", "8", "16" }, "Number of parallel fragments (default 1)."),
            new("--proxy", "Use an HTTP/HTTPS/SOCKS proxy.", FreeText: true, Placeholder: "socks5://127.0.0.1:1080",
                ValuesInfo: "A proxy URL: http://host:port, https://host:port or socks5://user:pass@host:port. Empty string = direct connection."),
            new("--geo-bypass-country", "Fake the X-Forwarded-For country to get around region locks.",
                new[] { "US", "DE", "GB", "NL", "JP" }, "Two-letter ISO 3166 country code, e.g. US, DE, GB."),
            new("--force-ipv4", "Connect over IPv4 only (fixes some 'connection reset' / 403 errors)."),
            new("--embed-subs", "Embed subtitles into the video file (use together with --sub-langs)."),
            new("--sub-langs", "Which subtitle languages to download/embed.",
                new[] { "en.*", "ru.*", "en.*,ru.*", "all", "all,-live_chat" }, "Comma-separated language codes or regex, e.g. en.*, de, all, all,-live_chat (exclude chat)."),
            new("--embed-chapters", "Add chapter markers to the video file."),
            new("--embed-metadata", "Write title, uploader, date etc. into the file's metadata."),
            new("--sponsorblock-remove", "Cut out SponsorBlock segments (YouTube only).",
                new[] { "sponsor", "sponsor,selfpromo", "all" }, "Comma-separated categories: sponsor, intro, outro, selfpromo, preview, filler, interaction, music_offtopic, all."),
            new("--add-header", "Send an extra HTTP header (e.g. a Referer that a site requires).", FreeText: true, Placeholder: "Referer:https://example.com/",
                ValuesInfo: "FIELD:VALUE, e.g. Referer:https://site.com/ or User-Agent:Mozilla/5.0 … Can be used several times."),
            new("--extractor-args", "Pass options to a specific extractor.",
                new[] { "youtube:player_client=default,web_safari", "youtube:lang=en" }, "KEY:ARGS, e.g. youtube:player_client=web,ios. See the yt-dlp README → Extractor arguments."),
            new("--no-check-certificates", "Ignore HTTPS certificate errors (only for sites with broken certificates)."),
        };

        private void BuildCatalog()
        {
            catalogPanel.Children.Clear();
            bool first = true;
            foreach (var option in Catalog)
            {
                if (!first)
                    catalogPanel.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6), Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });
                first = false;
                catalogPanel.Children.Add(CreateCatalogRow(option));
            }
        }

        private FrameworkElement CreateCatalogRow(CatalogOption option)
        {
            // Columns: text | value | info | copy | add. The three icon slots have the same width and no extra spacing,
            // so the gaps between info, copy and add are identical in every row.
            var grid = new Grid { ColumnSpacing = 0, Padding = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconSlot) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconSlot) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconSlot) });

            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = option.Name, FontFamily = new FontFamily("Consolas"), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsTextSelectionEnabled = true });
            text.Children.Add(new TextBlock { Text = option.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            grid.Children.Add(text);

            Func<string> getValue = () => null;
            if (option.Values != null || option.FreeText)
            {
                var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
                if (option.FreeText)
                {
                    var box = new TextBox { Width = 150, FontFamily = new FontFamily("Consolas"), FontSize = 12, PlaceholderText = option.Placeholder ?? "value" };
                    getValue = () => box.Text;
                    valueRow.Children.Add(box);
                }
                else
                {
                    var combo = new ComboBox { Width = 150, IsEditable = true, FontSize = 12, ItemsSource = option.Values, SelectedIndex = 0 };
                    getValue = () => string.IsNullOrWhiteSpace(combo.Text) ? combo.SelectedItem as string : combo.Text;
                    valueRow.Children.Add(combo);
                }
                Grid.SetColumn(valueRow, 1);
                grid.Children.Add(valueRow);
            }
            if (!string.IsNullOrEmpty(option.ValuesInfo))
            {
                var info = new Border
                {
                    Width = IconSlot, Height = IconSlot, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), VerticalAlignment = VerticalAlignment.Center,
                    Child = new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], Glyph = "\uE946", FontSize = 14,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] }
                };
                ToolTipService.SetToolTip(info, new ToolTip { Content = new TextBlock { Text = "Accepted values: " + option.ValuesInfo, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 } });
                Grid.SetColumn(info, 2);
                grid.Children.Add(info);
            }

            var copy = CreateIconButton("\uE8C8", "Copy");
            copy.Click += (_, _) =>
            {
                var package = new DataPackage();
                package.SetText(BuildArgument(option.Name, getValue()));
                Clipboard.SetContent(package);
                ShowCopied(copy);
            };
            Grid.SetColumn(copy, 3);
            grid.Children.Add(copy);

            var add = CreateIconButton("\uE710", "Add to Parameters (replaces the same option)");
            add.Click += (_, _) => AddArgument(option.Name, getValue());
            Grid.SetColumn(add, 4);
            grid.Children.Add(add);
            return grid;
        }

        private const double IconSlot = 32;

        private static Button CreateIconButton(string glyph, string tooltip)
        {
            var button = new Button
            {
                Content = new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], Glyph = glyph, FontSize = 14 },
                Width = IconSlot, Height = IconSlot,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            ToolTipService.SetToolTip(button, tooltip);
            return button;
        }

        private static async void ShowCopied(Button button)
        {
            var icon = button.Content as FontIcon;
            if (icon == null) return;
            icon.Glyph = "\uE73E";
            await Task.Delay(1200);
            icon.Glyph = "\uE8C8";
        }

        private static string BuildArgument(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return name;
            value = value.Trim();
            if (value.Any(char.IsWhiteSpace) && !(value.StartsWith("\"") && value.EndsWith("\"")))
                value = "\"" + value.Replace("\"", "\\\"") + "\"";
            return name + " " + value;
        }

        /// <summary>Appends the option on its own line; a line that already starts with the same option is replaced (except for repeatable --add-header).</summary>
        private void AddArgument(string name, string value)
        {
            string argument = BuildArgument(name, value);
            var lines = (argumentsText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            bool repeatable = name == "--add-header";
            int index = repeatable ? -1 : lines.FindIndex(l =>
            {
                string t = l.TrimStart();
                return t == name || t.StartsWith(name + " ", StringComparison.Ordinal) || t.StartsWith(name + "=", StringComparison.Ordinal);
            });
            if (index >= 0) lines[index] = argument;
            else
            {
                while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
                lines.Add(argument);
            }
            argsBox.Text = string.Join(Environment.NewLine, lines);
            argsBox.SelectionStart = argsBox.Text.Length;
        }

        private string argumentsText => argsBox.Text;

        #endregion

        private YtDlpCustomArgs.ValidationResult ShowValidation()
        {
            var r = YtDlpCustomArgs.Validate(argsBox.Text);
            if (string.IsNullOrWhiteSpace(argsBox.Text) || r.Tokens.Count == 0 && r.IsValid)
            {
                statusBar.IsOpen = false;
                return r;
            }

            if (!r.IsValid)
            {
                statusBar.Severity = InfoBarSeverity.Error;
                statusBar.Title = "Invalid parameters";
                statusBar.Message = string.Join(Environment.NewLine, r.Errors);
            }
            else if (r.Warnings.Any())
            {
                statusBar.Severity = InfoBarSeverity.Warning;
                statusBar.Title = $"{r.Tokens.Count} argument(s)";
                statusBar.Message = string.Join(Environment.NewLine, r.Warnings);
            }
            else
            {
                statusBar.Severity = InfoBarSeverity.Success;
                statusBar.Title = $"{r.Tokens.Count} argument(s)";
                statusBar.Message = enableToggle.IsOn ? "Syntax OK. They will be applied to new fetches and downloads." : "Syntax OK, but \"Apply custom parameters\" is Off – they are saved and not used.";
            }
            statusBar.IsOpen = true;
            return r;
        }

        private void OnArgsTextChanged(object sender, TextChangedEventArgs e)
        {
            // Typing parameters normally means "use them": switch on automatically unless the user turned it off on purpose.
            if (!initializing && !userToggled && !enableToggle.IsOn && !string.IsNullOrWhiteSpace(argsBox.Text))
            {
                initializing = true;
                enableToggle.IsOn = true;
                initializing = false;
            }
            ShowValidation();
        }

        private void OnToggleChanged(object sender, RoutedEventArgs e)
        {
            if (!initializing) userToggled = true;
            ShowValidation();
        }

        private void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var r = ShowValidation();
            if (enableToggle.IsOn && !r.IsValid)
            {
                args.Cancel = true;
                return;
            }
            var s = AppSettings.Instance;
            s.CustomYtDlpArgs = argsBox.Text ?? string.Empty;
            s.UseCustomYtDlpArgs = enableToggle.IsOn;
            s.AutoUpdateYtDlp = autoUpdateCheck.IsChecked == true;
        }

        private async Task RunAsync(string title, Func<CancellationToken, Task<string>> action)
        {
            runCts?.Cancel();
            runCts = new CancellationTokenSource();
            var token = runCts.Token;
            SetBusy(true);
            logBox.Text = $"> {title}{Environment.NewLine}";
            try
            {
                string output = await action(token);
                logBox.Text += output;
            }
            catch (OperationCanceledException) { logBox.Text += "Stopped."; }
            catch (Exception ex) { logBox.Text += "Error: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private void SetBusy(bool busy)
        {
            busyRing.IsActive = busy;
            cancelRunButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            validateButton.IsEnabled = !busy;
            updateButton.IsEnabled = !busy;
            testButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(testUrl);
        }

        private async void OnValidateClick(object sender, RoutedEventArgs e)
        {
            var r = ShowValidation();
            if (!r.IsValid)
            {
                logBox.Text = string.Join(Environment.NewLine, r.Errors);
                return;
            }
            await RunAsync("yt-dlp " + YtDlpCustomArgs.JoinForCommandLine(r.Tokens), async ct =>
            {
                var (ok, output) = await YtDlpCustomArgs.CheckWithYtDlpAsync(argsBox.Text, ct);
                return (ok ? "yt-dlp accepted the parameters." : "yt-dlp rejected the parameters:") + Environment.NewLine + output;
            });
        }

        private async void OnTestClick(object sender, RoutedEventArgs e)
        {
            var r = ShowValidation();
            if (!r.IsValid || string.IsNullOrWhiteSpace(testUrl)) return;
            await RunAsync("yt-dlp --simulate -v ... " + testUrl, async ct =>
            {
                var (ok, output) = await YtDlpCustomArgs.TestAgainstUrlAsync(argsBox.Text, testUrl, ct);
                return (ok ? "OK – yt-dlp could extract the video with these parameters." : "yt-dlp reported an error (see below).") + Environment.NewLine + output;
            });
        }

        private async void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            await RunAsync("yt-dlp -U", async ct => await YtDlpUpdater.EnsureUpToDateAsync(force: true, ct));
            await RefreshVersionAsync();
        }

        private void OnStopClick(object sender, RoutedEventArgs e) => runCts?.Cancel();
    }
}
