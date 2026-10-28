#nullable disable

using System;
using Jellyfin.Data.Enums;
using Jellyfin.Naming.Common;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Server.Implementations.Library.Resolvers;

/// <summary>
/// Class PhotoAlbumResolver.
/// </summary>
/// <param name="imageProcessor">The image processor.</param>
/// <param name="namingOptions">The naming options.</param>
public class PhotoAlbumResolver(IImageProcessor imageProcessor, NamingOptions namingOptions) : GenericFolderResolver<PhotoAlbum>
{
    /// <inheritdoc />
    public override ResolverPriority Priority => ResolverPriority.Second;

    /// <summary>
    /// Resolves the specified args.
    /// </summary>
    /// <param name="args">The args.</param>
    /// <returns>Trailer.</returns>
    protected override PhotoAlbum Resolve(ItemResolveArgs args)
    {
        // Must be an image file within a photo collection
        if (args.IsDirectory)
        {
            // Must be an image file within a photo collection
            var collectionType = args.GetCollectionType();

            if (collectionType == CollectionType.photos
                || (collectionType == CollectionType.homevideos && args.LibraryOptions.EnablePhotos))
            {
                if (HasPhotos(args))
                {
                    return new PhotoAlbum
                    {
                        Path = args.Path
                    };
                }
            }
        }

        return null;
    }

    private bool HasPhotos(ItemResolveArgs args)
    {
        var files = args.FileSystemChildren;

        foreach (var file in files)
        {
            if (!file.IsDirectory && PhotoResolver.IsImageFile(file.FullName, imageProcessor))
            {
                var filename = file.Name;
                var ownedByMedia = false;

                foreach (var siblingFile in files)
                {
                    if (PhotoResolver.IsOwnedByMedia(namingOptions, siblingFile.FullName, filename))
                    {
                        ownedByMedia = true;
                        break;
                    }
                }

                if (!ownedByMedia)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
