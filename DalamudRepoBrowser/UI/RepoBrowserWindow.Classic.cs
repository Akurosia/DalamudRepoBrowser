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
    private void DrawClassic(IReadOnlyList<RepoInfo> repos)

    {

        var settingsPressed = false;
        var settingsHovered = false;
        var sourcePressed = false;
        var sourceHovered = false;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {



            settingsPressed = ImGui.Button(FontAwesomeIcon.Wrench.ToIconString());

            settingsHovered = ImGui.IsItemHovered();



            ImGui.SameLine();



            sourcePressed = ImGui.Button(FontAwesomeIcon.Globe.ToIconString());

            sourceHovered = ImGui.IsItemHovered();



        }



        if (settingsPressed)

        {

            openSettings = !openSettings;

        }



        if (settingsHovered)

        {

            ImGui.SetTooltip("Settings");

        }



        if (sourcePressed)

        {

            OpenUrl(SourceUrl);

        }



        if (sourceHovered)

        {

            ImGui.SetTooltip("Source");

        }



        ImGui.SameLine();



        ImGui.TextColored(new Vector4(1, 0, 0, 1), "DO NOT INSTALL FROM REPOSITORIES YOU DO NOT TRUST.");



        var inputWidth = ImGui.GetWindowContentRegionMax().X / 4;

        ImGui.SameLine(inputWidth * 3);

        ImGui.SetNextItemWidth(inputWidth);

        if (ImGui.InputTextWithHint(

                "##Search",

                $"Search {filteredCount} / {repos.Count} Repos",

                ref searchText,

                64))

        {

            UpdateSearchResults(repos);

        }

        if (ImGui.Button("Add visible repos"))
        {
            SetVisibleReposEnabled(repos, true);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Add or enable every currently visible repository with one reload.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Disable visible repos"))
        {
            SetVisibleReposEnabled(repos, false);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Disable every currently visible repository with one reload.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Disable failed repos"))
        {
            DisableFailedRepos();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Disable enabled repositories that Dalamud reports as download failed.");
        }

        ImGui.SameLine();

        if (ImGui.Button(checkingDisabledRepos ? "Checking disabled..." : "Check disabled repos"))
        {
            CheckDisabledRepos();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Probe disabled repositories and enable the ones that are available again.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Sort repo entries"))
        {
            SortConfiguredRepos();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Sort configured repositories: active first, then inactive, alphabetically in each group.");
        }



        if (openSettings)

        {

            DrawClassicSettingsPanel();

        }



        ImGui.Separator();



        using (ImRaii.Child("RepoList"))
        {

            var chipBase = 32 * ImGuiHelpers.GlobalScale;

            var spacing = chipBase / 6;

            var padding = chipBase / 8;

            var indent = padding * 2;



            filteredCount = 0;

            foreach (var repoInfo in repos)

            {

                if (config.MaxPlugins < 50 && config.MaxPlugins < repoInfo.Plugins.Count)

                {

                    continue;

                }



                if (!string.IsNullOrEmpty(searchText) && !searchResults.Contains(repoInfo))

                {

                    continue;

                }



                var enabled = repoManager.GetRepoEnabled(repoInfo.Url) || repoManager.GetRepoEnabled(repoInfo.RawUrl);

                if (enabled && config.HideEnabledRepos && enabledRepos.Contains(repoInfo))

                {

                    continue;

                }



                var visiblePlugins = repoInfo.Plugins

                    .Where(plugin => (config.ShowOutdatedPlugins || IsPluginCurrentOrUnknown(plugin))

                                     && PluginPassesLanguageFilter(plugin)

                                     && PluginPassesClosedSourceFilter(plugin))

                    .ToList();

                if (visiblePlugins.Count == 0)

                {

                    continue;

                }



                filteredCount++;



                var seen = prevSeenRepos.Contains(repoInfo.Url);



                var isJustCopied = lastCopiedUrl == repoInfo.Url && (DateTime.Now - lastCopiedTime).TotalSeconds < 2;

                var copyButtonText = isJustCopied ? $"Copied!##{repoInfo.Url}" : $"Copy Link##{repoInfo.Url}";



                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.8f, 0.5f, 0.8f), isJustCopied))
                {

                    if (ImGui.Button(copyButtonText))

                    {

                        CopyUrl(repoInfo.Url);

                    }
                }



                ImGui.SameLine();



                using (ImRaii.PushFont(UiBuilder.IconFont))
                {



                    if (!string.IsNullOrEmpty(repoInfo.GitRepoUrl)

                        && ImGui.Button($"{FontAwesomeIcon.Globe.ToIconString()}##{repoInfo.Url}"))

                    {

                        OpenUrl(repoInfo.GitRepoUrl);

                    }



                }



                if (!seen)

                {

                    ImGui.GetWindowDrawList().AddRectFilledMultiColor(

                        ImGui.GetItemRectMin(),

                        new Vector2(

                            ImGui.GetWindowPos().X + ImGui.GetWindowSize().X,

                            ImGui.GetItemRectMax().Y + ImGui.GetItemRectSize().Y),

                        0,

                        ImGui.GetColorU32(ImGuiCol.TitleBgActive) | 0xFF000000,

                        0,

                        0);

                }



                ImGui.SameLine();



                if (ImGui.Checkbox($"{repoInfo.Url}##Enabled", ref enabled))

                {

                    repoManager.ToggleRepo(repoManager.HasRepo(repoInfo.RawUrl) ? repoInfo.RawUrl : repoInfo.Url);

                    if (enabled)

                    {

                        enabledRepos.Add(repoInfo);

                    }

                    else

                    {

                        enabledRepos.Remove(repoInfo);

                    }

                }



                ImGui.TextUnformatted($"Owner: {repoInfo.Owner}");

                ImGui.SameLine();

                if (!repoInfo.IsDefaultBranch && !string.IsNullOrEmpty(repoInfo.BranchName))

                {

                    ImGui.TextUnformatted($"Branch: {repoInfo.BranchName}");

                }



                if (repoInfo.LastUpdated > 0)

                {

                    ImGui.SameLine();

                    var lastUpdatedStr = DateTimeOffset.FromUnixTimeSeconds(repoInfo.LastUpdated)

                        .LocalDateTime

                        .ToString("yyyy-MM-dd");

                    ImGui.TextUnformatted($"Updated: {lastUpdatedStr}");

                }



                ImGui.NewLine();



                ImGui.Indent(indent);

                ImGui.SetWindowFontScale(0.9f);



                var count = 0;

                foreach (var plugin in visiblePlugins)

                {

                    var valid = IsPluginCurrentOrUnknown(plugin);

                    var prevCursor = ImGui.GetCursorPos();

                    ImGui.Dummy(ImGui.CalcTextSize(plugin.Name));

                    var textMin = ImGui.GetItemRectMin();

                    var textMax = ImGui.GetItemRectMax();

                    textMin.X -= padding;

                    textMax.X += padding;

                    var drawList = ImGui.GetWindowDrawList();

                    var rounding = ImGui.GetStyle().FrameRounding;

                    drawList.AddRectFilled(textMin, textMax, valid ? 0x20FFFFFFu : 0x200000FFu, rounding);

                    if (plugin.IsClosedSource)

                    {

                        drawList.AddRectFilled(textMin, textMax, ImGui.GetColorU32(new Vector4(0.35f, 0.22f, 0.05f, 0.2f)), rounding);

                        drawList.AddRect(

                            textMin,

                            textMax,

                            ImGui.GetColorU32(new Vector4(1f, 0.7f, 0.2f, 0.6f)),

                            rounding,

                            0,

                            1f * ImGuiHelpers.GlobalScale);

                    }

                    ImGui.SetCursorPos(prevCursor);



                    if (!valid)

                    {

                        const uint color = 0xA00000FF;

                        var thickness = 2 * ImGuiHelpers.GlobalScale;

                        drawList.AddLine(textMin, textMax, color, thickness);

                        drawList.AddLine(new Vector2(textMin.X, textMax.Y), new Vector2(textMax.X, textMin.Y), color, thickness);

                    }



                    ImGui.Text(plugin.Name);

                    if (ImGui.IsItemHovered())

                    {

                        ShowPluginTooltip(plugin);



                        if (!string.IsNullOrEmpty(plugin.RepoUrl)

                            && ImGui.IsMouseReleased(ImGuiMouseButton.Left)

                            && plugin.RepoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))

                        {

                            OpenUrl(plugin.RepoUrl);

                        }

                    }



                    if (++count % 6 == 5)

                    {

                        continue;

                    }



                    ImGui.SameLine();

                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + spacing);

                }



                ImGui.SetWindowFontScale(1);

                ImGui.Unindent(indent);

                ImGui.Spacing();

                ImGui.Separator();

            }



        }

    }



    private void DrawClassicSettingsPanel()

    {

        var save = false;



        using (ImRaii.Columns(2, string.Empty, false))
        {



            var useModernUi = config.UseModernUi;

            if (ImGui.Checkbox("Use new UI", ref useModernUi))

            {

                config.UseModernUi = useModernUi;

                save = true;

            }



            ImGui.Spacing();



            var showOutdated = config.ShowOutdatedPlugins;

            if (ImGui.Checkbox("Show Outdated Plugins", ref showOutdated))

            {

                config.ShowOutdatedPlugins = showOutdated;

                save = true;

            }



            ImGui.Spacing();



            var hideNonEnglish = config.HideNonEnglishPlugins;

            if (ImGui.Checkbox("Hide non-English plugins", ref hideNonEnglish))

            {

                config.HideNonEnglishPlugins = hideNonEnglish;

                save = true;

                if (!string.IsNullOrEmpty(searchText))

                {

                    UpdateSearchResults(repoManager.RepoList);

                }

            }



            ImGui.Spacing();



            var hideClosedSource = config.HideClosedSourcePlugins;

            if (ImGui.Checkbox("Hide closed-source plugins", ref hideClosedSource))

            {

                config.HideClosedSourcePlugins = hideClosedSource;

                save = true;

                if (!string.IsNullOrEmpty(searchText))

                {

                    UpdateSearchResults(repoManager.RepoList);

                }

            }



            ImGui.Spacing();



            ImGui.TextUnformatted("\tSort");

            var repoSort = config.RepoSort;

            var repoSortChanged = false;

            repoSortChanged |= ImGui.RadioButton("Owner", ref repoSort, 1);

            ImGui.SameLine();

            repoSortChanged |= ImGui.RadioButton("URL", ref repoSort, 2);

            ImGui.SameLine();

            repoSortChanged |= ImGui.RadioButton("# Plugins", ref repoSort, 3);

            ImGui.SameLine();

            repoSortChanged |= ImGui.RadioButton("Last Updated", ref repoSort, 4);

            if (repoSortChanged)

            {

                config.RepoSort = repoSort;

                save = true;

            }



            ImGui.NextColumn();

            ImGui.TextUnformatted(string.Empty);



            var hideEnabled = config.HideEnabledRepos;

            if (ImGui.Checkbox("Hide Enabled Repos", ref hideEnabled))

            {

                config.HideEnabledRepos = hideEnabled;

                save = true;

            }



            ImGui.SetNextItemWidth(ImGui.GetWindowWidth() / 5);

            var maxPlugins = config.MaxPlugins;

            if (ImGui.SliderInt(

                config.MaxPlugins >= 50 ? "\u221e Plugins###MaxPlugins" : "Maximum Plugins###MaxPlugins",

                ref maxPlugins,

                20,

                50))

            {

                config.MaxPlugins = maxPlugins;

                save = true;

            }



        }



        if (save)

        {

            repoManager.RequestSort();

            config.Save();

        }

    }

}
