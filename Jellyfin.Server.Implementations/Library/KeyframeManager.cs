using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Persistence;

namespace Jellyfin.Server.Implementations.Library;

/// <summary>
/// Manager for Keyframe data.
/// </summary>
/// <param name="repository">The keyframe repository.</param>
public class KeyframeManager(IKeyframeRepository repository) : IKeyframeManager
{
    /// <inheritdoc />
    public IReadOnlyList<KeyframeData> GetKeyframeData(Guid itemId)
    {
        return repository.GetKeyframeData(itemId);
    }

    /// <inheritdoc />
    public async Task SaveKeyframeDataAsync(Guid itemId, KeyframeData data, CancellationToken cancellationToken)
    {
        await repository.SaveKeyframeDataAsync(itemId, data, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteKeyframeDataAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await repository.DeleteKeyframeDataAsync(itemId, cancellationToken).ConfigureAwait(false);
    }
}
