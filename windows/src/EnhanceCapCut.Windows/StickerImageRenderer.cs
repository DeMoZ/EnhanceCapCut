using System.Numerics;
using EnhanceCapCut.Draft;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace EnhanceCapCut.Windows;

/// <summary>Draws natively rendered stickers ("Clock Time", see <see cref="ClockSticker"/>) with Direct2D (port of
/// the macOS StickerRenderer).</summary>
public sealed class StickerImageRenderer(Graphics graphics) : IDisposable
{
    readonly ID2D1StrokeStyle round = graphics.Factory.CreateStrokeStyle(new StrokeStyleProperties
    {
        LineJoin = LineJoin.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round,
        MiterLimit = 10,
    });

    /// <summary>Draws the clock <paramref name="offset"/> µs after its segment start into <paramref name="target"/>
    /// (a square bitmap; its width is the outer diameter).</summary>
    public void Render(ClockSticker clock, ID2D1Bitmap1 target, long offset)
    {
        var n = target.PixelSize.Width;
        graphics.DrawInto(target, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            ID2D1SolidColorBrush Brush(double[] c) => dc.CreateSolidColorBrush(new Color4((float)c[0], (float)c[1], (float)c[2], 1));
            var r = n / 2f;
            var center = new Vector2(r, r);
            var ring = (float)clock.RingWidth * r;
            var circle = new Ellipse(center, r - ring / 2, r - ring / 2);
            if (clock.FaceColor is { } face)
            {
                using var faceBrush = Brush(face);
                dc.FillEllipse(circle, faceBrush);
            }
            using (var ringBrush = Brush(clock.RingColor)) dc.DrawEllipse(circle, ringBrush, ring);

            var width = (float)clock.HandWidth * r;
            var (minute, hour) = ClockSticker.HandAngles(offset);
            // Direct2D is y-down: clockwise from 12 o'clock is (sin a, -cos a).
            Vector2 Tip(double degrees, double length)
            {
                var a = degrees * Math.PI / 180;
                var l = (float)length * r - width / 2;
                return new Vector2(center.X + l * (float)Math.Sin(a), center.Y - l * (float)Math.Cos(a));
            }
            using var path = graphics.Factory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.BeginFigure(Tip(minute, clock.MinuteHand), FigureBegin.Hollow);
                sink.AddLine(center);
                sink.AddLine(Tip(hour, clock.HourHand));
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            using var handBrush = Brush(clock.HandColor);
            dc.DrawGeometry(path, handBrush, width, round);
        });
    }

    public void Dispose() => round.Dispose();
}
