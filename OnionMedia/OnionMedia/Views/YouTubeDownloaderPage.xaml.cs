/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using AngleSharp.Dom;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OnionMedia.Core.ViewModels;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.UI.Xaml.Navigation;
using OnionMedia.Core;
using OnionMedia.Core.Models;
using YoutubeExplode.Playlists;
using Visibility = Microsoft.UI.Xaml.Visibility;
using System;
using Microsoft.UI.Xaml.Media.Animation;

namespace OnionMedia.Views
{
    public sealed partial class YouTubeDownloaderPage : Page
    {
        public YouTubeDownloaderViewModel ViewModel { get; }

        public YouTubeDownloaderPage()
        {
            ViewModel = Ioc.Default.GetService<YouTubeDownloaderViewModel>();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            Loaded += OnLoaded;
            InitializeComponent();
            App.MainWindow.SizeChanged += (_, _) => OnSizeChanged();
            // PropertyChanged can be raised from background threads; OnSizeChanged marshals to the UI thread itself.
            ViewModel.PropertyChanged += async (_, _) =>
            {
                try
                {
                    await Task.Delay(100);
                    OnSizeChanged();
                }
                catch (Exception ex) { Debug.WriteLine($"Donation banner update failed: {ex}"); }
            };
            AppSettings.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppSettings.ShowDonationBanner)) OnSizeChanged();
            };
			searchResultsList.SizeChanged += (_, _) => OnSizeChanged();
			videoQueue.SizeChanged += (_, _) => OnSizeChanged();

            // Grow the queue smoothly when it holds more than 3 items (and keep it big while/after downloading).
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.QueueExpanded))
                    DispatcherQueue.TryEnqueue(() => UpdateQueueHeight(animate: true));
            };
            SizeChanged += (_, _) => UpdateQueueHeight(animate: false);
            Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => UpdateQueueHeight(animate: false));
		}

        private const double CollapsedQueueHeight = 300;
        private Storyboard queueHeightStoryboard;
        private double targetQueueHeight = CollapsedQueueHeight;

        private double GetExpandedQueueHeight()
        {
            // Use most of the visible area, but never less than the collapsed size.
            // The list ends where the action buttons + the reserved bottom strip (Cookies / open folder overlay) begin,
            // so nothing overlaps at any window size; the page scrolls instead if the window is tiny.
            // NOTE: scrollViewer is VerticalAlignment="Top", so its ViewportHeight equals the CURRENT content height
            // (that clamped the list to its collapsed size). The page itself (mainFrame) has the real available height.
            double viewport = mainFrame.ActualHeight > 0 ? mainFrame.ActualHeight
                : (App.MainWindow.Bounds.Height > 0 ? App.MainWindow.Bounds.Height - 100 : 700);
            double top;
            try { top = videoQueue.TransformToVisual(mainFrame).TransformPoint(new Windows.Foundation.Point(0, 0)).Y + scrollViewer.VerticalOffset; }
            catch { top = 60; }
            double actions = queueActionsRow?.ActualHeight > 0 ? queueActionsRow.ActualHeight + queueActionsRow.Margin.Top + queueActionsRow.Margin.Bottom : 64;
            double below = 10 /* bottom row of the playlist card */ + actions + ContentArea.Padding.Bottom + 2;
            return Math.Max(CollapsedQueueHeight, Math.Floor(viewport - top - below));
        }

        private void UpdateQueueHeight(bool animate)
        {
            double target = ViewModel.QueueExpanded ? GetExpandedQueueHeight() : CollapsedQueueHeight;
            if (Math.Abs(target - targetQueueHeight) < 1 && (queueHeightStoryboard != null || Math.Abs(videoQueue.MaxHeight - target) < 1))
                return;
            targetQueueHeight = target;

            queueHeightStoryboard?.Stop();
            if (!animate)
            {
                queueHeightStoryboard = null;
                videoQueue.MaxHeight = target;
                return;
            }

            var animation = new DoubleAnimation
            {
                From = double.IsInfinity(videoQueue.MaxHeight) ? videoQueue.ActualHeight : videoQueue.MaxHeight,
                To = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, videoQueue);
            Storyboard.SetTargetProperty(animation, "MaxHeight");
            var sb = new Storyboard();
            sb.Children.Add(animation);
            sb.Completed += (_, _) =>
            {
                // Keep the final value as local value and release the animation (otherwise HoldEnd would block later changes).
                videoQueue.MaxHeight = target;
                sb.Stop();
                if (queueHeightStoryboard == sb)
                    queueHeightStoryboard = null;
                OnSizeChanged();
            };
            queueHeightStoryboard = sb;
            sb.Begin();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
	        base.OnNavigatedTo(e);
            OnSizeChanged();
        }

        private int bannerUpdatePending;

        /// <summary>
        /// Requests a (deferred) check whether the donation banner fits. Safe to call from any thread and from
        /// SizeChanged handlers: the actual work runs once, later, on the UI thread.
        /// </summary>
        private void OnSizeChanged()
        {
            if (System.Threading.Interlocked.Exchange(ref bannerUpdatePending, 1) == 1) return;
            var queue = DispatcherQueue;
            if (queue == null || !queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateDonationBanner))
                System.Threading.Interlocked.Exchange(ref bannerUpdatePending, 0);
        }

        private void UpdateDonationBanner()
        {
            System.Threading.Interlocked.Exchange(ref bannerUpdatePending, 0);
            try
            {
                bool show = false;
                if (AppSettings.Instance.ShowDonationBanner && IsLoaded && XamlRoot != null && ActualHeight > 0)
                {
                    // Use the banner's fixed height: ActualHeight is 0 while it is collapsed, which made the check
                    // flip between visible and collapsed on every layout pass (layout cycle -> crash).
                    double bannerHeight = double.IsNaN(donationGrid.Height) || donationGrid.Height <= 0 ? 100 : donationGrid.Height;
                    // Show only when the page does not scroll and there is free space below the content.
                    show = scrollViewer.ScrollableHeight == 0 && ActualHeight - scrollViewer.ViewportHeight > bannerHeight;
                }
                var visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (donationGrid.Visibility != visibility)
                    donationGrid.Visibility = visibility;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Donation banner update failed: {ex}");
            }
        }

		//Workaround for a bug from WinUI 3 (freezing ProgressRing)
		private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Ensure dialogs (e.g. the cookie viewer) opened from this page have a valid XamlRoot,
            // even when the downloader page is the first page the user visits.
            UIResources.XamlRoot = this.XamlRoot;

            btnProgressRing.IsActive = false;
            btnProgressRing.IsActive = true;
            rotateInSearchIconTrigger.Value = false;
            videolink.Focus(FocusState.Programmatic);
        }
        
        // Ctrl/Shift multi-select: the VM removes every selected item and hides the trim section.
        private void VideoQueue_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ViewModel.SelectedVideos = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<StreamItemModel>(videoQueue.SelectedItems));

        private void VideoQueue_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Delete || ViewModel.QueueIsEmpty) return;
            ViewModel.RemoveCommand.Execute(videoQueue.SelectedIndex);
            e.Handled = true;
        }

        private void RemoveAll_Clicked(object sender, RoutedEventArgs e)
        {
            removeBtnFlyout.Hide();
        }

        private void OpenDonationPage(object sender, RoutedEventArgs e)
        {
	        Process.Start(new ProcessStartInfo
			{
				FileName = GlobalResources.LocalDonationUrl,
				UseShellExecute = true
			});
		}

        private void OpenVideoFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = AppSettings.Instance.DownloadsVideoSavePath;
                if (System.IO.Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
            }
            catch { }
        }
    }
}
