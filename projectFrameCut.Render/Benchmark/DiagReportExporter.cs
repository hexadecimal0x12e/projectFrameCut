using projectFrameCut.Shared;
using projectFrameCut.Render.Rendering;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.Benchmark
{
    public static class DiagReportExporter
    {
        public static void ExportCsv(string diagReportPath, Renderer renderer)
        {
            if (string.IsNullOrWhiteSpace(diagReportPath)) return;

            var outputFile = ResolveCsvPath(diagReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

            // Snapshot to avoid concurrent collection issues.
            var prepare = renderer.FramePrepareElapsed.ToArray();
            var render = renderer.FrameRenderElapsed.ToArray();
            var dirty = renderer.FrameDirtyTime.ToArray();
            var stacks = renderer.FrameProcessStacks.ToArray();
            var diagnostics = renderer.FrameDiagnostics.ToArray();

            var prepareByFrame = prepare.ToDictionary(k => k.Key, v => v.Value);
            var renderByFrame = render.ToDictionary(k => k.Key, v => v.Value);
            var dirtyByFrame = dirty.ToDictionary(k => k.Key, v => v.Value);
            var stackByFrame = stacks.ToDictionary(k => k.Key, v => v.Value);
            var diagnosticsByFrame = diagnostics.ToDictionary(k => k.Key, v => v.Value);

            using var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            // Keep ProcessStack columns first; pipeline diagnostics use frame + stage rows.
            writer.WriteLine(string.Join(',', new[]
            {
                "RowType",
                "FrameIndex",
                "PrepareMs",
                "RenderMs",
                "FrameDirtyMs",
                "StepPath",
                "StepKey",
                "StepElapsedMs",
                "OperationDisplayName",
                "StepName",
                "Operator",
                "PropertiesJson",
                "FrameTotalMs",
                "FrameSucceeded",
                "StagePath",
                "StageParentPath",
                "StageDepth",
                "StageName",
                "StageCategory",
                "StageElapsedMs",
                "StageSelfMs",
                "StageChildCount",
                "StageIsWorkerSum"
            }));

            for (ulong i = renderer.StartFrame, end = (ulong)renderer.StartFrame + renderer.Duration; i < end; i++)
            {
                uint frame = (uint)i;
                double? prepMs = prepareByFrame.TryGetValue(frame, out var prepTs) ? prepTs.TotalMilliseconds : null;
                double? renderMs = renderByFrame.TryGetValue(frame, out var renderTs) ? renderTs.TotalMilliseconds : null;
                double? frameDirtyMs = dirtyByFrame.TryGetValue(frame, out var dirtyTs) ? dirtyTs.TotalMilliseconds : null;
                diagnosticsByFrame.TryGetValue(frame, out var diagnostic);

                WriteRow(writer,
                    rowType: "frame",
                    frameIndex: frame,
                    prepareMs: prepMs,
                    renderMs: renderMs,
                    frameDirtyMs: frameDirtyMs,
                    diagnostic: diagnostic);

                if (diagnostic is not null)
                {
                    foreach (var stage in diagnostic.Stages)
                        WriteRow(writer, rowType: "stage", frameIndex: frame, stage: stage);
                }

                if (!stackByFrame.TryGetValue(frame, out var frameStack) || frameStack is null) continue;

                foreach (var (step, path) in FlattenStacksWithPath(frameStack, ""))
                {
                    if (step?.Elapsed is not TimeSpan elapsed) continue;

                    var stepKey = GetStepKey(step);
                    var propsJson = step.Properties is null ? null : JsonSerializer.Serialize(step.Properties);

                    WriteRow(writer,
                        rowType: "step",
                        frameIndex: frame,
                        stepPath: path,
                        stepKey: stepKey,
                        stepElapsedMs: elapsed.TotalMilliseconds,
                        operationDisplayName: step.OperationDisplayName,
                        stepName: "NA Due to projectFrameCut.Drawing",
                        op: step.Operator?.FullName,
                        propertiesJson: propsJson);
                }
            }

            writer.Flush();
            Logger.Log($"[DiagReport] CSV exported: {outputFile}");
        }

        private static string ResolveCsvPath(string diagReportPath)
        {
            diagReportPath = diagReportPath.Trim().Trim('"');

            if (diagReportPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(diagReportPath);
            }

            var dir = Path.GetFullPath(diagReportPath);
            var file = $"diag_report_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            return Path.Combine(dir, file);
        }

        private static IEnumerable<(PictureProcessStack step, string path)> FlattenStacksWithPath(IEnumerable<PictureProcessStack>? steps, string prefix)
        {
            if (steps is null) yield break;

            int idx = 0;
            foreach (var step in steps)
            {
                var path = string.IsNullOrEmpty(prefix)
                    ? $"{idx}"
                    : $"{prefix}/{idx}";

                if (step is null)
                {
                    idx++;
                    continue;
                }

                yield return (step, path);

                if (step is OverlayedPictureProcessStack overlay)
                {
                    foreach (var s in FlattenStacksWithPath(overlay.TopSteps, path + "/Top")) yield return s;
                    foreach (var s in FlattenStacksWithPath(overlay.BaseSteps, path + "/Base")) yield return s;
                }

                idx++;
            }
        }

        private static string GetStepKey(PictureProcessStack step)
        {
            var name = step.OperationDisplayName;
            if (string.IsNullOrWhiteSpace(name)) name = step.Operator?.Name;
            return string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;
        }

        private static void WriteRow(
            StreamWriter writer,
            string rowType,
            uint frameIndex,
            double? prepareMs = null,
            double? renderMs = null,
            double? frameDirtyMs = null,
            string? stepPath = null,
            string? stepKey = null,
            double? stepElapsedMs = null,
            string? operationDisplayName = null,
            string? stepName = null,
            string? op = null,
            string? propertiesJson = null,
            RenderFrameDiagnosticSnapshot? diagnostic = null,
            RenderStageDiagnosticSnapshot? stage = null)
        {
            writer.WriteLine(string.Join(',', new[]
            {
                Csv(rowType),
                frameIndex.ToString(CultureInfo.InvariantCulture),
                CsvNumber(prepareMs),
                CsvNumber(renderMs),
                CsvNumber(frameDirtyMs),
                Csv(stepPath),
                Csv(stepKey),
                CsvNumber(stepElapsedMs),
                Csv(operationDisplayName),
                Csv(stepName),
                Csv(op),
                Csv(propertiesJson),
                CsvNumber(diagnostic?.Elapsed.TotalMilliseconds),
                CsvBoolean(diagnostic?.Succeeded),
                Csv(stage?.Path),
                Csv(stage?.ParentPath),
                stage?.Depth.ToString(CultureInfo.InvariantCulture),
                Csv(stage?.Name),
                Csv(stage?.Category),
                CsvNumber(stage?.Elapsed.TotalMilliseconds),
                CsvNumber(stage?.SelfElapsed.TotalMilliseconds),
                stage?.ChildCount.ToString(CultureInfo.InvariantCulture),
                CsvBoolean(stage?.IsWorkerSum)
            }));
        }

        private static string Csv(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string CsvNumber(double? v)
        {
            if (v is null) return "";
            return v.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string CsvBoolean(bool? v)
        {
            if (v is null) return "";
            return v.Value ? "true" : "false";
        }
    }
}
