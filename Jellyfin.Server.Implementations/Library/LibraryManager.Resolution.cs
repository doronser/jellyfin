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
/// Path resolution, ignore rules and extras discovery.
/// </content>
public partial class LibraryManager
{
    private BaseItem? Resolve(ItemResolveArgs args, IItemResolver resolver)
    {
        try
        {
            return resolver.ResolvePath(args);
        }
        catch (Exception ex)
        {
            LogErrorInResolver(ex, resolver.GetType().Name, args.Path);
            return null;
        }
    }

    public Guid GetNewItemId(string key, Type type)
    {
        return GetNewItemIdInternal(key, type, false);
    }

    private Guid GetNewItemIdInternal(string key, Type type, bool forceCaseInsensitive)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(type);

        string programDataPath = _configurationManager.ApplicationPaths.ProgramDataPath;
        if (key.StartsWith(programDataPath, StringComparison.Ordinal))
        {
            // Try to normalize paths located underneath program-data in an attempt to make them more portable
            key = key.Substring(programDataPath.Length)
                .TrimStart('/', '\\')
                .Replace('/', '\\');
        }

        if (forceCaseInsensitive || !_configurationManager.Configuration.EnableCaseSensitiveItemIds)
        {
            key = key.ToLowerInvariant();
        }

        key = type.FullName + key;

        return key.GetMD5();
    }

    public BaseItem? ResolvePath(
        FileSystemMetadata fileInfo,
        Folder? parent = null,
        IDirectoryService? directoryService = null,
        CollectionType? collectionType = null)
        => ResolvePath(fileInfo, directoryService ?? new DirectoryService(_fileSystem), null, parent, collectionType);

    private void SetAdditionalPartsFromStack(Video altVideo, string path)
    {
        if (altVideo.AdditionalParts is { Length: > 0 })
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        IEnumerable<FileSystemMetadata> siblings;
        try
        {
            siblings = _fileSystem.GetFiles(directory);
        }
        catch (Exception ex)
        {
            LogFailedToEnumerateSiblings(ex, path);
            return;
        }

        var stacks = StackResolver.Resolve(siblings, _namingOptions);
        foreach (var stack in stacks)
        {
            if (stack.Files.Count > 1
                && string.Equals(stack.Files[0], path, StringComparison.OrdinalIgnoreCase))
            {
                altVideo.AdditionalParts = stack.Files.Skip(1).ToArray();
                return;
            }
        }
    }

    /// <inheritdoc />
    public Video? ResolveAlternateVersion(string path, Type expectedVideoType, Folder? parent, CollectionType? collectionType)
    {
        // Clean up any existing item saved with wrong type (e.g. Video instead of Movie).
        // This happens when items were previously resolved without proper type context
        // in mixed-content libraries where collectionType is null.
        var expectedId = GetNewItemId(path, expectedVideoType);
        if (expectedVideoType != typeof(Video))
        {
            var wrongTypeId = GetNewItemId(path, typeof(Video));
            if (!wrongTypeId.Equals(expectedId) && GetItemById(wrongTypeId) is Video wrongTypeItem)
            {
                LogRemovingAlternateVersionWrongType(wrongTypeItem.GetType().Name, expectedVideoType.Name, path);

                // A full DeleteItem would save the primary version, which resolves its
                // alternates again and re-enters here before this row is gone.
                DeleteItemsUnsafeFast([wrongTypeItem]);

                // The fast path skips the parent bookkeeping, and the stale item is listed
                // under its ParentId, so that folder's cached listing has to be dropped.
                if (wrongTypeItem.GetParent() is Folder staleParent)
                {
                    staleParent.Children = null;
                    staleParent.UserData = null;
                }
            }
        }

        var resolved = ResolvePath(
            _fileSystem.GetFileSystemInfo(path),
            parent,
            collectionType: collectionType) as Video;

        if (resolved is null)
        {
            return null;
        }

        // Ensure the alternate version has the same concrete type as the primary video.
        // ResolvePath may return a generic Video for files in mixed-content libraries
        // where collectionType is null, even though the primary is a Movie/Episode/etc.
        if (resolved.GetType() != expectedVideoType)
        {
            if (Activator.CreateInstance(expectedVideoType) is Video correctVideo)
            {
                correctVideo.Path = resolved.Path;
                correctVideo.Name = resolved.Name;
                correctVideo.VideoType = resolved.VideoType;
                correctVideo.ProductionYear = resolved.ProductionYear;
                correctVideo.ExtraType = resolved.ExtraType;
                resolved = correctVideo;
            }
        }

        resolved.Id = expectedId;
        return resolved;
    }

    private BaseItem? ResolvePath(
        FileSystemMetadata fileInfo,
        IDirectoryService directoryService,
        IItemResolver[]? resolvers,
        Folder? parent = null,
        CollectionType? collectionType = null,
        LibraryOptions? libraryOptions = null)
    {
        ArgumentNullException.ThrowIfNull(fileInfo);

        var fullPath = fileInfo.FullName;

        if (collectionType is null && parent is not null)
        {
            collectionType = GetContentTypeOverride(fullPath, true);
        }

        var args = new ItemResolveArgs(_configurationManager.ApplicationPaths, this)
        {
            Parent = parent,
            FileInfo = fileInfo,
            CollectionType = collectionType,
            LibraryOptions = libraryOptions
        };

        // Return null if ignore rules deem that we should do so
        if (IgnoreFile(args.FileInfo, args.Parent))
        {
            return null;
        }

        // Gather child folder and files
        if (args.IsDirectory)
        {
            var isPhysicalRoot = args.IsPhysicalRoot;

            // When resolving the root, we need it's grandchildren (children of user views)
            var flattenFolderDepth = isPhysicalRoot ? 2 : 0;

            FileSystemMetadata[] files;
            var isVf = args.IsVf;

            try
            {
                files = FileData.GetFilteredFileSystemEntries(directoryService, args.Path, _fileSystem, _appHost, _logger, args, flattenFolderDepth: flattenFolderDepth, resolveShortcuts: isPhysicalRoot || isVf);
            }
            catch (Exception ex)
            {
                if (parent is not null && parent.IsPhysicalRoot)
                {
                    LogErrorGettingFilteredFileSystemEntries(ex, isPhysicalRoot, isVf);

                    files = [];
                }
                else
                {
                    throw;
                }
            }

            // Need to remove sub-paths that may have been resolved from shortcuts
            // Example: if \\server\movies exists, then strip out \\server\movies\action
            if (isPhysicalRoot)
            {
                files = NormalizeRootPathList(files).ToArray();
            }

            args.FileSystemChildren = files;
        }

        // Filter content based on ignore rules
        if (args.IsDirectory)
        {
            var filtered = args.GetActualFileSystemChildren().ToArray();
            args.FileSystemChildren = filtered ?? [];
        }

        return ResolveItem(args, resolvers);
    }

    public bool IgnoreFile(FileSystemMetadata file, BaseItem? parent)
        => EntityResolutionIgnoreRules.Any(r => r.ShouldIgnore(file, parent));

    public List<FileSystemMetadata> NormalizeRootPathList(IEnumerable<FileSystemMetadata> paths)
    {
        var originalList = paths.ToList();

        var list = originalList.Where(i => i.IsDirectory)
            .Select(i => Path.TrimEndingDirectorySeparator(i.FullName))
            .Distinct()
            .ToList();

        var dupes = list.Where(subPath => !subPath.EndsWith(":\\", StringComparison.Ordinal) && list.Any(i => _fileSystem.ContainsSubPath(i, subPath)))
            .ToList();

        foreach (var dupe in dupes)
        {
            LogFoundDuplicatePath(dupe);
        }

        var newList = list.Except(dupes, StringComparer.Ordinal).Select(_fileSystem.GetDirectoryInfo).ToList();
        newList.AddRange(originalList.Where(i => !i.IsDirectory));
        return newList;
    }

    public IEnumerable<BaseItem> ResolvePaths(IEnumerable<FileSystemMetadata> files, IDirectoryService directoryService, Folder parent, LibraryOptions libraryOptions, CollectionType? collectionType = null)
    {
        return ResolvePaths(files, directoryService, parent, libraryOptions, collectionType, EntityResolvers);
    }

    public IEnumerable<BaseItem> ResolvePaths(
        IEnumerable<FileSystemMetadata> files,
        IDirectoryService directoryService,
        Folder parent,
        LibraryOptions libraryOptions,
        CollectionType? collectionType,
        IItemResolver[] resolvers)
    {
        var fileList = files.Where(i => !IgnoreFile(i, parent)).ToList();

        if (parent is not null)
        {
            var multiItemResolvers = resolvers is null ? MultiItemResolvers : resolvers.OfType<IMultiItemResolver>();

            foreach (var resolver in multiItemResolvers)
            {
                var result = resolver.ResolveMultiple(parent, fileList, collectionType, directoryService);

                if (result?.Items.Count > 0)
                {
                    var items = result.Items;
                    items.RemoveAll(item => !ResolverHelper.SetInitialItemValues(item, parent, this, directoryService));
                    items.AddRange(ResolveFileList(result.ExtraFiles, directoryService, parent, collectionType, resolvers, libraryOptions));
                    return items;
                }
            }
        }

        return ResolveFileList(fileList, directoryService, parent, collectionType, resolvers, libraryOptions);
    }

    private IEnumerable<BaseItem> ResolveFileList(
        IReadOnlyList<FileSystemMetadata> fileList,
        IDirectoryService directoryService,
        Folder? parent,
        CollectionType? collectionType,
        IItemResolver[]? resolvers,
        LibraryOptions libraryOptions)
    {
        // Given that fileList is a list we can save enumerator allocations by indexing
        for (var i = 0; i < fileList.Count; i++)
        {
            var file = fileList[i];
            BaseItem? result = null;
            try
            {
                result = ResolvePath(file, directoryService, resolvers, parent, collectionType, libraryOptions);
            }
            catch (Exception ex)
            {
                LogErrorResolvingPath(ex, file.FullName);
            }

            if (result is not null)
            {
                yield return result;
            }
        }
    }

    /// <summary>
    /// Creates the root media folder.
    /// </summary>
    /// <returns>AggregateFolder.</returns>
    /// <exception cref="InvalidOperationException">Cannot create the root folder until plugins have loaded.</exception>
    public AggregateFolder CreateRootFolder()
    {
        var rootFolderPath = _configurationManager.ApplicationPaths.RootFolderPath;

        var rootFolder = GetItemById(GetNewItemId(rootFolderPath, typeof(AggregateFolder))) as AggregateFolder ??
                         (ResolvePath(_fileSystem.GetDirectoryInfo(rootFolderPath)) as Folder ?? throw new InvalidOperationException("Something went very wong"))
                         .DeepCopy<Folder, AggregateFolder>();

        // In case program data folder was moved
        if (!string.Equals(rootFolder.Path, rootFolderPath, StringComparison.Ordinal))
        {
            LogResettingRootFolderPath(rootFolderPath);
            rootFolder.Path = rootFolderPath;
        }

        // Add in the plug-in folders
        var path = Path.Combine(_configurationManager.ApplicationPaths.DataPath, "playlists");

        var info = Directory.CreateDirectory(path);
        Folder folder = new PlaylistsFolder
        {
            Path = path,
            DateCreated = info.CreationTimeUtc,
            DateModified = info.LastWriteTimeUtc,
        };

        if (folder.Id.IsEmpty())
        {
            folder.Id = GetNewItemId(folder.Path, folder.GetType());
        }

        var dbItem = GetItemById(folder.Id) as BasePluginFolder;

        if (dbItem is not null && string.Equals(dbItem.Path, folder.Path, StringComparison.OrdinalIgnoreCase))
        {
            folder = dbItem;
        }

        if (!folder.ParentId.Equals(rootFolder.Id))
        {
            rootFolder.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, CancellationToken.None).GetAwaiter().GetResult();
            folder.ParentId = rootFolder.Id;
            folder.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, CancellationToken.None).GetAwaiter().GetResult();
        }

        rootFolder.AddVirtualChild(folder);

        RegisterItem(folder);

        return rootFolder;
    }

    /// <inheritdoc />
    public bool FillMissingEpisodeNumbersFromPath(Episode episode, bool forceRefresh)
    {
        var series = episode.Series;
        bool? isAbsoluteNaming = series is not null && string.Equals(series.DisplayOrder, "absolute", StringComparison.OrdinalIgnoreCase);
        if (!isAbsoluteNaming.Value)
        {
            // In other words, no filter applied
            isAbsoluteNaming = null;
        }

        var resolver = new EpisodeResolver(_namingOptions);

        var isFolder = episode.VideoType == VideoType.BluRay || episode.VideoType == VideoType.Dvd;

        EpisodeInfo? episodeInfo = null;
        if (episode.IsFileProtocol)
        {
            episodeInfo = resolver.Resolve(episode.Path, isFolder, null, null, isAbsoluteNaming);
            // Resolve from parent folder if it's not the Season folder
            var parent = episode.GetParent();
            if (episodeInfo is null && parent.GetType() == typeof(Folder))
            {
                episodeInfo = resolver.Resolve(parent.Path, true, null, null, isAbsoluteNaming);
                if (episodeInfo is not null)
                {
                    // add the container
                    episodeInfo.Container = Path.GetExtension(episode.Path)?.TrimStart('.');
                }
            }
        }

        var changed = false;
        if (episodeInfo is null)
        {
            return changed;
        }

        if (episodeInfo.IsByDate)
        {
            if (episode.IndexNumber.HasValue)
            {
                episode.IndexNumber = null;
                changed = true;
            }

            if (episode.IndexNumberEnd.HasValue)
            {
                episode.IndexNumberEnd = null;
                changed = true;
            }

            if (!episode.PremiereDate.HasValue)
            {
                if (episodeInfo.Year.HasValue && episodeInfo.Month.HasValue && episodeInfo.Day.HasValue)
                {
                    episode.PremiereDate = new DateTime(episodeInfo.Year.Value, episodeInfo.Month.Value, episodeInfo.Day.Value).ToUniversalTime();
                }

                if (episode.PremiereDate.HasValue)
                {
                    changed = true;
                }
            }

            if (episode.ProductionYear is null)
            {
                episode.ProductionYear = episodeInfo.Year;

                if (episode.ProductionYear is not null)
                {
                    changed = true;
                }
            }
        }
        else
        {
            if (!episode.IndexNumber.HasValue || forceRefresh)
            {
                if (episode.IndexNumber != episodeInfo.EpisodeNumber)
                {
                    changed = true;
                }

                episode.IndexNumber = episodeInfo.EpisodeNumber;
            }

            if (!episode.IndexNumberEnd.HasValue || forceRefresh)
            {
                if (episode.IndexNumberEnd != episodeInfo.EndingEpisodeNumber)
                {
                    changed = true;
                }

                episode.IndexNumberEnd = episodeInfo.EndingEpisodeNumber;
            }

            if (!episode.ParentIndexNumber.HasValue || forceRefresh)
            {
                if (episode.ParentIndexNumber != episodeInfo.SeasonNumber)
                {
                    changed = true;
                }

                episode.ParentIndexNumber = episodeInfo.SeasonNumber;
            }
        }

        if (!episode.ParentIndexNumber.HasValue)
        {
            var season = episode.Season;

            if (season is not null)
            {
                episode.ParentIndexNumber = season.IndexNumber;
            }

            if (episode.ParentIndexNumber.HasValue)
            {
                changed = true;
            }
        }

        return changed;
    }

    public ItemLookupInfo ParseName(string name)
    {
        var namingOptions = _namingOptions;
        var result = VideoResolver.CleanDateTime(name, namingOptions);

        return new ItemLookupInfo
        {
            Name = VideoResolver.TryCleanString(result.Name, namingOptions, out var newName) ? newName : result.Name,
            Year = result.Year
        };
    }

    public IEnumerable<BaseItem> FindExtras(BaseItem owner, IReadOnlyList<FileSystemMetadata> fileSystemChildren, IDirectoryService directoryService)
    {
        // Apply .ignore rules
        var filtered = fileSystemChildren.Where(c => !_dotIgnoreIgnoreRule.ShouldIgnore(c, owner)).ToList();
        var isFolder = owner.IsFolder || (owner is Video video && (video.VideoType == VideoType.BluRay || video.VideoType == VideoType.Dvd));
        var ownerVideoInfo = VideoResolver.Resolve(owner.Path, isFolder, _namingOptions, libraryRoot: owner.ContainingFolderPath);
        if (ownerVideoInfo is null)
        {
            return [];
        }

        var candidates = new List<ExtraCandidate>();

        var count = filtered.Count;
        for (var i = 0; i < count; i++)
        {
            var current = filtered[i];
            if (current.IsDirectory && _namingOptions.AllExtrasTypesFolderNames.ContainsKey(current.Name))
            {
                var filesInSubFolder = _fileSystem.GetFiles(current.FullName, null, false, false);
                var filesInSubFolderList = filesInSubFolder.ToList();

                bool subFolderIsMixedFolder = filesInSubFolderList.Count > 1;

                foreach (var file in filesInSubFolderList)
                {
                    if (!_extraResolver.TryGetExtraTypeForOwner(file.FullName, ownerVideoInfo, out var extraType, out var extraRule))
                    {
                        continue;
                    }

                    AddCandidate(file, extraType.Value, extraRule, subFolderIsMixedFolder);
                }
            }
            else if (!current.IsDirectory && _extraResolver.TryGetExtraTypeForOwner(current.FullName, ownerVideoInfo, out var extraType, out var extraRule))
            {
                AddCandidate(current, extraType.Value, extraRule, false);
            }
        }

        var extras = new List<BaseItem>();
        var typeCounters = new Dictionary<ExtraType, int>();
        var generatedNames = new Dictionary<ExtraType, HashSet<string>>();

        // Order by path so that the numbering handed out below does not depend on the
        // order the file system happened to list the folder in
        foreach (var candidate in candidates.OrderBy(c => c.Extra.Path, StringComparer.Ordinal))
        {
            var extra = PrepareExtra(candidate);
            if (extra is not null)
            {
                extras.Add(extra);
            }
        }

        return extras;

        void AddCandidate(FileSystemMetadata file, ExtraType extraType, ExtraRule extraRule, bool isInMixedFolder)
        {
            var extra = ResolvePath(_fileSystem.GetFileInfo(file.FullName), directoryService, _extraResolver.GetResolversForExtraType(extraType));
            if (extra is Video or Audio)
            {
                candidates.Add(new ExtraCandidate(extra, extraType, extraRule, isInMixedFolder));
            }
        }

        BaseItem? PrepareExtra(ExtraCandidate candidate)
        {
            var resolved = candidate.Extra;
            var extra = resolved;
            var name = GetExtraName(candidate, ownerVideoInfo, typeCounters);

            // Try to retrieve it from the db. If we don't find it, use the resolved version
            var itemById = GetItemById(extra.Id);
            if (itemById is not null)
            {
                extra = itemById;
            }

            // The name derived from the file is only a default. A name that came from anywhere else,
            // such as a local metadata file, is the user's and has to survive the scan, so only a
            // name this method handed out itself is renewed; RefreshExtras persists the change.
            if (!string.IsNullOrEmpty(name)
                && extra.LockedFields?.Contains(MetadataField.Name) != true
                && (itemById is null || IsGeneratedExtraName(extra.Name, candidate)))
            {
                extra.Name = name;
            }

            // Only update extra type if it is more specific then the currently known extra type
            if (extra.ExtraType is null or ExtraType.Unknown || candidate.ExtraType != ExtraType.Unknown)
            {
                extra.ExtraType = candidate.ExtraType;
            }

            // Only return items that are actual extras (have ExtraType set)
            // Note: OwnerId and ParentId are set by RefreshExtras, not here,
            // so that RefreshExtras can detect when they need updating and set ForceSave.
            if (extra.ExtraType is not null)
            {
                extra.IsInMixedFolder = candidate.IsInMixedFolder;
                return extra;
            }

            return null;
        }

        bool IsGeneratedExtraName(string currentName, ExtraCandidate candidate)
        {
            // The file name is what an extra was called before it was given a name of its type
            if (string.Equals(currentName, candidate.Extra.Name, StringComparison.Ordinal))
            {
                return true;
            }

            if (!generatedNames.TryGetValue(candidate.ExtraType, out var names))
            {
                // Any of the numbers of this type may have been handed out, as the order the extras
                // of a type are numbered in shifts as files appear beside them or are taken away
                names = new HashSet<string>(StringComparer.Ordinal);
                var count = candidates.Count(c => c.ExtraType == candidate.ExtraType);
                for (var seen = 0; seen < count; seen++)
                {
                    names.Add(GetNumberedExtraName(candidate.ExtraType, seen));
                }

                generatedNames[candidate.ExtraType] = names;
            }

            return names.Contains(currentName);
        }
    }

    /// <summary>
    /// Gets the name to give an extra.
    /// </summary>
    /// <param name="candidate">The resolved extra.</param>
    /// <param name="ownerVideoInfo">The naming info of the owner.</param>
    /// <param name="typeCounters">Number of extras named after their type so far, per type.</param>
    /// <returns>The name.</returns>
    private string GetExtraName(ExtraCandidate candidate, VideoFileInfo ownerVideoInfo, Dictionary<ExtraType, int> typeCounters)
    {
        var isNamedAfterOwner = candidate.ExtraRule.RuleType switch
        {
            ExtraRuleType.Filename => true,
            ExtraRuleType.Suffix => string.Equals(candidate.Extra.Name, ownerVideoInfo.Name, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        if (!isNamedAfterOwner)
        {
            return candidate.Extra.Name;
        }

        typeCounters.TryGetValue(candidate.ExtraType, out var seen);
        typeCounters[candidate.ExtraType] = seen + 1;

        return GetNumberedExtraName(candidate.ExtraType, seen);
    }

    /// <summary>
    /// Gets the name given to the n-th extra of a type that is named after its type.
    /// </summary>
    /// <param name="extraType">The extra type.</param>
    /// <param name="seen">Number of extras of the type named before this one.</param>
    /// <returns>The name.</returns>
    private string GetNumberedExtraName(ExtraType extraType, int seen)
    {
        var typeName = _localization.GetServerLocalizedString(GetExtraTypeNameKey(extraType));

        return seen == 0
            ? typeName
            : string.Format(
                CultureInfo.InvariantCulture,
                _localization.GetServerLocalizedString("NameExtraNumbered"),
                typeName,
                seen + 1);
    }

    private static string GetExtraTypeNameKey(ExtraType extraType) => extraType switch
    {
        ExtraType.Clip => "NameExtraClip",
        ExtraType.Trailer => "NameExtraTrailer",
        ExtraType.BehindTheScenes => "NameExtraBehindTheScenes",
        ExtraType.DeletedScene => "NameExtraDeletedScene",
        ExtraType.Interview => "NameExtraInterview",
        ExtraType.Scene => "NameExtraScene",
        ExtraType.Sample => "NameExtraSample",
        ExtraType.ThemeSong => "NameExtraThemeSong",
        ExtraType.ThemeVideo => "NameExtraThemeVideo",
        ExtraType.Featurette => "NameExtraFeaturette",
        ExtraType.Short => "NameExtraShort",
        _ => "NameExtraUnknown"
    };

    public string GetPathAfterNetworkSubstitution(string path, BaseItem? ownerItem)
    {
        foreach (var map in _configurationManager.Configuration.PathSubstitutions)
        {
            if (path.TryReplaceSubPath(map.From, map.To, out var newPath))
            {
                return newPath;
            }
        }

        return path;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in {Resolver} resolving {Path}")]
    private partial void LogErrorInResolver(Exception ex, string resolver, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to enumerate siblings to detect stack for {Path}")]
    private partial void LogFailedToEnumerateSiblings(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removing alternate version with wrong type {WrongType}, expected {ExpectedType}: {Path}")]
    private partial void LogRemovingAlternateVersionWrongType(string wrongType, string expectedType, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in GetFilteredFileSystemEntries isPhysicalRoot: {IsPhysicalRoot} IsVf: {IsVf}")]
    private partial void LogErrorGettingFilteredFileSystemEntries(Exception ex, bool isPhysicalRoot, bool isVf);

    [LoggerMessage(Level = LogLevel.Information, Message = "Found duplicate path: {Path}")]
    private partial void LogFoundDuplicatePath(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error resolving path {Path}")]
    private partial void LogErrorResolvingPath(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resetting root folder path to {Path}")]
    private partial void LogResettingRootFolderPath(string path);
}
