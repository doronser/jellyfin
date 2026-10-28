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
/// Root folders, collection folders and user views.
/// </content>
public partial class LibraryManager
{
    public Folder GetUserRootFolder()
    {
        if (_userRootFolder is null)
        {
            lock (_userRootFolderSyncLock)
            {
                if (_userRootFolder is null)
                {
                    var userRootPath = _configurationManager.ApplicationPaths.DefaultUserViewsPath;

                    _logger.LogDebug("Creating userRootPath at {Path}", userRootPath);
                    Directory.CreateDirectory(userRootPath);

                    var newItemId = GetNewItemId(userRootPath, typeof(UserRootFolder));
                    UserRootFolder? tmpItem = null;
                    try
                    {
                        tmpItem = GetItemById(newItemId) as UserRootFolder;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error creating UserRootFolder {Path}", newItemId);
                    }

                    if (tmpItem is null)
                    {
                        _logger.LogDebug("Creating new userRootFolder with DeepCopy");
                        tmpItem = (ResolvePath(_fileSystem.GetDirectoryInfo(userRootPath)) as Folder ?? throw new InvalidOperationException("Failed to get user root path"))
                                    .DeepCopy<Folder, UserRootFolder>();
                    }

                    // In case program data folder was moved
                    if (!string.Equals(tmpItem.Path, userRootPath, StringComparison.Ordinal))
                    {
                        _logger.LogInformation("Resetting user root folder path to {0}", userRootPath);
                        tmpItem.Path = userRootPath;
                    }

                    _userRootFolder = tmpItem;
                    _logger.LogDebug("Setting userRootFolder: {Folder}", _userRootFolder);
                }
            }
        }

        return _userRootFolder;
    }

    /// <inheritdoc />
    public BaseItem? FindByPath(string path, bool? isFolder)
    {
        // If this returns multiple items it could be tricky figuring out which one is correct.
        // In most cases, the newest one will be and the others obsolete but not yet cleaned up
        ArgumentException.ThrowIfNullOrEmpty(path);

        var query = new InternalItemsQuery
        {
            Path = path,
            IsFolder = isFolder,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            Limit = 1,
            DtoOptions = new DtoOptions(true)
        };

        return GetItemList(query)
            .FirstOrDefault();
    }

    public List<Folder> GetCollectionFolders(BaseItem item)
    {
        return GetCollectionFolders(item, GetUserRootFolder().Children.OfType<Folder>());
    }

    public List<Folder> GetCollectionFolders(BaseItem item, IEnumerable<Folder> allUserRootChildren)
    {
        while (item is not null)
        {
            var parent = item.GetParent();

            if (parent is AggregateFolder)
            {
                break;
            }

            if (parent is null)
            {
                var owner = item.GetOwner();

                if (owner is null)
                {
                    break;
                }

                item = owner;
            }
            else
            {
                item = parent;
            }
        }

        if (item is null)
        {
            return [];
        }

        return GetCollectionFoldersInternal(item, allUserRootChildren);
    }

    private static List<Folder> GetCollectionFoldersInternal(BaseItem item, IEnumerable<Folder> allUserRootChildren)
    {
        return allUserRootChildren
            .Where(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase) || i.PhysicalLocations.Contains(item.Path.AsSpan(), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public LibraryOptions GetLibraryOptions(BaseItem item)
    {
        if (item is CollectionFolder collectionFolder)
        {
            return collectionFolder.GetLibraryOptions();
        }

        // List.Find is more performant than FirstOrDefault due to enumerator allocation
        return GetCollectionFolders(item)
            .Find(folder => folder is CollectionFolder) is CollectionFolder collectionFolder2
            ? collectionFolder2.GetLibraryOptions()
            : new LibraryOptions();
    }

    public CollectionType? GetContentType(BaseItem item)
    {
        var configuredContentType = GetConfiguredContentType(item, false);
        if (configuredContentType is not null)
        {
            return configuredContentType;
        }

        configuredContentType = GetConfiguredContentType(item, true);
        if (configuredContentType is not null)
        {
            return configuredContentType;
        }

        return GetInheritedContentType(item);
    }

    public CollectionType? GetInheritedContentType(BaseItem item)
    {
        var type = GetTopFolderContentType(item);

        if (type is not null)
        {
            return type;
        }

        return item.GetParents()
            .Select(GetConfiguredContentType)
            .LastOrDefault(i => i is not null);
    }

    public CollectionType? GetConfiguredContentType(BaseItem item)
    {
        return GetConfiguredContentType(item, false);
    }

    public CollectionType? GetConfiguredContentType(string path)
    {
        return GetContentTypeOverride(path, false);
    }

    public CollectionType? GetConfiguredContentType(BaseItem item, bool inheritConfiguredPath)
    {
        if (item is ICollectionFolder collectionFolder)
        {
            return collectionFolder.CollectionType;
        }

        return GetContentTypeOverride(item.ContainingFolderPath, inheritConfiguredPath);
    }

    private CollectionType? GetContentTypeOverride(string path, bool inherit)
    {
        var nameValuePair = _configurationManager.Configuration.ContentTypes
                                .FirstOrDefault(i => _fileSystem.AreEqual(i.Name, path)
                                                     || (inherit && !string.IsNullOrEmpty(i.Name)
                                                                 && _fileSystem.ContainsSubPath(i.Name, path)));
        if (Enum.TryParse<CollectionType>(nameValuePair?.Value, out var collectionType))
        {
            return collectionType;
        }

        return null;
    }

    private CollectionType? GetTopFolderContentType(BaseItem item)
    {
        if (item is null)
        {
            return null;
        }

        while (!item.ParentId.IsEmpty())
        {
            var parent = item.GetParent();
            if (parent is null || parent is AggregateFolder)
            {
                break;
            }

            item = parent;
        }

        return GetUserRootFolder().Children
            .OfType<ICollectionFolder>()
            .Where(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase) || i.PhysicalLocations.Contains(item.Path))
            .Select(i => i.CollectionType)
            .FirstOrDefault(i => i is not null);
    }

    public UserView GetNamedView(
        User user,
        string name,
        CollectionType? viewType,
        string sortName)
    {
        return GetNamedView(user, name, Guid.Empty, viewType, sortName);
    }

    public UserView GetNamedView(
        string name,
        CollectionType viewType,
        string sortName)
    {
        var path = Path.Combine(
            _configurationManager.ApplicationPaths.InternalMetadataPath,
            "views",
            _fileSystem.GetValidFilename(viewType.ToString()));

        // The display name is localized, so it must not take part in the id.
        var id = GetNewItemId(path + "_namedview_" + viewType.ToString(), typeof(UserView));

        var item = GetItemById(id) as UserView;

        var refresh = false;

        if (item is null || !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            var info = Directory.CreateDirectory(path);
            item = new UserView
            {
                Path = path,
                Id = id,
                DateCreated = info.CreationTimeUtc,
                DateModified = info.LastWriteTimeUtc,
                Name = name,
                ViewType = viewType,
                ForcedSortName = sortName
            };

            CreateItem(item, null);

            refresh = true;
        }
        else if (!string.Equals(item.Name, name, StringComparison.Ordinal))
        {
            item.Name = name;
            item.ForcedSortName = sortName;

            refresh = true;
        }

        if (refresh)
        {
            item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, CancellationToken.None).GetAwaiter().GetResult();
            ProviderManager.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Normal);
        }

        return item;
    }

    public UserView GetNamedView(
        User user,
        string name,
        Guid parentId,
        CollectionType? viewType,
        string sortName)
    {
        var id = GetNamedViewId(user, parentId, viewType);

        var path = Path.Combine(_configurationManager.ApplicationPaths.InternalMetadataPath, "views", id.ToString("N", CultureInfo.InvariantCulture));

        var item = GetItemById(id) as UserView;

        var isNew = false;

        if (item is null)
        {
            var info = Directory.CreateDirectory(path);
            item = new UserView
            {
                Path = path,
                Id = id,
                DateCreated = info.CreationTimeUtc,
                DateModified = info.LastWriteTimeUtc,
                Name = name,
                ViewType = viewType,
                ForcedSortName = sortName,
                UserId = user.Id,
                DisplayParentId = parentId
            };

            CreateItem(item, null);

            isNew = true;
        }
        else if (!string.Equals(item.Name, name, StringComparison.Ordinal))
        {
            item.Name = name;
            item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
        }

        var lastRefreshedUtc = item.DateLastRefreshed;
        var refresh = isNew || DateTime.UtcNow - lastRefreshedUtc >= _viewRefreshInterval;

        if (!refresh && !item.DisplayParentId.IsEmpty())
        {
            var displayParent = GetItemById(item.DisplayParentId);
            refresh = displayParent is not null && displayParent.DateLastSaved > lastRefreshedUtc;
        }

        if (refresh)
        {
            ProviderManager.QueueRefresh(
                item.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    // Need to force save to increment DateLastSaved
                    ForceSave = true
                },
                RefreshPriority.Normal);
        }

        return item;
    }

    public Guid GetNamedViewId(User user, Guid parentId, CollectionType? viewType)
    {
        var parentIdString = parentId.IsEmpty()
            ? null
            : parentId.ToString("N", CultureInfo.InvariantCulture);

        // The name is either localized (grouped views) or the library folder's own name.
        var idValues = "38_namedview_" + user.Id.ToString("N", CultureInfo.InvariantCulture) + (parentIdString ?? string.Empty) + (viewType?.ToString() ?? string.Empty);

        return GetNewItemId(idValues, typeof(UserView));
    }

    public UserView GetShadowView(
        BaseItem parent,
        CollectionType? viewType,
        string sortName)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var name = parent.Name;
        var parentId = parent.Id;
        var id = GetShadowViewId(name, parentId, viewType);

        var path = parent.Path;

        var item = GetItemById(id) as UserView;

        var isNew = false;

        if (item is null)
        {
            var info = Directory.CreateDirectory(path);
            item = new UserView
            {
                Path = path,
                Id = id,
                DateCreated = info.CreationTimeUtc,
                DateModified = info.LastWriteTimeUtc,
                Name = name,
                ViewType = viewType,
                ForcedSortName = sortName,
                DisplayParentId = parentId
            };

            CreateItem(item, null);

            isNew = true;
        }

        var lastRefreshedUtc = item.DateLastRefreshed;
        var refresh = isNew || DateTime.UtcNow - lastRefreshedUtc >= _viewRefreshInterval;

        if (!refresh && !item.DisplayParentId.IsEmpty())
        {
            var displayParent = GetItemById(item.DisplayParentId);
            refresh = displayParent is not null && displayParent.DateLastSaved > lastRefreshedUtc;
        }

        if (refresh)
        {
            ProviderManager.QueueRefresh(
                item.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    // Need to force save to increment DateLastSaved
                    ForceSave = true
                },
                RefreshPriority.Normal);
        }

        return item;
    }

    public Guid GetShadowViewId(string name, Guid parentId, CollectionType? viewType)
    {
        var idValues = "38_namedview_" + name + parentId + (viewType?.ToString() ?? string.Empty);

        return GetNewItemId(idValues, typeof(UserView));
    }

    public UserView GetNamedView(
        string name,
        Guid parentId,
        CollectionType? viewType,
        string sortName,
        string uniqueId)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var parentIdString = parentId.IsEmpty()
            ? null
            : parentId.ToString("N", CultureInfo.InvariantCulture);
        var idValues = "37_namedview_" + (parentIdString ?? string.Empty) + (viewType?.ToString() ?? string.Empty);
        if (!string.IsNullOrEmpty(uniqueId))
        {
            idValues += uniqueId;
        }

        var id = GetNewItemId(idValues, typeof(UserView));

        var path = Path.Combine(_configurationManager.ApplicationPaths.InternalMetadataPath, "views", id.ToString("N", CultureInfo.InvariantCulture));

        var item = GetItemById(id) as UserView;

        var isNew = false;

        if (item is null)
        {
            var info = Directory.CreateDirectory(path);
            item = new UserView
            {
                Path = path,
                Id = id,
                DateCreated = info.CreationTimeUtc,
                DateModified = info.LastWriteTimeUtc,
                Name = name,
                ViewType = viewType,
                ForcedSortName = sortName,
                DisplayParentId = parentId
            };

            CreateItem(item, null);

            isNew = true;
        }

        if (viewType != item.ViewType || !string.Equals(item.Name, name, StringComparison.Ordinal))
        {
            item.ViewType = viewType;
            item.Name = name;
            item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
        }

        var lastRefreshedUtc = item.DateLastRefreshed;
        var refresh = isNew || DateTime.UtcNow - lastRefreshedUtc >= _viewRefreshInterval;

        if (!refresh && !item.DisplayParentId.IsEmpty())
        {
            var displayParent = GetItemById(item.DisplayParentId);
            refresh = displayParent is not null && displayParent.DateLastSaved > lastRefreshedUtc;
        }

        if (refresh)
        {
            ProviderManager.QueueRefresh(
                item.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    // Need to force save to increment DateLastSaved
                    ForceSave = true
                },
                RefreshPriority.Normal);
        }

        return item;
    }

    public BaseItem GetParentItem(Guid? parentId, Guid? userId)
    {
        if (parentId.HasValue)
        {
            return GetItemById(parentId.Value) ?? throw new ArgumentException($"Invalid parent id: {parentId.Value}");
        }

        if (!userId.IsNullOrEmpty())
        {
            return GetUserRootFolder();
        }

        return RootFolder;
    }

    /// <inheritdoc />
    public void QueueLibraryScan()
    {
        _taskManager.QueueScheduledTask<RefreshMediaLibraryTask>();
    }
}
