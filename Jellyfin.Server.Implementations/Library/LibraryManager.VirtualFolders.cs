#pragma warning disable CS1591
#pragma warning disable CA5394

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using Jellyfin.Naming.TV;
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
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Sorting;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using EpisodeInfo = Jellyfin.Naming.TV.EpisodeInfo;
using Genre = MediaBrowser.Controller.Entities.Genre;
using Person = MediaBrowser.Controller.Entities.Person;
using VideoResolver = Jellyfin.Naming.Video.VideoResolver;

namespace Jellyfin.Server.Implementations.Library;

/// <content>
/// Library validation, virtual folders and media paths.
/// </content>
public partial class LibraryManager
{
    /// <summary>
    /// Validates the media library internal.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task ValidateMediaLibraryInternal(IProgress<double> progress, CancellationToken cancellationToken)
    {
        IsScanRunning = true;
        ClearIgnoreRuleCache();
        LibraryMonitor.Stop();

        try
        {
            await PerformLibraryValidation(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ClearIgnoreRuleCache();
            LibraryMonitor.Start();
            IsScanRunning = false;
        }
    }

    public async Task ValidateTopLibraryFolders(CancellationToken cancellationToken, bool removeRoot = false)
    {
        ClearIgnoreRuleCache();
        RootFolder.Children = null;
        await RootFolder.RefreshMetadata(cancellationToken).ConfigureAwait(false);

        // Start by just validating the children of the root, but go no further
        await RootFolder.ValidateChildren(
            new Progress<double>(),
            new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
            recursive: false,
            allowRemoveRoot: removeRoot,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var rootFolder = GetUserRootFolder();
        rootFolder.Children = null;

        await rootFolder.RefreshMetadata(cancellationToken).ConfigureAwait(false);

        await rootFolder.ValidateChildren(
            new Progress<double>(),
            new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
            recursive: false,
            allowRemoveRoot: removeRoot,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Quickly scan CollectionFolders for changes
        var toDelete = new List<Guid>();
        foreach (var child in rootFolder.Children!.OfType<Folder>())
        {
            // If the user has somehow deleted the collection directory, remove the metadata from the database.
            if (child is CollectionFolder collectionFolder && !Directory.Exists(collectionFolder.Path))
            {
                toDelete.Add(collectionFolder.Id);
            }
            else
            {
                await child.RefreshMetadata(cancellationToken).ConfigureAwait(false);
            }
        }

        if (toDelete.Count > 0)
        {
            _persistenceService.DeleteItem(toDelete.ToArray());
        }

        ClearIgnoreRuleCache();
    }

    /// <inheritdoc />
    public void ClearIgnoreRuleCache()
    {
        _dotIgnoreIgnoreRule.ClearDirectoryCache();
    }

    private async Task PerformLibraryValidation(IProgress<double> progress, CancellationToken cancellationToken)
    {
        LogValidatingMediaLibrary();

        await ValidateTopLibraryFolders(cancellationToken).ConfigureAwait(false);

        var innerProgress = new Progress<double>(pct => progress.Report(pct * 0.96));

        // Validate the entire media library
        await RootFolder.ValidateChildren(innerProgress, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), recursive: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        progress.Report(96);

        innerProgress = new Progress<double>(pct => progress.Report(96 + (pct * .04)));

        await RunPostScanTasks(innerProgress, cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }

    /// <summary>
    /// Runs the post scan tasks.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Task.</returns>
    private async Task RunPostScanTasks(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var tasks = PostScanTasks.ToList();

        var numComplete = 0;
        var numTasks = tasks.Count;

        LogRunningPostScanTasks(numTasks);

        var phaseStart = Stopwatch.GetTimestamp();

        foreach (var task in tasks)
        {
            // Prevent access to modified closure
            var currentNumComplete = numComplete;

            var innerProgress = new Progress<double>(pct =>
            {
                double innerPercent = pct;
                innerPercent /= 100;
                innerPercent += currentNumComplete;

                innerPercent /= numTasks;
                innerPercent *= 100;

                progress.Report(innerPercent);
            });

            var taskName = task.GetType().Name;
            var taskStart = Stopwatch.GetTimestamp();

            LogRunningPostScanTask(currentNumComplete + 1, numTasks, taskName);

            try
            {
                await task.Run(innerProgress, cancellationToken).ConfigureAwait(false);

                var elapsed = Stopwatch.GetElapsedTime(taskStart);
                LogPostScanTaskCompleted(taskName, Math.Truncate(elapsed.TotalMinutes), elapsed.Seconds);
            }
            catch (OperationCanceledException)
            {
                var elapsed = Stopwatch.GetElapsedTime(taskStart);
                LogPostScanTaskCancelled(taskName, Math.Truncate(elapsed.TotalMinutes), elapsed.Seconds);
                throw;
            }
            catch (Exception ex)
            {
                var elapsed = Stopwatch.GetElapsedTime(taskStart);
                LogPostScanTaskFailed(ex, taskName, Math.Truncate(elapsed.TotalMinutes), elapsed.Seconds);
            }

            numComplete++;
            double percent = numComplete;
            percent /= numTasks;
            progress.Report(percent * 100);
        }

        var phaseElapsed = Stopwatch.GetElapsedTime(phaseStart);
        LogAllPostScanTasksCompleted(Math.Truncate(phaseElapsed.TotalMinutes), phaseElapsed.Seconds);

        _persistenceService.UpdateInheritedValues();

        progress.Report(100);
    }

    /// <summary>
    /// Gets the default view.
    /// </summary>
    /// <returns>IEnumerable{VirtualFolderInfo}.</returns>
    public List<VirtualFolderInfo> GetVirtualFolders()
    {
        return GetVirtualFolders(false);
    }

    public List<VirtualFolderInfo> GetVirtualFolders(bool includeRefreshState)
    {
        LogGettingTopLibraryFolders();
        var topLibraryFolders = GetUserRootFolder().Children.ToList();

        LogGettingRefreshQueue();
        var refreshQueue = includeRefreshState ? ProviderManager.GetRefreshQueue() : null;

        return _fileSystem.GetDirectoryPaths(_configurationManager.ApplicationPaths.DefaultUserViewsPath)
            .Select(dir => GetVirtualFolderInfo(dir, topLibraryFolders, refreshQueue))
            .ToList();
    }

    private VirtualFolderInfo GetVirtualFolderInfo(string dir, List<BaseItem> allCollectionFolders, HashSet<Guid>? refreshQueue)
    {
        var info = new VirtualFolderInfo
        {
            Name = Path.GetFileName(dir),

            Locations = _fileSystem.GetFilePaths(dir, false)
            .Where(i => Path.GetExtension(i.AsSpan()).Equals(ShortcutFileExtension, StringComparison.OrdinalIgnoreCase))
                .Select(i =>
                {
                    try
                    {
                        return _appHost.ExpandVirtualPath(_fileSystem.ResolveShortcut(i));
                    }
                    catch (Exception ex)
                    {
                        LogErrorResolvingShortcutFile(ex, i);
                        return null;
                    }
                })
                .Where(i => i is not null)
                .Order()
                .ToArray(),

            CollectionType = GetCollectionType(dir)
        };

        var libraryFolder = allCollectionFolders.FirstOrDefault(i => string.Equals(i.Path, dir, StringComparison.OrdinalIgnoreCase));
        if (libraryFolder is not null)
        {
            var libraryFolderId = libraryFolder.Id.ToString("N", CultureInfo.InvariantCulture);
            info.ItemId = libraryFolderId;
            if (libraryFolder.HasImage(ImageType.Primary))
            {
                info.PrimaryImageItemId = libraryFolderId;
            }

            info.LibraryOptions = GetLibraryOptions(libraryFolder);

            if (refreshQueue is not null)
            {
                info.RefreshProgress = libraryFolder.GetRefreshProgress();

                info.RefreshStatus = info.RefreshProgress.HasValue ? "Active" : refreshQueue.Contains(libraryFolder.Id) ? "Queued" : "Idle";
            }
        }

        return info;
    }

    private CollectionTypeOptions? GetCollectionType(string path)
    {
        var files = _fileSystem.GetFilePaths(path, [".collection"], true, false);
        foreach (ReadOnlySpan<char> file in files)
        {
            if (Enum.TryParse<CollectionTypeOptions>(Path.GetFileNameWithoutExtension(file), true, out var res))
            {
                return res;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public BaseItem? GetItemById(Guid id)
    {
        if (id.IsEmpty())
        {
            throw new ArgumentException("Guid can't be empty", nameof(id));
        }

        if (_cache.TryGet(id, out var item))
        {
            return item;
        }

        item = RetrieveItem(id);

        if (item is not null)
        {
            RegisterItem(item);
        }

        return item;
    }

    /// <inheritdoc />
    public int? GetSeasonNumberFromPath(string path, Guid? parentId)
    {
        var parentPath = parentId.HasValue ? GetItemById(parentId.Value)?.ContainingFolderPath : null;
        return SeasonPathParser.Parse(path, parentPath, true, true).SeasonNumber;
    }

    public IReadOnlyList<PersonInfo> GetPeople(InternalPeopleQuery query)
    {
        return _peopleRepository.GetPeople(query).Items;
    }

    private async Task SavePeopleMetadataAsync(IEnumerable<PersonInfo> people, CancellationToken cancellationToken)
    {
        foreach (var person in people)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var itemUpdateType = ItemUpdateType.MetadataDownload;
            var saveEntity = false;
            var personEntity = GetPerson(person.Name);

            if (personEntity is null)
            {
                try
                {
                    personEntity = GetOrCreatePerson(person.Name);
                    saveEntity = true;
                }
                catch (Exception ex)
                {
                    LogFailedToCreatePerson(ex, person.Name);
                    continue;
                }
            }

            foreach (var id in person.ProviderIds)
            {
                if (!string.Equals(personEntity.GetProviderId(id.Key), id.Value, StringComparison.OrdinalIgnoreCase))
                {
                    personEntity.SetProviderId(id.Key, id.Value);
                    saveEntity = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(person.ImageUrl) && !personEntity.HasImage(ImageType.Primary))
            {
                personEntity.SetImage(
                    new ItemImageInfo
                    {
                        Path = person.ImageUrl,
                        Type = ImageType.Primary
                    },
                    0);

                saveEntity = true;
                itemUpdateType = ItemUpdateType.ImageUpdate;
            }

            if (saveEntity)
            {
                await RunMetadataSavers(personEntity, itemUpdateType).ConfigureAwait(false);
                personEntity.DateLastSaved = DateTime.UtcNow;

                CreateItems([personEntity], null, CancellationToken.None);
            }
        }
    }

    public void AddMediaPath(string virtualFolderName, MediaPathInfo mediaPath)
    {
        AddMediaPathInternal(virtualFolderName, mediaPath, true);
    }

    private void AddMediaPathInternal(string virtualFolderName, MediaPathInfo pathInfo, bool saveLibraryOptions)
    {
        ArgumentNullException.ThrowIfNull(pathInfo);

        var path = pathInfo.Path;

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(nameof(path));
        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException("The path does not exist.");
        }

        var rootFolderPath = _configurationManager.ApplicationPaths.DefaultUserViewsPath;
        var virtualFolderPath = FileSystemHelper.GetChildPath(rootFolderPath, virtualFolderName)
            ?? throw new FileNotFoundException(
                string.Format(CultureInfo.InvariantCulture, "The media collection {0} does not exist", virtualFolderName));

        CreateShortcut(virtualFolderPath, pathInfo);

        if (saveLibraryOptions)
        {
            var libraryOptions = CollectionFolder.GetLibraryOptions(virtualFolderPath);

            libraryOptions.PathInfos = [.. libraryOptions.PathInfos, pathInfo];

            SyncLibraryOptionsToLocations(virtualFolderPath, libraryOptions);

            CollectionFolder.SaveLibraryOptions(virtualFolderPath, libraryOptions);
        }
    }

    public void UpdateMediaPath(string virtualFolderName, MediaPathInfo mediaPath)
    {
        ArgumentNullException.ThrowIfNull(mediaPath);

        var rootFolderPath = _configurationManager.ApplicationPaths.DefaultUserViewsPath;
        var virtualFolderPath = FileSystemHelper.GetChildPath(rootFolderPath, virtualFolderName)
            ?? throw new FileNotFoundException(
                string.Format(CultureInfo.InvariantCulture, "The media collection {0} does not exist", virtualFolderName));

        var libraryOptions = CollectionFolder.GetLibraryOptions(virtualFolderPath);

        SyncLibraryOptionsToLocations(virtualFolderPath, libraryOptions);

        CollectionFolder.SaveLibraryOptions(virtualFolderPath, libraryOptions);
    }

    private void SyncLibraryOptionsToLocations(string virtualFolderPath, LibraryOptions options)
    {
        var topLibraryFolders = GetUserRootFolder().Children.ToList();
        var info = GetVirtualFolderInfo(virtualFolderPath, topLibraryFolders, null);

        if (info.Locations.Length > 0 && info.Locations.Length != options.PathInfos.Length)
        {
            var list = options.PathInfos.ToList();

            foreach (var location in info.Locations)
            {
                if (!list.Any(i => string.Equals(i.Path, location, StringComparison.Ordinal)))
                {
                    list.Add(new MediaPathInfo(location));
                }
            }

            options.PathInfos = list.ToArray();
        }
    }

    public async Task RemoveVirtualFolder(string name, bool refreshLibrary)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        var rootFolderPath = _configurationManager.ApplicationPaths.DefaultUserViewsPath;

        var path = FileSystemHelper.GetChildPath(rootFolderPath, name);

        if (path is null || !Directory.Exists(path))
        {
            throw new FileNotFoundException("The media folder does not exist");
        }

        LibraryMonitor.Stop();

        try
        {
            Directory.Delete(path, true);
            _directoryService.Invalidate(path);
        }
        finally
        {
            CollectionFolder.OnCollectionFolderChange();

            if (refreshLibrary)
            {
                await ValidateTopLibraryFolders(CancellationToken.None, true).ConfigureAwait(false);

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

    private void RemoveContentTypeOverrides(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        List<NameValuePair>? removeList = null;

        foreach (var contentType in _configurationManager.Configuration.ContentTypes)
        {
            if (string.IsNullOrWhiteSpace(contentType.Name)
                || _fileSystem.AreEqual(path, contentType.Name)
                || _fileSystem.ContainsSubPath(path, contentType.Name))
            {
                (removeList ??= new()).Add(contentType);
            }
        }

        if (removeList is not null)
        {
            _configurationManager.Configuration.ContentTypes = _configurationManager.Configuration.ContentTypes
                .Except(removeList)
                .ToArray();

            _configurationManager.SaveConfiguration();
        }
    }

    public void RemoveMediaPath(string virtualFolderName, string mediaPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediaPath);

        var rootFolderPath = _configurationManager.ApplicationPaths.DefaultUserViewsPath;
        var virtualFolderPath = FileSystemHelper.GetChildPath(rootFolderPath, virtualFolderName);

        if (virtualFolderPath is null || !Directory.Exists(virtualFolderPath))
        {
            throw new FileNotFoundException(
                string.Format(CultureInfo.InvariantCulture, "The media collection {0} does not exist", virtualFolderName));
        }

        var shortcut = _fileSystem.GetFilePaths(virtualFolderPath, true)
            .Where(i => Path.GetExtension(i.AsSpan()).Equals(ShortcutFileExtension, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(f => _appHost.ExpandVirtualPath(_fileSystem.ResolveShortcut(f)).Equals(mediaPath, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(shortcut))
        {
            _fileSystem.DeleteFile(shortcut);
            _directoryService.Invalidate(shortcut);
        }

        var libraryOptions = CollectionFolder.GetLibraryOptions(virtualFolderPath);

        libraryOptions.PathInfos = libraryOptions
            .PathInfos
            .Where(i => !string.Equals(i.Path, mediaPath, StringComparison.Ordinal))
            .ToArray();

        CollectionFolder.SaveLibraryOptions(virtualFolderPath, libraryOptions);
    }

    private static bool ItemIsVisible(BaseItem? item, User? user)
    {
        if (item is null)
        {
            return false;
        }

        if (user is null)
        {
            return true;
        }

        return item is UserRootFolder || item.IsVisibleStandalone(user);
    }

    public void CreateShortcut(string virtualFolderPath, MediaPathInfo pathInfo)
    {
        var path = pathInfo.Path;
        var rootFolderPath = _configurationManager.ApplicationPaths.DefaultUserViewsPath;

        var shortcutFilename = Path.GetFileNameWithoutExtension(path);

        var lnk = Path.Combine(virtualFolderPath, shortcutFilename + ShortcutFileExtension);

        while (File.Exists(lnk))
        {
            shortcutFilename += "1";
            lnk = Path.Combine(virtualFolderPath, shortcutFilename + ShortcutFileExtension);
        }

        _fileSystem.CreateShortcut(lnk, _appHost.ReverseVirtualPath(path));
        _directoryService.Invalidate(lnk);
        RemoveContentTypeOverrides(path);
    }

    /// <inheritdoc />
    public async Task RerouteLinkedChildReferencesAsync(Guid fromChildId, Guid toChildId)
    {
        var affectedParentIds = _linkedChildrenService.RerouteLinkedChildren(fromChildId, toChildId);

        // Update in-memory LinkedChildren and re-save metadata (NFO) for affected parents
        foreach (var parentId in affectedParentIds)
        {
            if (GetItemById(parentId) is Folder parent)
            {
                foreach (var lc in parent.LinkedChildren)
                {
                    if (lc.ItemId.HasValue && lc.ItemId.Value.Equals(fromChildId))
                    {
                        lc.ItemId = toChildId;
                    }
                }

                await RunMetadataSavers(parent, ItemUpdateType.MetadataEdit).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Validating media library")]
    private partial void LogValidatingMediaLibrary();

    [LoggerMessage(Level = LogLevel.Information, Message = "Running {TaskCount} post-scan task(s)")]
    private partial void LogRunningPostScanTasks(int taskCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Running post-scan task {TaskNumber}/{TaskCount}: {TaskName}")]
    private partial void LogRunningPostScanTask(int taskNumber, int taskCount, string taskName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Post-scan task {TaskName} completed after {Minutes} minute(s) and {Seconds} seconds")]
    private partial void LogPostScanTaskCompleted(string taskName, double minutes, int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Post-scan task {TaskName} cancelled after {Minutes} minute(s) and {Seconds} seconds")]
    private partial void LogPostScanTaskCancelled(string taskName, double minutes, int seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Post-scan task {TaskName} failed after {Minutes} minute(s) and {Seconds} seconds")]
    private partial void LogPostScanTaskFailed(Exception ex, string taskName, double minutes, int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "All post-scan tasks completed after {Minutes} minute(s) and {Seconds} seconds")]
    private partial void LogAllPostScanTasksCompleted(double minutes, int seconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Getting topLibraryFolders")]
    private partial void LogGettingTopLibraryFolders();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Getting refreshQueue")]
    private partial void LogGettingRefreshQueue();

    [LoggerMessage(Level = LogLevel.Error, Message = "Error resolving shortcut file {File}")]
    private partial void LogErrorResolvingShortcutFile(Exception ex, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to create person {Name}")]
    private partial void LogFailedToCreatePerson(Exception ex, string name);
}
