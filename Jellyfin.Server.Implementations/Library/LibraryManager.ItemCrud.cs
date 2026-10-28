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
/// Item creation, update and deletion.
/// </content>
public partial class LibraryManager
{
    public void DeleteItem(BaseItem item, DeleteOptions options)
    {
        DeleteItem(item, options, false);
    }

    public void DeleteItem(BaseItem item, DeleteOptions options, bool notifyParentItem)
    {
        ArgumentNullException.ThrowIfNull(item);

        var parent = item.GetOwner() ?? item.GetParent();

        DeleteItem(item, options, parent, notifyParentItem);
    }

    public void DeleteItemsUnsafeFast(IReadOnlyCollection<BaseItem> items, bool deleteSourceFiles = false)
    {
        if (items.Count == 0)
        {
            return;
        }

        var pathMaps = items.Select(e =>
            (Item: e,
            InternalPath: GetInternalMetadataPaths(e),
            DeletePaths: deleteSourceFiles ? e.GetDeletePaths() : [])).ToArray();

        foreach (var (item, internalPaths, pathsToDelete) in pathMaps)
        {
            foreach (var metadataPath in internalPaths)
            {
                if (!Directory.Exists(metadataPath))
                {
                    continue;
                }

                LogDeletingMetadataPath(item.GetType().Name, item.Name ?? "Unknown name", metadataPath, item.Id);

                try
                {
                    Directory.Delete(metadataPath, true);
                }
                catch (Exception ex)
                {
                    LogErrorDeletingMetadataPath(ex, metadataPath);
                }
            }

            foreach (var fileSystemInfo in pathsToDelete)
            {
                DeleteItemPath(item, false, fileSystemInfo);
            }
        }

        var externalDataManager = _externalDataManagerFactory.Value;
        foreach (var (item, _, _) in pathMaps)
        {
            externalDataManager.DeleteExternalItemFiles(item);
        }

        _persistenceService.DeleteItem([.. pathMaps.Select(f => f.Item.Id)]);

        // Evict the deleted items from the cache and announce each removal.
        foreach (var (item, _, _) in pathMaps)
        {
            _cache.TryRemove(item.Id, out _);
            ReportItemRemoved(item, item.GetOwner() ?? item.GetParent());
        }
    }

    public void DeleteItem(BaseItem item, DeleteOptions options, BaseItem parent, bool notifyParentItem)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.SourceType == SourceType.Channel)
        {
            if (options.DeleteFromExternalProvider)
            {
                try
                {
                    BaseItem.ChannelManager.DeleteItem(item).GetAwaiter().GetResult();
                }
                catch (ArgumentException)
                {
                    // channel no longer installed
                }
            }

            options.DeleteFileLocation = false;
        }

        if (item is LiveTvProgram)
        {
            LogRemovingItemDebug(item.GetType().Name, item.Name ?? "Unknown name", item.Path ?? string.Empty, item.Id);
        }
        else
        {
            LogRemovingItem(item.GetType().Name, item.Name ?? "Unknown name", item.Path ?? string.Empty, item.Id);
        }

        // If deleting a primary version video, clear PrimaryVersionId from alternate versions
        // OwnerId check: items with OwnerId set are alternate versions or extras, not primaries
        if (item is Video video && !video.PrimaryVersionId.HasValue && video.OwnerId.IsEmpty())
        {
            var localAlternateIds = GetLocalAlternateVersionIds(video).ToHashSet();
            var allAlternateVersions = localAlternateIds
                .Concat(GetLinkedAlternateVersions(video).Select(v => v.Id))
                .Distinct()
                .Select(id => GetItemById(id))
                .OfType<Video>()
                .ToList();

            // Partition alternates by whether their files still exist on disk
            var alternateVersions = new List<Video>();
            var missingAlternates = new List<Video>();
            foreach (var alt in allAlternateVersions)
            {
                if (!string.IsNullOrEmpty(alt.Path) && !_fileSystem.FileExists(alt.Path))
                {
                    missingAlternates.Add(alt);
                }
                else
                {
                    alternateVersions.Add(alt);
                }
            }

            // Delete alternates whose files no longer exist to avoid ghost items.
            // Clear PrimaryVersionId first so DeleteItem doesn't try to update the primary being deleted.
            foreach (var missing in missingAlternates)
            {
                LogDeletingMissingAlternateVersion(missing.Name ?? "Unknown name", missing.Path ?? string.Empty);
                missing.SetPrimaryVersionId(null);
                missing.OwnerId = Guid.Empty;
                missing.LocalAlternateVersions = [];
                missing.LinkedAlternateVersions = [];
                DeleteItem(missing, new DeleteOptions { DeleteFileLocation = false }, false);
            }

            if (alternateVersions.Count > 0)
            {
                LogClearingPrimaryVersionId(alternateVersions.Count, item.Name ?? "Unknown name");

                // Promote the first alternate version to be the new primary
                var newPrimary = alternateVersions[0];
                newPrimary.SetPrimaryVersionId(null);
                newPrimary.OwnerId = Guid.Empty;

                // Transfer alternate version arrays from old primary to new primary
                // so UpdateToRepositoryAsync creates correct LinkedChildren entries
                newPrimary.LocalAlternateVersions = video.LocalAlternateVersions
                    .Where(p => !string.Equals(p, newPrimary.Path, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                newPrimary.LinkedAlternateVersions = video.LinkedAlternateVersions
                    .Where(lc => !lc.ItemId.HasValue || !lc.ItemId.Value.Equals(newPrimary.Id))
                    .ToArray();

                newPrimary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();

                // Re-route playlist/collection references from deleted primary to new primary
                RerouteLinkedChildReferencesAsync(video.Id, newPrimary.Id).GetAwaiter().GetResult();

                // Update remaining alternates to point to new primary
                foreach (var alternate in alternateVersions.Skip(1))
                {
                    alternate.SetPrimaryVersionId(newPrimary.Id);
                    // Only set OwnerId for local alternates; linked alternates are independent items
                    alternate.OwnerId = localAlternateIds.Contains(alternate.Id) ? newPrimary.Id : Guid.Empty;
                    alternate.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
                }
            }
        }
        else if (item is Video alternateVideo && alternateVideo.PrimaryVersionId.HasValue)
        {
            // If deleting an alternate version, re-route references to its primary
            RerouteLinkedChildReferencesAsync(alternateVideo.Id, alternateVideo.PrimaryVersionId.Value).GetAwaiter().GetResult();

            // Remove deleted alternate from primary's LinkedAlternateVersions
            if (GetItemById(alternateVideo.PrimaryVersionId.Value) is Video primaryVideo)
            {
                primaryVideo.LinkedAlternateVersions = primaryVideo.LinkedAlternateVersions
                    .Where(lc => !lc.ItemId.HasValue || !lc.ItemId.Value.Equals(alternateVideo.Id))
                    .ToArray();
                primaryVideo.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        var children = item.IsFolder
            ? ((Folder)item).GetRecursiveChildren(false)
            : [];

        foreach (var metadataPath in GetMetadataPaths(item, children))
        {
            if (!Directory.Exists(metadataPath))
            {
                continue;
            }

            LogDeletingMetadataPath(item.GetType().Name, item.Name ?? "Unknown name", metadataPath, item.Id);

            try
            {
                Directory.Delete(metadataPath, true);
            }
            catch (Exception ex)
            {
                LogErrorDeletingMetadataPath(ex, metadataPath);
            }
        }

        if ((options.DeleteFileLocation && item.IsFileProtocol) || IsInternalItem(item))
        {
            // Assume only the first is required
            // Add this flag to GetDeletePaths if required in the future
            var isRequiredForDelete = true;

            foreach (var fileSystemInfo in item.GetDeletePaths())
            {
                DeleteItemPath(item, isRequiredForDelete, fileSystemInfo);

                isRequiredForDelete = false;
            }
        }

        item.SetParent(null);

        var externalDataManager = _externalDataManagerFactory.Value;
        externalDataManager.DeleteExternalItemFiles(item);
        foreach (var child in children)
        {
            externalDataManager.DeleteExternalItemFiles(child);
        }

        _persistenceService.DeleteItem([item.Id, .. children.Select(f => f.Id)]);
        _cache.TryRemove(item.Id, out _);
        foreach (var child in children)
        {
            _cache.TryRemove(child.Id, out _);
        }

        if (parent is Folder folder)
        {
            folder.Children = null;
            folder.UserData = null;
        }

        // Announce the descendants before the item itself.
        foreach (var child in children)
        {
            ReportItemRemoved(child, item);
        }

        ReportItemRemoved(item, parent);
    }

    private void DeleteItemPath(BaseItem item, bool isRequiredForDelete, FileSystemMetadata fileSystemInfo)
    {
        if (Directory.Exists(fileSystemInfo.FullName) || File.Exists(fileSystemInfo.FullName))
        {
            try
            {
                LogDeletingItemPath(item.GetType().Name, item.Name ?? "Unknown name", fileSystemInfo.FullName, item.Id);

                if (fileSystemInfo.IsDirectory)
                {
                    Directory.Delete(fileSystemInfo.FullName, true);
                }
                else
                {
                    File.Delete(fileSystemInfo.FullName);
                }
            }
            catch (DirectoryNotFoundException)
            {
                LogDirectoryNotFound(item.GetType().Name, item.Name ?? "Unknown name", fileSystemInfo.FullName, item.Id);
            }
            catch (FileNotFoundException)
            {
                LogFileNotFound(item.GetType().Name, item.Name ?? "Unknown name", fileSystemInfo.FullName, item.Id);
            }
            catch (IOException)
            {
                if (isRequiredForDelete)
                {
                    throw;
                }
            }
            catch (UnauthorizedAccessException)
            {
                if (isRequiredForDelete)
                {
                    throw;
                }
            }
        }
    }

    private bool IsInternalItem(BaseItem item)
    {
        if (!item.IsFileProtocol)
        {
            return false;
        }

        var pathToCheck = item switch
        {
            Genre => _configurationManager.ApplicationPaths.GenrePath,
            MusicArtist => _configurationManager.ApplicationPaths.ArtistsPath,
            MusicGenre => _configurationManager.ApplicationPaths.MusicGenrePath,
            Person => _configurationManager.ApplicationPaths.PeoplePath,
            Studio => _configurationManager.ApplicationPaths.StudioPath,
            Year => _configurationManager.ApplicationPaths.YearPath,
            _ => null
        };

        var itemPath = item.Path;
        if (!string.IsNullOrEmpty(pathToCheck) && !string.IsNullOrEmpty(itemPath))
        {
            var cleanPath = _fileSystem.GetValidFilename(itemPath);
            var cleanCheckPath = _fileSystem.GetValidFilename(pathToCheck);

            return cleanPath.StartsWith(cleanCheckPath, StringComparison.Ordinal);
        }

        return false;
    }

    private List<string> GetMetadataPaths(BaseItem item, IEnumerable<BaseItem> children)
    {
        var list = GetInternalMetadataPaths(item);
        foreach (var child in children)
        {
            list.AddRange(GetInternalMetadataPaths(child));
        }

        return list;
    }

    private List<string> GetInternalMetadataPaths(BaseItem item)
    {
        var list = new List<string>
        {
            item.GetInternalMetadataPath()
        };

        if (item is Video video)
        {
            // Trickplay
            list.Add(_pathManager.GetTrickplayDirectory(video));

            // Chapter Images
            list.Add(_pathManager.GetChapterImageFolderPath(video));

            // Subtitles and attachments
            foreach (var mediaSource in item.GetMediaSources(false))
            {
                var subtitleFolder = _pathManager.GetSubtitleFolderPath(mediaSource.Id);
                if (subtitleFolder is not null)
                {
                    list.Add(subtitleFolder);
                }

                var attachmentFolder = _pathManager.GetAttachmentFolderPath(mediaSource.Id);
                if (attachmentFolder is not null)
                {
                    list.Add(attachmentFolder);
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Resolves the item.
    /// </summary>
    /// <param name="args">The args.</param>
    /// <param name="resolvers">The resolvers.</param>
    /// <returns>BaseItem.</returns>
    private BaseItem? ResolveItem(ItemResolveArgs args, IItemResolver[]? resolvers)
    {
        var item = (resolvers ?? EntityResolvers).Select(r => Resolve(args, r))
            .FirstOrDefault(i => i is not null);

        if (item is not null)
        {
            ResolverHelper.SetInitialItemValues(item, args, _fileSystem, this);
        }

        return item;
    }

    /// <inheritdoc />
    public void CreateItems(IReadOnlyList<BaseItem> items, BaseItem? parent, CancellationToken cancellationToken)
    {
        // Resolve and add any local alternate version items that don't exist yet
        // This ensures they exist in the database when LinkedChildren are processed
        var allItems = new List<BaseItem>(items);
        var parentFolder = parent as Folder;
        var parentCollectionType = parent is not null ? GetTopFolderContentType(parent) : null;
        foreach (var item in items)
        {
            // Parental controls filter on the stored score, so it has to match the ratings being saved.
            item.OnMetadataChanged();

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
            RegisterItem(item);
        }

        if (parent is Folder folder)
        {
            folder.Children = null;
            folder.UserData = null;
        }

        if (ItemAdded is not null)
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
                    ItemAdded(
                        this,
                        new ItemChangeEventArgs
                        {
                            Item = item,
                            Parent = parent ?? item.GetParent()
                        });
                }
                catch (Exception ex)
                {
                    LogErrorInItemAddedHandler(ex);
                }
            }
        }
    }

    private bool ImageNeedsRefresh(ItemImageInfo image)
    {
        if (image.Path is not null && image.IsLocalFile)
        {
            if (image.Width == 0 || image.Height == 0 || string.IsNullOrEmpty(image.BlurHash))
            {
                return true;
            }

            try
            {
                return image.DateModified.Subtract(_fileSystem.GetLastWriteTimeUtc(image.Path)).Duration().TotalSeconds > 1;
            }
            catch (Exception ex)
            {
                LogCannotGetFileInfo(ex, image.Path);
                return false;
            }
        }

        return image.Path is not null && !image.IsLocalFile;
    }

    /// <inheritdoc />
    public Task UpdateItemAsync(BaseItem item, BaseItem parent, ItemUpdateType updateReason, CancellationToken cancellationToken)
        => UpdateItemsAsync([item], parent, updateReason, cancellationToken);

    /// <summary>
    /// Forgets the cached local alternate versions of the supplied items that they no longer list.
    /// </summary>
    /// <param name="items">The items about to be saved.</param>
    private void ForgetDroppedLocalAlternateVersions(IReadOnlyList<BaseItem> items)
    {
        foreach (var video in items.OfType<Video>())
        {
            var videoType = video.GetType();
            var keptIds = video.LocalAlternateVersions
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(path => GetNewItemId(path, videoType))
                .ToHashSet();

            foreach (var versionId in GetLocalAlternateVersionIds(video))
            {
                if (!keptIds.Contains(versionId))
                {
                    _cache.TryRemove(versionId, out _);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken)
    {
        await _persistenceService.ReattachUserDataAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves the item.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>BaseItem.</returns>
    public BaseItem RetrieveItem(Guid id)
    {
        return _itemRepository.RetrieveItem(id);
    }

    /// <inheritdoc />
    public QueryFiltersLegacy GetQueryFiltersLegacy(InternalItemsQuery query)
    {
        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        SetTopParentOrAncestorIds(query);
        return _itemRepository.GetQueryFiltersLegacy(query);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleting metadata path, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}")]
    private partial void LogDeletingMetadataPath(string type, string name, string path, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error deleting {MetadataPath}")]
    private partial void LogErrorDeletingMetadataPath(Exception ex, string metadataPath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Removing item, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}")]
    private partial void LogRemovingItemDebug(string type, string name, string path, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removing item, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}")]
    private partial void LogRemovingItem(string type, string name, string path, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleting missing alternate version {Name} ({Path})")]
    private partial void LogDeletingMissingAlternateVersion(string name, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Clearing PrimaryVersionId from {Count} alternate versions of {Name}")]
    private partial void LogClearingPrimaryVersionId(int count, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleting item path, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}")]
    private partial void LogDeletingItemPath(string type, string name, string path, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Directory not found, only removing from database, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}")]
    private partial void LogDirectoryNotFound(string type, string name, string path, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "File not found, only removing from database, Type: {Type}, Name: {Name}, Path: {Path}, Id: {Id}")]
    private partial void LogFileNotFound(string type, string name, string path, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in ItemAdded event handler")]
    private partial void LogErrorInItemAddedHandler(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cannot get file info for {Path}")]
    private partial void LogCannotGetFileInfo(Exception ex, string path);
}
