/*
 * Copyright (C) 2022 Jaden Phil Nebel (Onionware)
 *
 * This file is part of OnionMedia.
 * OnionMedia is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3.

 * OnionMedia is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

 * You should have received a copy of the GNU Affero General Public License along with OnionMedia. If not, see <https://www.gnu.org/licenses/>.
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using OnionMedia.Core.Models;
using YoutubeExplode.Exceptions;
using OnionMedia.Core.Enums;
using System.Net.Http;
using System.Text;
using System.IO;
using OnionMedia.Core.Classes;
using OnionMedia.Core.Extensions;
using System.Text.RegularExpressions;
using TextCopy;
using OnionMedia.Core.Services;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using System.Net;
using DownloadState = OnionMedia.Core.Enums.DownloadState;
using System.Drawing;
using System.Drawing.Imaging;

namespace OnionMedia.Core.ViewModels
{
    [ObservableObject]
    public sealed partial class YouTubeDownloaderViewModel
    {
        public YouTubeDownloaderViewModel(IDialogService dialogService, IDownloaderDialogService downloaderDialogService, IDispatcherService dispatcher, INetworkStatusService networkStatusService, IToastNotificationService toastNotificationService, IPathProvider pathProvider, ITaskbarProgressService taskbarProgressService, IWindowClosingService windowClosingService, IFiletagEditorDialog filetagDialogService)
        {
            this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            this.downloaderDialogService = downloaderDialogService ?? throw new ArgumentNullException(nameof(downloaderDialogService));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.toastNotificationService = toastNotificationService ?? throw new ArgumentNullException(nameof(toastNotificationService));
            this.pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
            this.filetagDialogService = filetagDialogService ?? throw new ArgumentNullException(nameof(filetagDialogService));
            this.networkStatusService = networkStatusService;
            this.taskbarProgressService = taskbarProgressService;

			VideoFetchingErrors.CollectionChanged += (_,_) => OnPropertyChanged(nameof(VideoFetchingLogAvailable));
			SearchResults.CollectionChanged += (o, e) => OnPropertyChanged(nameof(AnyResults));
            Videos.CollectionChanged += OnProgressChanged;

            AddVideoCommand = new(async link => await FillInfosAsync(link), link => !DownloadFileCommand.IsRunning);

            AddSearchedVideo = new(async item => await FillInfosAsync(item.Url), item => !DownloadFileCommand.IsRunning);

            DownloadFileCommand = new(async qualityLabel => await DownloadVideosAsync(Videos, SelectedDownloadMode, qualityLabel));

            RemoveCommand = new(async index => await RemoveVideoAsync());

            RestartDownloadCommand = new(video => video.RaiseCancel(true), video => video != null && video.DownloadState == DownloadState.IsLoading);
            DownloadFileCommand.PropertyChanged += (o, e) => UpdateProgressStateProperties();
            AddVideoCommand.PropertyChanged += (o, e) => UpdateProgressStateProperties();
            AddSearchedVideo.PropertyChanged += (o, e) => UpdateProgressStateProperties();
            Videos.CollectionChanged += (o, e) => UpdateProgressStateProperties();
            windowClosingService.Closed += (o, e) => CancelAll();
            networkAvailable = this.networkStatusService?.IsNetworkConnectionAvailable() ?? true;
            if (this.networkStatusService != null)
                this.networkStatusService.ConnectionStateChanged += (o, e) => this.dispatcher.Enqueue(() => NetworkAvailable = e);
        }

        private readonly IDialogService dialogService;
        private readonly IDownloaderDialogService downloaderDialogService;
        private readonly IFiletagEditorDialog filetagDialogService;
        private readonly IDispatcherService dispatcher;
        private readonly INetworkStatusService networkStatusService;
        private readonly IToastNotificationService toastNotificationService;
        private readonly IPathProvider pathProvider;
        private readonly ITaskbarProgressService taskbarProgressService;
        private static readonly IUrlService urlService = IoC.Default.GetService<IUrlService>() ?? throw new ArgumentNullException();

        public static AsyncRelayCommand<string> OpenUrlCommand { get; } = new(async url => await urlService.OpenUrlAsync(url));
        public AsyncRelayCommand<string> DownloadFileCommand { get; }
        public AsyncRelayCommand<string> AddVideoCommand { get; }
        public AsyncRelayCommand<SearchItemModel> AddSearchedVideo { get; }
        public RelayCommand<StreamItemModel> RestartDownloadCommand { get; }
        public RelayCommand<int> RemoveCommand { get; }

        public event EventHandler<bool> DownloadDone;

        [ObservableProperty]
        [AlsoNotifyChangeFor(nameof(CanCancelOperation))]
        private bool isImporting;

        [ObservableProperty]
        private int importTotalLinks;

        [ObservableProperty]
        private int importProcessedLinks;

        public int ImportProgress => ImportTotalLinks > 0 ? (int)((double)ImportProcessedLinks / ImportTotalLinks * 100) : 0;

        public string ImportProgressText => $"Imported: {ImportProcessedLinks} / {ImportTotalLinks}";

        public string QueueStatusText
        {
            get
            {
                if (Videos.Any())
                {
                    return $"{Videos.Count(v => v.Success)} / {Videos.Count}";
                }
                return "0 / 0";
            }
        }

        [ObservableProperty]
        [AlsoNotifyChangeFor(nameof(ResolutionsAvailable))]
        private DownloadMode selectedDownloadMode = DownloadMode.Video;

        [ObservableProperty]
        private bool validUrl;

        [ObservableProperty]
        private bool videoNotFound;

        [ObservableProperty]
        private bool networkAvailable;

        public bool ResolutionsAvailable => SelectedDownloadMode != DownloadMode.Audio && Videos.Any() && Resolutions.Any();

        public bool VideoFetchingLogAvailable => VideoFetchingErrors.Any();

		public string SearchTerm
        {
            get => searchTerm;
            set
            {
                if (!SetProperty(ref searchTerm, value)) return;
                VideoNotFound = false;
            }
        }
        private string searchTerm = string.Empty;
        public ObservableCollection<SearchItemModel> SearchResults { get; } = new();
        public ObservableCollection<string[]> VideoFetchingErrors { get; } = new();
        public ObservableCollection<StreamItemModel> Videos { get; set; } = new();
        public ObservableCollection<string> Resolutions { get; set; } = new();

        private StreamItemModel selectedVideo;
        public StreamItemModel SelectedVideo
        {
            get => selectedVideo;
            set
            {
                if (selectedVideo != value && value != null)
                {
                    selectedVideo = value;
                    OnPropertyChanged();
                }
            }
        }

        private string previouslySelected;
        private string selectedQuality;

        public string SelectedQuality
        {
            get => selectedQuality;
            set
            {
                if (selectedQuality == value) return;
                
                selectedQuality = value;
                if (!string.IsNullOrEmpty(selectedQuality))
                {
                    previouslySelected = selectedQuality;
                }
                
                OnPropertyChanged();
            }
        }

        [ICommand]
        private void SetDownloadMode(string mode)
        {
            if (Enum.TryParse<DownloadMode>(mode, out var result))
            {
                SelectedDownloadMode = result;
            }
        }

        private void OnSelectedDownloadModeChanged(DownloadMode value)
        {
            if (Resolutions == null) return;

            if (value == DownloadMode.Gif && ResolutionsAvailable)
            {
                var res720 = Resolutions.FirstOrDefault(r => r != null && r.StartsWith("720p"));
                if (res720 != null)
                {
                    SelectedQuality = res720;
                }
            }
        }

        private void UpdateResolutions()
        {
            Resolutions = new ObservableCollection<string>(DownloaderMethods.GetResolutions(Videos));
            OnPropertyChanged(nameof(Resolutions));
            OnPropertyChanged(nameof(ResolutionsAvailable));

            //TODO Filter videos without QualityLabels
            if (!string.IsNullOrEmpty(previouslySelected))
                SelectedQuality = previouslySelected;
            else if (SelectedDownloadMode == DownloadMode.Gif && Resolutions.Any())
            {
                var res720 = Resolutions.FirstOrDefault(r => r.StartsWith("720p"));
                SelectedQuality = res720 ?? Resolutions[0];
            }
            else if (Resolutions.Any())
                SelectedQuality = Resolutions[0];
            else
                SelectedQuality = null;

            // Ensure selection if resolutions exist but nothing is selected
            if (Resolutions.Any() && string.IsNullOrEmpty(SelectedQuality))
            {
                SelectedQuality = Resolutions[0];
            }
        }

        //Search a video or get a video from a url and add it to the queue.
        private async Task FillInfosAsync(string videolink, bool allowPlaylists = true)
        {
            if (VideoNotFound)
            {
                VideoNotFound = false;
                VideoNotFound = true;
                return;
            }

            string urlClone = (string)(videolink?.Clone() ?? string.Empty);

            //Cancel searching and clear results
            if (searchProcesses > 0 && !DownloaderMethods.VideoSearchCancelSource.IsCancellationRequested)
                DownloaderMethods.VideoSearchCancelSource.Cancel();
            SearchResults.Clear();
            VideoFetchingErrors.Clear();


			if (string.IsNullOrWhiteSpace(videolink))
                return;

            bool validUri = Regex.IsMatch(videolink, GlobalResources.URLREGEX);
            bool isYoutubePlaylist = validUri && allowPlaylists && IsYoutubePlaylist(urlClone);

            //Remove the "feature=share" from shared yt-shorts urls.
            if (videolink.Contains("youtube.com/shorts/"))
                videolink = videolink.Replace("?feature=share", string.Empty);

            try
            {
                if (!validUri)
                {
                    await RefreshResultsAsync(videolink.Clone() as string);
                    return;
                }

                if (isYoutubePlaylist && (AppSettings.Instance.VideoAddMode is VideoAddMode.AddPlaylist || AppSettings.Instance.VideoAddMode is VideoAddMode.AskForVideoAddMode && await AskForPlaylistAsync()))
                {
                    ScanVideoCount++;
                    try
                    {
                        var videos = await DownloaderMethods.GetVideosFromPlaylistAsync(urlClone);

                        var urls = (await downloaderDialogService.ShowPlaylistSelectorDialogAsync(videos)).Select(v => v.Url);
                        var videosToAdd = await GetVideosAsync(urls);
                        var sortedVideos = videosToAdd.OrderBy(video => videos.IndexOf(v => v.Url == video.Video.Url));

                        AddVideos(sortedVideos);
                        if (Videos.Any())
                            SelectedVideo = Videos[^1];
                        return;
                    }
                    finally
                    {
                        ScanVideoCount--;
                    }
                }

                ScanVideoCount++;
                var data = await DownloaderMethods.downloadClient.RunVideoDataFetch(videolink);

                // Handling for GIF/Direct downloads when yt-dlp fails to extract "video" metadata
				if (!data.Success && SelectedDownloadMode == DownloadMode.Gif)
				{
                     // If the user selected GIF mode and yt-dlp failed, we assume they are trying to download a direct GIF file or similar asset.
                     // We trust the user's intent and attempt to download the URL directly as a file.
                     
                     var dummyData = new VideoData
                     {
                         Title = "GIF Animation",
                         Url = videolink,
                         Formats = new [] { 
                             new FormatData { Extension = "gif", Url = videolink } 
                         }
                     };
                     
                     // Recreate RunResult with success = true
                     data = new RunResult<VideoData>(true, new string[0], dummyData);
				}

				if (!data.Success)
				{
					VideoFetchingErrors.Add(data.ErrorOutput);
				}
				if (data.Data == null && urlClone == SearchTerm)
                {
                    VideoNotFound = true;
                    ScanVideoCount--;
                    return;
                }

                var video = new StreamItemModel(data);
                video.Video.Url = videolink;

                if (Videos.Any(v => v.Video.ID == video.Video.ID))
                {
                    ScanVideoCount--;
                    return;
                }

                lock (Videos)
                    Videos.Add(video);

                video.ProgressChangedEventHandler += OnProgressChanged;
                SelectedVideo = video;

                if (urlClone == SearchTerm)
                    SearchTerm = string.Empty;

                OnPropertyChanged(nameof(QueueStatusText));
                UpdateResolutions();

                ScanVideoCount--;
                OnPropertyChanged(nameof(QueueIsEmpty));
                OnPropertyChanged(nameof(QueueIsNotEmpty));
                OnPropertyChanged(nameof(MultipleVideos));
            }
            catch (Exception ex)
            {
                ScanVideoCount--;
                switch (ex)
                {
                    case InvalidOperationException:
                        Debug.WriteLine("InvalidOperation triggered");
                        break;

                    case VideoUnavailableException:
                        await RefreshResultsAsync(videolink);
                        break;

                    case YoutubeExplodeException:
                        Debug.WriteLine("Video error");
                        break;

                    //TODO: Check what is that piece of code doing?! (i cant remember)
                    case ArgumentOutOfRangeException:
                        throw new ArgumentOutOfRangeException("This bug should be fixed...", ex);

                    case ArgumentNullException:
                        SearchResults.Clear();
                        break;

                    case NotSupportedException:
                        await dialogService.ShowInfoDialogAsync("livestreamDlgTitle".GetLocalized(), "livestreamDlgContent".GetLocalized(), "OK");
                        break;

                    case HttpRequestException:
                        Debug.WriteLine("No internet connection!");
                        break;
                }
            }
        }

        [ICommand]
        private async Task CopyUrlAsync(string url)
        {
	        await ClipboardService.SetTextAsync(url);
        }

        [ICommand]
        private async Task ShowVideoFetchingLogAsync()
        {
            if (!VideoFetchingErrors.Any())
                return;

            string lastLog = string.Join('\n', VideoFetchingErrors.Last());
            
            bool? storeAsFile = await dialogService.ShowInteractionDialogAsync("Log:", lastLog, "save".GetLocalized(), "copy".GetLocalized(), "close".GetLocalized());
            if (storeAsFile is null)
            {
                return;
            }
            if (storeAsFile is false)
            {
                await ClipboardService.SetTextAsync(lastLog);
                return;
            }
            Dictionary<string, IEnumerable<string>> types = new()
            {
                { "", new[] { ".txt" } }
            };
			string filepath = await dialogService.ShowSaveFilePickerDialogAsync("video-fetching-log.txt", types, DirectoryLocation.Desktop);
			if (filepath != null)
			{
				await File.WriteAllTextAsync(filepath, lastLog);
			}
		}

		[ICommand]
        private async Task ShowLogAsync(StreamItemModel video)
        {
	        if (string.IsNullOrEmpty(video.DownloadLog))
	        {
		        await dialogService.ShowInfoDialogAsync(video.Video.Title, "emptyLog".GetLocalized(), "close".GetLocalized());
		        return;
	        }

	        bool? storeAsFile = await dialogService.ShowInteractionDialogAsync(video.Video.Title, video.DownloadLog, "save".GetLocalized(), "copy".GetLocalized(), "close".GetLocalized());
	        if (storeAsFile is null)
	        {
		        return;
	        }
	        if (storeAsFile is false)
	        {
		        await ClipboardService.SetTextAsync(video.DownloadLog);
		        return;
	        }

	        Dictionary<string, IEnumerable<string>> types = new()
	        {
		        { "", new[] { ".txt" } }
	        };
			string filepath = await dialogService.ShowSaveFilePickerDialogAsync("log.txt", types, DirectoryLocation.Documents);
	        if (filepath != null)
	        {
		        await File.WriteAllTextAsync(filepath, video.DownloadLog);
	        }
        }

        [ICommand]
        private async Task DownloadThumbnailAsync(StreamItemModel video)
        {
	        string tempPath = Path.GetTempFileName();

	        var types = new Dictionary<string, IEnumerable<string>>()
	        {
		        { "Portable Network Graphics", new[] { ".png" } },
		        { "JPEG", new[] { ".jpg", ".jpeg" } }
			};
	        string filepath = await dialogService.ShowSaveFilePickerDialogAsync(video.Video.Title.TrimToFilename(int.MaxValue), types, DirectoryLocation.Pictures);
	        if (filepath is null)
	        {
		        return;
	        }

	        string format = Path.GetExtension(filepath) switch
	        {
		        ".jpg" => "jpg",
		        ".jpeg" => "jpg",
		        _ => "png"
	        };

	        await DownloaderMethods.DownloadThumbnailAsync(video.Video.Url, tempPath, format);
            File.Move(tempPath, filepath, true);
        }

        [ICommand]
        private async Task EditTagsAsync(StreamItemModel video)
        {
	        FileTags tags = video.CustomTags ?? new()
	        {
		        Title = video.Video.Title,
		        Description = video.Video.Description,
		        Artist = video.Video.Uploader,
		        Year = video.Video.UploadDate.HasValue ? (uint)video.Video.UploadDate.Value.Year : 0,
	        };
	        var finalTags = await filetagDialogService.ShowTagEditorDialogAsync(tags);
	        if (finalTags is null)
	        {
		        return;
	        }

            video.CustomTags = finalTags;
            if (video.DownloadState == DownloadState.IsDone && File.Exists(video.Path.OriginalString))
            {
                DownloaderMethods.SaveTags(video.Path.OriginalString, finalTags);
            }
        }

		[ICommand]
        private async Task LoadFromFileAsync()
        {
            if (IsImporting) return;
            var path = await dialogService.ShowSingleFilePickerDialogAsync(DirectoryLocation.Documents);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            var lines = await File.ReadAllLinesAsync(path);
            await ProcessLinksAsync(lines);
        }

        [ICommand]
        private async Task PasteFromClipboardAsync()
        {
            if (IsImporting) return;
            var text = await ClipboardService.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
                return;

            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            await ProcessLinksAsync(lines);
        }

        private CancellationTokenSource importCancellationTokenSource;

        [ICommand]
        private void StopImport()
        {
            if (importCancellationTokenSource != null && !importCancellationTokenSource.IsCancellationRequested)
            {
                importCancellationTokenSource.Cancel();
                IsImporting = false; // Immediately update UI
            }
        }

        private async Task ProcessLinksAsync(IEnumerable<string> links)
        {
            if (IsImporting) return;

            // Pre-filter: Valid URL format AND not already in queue
            var validLinks = links
                .Where(l => !string.IsNullOrWhiteSpace(l) && Regex.IsMatch(l.Trim(), GlobalResources.URLREGEX))
                .Select(l => l.Trim())
                .Distinct()
                .Where(url => !Videos.Any(v => v.Video.Url == url)) // Check for existing
                .ToList();

            if (!validLinks.Any())
            {
                await dialogService.ShowInfoDialogAsync("Error", "No new valid links found.", "OK");
                return;
            }

            IsImporting = true;
            ScanVideoCount++;
            importCancellationTokenSource = new CancellationTokenSource();
            var token = importCancellationTokenSource.Token;

            // Initialize total links BEFORE starting the loop
            ImportTotalLinks = validLinks.Count;
            ImportProcessedLinks = 0;
            OnPropertyChanged(nameof(ImportProgress));
            OnPropertyChanged(nameof(ImportProgressText));

            int addedCount = 0;
            int skippedCount = 0;
            List<(string Url, string Error)> errors = new();

            try
            {
                using SemaphoreSlim semaphore = new(10); 
                List<Task> tasks = new();

                foreach (var url in validLinks)
                {
                    if (token.IsCancellationRequested) break;

                    await semaphore.WaitAsync(token);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            if (token.IsCancellationRequested) return;

                            RunResult<VideoData> data = null;
                            // Retry logic: 3 attempts
                            for (int i = 0; i < 3; i++)
                            {
                                if (token.IsCancellationRequested) break;
                                data = await DownloaderMethods.downloadClient.RunVideoDataFetch(url);
                                if (data.Success) break;
                                try { await Task.Delay(500 * (i + 1), token); } catch (OperationCanceledException) { break; }
                            }

                            if (token.IsCancellationRequested) return;

                            if (data != null && data.Success)
                            {
                                var video = new StreamItemModel(data);
                                video.Video.Url = url;

                                bool duplicate = false;
                                dispatcher.Enqueue(() =>
                                {
                                    if (Videos.Any(v => v.Video.ID == video.Video.ID))
                                    {
                                        duplicate = true;
                                    }
                                    else
                                    {
                                        Videos.Add(video);
                                        OnPropertyChanged(nameof(QueueStatusText));
                                        video.ProgressChangedEventHandler += OnProgressChanged;
                                        Interlocked.Increment(ref addedCount);
                                    }
                                });
                                
                                if(duplicate) Interlocked.Increment(ref skippedCount);
                            }
                            else
                            {
                                Interlocked.Increment(ref skippedCount);
                                string errorMsg = data != null ? string.Join(", ", data.ErrorOutput) : "Unknown error";
                                lock(errors) errors.Add((url, errorMsg));
                            }
                        }
                        catch (Exception ex)
                        {
                            if (!token.IsCancellationRequested)
                            {
                                Interlocked.Increment(ref skippedCount);
                                lock(errors) errors.Add((url, ex.Message));
                            }
                        }
                        finally
                        {
                            Interlocked.Increment(ref importProcessedLinks);
                            dispatcher.Enqueue(() =>
                            {
                                OnPropertyChanged(nameof(ImportProcessedLinks));
                                OnPropertyChanged(nameof(ImportProgress));
                                OnPropertyChanged(nameof(ImportProgressText));
                            });
                            semaphore.Release();
                        }
                    }, token));
                }

                try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
                
                string message = $"Added: {addedCount}\nSkipped: {skippedCount}";
                if (token.IsCancellationRequested) message += "\n(Import stopped)";

                bool hasErrors = errors.Any();
                
                if (hasErrors)
                {
                    message += $"\n\nErrors ({errors.Count}):\n{string.Join("\n", errors.Select(e => $"{e.Url}: {e.Error}").Take(5))}";
                    if (errors.Count > 5) message += "\n...";
                }
                
                if (hasErrors)
                {
                    bool? result = await dialogService.ShowInteractionDialogAsync("Import Complete", message, "OK", "Copy Failed URLs", null);
                    if (result == false) // "Copy Failed URLs"
                    {
                        await ClipboardService.SetTextAsync(string.Join(Environment.NewLine, errors.Select(e => e.Url)));
                    }
                }
                else
                {
                    await dialogService.ShowInfoDialogAsync("Import Complete", message, "OK");
                }

                // Update resolutions after mass import
                Resolutions = new ObservableCollection<string>(DownloaderMethods.GetResolutions(Videos));
                OnPropertyChanged(nameof(Resolutions));
                OnPropertyChanged(nameof(ResolutionsAvailable));
                
                // FORCE selection if empty
                if (Resolutions.Any() && (string.IsNullOrEmpty(SelectedQuality) || !Resolutions.Contains(SelectedQuality)))
                {
                    SelectedQuality = Resolutions[0];
                }

                // If resolutions are still empty, try to fetch them again for the selected video if exists
                if (!Resolutions.Any() && SelectedVideo != null)
                {
                    var singleRes = DownloaderMethods.GetResolutions(new[] { SelectedVideo });
                    if (singleRes.Any())
                    {
                        Resolutions = new ObservableCollection<string>(singleRes);
                        OnPropertyChanged(nameof(Resolutions));
                        OnPropertyChanged(nameof(ResolutionsAvailable));
                        SelectedQuality = Resolutions[0];
                    }
                }

                // Fallback: If still empty but we have videos, assume highest quality and force update
                if (!Resolutions.Any() && Videos.Any())
                {
                    // Trigger a re-evaluation of resolutions from the first video
                    var firstVideo = Videos.FirstOrDefault();
                    if (firstVideo != null)
                    {
                        var res = DownloaderMethods.GetResolutions(new[] { firstVideo });
                        if (res.Any())
                        {
                            Resolutions = new ObservableCollection<string>(res);
                            OnPropertyChanged(nameof(Resolutions));
                            OnPropertyChanged(nameof(ResolutionsAvailable));
                            SelectedQuality = Resolutions[0];
                        }
                    }
                }

                OnPropertyChanged(nameof(QueueStatusText));
                OnPropertyChanged(nameof(QueueIsEmpty));
                OnPropertyChanged(nameof(QueueIsNotEmpty));
            }
            catch (OperationCanceledException) { }
            finally
            {
                ScanVideoCount--;
                IsImporting = false;
                importCancellationTokenSource?.Dispose();
                importCancellationTokenSource = null;
                
                ImportTotalLinks = 0;
                ImportProcessedLinks = 0;
                OnPropertyChanged(nameof(ImportProgress));
                OnPropertyChanged(nameof(ImportProgressText));
            }
        }

        private void AddVideos(IEnumerable<StreamItemModel> videos)
        {
            if (videos == null) throw new ArgumentNullException(nameof(videos));
            if (!videos.Any()) return;

            ScanVideoCount++;
            try
            {
                lock (Videos)
                    Videos.AddRange(videos.Where(video => !Videos.Any(v => video.Video.ID == v.Video.ID)));

                OnPropertyChanged(nameof(QueueStatusText));

                foreach (var video in Videos)
                    video.ProgressChangedEventHandler += OnProgressChanged;

                Resolutions = new ObservableCollection<string>(DownloaderMethods.GetResolutions(Videos));
                OnPropertyChanged(nameof(Resolutions));
                OnPropertyChanged(nameof(ResolutionsAvailable));

                //TODO Filter videos without QualityLabels
                if (previouslySelected != null)
                    SelectedQuality = previouslySelected;
                else if (Resolutions.Any())
                    SelectedQuality = Resolutions[0];
                else
                    SelectedQuality = null;

                OnPropertyChanged(nameof(QueueIsEmpty));
                OnPropertyChanged(nameof(QueueIsNotEmpty));
                OnPropertyChanged(nameof(MultipleVideos));
            }
            catch (InvalidOperationException)
            {
                Debug.WriteLine("InvalidOperation triggered");
            }
            finally
            {
                ScanVideoCount--;
            }
        }

        private async Task<IEnumerable<StreamItemModel>> GetVideosAsync(IEnumerable<string> urls, CancellationToken cToken = default)
        {
            List<StreamItemModel> items = new();

            List<Task> tasks = new();
            SemaphoreSlim queue = new(20, 20);
            foreach (var url in urls)
            {
                await queue.WaitAsync(cToken);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var video = await DownloaderMethods.downloadClient.RunVideoDataFetch(url, ct: cToken);

                        // Video fetch logging for playlists
                        if (!video.Success)
                        {
                            dispatcher.Enqueue(() => VideoFetchingErrors.Add(video.ErrorOutput));
                        }

                        StreamItemModel item = new(video);
                        item.Video.Url = url;
                        lock (items)
                            items.Add(item);
                    }
                    catch (Exception ex) { Debug.WriteLine(ex.Message); }
                }, cToken).ContinueWith(o => queue.Release()));
            }

            await Task.WhenAll(tasks);
            return items;
        }

        private static bool IsYoutubePlaylist(string url)
        {
            if (url.IsNullOrWhiteSpace()) return false;
            return url.Contains("youtu") && url.Contains("list=");
        }

        private async Task<bool> AskForPlaylistAsync()
        {
            return await dialogService.ShowInteractionDialogAsync("askForPlaylistDownloadTitle".GetLocalized(), "askForPlaylistDownloadContent".GetLocalized(), "playlist".GetLocalized(), "video".GetLocalized(), null) == true;
        }

        private void OnProgressChanged(object sender, EventArgs e)
        {
            OnPropertyChanged(nameof(ResolutionsAvailable));
            OnPropertyChanged(nameof(DownloadProgress));

            if (canceledAll || DownloadProgress == 100)
            {
                taskbarProgressService.UpdateProgress(typeof(YouTubeDownloaderViewModel), 0);
                taskbarProgressService.UpdateState(typeof(YouTubeDownloaderViewModel), ProgressBarState.None);
                return;
            }
            taskbarProgressService.UpdateProgress(typeof(YouTubeDownloaderViewModel), DownloadProgress);
        }

        private async Task RemoveVideoAsync()
        {
            // If no video is selected, try to select the last one to remove it (as requested behavior)
            if (SelectedVideo == null && Videos.Any())
            {
                SelectedVideo = Videos.Last();
            }

            if (SelectedVideo == null) return;

            try
            {
                if (SelectedVideo.DownloadState == DownloadState.IsLoading)
                    SelectedVideo.RaiseCancel();

                if (Videos.Count <= 1)
                    Videos.Clear();
                else
                    Videos.Remove(SelectedVideo);

                OnPropertyChanged(nameof(QueueIsEmpty));
                OnPropertyChanged(nameof(QueueIsNotEmpty));
                OnPropertyChanged(nameof(MultipleVideos));

                if (Videos.Any())
                    SelectedVideo = Videos[^1];

                Resolutions = new ObservableCollection<string>(DownloaderMethods.GetResolutions(Videos));
                OnPropertyChanged(nameof(Resolutions));
                OnPropertyChanged(nameof(ResolutionsAvailable));

                if (previouslySelected != null && Resolutions.Contains(previouslySelected))
                    SelectedQuality = previouslySelected;
                else if (Resolutions.Any())
                    SelectedQuality = Resolutions[0];
                else
                    SelectedQuality = null;
            }
            catch (InvalidOperationException) { Debug.WriteLine("InvalidOperation triggered"); }
            await Task.CompletedTask;
        }

        private bool canceledAll = false;
        private async Task DownloadVideosAsync(IList<StreamItemModel> videos, DownloadMode downloadMode, string qualityLabel)
        {
            if (videos == null || !videos.Any())
                throw new ArgumentException("videos is null or empty.");

            string path = null;
            if (!AppSettings.Instance.UseFixedStoragePaths)
            {
                path = await dialogService.ShowFolderPickerDialogAsync(DirectoryLocation.Videos);
                if (path == null) return;
            }

            canceledAll = false;
            taskbarProgressService?.SetType(typeof(YouTubeDownloaderViewModel));
            VideoNotFound = false;
            int finishedCount = 0;
            uint unauthorizedAccessExceptions = 0;
            uint directoryNotFoundExceptions = 0;
            uint notEnoughSpaceExceptions = 0;

            List<Task> tasks = new();
            SemaphoreSlim queue = new(AppSettings.Instance.SimultaneousOperationCount, AppSettings.Instance.SimultaneousOperationCount);
            StreamItemModel[] items = videos.ToArray();
            StreamItemModel loadedVideo = null;
            
            CanceledAll += (o, e) => canceledAll = true;
            items.Where(i => i != null && videos.Contains(i)).ForEach(i => i.SetProgressToDefault());
            items.ForEach(v => v.QualityLabel = qualityLabel);
            
            List<StreamItemModel> failedVideos = new();

            foreach (var video in items)
            {
                if (canceledAll || !videos.Contains(video) || video.DownloadState == DownloadState.IsCancelled) continue;
                await queue.WaitAsync();

                if (canceledAll || !videos.Contains(video) || video.DownloadState == DownloadState.IsCancelled) continue;

                video.FinishedEventHandler += (o, e) =>
                {
                    loadedVideo = (StreamItemModel)o;
                    Interlocked.Increment(ref finishedCount);
                    OnPropertyChanged(nameof(QueueStatusText));
                };

                tasks.Add(DownloaderMethods.DownloadStreamAsync(video, downloadMode, path).ContinueWith(t =>
                {
                    queue.Release();
                    if (t.Exception?.InnerException == null) return;
                    
                    // Track failure
                    lock(failedVideos) failedVideos.Add(video);

                    switch (t.Exception?.InnerException)
                    {
                        default:
                            Debug.WriteLine("Exception occured while saving the file.");
                            break;

                        case UnauthorizedAccessException:
                            unauthorizedAccessExceptions++;
                            break;

                        case DirectoryNotFoundException:
                            directoryNotFoundExceptions++;
                            break;

                        case NotEnoughSpaceException:
                            notEnoughSpaceExceptions++;
                            break;
                    }
                }));
            }
            await Task.WhenAll(tasks);

            //Remove downloaded videos from list
            if (AppSettings.Instance.ClearListsAfterOperation)
                items.ForEach(v => videos.Remove(v), v => videos.Contains(v) && v.Success);

            Debug.WriteLine("Downloadtask is done.");

            foreach (var dir in Directory.GetDirectories(pathProvider.DownloaderTempdir))
            {
                try { Directory.Delete(dir, true); }
                catch { /* Dont crash if a directory cant be deleted */ }
            }

            try
            {
	            if (unauthorizedAccessExceptions + directoryNotFoundExceptions + notEnoughSpaceExceptions > 0)
	            {
		            taskbarProgressService?.UpdateState(typeof(YouTubeDownloaderViewModel), ProgressBarState.Error);
		            await GlobalResources.DisplayFileSaveErrorDialog(unauthorizedAccessExceptions,
			            directoryNotFoundExceptions, notEnoughSpaceExceptions);
	            }
                
                // Post-download summary for failed videos
                if (failedVideos.Any() && !canceledAll)
                {
                    string failedMsg = $"{failedVideos.Count} video(s) failed to download.";
                    bool showDialog = true;
                    while (showDialog)
                    {
                        bool? result = await dialogService.ShowInteractionDialogAsync("Download Completed with Errors", failedMsg, "Retry Failed", "Copy Failed Links", "Close");
                        
                        if (result == true) // Retry
                        {
                             showDialog = false;
                             failedVideos.ForEach(v => v.SetProgressToDefault());
                             await DownloadVideosAsync(failedVideos, downloadMode, qualityLabel);
                        }
                        else if (result == false) // Copy
                        {
                             string failedUrls = string.Join(Environment.NewLine, failedVideos.Select(v => v.Video.Url));
                             await ClipboardService.SetTextAsync(failedUrls);
                             // Loop continues, dialog re-appears
                        }
                        else // Close
                        {
                             showDialog = false;
                        }
                    }
                }

	            taskbarProgressService?.UpdateState(typeof(YouTubeDownloaderViewModel), ProgressBarState.None);

	            if (!AppSettings.Instance.SendMessageAfterDownload)
	            {
		            return;
	            }

	            if (finishedCount == 1)
	            {
		            loadedVideo.ShowToast();
	            }
	            else if (finishedCount > 1)
	            {
		            IEnumerable<string> filenames = null;

		            if (items.Any(v => v?.Path != null && Path.GetDirectoryName(v.Path.OriginalString) ==
			                Path.GetDirectoryName(loadedVideo.Path.OriginalString)))
		            {
			            filenames = items
				            .Where(v => v?.Path != null && v.DownloadState is DownloadState.IsDone &&
				                        File.Exists(v.Path.OriginalString))
				            .Select(v => Path.GetFileName(v.Path.OriginalString));
		            }

		            toastNotificationService.SendDownloadsDoneNotification(loadedVideo.Path.OriginalString,
			            (uint)finishedCount, filenames);
	            }
            }
            finally
            {
	            if (!(canceledAll || items.All(v => v.DownloadState == DownloadState.IsCancelled)))
				{
					bool errors = items.Any(v => videos.Contains(v) && v.Failed);
					DownloadDone?.Invoke(this, errors);
				}
            }
        }

        private (string query, ICollection<SearchItemModel> results) lastSearch = (string.Empty, new Collection<SearchItemModel>());
        private async Task RefreshResultsAsync(string searchTerm)
        {
            if (searchTerm.Equals(lastSearch.query) && lastSearch.results.Any())
            {
                SearchResults.Replace(lastSearch.results);
                return;
            }

            searchProcesses++;
            try
            {
                SearchResults.Clear();
                await foreach (var result in DownloaderMethods.GetSearchResultsAsync(searchTerm))
                    SearchResults.Add(result);
                lastSearch.query = searchTerm;
                lastSearch.results.Replace(SearchResults);
            }
            catch (HttpRequestException)
            {
                Debug.WriteLine("No internet connection!");
            }
            catch (TaskCanceledException) { }
            finally { searchProcesses--; }
        }
        private int searchProcesses = 0;

        [ICommand]
        private void ClearResults()
        {
            if (!DownloaderMethods.VideoSearchCancelSource.IsCancellationRequested)
                DownloaderMethods.VideoSearchCancelSource.Cancel();

            VideoNotFound = false;
            if (!SearchResults.Any()) return;
            SearchResults.Clear();
            lastSearch = (string.Empty, new Collection<SearchItemModel>());
        }

        [ICommand]
        private void CancelAll()
        {
            if (IsImporting)
            {
                StopImport();
            }

            Videos.ForEach(v => v?.RaiseCancel());
            CanceledAll?.Invoke(this, EventArgs.Empty);
        }
        public event EventHandler CanceledAll;

        [ICommand]
        private void RemoveAll()
        {
            CancelAll();
            Videos.Clear();
            // Reset Resolutions
            Resolutions.Clear();
            SelectedQuality = null;
            
            OnPropertyChanged(nameof(QueueStatusText));
            OnPropertyChanged(nameof(QueueIsEmpty));
            OnPropertyChanged(nameof(QueueIsNotEmpty));
            OnPropertyChanged(nameof(ResolutionsAvailable));
        }

        public int DownloadProgress
        {
            get
            {
                if (Videos.Any() && Videos.All(v => v.ProgressInfo.DownloadState == YoutubeDLSharp.DownloadState.Success))
                    return 100;

                double progress = 0;
                int videocount = Videos.Count;

                //Get progress from all videos
                foreach (var video in Videos)
                    progress += video.ProgressInfo.Progress;

                if (videocount == 0)
                    return 0;

                return (int)(progress / videocount);
            }
        }

        /// <summary>
        /// The number of videos that get scanned in the moment.
        /// </summary>
        [ObservableProperty]
        [AlsoNotifyChangeFor(nameof(ReadyToDownload))]
        private int scanVideoCount;

        public bool QueueIsEmpty => !Videos.Any();
        public bool QueueIsNotEmpty => Videos.Any() && !DownloadFileCommand.IsRunning;
        public bool ReadyToDownload => QueueIsNotEmpty && ScanVideoCount == 0;
        public bool AnyResults => SearchResults.Any();
        public bool AddingVideo => AddVideoCommand.IsRunning || AddSearchedVideo.IsRunning;
        public bool CanCancelOperation => DownloadFileCommand.IsRunning || IsImporting;

        private void UpdateProgressStateProperties()
        {
            OnPropertyChanged(nameof(AddingVideo));
            OnPropertyChanged(nameof(QueueIsEmpty));
            OnPropertyChanged(nameof(QueueIsNotEmpty));
            OnPropertyChanged(nameof(ReadyToDownload));
            OnPropertyChanged(nameof(QueueStatusText));
            OnPropertyChanged(nameof(CanCancelOperation));
        }

        public bool MultipleVideos => Videos.Count > 1;

        public bool SelectionIsInRange(IEnumerable<StreamItemModel> collection, int index, bool inverseResult = false)
        {
            if (!inverseResult)
                return index < collection.Count() && index >= 0 && collection.Any();
            return !(index < collection.Count() && index >= 0 && collection.Any());
        }
    }
}
