using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Addressables
{
    [McpForUnityTool("manage_addressables", AutoRegister = false, Group = "addressables")]
    public static class ManageAddressables
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);

            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage);

            string action = actionResult.Value.ToLowerInvariant();

            try
            {
                switch (action)
                {
                    case "ping":
                        return Ping();
                    case "group_list":
                        return GroupList(p);
                    case "group_create":
                        return GroupCreate(p);
                    case "group_remove":
                        return GroupRemove(p);
                    case "entry_add":
                        return EntryAdd(p);
                    case "entry_remove":
                        return EntryRemove(p);
                    case "entry_set_address":
                        return EntrySetAddress(p);
                    case "entry_find":
                        return EntryFind(p);
                    case "entry_move":
                        return EntryMove(p);
                    case "label_list":
                        return LabelList();
                    case "label_add":
                        return LabelAdd(p);
                    case "label_remove":
                        return LabelRemove(p);
                    case "label_set":
                        return LabelSet(p);
                    case "profile_list":
                        return ProfileList();
                    case "profile_get":
                        return ProfileGet(p);
                    case "profile_set":
                        return ProfileSet(p);
                    case "profile_set_active":
                        return ProfileSetActive(p);
                    case "build_content":
                    case "build_update":
                        return new ErrorResponse(
                            $"{action} is an async action. It should be routed to HandleCommandAsync.");
                    case "build_clean":
                        return BuildClean();
                    case "get_settings":
                        return GetSettingsAction();
                    case "set_settings":
                        return SetSettingsAction(p);
                    default:
                        return new ErrorResponse(
                            $"Unknown action: '{action}'. Supported actions: ping, group_list, group_create, group_remove, " +
                            "entry_add, entry_remove, entry_set_address, entry_find, entry_move, " +
                            "label_list, label_add, label_remove, label_set, " +
                            "profile_list, profile_get, profile_set, profile_set_active, " +
                            "build_content, build_update, build_clean, get_settings, set_settings.");
                }
            }
            catch (Exception ex)
            {
                return new ErrorResponse(ex.Message, new { stackTrace = ex.StackTrace });
            }
        }

        public static async Task<object> HandleCommandAsync(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);

            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage);

            string action = actionResult.Value.ToLowerInvariant();

            if (action == "build_content" || action == "build_update")
            {
                try
                {
                    return action == "build_content"
                        ? await BuildContent()
                        : await BuildUpdate(p);
                }
                catch (Exception ex)
                {
                    return new ErrorResponse(ex.Message, new { stackTrace = ex.StackTrace });
                }
            }

            // All other actions are sync — delegate to HandleCommand
            return HandleCommand(@params);
        }

        private static AddressableAssetSettings GetSettings()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                throw new InvalidOperationException(
                    "Addressable Asset Settings not found. " +
                    "Open Window > Asset Management > Addressables > Groups to initialize, " +
                    "or install com.unity.addressables via Package Manager.");
            return settings;
        }

        // === ping ===

        private static object Ping()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            bool available = settings != null;

            return new SuccessResponse("Addressables system status.", new
            {
                available,
                package_version = UnityEditor.PackageManager.PackageInfo
                    .FindForAssembly(typeof(AddressableAssetSettings).Assembly)?.version ?? "unknown",
                group_count = available ? settings.groups.Count : 0,
            });
        }

        // === group_list ===

        private static object GroupList(ToolParams p)
        {
            var settings = GetSettings();
            int pageSize = p.GetInt("page_size") ?? 50;
            int pageNumber = p.GetInt("page_number") ?? 1;

            var allGroups = settings.groups;
            int total = allGroups.Count;
            int skip = (pageNumber - 1) * pageSize;

            var page = allGroups
                .Skip(skip)
                .Take(pageSize)
                .Select(g => new
                {
                    name = g.Name,
                    guid = g.Guid,
                    is_default = g == settings.DefaultGroup,
                    entry_count = g.entries.Count,
                    schemas = g.Schemas.Select(s => s.GetType().Name).ToArray(),
                })
                .ToArray();

            return new SuccessResponse($"Found {total} groups.", new
            {
                groups = page,
                total,
                page = pageNumber,
                page_size = pageSize,
                has_more = skip + pageSize < total,
            });
        }

        // === group_create ===

        private static object GroupCreate(ToolParams p)
        {
            var groupResult = p.GetRequired("group", "'group' parameter is required for group_create.");
            if (!groupResult.IsSuccess)
                return new ErrorResponse(groupResult.ErrorMessage);

            string groupName = groupResult.Value;
            var settings = GetSettings();

            // Check for duplicate
            var existing = settings.FindGroup(groupName);
            if (existing != null)
                return new ErrorResponse($"Group '{groupName}' already exists.");

            var group = settings.CreateGroup(groupName, false, false, false, null,
                typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.GroupAdded, group, true);

            return new SuccessResponse($"Created group '{groupName}'.", new
            {
                name = group.Name,
                guid = group.Guid,
                schemas = group.Schemas.Select(s => s.GetType().Name).ToArray(),
            });
        }

        // === group_remove ===

        private static object GroupRemove(ToolParams p)
        {
            var groupResult = p.GetRequired("group", "'group' parameter is required for group_remove.");
            if (!groupResult.IsSuccess)
                return new ErrorResponse(groupResult.ErrorMessage);

            string groupName = groupResult.Value;
            var settings = GetSettings();

            var group = settings.FindGroup(groupName);
            if (group == null)
                return new ErrorResponse($"Group '{groupName}' not found.");

            if (group == settings.DefaultGroup)
                return new ErrorResponse("Cannot remove the default group.");

            int entryCount = group.entries.Count;
            settings.RemoveGroup(group);

            return new SuccessResponse($"Removed group '{groupName}'.", new
            {
                removed_group = groupName,
                entries_removed = entryCount,
            });
        }

        // === entry_add ===

        private static object EntryAdd(ToolParams p)
        {
            var pathResult = p.GetRequired("path", "'path' parameter is required for entry_add.");
            if (!pathResult.IsSuccess)
                return new ErrorResponse(pathResult.ErrorMessage);

            string assetPath = pathResult.Value;
            var settings = GetSettings();

            string guid = ResolveAssetGuid(assetPath);
            if (guid == null)
                return new ErrorResponse($"Asset not found: '{assetPath}'.");

            // Determine target group
            string groupName = p.Get("group");
            AddressableAssetGroup targetGroup;
            if (!string.IsNullOrEmpty(groupName))
            {
                targetGroup = settings.FindGroup(groupName);
                if (targetGroup == null)
                    return new ErrorResponse($"Group '{groupName}' not found.");
            }
            else
            {
                targetGroup = settings.DefaultGroup;
            }

            var entry = settings.CreateOrMoveEntry(guid, targetGroup, false, false);
            if (entry == null)
                return new ErrorResponse($"Failed to create addressable entry for '{assetPath}'.");

            // Set custom address if provided
            string address = p.Get("address");
            if (!string.IsNullOrEmpty(address))
                entry.address = address;

            // Apply labels if provided
            var labels = p.GetStringArray("labels");
            if (labels != null)
            {
                foreach (var label in labels)
                {
                    if (!settings.GetLabels().Contains(label))
                        settings.AddLabel(label);
                    entry.SetLabel(label, true);
                }
            }

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryCreated, entry, true);

            return new SuccessResponse($"Added '{assetPath}' as addressable.", new
            {
                asset_path = AssetDatabase.GUIDToAssetPath(guid),
                asset_guid = guid,
                address = entry.address,
                group = targetGroup.Name,
                labels = entry.labels.ToArray(),
            });
        }

        // === entry_remove ===

        private static object EntryRemove(ToolParams p)
        {
            var pathResult = p.GetRequired("path", "'path' parameter is required for entry_remove.");
            if (!pathResult.IsSuccess)
                return new ErrorResponse(pathResult.ErrorMessage);

            string assetPath = pathResult.Value;
            var settings = GetSettings();

            string guid = ResolveAssetGuid(assetPath);
            if (guid == null)
                return new ErrorResponse($"Asset not found: '{assetPath}'.");

            var entry = settings.FindAssetEntry(guid);
            if (entry == null)
                return new ErrorResponse($"Asset '{assetPath}' is not an addressable entry.");

            string groupName = entry.parentGroup.Name;
            string address = entry.address;
            entry.parentGroup.RemoveAssetEntry(entry);

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryRemoved, entry, true);

            return new SuccessResponse($"Removed addressable entry for '{assetPath}'.", new
            {
                asset_path = assetPath,
                removed_from_group = groupName,
                address,
            });
        }

        // === entry_set_address ===

        private static object EntrySetAddress(ToolParams p)
        {
            var pathResult = p.GetRequired("path", "'path' parameter is required for entry_set_address.");
            if (!pathResult.IsSuccess)
                return new ErrorResponse(pathResult.ErrorMessage);

            var addressResult = p.GetRequired("address", "'address' parameter is required for entry_set_address.");
            if (!addressResult.IsSuccess)
                return new ErrorResponse(addressResult.ErrorMessage);

            string assetPath = pathResult.Value;
            string newAddress = addressResult.Value;
            var settings = GetSettings();

            string guid = ResolveAssetGuid(assetPath);
            if (guid == null)
                return new ErrorResponse($"Asset not found: '{assetPath}'.");

            var entry = settings.FindAssetEntry(guid);
            if (entry == null)
                return new ErrorResponse($"Asset '{assetPath}' is not an addressable entry.");

            string oldAddress = entry.address;
            entry.address = newAddress;

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entry, true);

            return new SuccessResponse($"Updated address for '{assetPath}'.", new
            {
                asset_path = assetPath,
                old_address = oldAddress,
                new_address = newAddress,
                group = entry.parentGroup.Name,
            });
        }

        // === entry_find ===

        private static object EntryFind(ToolParams p)
        {
            var settings = GetSettings();
            string query = p.Get("query");
            string labelFilter = p.Get("label");
            string groupFilter = p.Get("group");
            int pageSize = p.GetInt("page_size") ?? 50;
            int pageNumber = p.GetInt("page_number") ?? 1;

            var entries = new List<AddressableAssetEntry>();

            foreach (var group in settings.groups)
            {
                if (!string.IsNullOrEmpty(groupFilter) &&
                    !group.Name.Equals(groupFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var entry in group.entries)
                {
                    if (!string.IsNullOrEmpty(labelFilter) && !entry.labels.Contains(labelFilter))
                        continue;

                    if (!string.IsNullOrEmpty(query))
                    {
                        bool matches = entry.address.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                                       || entry.AssetPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!matches) continue;
                    }

                    entries.Add(entry);
                }
            }

            int total = entries.Count;
            int skip = (pageNumber - 1) * pageSize;

            var page = entries
                .Skip(skip)
                .Take(pageSize)
                .Select(e => new
                {
                    address = e.address,
                    asset_path = e.AssetPath,
                    asset_guid = e.guid,
                    group = e.parentGroup.Name,
                    labels = e.labels.ToArray(),
                })
                .ToArray();

            return new SuccessResponse($"Found {total} entries.", new
            {
                entries = page,
                total,
                page = pageNumber,
                page_size = pageSize,
                has_more = skip + pageSize < total,
            });
        }

        // === entry_move ===

        private static object EntryMove(ToolParams p)
        {
            var pathResult = p.GetRequired("path", "'path' parameter is required for entry_move.");
            if (!pathResult.IsSuccess)
                return new ErrorResponse(pathResult.ErrorMessage);

            var targetGroupResult = p.GetRequired("target_group", "'target_group' parameter is required for entry_move.");
            if (!targetGroupResult.IsSuccess)
                return new ErrorResponse(targetGroupResult.ErrorMessage);

            string assetPath = pathResult.Value;
            string targetGroupName = targetGroupResult.Value;
            var settings = GetSettings();

            string guid = ResolveAssetGuid(assetPath);
            if (guid == null)
                return new ErrorResponse($"Asset not found: '{assetPath}'.");

            var entry = settings.FindAssetEntry(guid);
            if (entry == null)
                return new ErrorResponse($"Asset '{assetPath}' is not an addressable entry.");

            var targetGroup = settings.FindGroup(targetGroupName);
            if (targetGroup == null)
                return new ErrorResponse($"Target group '{targetGroupName}' not found.");

            string sourceGroup = entry.parentGroup.Name;
            if (sourceGroup == targetGroupName)
                return new SuccessResponse($"Entry is already in group '{targetGroupName}'.", new
                {
                    asset_path = assetPath,
                    group = targetGroupName,
                });

            settings.CreateOrMoveEntry(guid, targetGroup, false, false);

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entry, true);

            return new SuccessResponse($"Moved '{assetPath}' to group '{targetGroupName}'.", new
            {
                asset_path = assetPath,
                source_group = sourceGroup,
                target_group = targetGroupName,
                address = entry.address,
            });
        }

        // === label_list ===

        private static object LabelList()
        {
            var settings = GetSettings();
            var labels = settings.GetLabels();

            return new SuccessResponse($"Found {labels.Count} labels.", new
            {
                labels = labels.ToArray(),
                total = labels.Count,
            });
        }

        // === label_add ===

        private static object LabelAdd(ToolParams p)
        {
            var labelResult = p.GetRequired("label", "'label' parameter is required for label_add.");
            if (!labelResult.IsSuccess)
                return new ErrorResponse(labelResult.ErrorMessage);

            string label = labelResult.Value;
            var settings = GetSettings();

            var existing = settings.GetLabels();
            if (existing.Contains(label))
                return new ErrorResponse($"Label '{label}' already exists.");

            settings.AddLabel(label);

            return new SuccessResponse($"Added label '{label}'.", new
            {
                label,
                total_labels = settings.GetLabels().Count,
            });
        }

        // === label_remove ===

        private static object LabelRemove(ToolParams p)
        {
            var labelResult = p.GetRequired("label", "'label' parameter is required for label_remove.");
            if (!labelResult.IsSuccess)
                return new ErrorResponse(labelResult.ErrorMessage);

            string label = labelResult.Value;
            var settings = GetSettings();

            var existing = settings.GetLabels();
            if (!existing.Contains(label))
                return new ErrorResponse($"Label '{label}' not found.");

            settings.RemoveLabel(label);

            return new SuccessResponse($"Removed label '{label}'.", new
            {
                label,
                total_labels = settings.GetLabels().Count,
            });
        }

        // === label_set ===

        private static object LabelSet(ToolParams p)
        {
            var pathResult = p.GetRequired("path", "'path' parameter is required for label_set.");
            if (!pathResult.IsSuccess)
                return new ErrorResponse(pathResult.ErrorMessage);

            var labelResult = p.GetRequired("label", "'label' parameter is required for label_set.");
            if (!labelResult.IsSuccess)
                return new ErrorResponse(labelResult.ErrorMessage);

            string assetPath = pathResult.Value;
            string label = labelResult.Value;
            bool enabled = p.GetBool("enabled", true);
            var settings = GetSettings();

            string guid = ResolveAssetGuid(assetPath);
            if (guid == null)
                return new ErrorResponse($"Asset not found: '{assetPath}'.");

            var entry = settings.FindAssetEntry(guid);
            if (entry == null)
                return new ErrorResponse($"Asset '{assetPath}' is not an addressable entry.");

            // Auto-create label if it doesn't exist
            if (!settings.GetLabels().Contains(label))
                settings.AddLabel(label);

            entry.SetLabel(label, enabled);

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.LabelAdded, entry, true);

            return new SuccessResponse(
                $"{(enabled ? "Added" : "Removed")} label '{label}' on '{assetPath}'.", new
                {
                    asset_path = assetPath,
                    label,
                    enabled,
                    all_labels = entry.labels.ToArray(),
                });
        }

        // === build_content ===

        private static async Task<object> BuildContent()
        {
            GetSettings(); // Validate settings exist

            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            EditorApplication.delayCall += () =>
            {
                try
                {
                    AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);

                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        tcs.TrySetResult(new ErrorResponse($"Build failed: {result.Error}", new
                        {
                            error = result.Error,
                            duration = result.Duration,
                        }));
                    }
                    else
                    {
                        tcs.TrySetResult(new SuccessResponse("Addressables build completed.", new
                        {
                            duration = result.Duration,
                            output_path = result.OutputPath,
                            location_count = result.LocationCount,
                        }));
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(new ErrorResponse($"Build exception: {ex.Message}", new
                    {
                        stackTrace = ex.StackTrace,
                    }));
                }
            };

            return await tcs.Task;
        }

        // === profile_list ===

        private static object ProfileList()
        {
            var settings = GetSettings();
            var profileSettings = settings.profileSettings;
            var names = profileSettings.GetAllProfileNames();
            var variableNames = profileSettings.GetVariableNames();

            var profiles = names.Select(name =>
            {
                var id = profileSettings.GetProfileId(name);
                var variables = new Dictionary<string, string>();
                foreach (var varName in variableNames)
                    variables[varName] = profileSettings.GetValueByName(id, varName);

                return new
                {
                    name,
                    id,
                    is_active = id == settings.activeProfileId,
                    variables,
                };
            }).ToArray();

            return new SuccessResponse($"Found {profiles.Length} profiles.", new { profiles });
        }

        // === profile_get ===

        private static object ProfileGet(ToolParams p)
        {
            var profileResult = p.GetRequired("profile", "'profile' parameter is required for profile_get.");
            if (!profileResult.IsSuccess)
                return new ErrorResponse(profileResult.ErrorMessage);

            string profileName = profileResult.Value;
            var settings = GetSettings();
            var profileSettings = settings.profileSettings;

            var id = profileSettings.GetProfileId(profileName);
            if (string.IsNullOrEmpty(id))
                return new ErrorResponse($"Profile '{profileName}' not found.");

            var variableNames = profileSettings.GetVariableNames();
            var variables = new Dictionary<string, string>();
            foreach (var varName in variableNames)
                variables[varName] = profileSettings.GetValueByName(id, varName);

            return new SuccessResponse($"Profile '{profileName}'.", new
            {
                name = profileName,
                id,
                is_active = id == settings.activeProfileId,
                variables,
            });
        }

        // === profile_set ===

        private static object ProfileSet(ToolParams p)
        {
            var profileResult = p.GetRequired("profile", "'profile' parameter is required for profile_set.");
            if (!profileResult.IsSuccess)
                return new ErrorResponse(profileResult.ErrorMessage);

            var variableResult = p.GetRequired("variable", "'variable' parameter is required for profile_set.");
            if (!variableResult.IsSuccess)
                return new ErrorResponse(variableResult.ErrorMessage);

            var valueResult = p.GetRequired("value", "'value' parameter is required for profile_set.");
            if (!valueResult.IsSuccess)
                return new ErrorResponse(valueResult.ErrorMessage);

            string profileName = profileResult.Value;
            string variableName = variableResult.Value;
            string value = valueResult.Value;
            var settings = GetSettings();
            var profileSettings = settings.profileSettings;

            var id = profileSettings.GetProfileId(profileName);
            if (string.IsNullOrEmpty(id))
                return new ErrorResponse($"Profile '{profileName}' not found.");

            profileSettings.SetValue(id, variableName, value);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.ProfileModified, null, true);

            return new SuccessResponse(
                $"Set '{variableName}' = '{value}' on profile '{profileName}'.", new
                {
                    profile = profileName,
                    variable = variableName,
                    value,
                });
        }

        // === profile_set_active ===

        private static object ProfileSetActive(ToolParams p)
        {
            var profileResult = p.GetRequired("profile", "'profile' parameter is required for profile_set_active.");
            if (!profileResult.IsSuccess)
                return new ErrorResponse(profileResult.ErrorMessage);

            string profileName = profileResult.Value;
            var settings = GetSettings();
            var profileSettings = settings.profileSettings;

            var id = profileSettings.GetProfileId(profileName);
            if (string.IsNullOrEmpty(id))
                return new ErrorResponse($"Profile '{profileName}' not found.");

            settings.activeProfileId = id;
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.ActiveProfileSet, null, true);

            return new SuccessResponse($"Active profile set to '{profileName}'.", new
            {
                profile = profileName,
                id,
            });
        }

        // === build_update ===

        private static async Task<object> BuildUpdate(ToolParams p)
        {
            var settings = GetSettings();
            string contentStatePath = p.Get("content_state_path");

            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (string.IsNullOrEmpty(contentStatePath))
                        contentStatePath = ContentUpdateScript.GetContentStateDataPath(false);

                    if (string.IsNullOrEmpty(contentStatePath))
                    {
                        tcs.TrySetResult(new ErrorResponse(
                            "No content state file found. Run a full build first (build_content), " +
                            "or provide content_state_path explicitly."));
                        return;
                    }

                    var result = ContentUpdateScript.BuildContentUpdate(settings, contentStatePath);

                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        tcs.TrySetResult(new ErrorResponse($"Content update build failed: {result.Error}", new
                        {
                            error = result.Error,
                            duration = result.Duration,
                        }));
                    }
                    else
                    {
                        tcs.TrySetResult(new SuccessResponse("Addressables content update build completed.", new
                        {
                            duration = result.Duration,
                            output_path = result.OutputPath,
                            location_count = result.LocationCount,
                        }));
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(new ErrorResponse($"Content update build exception: {ex.Message}", new
                    {
                        stackTrace = ex.StackTrace,
                    }));
                }
            };

            return await tcs.Task;
        }

        // === build_clean ===

        private static object BuildClean()
        {
            var settings = GetSettings();
            AddressableAssetSettings.CleanPlayerContent(settings.ActivePlayerDataBuilder);
            return new SuccessResponse("Addressables build output cleaned.");
        }

        // === get_settings ===

        private static object GetSettingsAction()
        {
            var settings = GetSettings();
            var profileSettings = settings.profileSettings;
            var activeProfileName = profileSettings.GetAllProfileNames()
                .FirstOrDefault(n => profileSettings.GetProfileId(n) == settings.activeProfileId) ?? "unknown";

            return new SuccessResponse("Addressables settings.", new
            {
                active_profile = activeProfileName,
                active_profile_id = settings.activeProfileId,
                player_version_override = settings.OverridePlayerVersion,
                max_concurrent_web_requests = settings.MaxConcurrentWebRequests,
                unique_bundle_ids = settings.UniqueBundleIds,
                contiguous_bundles = settings.ContiguousBundles,
                non_recursive_dependency_calculation = settings.NonRecursiveBuilding,
                build_remote_catalog = settings.BuildRemoteCatalog,
                group_count = settings.groups.Count,
                label_count = settings.GetLabels().Count,
                profile_count = profileSettings.GetAllProfileNames().Count,
            });
        }

        // === set_settings ===

        private static object SetSettingsAction(ToolParams p)
        {
            var keyResult = p.GetRequired("key", "'key' parameter is required for set_settings.");
            if (!keyResult.IsSuccess)
                return new ErrorResponse(keyResult.ErrorMessage);

            var valueResult = p.GetRequired("value", "'value' parameter is required for set_settings.");
            if (!valueResult.IsSuccess)
                return new ErrorResponse(valueResult.ErrorMessage);

            string key = keyResult.Value;
            string value = valueResult.Value;
            var settings = GetSettings();

            switch (key.ToLowerInvariant())
            {
                case "player_version_override":
                    settings.OverridePlayerVersion = value;
                    break;
                case "max_concurrent_web_requests":
                    settings.MaxConcurrentWebRequests = int.Parse(value);
                    break;
                case "unique_bundle_ids":
                    settings.UniqueBundleIds = bool.Parse(value);
                    break;
                case "contiguous_bundles":
                    settings.ContiguousBundles = bool.Parse(value);
                    break;
                case "non_recursive_dependency_calculation":
                    settings.NonRecursiveBuilding = bool.Parse(value);
                    break;
                case "build_remote_catalog":
                    settings.BuildRemoteCatalog = bool.Parse(value);
                    break;
                default:
                    return new ErrorResponse(
                        $"Unknown setting key: '{key}'. Valid keys: player_version_override, " +
                        "max_concurrent_web_requests, unique_bundle_ids, contiguous_bundles, " +
                        "non_recursive_dependency_calculation, build_remote_catalog.");
            }

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true);

            return new SuccessResponse($"Updated '{key}' to '{value}'.", new { key, value });
        }

        // === Helpers ===

        private static string ResolveAssetGuid(string pathOrGuid)
        {
            // If it looks like a GUID (32 hex chars), validate it directly
            if (pathOrGuid.Length == 32 && Regex.IsMatch(pathOrGuid, @"^[0-9a-fA-F]{32}$"))
            {
                string path = AssetDatabase.GUIDToAssetPath(pathOrGuid);
                return !string.IsNullOrEmpty(path) ? pathOrGuid : null;
            }

            // Treat as asset path
            string guid = AssetDatabase.AssetPathToGUID(pathOrGuid);
            return !string.IsNullOrEmpty(guid) ? guid : null;
        }
    }
}
