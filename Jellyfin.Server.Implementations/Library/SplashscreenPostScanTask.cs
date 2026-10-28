using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Library;

/// <summary>
/// The splashscreen post scan task.
/// </summary>
/// <param name="itemRepository">Instance of the <see cref="IItemRepository"/> interface.</param>
/// <param name="imageEncoder">Instance of the <see cref="IImageEncoder"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger{SplashscreenPostScanTask}"/> interface.</param>
public partial class SplashscreenPostScanTask(
    IItemRepository itemRepository,
    IImageEncoder imageEncoder,
    ILogger<SplashscreenPostScanTask> logger) : ILibraryPostScanTask
{
    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var posters = GetItemsWithImageType(ImageType.Primary)
            .Select(x => x.GetImages(ImageType.Primary).FirstOrDefault()?.Path)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToList();
        var backdrops = GetItemsWithImageType(ImageType.Thumb)
            .Select(x => x.GetImages(ImageType.Thumb).FirstOrDefault()?.Path)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToList();
        if (backdrops.Count == 0)
        {
            // Thumb images fit better because they include the title in the image but are not provided with TMDb.
            // Using backdrops as a fallback to generate an image at all
            LogNoThumbImages();
            backdrops = GetItemsWithImageType(ImageType.Backdrop)
                .Select(x => x.GetImages(ImageType.Backdrop).FirstOrDefault()?.Path)
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(path => path!)
                .ToList();
        }

        imageEncoder.CreateSplashscreen(posters, backdrops);
        return Task.CompletedTask;
    }

    private IReadOnlyList<BaseItem> GetItemsWithImageType(ImageType imageType)
    {
        // TODO make included libraries configurable
        return itemRepository.GetItemList(new InternalItemsQuery
        {
            CollapseBoxSetItems = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false),
            ImageTypes = [imageType],
            Limit = 30,
            // TODO max parental rating configurable
            MaxParentalRating = new(13, null),
            OrderBy =
            [
                (ItemSortBy.Random, SortOrder.Ascending)
            ],
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series]
        });
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No thumb images found. Using backdrops to generate splashscreen")]
    private partial void LogNoThumbImages();
}
