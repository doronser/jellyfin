#nullable disable

using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Naming.Audio;
using Jellyfin.Naming.Common;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Library.Resolvers.Audio;

/// <summary>
/// The music artist resolver.
/// </summary>
/// <param name="logger">Instance of the <see cref="MusicAlbumResolver"/> interface.</param>
/// <param name="namingOptions">The <see cref="NamingOptions"/>.</param>
/// <param name="directoryService">The directory service.</param>
public class MusicArtistResolver(
    ILogger<MusicAlbumResolver> logger,
    NamingOptions namingOptions,
    IDirectoryService directoryService) : ItemResolver<MusicArtist>
{
    /// <summary>
    /// Gets the priority.
    /// </summary>
    /// <value>The priority.</value>
    public override ResolverPriority Priority => ResolverPriority.Second;

    /// <summary>
    /// Resolves the specified resolver arguments.
    /// </summary>
    /// <param name="args">The resolver arguments.</param>
    /// <returns>A <see cref="MusicArtist"/>.</returns>
    protected override MusicArtist Resolve(ItemResolveArgs args)
    {
        if (!args.IsDirectory)
        {
            return null;
        }

        // Don't allow nested artists
        if (args.HasParent<MusicArtist>() || args.HasParent<MusicAlbum>())
        {
            return null;
        }

        var collectionType = args.GetCollectionType();

        var isMusicMediaFolder = collectionType == CollectionType.music;

        // If there's a collection type and it's not music, it can't be a music artist
        if (!isMusicMediaFolder)
        {
            return null;
        }

        if (args.ContainsFileSystemEntryByName("artist.nfo"))
        {
            return new MusicArtist();
        }

        // Avoid mis-identifying top folders
        if (args.Parent.IsRoot)
        {
            return null;
        }

        var albumResolver = new MusicAlbumResolver(logger, namingOptions, directoryService);
        var albumParser = new AlbumParser(namingOptions);

        var directories = args.FileSystemChildren.Where(i => i.IsDirectory);

        var result = Parallel.ForEach(directories, (fileSystemInfo, state) =>
        {
            // If we contain a artist subfolder assume we are an artist folder
            foreach (var subfolder in namingOptions.ArtistSubfolders)
            {
                if (fileSystemInfo.Name.Equals(subfolder, StringComparison.OrdinalIgnoreCase))
                {
                    // Stop once we see an artist subfolder
                    state.Stop();
                }
            }

            // If the folder is a multi-disc folder, then it is not an artist folder
            if (albumParser.IsMultiPart(fileSystemInfo.FullName))
            {
                return;
            }

            // If we contain a music album assume we are an artist folder
            if (albumResolver.IsMusicAlbum(fileSystemInfo.FullName, directoryService))
            {
                // Stop once we see a music album
                state.Stop();
            }
        });

        return !result.IsCompleted ? new MusicArtist() : null;
    }
}
