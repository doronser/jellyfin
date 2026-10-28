#nullable disable

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Naming.Common;
using Jellyfin.Naming.TV;
using Jellyfin.Server.Implementations.Library;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Library.Resolvers.TV;

/// <summary>
/// Class SeasonResolver.
/// </summary>
/// <param name="namingOptions">The naming options.</param>
/// <param name="localization">The localization.</param>
/// <param name="logger">The logger.</param>
public partial class SeasonResolver(
    NamingOptions namingOptions,
    ILocalizationManager localization,
    ILogger<SeasonResolver> logger) : GenericFolderResolver<Season>
{
    /// <summary>
    /// Resolves the specified args.
    /// </summary>
    /// <param name="args">The args.</param>
    /// <returns>Season.</returns>
    protected override Season Resolve(ItemResolveArgs args)
    {
        if (args.Parent is Series series && args.IsDirectory)
        {
            var path = args.Path;

            var seasonParserResult = SeasonPathParser.Parse(path, series.ContainingFolderPath, true, true);

            var season = new Season
            {
                IndexNumber = seasonParserResult.SeasonNumber,
                SeriesId = series.Id,
                SeriesName = series.Name,
                Path = seasonParserResult.IsSeasonFolder ? path : null
            };

            if (!season.IndexNumber.HasValue || !seasonParserResult.IsSeasonFolder)
            {
                var resolver = new Jellyfin.Naming.TV.EpisodeResolver(namingOptions);

                var folderName = System.IO.Path.GetFileName(path);
                var testPath = @"\\test\" + folderName;

                var episodeInfo = resolver.Resolve(testPath, true);

                if (episodeInfo?.EpisodeNumber is not null && episodeInfo.SeasonNumber.HasValue)
                {
                    LogFolderWithEpisodeNumber(path, episodeInfo.SeasonNumber.Value, episodeInfo.EpisodeNumber.Value);

                    return null;
                }

                var hasAnyVideo = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Any(file => namingOptions.VideoFileExtensions.Contains(Path.GetExtension(file)));

                if (!hasAnyVideo)
                {
                    return null;
                }
            }

            if (season.IndexNumber.HasValue && string.IsNullOrEmpty(season.Name))
            {
                var seasonNumber = season.IndexNumber.Value;
                season.Name = seasonNumber == 0 ?
                    args.LibraryOptions.SeasonZeroDisplayName :
                    string.Format(
                        CultureInfo.InvariantCulture,
                        localization.GetServerLocalizedString("NameSeasonNumber"),
                        seasonNumber,
                        args.LibraryOptions.PreferredMetadataLanguage);
            }

            SetProviderIdFromPath(season, path);

            return season;
        }

        return null;
    }

    /// <summary>
    /// Sets provider ids from the season folder name.
    /// </summary>
    /// <param name="item">The season.</param>
    /// <param name="path">The season folder path.</param>
    private static void SetProviderIdFromPath(Season item, string path)
    {
        var justName = Path.GetFileName(path.AsSpan());

        var tvdbId = justName.GetAttributeValue("tvdbid");
        item.TrySetProviderId(MetadataProvider.Tvdb, tvdbId);

        var tvmazeId = justName.GetAttributeValue("tvmazeid");
        item.TrySetProviderId(MetadataProvider.TvMaze, tvmazeId);

        var tmdbId = justName.GetAttributeValue("tmdbid");
        item.TrySetProviderId(MetadataProvider.Tmdb, tmdbId);

        // Anime databases model a single cour as its own entry, so a multi-season
        // series maps to one of these ids per season rather than one per series.
        var anidbId = justName.GetAttributeValue("anidbid");
        item.TrySetProviderId("AniDB", anidbId);

        var aniListId = justName.GetAttributeValue("anilistid");
        item.TrySetProviderId("AniList", aniListId);

        var aniSearchId = justName.GetAttributeValue("anisearchid");
        item.TrySetProviderId("AniSearch", aniSearchId);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Found folder underneath series with episode number: {Path}. Season {SeasonNumber}. Episode {EpisodeNumber}")]
    private partial void LogFolderWithEpisodeNumber(string path, int seasonNumber, int episodeNumber);
}
