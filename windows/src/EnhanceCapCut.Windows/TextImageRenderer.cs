using System.Numerics;
using EnhanceCapCut.Draft;
using EnhanceCapCut.Render;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace EnhanceCapCut.Windows;

/// <summary>
/// Renders CapCut text/subtitle materials with DirectWrite (port of the macOS TextRasterizer, same calibration in
/// <see cref="TextCalibration"/>). The result is a premultiplied bitmap whose text box is centered, like CapCut's.
/// Characters a font can't draw fall back to Windows' system fonts (CapCut's own fallback fonts aren't read yet).
/// </summary>
public sealed class TextImageRenderer(Graphics graphics) : IDisposable
{
    /// <summary>Used when a draft's font file isn't on this computer.</summary>
    const string FallbackFamily = "Segoe UI";
    readonly Dictionary<string, (IDWriteFontCollection Collection, string Family)?> fonts = [];

    sealed record Outline(int Start, int End, float Relative, Rgba Color);

    /// <summary>The text as a bitmap; <paramref name="scale"/> multiplies the canvas-relative size.</summary>
    public ID2D1Bitmap1? Render(TextLayerSpec spec, float canvasWidth, float scale)
    {
        var material = spec.Material;
        var text = spec.Content.Text;
        if (text.Length == 0) return null;
        float Unit(string? fontPath) => TextCalibration.Unit(fontPath, material.FontPath, canvasWidth, scale);
        var baseStyle = spec.Content.Styles.FirstOrDefault();
        var baseSize = (float)(baseStyle?.Size ?? material.FontSize) * Unit(baseStyle?.FontPath);
        if (baseSize <= 0) return null;
        var baseFont = Font(baseStyle?.FontPath ?? material.FontPath);

        using var format = graphics.Write.CreateTextFormat(baseFont?.Family ?? FallbackFamily, baseFont?.Collection,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, baseSize, "en-us");
        format.TextAlignment = material.Alignment switch { 0 => TextAlignment.Leading, 2 => TextAlignment.Trailing, _ => TextAlignment.Center };
        format.WordWrapping = WordWrapping.Wrap;
        // Break lines at the material's max line width (a fraction of the canvas width) and at newlines.
        var wrapWidth = (float)Math.Max(material.LineMaxWidth, 0.05) * TextCalibration.WrapFactor * canvasWidth * scale;
        using var layout = graphics.Write.CreateTextLayout(text, format, wrapWidth, 1_000_000);
        using var layout1 = layout.QueryInterfaceOrNull<IDWriteTextLayout1>();

        var brushes = new List<ID2D1SolidColorBrush>();
        var outlines = new List<Outline>();
        float maxFontSize = 0, strokeWidth = 0;
        var styles = spec.Content.Styles.Count == 0 ? [null] : spec.Content.Styles.Cast<TextStyle?>().ToList();
        foreach (var style in styles)
        {
            int lo = 0, hi = text.Length;
            if (style?.Range is { Count: 2 } r)
            {
                lo = Math.Clamp(r[0], 0, text.Length);
                hi = Math.Clamp(r[1], lo, text.Length);
            }
            if (hi <= lo) continue;
            var range = new TextRange((uint)lo, (uint)(hi - lo));
            var size = (float)(style?.Size ?? material.FontSize) * Unit(style?.FontPath);
            maxFontSize = Math.Max(maxFontSize, size);
            layout.SetFontSize(size, range);
            var font = Font(style?.FontPath ?? material.FontPath);
            layout.SetFontFamilyName(font?.Family ?? FallbackFamily, range);
            layout.SetFontCollection(font?.Collection, range);
            if ((style?.Bold ?? false) || material.BoldWidth > 0) layout.SetFontWeight(FontWeight.Bold, range);
            if ((style?.Italic ?? false) || material.ItalicDegree > 0) layout.SetFontStyle(FontStyle.Italic, range);
            if ((style?.Underline ?? false) || material.Underline) layout.SetUnderline(true, range);
            var alpha = (style?.FillAlpha ?? 1) * material.GlobalAlpha;
            var fill = style?.FillColor is { } c ? Rgba.FromRgb(c, alpha) : Rgba.FromHex(material.TextColor, alpha);
            var brush = Brush(fill);
            brushes.Add(brush);
            layout.SetDrawingEffect(brush, range);
            if (material.LetterSpacing != 0) layout1?.SetCharacterSpacing(0, (float)material.LetterSpacing * size, 0, range);
            if (style?.Strokes.FirstOrDefault() is { Width: > 0 } stroke)
            {
                var relative = (float)stroke.Width * TextCalibration.StrokeScale;
                strokeWidth = Math.Max(strokeWidth, relative * size);
                outlines.Add(new(lo, hi, relative, Rgba.FromRgb(stroke.Color, stroke.Alpha * material.GlobalAlpha)));
            }
        }
        if (outlines.Count == 0 && material.BorderColor.Length > 0 && material.BorderWidth > 0)
        {
            var relative = (float)material.BorderWidth * TextCalibration.StrokeScale;
            strokeWidth = relative * maxFontSize;
            outlines.Add(new(0, text.Length, relative, Rgba.FromHex(material.BorderColor, material.GlobalAlpha)));
        }
        if (maxFontSize <= 0) { brushes.ForEach(b => b.Dispose()); return null; }

        // CapCut spaces lines by about one font size, ignoring the font's own ascent/descent/leading.
        var pitch = baseSize * (TextCalibration.LineHeight + (float)material.LineSpacing);
        var (a, d) = AscentDescent(baseFont);
        var ascent = baseSize * a / Math.Max(a + d, 1e-3f);
        var descent = baseSize - ascent;
        layout.SetLineSpacing(LineSpacingMethod.Uniform, pitch, ascent);
        var metrics = layout.Metrics;
        var lines = Math.Max(1, (int)metrics.LineCount);
        var textWidth = MathF.Ceiling(metrics.Width);
        var textHeight = MathF.Ceiling((lines - 1) * pitch + ascent + descent);

        var hasBackground = material.BackgroundColor.Length > 0 && material.BackgroundAlpha > 0;
        var padX = hasBackground ? (float)material.BackgroundWidth * baseSize : 0;
        var padY = hasBackground ? (float)material.BackgroundHeight * baseSize : 0;
        var shadowOffset = material.HasShadow ? (float)material.ShadowDistance * baseSize / 18 : 0;
        var shadowBlur = material.HasShadow ? (float)material.ShadowSmoothing * baseSize / 3 : 0;
        // Glyphs can overhang the line boxes; keep room for them, the outline and the shadow.
        var margin = MathF.Ceiling(strokeWidth * 2 + shadowOffset + shadowBlur * 2 + maxFontSize * 0.4f + 4);
        float boxWidth = textWidth + padX * 2, boxHeight = textHeight + padY * 2;
        int width = (int)(boxWidth + margin * 2), height = (int)(boxHeight + margin * 2);
        if (width <= 0 || height <= 0 || width >= 16384 || height >= 16384) { brushes.ForEach(b => b.Dispose()); return null; }

        // Text (outline under fill, so the stroke only grows outward), then background and shadow around it.
        var origin = new Vector2(margin + padX - metrics.Left, margin + padY);
        var glyphs = graphics.CreateBitmap(width, height, BitmapOptions.Target);
        using (var fallbackBrush = Brush(new Rgba(1, 1, 1, 1)))
        {
            graphics.DrawInto(glyphs, dc =>
            {
                dc.Clear(new Color4(0, 0, 0, 0));
                if (outlines.Count > 0)
                {
                    using var renderer = new OutlineRenderer(graphics, outlines, Brush);
                    layout.Draw(IntPtr.Zero, renderer, origin.X, origin.Y);
                }
                dc.DrawTextLayout(origin, layout, fallbackBrush, DrawTextOptions.EnableColorFont);
            });
        }
        brushes.ForEach(b => b.Dispose());
        if (!hasBackground && !material.HasShadow) return glyphs;

        var result = graphics.CreateBitmap(width, height, BitmapOptions.Target);
        graphics.DrawInto(result, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            if (hasBackground)
            {
                var radius = (float)material.BackgroundRoundRadius * Math.Min(boxWidth, boxHeight) / 2;
                using var background = Brush(Rgba.FromHex(material.BackgroundColor, material.BackgroundAlpha * material.GlobalAlpha));
                dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(margin, margin, boxWidth, boxHeight), radius, radius), background);
            }
            if (material.HasShadow)
            {
                var angle = material.ShadowAngle * Math.PI / 180;
                var color = Rgba.FromHex(material.ShadowColor.Length == 0 ? "#000000" : material.ShadowColor,
                    material.ShadowAlpha * material.GlobalAlpha);
                using var shadow = new Shadow(dc) { BlurStandardDeviation = shadowBlur / 2, Color = new Vector4(color.R, color.G, color.B, color.A) };
                shadow.SetInput(0, glyphs, true);
                // CapCut's angle is counter-clockwise from +x with y up; Direct2D's y points down.
                var offset = new Vector2((float)(Math.Cos(angle) * shadowOffset), (float)(-Math.Sin(angle) * shadowOffset));
                dc.DrawImage(shadow, offset);
            }
            dc.DrawImage(glyphs);
        });
        glyphs.Dispose();
        return result;
    }

    /// <summary>A solid brush (Direct2D takes straight alpha here).</summary>
    ID2D1SolidColorBrush Brush(Rgba c) => graphics.Context.CreateSolidColorBrush(new Color4(c.R, c.G, c.B, c.A));

    /// <summary>Loads a draft's font file into its own font collection (cached); null when the file is missing.</summary>
    (IDWriteFontCollection Collection, string Family)? Font(string path)
    {
        if (fonts.TryGetValue(path, out var cached)) return cached;
        (IDWriteFontCollection, string)? result = null;
        if (path.Length > 0 && File.Exists(path))
        {
            try
            {
                using var file = graphics.Write.CreateFontFileReference(path, null);
                using var builder = graphics.Write.CreateFontSetBuilder();
                builder.AddFontFile(file);
                using var set = builder.CreateFontSet();
                var collection = graphics.Write.CreateFontCollectionFromFontSet(set);
                if (collection.FontFamilyCount > 0)
                {
                    using var family = collection.GetFontFamily(0);
                    using var names = family.FamilyNames;
                    result = (collection, names.GetString(0));
                }
                else
                {
                    collection.Dispose();
                }
            }
            catch (SharpGenException)
            {
                result = null;
            }
        }
        fonts[path] = result;
        return result;
    }

    /// <summary>The font's ascent and descent in em units.</summary>
    (float Ascent, float Descent) AscentDescent((IDWriteFontCollection Collection, string Family)? font)
    {
        try
        {
            IDWriteFontCollection collection;
            uint index = 0;
            if (font is { } f)
            {
                collection = f.Collection;
            }
            else
            {
                collection = graphics.Write.GetSystemFontCollection(false);
                collection.FindFamilyName(FallbackFamily, out index);
            }
            using var family = collection.GetFontFamily(index);
            using var match = family.GetFirstMatchingFont(FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);
            var m = match.Metrics;
            if (font is null) collection.Dispose();
            return ((float)m.Ascent / m.DesignUnitsPerEm, (float)m.Descent / m.DesignUnitsPerEm);
        }
        catch (SharpGenException)
        {
            return (0.8f, 0.2f);
        }
    }

    public void Dispose()
    {
        foreach (var font in fonts.Values) font?.Collection.Dispose();
        fonts.Clear();
    }

    /// <summary>
    /// Strokes each glyph run's outline (centered on the glyph edge, width = 2 × relative × font size, round joins)
    /// in the colour of the outline range it belongs to.
    /// </summary>
    sealed class OutlineRenderer(Graphics graphics, List<Outline> outlines, Func<Rgba, ID2D1SolidColorBrush> brush)
        : TextRendererBase
    {
        readonly ID2D1StrokeStyle round = graphics.Factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            LineJoin = LineJoin.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round,
            MiterLimit = 10,
        });

        public override void DrawGlyphRun(IntPtr clientDrawingContext, float baselineOriginX, float baselineOriginY,
            MeasuringMode measuringMode, GlyphRun glyphRun, GlyphRunDescription glyphRunDescription, IUnknown clientDrawingEffect)
        {
            var position = (int)glyphRunDescription.TextPosition;
            var outline = outlines.LastOrDefault(o => position >= o.Start && position < o.End);
            if (outline is null || glyphRun.FontFace is null || glyphRun.Indices is null) return;
            using var path = graphics.Factory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                glyphRun.FontFace.GetGlyphRunOutline(glyphRun.FontEmSize, glyphRun.Indices, glyphRun.Advances,
                    glyphRun.Offsets, glyphRun.IsSideways, glyphRun.BidiLevel % 2 == 1, sink);
                sink.Close();
            }
            var place = Matrix3x2.CreateTranslation(baselineOriginX, baselineOriginY);
            using var placed = graphics.Factory.CreateTransformedGeometry(path, ref place);
            using var b = brush(outline.Color);
            graphics.Context.DrawGeometry(placed, b, 2 * outline.Relative * glyphRun.FontEmSize, round);
        }

        public override Matrix3x2 GetCurrentTransform(IntPtr clientDrawingContext) => graphics.Context.Transform;
        public override float GetPixelsPerDip(IntPtr clientDrawingContext) => 1;
        public override RawBool IsPixelSnappingDisabled(IntPtr clientDrawingContext) => true;

        protected override void DisposeCore(bool disposing)
        {
            round.Dispose();
            base.DisposeCore(disposing);
        }
    }
}
