using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace DalamudRepoBrowser;

internal sealed class RepoManager : IDisposable
{
    private const long CacheTtlMilliseconds = 21600000; // 6 hours

    public const string RepoMasterUrl = "https://raw.githubusercontent.com/BeSlightly/Aetherfeed/refs/heads/main/public/data/plugins.json";
    public const string PriorityReposUrl = "https://raw.githubusercontent.com/BeSlightly/Aetherfeed/refs/heads/main/public/data/priority-repos.json";
    private const string RepoMasterLastUpdatedUrl = "https://raw.githubusercontent.com/BeSlightly/Aetherfeed/refs/heads/main/public/data/last-updated.json";

    private readonly Configuration config;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly DalamudRepoSettingsAccessor repoSettingsAccessor;
    private readonly HttpClient httpClient;
    private readonly HashSet<string> fetchedRepos = new();
    private readonly object fetchedReposLock = new();

    private IReadOnlyList<RepoInfo> repoList = Array.Empty<RepoInfo>();
    private int fetchId;
    private int sortCountdown;

    public RepoManager(Configuration config, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.config = config;
        this.pluginInterface = pluginInterface;
        this.log = log;
        repoSettingsAccessor = new DalamudRepoSettingsAccessor(log);

        httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });

        httpClient.DefaultRequestHeaders.Add("Application-Name", "DalamudRepoBrowser");
        httpClient.DefaultRequestHeaders.Add(
            "Application-Version",
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "1.0.0.0");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DalamudRepoBrowser/1.0");
    }

    public int CurrentApiLevel => repoSettingsAccessor.CurrentApiLevel;
    public IReadOnlyList<RepoInfo> RepoList => repoList;
    public int SortCountdown => Volatile.Read(ref sortCountdown);

    public void RequestSort()
    {
        Interlocked.Exchange(ref sortCountdown, 1);
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    public bool HasRepo(string url) => repoSettingsAccessor.HasRepo(url);

    public bool GetRepoEnabled(string url) => repoSettingsAccessor.GetRepoEnabled(url);

    public void ToggleRepo(string url) => repoSettingsAccessor.ToggleRepo(url);

    public int SetReposEnabled(IEnumerable<RepoInfo> repos, bool enabled)
    {
        var urls = repos.Select(GetConfiguredRepoUrl).Distinct(StringComparer.Ordinal).ToList();
        return repoSettingsAccessor.SetReposEnabled(urls, enabled);
    }

    public int DisableFailedRepos() => repoSettingsAccessor.DisableFailedRepos();

    private string GetConfiguredRepoUrl(RepoInfo repo)
    {
        if (!string.IsNullOrEmpty(repo.RawUrl) && repoSettingsAccessor.HasRepo(repo.RawUrl))
        {
            return repo.RawUrl;
        }

        return repo.Url;
    }

    public bool TryConsumeSortCountdown()
    {
        if (Volatile.Read(ref sortCountdown) <= 0)
        {
            return false;
        }

        var next = Interlocked.Decrement(ref sortCountdown);
        if (next > 0)
        {
            return false;
        }

        Interlocked.Exchange(ref sortCountdown, 0);
        return true;
    }

    public HashSet<RepoInfo> SortAndUpdateSeen(HashSet<string> prevSeenRepos)
    {
        var currentRepos = RepoList;

        var sorted = config.RepoSort switch
        {
            0 => currentRepos,
            1 => currentRepos.OrderBy(repo => repo.Owner).ToList(),
            2 => currentRepos.OrderBy(repo => repo.Url).ToList(),
            3 => currentRepos.OrderByDescending(repo => repo.Plugins.Count).ToList(),
            4 => currentRepos.OrderByDescending(repo => repo.LastUpdated).ToList(),
            _ => currentRepos
        };

        var enabledRepos = new HashSet<RepoInfo>();
        foreach (var repo in sorted)
        {
            if (GetRepoEnabled(repo.Url) || GetRepoEnabled(repo.RawUrl))
            {
                enabledRepos.Add(repo);
            }

            config.SeenRepos.Add(repo.Url);
        }

        repoList = sorted.OrderBy(repo => prevSeenRepos.Contains(repo.Url)).ToList();
        config.Save();

        return enabledRepos;
    }

    public void FetchRepoMasters()
    {
        log.Debug("Manual refetch triggered - clearing caches and fetching fresh data");

        Interlocked.Increment(ref fetchId);
        repoList = Array.Empty<RepoInfo>();

        lock (fetchedReposLock)
        {
            fetchedRepos.Clear();
        }

        config.LastUpdatedPriorityRepos = 0;
        config.LastUpdatedRepoList = 0;
        config.Save();

        log.Debug("Cache timestamps cleared, forcing fresh fetch from remote sources");
        
        _ = Task.Run(async () =>
        {
            var pTask = FetchPriorityReposAsync();
            var rTask = FetchRepoListAsync(RepoMasterUrl);
            await Task.WhenAll(pTask, rTask).ConfigureAwait(false);
            
            var pUpdated = pTask.Result;
            var rUpdated = rTask.Result;
            
            if (pUpdated || rUpdated)
            {
                Plugin.NotificationManager.AddNotification(new Notification
                {
                    Content = "Repository cache updated.",
                    Title = "Repository Browser",
                    Type = NotificationType.Success
                });
            }
        });
    }

    private bool ShouldCheckRepoList()
    {
        try
        {
            var filePath = GetReposFilePath();
            if (!File.Exists(filePath) || config.LastUpdatedRepoList == 0)
            {
                return true;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return now >= config.LastUpdatedRepoList + CacheTtlMilliseconds;
        }
        catch
        {
            return true;
        }
    }

    private bool ShouldCheckPriorityReposList()
    {
        try
        {
            var filePath = GetPriorityReposFilePath();
            if (!File.Exists(filePath) || config.LastUpdatedPriorityRepos == 0)
            {
                return true;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return now >= config.LastUpdatedPriorityRepos + CacheTtlMilliseconds;
        }
        catch
        {
            return true;
        }
    }

    private string GetReposFilePath()
    {
        return Path.Combine(pluginInterface.ConfigDirectory.FullName, "repos.json");
    }

    private string GetPriorityReposFilePath()
    {
        return Path.Combine(pluginInterface.ConfigDirectory.FullName, "priority-repos.json");
    }

    private async Task<bool> FetchPriorityReposAsync()
    {
        log.Debug($"Fetching priority repositories from {PriorityReposUrl}");
        var updated = false;

        try
        {
            string data;
            if (ShouldCheckPriorityReposList())
            {
                log.Debug("Retrieving latest priority repos data from master api.");
                data = await httpClient.GetStringAsync(PriorityReposUrl).ConfigureAwait(false);
                await File.WriteAllTextAsync(GetPriorityReposFilePath(), data).ConfigureAwait(false);
                config.LastUpdatedPriorityRepos = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                config.Save();
                updated = true;
            }
            else
            {
                log.Debug("Using cached data for priority repos list.");
                data = await File.ReadAllTextAsync(GetPriorityReposFilePath()).ConfigureAwait(false);
            }

            var priorityRepos = JArray.Parse(data);
            config.PriorityRepos.Clear();

            foreach (var repo in priorityRepos)
            {
                var repoUrl = repo.ToString();
                if (!string.IsNullOrEmpty(repoUrl))
                {
                    config.PriorityRepos.Add(repoUrl);
                }
            }

            config.Save();
            log.Debug($"Loaded {config.PriorityRepos.Count} priority repositories");
            return updated;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed loading priority repositories from {PriorityReposUrl}");
            return false;
        }
    }

    private async Task<bool> FetchRepoListAsync(string repoMaster)
    {
        log.Debug($"Fetching repositories from {repoMaster}");
        var updated = false;

        var startedFetch = Volatile.Read(ref fetchId);
        try
        {
            string data;
            if (ShouldCheckRepoList())
            {
                log.Debug("Retrieving latest data from repo master api.");
                using var response = await httpClient.GetAsync(repoMaster).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                data = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                await File.WriteAllTextAsync(GetReposFilePath(), data).ConfigureAwait(false);
                config.LastUpdatedRepoList = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                config.Save();
                updated = true;
            }
            else
            {
                log.Debug("Using cached data for repo list.");
                data = await File.ReadAllTextAsync(GetReposFilePath()).ConfigureAwait(false);
            }

            await UpdateRepoMasterTimestampAsync().ConfigureAwait(false);

            var repos = JArray.Parse(data);

            if (Volatile.Read(ref fetchId) != startedFetch)
            {
                return updated;
            }

            log.Debug($"Fetched {repos.Count} repositories from {repoMaster}");

            var tempRepoList = new List<RepoInfo>();
            foreach (var json in repos)
            {
                RepoInfo info;

                try
                {
                    var repoUrl = (string?)json["repo_url"] ?? string.Empty;
                    var rawUrl = RepoUrlHelper.GetRawUrl(repoUrl);
                    info = new RepoInfo(json, rawUrl);
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"Failed parsing {(string?)json["repo_url"]}.");
                    continue;
                }

                lock (fetchedReposLock)
                {
                    if (!fetchedRepos.Add(info.Url))
                    {
                        log.Error($"{info.Url} has already been fetched");
                        continue;
                    }
                }

                if (info.Plugins.Count == 0)
                {
                    log.Debug($"{info.Url} contains no usable plugins!");
                    continue;
                }

                tempRepoList.Add(info);
            }

            log.Debug($"Applying plugin deduplication with {config.PriorityRepos.Count} priority repos");
            var deduplicatedRepos = ApplyPluginDeduplication(tempRepoList);
            log.Debug($"Deduplication complete: {tempRepoList.Count} -> {deduplicatedRepos.Count} repos");

            repoList = deduplicatedRepos;
            Interlocked.Exchange(ref sortCountdown, 60);
            return updated;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed loading repositories from {repoMaster}");
            return false;
        }
    }

    private List<RepoInfo> ApplyPluginDeduplication(List<RepoInfo> repos)
    {
        log.Debug($"Starting deduplication with {repos.Count} repos");

        var pluginGroups = new Dictionary<string, List<(RepoInfo repo, PluginInfo plugin)>>();
        foreach (var repo in repos)
        {
            foreach (var plugin in repo.Plugins)
            {
                var pluginKey = !string.IsNullOrEmpty(plugin.InternalName) ? plugin.InternalName : plugin.Name;
                if (string.IsNullOrEmpty(pluginKey))
                {
                    continue;
                }

                if (!pluginGroups.TryGetValue(pluginKey, out var list))
                {
                    list = new List<(RepoInfo repo, PluginInfo plugin)>();
                    pluginGroups[pluginKey] = list;
                }

                list.Add((repo, plugin));
            }
        }

        log.Debug($"Found {pluginGroups.Count} unique plugins across all repos");

        var selectedByRepo = new Dictionary<string, HashSet<string>>();

        foreach (var pluginGroup in pluginGroups)
        {
            var occurrencesByDeveloper = new Dictionary<string, List<(RepoInfo repo, PluginInfo plugin)>>();
            foreach (var occurrence in pluginGroup.Value)
            {
                var developer = !string.IsNullOrEmpty(occurrence.repo.Owner)
                    ? occurrence.repo.Owner
                    : (!string.IsNullOrEmpty(occurrence.plugin.Author)
                        ? occurrence.plugin.Author
                        : "Unknown Developer");

                if (!occurrencesByDeveloper.TryGetValue(developer, out var list))
                {
                    list = new List<(RepoInfo repo, PluginInfo plugin)>();
                    occurrencesByDeveloper[developer] = list;
                }

                list.Add(occurrence);
            }

            var deduplicatedOccurrences = new List<(RepoInfo repo, PluginInfo plugin)>();
            foreach (var developerGroup in occurrencesByDeveloper.Values)
            {
                var hasPriorityOccurrence = developerGroup.Any(occurrence =>
                    config.PriorityRepos.Contains(occurrence.repo.Url));
                var candidates = hasPriorityOccurrence
                    ? developerGroup.Where(occurrence => config.PriorityRepos.Contains(occurrence.repo.Url)).ToList()
                    : developerGroup;
                deduplicatedOccurrences.Add(GetBestCandidate(candidates));
            }

            var priorityCandidates = deduplicatedOccurrences
                .Where(occurrence => config.PriorityRepos.Contains(occurrence.repo.Url))
                .ToList();

            var chosenOccurrences = priorityCandidates.Count > 0
                ? new List<(RepoInfo repo, PluginInfo plugin)> { GetBestCandidate(priorityCandidates) }
                : deduplicatedOccurrences;

            foreach (var occurrence in chosenOccurrences)
            {
                if (!selectedByRepo.TryGetValue(occurrence.repo.Url, out var pluginKeys))
                {
                    pluginKeys = new HashSet<string>();
                    selectedByRepo[occurrence.repo.Url] = pluginKeys;
                }

                pluginKeys.Add(pluginGroup.Key);
            }
        }

        var result = new List<RepoInfo>();
        foreach (var repo in repos)
        {
            if (!selectedByRepo.TryGetValue(repo.Url, out var pluginKeys))
            {
                continue;
            }

            var filteredPlugins = repo.Plugins
                .Where(plugin =>
                {
                    var pluginKey = !string.IsNullOrEmpty(plugin.InternalName) ? plugin.InternalName : plugin.Name;
                    return !string.IsNullOrEmpty(pluginKey) && pluginKeys.Contains(pluginKey);
                })
                .ToList();

            if (filteredPlugins.Count == 0)
            {
                continue;
            }

            result.Add(new RepoInfo(repo, filteredPlugins));
        }

        log.Debug($"Deduplication complete: {repos.Count} -> {result.Count} repos");

        return result;
    }

    private static (RepoInfo repo, PluginInfo plugin) GetBestCandidate(List<(RepoInfo repo, PluginInfo plugin)> candidates)
    {
        return candidates
            .OrderByDescending(c => c.plugin.ApiLevel)
            .ThenByDescending(c => c.plugin.LastUpdate)
            .First();
    }

    private async Task UpdateRepoMasterTimestampAsync()
    {
        try
        {
            using var response = await httpClient.GetAsync(RepoMasterLastUpdatedUrl).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var json = JObject.Parse(payload);
            var unix = (long?)json["unix"];
            var nextUnix = (long?)json["next_unix"];
            var updated = false;

            if (unix.HasValue)
            {
                config.LastRemoteRepoListUpdatedUtc = unix.Value;
                updated = true;
            }

            if (nextUnix.HasValue)
            {
                config.NextRemoteRepoListUpdatedUtc = nextUnix.Value;
                updated = true;
            }

            if (updated)
            {
                config.Save();
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Failed to fetch repo master update metadata.");
        }
    }
}
