#nullable disable

using Jellyfin.Naming.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Library.Resolvers;

/// <summary>
/// Resolves a Path into an instance of the <see cref="Video"/> class.
/// </summary>
/// <param name="logger">The logger.</param>
/// <param name="namingOptions">The naming options.</param>
/// <param name="directoryService">The directory service.</param>
/// <param name="parseName">Whether to parse the file name for metadata such as the year.</param>
/// <typeparam name="T">The type of item to resolve.</typeparam>
public class GenericVideoResolver<T>(
    ILogger logger,
    NamingOptions namingOptions,
    IDirectoryService directoryService,
    bool parseName = false) : BaseVideoResolver<T>(logger, namingOptions, directoryService)
    where T : Video, new()
{
    /// <inheritdoc />
    protected override T Resolve(ItemResolveArgs args)
    {
        return ResolveVideo<T>(args, parseName);
    }
}
