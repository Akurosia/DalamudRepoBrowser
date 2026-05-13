using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace DalamudRepoBrowser;

internal sealed partial class RepoBrowserWindow
{
    private void MaybeRefreshEnabledRepos(IReadOnlyList<RepoInfo> repos)
    {
        if (repos.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if ((now - lastEnabledRefresh).TotalMilliseconds < EnabledRefreshIntervalMs)
        {
            return;
        }

        enabledRepos.Clear();
        foreach (var repo in repos)
        {
            if (repoManager.GetRepoEnabled(repo.Url) || repoManager.GetRepoEnabled(repo.RawUrl))
            {
                enabledRepos.Add(repo);
            }
        }

        enabledReposInitialized = true;
        enabledReposSource = repos;
        lastEnabledRefresh = now;
    }

    private bool PluginPassesLanguageFilter(PluginInfo plugin)
    {
        return !config.HideNonEnglishPlugins || IsLatinOnly(plugin);
    }

    private bool IsPluginCurrentOrUnknown(PluginInfo plugin)
    {
        return plugin.ApiLevel == 0 || plugin.ApiLevel == repoManager.CurrentApiLevel;
    }

    private bool PluginPassesClosedSourceFilter(PluginInfo plugin)
    {
        return !config.HideClosedSourcePlugins || !plugin.IsClosedSource;
    }

    private List<RepoInfo> GetVisibleRepos(IReadOnlyList<RepoInfo> repos)
    {
        var visibleRepos = new List<RepoInfo>();
        foreach (var repo in repos)
        {
            if (config.MaxPlugins < 50 && config.MaxPlugins < repo.Plugins.Count)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(searchText) && !searchResults.Contains(repo))
            {
                continue;
            }

            var enabled = enabledReposInitialized
                ? enabledRepos.Contains(repo)
                : repoManager.GetRepoEnabled(repo.Url) || repoManager.GetRepoEnabled(repo.RawUrl);
            if (enabled && config.HideEnabledRepos)
            {
                continue;
            }

            if (!repo.Plugins.Any(plugin => (config.ShowOutdatedPlugins || IsPluginCurrentOrUnknown(plugin))
                                           && PluginPassesLanguageFilter(plugin)
                                           && PluginPassesClosedSourceFilter(plugin)))
            {
                continue;
            }

            visibleRepos.Add(repo);
        }

        return visibleRepos;
    }

    private void SetVisibleReposEnabled(IReadOnlyList<RepoInfo> repos, bool enabled)
    {
        var visibleRepos = GetVisibleRepos(repos);
        var changed = repoManager.SetReposEnabled(visibleRepos, enabled);

        foreach (var repo in visibleRepos)
        {
            if (enabled)
            {
                enabledRepos.Add(repo);
            }
            else
            {
                enabledRepos.Remove(repo);
            }
        }

        enabledReposInitialized = true;
        enabledReposSource = repos;
        lastEnabledRefresh = DateTimeOffset.Now;

        Plugin.NotificationManager.AddNotification(new Notification
        {
            Content = changed == 0
                ? "No visible repositories needed changes."
                : $"{(enabled ? "Enabled" : "Disabled")} {changed} visible repositories.",
            Title = "Repository Browser",
            Type = changed == 0 ? NotificationType.Info : NotificationType.Success
        });
    }

    private void DisableFailedRepos()
    {
        var changed = repoManager.DisableFailedRepos();
        enabledReposInitialized = false;
        enabledReposSource = null;
        enabledRepos.Clear();

        Plugin.NotificationManager.AddNotification(new Notification
        {
            Content = changed == 0
                ? "Dalamud has no enabled failed repositories to disable."
                : $"Disabled {changed} repositories marked failed by Dalamud.",
            Title = "Repository Browser",
            Type = changed == 0 ? NotificationType.Info : NotificationType.Success
        });
    }

    private string GetRemoteUpdateStatusText()
    {
        if (config.LastRemoteRepoListUpdatedUtc <= 0)
        {
            return "Aetherfeed updated: unknown";
        }

        var remoteUpdated = DateTimeOffset.FromUnixTimeSeconds(config.LastRemoteRepoListUpdatedUtc).ToLocalTime();
        var nextUpdate = config.NextRemoteRepoListUpdatedUtc > 0
            ? DateTimeOffset.FromUnixTimeSeconds(config.NextRemoteRepoListUpdatedUtc).ToLocalTime()
            : remoteUpdated.AddHours(6);
        var now = uiOpenedAt;
        var nextText = GetApproximateNextUpdateText(nextUpdate, now);
        return $"Aetherfeed updated {remoteUpdated:MMM dd, yyyy HH:mm} • {nextText}";
    }

    private static string GetApproximateNextUpdateText(DateTimeOffset nextUpdate, DateTimeOffset now)
    {
        var remaining = nextUpdate - now;
        if (remaining.TotalSeconds <= 0)
        {
            return "Next update soon";
        }

        if (remaining.TotalHours >= 24)
        {
            var days = (int)Math.Ceiling(remaining.TotalDays);
            return $"Next update in ~{days}d";
        }

        if (remaining.TotalHours >= 1)
        {
            var hours = (int)Math.Floor(remaining.TotalHours);
            var mins = remaining.Minutes;
            return mins > 0
                ? $"Next update in ~{hours}h {mins}m"
                : $"Next update in ~{hours}h";
        }

        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        return $"Next update in ~{minutes}m";
    }

    private static string GetRelativeTimeText(DateTimeOffset timestamp, DateTimeOffset reference)
    {
        if (reference == default)
        {
            reference = DateTimeOffset.Now;
        }

        var delta = reference - timestamp;
        if (delta.TotalSeconds < 60)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            var minutes = (int)Math.Floor(delta.TotalMinutes);
            return $"{minutes}m ago";
        }

        if (delta.TotalHours < 24)
        {
            var hours = (int)Math.Floor(delta.TotalHours);
            return $"{hours}h ago";
        }

        var days = (int)Math.Floor(delta.TotalDays);
        return $"{days}d ago";
    }

    private static bool IsLatinOnly(PluginInfo plugin)
    {
        return plugin.IsLatinOnly;
    }

    private static string GetSortLabel(int repoSort)
    {
        return repoSort switch
        {
            1 => "Owner",
            2 => "URL",
            3 => "# Plugins",
            4 => "Last Updated",
            _ => "Default"
        };
    }

    private static int LowerBound(IReadOnlyList<float> values, float target)
    {
        var lo = 0;
        var hi = values.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (values[mid] < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }


    private void UpdateSearchResults(IReadOnlyList<RepoInfo> repos)
    {
        searchResults = repos.Where(repo =>
            RepoMatchesSearch(repo, searchText, config.HideNonEnglishPlugins, config.HideClosedSourcePlugins)).ToHashSet();
    }

    private static void ShowPluginTooltip(PluginInfo plugin)
    {
        var scale = ImGuiHelpers.GlobalScale;

        using (ImRaii.Tooltip())
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(4 * scale, 8 * scale)))
        {

            // Header: Name
            ImGui.SetWindowFontScale(1.1f);
            ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), plugin.Name);
            ImGui.SetWindowFontScale(1f);

            // Punchline (Italic/Muted)
            if (!string.IsNullOrEmpty(plugin.Punchline))
            {
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 0.9f, 0.8f), plugin.Punchline);
            }

            if (plugin.IsClosedSource)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 0.7f, 0.2f, 0.95f)))
                {
                    ImGui.TextUnformatted("Closed Source");
                }
            }

            ImGui.Separator();

            // Description
            if (!string.IsNullOrEmpty(plugin.Description))
            {
                using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 25))
                {
                    ImGui.TextUnformatted(plugin.Description);
                }
            }

            // Footer: Metadata
            ImGui.Separator();

            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.6f, 1f)))
            {
                ImGui.TextUnformatted($"API Level: {plugin.ApiLevel}");

                if (!string.IsNullOrEmpty(plugin.RepoUrl))
                {
                    ImGui.TextUnformatted(plugin.RepoUrl);
                }
            }
        }
    }

    private static bool RepoMatchesSearch(
        RepoInfo repo,
        string searchValue,
        bool hideNonEnglishPlugins,
        bool hideClosedSourcePlugins)
    {
        if (string.IsNullOrEmpty(searchValue))
        {
            return false;
        }

        return repo.Url.Contains(searchValue, StringComparison.CurrentCultureIgnoreCase)
               || repo.FullName.Contains(searchValue, StringComparison.CurrentCultureIgnoreCase)
               || repo.Plugins.Any(plugin => PluginMatchesSearch(
                   plugin,
                   searchValue,
                   hideNonEnglishPlugins,
                   hideClosedSourcePlugins));
    }

    private static bool PluginMatchesSearch(
        PluginInfo plugin,
        string searchValue,
        bool hideNonEnglishPlugins,
        bool hideClosedSourcePlugins)
    {
        if (hideNonEnglishPlugins && !IsLatinOnly(plugin))
        {
            return false;
        }
        if (hideClosedSourcePlugins && plugin.IsClosedSource)
        {
            return false;
        }

        if (plugin.Name.Contains(searchValue, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        if (plugin.Punchline.Contains(searchValue, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        if (plugin.Description.Contains(searchValue, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        if (plugin.Tags.Any(tag => tag.Equals(searchValue, StringComparison.CurrentCultureIgnoreCase)))
        {
            return true;
        }

        return plugin.CategoryTags.Any(tag => tag.Equals(searchValue, StringComparison.CurrentCultureIgnoreCase));
    }

    private void CopyUrl(string url)
    {
        ImGui.SetClipboardText(url);
        lastCopiedUrl = url;
        lastCopiedTime = DateTime.Now;

        Plugin.NotificationManager.AddNotification(new Notification
        {
            Content = $"Copied to clipboard:\n{url}",
            Title = "Repository Browser",
            Type = NotificationType.Success
        });
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}
