#if WINDOWS
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.Numerics;
using Windows.Graphics.DirectX;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using Point = projectFrameCut.Drawing.Vector.Point;

namespace projectFrameCut.Render.HwAccelEngine.VectorRasterizer.Windows
{
    /// <summary>
    /// Win2D (Direct2D) based vector rasterizer. Draws the vector picture onto an
    /// offscreen <see cref="CanvasRenderTarget"/> using the GPU rasterizer built into
    /// Direct2D, then reads the pixels back into a <see cref="Picture16bpp"/>.
    /// Preferred over the ILGPU compute path on Windows; callers should fall back to
    /// <see cref="ILGpuVectorRasterizer"/> when this throws.
    /// </summary>
    internal static class Win2DVectorRasterizer
    {
        private static readonly object s_deviceLock = new();
        private static CanvasDevice? s_device;

        private static CanvasDevice GetDevice()
        {
            lock (s_deviceLock)
            {
                s_device ??= CanvasDevice.GetSharedDevice();
                return s_device;
            }
        }

        private static void DropDevice()
        {
            lock (s_deviceLock)
            {
                s_device = null;
            }
        }

        /// <summary>
        /// Render <paramref name="canvas"/> at width x height. Direct2D per-primitive
        /// antialiasing is used when <paramref name="antialias"/> is true (no supersampling).
        /// Throws when no D2D device is available or the size exceeds device limits.
        /// </summary>
        public static IPicture Render(VectorPicture canvas, int width, int height,
            bool transparentBackground, bool antialias, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                return RenderCore(canvas, width, height, transparentBackground, antialias, ct, sw);
            }
            catch (Exception ex) when (GetDevice().IsDeviceLost(ex.HResult))
            {
                // Device lost (driver reset, adapter removed, ...): recreate once and retry.
                Logger.Log("Win2D device lost during vector rasterization; recreating device and retrying.", "warning");
                DropDevice();
                return RenderCore(canvas, width, height, transparentBackground, antialias, ct, sw);
            }
        }

        private static IPicture RenderCore(VectorPicture canvas, int width, int height,
            bool transparentBackground, bool antialias, CancellationToken ct, Stopwatch sw)
        {
            ct.ThrowIfCancellationRequested();
            var device = GetDevice();

            if ((uint)width > device.MaximumBitmapSizeInPixels || (uint)height > device.MaximumBitmapSizeInPixels)
                throw new InvalidOperationException(
                    $"Requested size {width}x{height} exceeds the Direct2D bitmap limit of {device.MaximumBitmapSizeInPixels}px.");

            // Prefer a 16-bit-per-channel target to preserve Picture16bpp precision;
            // fall back to 8-bit BGRA when the hardware doesn't support it.
            CanvasRenderTarget rt;
            bool sixteenBit;
            try
            {
                rt = new CanvasRenderTarget(device, width, height, 96f,
                    DirectXPixelFormat.R16G16B16A16UIntNormalized, CanvasAlphaMode.Premultiplied);
                sixteenBit = true;
            }
            catch (Exception)
            {
                rt = new CanvasRenderTarget(device, width, height, 96f,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
                sixteenBit = false;
            }

            using (rt)
            {
                using (var ds = rt.CreateDrawingSession())
                {
                    ds.Antialiasing = antialias ? CanvasAntialiasing.Antialiased : CanvasAntialiasing.Aliased;
                    // Opaque mode composites over white, matching the ILGPU/CPU rasterizers.
                    ds.Clear(transparentBackground ? Vector4.Zero : Vector4.One);

                    DrawElements(ds, device, canvas, width, height, ct);
                }

                ct.ThrowIfCancellationRequested();
                var result = ReadBack(rt, width, height, sixteenBit, transparentBackground);
                Logger.LogDiagnostic($"Win2D rasterization of {canvas.Elements.Count} elements at {width}x{height} took {sw.ElapsedMilliseconds}ms.");
                return result;
            }
        }

        // -----------------------------------------------------------------
        // Element / segment drawing
        // -----------------------------------------------------------------

        private static void DrawElements(CanvasDrawingSession ds, CanvasDevice device,
            VectorPicture canvas, int renderWidth, int renderHeight, CancellationToken ct)
        {
            // Same element transform semantics as PrimitiveBuilder.Build.
            foreach (var element in canvas.Elements.OrderBy(e => e.LayerIndex))
            {
                ct.ThrowIfCancellationRequested();

                float ox, oy, sx, sy;
                if (element.UseUniformScale)
                {
                    float us = Math.Min(renderWidth, renderHeight);
                    sx = us; sy = us;
                    ox = element.BaseX * renderWidth + element.RelativeX * us;
                    oy = element.BaseY * renderHeight + element.RelativeY * us;
                }
                else
                {
                    sx = renderWidth; sy = renderHeight;
                    ox = element.RelativeX * renderWidth;
                    oy = element.RelativeY * renderHeight;
                }

                float cosA = MathF.Cos(element.Rotation);
                float sinA = MathF.Sin(element.Rotation);
                bool rotated = MathF.Abs(element.Rotation) > 1e-6f;

                foreach (var seg in element.Draw())
                {
                    // Skip the rotation pass entirely for unrotated elements so
                    // rounded rectangles keep their exact analytic form.
                    var s = rotated ? PrimitiveBuilder.RotateSegment(seg, cosA, sinA) : seg;
                    DrawSegment(ds, device, s, ox, oy, sx, sy);
                }
            }
        }

        private static void DrawSegment(CanvasDrawingSession ds, CanvasDevice device, VectorSegment seg,
            float ox, float oy, float sx, float sy)
        {
            switch (seg)
            {
                case StraightLineVectorSegment s:
                    DrawStraightLine(ds, s, ox, oy, sx, sy);
                    break;
                case RoundedRectangleVectorSegment s:
                    DrawRoundedRect(ds, s, ox, oy, sx, sy);
                    break;
                case RectangleVectorSegment s:
                    DrawRect(ds, s, ox, oy, sx, sy);
                    break;
                case EllipseVectorSegment s:
                    DrawEllipse(ds, s, ox, oy, sx, sy);
                    break;
                case CubicBezierVectorSegment s:
                    DrawCubicBezier(ds, device, s, ox, oy, sx, sy);
                    break;
                case QuadraticBezierVectorSegment s:
                    DrawQuadraticBezier(ds, device, s, ox, oy, sx, sy);
                    break;
                case ArcVectorSegment s:
                    DrawArc(ds, device, s, ox, oy, sx, sy);
                    break;
                case PolygonVectorSegment s:
                    DrawPolygon(ds, device, s, ox, oy, sx, sy);
                    break;
                case PolylineVectorSegment s:
                    DrawPolyline(ds, device, s, ox, oy, sx, sy);
                    break;
            }
        }

        private static float CX(float segX, float ox, float sx) => ox + segX * sx;
        private static float CY(float segY, float oy, float sy) => oy + segY * sy;

        /// <summary>16-bit channel + float alpha to a high-precision D2D color.</summary>
        private static Vector4 StraightColor(ushort r, ushort g, ushort b, float a)
            => new(r / 65535f, g / 65535f, b / 65535f, a);

        private static Vector4 StrokeColor(VectorSegment s) => StraightColor(s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA);
        private static Vector4 FillColor(VectorSegment s) => StraightColor(s.FillR, s.FillG, s.FillB, s.FillA);

        /// <summary>Solid brush carrying the full 16-bit color precision via ColorHdr.</summary>
        private static CanvasSolidColorBrush Brush(CanvasDrawingSession ds, Vector4 color)
            => new(ds, default) { ColorHdr = color };

        private static CanvasSolidColorBrush StrokeBrush(CanvasDrawingSession ds, VectorSegment s) => Brush(ds, StrokeColor(s));
        private static CanvasSolidColorBrush FillBrush(CanvasDrawingSession ds, VectorSegment s) => Brush(ds, FillColor(s));

        private static bool HasStroke(VectorSegment s) => s.Thickness > 0f && s.StrokeA > 0f;
        private static bool HasFill(VectorSegment s) => s.FillA > 0f;

        /// <summary>Round caps/joins to match the distance-to-segment stroke model of the ILGPU/CPU paths.</summary>
        private static readonly CanvasStrokeStyle s_roundStroke = new()
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            DashCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round,
        };

        private static void DrawStraightLine(CanvasDrawingSession ds, StraightLineVectorSegment s,
            float ox, float oy, float sx, float sy)
        {
            if (!HasStroke(s)) return;
            using var brush = StrokeBrush(ds, s);
            ds.DrawLine(
                new Vector2(CX(s.X1, ox, sx), CY(s.Y1, oy, sy)),
                new Vector2(CX(s.X2, ox, sx), CY(s.Y2, oy, sy)),
                brush, s.Thickness, s_roundStroke);
        }

        private static void DrawRect(CanvasDrawingSession ds, RectangleVectorSegment s,
            float ox, float oy, float sx, float sy)
        {
            float x = CX(s.X, ox, sx);
            float y = CY(s.Y, oy, sy);
            float w = s.Width * sx;
            float h = s.Height * sy;

            if (HasFill(s))
            {
                using var fill = FillBrush(ds, s);
                ds.FillRectangle(x, y, w, h, fill);
            }
            if (HasStroke(s))
            {
                using var stroke = StrokeBrush(ds, s);
                ds.DrawRectangle(x, y, w, h, stroke, s.Thickness, s_roundStroke);
            }
        }

        private static void DrawRoundedRect(CanvasDrawingSession ds, RoundedRectangleVectorSegment s,
            float ox, float oy, float sx, float sy)
        {
            float x = CX(s.X, ox, sx);
            float y = CY(s.Y, oy, sy);
            float w = s.Width * sx;
            float h = s.Height * sy;
            float radius = s.CornerRadius * Math.Min(sx, sy);
            radius = Math.Min(radius, Math.Min(w, h) * 0.5f);
            if (radius < 0f) radius = 0f;

            if (HasFill(s))
            {
                using var fill = FillBrush(ds, s);
                ds.FillRoundedRectangle(x, y, w, h, radius, radius, fill);
            }
            if (HasStroke(s))
            {
                using var stroke = StrokeBrush(ds, s);
                ds.DrawRoundedRectangle(x, y, w, h, radius, radius, stroke, s.Thickness, s_roundStroke);
            }
        }

        private static void DrawEllipse(CanvasDrawingSession ds, EllipseVectorSegment s,
            float ox, float oy, float sx, float sy)
        {
            float cx = CX(s.X, ox, sx);
            float cy = CY(s.Y, oy, sy);
            float rx = s.RadiusX * sx;
            float ry = s.RadiusY * sy;
            if (rx <= 0f || ry <= 0f) return;

            if (HasFill(s))
            {
                using var fill = FillBrush(ds, s);
                ds.FillEllipse(cx, cy, rx, ry, fill);
            }
            if (HasStroke(s))
            {
                using var stroke = StrokeBrush(ds, s);
                ds.DrawEllipse(cx, cy, rx, ry, stroke, s.Thickness, s_roundStroke);
            }
        }

        private static void DrawCubicBezier(CanvasDrawingSession ds, CanvasDevice device, CubicBezierVectorSegment s,
            float ox, float oy, float sx, float sy)
        {
            if (!HasStroke(s)) return;
            using var pb = new CanvasPathBuilder(device);
            pb.BeginFigure(CX(s.X1, ox, sx), CY(s.Y1, oy, sy));
            pb.AddCubicBezier(
                new Vector2(CX(s.X2, ox, sx), CY(s.Y2, oy, sy)),
                new Vector2(CX(s.X3, ox, sx), CY(s.Y3, oy, sy)),
                new Vector2(CX(s.X4, ox, sx), CY(s.Y4, oy, sy)));
            pb.EndFigure(CanvasFigureLoop.Open);
            using var geo = CanvasGeometry.CreatePath(pb);
            using var brush = StrokeBrush(ds, s);
            ds.DrawGeometry(geo, brush, s.Thickness, s_roundStroke);
        }

        private static void DrawQuadraticBezier(CanvasDrawingSession ds, CanvasDevice device, QuadraticBezierVectorSegment s,
            float ox, float oy, float sx, float sy)
        {
            if (!HasStroke(s)) return;
            using var pb = new CanvasPathBuilder(device);
            pb.BeginFigure(CX(s.X1, ox, sx), CY(s.Y1, oy, sy));
            pb.AddQuadraticBezier(
                new Vector2(CX(s.X2, ox, sx), CY(s.Y2, oy, sy)),
                new Vector2(CX(s.X3, ox, sx), CY(s.Y3, oy, sy)));
            pb.EndFigure(CanvasFigureLoop.Open);
            using var geo = CanvasGeometry.CreatePath(pb);
            using var brush = StrokeBrush(ds, s);
            ds.DrawGeometry(geo, brush, s.Thickness, s_roundStroke);
        }

        private static void DrawArc(CanvasDrawingSession ds, CanvasDevice device, ArcVectorSegment s,
            float ox, float oy, float sx, float sy)
        {
            if (!HasStroke(s)) return;
            float cx = CX(s.X, ox, sx);
            float cy = CY(s.Y, oy, sy);
            float rx = s.RadiusX * sx;
            float ry = s.RadiusY * sy;
            if (rx <= 0f || ry <= 0f) return;

            using var pb = new CanvasPathBuilder(device);
            pb.BeginFigure(
                cx + rx * MathF.Cos(s.StartAngle),
                cy + ry * MathF.Sin(s.StartAngle));
            pb.AddArc(new Vector2(cx, cy), rx, ry, s.StartAngle, s.SweepAngle);
            pb.EndFigure(CanvasFigureLoop.Open);
            using var geo = CanvasGeometry.CreatePath(pb);
            using var brush = StrokeBrush(ds, s);
            ds.DrawGeometry(geo, brush, s.Thickness, s_roundStroke);
        }

        private static void DrawPolygon(CanvasDrawingSession ds, CanvasDevice device, PolygonVectorSegment s,
            float ox, float oy, float sx, float sy)
        {
            var pts = s.Points;
            if (pts.Length < 3) return;

            if (HasFill(s))
            {
                using var pb = new CanvasPathBuilder(device);
                // Non-zero winding with holes as extra contours, matching the
                // ILGPU kernel and the CPU scanline filler.
                pb.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
                AddClosedContour(pb, pts, ox, oy, sx, sy);
                if (s.Holes is { Length: > 0 })
                {
                    foreach (var hole in s.Holes)
                    {
                        if (hole.Length < 3) continue;
                        AddClosedContour(pb, hole, ox, oy, sx, sy);
                    }
                }
                using var geo = CanvasGeometry.CreatePath(pb);
                using var fill = FillBrush(ds, s);
                ds.FillGeometry(geo, fill);
            }

            if (HasStroke(s))
            {
                using var pb = new CanvasPathBuilder(device);
                AddClosedContour(pb, pts, ox, oy, sx, sy);
                using var geo = CanvasGeometry.CreatePath(pb);
                using var stroke = StrokeBrush(ds, s);
                ds.DrawGeometry(geo, stroke, s.Thickness, s_roundStroke);
            }
        }

        private static void AddClosedContour(CanvasPathBuilder pb, Point[] pts,
            float ox, float oy, float sx, float sy)
        {
            pb.BeginFigure(CX(pts[0].X, ox, sx), CY(pts[0].Y, oy, sy));
            for (int i = 1; i < pts.Length; i++)
                pb.AddLine(CX(pts[i].X, ox, sx), CY(pts[i].Y, oy, sy));
            pb.EndFigure(CanvasFigureLoop.Closed);
        }

        private static void DrawPolyline(CanvasDrawingSession ds, CanvasDevice device, PolylineVectorSegment s,
            float ox, float oy, float sx, float sy)
        {
            if (!HasStroke(s) || s.Points.Length < 2) return;
            var pts = s.Points;
            using var pb = new CanvasPathBuilder(device);
            pb.BeginFigure(CX(pts[0].X, ox, sx), CY(pts[0].Y, oy, sy));
            for (int i = 1; i < pts.Length; i++)
                pb.AddLine(CX(pts[i].X, ox, sx), CY(pts[i].Y, oy, sy));
            pb.EndFigure(CanvasFigureLoop.Open);
            using var geo = CanvasGeometry.CreatePath(pb);
            using var brush = StrokeBrush(ds, s);
            ds.DrawGeometry(geo, brush, s.Thickness, s_roundStroke);
        }

        // -----------------------------------------------------------------
        // Pixel read-back
        // -----------------------------------------------------------------

        private static Picture16bpp ReadBack(CanvasRenderTarget rt, int width, int height,
            bool sixteenBit, bool transparentBackground)
        {
            var bytes = rt.GetPixelBytes();
            int pixels = width * height;

            var outR = new ushort[pixels];
            var outG = new ushort[pixels];
            var outB = new ushort[pixels];
            var outA = transparentBackground ? new float[pixels] : null;

            if (sixteenBit)
            {
                // R16G16B16A16UIntNormalized, premultiplied.
                Parallel.For(0, height, y =>
                {
                    var span = System.Runtime.InteropServices.MemoryMarshal
                        .Cast<byte, ushort>(bytes.AsSpan(y * width * 8, width * 8));
                    int rowBase = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int i = rowBase + x;
                        int o = x * 4;
                        ushort r = span[o + 0], g = span[o + 1], b = span[o + 2], a = span[o + 3];
                        if (outA is null)
                        {
                            // Opaque background: alpha is 1 everywhere.
                            outR[i] = r; outG[i] = g; outB[i] = b;
                        }
                        else
                        {
                            float af = a / 65535f;
                            outA[i] = af;
                            if (a > 0)
                            {
                                // Un-premultiply to straight alpha.
                                outR[i] = (ushort)Math.Min(65535, r * 65535 / a);
                                outG[i] = (ushort)Math.Min(65535, g * 65535 / a);
                                outB[i] = (ushort)Math.Min(65535, b * 65535 / a);
                            }
                        }
                    }
                });
            }
            else
            {
                // B8G8R8A8UIntNormalized, premultiplied.
                Parallel.For(0, height, y =>
                {
                    int rowBase = y * width;
                    int byteBase = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int i = rowBase + x;
                        int o = byteBase + x * 4;
                        byte b8 = bytes[o + 0], g8 = bytes[o + 1], r8 = bytes[o + 2], a8 = bytes[o + 3];
                        if (outA is null)
                        {
                            outR[i] = (ushort)(r8 * 257);
                            outG[i] = (ushort)(g8 * 257);
                            outB[i] = (ushort)(b8 * 257);
                        }
                        else
                        {
                            float af = a8 / 255f;
                            outA[i] = af;
                            if (a8 > 0)
                            {
                                outR[i] = (ushort)Math.Min(65535, r8 * 255 * 257 / a8);
                                outG[i] = (ushort)Math.Min(65535, g8 * 255 * 257 / a8);
                                outB[i] = (ushort)Math.Min(65535, b8 * 255 * 257 / a8);
                            }
                        }
                    }
                });
            }

            bool hasAlpha = false;
            if (outA is not null)
            {
                foreach (var a in outA)
                {
                    if (a < 1f) { hasAlpha = true; break; }
                }
            }

            return new Picture16bpp(width, height)
            {
                r = outR,
                g = outG,
                b = outB,
                a = hasAlpha ? outA : null,
                HasAlphaChannel = hasAlpha,
            };
        }
    }
}
#endif
