using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DalamudRepoBrowser;

internal sealed class DalamudRepoSettingsAccessor
{
    private readonly IPluginLog log;
    private bool initialized;

    private Type? serviceType;
    private Type? thirdPartyRepoSettingsType;
    private object? dalamudPluginManager;
    private object? dalamudConfig;
    private PropertyInfo? dalamudRepoSettingsProperty;
    private MethodInfo? pluginReload;
    private MethodInfo? configSave;

    public DalamudRepoSettingsAccessor(IPluginLog log)
    {
        this.log = log;
        CurrentApiLevel = typeof(IDalamudPluginInterface).Assembly.GetName().Version?.Major ?? 0;
    }

    public int CurrentApiLevel { get; }

    public bool HasRepo(string url)
    {
        return GetRepoSettings(url) != null;
    }

    public bool GetRepoEnabled(string url)
    {
        return GetRepoSettings(url) is { IsEnabled: true };
    }

    public void ToggleRepo(string url)
    {
        try
        {
            var repo = GetRepoSettings(url);
            if (repo != null)
            {
                repo.IsEnabled = !repo.IsEnabled;
                SaveAndReload();
                return;
            }

            if (AddRepo(url))
            {
                SaveAndReload();
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed toggling repository.");
        }
    }

    public int SetReposEnabled(IEnumerable<string> urls, bool enabled)
    {
        var changed = 0;

        try
        {
            foreach (var url in urls)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var repo = GetRepoSettings(url);
                if (repo != null)
                {
                    if (repo.IsEnabled == enabled)
                    {
                        continue;
                    }

                    repo.IsEnabled = enabled;
                    changed++;
                    continue;
                }

                if (enabled && AddRepo(url))
                {
                    changed++;
                }
            }

            if (changed > 0)
            {
                SaveAndReload();
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed updating repositories.");
        }

        return changed;
    }

    public int DisableFailedRepos()
    {
        var changed = 0;

        try
        {
            if (!EnsureInitialized())
            {
                return 0;
            }

            var reposProperty = dalamudPluginManager?.GetType()
                .GetProperty("Repos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (reposProperty?.GetValue(dalamudPluginManager) is not IEnumerable repos)
            {
                return 0;
            }

            foreach (var obj in repos)
            {
                var type = obj.GetType();
                var state = type.GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(obj)
                    ?.ToString();
                if (!string.Equals(state, "Fail", StringComparison.Ordinal))
                {
                    continue;
                }

                var isThirdParty = (bool?)type.GetProperty("IsThirdParty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(obj) ?? false;
                var isEnabled = (bool?)type.GetProperty("IsEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(obj) ?? false;
                if (!isThirdParty || !isEnabled)
                {
                    continue;
                }

                var url = (string?)type.GetProperty("PluginMasterUrl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(obj);
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var settings = GetRepoSettings(url);
                if (settings is not { IsEnabled: true })
                {
                    continue;
                }

                settings.IsEnabled = false;
                changed++;
            }

            if (changed > 0)
            {
                SaveAndReload();
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed disabling failed repositories.");
        }

        return changed;
    }

    public List<string> GetDisabledRepoUrls()
    {
        var urls = new List<string>();

        try
        {
            var repoSettings = GetRepoSettingsList();
            if (repoSettings == null)
            {
                return urls;
            }

            foreach (var obj in repoSettings)
            {
                var settings = new RepoSettings(obj);
                if (!settings.IsEnabled && !string.IsNullOrWhiteSpace(settings.Url))
                {
                    urls.Add(settings.Url);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed reading disabled repositories.");
        }

        return urls;
    }

    public bool SortReposByStateAndUrl()
    {
        try
        {
            var repoSettings = GetRepoSettingsList();
            if (repoSettings == null)
            {
                return false;
            }

            var objects = repoSettings.Cast<object>().ToList();
            var sorted = objects
                .Select(obj => new { Object = obj, Settings = new RepoSettings(obj) })
                .OrderByDescending(entry => entry.Settings.IsEnabled)
                .ThenBy(entry => entry.Settings.Url, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.Object)
                .ToList();

            if (objects.SequenceEqual(sorted))
            {
                return false;
            }

            var listType = repoSettings.GetType();
            var clear = listType.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public);
            var add = listType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
            if (clear == null || add == null)
            {
                return false;
            }

            clear.Invoke(repoSettings, null);
            foreach (var obj in sorted)
            {
                add.Invoke(repoSettings, new[] { obj });
            }

            SaveAndReload();
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed sorting repositories.");
            return false;
        }
    }

    private RepoSettings? GetRepoSettings(string url)
    {
        var repoSettings = GetRepoSettingsList();
        if (repoSettings == null)
        {
            return null;
        }

        foreach (var obj in repoSettings)
        {
            var settings = new RepoSettings(obj);
            if (settings.Url == url)
            {
                return settings;
            }
        }

        return null;
    }

    private IEnumerable? GetRepoSettingsList()
    {
        if (!EnsureInitialized())
        {
            return null;
        }

        return (IEnumerable?)dalamudRepoSettingsProperty?.GetValue(dalamudConfig);
    }

    private bool AddRepo(string url)
    {
        if (!EnsureInitialized())
        {
            return false;
        }

        var repoSettings = GetRepoSettingsList();
        if (repoSettings == null)
        {
            return false;
        }

        var add = repoSettings.GetType().GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
        if (add == null || thirdPartyRepoSettingsType == null)
        {
            return false;
        }

        var obj = Activator.CreateInstance(thirdPartyRepoSettingsType);
        if (obj == null)
        {
            return false;
        }

        _ = new RepoSettings(obj)
        {
            Url = url,
            IsEnabled = true
        };

        add.Invoke(repoSettings, new[] { obj });
        return true;
    }

    private bool EnsureInitialized()
    {
        if (initialized)
        {
            return true;
        }

        try
        {
            var dalamudAssembly = typeof(IDalamudPluginInterface).Assembly;
            serviceType = dalamudAssembly.GetType("Dalamud.Service`1");
            thirdPartyRepoSettingsType = dalamudAssembly.GetType("Dalamud.Configuration.ThirdPartyRepoSettings");

            if (serviceType == null || thirdPartyRepoSettingsType == null)
            {
                log.Error("Failed to locate Dalamud service types.");
                return false;
            }

            dalamudPluginManager = GetService("Dalamud.Plugin.Internal.PluginManager");
            dalamudConfig = GetService("Dalamud.Configuration.Internal.DalamudConfiguration");

            if (dalamudPluginManager == null || dalamudConfig == null)
            {
                log.Error("Failed to locate Dalamud services.");
                return false;
            }

            dalamudRepoSettingsProperty = dalamudConfig.GetType()
                .GetProperty("ThirdRepoList", BindingFlags.Instance | BindingFlags.Public);

            pluginReload = dalamudPluginManager.GetType()
                .GetMethod("SetPluginReposFromConfigAsync", BindingFlags.Instance | BindingFlags.Public);

            configSave = dalamudConfig.GetType()
                .GetMethod("QueueSave", BindingFlags.Instance | BindingFlags.Public);

            if (dalamudRepoSettingsProperty == null || pluginReload == null || configSave == null)
            {
                log.Error("Failed to bind Dalamud configuration accessors.");
                return false;
            }

            initialized = true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to initialize Dalamud repo settings accessor.");
        }

        return initialized;
    }

    private object? GetService(string typeName)
    {
        if (serviceType == null)
        {
            return null;
        }

        var getType = serviceType.Assembly.GetType(typeName);
        if (getType == null)
        {
            return null;
        }

        var getService = serviceType.MakeGenericType(getType).GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
        return getService?.Invoke(null, null);
    }

    private void SaveAndReload()
    {
        configSave?.Invoke(dalamudConfig, null);
        pluginReload?.Invoke(dalamudPluginManager, new object[] { true });
    }
}
