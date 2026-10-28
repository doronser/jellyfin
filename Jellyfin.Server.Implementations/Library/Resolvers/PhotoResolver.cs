using System;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using Jellyfin.Naming.Common;
using Jellyfin.Naming.Video;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Server.Implementations.Library.Resolvers;

/// <summary>
/// Class PhotoResolver.
/// </summary>
/// <param name="imageProcessor">The image processor.</param>
/// <param name="namingOptions">The naming options.</param>
/// <param name="directoryService">The directory service.</param>
public class PhotoResolver(
    IImageProcessor imageProcessor,
    NamingOptions namingOptions,
    IDirectoryService directoryService) : ItemResolver<Photo>
{
    private static readonly string[] _ignoreFiles = new[]
    {
        "folder",
        "thumb",
        "landscape",
        "fanart",
        "backdrop",
        "poster",
        "cover",
        "logo",
        "default"
    };

    /// <summary>
    /// Resolves the specified args.
    /// </summary>
    /// <param name="args">The args.</param>
    /// <returns>Trailer.</returns>
    protected override Photo? Resolve(ItemResolveArgs args)
    {
        if (!args.IsDirectory)
        {
            // Must be an image file within a photo collection
            var collectionType = args.CollectionType;

            if (collectionType == CollectionType.photos
                || (collectionType == CollectionType.homevideos && args.LibraryOptions.EnablePhotos))
            {
                if (IsImageFile(args.Path, imageProcessor))
                {
                    var filename = Path.GetFileNameWithoutExtension(args.Path.AsSpan());

                    // Make sure the image doesn't belong to a video file
                    var files = directoryService.GetFiles(Path.GetDirectoryName(args.Path)
                        ?? throw new InvalidOperationException("Path can't be a root directory."));

                    foreach (var file in files)
                    {
                        if (IsOwnedByMedia(namingOptions, file.FullName, filename))
                        {
                            return null;
                        }
                    }

                    return new Photo
                    {
                        Path = args.Path
                    };
                }
            }
        }

        return null;
    }

    internal static bool IsOwnedByMedia(NamingOptions namingOptions, string file, ReadOnlySpan<char> imageFilename)
    {
        return VideoResolver.IsVideoFile(file, namingOptions) && IsOwnedByResolvedMedia(file, imageFilename);
    }

    internal static bool IsOwnedByResolvedMedia(ReadOnlySpan<char> file, ReadOnlySpan<char> imageFilename)
        => imageFilename.StartsWith(Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase);

    internal static bool IsImageFile(string path, IImageProcessor imageProcessor)
    {
        ArgumentNullException.ThrowIfNull(path);

        var extension = Path.GetExtension(path.AsSpan()).TrimStart('.');
        if (!imageProcessor.SupportedInputFormats.Contains(extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var filename = Path.GetFileNameWithoutExtension(path);

        if (_ignoreFiles.Any(i => filename.StartsWith(i, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }
}
