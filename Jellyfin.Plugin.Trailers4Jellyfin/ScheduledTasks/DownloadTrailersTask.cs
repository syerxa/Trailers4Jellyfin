using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailers4Jellyfin.ScheduledTasks
{
    public class DownloadTrailersTask : IScheduledTask
    {
        private readonly ILogger<DownloadTrailersTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly TmdbService _tmdbService;
        private readonly TrailerDownloadService _downloadService;

        public string Name => "Download TMDB Trailers";
        public string Key => "Trailers4JellyfinDownload";
        public string Description => "Downloads trailers from TMDB for upcoming and recently released movies not in your library, for use with Jellyfin Cinema Mode.";
        public string Category => "Trailers4Jellyfin";

        public DownloadTrailersTask(
            ILogger<DownloadTrailersTask> logger,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            TmdbService tmdbService,
            TrailerDownloadService downloadService)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _tmdbService = tmdbService;
            _downloadService = downloadService;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks,
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                _logger.LogWarning("|Trailers4Jellyfin| No TMDB API key configured. Skipping task.");
                return;
            }

            if (string.IsNullOrWhiteSpace(config.DownloadFolder))
            {
                _logger.LogWarning("|Trailers4Jellyfin| No download folder configured. Skipping task.");
                return;
            }

            if (!config.SourceNowPlaying && !config.SourceUpcoming && !config.SourcePopular && !config.SourceTopRated)
            {
                _logger.LogWarning("|Trailers4Jellyfin| No TMDB sources selected. Enable at least one source. Skipping task.");
                return;
            }

            Directory.CreateDirectory(config.DownloadFolder);

            CleanupTrailers(config);

            progress.Report(5);

            var libraryTmdbIds = config.SkipMoviesInLibrary
                ? GetLibraryTmdbIds()
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("|Trailers4Jellyfin| Library contains {Count} movies with TMDB IDs (will skip these)", libraryTmdbIds.Count);

            progress.Report(10);

            var allowedLanguages = string.IsNullOrWhiteSpace(config.AllowedLanguages)
                ? null
                : new HashSet<string>(
                    config.AllowedLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase) as IReadOnlySet<string>;

            _logger.LogInformation("|Trailers4Jellyfin| Fetching candidates from TMDB...");
            var candidates = await _tmdbService.GetCandidateMoviesAsync(config, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("|Trailers4Jellyfin| Found {Count} candidate movies across all sources", candidates.Count);

            // Fetch genre ID→name map once for sidecar metadata.
            var genreMap = await _tmdbService.GetGenreMapAsync(config.TmdbApiKey, cancellationToken).ConfigureAwait(false);

            progress.Report(20);

            if (config.SkipMoviesInLibrary)
            {
                candidates = candidates
                    .Where(m => !libraryTmdbIds.Contains(m.Id.ToString()))
                    .ToList();
                _logger.LogInformation("|Trailers4Jellyfin| {Count} candidates remain after filtering library movies", candidates.Count);
            }

            if (candidates.Count == 0)
            {
                _logger.LogInformation("|Trailers4Jellyfin| No new candidates to download. All done.");
                progress.Report(100);
                return;
            }

            int downloaded = 0;
            int processed = 0;

            // Trailers that were deleted after being watched: don't re-download them (they would
            // otherwise come straight back while the movie is still in a current TMDB source).
            var watchedBlocklist = config.SkipPreviouslyWatched
                ? LoadWatchedBlocklist(config)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var movie in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (downloaded >= config.MaxTrailersToDownload)
                    break;

                double taskProgress = 20 + (80.0 * processed / candidates.Count);
                progress.Report(taskProgress);
                processed++;

                var outputPath = BuildOutputPath(movie.Title, movie.Year, config);

                if (config.SkipAlreadyDownloaded && File.Exists(outputPath))
                {
                    _logger.LogDebug("|Trailers4Jellyfin| Already downloaded: {Path}", outputPath);
                    continue;
                }

                if (config.SkipPreviouslyWatched && watchedBlocklist.Contains(Path.GetFileName(outputPath)))
                {
                    _logger.LogInformation(
                        "|Trailers4Jellyfin| Skipping previously-watched trailer (will not re-download): {Name}",
                        Path.GetFileName(outputPath));
                    continue;
                }

                var trailers = await _tmdbService.GetTrailersAsync(
                    movie.Id.ToString(), config.TmdbApiKey, allowedLanguages, cancellationToken).ConfigureAwait(false);

                if (trailers.Count == 0)
                {
                    _logger.LogDebug("|Trailers4Jellyfin| No YouTube trailers on TMDB for '{Title}'", movie.Title);
                    continue;
                }

                var trailer = trailers[0];
                _logger.LogInformation("|Trailers4Jellyfin| Downloading '{Trailer}' for '{Movie}'", trailer.Name, movie.Title);

                var success = await _downloadService.DownloadAsync(
                    trailer.Key,
                    outputPath,
                    config.PreferredVideoHeight,
                    config.YtDlpPath,
                    config.CookiesFilePath,
                    config.FfmpegPath,
                    cancellationToken).ConfigureAwait(false);

                if (success)
                {
                    downloaded++;
                    _logger.LogInformation(
                        "|Trailers4Jellyfin| [{Done}/{Max}] Saved trailer for '{Movie}' → {Path}",
                        downloaded, config.MaxTrailersToDownload, movie.Title, outputPath);

                    await SaveSidecarAsync(outputPath, movie.GenreIds, genreMap, cancellationToken).ConfigureAwait(false);
                }
            }

            _logger.LogInformation("|Trailers4Jellyfin| Task complete. Downloaded {Count} trailer(s).", downloaded);
            progress.Report(100);
        }

        private async Task SaveSidecarAsync(
            string trailerPath,
            IReadOnlyList<int> genreIds,
            Dictionary<int, string> genreMap,
            CancellationToken ct)
        {
            if (genreIds == null || genreIds.Count == 0) return;

            var genres = genreIds
                .Select(id => genreMap.TryGetValue(id, out var name) ? name : null)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            if (genres.Count == 0) return;

            var sidecarPath = Path.ChangeExtension(trailerPath, ".json");
            var json = JsonSerializer.Serialize(new { genres });
            await File.WriteAllTextAsync(sidecarPath, json, ct).ConfigureAwait(false);
        }

        private HashSet<string> GetLibraryTmdbIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var movies = _libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, Recursive = true })
                .OfType<Movie>();

            foreach (var movie in movies)
            {
                var tmdbId = movie.GetProviderId(MetadataProvider.Tmdb);
                if (!string.IsNullOrEmpty(tmdbId))
                    ids.Add(tmdbId);
            }

            return ids;
        }

        // GetUsers() replaced the Users property in Jellyfin 10.11.10. Try both via reflection
        // so the plugin works across patch versions without recompiling.
        private IList<User> GetAllUsers()
        {
            var type = _userManager.GetType();

            var method = type.GetMethod("GetUsers", Type.EmptyTypes);
            if (method != null)
                return ((IEnumerable<User>)method.Invoke(_userManager, null)!).ToList();

            var prop = type.GetProperty("Users");
            if (prop != null)
                return ((IEnumerable<User>)prop.GetValue(_userManager)!).ToList();

            throw new MissingMethodException("Neither GetUsers() nor Users found on IUserManager.");
        }

        private void CleanupTrailers(Configuration.PluginConfiguration config)
        {
            var downloadFolder = config.DownloadFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Build a lookup of library items by path for watched-status checks.
            // EnableUserData ensures each item's UserData rows are loaded with the query.
            var trailerItemsByPath = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    Recursive = true,
                    DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions { EnableUserData = true },
                })
                .Where(t => t.Path != null && t.Path.StartsWith(downloadFolder, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(t => t.Path!, StringComparer.OrdinalIgnoreCase);

            // Sweep up intermediates from a previously interrupted download. Cleanup runs before
            // any download in this task, so nothing here is in flight.
            foreach (var partial in Directory.EnumerateFiles(config.DownloadFolder)
                         .Where(TrailerDownloadService.IsPartialDownloadArtifact)
                         .ToList())
            {
                try
                {
                    _logger.LogInformation("|Trailers4Jellyfin| Removing leftover partial download: {File}", Path.GetFileName(partial));
                    File.Delete(partial);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "|Trailers4Jellyfin| Could not delete partial download {File}", Path.GetFileName(partial));
                }
            }

            var files = Directory.GetFiles(config.DownloadFolder, "*.mp4")
                .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal))
                .Where(f => !TrailerDownloadService.IsPartialDownloadArtifact(f))
                .ToList();

            // Users property was replaced with GetUsers() in Jellyfin 10.11.10+.
            IList<User>? users = null;
            try
            {
                users = GetAllUsers();
            }
            catch (MissingMethodException ex)
            {
                _logger.LogWarning(ex, "|Trailers4Jellyfin| Cannot enumerate users in this Jellyfin version — DeleteWatchedTrailers skipped, deleted trailers' play state not reset.");
            }

            // Delete watched trailers first.
            if (config.DeleteWatchedTrailers && users != null)
            {
                var watchedFiles = new List<string>();
                foreach (var file in files)
                {
                    if (!trailerItemsByPath.TryGetValue(file, out var item)) continue;

                    // Read the UserData rows loaded by our own GetItemList query rather than
                    // IUserDataManager.GetUserData: on Jellyfin 10.11 the latter serves a
                    // process-lifetime cache that can hold a stale "never played" record
                    // (created whenever some code path resolved the item without its user
                    // data loaded), making watched trailers invisible to this task.
                    bool watched = (item.UserData?.Any(d => d.Played) ?? false)
                        || users.Any(u => _userDataManager.GetUserData(u, item)?.Played ?? false);
                    if (watched) watchedFiles.Add(file);
                }

                // Remember these trailers so the download loop doesn't immediately re-fetch them
                // from a still-current TMDB source. Only watched-deletions are recorded. The list is
                // saved BEFORE deleting: if it can't be written, keep the files and retry next run
                // rather than delete trailers that would come straight back.
                var blocklist = LoadWatchedBlocklist(config);
                var blocklistChanged = false;
                foreach (var file in watchedFiles)
                {
                    if (blocklist.Add(Path.GetFileName(file)))
                        blocklistChanged = true;
                }

                if (!blocklistChanged || SaveWatchedBlocklist(config, blocklist))
                {
                    foreach (var file in watchedFiles)
                    {
                        _logger.LogInformation("|Trailers4Jellyfin| Deleting watched trailer: {File}", Path.GetFileName(file));
                        DeleteTrailer(file, trailerItemsByPath[file], users);
                        files.Remove(file);
                    }
                }
            }

            // Delete oldest trailers when over the cap.
            if (config.MaxTotalTrailers > 0 && files.Count > config.MaxTotalTrailers)
            {
                var toDelete = files
                    .OrderBy(File.GetCreationTime)
                    .Take(files.Count - config.MaxTotalTrailers)
                    .ToList();

                foreach (var file in toDelete)
                {
                    _logger.LogInformation("|Trailers4Jellyfin| Deleting oldest trailer to stay under cap: {File}", Path.GetFileName(file));
                    DeleteTrailer(file, trailerItemsByPath.GetValueOrDefault(file), users);
                }
            }
        }

        private void DeleteTrailer(string file, BaseItem? item, IList<User>? users)
        {
            if (item != null && users != null)
                ResetPlayState(item, users);

            File.Delete(file);
            var sidecar = Path.ChangeExtension(file, ".json");
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }

        // A trailer is identified as the movie it advertises, so it carries that movie's TMDB/IMDb
        // ids, and Jellyfin keys play state by those ids. When a file is deleted, Jellyfin parks its
        // play state and re-attaches it to the next item with the same ids — so without this reset,
        // a trailer watched as a pre-roll makes the real movie show as watched (or with a resume
        // point) once it is imported. Only users with play state are touched: MarkUnplayed writes
        // rows, and calling it for everyone would leave an empty record per user behind.
        private void ResetPlayState(BaseItem item, IList<User> users)
        {
            foreach (var user in users)
            {
                var cached = _userDataManager.GetUserData(user, item);
                bool hasState = (item.UserData?.Any(d => d.UserId.Equals(user.Id)
                        && (d.Played || d.PlayCount > 0 || d.PlaybackPositionTicks > 0)) ?? false)
                    || (cached != null && (cached.Played || cached.PlayCount > 0 || cached.PlaybackPositionTicks > 0));
                if (!hasState) continue;

                try
                {
                    item.MarkUnplayed(user);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "|Trailers4Jellyfin| Could not reset play state of '{Name}' for {User}; the movie may show as watched when imported.", item.Name, user.Username);
                }
            }
        }

        private string BuildOutputPath(string title, int? year, Configuration.PluginConfiguration config)
        {
            var safeName = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
            var yearPart = year.HasValue ? $" ({year.Value})" : string.Empty;
            return Path.Combine(config.DownloadFolder, $"{safeName}{yearPart}.mp4");
        }

        // Persistent record of trailer filenames that were deleted after being watched, so they are
        // not re-downloaded on later runs. Stored as a hidden file inside the download folder (which
        // is stable across restarts/updates); Jellyfin's library scan ignores dot-files and the
        // cleanup pass only globs *.mp4, so it is never treated as media.
        private static string BlocklistPath(Configuration.PluginConfiguration config)
            => Path.Combine(config.DownloadFolder, ".t4j-watched.json");

        private HashSet<string> LoadWatchedBlocklist(Configuration.PluginConfiguration config)
        {
            try
            {
                var path = BlocklistPath(config);
                if (!File.Exists(path))
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
                return new HashSet<string>(names ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "|Trailers4Jellyfin| Could not read watched-trailer blocklist; treating as empty.");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private bool SaveWatchedBlocklist(Configuration.PluginConfiguration config, HashSet<string> names)
        {
            try
            {
                File.WriteAllText(BlocklistPath(config), JsonSerializer.Serialize(names.ToList()));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "|Trailers4Jellyfin| Could not persist watched-trailer blocklist; keeping watched trailers until it can be saved.");
                return false;
            }
        }
    }
}
