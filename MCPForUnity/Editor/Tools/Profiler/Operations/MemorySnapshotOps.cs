using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Profiler
{
    internal static class MemorySnapshotOps
    {
        private static readonly string[] MemoryProfilerTypeNames =
        {
            "Unity.Profiling.Memory.MemoryProfiler, UnityEngine.CoreModule",
            "Unity.MemoryProfiler.MemoryProfiler, Unity.MemoryProfiler.Editor",
            "UnityEngine.Profiling.Memory.Experimental.MemoryProfiler, UnityEngine.CoreModule"
        };

        private static readonly string[] DebugScreenCaptureTypeNames =
        {
            "Unity.Profiling.DebugScreenCapture, UnityEngine.CoreModule",
            "UnityEngine.Profiling.Experimental.DebugScreenCapture, UnityEngine.CoreModule",
            "Unity.Profiling.Memory.Experimental.DebugScreenCapture, Unity.MemoryProfiler.Editor"
        };

        private static Type MemoryProfilerType => ResolveFirstType(MemoryProfilerTypeNames);
        private static bool HasPackage => MemoryProfilerType != null;

        internal static async Task<object> TakeSnapshotAsync(JObject @params)
        {
            if (!HasPackage)
                return PackageMissingError();

            var p = new ToolParams(@params);
            string snapshotPath = ResolveSnapshotPath(p);
            string[] requestedFlags = p.GetStringArray("capture_flags");
            bool includeScreenshot = p.GetBool("include_screenshot", false);

            var profilerType = MemoryProfilerType;
            MethodInfo takeMethod = ResolveTakeSnapshotMethod(profilerType, includeScreenshot);
            if (takeMethod == null)
                return new ErrorResponse("Could not find a supported TakeSnapshot method on MemoryProfiler. API may have changed.");

            object effectiveCaptureFlags;
            try
            {
                effectiveCaptureFlags = ResolveCaptureFlags(takeMethod, requestedFlags);
            }
            catch (ArgumentException ex)
            {
                return new ErrorResponse(ex.Message);
            }

            string screenshotStatus = includeScreenshot ? "requested_unavailable" : "not_requested";
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                Action<string, bool> callback = (path, result) =>
                {
                    if (!result)
                    {
                        tcs.TrySetResult(new ErrorResponse($"Snapshot capture failed for path: {path}"));
                        return;
                    }

                    var fi = new FileInfo(path);
                    tcs.TrySetResult(new SuccessResponse("Memory snapshot captured.", new
                    {
                        path,
                        file_name = fi.Name,
                        directory = fi.DirectoryName,
                        size_bytes = fi.Exists ? fi.Length : 0,
                        size_mb = fi.Exists ? Math.Round(fi.Length / (1024.0 * 1024.0), 2) : 0,
                        captured_at_utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        effective_capture_flags = effectiveCaptureFlags?.ToString(),
                        screenshot = new
                        {
                            requested = includeScreenshot,
                            status = screenshotStatus
                        }
                    }));
                };

                object screenshotCallback = null;
                var parameters = takeMethod.GetParameters();
                if (parameters.Length == 4)
                {
                    Type screenshotType = parameters[2].ParameterType.GenericTypeArguments[2];
                    screenshotCallback = CreateScreenshotCallbackDelegate(parameters[2].ParameterType, screenshotType, () =>
                    {
                        screenshotStatus = "captured";
                    });
                }
                else if (includeScreenshot)
                {
                    screenshotStatus = "unsupported_api";
                }

                switch (parameters.Length)
                {
                    case 4:
                        takeMethod.Invoke(null, new[] { snapshotPath, callback, screenshotCallback, effectiveCaptureFlags });
                        break;
                    case 3:
                        if (includeScreenshot)
                            screenshotStatus = "unsupported_api";
                        takeMethod.Invoke(null, new[] { snapshotPath, callback, effectiveCaptureFlags });
                        break;
                    case 2:
                        if (includeScreenshot)
                            screenshotStatus = "unsupported_api";
                        takeMethod.Invoke(null, new object[] { snapshotPath, callback });
                        break;
                    default:
                        return new ErrorResponse($"TakeSnapshot has unexpected {parameters.Length} parameters. API may have changed.");
                }
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"Failed to take snapshot: {ex.Message}");
            }

            var timeout = Task.Delay(TimeSpan.FromSeconds(60));
            var completed = await Task.WhenAny(tcs.Task, timeout);
            if (completed == timeout)
                return new ErrorResponse("Snapshot timed out after 60 seconds.");

            return await tcs.Task;
        }

        internal static object ListSnapshots(JObject @params)
        {
            if (!HasPackage)
                return PackageMissingError();

            var p = new ToolParams(@params);
            string searchPath = p.Get("search_path");

            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(searchPath))
            {
                dirs.Add(searchPath);
            }
            else
            {
                dirs.Add(Path.Combine(Application.temporaryCachePath, "MemoryCaptures"));
                dirs.Add(Path.Combine(Application.dataPath, "..", "MemoryCaptures"));
            }

            var snapshots = new List<object>();
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir))
                    continue;

                foreach (string file in Directory.GetFiles(dir, "*.snap"))
                {
                    var fi = new FileInfo(file);
                    snapshots.Add(new
                    {
                        path = fi.FullName,
                        size_bytes = fi.Length,
                        size_mb = Math.Round(fi.Length / (1024.0 * 1024.0), 2),
                        created = fi.CreationTimeUtc.ToString("o", CultureInfo.InvariantCulture)
                    });
                }
            }

            return new SuccessResponse($"Found {snapshots.Count} snapshot(s).", new
            {
                snapshots,
                searched_dirs = dirs
            });
        }

        internal static object CompareSnapshots(JObject @params)
        {
            if (!HasPackage)
                return PackageMissingError();

            var p = new ToolParams(@params);
            var pathAResult = p.GetRequired("snapshot_a");
            if (!pathAResult.IsSuccess)
                return new ErrorResponse(pathAResult.ErrorMessage);

            var pathBResult = p.GetRequired("snapshot_b");
            if (!pathBResult.IsSuccess)
                return new ErrorResponse(pathBResult.ErrorMessage);

            string pathA = pathAResult.Value;
            string pathB = pathBResult.Value;

            if (!File.Exists(pathA))
                return new ErrorResponse($"Snapshot file not found: {pathA}");
            if (!File.Exists(pathB))
                return new ErrorResponse($"Snapshot file not found: {pathB}");

            var fiA = new FileInfo(pathA);
            var fiB = new FileInfo(pathB);

            return new SuccessResponse("Snapshot comparison (file-level metadata).", new
            {
                snapshot_a = new
                {
                    path = fiA.FullName,
                    size_bytes = fiA.Length,
                    size_mb = Math.Round(fiA.Length / (1024.0 * 1024.0), 2),
                    created = fiA.CreationTimeUtc.ToString("o", CultureInfo.InvariantCulture)
                },
                snapshot_b = new
                {
                    path = fiB.FullName,
                    size_bytes = fiB.Length,
                    size_mb = Math.Round(fiB.Length / (1024.0 * 1024.0), 2),
                    created = fiB.CreationTimeUtc.ToString("o", CultureInfo.InvariantCulture)
                },
                delta = new
                {
                    size_delta_bytes = fiB.Length - fiA.Length,
                    size_delta_mb = Math.Round((fiB.Length - fiA.Length) / (1024.0 * 1024.0), 2),
                    time_delta_seconds = (fiB.CreationTimeUtc - fiA.CreationTimeUtc).TotalSeconds
                },
                note = "For detailed object-level comparison, open both snapshots in the Memory Profiler window."
            });
        }

        private static string ResolveSnapshotPath(ToolParams p)
        {
            string snapshotPath = p.Get("snapshot_path");
            if (!string.IsNullOrEmpty(snapshotPath))
                return snapshotPath;

            string snapshotLabel = SanitizeForFileName(p.Get("snapshot_label", "snapshot"));
            string dir = Path.Combine(Application.temporaryCachePath, "MemoryCaptures");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"{snapshotLabel}_{DateTime.Now:yyyyMMdd_HHmmss}.snap");
        }

        private static MethodInfo ResolveTakeSnapshotMethod(Type profilerType, bool includeScreenshot)
        {
            Type debugScreenCaptureType = ResolveFirstType(DebugScreenCaptureTypeNames);
            Type screenshotCallbackType = null;
            if (debugScreenCaptureType != null)
            {
                screenshotCallbackType = typeof(Action<,,>).MakeGenericType(
                    typeof(string), typeof(bool), debugScreenCaptureType);
            }

            MethodInfo bestMethod = null;
            foreach (MethodInfo method in profilerType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "TakeSnapshot")
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2 || parameters[0].ParameterType != typeof(string) || parameters[1].ParameterType != typeof(Action<string, bool>))
                    continue;

                if (parameters.Length == 4 && screenshotCallbackType != null && parameters[2].ParameterType == screenshotCallbackType && parameters[3].ParameterType.IsEnum)
                    return method;

                if (parameters.Length == 3 && parameters[2].ParameterType.IsEnum && bestMethod == null)
                    bestMethod = method;

                if (parameters.Length == 2 && bestMethod == null)
                    bestMethod = method;
            }

            return bestMethod;
        }

        private static object ResolveCaptureFlags(MethodInfo takeMethod, string[] requestedFlags)
        {
            ParameterInfo[] parameters = takeMethod.GetParameters();
            ParameterInfo flagsParam = parameters.Length >= 3 && parameters[^1].ParameterType.IsEnum ? parameters[^1] : null;
            if (flagsParam == null)
                return null;

            Type flagsType = flagsParam.ParameterType;
            if (requestedFlags == null || requestedFlags.Length == 0)
            {
                if (flagsParam.HasDefaultValue && flagsParam.DefaultValue != null)
                    return Enum.ToObject(flagsType, flagsParam.DefaultValue);

                Array values = Enum.GetValues(flagsType);
                return values.Length > 0 ? values.GetValue(0) : Activator.CreateInstance(flagsType);
            }

            ulong aggregate = 0;
            foreach (string flagName in requestedFlags)
            {
                if (string.IsNullOrWhiteSpace(flagName))
                    continue;

                try
                {
                    object parsed = Enum.Parse(flagsType, flagName.Trim(), true);
                    aggregate |= Convert.ToUInt64(parsed, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    throw new ArgumentException(
                        $"Unknown capture flag '{flagName}'. Valid values: {string.Join(", ", Enum.GetNames(flagsType))}");
                }
            }

            return Enum.ToObject(flagsType, aggregate);
        }

        private static object CreateScreenshotCallbackDelegate(Type delegateType, Type screenshotType, Action onScreenshot)
        {
            MethodInfo helper = typeof(MemorySnapshotOps)
                .GetMethod(nameof(BuildScreenshotCallback), BindingFlags.NonPublic | BindingFlags.Static)
                ?.MakeGenericMethod(screenshotType);
            return helper?.Invoke(null, new object[] { onScreenshot, delegateType });
        }

        private static object BuildScreenshotCallback<TScreenCapture>(Action onScreenshot, Type delegateType)
        {
            Action<string, bool, TScreenCapture> callback = (_, success, __) =>
            {
                if (success)
                    onScreenshot?.Invoke();
            };

            return Delegate.CreateDelegate(delegateType, callback.Target, callback.Method);
        }

        private static Type ResolveFirstType(IEnumerable<string> typeNames)
        {
            foreach (string typeName in typeNames)
            {
                Type type = Type.GetType(typeName);
                if (type != null)
                    return type;
            }

            return null;
        }

        private static string SanitizeForFileName(string value)
        {
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidChar, '_');
            }

            return value.Replace(' ', '_');
        }

        private static ErrorResponse PackageMissingError()
        {
            return new ErrorResponse(
                "Package com.unity.memoryprofiler is required. "
                + "Install via Package Manager or: manage_packages action=add_package package_id=com.unity.memoryprofiler");
        }
    }
}
