using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Trickplay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Library;

/// <summary>
/// IExternalDataManager implementation.
/// </summary>
/// <param name="keyframeManager">The keyframe manager.</param>
/// <param name="mediaSegmentManager">The media segment manager.</param>
/// <param name="pathManager">The path manager.</param>
/// <param name="trickplayManager">The trickplay manager.</param>
/// <param name="chapterManager">The chapter manager.</param>
/// <param name="logger">The logger.</param>
public partial class ExternalDataManager(
    IKeyframeManager keyframeManager,
    IMediaSegmentManager mediaSegmentManager,
    IPathManager pathManager,
    ITrickplayManager trickplayManager,
    IChapterManager chapterManager,
    ILogger<ExternalDataManager> logger) : IExternalDataManager
{
    /// <inheritdoc/>
    public async Task DeleteExternalItemDataAsync(BaseItem item, CancellationToken cancellationToken)
    {
        DeleteExternalItemFiles(item);

        var itemId = item.Id;
        await keyframeManager.DeleteKeyframeDataAsync(itemId, cancellationToken).ConfigureAwait(false);
        await mediaSegmentManager.DeleteSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        await trickplayManager.DeleteTrickplayDataAsync(itemId, cancellationToken).ConfigureAwait(false);
        await chapterManager.DeleteChapterDataAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void DeleteExternalItemFiles(BaseItem item)
    {
        foreach (var path in pathManager.GetExtractedDataPaths(item))
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                LogPruneExternalDataFailed(path, ex.ToString());
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unable to prune external item data at {Path}: {Exception}")]
    private partial void LogPruneExternalDataFailed(string path, string exception);
}
