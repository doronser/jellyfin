#pragma warning disable CS1591
#pragma warning disable CA5394

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using Jellyfin.Naming.TV;
using Jellyfin.Naming.Video;
using Jellyfin.Server.Implementations.Playlists;
using Jellyfin.Server.Implementations.ScheduledTasks.Tasks;
using Jellyfin.Server.Implementations.Sorting;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Controller.Sorting;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using EpisodeInfo = Jellyfin.Naming.TV.EpisodeInfo;
using Genre = MediaBrowser.Controller.Entities.Genre;
using Person = MediaBrowser.Controller.Entities.Person;

namespace Jellyfin.Server.Implementations.Library;

/// <content>
/// Item, people and by-name item queries.
/// </content>
public partial class LibraryManager
{
    /// <inheritdoc />
    public Guid GetPersonId(string name)
    {
        return GetItemByNameId<Person>(Person.GetPath(name));
    }

    /// <inheritdoc />
    public Person? GetPerson(string name)
    {
        var path = Person.GetPath(name);
        var id = GetItemByNameId<Person>(path);
        if (GetItemById(id) is Person item)
        {
            return item;
        }

        return null;
    }

    /// <inheritdoc />
    public Person GetOrCreatePerson(string name)
    {
        var existing = GetPerson(name);
        if (existing is not null)
        {
            return existing;
        }

        var path = Person.GetPath(name);
        var info = Directory.CreateDirectory(path);
        var item = new Person
        {
            Name = name,
            Id = GetItemByNameId<Person>(path),
            DateCreated = info.CreationTimeUtc,
            DateModified = info.LastWriteTimeUtc,
            Path = path
        };

        item.PresentationUniqueKey = item.CreatePresentationUniqueKey();

        CreateItem(item, null);

        return item;
    }

    /// <summary>
    /// Gets the studio.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>Task{Studio}.</returns>
    public Studio GetStudio(string name)
    {
        return CreateItemByName<Studio>(Studio.GetPath, name, new DtoOptions(true));
    }

    public Guid GetStudioId(string name)
    {
        return GetItemByNameId<Studio>(Studio.GetPath(name));
    }

    public Guid GetGenreId(string name)
    {
        return GetItemByNameId<Genre>(Genre.GetPath(name));
    }

    public Guid GetMusicGenreId(string name)
    {
        return GetItemByNameId<MusicGenre>(MusicGenre.GetPath(name));
    }

    /// <summary>
    /// Gets the genre.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>Task{Genre}.</returns>
    public Genre GetGenre(string name)
    {
        return CreateItemByName<Genre>(Genre.GetPath, name, new DtoOptions(true));
    }

    /// <summary>
    /// Gets the music genre.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>Task{MusicGenre}.</returns>
    public MusicGenre GetMusicGenre(string name)
    {
        return CreateItemByName<MusicGenre>(MusicGenre.GetPath, name, new DtoOptions(true));
    }

    /// <summary>
    /// Gets the year.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>Task{Year}.</returns>
    public Year GetYear(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Years less than or equal to 0 are invalid.");
        }

        var name = value.ToString(CultureInfo.InvariantCulture);

        return CreateItemByName<Year>(Year.GetPath, name, new DtoOptions(true));
    }

    /// <summary>
    /// Gets a Genre.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>Task{Genre}.</returns>
    public MusicArtist GetArtist(string name)
    {
        return GetArtist(name, new DtoOptions(true));
    }

    public IReadOnlyDictionary<string, MusicArtist[]> GetArtists(IReadOnlyList<string> names)
    {
        return _linkedChildrenService.FindArtists(names);
    }

    public MusicArtist GetArtist(string name, DtoOptions options)
    {
        return CreateItemByName<MusicArtist>(MusicArtist.GetPath, name, options);
    }

    private T CreateItemByName<T>(Func<string, string> getPathFn, string name, DtoOptions options)
        where T : BaseItem, new()
    {
        if (typeof(T) == typeof(MusicArtist))
        {
            var existing = GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.MusicArtist],
                Name = name,
                DtoOptions = options
            }).Cast<MusicArtist>()
            .OrderBy(i => i.IsAccessedByName ? 1 : 0)
            .Cast<T>()
            .FirstOrDefault();

            if (existing is not null)
            {
                return existing;
            }
        }

        var path = getPathFn(name);
        var id = GetItemByNameId<T>(path);
        var item = GetItemById(id) as T;
        if (item is null)
        {
            var info = Directory.CreateDirectory(path);
            item = new T
            {
                Name = name,
                Id = id,
                DateCreated = info.CreationTimeUtc,
                DateModified = info.LastWriteTimeUtc,
                Path = path
            };

            CreateItem(item, null);
        }

        return item;
    }

    private Guid GetItemByNameId<T>(string path)
          where T : BaseItem, new()
    {
        var forceCaseInsensitiveId = _configurationManager.Configuration.EnableNormalizedItemByNameIds;
        return GetNewItemIdInternal(path, typeof(T), forceCaseInsensitiveId);
    }

    /// <summary>
    /// Reloads the root media folder.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Task.</returns>
    public Task ValidateMediaLibrary(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Just run the scheduled task so that the user can see it
        _taskManager.CancelIfRunningAndQueue<RefreshMediaLibraryTask>();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public T? GetItemById<T>(Guid id)
        where T : BaseItem
    {
        var item = GetItemById(id);
        if (item is T typedItem)
        {
            return typedItem;
        }

        return null;
    }

    /// <inheritdoc />
    public T? GetItemById<T>(Guid id, Guid userId)
        where T : BaseItem
    {
        var user = userId.IsEmpty() ? null : _userManager.GetUserById(userId);
        return GetItemById<T>(id, user);
    }

    /// <inheritdoc />
    public T? GetItemById<T>(Guid id, User? user)
        where T : BaseItem
    {
        var item = GetItemById<T>(id);
        return ItemIsVisible(item, user) ? item : null;
    }

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query, bool allowExternalContent)
    {
        if (query.Recursive && !query.ParentId.IsEmpty())
        {
            var parent = GetItemById(query.ParentId);
            if (parent is not null)
            {
                SetTopParentIdsOrAncestors(query, [parent]);
            }
        }

        if (query.User is not null)
        {
            AddUserToQuery(query, query.User, allowExternalContent);
        }

        return _itemRepository.GetItemList(query);
    }

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query)
    {
        return GetItemList(query, true);
    }

    public int GetCount(InternalItemsQuery query)
    {
        if (query.Recursive && !query.ParentId.IsEmpty())
        {
            var parent = GetItemById(query.ParentId);
            if (parent is not null)
            {
                SetTopParentIdsOrAncestors(query, [parent]);
            }
        }

        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        return _countService.GetCount(query);
    }

    public ItemCounts GetItemCounts(InternalItemsQuery query)
    {
        if (query.Recursive && !query.ParentId.IsEmpty())
        {
            var parent = GetItemById(query.ParentId);
            if (parent is not null)
            {
                SetTopParentIdsOrAncestors(query, [parent]);
            }
        }

        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        return _countService.GetItemCounts(query);
    }

    /// <inheritdoc/>
    public ItemCounts GetItemCountsForNameItem(BaseItemKind kind, Guid id, BaseItemKind[] relatedItemKinds, User? user)
    {
        var query = new InternalItemsQuery(user);
        if (user is not null)
        {
            AddUserToQuery(query, user);
        }

        return _countService.GetItemCountsForNameItem(kind, id, relatedItemKinds, query);
    }

    /// <inheritdoc/>
    public Dictionary<Guid, ItemCounts> GetItemCountsForNameItems(BaseItemKind kind, IReadOnlyList<Guid> ids, BaseItemKind[] relatedItemKinds, User? user)
    {
        var query = new InternalItemsQuery(user);
        if (user is not null)
        {
            AddUserToQuery(query, user);
        }

        return _countService.GetItemCountsForNameItems(kind, ids, relatedItemKinds, query);
    }

    public Dictionary<Guid, int> GetChildCountBatch(IReadOnlyList<Guid> parentIds, User? user)
    {
        return _countService.GetChildCountBatch(parentIds, user);
    }

    /// <inheritdoc/>
    public Dictionary<Guid, (int Played, int Total)> GetPlayedAndTotalCountBatch(IReadOnlyList<Guid> folderIds, User user)
    {
        return _countService.GetPlayedAndTotalCountBatch(folderIds, user);
    }

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query, List<BaseItem> parents)
    {
        SetTopParentIdsOrAncestors(query, parents);

        if (query.AncestorIds.Length == 0 && query.TopParentIds.Length == 0)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }
        }

        return _itemRepository.GetItemList(query);
    }

    public IReadOnlyList<BaseItem> GetLatestItemList(InternalItemsQuery query, IReadOnlyList<BaseItem> parents, CollectionType collectionType)
    {
        SetTopParentIdsOrAncestors(query, parents);

        if (query.AncestorIds.Length == 0 && query.TopParentIds.Length == 0)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }
        }

        return _itemRepository.GetLatestItemList(query, collectionType);
    }

    public IReadOnlyList<string> GetNextUpSeriesKeys(InternalItemsQuery query, IReadOnlyCollection<BaseItem> parents, DateTime dateCutoff)
    {
        SetTopParentIdsOrAncestors(query, parents);

        if (query.AncestorIds.Length == 0 && query.TopParentIds.Length == 0)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }
        }

        return _nextUpService.GetNextUpSeriesKeys(query, dateCutoff);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, MediaBrowser.Controller.Persistence.NextUpEpisodeBatchResult> GetNextUpEpisodesBatch(
        InternalItemsQuery query,
        IReadOnlyList<string> seriesKeys,
        bool includeSpecials,
        bool includeWatchedForRewatching)
    {
        return _nextUpService.GetNextUpEpisodesBatch(query, seriesKeys, includeSpecials, includeWatchedForRewatching);
    }

    public QueryResult<BaseItem> QueryItems(InternalItemsQuery query)
    {
        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        if (query.EnableTotalRecordCount)
        {
            return _itemRepository.GetItems(query);
        }

        return new QueryResult<BaseItem>(
            query.StartIndex,
            null,
            _itemRepository.GetItemList(query));
    }

    public IReadOnlyList<Guid> GetItemIds(InternalItemsQuery query)
    {
        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        return _itemRepository.GetItemIdsList(query);
    }

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetStudios(InternalItemsQuery query)
    {
        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        SetTopParentOrAncestorIds(query);
        return _itemRepository.GetStudios(query);
    }

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetGenres(InternalItemsQuery query)
    {
        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        SetTopParentOrAncestorIds(query);
        return _itemRepository.GetGenres(query);
    }

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetMusicGenres(InternalItemsQuery query)
    {
        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        SetTopParentOrAncestorIds(query);
        return _itemRepository.GetMusicGenres(query);
    }

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAllArtists(InternalItemsQuery query)
    {
        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        SetTopParentOrAncestorIds(query);
        return _itemRepository.GetAllArtists(query);
    }

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetArtists(InternalItemsQuery query)
    {
        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        SetTopParentOrAncestorIds(query);
        return _itemRepository.GetArtists(query);
    }

    private void SetTopParentOrAncestorIds(InternalItemsQuery query)
    {
        var ancestorIds = query.AncestorIds;
        int len = ancestorIds.Length;
        if (len == 0)
        {
            return;
        }

        var parents = new BaseItem[len];
        for (int i = 0; i < len; i++)
        {
            parents[i] = GetItemById(ancestorIds[i]) ?? throw new ArgumentException($"Failed to find parent with id: {ancestorIds[i]}");
            if (parents[i] is not (ICollectionFolder or UserView))
            {
                return;
            }
        }

        // Optimize by querying against top level views
        var topParentIds = parents.SelectMany(i => GetTopParentIdsForQuery(i, query.User)).ToArray();
        if (topParentIds.Length == 0)
        {
            return;
        }

        query.TopParentIds = topParentIds;
        query.AncestorIds = [];
    }

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAlbumArtists(InternalItemsQuery query)
    {
        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        SetTopParentOrAncestorIds(query);
        return _itemRepository.GetAlbumArtists(query);
    }

    public QueryResult<BaseItem> GetItemsResult(InternalItemsQuery query)
    {
        if (query.Recursive && !query.ParentId.IsEmpty())
        {
            var parent = GetItemById(query.ParentId);
            if (parent is not null)
            {
                SetTopParentIdsOrAncestors(query, [parent]);
            }
        }

        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        if (query.EnableTotalRecordCount)
        {
            return _itemRepository.GetItems(query);
        }

        return new QueryResult<BaseItem>(
            query.StartIndex,
            null,
            _itemRepository.GetItemList(query));
    }

    private void SetTopParentIdsOrAncestors(InternalItemsQuery query, IReadOnlyCollection<BaseItem> parents)
    {
        if (parents.All(i => i is ICollectionFolder || i is UserView))
        {
            // Optimize by querying against top level views
            var topParentIds = parents.SelectMany(i => GetTopParentIdsForQuery(i, query.User)).ToArray();

            if (topParentIds.Length > 0)
            {
                query.TopParentIds = topParentIds;
            }
            else
            {
                SetAncestorIds(query, parents);
            }
        }
        else if (parents.Count == 1 && parents.First() is Folder folder
            && (folder is Playlist || folder is BoxSet)
            && folder.LinkedChildren.Length > 0)
        {
            // Playlists and BoxSets store their contents in LinkedChildren and never
            // populate AncestorIds for those items, so a recursive AncestorIds query
            // would return zero rows. Filter by the descendant set instead, which follows
            // the links and keeps descending, so a linked folder contributes what is below
            // it as well - the episodes of a Series added to a collection, for example.
            query.DescendantOfId = folder.Id;
        }
        else
        {
            SetAncestorIds(query, parents);
        }

        query.Parent = null;
    }

    private static void SetAncestorIds(InternalItemsQuery query, IReadOnlyCollection<BaseItem> parents)
    {
        // We need to be able to query from any arbitrary ancestor up the tree
        query.AncestorIds = parents.SelectMany(i => i.GetIdsForAncestorQuery()).ToArray();

        // Prevent searching in all libraries due to empty filter
        if (query.AncestorIds.Length == 0)
        {
            query.AncestorIds = [Guid.NewGuid()];
        }
    }

    private void AddUserToQuery(InternalItemsQuery query, User user, bool allowExternalContent = true)
    {
        if (query.User is null)
        {
            query.SetUser(user);
        }

        if (query.AncestorIds.Length == 0 &&
            query.ParentId.IsEmpty() &&
            query.ChannelIds.Count == 0 &&
            query.TopParentIds.Length == 0 &&
            string.IsNullOrEmpty(query.AncestorWithPresentationUniqueKey) &&
            string.IsNullOrEmpty(query.SeriesPresentationUniqueKey) &&
            query.ItemIds.Length == 0 &&
            query.OwnerIds.Length == 0)
        {
            var userViews = UserViewManager.GetUserViews(new UserViewQuery
            {
                User = user,
                IncludeHidden = true,
                IncludeExternalContent = allowExternalContent
            });

            query.TopParentIds = userViews.SelectMany(i => GetTopParentIdsForQuery(i, user)).ToArray();

            // Prevent searching in all libraries due to empty filter
            if (query.TopParentIds.Length == 0)
            {
                query.TopParentIds = [Guid.NewGuid()];
            }
        }
    }

    /// <inheritdoc/>
    public void ConfigureUserAccess(InternalItemsQuery query, User user)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(user);

        AddUserToQuery(query, user);
    }

    private IEnumerable<Guid> GetTopParentIdsForQuery(BaseItem item, User? user)
    {
        if (item is UserView view)
        {
            if (view.ViewType == CollectionType.livetv)
            {
                return [view.Id];
            }

            // Translate view into folders
            if (!view.DisplayParentId.IsEmpty())
            {
                var displayParent = GetItemById(view.DisplayParentId);
                if (displayParent is not null)
                {
                    return GetTopParentIdsForQuery(displayParent, user);
                }

                return [];
            }

            if (!view.ParentId.IsEmpty())
            {
                var displayParent = GetItemById(view.ParentId);
                if (displayParent is not null)
                {
                    return GetTopParentIdsForQuery(displayParent, user);
                }

                return [];
            }

            // Handle grouping
            if (user is not null && view.ViewType != CollectionType.unknown && UserView.IsEligibleForGrouping(view.ViewType)
                && user.GetPreference(PreferenceKind.GroupedFolders).Length > 0)
            {
                return GetUserRootFolder()
                    .GetChildren(user, true)
                    .OfType<CollectionFolder>()
                    .Where(i => i.CollectionType is null || i.CollectionType == view.ViewType)
                    .Where(i => user.IsFolderGrouped(i.Id))
                    .SelectMany(i => GetTopParentIdsForQuery(i, user));
            }

            return [];
        }

        if (item is CollectionFolder collectionFolder)
        {
            return collectionFolder.PhysicalFolderIds;
        }

        var topParent = item.GetTopParent();
        if (topParent is not null)
        {
            return [topParent.Id];
        }

        return [];
    }

    /// <summary>
    /// Gets the intros.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="user">The user.</param>
    /// <returns>IEnumerable{System.String}.</returns>
    public async Task<IEnumerable<Video>> GetIntros(BaseItem item, User user)
    {
        if (IntroProviders.Length == 0)
        {
            return [];
        }

        var tasks = IntroProviders
            .Select(i => GetIntros(i, item, user));

        var items = await Task.WhenAll(tasks).ConfigureAwait(false);

        return items
            .SelectMany(i => i)
            .Select(ResolveIntro)
            .Where(i => i is not null)!; // null values got filtered out
    }

    /// <summary>
    /// Gets the intros.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="item">The item.</param>
    /// <param name="user">The user.</param>
    /// <returns>Task&lt;IEnumerable&lt;IntroInfo&gt;&gt;.</returns>
    private async Task<IEnumerable<IntroInfo>> GetIntros(IIntroProvider provider, BaseItem item, User user)
    {
        try
        {
            return await provider.GetIntros(item, user).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogErrorGettingIntros(ex);

            return [];
        }
    }

    /// <summary>
    /// Resolves the intro.
    /// </summary>
    /// <param name="info">The info.</param>
    /// <returns>Video.</returns>
    private Video? ResolveIntro(IntroInfo info)
    {
        Video? video = null;

        if (info.ItemId.HasValue)
        {
            // Get an existing item by Id
            video = GetItemById(info.ItemId.Value) as Video;

            if (video is null)
            {
                LogUnableToLocateItem(info.ItemId.Value);
            }
        }
        else if (!string.IsNullOrEmpty(info.Path))
        {
            try
            {
                // Try to resolve the path into a video
                video = ResolvePath(_fileSystem.GetFileSystemInfo(info.Path)) as Video;

                if (video is null)
                {
                    LogIntroResolverReturnedNull(info.Path);
                }
                else
                {
                    // Pull the saved db item that will include metadata
                    var dbItem = GetItemById(video.Id) as Video;

                    if (dbItem is not null)
                    {
                        video = dbItem;
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                LogErrorResolvingIntroPath(ex, info.Path);
            }
        }
        else
        {
            LogIntroInfoMissingPathAndItemId();
        }

        return video;
    }

    /// <inheritdoc />
    public IEnumerable<Guid> GetLocalAlternateVersionIds(Video video)
    {
        ArgumentNullException.ThrowIfNull(video);

        var linkedIds = _linkedChildrenService.GetLinkedChildrenIds(video.Id, (int)MediaBrowser.Controller.Entities.LinkedChildType.LocalAlternateVersion);
        if (linkedIds.Count > 0)
        {
            return linkedIds;
        }

        return [];
    }

    /// <inheritdoc />
    public IEnumerable<Video> GetLinkedAlternateVersions(Video video)
    {
        ArgumentNullException.ThrowIfNull(video);

        var linkedIds = _linkedChildrenService.GetLinkedChildrenIds(video.Id, (int)MediaBrowser.Controller.Entities.LinkedChildType.LinkedAlternateVersion);
        if (linkedIds.Count > 0)
        {
            return linkedIds
                .Select(id => GetItemById(id))
                .Where(i => i is not null)
                .OfType<Video>()
                .OrderBy(i => i.SortName);
        }

        return [];
    }

    /// <inheritdoc />
    public IReadOnlySet<Guid> GetItemIdsWithAlternateVersions(IReadOnlyList<Guid> itemIds)
    {
        return _linkedChildrenService.GetItemIdsWithAlternateVersions(itemIds);
    }

    /// <inheritdoc />
    public void UpsertLinkedChild(Guid parentId, Guid childId, MediaBrowser.Controller.Entities.LinkedChildType childType)
    {
        _linkedChildrenService.UpsertLinkedChild(parentId, childId, childType);
    }

    /// <inheritdoc />
    public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder)
    {
        IOrderedEnumerable<BaseItem>? orderedItems = null;

        foreach (var orderBy in sortBy.Select(o => GetComparer(o, user)).Where(c => c is not null))
        {
            if (orderBy is RandomComparer)
            {
                var randomItems = items.ToArray();
                Random.Shared.Shuffle(randomItems);
                items = randomItems;
                // Items are no longer ordered at this point, so set orderedItems back to null
                orderedItems = null;
            }
            else if (orderedItems is null)
            {
                orderedItems = sortOrder == SortOrder.Descending
                    ? items.OrderByDescending(i => i, orderBy)
                    : items.OrderBy(i => i, orderBy);
            }
            else
            {
                orderedItems = sortOrder == SortOrder.Descending
                    ? orderedItems!.ThenByDescending(i => i, orderBy)
                    : orderedItems!.ThenBy(i => i, orderBy); // orderedItems is set during the first iteration
            }
        }

        return orderedItems ?? items;
    }

    /// <inheritdoc />
    public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<(ItemSortBy OrderBy, SortOrder SortOrder)> orderBy)
    {
        IOrderedEnumerable<BaseItem>? orderedItems = null;

        foreach (var (name, sortOrder) in orderBy)
        {
            var comparer = GetComparer(name, user);
            if (comparer is null)
            {
                continue;
            }

            if (comparer is RandomComparer)
            {
                var randomItems = items.ToArray();
                Random.Shared.Shuffle(randomItems);
                items = randomItems;
                // Items are no longer ordered at this point, so set orderedItems back to null
                orderedItems = null;
            }
            else if (orderedItems is null)
            {
                orderedItems = sortOrder == SortOrder.Descending
                    ? items.OrderByDescending(i => i, comparer)
                    : items.OrderBy(i => i, comparer);
            }
            else
            {
                orderedItems = sortOrder == SortOrder.Descending
                    ? orderedItems!.ThenByDescending(i => i, comparer)
                    : orderedItems!.ThenBy(i => i, comparer); // orderedItems is set during the first iteration
            }
        }

        return orderedItems ?? items;
    }

    /// <summary>
    /// Gets the comparer.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="user">The user.</param>
    /// <returns>IBaseItemComparer.</returns>
    private IBaseItemComparer? GetComparer(ItemSortBy name, User? user)
    {
        var comparer = Comparers.FirstOrDefault(c => name == c.Type);

        if (comparer is IUserBaseItemComparer)
        {
            if (user is null)
            {
                throw new ArgumentException($"Sort key '{name}' requires a user, but none was provided.");
            }

            var userComparer = (IUserBaseItemComparer)Activator.CreateInstance(comparer.GetType())!; // only null for Nullable<T> instances

            userComparer.User = user;
            userComparer.UserManager = _userManager;
            userComparer.UserDataManager = _userDataManager;

            return userComparer;
        }

        return comparer;
    }

    /// <inheritdoc />
    public void CreateItem(BaseItem item, BaseItem? parent)
    {
        CreateItems([item], parent, CancellationToken.None);
    }

    public IReadOnlyList<PersonInfo> GetPeople(BaseItem item)
    {
        if (item.SupportsPeople)
        {
            var people = GetPeople(new InternalPeopleQuery
            {
                ItemId = item.Id
            });

            if (people.Count > 0)
            {
                return people;
            }
        }

        return [];
    }

    public QueryResult<BaseItem> GetPeopleItems(InternalPeopleQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // This hands back by-name items, so the people without one are not ours to report.
        query.MustHaveItem = true;

        var queryResult = _peopleRepository.GetPeople(query);
        var baseItems = queryResult.Items.Select(i =>
            {
                try
                {
                    return GetPerson(i.Name);
                }
                catch (Exception ex)
                {
                    LogErrorRetrievingBaseItemForPerson(ex, i.Name);
                    return null;
                }
            })
            .Where(i => i is not null)
            .Where(i => query.User is null || i!.IsVisible(query.User))
            .OfType<BaseItem>()
            .ToList()
            .AsReadOnly();

        return new QueryResult<BaseItem>
        {
            StartIndex = queryResult.StartIndex,
            TotalRecordCount = queryResult.TotalRecordCount,
            Items = baseItems,
        };
    }

    public IReadOnlyList<string> GetPeopleNames(InternalPeopleQuery query)
    {
        return _peopleRepository.GetPeopleNames(query);
    }

    /// <inheritdoc/>
    public int DeleteOrphanedCredits()
    {
        return _peopleRepository.DeleteOrphanedCredits();
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> GetPeopleNamesByItems(IReadOnlyList<Guid> itemIds, IReadOnlyList<string> personTypes)
    {
        return _peopleRepository.GetPeopleNamesByItems(itemIds, personTypes);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<Guid, IReadOnlyList<PersonInfo>> GetPeopleByItems(IReadOnlyList<Guid> itemIds)
    {
        return _peopleRepository.GetPeopleByItems(itemIds);
    }

    public void UpdatePeople(BaseItem item, List<PersonInfo> people)
    {
        UpdatePeopleAsync(item, people, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task UpdatePeopleAsync(BaseItem item, IReadOnlyList<PersonInfo> people, CancellationToken cancellationToken)
    {
        if (!item.SupportsPeople)
        {
            return;
        }

        if (people is not null)
        {
            people = people.Where(e => e is not null).ToArray();
            _peopleRepository.UpdatePeople(item.Id, people);
            await SavePeopleMetadataAsync(people, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ItemImageInfo> ConvertImageToLocal(BaseItem item, ItemImageInfo image, int imageIndex, bool removeOnFailure)
    {
        foreach (var url in image.Path.Split('|'))
        {
            try
            {
                LogConvertImageToLocal(item.Id, url);

                await ProviderManager.SaveImage(item, url, image.Type, imageIndex, CancellationToken.None).ConfigureAwait(false);

                await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None).ConfigureAwait(false);

                var localImage = item.GetImageInfo(image.Type, imageIndex);
                if (localImage is null)
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "Downloaded {0} image {1} from {2} is not attached to {3} ({4})",
                        image.Type,
                        imageIndex,
                        url,
                        item.Name,
                        item.Id));
                }

                return localImage;
            }
            catch (HttpRequestException ex)
            {
                if (ex.StatusCode.HasValue
                    && (ex.StatusCode.Value == HttpStatusCode.NotFound || ex.StatusCode.Value == HttpStatusCode.Forbidden))
                {
                    LogErrorDownloadingImage(ex, url);
                    continue;
                }

                throw;
            }
        }

        if (removeOnFailure)
        {
            // Remove this image to prevent it from retrying over and over
            item.RemoveImage(image);
            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None).ConfigureAwait(false);
        }

        throw new InvalidOperationException(string.Format(
            CultureInfo.InvariantCulture,
            "Unable to convert any {0} image url in \"{1}\" to a local file for {2} ({3})",
            image.Type,
            image.Path,
            item.Name,
            item.Id));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetTagNames(InternalItemsQuery query)
    {
        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        SetTopParentOrAncestorIds(query);
        return _itemRepository.GetTagNames(query);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetMediaStreamLanguages(MediaStreamType mediaStreamType)
    {
        return _mediaStreamRepository.GetMediaStreamLanguages(mediaStreamType);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetMediaStreamLanguages(MediaStreamType mediaStreamType, InternalItemsQuery query)
    {
        if (query.User is not null)
        {
            AddUserToQuery(query, query.User);
        }

        SetTopParentOrAncestorIds(query);
        return _itemRepository.GetMediaStreamLanguages(query, mediaStreamType);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error getting intros")]
    private partial void LogErrorGettingIntros(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unable to locate item with Id {ID}.")]
    private partial void LogUnableToLocateItem(Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Intro resolver returned null for {Path}.")]
    private partial void LogIntroResolverReturnedNull(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error resolving path {Path}.")]
    private partial void LogErrorResolvingIntroPath(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "IntroProvider returned an IntroInfo with null Path and ItemId.")]
    private partial void LogIntroInfoMissingPathAndItemId();

    [LoggerMessage(Level = LogLevel.Error, Message = "error retrieving BaseItem for person: {Name}")]
    private partial void LogErrorRetrievingBaseItemForPerson(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ConvertImageToLocal item {ItemId} - image url: {Url}")]
    private partial void LogConvertImageToLocal(Guid itemId, string url);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error downloading image {Url}")]
    private partial void LogErrorDownloadingImage(Exception ex, string url);

    private sealed record ExtraCandidate(BaseItem Extra, ExtraType ExtraType, ExtraRule ExtraRule, bool IsInMixedFolder);
}
