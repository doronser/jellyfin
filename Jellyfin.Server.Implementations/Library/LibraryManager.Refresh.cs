#pragma warning disable CS1591
#pragma warning disable CA5394

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BitFaster.Caching.Lru;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using Jellyfin.Naming.Common;
using Jellyfin.Naming.TV;
using Jellyfin.Naming.Video;
using Jellyfin.Server.Implementations.Library.Resolvers;
using Jellyfin.Server.Implementations.Playlists;
using Jellyfin.Server.Implementations.ScheduledTasks.Tasks;
using Jellyfin.Server.Implementations.Sorting;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Controller.Sorting;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using EpisodeInfo = Jellyfin.Naming.TV.EpisodeInfo;
using Genre = MediaBrowser.Controller.Entities.Genre;
using Person = MediaBrowser.Controller.Entities.Person;
using VideoResolver = Jellyfin.Naming.Video.VideoResolver;

namespace Jellyfin.Server.Implementations.Library;

/// <content>
/// Image and metadata refresh.
/// </content>
public partial class LibraryManager
{
    /// <inheritdoc />
    public async Task UpdateImagesAsync(BaseItem item, bool forceUpdate = false)
    {
        ArgumentNullException.ThrowIfNull(item);

        var outdated = forceUpdate
            ? item.ImageInfos.Where(i => i.Path is not null).ToArray()
            : item.ImageInfos.Where(ImageNeedsRefresh).ToArray();

        var parentItem = item.GetParent();
        var isLiveTvShow = item.SourceType != SourceType.Library &&
                           parentItem is not null &&
                           parentItem.SourceType != SourceType.Library; // not a channel

        // Skip image processing if current or live tv show
        if (outdated.Length == 0 || isLiveTvShow)
        {
            RegisterItem(item);
            return;
        }

        foreach (var img in outdated)
        {
            var image = img;
            if (!img.IsLocalFile)
            {
                try
                {
                    var index = item.GetImageIndex(img);
                    image = await ConvertImageToLocal(item, img, index, true).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    _logger.LogWarning("Cannot get image index for {ImagePath}", img.Path);
                    continue;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    _logger.LogWarning(ex, "Cannot fetch image from {ImagePath}", img.Path);
                    continue;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Cannot fetch image from {ImagePath}. Http status code: {HttpStatus}", img.Path, ex.StatusCode);
                    continue;
                }
            }

            if (string.IsNullOrEmpty(image.Path) || !File.Exists(image.Path))
            {
                _logger.LogWarning(
                    "{ImageType} image for {ItemName} ({ItemId}) not found at \"{ImagePath}\", source was {SourcePath}",
                    img.Type,
                    item.Name,
                    item.Id,
                    image.Path,
                    img.Path);
                continue;
            }

            ImageDimensions size;
            try
            {
                size = _imageProcessor.GetImageDimensions(item, image);
                image.Width = size.Width;
                image.Height = size.Height;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot get image dimensions for {ImagePath}", image.Path);
                size = default;
                image.Width = 0;
                image.Height = 0;
            }

            try
            {
                var blurhash = _imageProcessor.GetImageBlurHash(image.Path, size);
                image.BlurHash = blurhash;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot compute blurhash for {ImagePath}", image.Path);
                image.BlurHash = string.Empty;
            }

            try
            {
                var modifiedDate = _fileSystem.GetLastWriteTimeUtc(image.Path);
                image.DateModified = modifiedDate;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot update DateModified for {ImagePath}", image.Path);
            }
        }

        item.ValidateImages();

        await _persistenceService.SaveImagesAsync(item).ConfigureAwait(false);

        RegisterItem(item);
    }

    /// <inheritdoc />
    public async Task UpdateItemsAsync(IReadOnlyList<BaseItem> items, BaseItem parent, ItemUpdateType updateReason, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            // Parental controls filter on the stored score, so it has to match the ratings being saved.
            item.OnMetadataChanged();

            item.DateLastSaved = DateTime.UtcNow;
            await RunMetadataSavers(item, updateReason).ConfigureAwait(false);

            // Modify again, so saved value is after write time of externally saved metadata
            item.DateLastSaved = DateTime.UtcNow;
        }

        ForgetDroppedLocalAlternateVersions(items);

        // Resolve and add any local alternate version items that don't exist yet
        // This ensures they exist in the database when LinkedChildren are processed
        var allItems = new List<BaseItem>(items);
        var parentFolder = parent as Folder;
        var parentCollectionType = GetTopFolderContentType(parent);
        foreach (var item in items)
        {
            if (item is Video video && video.LocalAlternateVersions.Length > 0)
            {
                var videoType = video.GetType();
                foreach (var path in video.LocalAlternateVersions)
                {
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    // Use the primary video's type for ID calculation to ensure consistency
                    var altId = GetNewItemId(path, videoType);
                    if (GetItemById(altId) is null && !allItems.Any(i => i.Id.Equals(altId)))
                    {
                        // Alternate version doesn't exist, resolve and create it
                        // ensuring it has the same type as the primary video
                        var altVideo = ResolveAlternateVersion(path, videoType, parentFolder, parentCollectionType);
                        if (altVideo is not null)
                        {
                            altVideo.OwnerId = video.Id;
                            altVideo.SetPrimaryVersionId(video.Id);
                            altVideo.IsInMixedFolder = video.IsInMixedFolder;
                            // ResolveAlternateVersion only sees the alternate's primary file.
                            // If the alternate is itself a stack (e.g. 1080p part1 + part2),
                            // detect its parts from sibling files so its AdditionalParts persist.
                            SetAdditionalPartsFromStack(altVideo, path);
                            allItems.Add(altVideo);
                        }
                    }
                }
            }
        }

        _persistenceService.SaveItems(allItems, cancellationToken);

        foreach (var item in allItems)
        {
            if (!items.Contains(item))
            {
                RegisterItem(item);
            }
        }

        if (parent is Folder folder)
        {
            folder.Children = null;
            folder.UserData = null;
        }

        if (ItemUpdated is not null)
        {
            foreach (var item in items)
            {
                // With the live tv guide this just creates too much noise
                if (item.SourceType != SourceType.Library)
                {
                    continue;
                }

                try
                {
                    ItemUpdated(
                        this,
                        new ItemChangeEventArgs
                        {
                            Item = item,
                            Parent = parent,
                            UpdateReason = updateReason
                        });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in ItemUpdated event handler");
                }
            }
        }
    }

    public async Task RunMetadataSavers(BaseItem item, ItemUpdateType updateReason)
    {
        if (item.IsFileProtocol)
        {
            await ProviderManager.SaveMetadataAsync(item, updateReason).ConfigureAwait(false);
        }

        await UpdateImagesAsync(item, updateReason >= ItemUpdateType.ImageUpdate).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports the item removed.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="parent">The parent item.</param>
    public void ReportItemRemoved(BaseItem item, BaseItem parent)
    {
        if (ItemRemoved is not null)
        {
            try
            {
                ItemRemoved(
                    this,
                    new ItemChangeEventArgs
                    {
                        Item = item,
                        Parent = parent
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ItemRemoved event handler");
            }
        }
    }

    public async Task AddVirtualFolder(string name, CollectionTypeOptions? collectionType, LibraryOptions options, bool refreshLibrary)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        name = _fileSystem.GetValidFilename(name.Trim());

        var rootFolderPath = _configurationManager.ApplicationPaths.DefaultUserViewsPath;

        var existingNameCount = 1; // first numbered name will be 2
        var virtualFolderPath = Path.Combine(rootFolderPath, name);
        var originalName = name;
        while (Directory.Exists(virtualFolderPath))
        {
            existingNameCount++;
            name = originalName + existingNameCount;
            virtualFolderPath = Path.Combine(rootFolderPath, name);
        }

        var mediaPathInfos = options.PathInfos;
        if (mediaPathInfos is not null)
        {
            var invalidpath = mediaPathInfos.FirstOrDefault(i => !Directory.Exists(i.Path));
            if (invalidpath is not null)
            {
                throw new ArgumentException("The specified path does not exist: " + invalidpath.Path + ".");
            }
        }

        LibraryMonitor.Stop();

        try
        {
            Directory.CreateDirectory(virtualFolderPath);

            if (collectionType is not null)
            {
                var path = Path.Combine(virtualFolderPath, collectionType.ToString()!.ToLowerInvariant() + ".collection"); // Can't be null with legal values?

                FileHelper.CreateEmpty(path);
            }

            CollectionFolder.SaveLibraryOptions(virtualFolderPath, options);

            if (mediaPathInfos is not null)
            {
                foreach (var path in mediaPathInfos)
                {
                    AddMediaPathInternal(name, path, false);
                }
            }

            // The libraries root was listed before this folder existed, so drop that listing:
            // anything still reading it resolves the library set without the new folder.
            _directoryService.Invalidate(virtualFolderPath);
        }
        finally
        {
            await ValidateTopLibraryFolders(CancellationToken.None).ConfigureAwait(false);

            if (refreshLibrary)
            {
                _ = StartScanInBackground();
            }
            else
            {
                // Need to add a delay here or directory watchers may still pick up the changes
                await Task.Delay(1000).ConfigureAwait(false);
                LibraryMonitor.Start();
            }
        }
    }

    internal Task StartScanInBackground()
    {
        // An active scan already handles library structure changes, so this request can be dropped.
        if (IsScanRunning)
        {
            return Task.CompletedTask;
        }

        // Queue instead of restarting so a scan that starts after the check is allowed to finish.
        return Task.Run(QueueLibraryScan);
    }
}
