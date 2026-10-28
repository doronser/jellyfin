using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Library;

/// <summary>
/// IPathManager implementation.
/// </summary>
/// <param name="logger">The logger.</param>
/// <param name="config">The server configuration manager.</param>
/// <param name="appPaths">The application paths.</param>
public partial class PathManager(
    ILogger<PathManager> logger,
    IServerConfigurationManager config,
    IApplicationPaths appPaths) : IPathManager
{
    private string SubtitleCachePath => Path.Combine(appPaths.DataPath, "subtitles");

    private string AttachmentCachePath => Path.Combine(appPaths.DataPath, "attachments");

    /// <inheritdoc />
    public string? GetAttachmentPath(string mediaSourceId, string fileName)
    {
        var folder = GetAttachmentFolderPath(mediaSourceId);
        if (folder is null)
        {
            return null;
        }

        var safeName = PathHelper.GetSafeLeafFileName(fileName);
        if (safeName is null)
        {
            LogRejectedAttachmentFileName(fileName, mediaSourceId);
            return null;
        }

        return Path.Combine(folder, safeName);
    }

    /// <inheritdoc />
    public string? GetAttachmentFolderPath(string mediaSourceId)
    {
        if (!Guid.TryParse(mediaSourceId, out var parsed))
        {
            LogNoAttachmentFolder(mediaSourceId);
            return null;
        }

        var id = parsed.ToString("D", CultureInfo.InvariantCulture).AsSpan();
        return Path.Join(AttachmentCachePath, id[..2], id);
    }

    /// <inheritdoc />
    public string? GetSubtitleFolderPath(string mediaSourceId)
    {
        if (!Guid.TryParse(mediaSourceId, out var parsed))
        {
            LogNoSubtitleFolder(mediaSourceId);
            return null;
        }

        var id = parsed.ToString("D", CultureInfo.InvariantCulture).AsSpan();
        return Path.Join(SubtitleCachePath, id[..2], id);
    }

    /// <inheritdoc />
    public string? GetSubtitlePath(string mediaSourceId, int streamIndex, string extension)
    {
        var folder = GetSubtitleFolderPath(mediaSourceId);
        return folder is null ? null : Path.Combine(folder, streamIndex.ToString(CultureInfo.InvariantCulture) + extension);
    }

    /// <inheritdoc />
    public string GetTrickplayDirectory(BaseItem item, bool saveWithMedia = false)
    {
        var id = item.Id.ToString("D", CultureInfo.InvariantCulture).AsSpan();

        return saveWithMedia
            ? Path.Combine(item.ContainingFolderPath, Path.ChangeExtension(Path.GetFileName(item.Path), ".trickplay"))
            : Path.Join(config.ApplicationPaths.TrickplayPath, id[..2], id);
    }

    /// <inheritdoc/>
    public string GetChapterImageFolderPath(BaseItem item)
    {
        return Path.Combine(item.GetInternalMetadataPath(), "chapters");
    }

    /// <inheritdoc/>
    public string GetChapterImagePath(BaseItem item, long chapterPositionTicks)
    {
        var filename = item.DateModified.Ticks.ToString(CultureInfo.InvariantCulture) + "_" + chapterPositionTicks.ToString(CultureInfo.InvariantCulture) + ".jpg";

        return Path.Combine(GetChapterImageFolderPath(item), filename);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetExtractedDataPaths(BaseItem item)
    {
        var mediaSourceId = item.Id.ToString("N", CultureInfo.InvariantCulture);
        List<string> paths = [];
        var attachmentFolder = GetAttachmentFolderPath(mediaSourceId);
        if (attachmentFolder is not null)
        {
            paths.Add(attachmentFolder);
        }

        var subtitleFolder = GetSubtitleFolderPath(mediaSourceId);
        if (subtitleFolder is not null)
        {
            paths.Add(subtitleFolder);
        }

        paths.Add(GetTrickplayDirectory(item, false));
        if (!string.IsNullOrEmpty(item.Path))
        {
            paths.Add(GetTrickplayDirectory(item, true));
        }

        paths.Add(GetChapterImageFolderPath(item));

        return paths;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejecting attachment filename '{FileName}' for MediaSource {MediaSourceId}: not a valid leaf name.")]
    private partial void LogRejectedAttachmentFileName(string fileName, string mediaSourceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MediaSource Id '{MediaSourceId}' is not a GUID; no on-disk attachment folder.")]
    private partial void LogNoAttachmentFolder(string mediaSourceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MediaSource Id '{MediaSourceId}' is not a GUID; no on-disk subtitle folder.")]
    private partial void LogNoSubtitleFolder(string mediaSourceId);
}
