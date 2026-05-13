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
    private void DrawModernFilterBar(IReadOnlyList<RepoInfo> repos)

    {

        var scale = ImGuiHelpers.GlobalScale;

        var barHeight = 86f * scale;

        var padding = 12f * scale;



        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 12f * scale)
                   .Push(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding / 1.5f))
                   .Push(ImGuiStyleVar.ChildBorderSize, 1f * scale))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.05f, 0.08f, 0.14f, 0.6f))
                   .Push(ImGuiCol.Border, new Vector4(0.2f, 0.3f, 0.5f, 0.3f)))
        {

            // Glassy dark blue background

            using (ImRaii.Child(

                "ModernFilterBar",

                new Vector2(0, barHeight),

                true, // Enable border

                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {



                var inputWidth = MathF.Max(150f * scale, ImGui.GetContentRegionAvail().X - (550f * scale));



                // Style the input text to blend better

                var inputHeight = 0f;
                using (ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0.03f, 0.05f, 0.1f, 0.8f)))
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 8f * scale))
                {



                    ImGui.SetNextItemWidth(inputWidth);

                    if (ImGui.InputTextWithHint(

                            "##SearchModern",

                            $"Search {filteredCount} / {repos.Count} Repos...",

                            ref searchText,

                            64))

                    {

                        UpdateSearchResults(repos);

                    }



                    inputHeight = ImGui.GetFrameHeight();

                }

                ImGui.SameLine();

                if (ImGui.Button("Add visible", new Vector2(100f * scale, inputHeight)))
                {
                    SetVisibleReposEnabled(repos, true);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Add or enable every currently visible repository with one reload.");
                }

                ImGui.SameLine();

                if (ImGui.Button("Disable visible", new Vector2(120f * scale, inputHeight)))
                {
                    SetVisibleReposEnabled(repos, false);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Disable every currently visible repository with one reload.");
                }

                ImGui.SameLine();

                if (ImGui.Button("Disable failed", new Vector2(120f * scale, inputHeight)))
                {
                    DisableFailedRepos();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Disable enabled repositories that Dalamud reports as download failed.");
                }

                ImGui.NewLine();

                if (ImGui.Button(checkingDisabledRepos ? "Checking..." : "Check disabled", new Vector2(120f * scale, inputHeight)))
                {
                    CheckDisabledRepos();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Probe disabled repositories and enable the ones that are available again.");
                }

                ImGui.SameLine();

                if (ImGui.Button("Sort repos", new Vector2(90f * scale, inputHeight)))
                {
                    SortConfiguredRepos();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Sort configured repositories: active first, then inactive, alphabetically in each group.");
                }

                var statusText = $"{filteredCount} repositories shown";

                var statusSize = ImGui.CalcTextSize(statusText);

                var statusX = ImGui.GetWindowContentRegionMax().X - statusSize.X - padding;

                ImGui.SameLine(statusX);

                var statusY = ImGui.GetCursorPosY() + (inputHeight - statusSize.Y) * 0.5f;

                ImGui.SetCursorPosY(statusY);



                // Aether blue text for the counter

                ImGui.TextColored(new Vector4(0.4f, 0.7f, 0.9f, 0.8f), statusText);



            }
        }

    }





    private void DrawModernWarning()

    {

        if (config.DismissedModernWarning)

            return;



        var scale = ImGuiHelpers.GlobalScale;

        var padding = 10f * scale;



        var disclaimerText = "These plugin lists are dynamically generated and not vetted. They may contain malware or jeopardize your account. Proceed with caution. If you're installing plugins from custom repositories, DO NOT ask for help on the XIVLauncher discord. Please review the plugin's README for support.";



        // Fixed safe height

        var bannerHeight = 85f * scale;



        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 10f * scale)
                   .Push(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding))
                   .Push(ImGuiStyleVar.ChildBorderSize, 1f * scale))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.25f, 0.14f, 0.02f, 0.4f))
                   .Push(ImGuiCol.Border, new Vector4(0.5f, 0.35f, 0.1f, 0.3f)))
        {

            // Amber/Orange tinted background

            using (ImRaii.Child(

                "ModernWarning",

                new Vector2(0, bannerHeight),

                true,

                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {



                using (ImRaii.PushFont(UiBuilder.IconFont))
                {

                    ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), FontAwesomeIcon.ExclamationTriangle.ToIconString());

                }

                ImGui.SameLine();



                var width = ImGui.GetContentRegionAvail().X - (30f * scale);

                using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width))
                {

                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.6f, 0.9f), disclaimerText);

                }



                // Dismiss button

                ImGui.SetCursorPos(new Vector2(ImGui.GetWindowContentRegionMax().X - (24f * scale), padding));

                using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
                           .Push(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.1f))
                           .Push(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.2f)))
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {

                    if (ImGui.Button($"{FontAwesomeIcon.Times.ToIconString()}##DismissWarning", new Vector2(20f * scale, 20f * scale)))

                    {

                        config.DismissedModernWarning = true;

                        config.Save();

                    }

                }



            }
        }

    }



    private void DrawModernSettingsPanel()

    {

        var save = false;

        var scale = ImGuiHelpers.GlobalScale;

        var settingsHeight = 250f * scale; // Slightly taller for breathing room



        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 12f * scale)
                   .Push(ImGuiStyleVar.WindowPadding, new Vector2(16f * scale, 14f * scale))
                   .Push(ImGuiStyleVar.ChildBorderSize, 1f * scale))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.04f, 0.06f, 0.1f, 0.95f))
                   .Push(ImGuiCol.Border, new Vector4(0.2f, 0.3f, 0.5f, 0.3f)))
        {

            // Deep void background

            using (ImRaii.Child("ModernSettingsPanel", new Vector2(0, settingsHeight), true))
            {



                using (ImRaii.PushFont(UiBuilder.IconFont))
                {

                    ImGui.TextColored(new Vector4(0.4f, 0.7f, 0.9f, 1f), FontAwesomeIcon.Cog.ToIconString());

                }

                ImGui.SameLine();

                ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1f), "Configuration");



                ImGui.SameLine();

                var width = ImGui.GetContentRegionAvail().X;

                ImGui.Dummy(new Vector2(width - (20 * scale), 0)); // Spacer

                ImGui.SameLine();

                var closePressed = false;
                var closeHovered = false;
                using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
                           .Push(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.1f))
                           .Push(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.2f)))
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {

                    closePressed = ImGui.Button($"{FontAwesomeIcon.Times.ToIconString()}##CloseSettings", new Vector2(20f * scale, 20f * scale));

                    closeHovered = ImGui.IsItemHovered();

                }

                if (closePressed)

                {

                    openSettings = false;

                }

                if (closeHovered)

                {

                    ImGui.SetTooltip("Close");

                }



                ImGui.Separator();

                ImGui.Spacing();



                using (var table = ImRaii.Table("ModernSettingsOptions", 2, ImGuiTableFlags.SizingStretchSame))
                    if (table)

                    {

                        ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthStretch);

                        ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch);

                        ImGui.TableNextRow();



                        ImGui.TableSetColumnIndex(0);

                        var useModernUi = config.UseModernUi;

                        if (ImGui.Checkbox("Use new UI", ref useModernUi))

                        {

                            config.UseModernUi = useModernUi;

                            save = true;

                        }



                        var showOutdated = config.ShowOutdatedPlugins;

                        if (ImGui.Checkbox("Show Outdated Plugins", ref showOutdated))

                        {

                            config.ShowOutdatedPlugins = showOutdated;

                            save = true;

                        }



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



                        ImGui.TableSetColumnIndex(1);

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



                        var hideEnabled = config.HideEnabledRepos;

                        if (ImGui.Checkbox("Hide Enabled Repos", ref hideEnabled))

                        {

                            config.HideEnabledRepos = hideEnabled;

                            save = true;

                        }



                    }



                ImGui.Spacing();

                ImGui.SetNextItemWidth(ImGui.GetWindowWidth() / 3f);

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



                ImGui.Separator();

                ImGui.Spacing();



                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.7f, 1f), "Sort By");



                var repoSort = config.RepoSort;

                ImGui.SetNextItemWidth(200f * scale);

                using (var combo = ImRaii.Combo("##RepoSort", GetSortLabel(repoSort)))
                    if (combo)

                    {

                        if (ImGui.Selectable("Owner", repoSort == 1)) repoSort = 1;

                        if (ImGui.Selectable("URL", repoSort == 2)) repoSort = 2;

                        if (ImGui.Selectable("# Plugins", repoSort == 3)) repoSort = 3;

                        if (ImGui.Selectable("Last Updated", repoSort == 4)) repoSort = 4;

                    }



                if (repoSort != config.RepoSort)

                {

                    config.RepoSort = repoSort;

                    save = true;

                }



            }
        }



        if (save)

        {

            repoManager.RequestSort();

            config.Save();

        }

    }

}
