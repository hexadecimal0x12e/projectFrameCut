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
            var stacks = renderer.FrameProcessStacks.ToArray();

            var prepareByFrame = prepare.ToDictionary(k => k.Key, v => v.Value);
            var renderByFrame = render.ToDictionary(k => k.Key, v => v.Value);
            var stackByFrame = stacks.ToDictionary(k => k.Key, v => v.Value);

            using var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            // Single CSV with two row kinds: frame + step.
            writer.WriteLine(string.Join(',', new[]
            {
                "RowType",
                "FrameIndex",
                "PrepareMs",
                "RenderMs",
                "StepPath",
                "StepKey",
                "StepElapsedMs",
                "OperationDisplayName",
                "StepName",
                "Operator",
                "PropertiesJson"
            }));

            var start = renderer.StartFrame;
            var endExclusive = renderer.StartFrame + renderer.Duration;
            for (uint frame = start; frame < endExclusive; frame++)
            {
                double? prepMs = prepareByFrame.TryGetValue(frame, out var prepTs) ? prepTs.TotalMilliseconds : null;
                double? renderMs = renderByFrame.TryGetValue(frame, out var renderTs) ? renderTs.TotalMilliseconds : null;

                WriteRow(writer,
                    rowType: "frame",
                    frameIndex: frame,
                    prepareMs: prepMs,
                    renderMs: renderMs,
                    stepPath: null,
                    stepKey: null,
                    stepElapsedMs: null,
                    operationDisplayName: null,
                    stepName: null,
                    op: null,
                    propertiesJson: null);

                if (!stackByFrame.TryGetValue(frame, out var frameStack) || frameStack is null) continue;

                foreach (var (step, path) in FlattenStacksWithPath(frameStack, ""))
                {
                    if (step?.Elapsed is not TimeSpan elapsed) continue;

                    var stepKey = GetStepKey(step);
                    var propsJson = step.Properties is null ? null : JsonSerializer.Serialize(step.Properties);

                    WriteRow(writer,
                        rowType: "step",
                        frameIndex: frame,
                        prepareMs: null,
                        renderMs: null,
                        stepPath: path,
                        stepKey: stepKey,
                        stepElapsedMs: elapsed.TotalMilliseconds,
                        operationDisplayName: step.OperationDisplayName,
                        stepName: step.StepUsed?.Name,
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
            if (string.IsNullOrWhiteSpace(name)) name = step.StepUsed?.Name;
            if (string.IsNullOrWhiteSpace(name)) name = step.Operator?.Name;
            return string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;
        }

        private static void WriteRow(
            StreamWriter writer,
            string rowType,
            uint frameIndex,
            double? prepareMs,
            double? renderMs,
            string? stepPath,
            string? stepKey,
            double? stepElapsedMs,
            string? operationDisplayName,
            string? stepName,
            string? op,
            string? propertiesJson)
        {
            writer.Write(Csv(rowType));
            writer.Write(',');
            writer.Write(frameIndex.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(CsvNumber(prepareMs));
            writer.Write(',');
            writer.Write(CsvNumber(renderMs));
            writer.Write(',');
            writer.Write(Csv(stepPath));
            writer.Write(',');
            writer.Write(Csv(stepKey));
            writer.Write(',');
            writer.Write(CsvNumber(stepElapsedMs));
            writer.Write(',');
            writer.Write(Csv(operationDisplayName));
            writer.Write(',');
            writer.Write(Csv(stepName));
            writer.Write(',');
            writer.Write(Csv(op));
            writer.Write(',');
            writer.Write(Csv(propertiesJson));
            writer.WriteLine();
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
    }
}
