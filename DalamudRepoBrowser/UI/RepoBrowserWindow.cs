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
using Dalamud.Interface.Windowing;

namespace DalamudRepoBrowser;

internal sealed partial class RepoBrowserWindow : Window, IDisposable
{
    private const string SourceUrl = "https://beslightly.github.io/Aetherfeed/";


    private readonly RepoManager repoManager;
    private readonly Configuration config;
    private readonly HashSet<string> prevSeenRepos;

    private bool openSettings;
    private bool firstOpen = true;
    private HashSet<RepoInfo> enabledRepos = new();
    private HashSet<RepoInfo> searchResults = new();
    private string searchText = string.Empty;
    private uint filteredCount;
    private DateTimeOffset uiOpenedAt;
    private bool enabledReposInitialized;
    private bool checkingDisabledRepos;
    private IReadOnlyList<RepoInfo>? enabledReposSource;
    private DateTimeOffset lastEnabledRefresh = DateTimeOffset.MinValue;
    private const int EnabledRefreshIntervalMs = 1500;
    private IReadOnlyList<RepoInfo>? modernCacheRepos;
    private bool modernCacheValid;
    private bool modernCacheShowOutdated;
    private bool modernCacheHideNonEnglish;
    private bool modernCacheHideClosedSource;
    private int modernCacheApiLevel;
    private DateTimeOffset modernCacheUiOpenedAt;
    private float modernTextScale = -1f;
    private readonly Dictionary<RepoInfo, ModernRepoCache> modernRepoCache = new();
    private readonly Dictionary<PluginInfo, float> pluginTextWidthCache = new();

    private string lastCopiedUrl = string.Empty;
    private DateTime lastCopiedTime = DateTime.MinValue;

    private const string ModernHeaderTitle = "Aetherfeed Browser";
    private const string ModernHeaderSubtitle = "Discover repositories automatically scraped from GitHub";

    public RepoBrowserWindow(RepoManager repoManager, Configuration config)
        : base("Repository Browser")
    {
        this.repoManager = repoManager;
        this.config = config;
        prevSeenRepos = config.SeenRepos.ToHashSet();

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(830, 570),
            MaximumSize = new Vector2(9999)
        };

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.SyncAlt,
            IconOffset = new Vector2(1, 1),
            Click = _ => repoManager.FetchRepoMasters(),
            ShowTooltip = () => ImGui.SetTooltip("Refresh repositories")
        });
    }

    public void Dispose()
    {
    }

    public void OpenSettings()
    {
        IsOpen = true;
        openSettings = true;
    }

    public override void Draw()
    {
        if (ImGui.IsWindowAppearing() || uiOpenedAt == default)
        {
            uiOpenedAt = DateTimeOffset.Now;
        }

        if (repoManager.TryConsumeSortCountdown())
        {
            enabledRepos = repoManager.SortAndUpdateSeen(prevSeenRepos);
            enabledReposInitialized = true;
            enabledReposSource = repoManager.RepoList;
        }

        if (firstOpen)
        {
            repoManager.FetchRepoMasters();
            firstOpen = false;
        }

        var repos = repoManager.RepoList;
        if (enabledReposInitialized && !ReferenceEquals(enabledReposSource, repos))
        {
            enabledReposInitialized = false;
            enabledReposSource = null;
            enabledRepos.Clear();
        }

        if (!ReferenceEquals(modernCacheRepos, repos))
        {
            modernCacheValid = false;
        }

        MaybeRefreshEnabledRepos(repos);

        if (config.UseModernUi)
        {
            DrawModern(repos);
        }
        else
        {
            DrawClassic(repos);
        }
    }
}
