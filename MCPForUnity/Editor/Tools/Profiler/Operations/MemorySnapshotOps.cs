using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Profiler
{
    internal static class MemorySnapshotOps
    {
        private static readonly Type MemoryProfilerType =
            Type.GetType("Unity.MemoryProfiler.MemoryProfiler, Unity.MemoryProfiler.Editor");

        private static readonly string[] DefaultCaptureFlagNames =
        {
            "ManagedObjects",
            "NativeObjects",
            "NativeAllocations",
            "NativeAllocationSites",
            "NativeStackTraces",
        };

        private static bool HasPackage => MemoryProfilerType != null;

        internal static async Task<object> TakeSnapshotAsync(JObject @params)
        {
            if (!HasPackage)
                return PackageMissingError();

            var p = new ToolParams(@params);
            string snapshotPath = ResolveSnapshotPath(p.Get("snapshot_path"), p.Get("snapshot_label"));
            bool includeScreenshot = p.GetBool("include_screenshot");
            string[] requestedCaptureFlags = p.GetStringArray("capture_flags");

            var takeSnapshotMethods = MemoryProfilerType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "TakeSnapshot")
                .ToArray();

            MethodInfo screenshotMethod = FindTakeSnapshotMethod(takeSnapshotMethods, 4);
            MethodInfo flagsMethod = FindTakeSnapshotMethod(takeSnapshotMethods, 3);
            MethodInfo legacyMethod = FindTakeSnapshotMethod(takeSnapshotMethods, 2);

            bool canAttemptScreenshot = includeScreenshot && Application.isPlaying && screenshotMethod != null;
            MethodInfo takeMethod = canAttemptScreenshot
                ? screenshotMethod
                : flagsMethod ?? legacyMethod;

            if (takeMethod == null)
                return new ErrorResponse("Could not find a supported TakeSnapshot method on MemoryProfiler. API may have changed.");

            bool captureFlagsSupported = takeMethod.GetParameters().Length >= 3
                && IsFlagsParameterType(takeMethod.GetParameters().Last().ParameterType);

            object captureFlagsValue = null;
            string[] effectiveCaptureFlags = Array.Empty<string>();

            if (captureFlagsSupported)
            {
                var captureFlagsResult = ResolveCaptureFlags(
                    takeMethod.GetParameters().Last().ParameterType,
                    requestedCaptureFlags);
                if (!captureFlagsResult.IsSuccess)
                    return new ErrorResponse(captureFlagsResult.ErrorMessage);

                captureFlagsValue = captureFlagsResult.Value.Value;
                effectiveCaptureFlags = captureFlagsResult.Value.Names;
            }
            else if (requestedCaptureFlags != null && requestedCaptureFlags.Length > 0)
            {
                return new ErrorResponse(
                    "This Unity Memory Profiler API version does not support named capture_flags. "
                    + "Remove capture_flags or update the package.");
            }

            var snapshotTcs = new TaskCompletionSource<SnapshotCaptureResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            ScreenshotCaptureContext screenshotContext = null;
            Delegate screenshotCallback = null;
            string screenshotStatus = includeScreenshot
                ? (Application.isPlaying ? "unsupported_api" : "skipped_not_playing")
                : "not_requested";

            if (canAttemptScreenshot)
            {
                screenshotContext = new ScreenshotCaptureContext(snapshotPath);
                screenshotCallback = CreateScreenshotCallback(
                    takeMethod.GetParameters()[2].ParameterType,
                    screenshotContext);
                screenshotStatus = "pending";
            }

            try
            {
                Action<string, bool> snapshotCallback = (path, result) =>
                {
                    snapshotTcs.TrySetResult(new SnapshotCaptureResult
                    {
                        Path = path,
                        Success = result,
                        CapturedAtUtc = DateTime.UtcNow,
                    });
                };

                switch (takeMethod.GetParameters().Length)
                {
                    case 4:
                        takeMethod.Invoke(null, new object[] { snapshotPath, snapshotCallback, screenshotCallback, captureFlagsValue });
                        break;
                    case 3:
                        takeMethod.Invoke(null, new object[] { snapshotPath, snapshotCallback, captureFlagsValue });
                        break;
                    case 2:
                        takeMethod.Invoke(null, new object[] { snapshotPath, snapshotCallback });
                        break;
                    default:
                        return new ErrorResponse("TakeSnapshot has an unexpected signature. API may have changed.");
                }
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"Failed to take snapshot: {ex.Message}");
            }

            var snapshotTimeout = Task.Delay(TimeSpan.FromSeconds(30));
            var completedSnapshot = await Task.WhenAny(snapshotTcs.Task, snapshotTimeout);
            if (completedSnapshot == snapshotTimeout)
                return new ErrorResponse("Snapshot timed out after 30 seconds.");

            SnapshotCaptureResult snapshotResult = await snapshotTcs.Task;
            if (!snapshotResult.Success)
                return new ErrorResponse($"Snapshot capture failed for path: {snapshotResult.Path}");

            ScreenshotCaptureResult screenshotResult = null;
            if (canAttemptScreenshot && screenshotContext != null)
            {
                var screenshotTimeout = Task.Delay(TimeSpan.FromSeconds(15));
                var completedScreenshot = await Task.WhenAny(screenshotContext.CompletionSource.Task, screenshotTimeout);
                if (completedScreenshot == screenshotTimeout)
                {
                    screenshotResult = new ScreenshotCaptureResult
                    {
                        Requested = true,
                        Attempted = true,
                        Captured = false,
                        Status = "timed_out",
                    };
                }
                else
                {
                    screenshotResult = await screenshotContext.CompletionSource.Task;
                }

                screenshotStatus = screenshotResult.Status;
            }
            else if (includeScreenshot)
            {
                screenshotResult = new ScreenshotCaptureResult
                {
                    Requested = true,
                    Attempted = false,
                    Captured = false,
                    Status = screenshotStatus,
                };
            }

            var fileInfo = new FileInfo(snapshotResult.Path);
            object screenshotData = new
            {
                requested = includeScreenshot,
                attempted = screenshotResult?.Attempted ?? false,
                captured = screenshotResult?.Captured ?? false,
                status = screenshotResult?.Status ?? screenshotStatus,
                path = screenshotResult?.Path,
                width = screenshotResult?.Width,
                height = screenshotResult?.Height,
                error = screenshotResult?.Error,
            };

            return new SuccessResponse("Memory snapshot captured.", new
            {
                path = snapshotResult.Path,
                file_name = Path.GetFileName(snapshotResult.Path),
                directory = Path.GetDirectoryName(snapshotResult.Path),
                size_bytes = fileInfo.Exists ? fileInfo.Length : 0,
                size_mb = fileInfo.Exists ? Math.Round(fileInfo.Length / (1024.0 * 1024.0), 2) : 0,
                captured_at_utc = snapshotResult.CapturedAtUtc.ToString("o"),
                capture_flags_supported = captureFlagsSupported,
                capture_flags = effectiveCaptureFlags,
                include_screenshot = includeScreenshot,
                screenshot = screenshotData,
            });
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
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.snap"))
                {
                    var fi = new FileInfo(file);
                    snapshots.Add(new
                    {
                        path = fi.FullName,
                        size_bytes = fi.Length,
                        size_mb = Math.Round(fi.Length / (1024.0 * 1024.0), 2),
                        created = fi.CreationTimeUtc.ToString("o"),
                    });
                }
            }

            return new SuccessResponse($"Found {snapshots.Count} snapshot(s).", new
            {
                snapshots,
                searched_dirs = dirs,
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
                    created = fiA.CreationTimeUtc.ToString("o"),
                },
                snapshot_b = new
                {
                    path = fiB.FullName,
                    size_bytes = fiB.Length,
                    size_mb = Math.Round(fiB.Length / (1024.0 * 1024.0), 2),
                    created = fiB.CreationTimeUtc.ToString("o"),
                },
                delta = new
                {
                    size_delta_bytes = fiB.Length - fiA.Length,
                    size_delta_mb = Math.Round((fiB.Length - fiA.Length) / (1024.0 * 1024.0), 2),
                    time_delta_seconds = (fiB.CreationTimeUtc - fiA.CreationTimeUtc).TotalSeconds,
                },
                note = "For detailed object-level comparison, open both snapshots in the Memory Profiler window.",
            });
        }

        private static MethodInfo FindTakeSnapshotMethod(IEnumerable<MethodInfo> methods, int parameterCount)
        {
            return methods.FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                if (parameters.Length != parameterCount)
                    return false;

                if (parameters[0].ParameterType != typeof(string))
                    return false;

                if (parameters[1].ParameterType != typeof(Action<string, bool>))
                    return false;

                if (parameterCount == 4)
                {
                    return parameters[2].ParameterType.IsGenericType
                        && parameters[2].ParameterType.GetGenericTypeDefinition() == typeof(Action<,,>)
                        && IsFlagsParameterType(parameters[3].ParameterType);
                }

                if (parameterCount == 3)
                    return IsFlagsParameterType(parameters[2].ParameterType);

                return true;
            });
        }

        private static bool IsFlagsParameterType(Type type)
        {
            return type != null && (type.IsEnum || type == typeof(uint) || type == typeof(int) || type == typeof(long));
        }

        private static Result<CaptureFlagsResolution> ResolveCaptureFlags(Type flagsType, string[] requestedFlags)
        {
            if (!flagsType.IsEnum)
            {
                object zeroValue = flagsType == typeof(uint) ? 0u
                    : flagsType == typeof(long) ? 0L
                    : 0;
                return Result<CaptureFlagsResolution>.Success(new CaptureFlagsResolution
                {
                    Value = zeroValue,
                    Names = Array.Empty<string>(),
                });
            }

            var requestedNames = requestedFlags != null && requestedFlags.Length > 0
                ? requestedFlags
                : DefaultCaptureFlagNames;

            var knownNames = new HashSet<string>(Enum.GetNames(flagsType), StringComparer.OrdinalIgnoreCase);
            long value = 0;
            var normalizedNames = new List<string>();

            foreach (string requestedName in requestedNames)
            {
                if (string.IsNullOrWhiteSpace(requestedName))
                    continue;

                if (!knownNames.Contains(requestedName))
                {
                    return Result<CaptureFlagsResolution>.Error(
                        $"Unknown capture flag '{requestedName}'. Valid flags: {string.Join(", ", Enum.GetNames(flagsType))}");
                }

                object parsed = Enum.Parse(flagsType, requestedName, true);
                value |= Convert.ToInt64(parsed);
                normalizedNames.Add(Enum.GetName(flagsType, parsed) ?? requestedName);
            }

            return Result<CaptureFlagsResolution>.Success(new CaptureFlagsResolution
            {
                Value = Enum.ToObject(flagsType, value),
                Names = normalizedNames.Distinct(StringComparer.Ordinal).ToArray(),
            });
        }

        private static string ResolveSnapshotPath(string snapshotPath, string snapshotLabel)
        {
            if (!string.IsNullOrEmpty(snapshotPath))
            {
                string fullPath = Path.GetFullPath(snapshotPath);
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                return fullPath;
            }

            string dir = Path.Combine(Application.temporaryCachePath, "MemoryCaptures");
            Directory.CreateDirectory(dir);

            string label = SanitizeSnapshotLabel(snapshotLabel);
            return Path.Combine(dir, $"{label}_{DateTime.Now:yyyyMMdd_HHmmss}.snap");
        }

        private static string SanitizeSnapshotLabel(string snapshotLabel)
        {
            if (string.IsNullOrWhiteSpace(snapshotLabel))
                return "snapshot";

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(snapshotLabel
                .Trim()
                .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                .ToArray())
                .Replace(' ', '_');

            return string.IsNullOrWhiteSpace(sanitized) ? "snapshot" : sanitized;
        }

        private static Delegate CreateScreenshotCallback(Type callbackType, ScreenshotCaptureContext context)
        {
            var pathParam = Expression.Parameter(typeof(string), "path");
            var resultParam = Expression.Parameter(typeof(bool), "result");
            var captureParam = Expression.Parameter(callbackType.GetGenericArguments()[2], "capture");

            var body = Expression.Call(
                typeof(MemorySnapshotOps),
                nameof(HandleScreenshotCapture),
                null,
                pathParam,
                resultParam,
                Expression.Convert(captureParam, typeof(object)),
                Expression.Constant(context));

            return Expression.Lambda(callbackType, body, pathParam, resultParam, captureParam).Compile();
        }

        private static void HandleScreenshotCapture(
            string path,
            bool result,
            object screenCapture,
            ScreenshotCaptureContext context)
        {
            var screenshotResult = new ScreenshotCaptureResult
            {
                Requested = true,
                Attempted = true,
                Captured = false,
                Status = "capture_failed",
            };

            if (!result)
            {
                context.CompletionSource.TrySetResult(screenshotResult);
                return;
            }

            if (screenCapture == null)
            {
                screenshotResult.Status = "empty_capture";
                context.CompletionSource.TrySetResult(screenshotResult);
                return;
            }

            try
            {
                if (!TryCreateScreenshotPng(screenCapture, GetScreenshotPath(path, context.SnapshotPath),
                        out string screenshotPath, out int width, out int height, out string error))
                {
                    screenshotResult.Status = "save_failed";
                    screenshotResult.Error = error;
                    context.CompletionSource.TrySetResult(screenshotResult);
                    return;
                }

                screenshotResult.Captured = true;
                screenshotResult.Status = "captured";
                screenshotResult.Path = screenshotPath;
                screenshotResult.Width = width;
                screenshotResult.Height = height;
            }
            catch (Exception ex)
            {
                screenshotResult.Status = "save_failed";
                screenshotResult.Error = ex.Message;
            }

            context.CompletionSource.TrySetResult(screenshotResult);
        }

        private static string GetScreenshotPath(string callbackPath, string snapshotPath)
        {
            string basePath = string.IsNullOrEmpty(callbackPath) ? snapshotPath : callbackPath;
            return Path.ChangeExtension(basePath, ".png");
        }

        private static bool TryCreateScreenshotPng(
            object screenCapture,
            string screenshotPath,
            out string savedPath,
            out int width,
            out int height,
            out string error)
        {
            savedPath = null;
            width = 0;
            height = 0;
            error = null;

            var captureType = screenCapture.GetType();
            var widthProperty = captureType.GetProperty("Width");
            var heightProperty = captureType.GetProperty("Height");
            var imageFormatProperty = captureType.GetProperty("ImageFormat");
            var rawDataProperty = captureType.GetProperty("RawImageDataReference");

            if (widthProperty == null || heightProperty == null || imageFormatProperty == null || rawDataProperty == null)
            {
                error = "DebugScreenCapture did not expose the expected properties.";
                return false;
            }

            width = Convert.ToInt32(widthProperty.GetValue(screenCapture));
            height = Convert.ToInt32(heightProperty.GetValue(screenCapture));
            var imageFormat = imageFormatProperty.GetValue(screenCapture);
            var rawData = rawDataProperty.GetValue(screenCapture);

            if (!(imageFormat is TextureFormat textureFormat))
            {
                error = "DebugScreenCapture.ImageFormat was not a TextureFormat.";
                return false;
            }

            byte[] bytes = ConvertRawImageDataToBytes(rawData);
            if (bytes == null || bytes.Length == 0)
            {
                error = "DebugScreenCapture contained no raw image data.";
                return false;
            }

            var directory = Path.GetDirectoryName(screenshotPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var texture = new Texture2D(width, height, textureFormat, false);
            try
            {
                texture.LoadRawTextureData(bytes);
                texture.Apply(false, false);
                File.WriteAllBytes(screenshotPath, texture.EncodeToPNG());
                savedPath = screenshotPath;
                return true;
            }
            finally
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(texture);
                else
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static byte[] ConvertRawImageDataToBytes(object rawData)
        {
            if (rawData == null)
                return null;

            if (rawData is byte[] byteArray)
                return byteArray;

            MethodInfo toArrayMethod = rawData.GetType().GetMethod("ToArray", Type.EmptyTypes);
            return toArrayMethod?.Invoke(rawData, null) as byte[];
        }

        private static ErrorResponse PackageMissingError()
        {
            return new ErrorResponse(
                "Package com.unity.memoryprofiler is required. "
                + "Install via Package Manager or: manage_packages action=add_package package_id=com.unity.memoryprofiler");
        }

        private sealed class CaptureFlagsResolution
        {
            public object Value { get; set; }
            public string[] Names { get; set; }
        }

        private sealed class SnapshotCaptureResult
        {
            public string Path { get; set; }
            public bool Success { get; set; }
            public DateTime CapturedAtUtc { get; set; }
        }

        private sealed class ScreenshotCaptureContext
        {
            public ScreenshotCaptureContext(string snapshotPath)
            {
                SnapshotPath = snapshotPath;
            }

            public string SnapshotPath { get; }

            public TaskCompletionSource<ScreenshotCaptureResult> CompletionSource { get; } =
                new TaskCompletionSource<ScreenshotCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class ScreenshotCaptureResult
        {
            public bool Requested { get; set; }
            public bool Attempted { get; set; }
            public bool Captured { get; set; }
            public string Status { get; set; }
            public string Path { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public string Error { get; set; }
        }
    }
}
