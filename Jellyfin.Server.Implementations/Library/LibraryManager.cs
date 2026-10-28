#pragma warning disable CS1591
#pragma warning disable CA5394

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using BitFaster.Caching.Lru;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using Jellyfin.Naming.Common;
using Jellyfin.Naming.Video;
using Jellyfin.Server.Implementations.Library.Resolvers;
using Jellyfin.Server.Implementations.ScheduledTasks.Tasks;
using Jellyfin.Server.Implementations.Sorting;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Controller.Sorting;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using EpisodeInfo = Jellyfin.Naming.TV.EpisodeInfo;
using Genre = MediaBrowser.Controller.Entities.Genre;
using Person = MediaBrowser.Controller.Entities.Person;
using VideoResolver = Jellyfin.Naming.Video.VideoResolver;

namespace Jellyfin.Server.Implementations.Library;

/// <summary>
/// Class LibraryManager.
/// </summary>
public partial class LibraryManager : ILibraryManager
{
    private const string ShortcutFileExtension = ".mblink";

    private readonly ILogger<LibraryManager> _logger;
    private readonly ITaskManager _taskManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly Lazy<ILibraryMonitor> _libraryMonitorFactory;
    private readonly Lazy<IProviderManager> _providerManagerFactory;
    private readonly Lazy<IUserViewManager> _userViewManagerFactory;
    private readonly IServerApplicationHost _appHost;
    private readonly IFileSystem _fileSystem;
    private readonly IItemRepository _itemRepository;
    private readonly IItemPersistenceService _persistenceService;
    private readonly INextUpService _nextUpService;
    private readonly IItemCountService _countService;
    private readonly ILinkedChildrenService _linkedChildrenService;
    private readonly IImageProcessor _imageProcessor;
    private readonly NamingOptions _namingOptions;
    private readonly IPeopleRepository _peopleRepository;
    private readonly ExtraResolver _extraResolver;
    private readonly IPathManager _pathManager;
    private readonly ILocalizationManager _localization;
    private readonly IDirectoryService _directoryService;
    private readonly FastConcurrentLru<Guid, BaseItem> _cache;
    private readonly DotIgnoreIgnoreRule _dotIgnoreIgnoreRule;
    private readonly IMediaStreamRepository _mediaStreamRepository;
    private readonly Lazy<IExternalDataManager> _externalDataManagerFactory;

    /// <summary>
    /// The _root folder sync lock.
    /// </summary>
    private readonly Lock _rootFolderSyncLock = new();
    private readonly Lock _userRootFolderSyncLock = new();

    private readonly TimeSpan _viewRefreshInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// The _root folder.
    /// </summary>
    private volatile AggregateFolder? _rootFolder;
    private volatile UserRootFolder? _userRootFolder;

    private bool _wizardCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryManager" /> class.
    /// </summary>
    /// <param name="appHost">The application host.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="taskManager">The task manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="libraryMonitorFactory">The library monitor.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="providerManagerFactory">The provider manager.</param>
    /// <param name="userViewManagerFactory">The user view manager.</param>
    /// <param name="itemRepository">The item repository.</param>
    /// <param name="persistenceService">The item persistence service.</param>
    /// <param name="nextUpService">The next up service.</param>
    /// <param name="countService">The item count service.</param>
    /// <param name="linkedChildrenService">The linked children service.</param>
    /// <param name="imageProcessor">The image processor.</param>
    /// <param name="namingOptions">The naming options.</param>
    /// <param name="directoryService">The directory service.</param>
    /// <param name="peopleRepository">The people repository.</param>
    /// <param name="pathManager">The path manager.</param>
    /// <param name="dotIgnoreIgnoreRule">The .ignore rule handler.</param>
    /// <param name="localization">The localization manager.</param>
    /// <param name="mediaStreamRepository">The media stream repository.</param>
    /// <param name="externalDataManagerFactory">The external data manager (lazy, to break the DI cycle through ChapterManager).</param>
    public LibraryManager(
        IServerApplicationHost appHost,
        ILoggerFactory loggerFactory,
        ITaskManager taskManager,
        IUserManager userManager,
        IServerConfigurationManager configurationManager,
        IUserDataManager userDataManager,
        Lazy<ILibraryMonitor> libraryMonitorFactory,
        IFileSystem fileSystem,
        Lazy<IProviderManager> providerManagerFactory,
        Lazy<IUserViewManager> userViewManagerFactory,
        IItemRepository itemRepository,
        IItemPersistenceService persistenceService,
        INextUpService nextUpService,
        IItemCountService countService,
        ILinkedChildrenService linkedChildrenService,
        IImageProcessor imageProcessor,
        NamingOptions namingOptions,
        IDirectoryService directoryService,
        IPeopleRepository peopleRepository,
        IPathManager pathManager,
        DotIgnoreIgnoreRule dotIgnoreIgnoreRule,
        ILocalizationManager localization,
        IMediaStreamRepository mediaStreamRepository,
        Lazy<IExternalDataManager> externalDataManagerFactory)
    {
        _appHost = appHost;
        _logger = loggerFactory.CreateLogger<LibraryManager>();
        _taskManager = taskManager;
        _userManager = userManager;
        _configurationManager = configurationManager;
        _userDataManager = userDataManager;
        _libraryMonitorFactory = libraryMonitorFactory;
        _fileSystem = fileSystem;
        _providerManagerFactory = providerManagerFactory;
        _userViewManagerFactory = userViewManagerFactory;
        _itemRepository = itemRepository;
        _persistenceService = persistenceService;
        _nextUpService = nextUpService;
        _countService = countService;
        _linkedChildrenService = linkedChildrenService;
        _imageProcessor = imageProcessor;

        _cache = new FastConcurrentLru<Guid, BaseItem>(_configurationManager.Configuration.CacheSize);

        _namingOptions = namingOptions;
        _peopleRepository = peopleRepository;
        _pathManager = pathManager;
        _dotIgnoreIgnoreRule = dotIgnoreIgnoreRule;
        _localization = localization;
        _directoryService = directoryService;
        _extraResolver = new ExtraResolver(loggerFactory.CreateLogger<ExtraResolver>(), namingOptions, directoryService);

        _configurationManager.ConfigurationUpdated += ConfigurationUpdated;

        _mediaStreamRepository = mediaStreamRepository;
        _externalDataManagerFactory = externalDataManagerFactory;

        RecordConfigurationValues(_configurationManager.Configuration);
    }

    /// <summary>
    /// Occurs when [item added].
    /// </summary>
    public event EventHandler<ItemChangeEventArgs>? ItemAdded;

    /// <summary>
    /// Occurs when [item updated].
    /// </summary>
    public event EventHandler<ItemChangeEventArgs>? ItemUpdated;

    /// <summary>
    /// Occurs when [item removed].
    /// </summary>
    public event EventHandler<ItemChangeEventArgs>? ItemRemoved;

    /// <summary>
    /// Gets the root folder.
    /// </summary>
    /// <value>The root folder.</value>
    public AggregateFolder RootFolder
    {
        get
        {
            if (_rootFolder is null)
            {
                lock (_rootFolderSyncLock)
                {
                    _rootFolder ??= CreateRootFolder();
                }
            }

            return _rootFolder;
        }
    }

    private ILibraryMonitor LibraryMonitor => _libraryMonitorFactory.Value;

    private IProviderManager ProviderManager => _providerManagerFactory.Value;

    private IUserViewManager UserViewManager => _userViewManagerFactory.Value;

    /// <summary>
    /// Gets or sets the postscan tasks.
    /// </summary>
    /// <value>The postscan tasks.</value>
    private ILibraryPostScanTask[] PostScanTasks { get; set; } = [];

    /// <summary>
    /// Gets or sets the intro providers.
    /// </summary>
    /// <value>The intro providers.</value>
    private IIntroProvider[] IntroProviders { get; set; } = [];

    /// <summary>
    /// Gets or sets the list of entity resolution ignore rules.
    /// </summary>
    /// <value>The entity resolution ignore rules.</value>
    private IResolverIgnoreRule[] EntityResolutionIgnoreRules { get; set; } = [];

    /// <summary>
    /// Gets or sets the list of currently registered entity resolvers.
    /// </summary>
    /// <value>The entity resolvers enumerable.</value>
    private IItemResolver[] EntityResolvers { get; set; } = [];

    private IMultiItemResolver[] MultiItemResolvers { get; set; } = [];

    /// <summary>
    /// Gets or sets the comparers.
    /// </summary>
    /// <value>The comparers.</value>
    private IBaseItemComparer[] Comparers { get; set; } = [];

    public bool IsScanRunning { get; private set; }

    /// <summary>
    /// Adds the parts.
    /// </summary>
    /// <param name="rules">The rules.</param>
    /// <param name="resolvers">The resolvers.</param>
    /// <param name="introProviders">The intro providers.</param>
    /// <param name="itemComparers">The item comparers.</param>
    /// <param name="postScanTasks">The post scan tasks.</param>
    public void AddParts(
        IEnumerable<IResolverIgnoreRule> rules,
        IEnumerable<IItemResolver> resolvers,
        IEnumerable<IIntroProvider> introProviders,
        IEnumerable<IBaseItemComparer> itemComparers,
        IEnumerable<ILibraryPostScanTask> postScanTasks)
    {
        EntityResolutionIgnoreRules = rules.ToArray();
        EntityResolvers = resolvers.OrderBy(i => i.Priority).ToArray();
        MultiItemResolvers = EntityResolvers.OfType<IMultiItemResolver>().ToArray();
        IntroProviders = introProviders.ToArray();
        Comparers = itemComparers.ToArray();
        PostScanTasks = postScanTasks.ToArray();
    }

    /// <summary>
    /// Records the configuration values.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    private void RecordConfigurationValues(ServerConfiguration configuration)
    {
        _wizardCompleted = configuration.IsStartupWizardCompleted;
    }

    /// <summary>
    /// Configurations the updated.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The <see cref="EventArgs" /> instance containing the event data.</param>
    private void ConfigurationUpdated(object? sender, EventArgs e)
    {
        var config = _configurationManager.Configuration;

        var wizardChanged = config.IsStartupWizardCompleted != _wizardCompleted;

        RecordConfigurationValues(config);

        if (wizardChanged)
        {
            QueueLibraryScan();
        }
    }

    public void RegisterItem(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item is IItemByName)
        {
            if (item is not MusicArtist)
            {
                return;
            }
        }
        else if (!item.IsFolder)
        {
            if (item is not Video && item is not LiveTvChannel)
            {
                return;
            }
        }

        _cache.AddOrUpdate(item.Id, item);
    }
}
