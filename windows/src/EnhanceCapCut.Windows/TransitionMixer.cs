using System.Numerics;
using EnhanceCapCut.Draft;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace EnhanceCapCut.Windows;

/// <summary>
/// Mixes the two clips of a transition (port of the macOS TransitionMixer). Both come in as "plates": the clip placed
/// on the frame (with the canvas background on the main track), frame-sized bitmaps.
/// </summary>
sealed class TransitionMixer(Graphics graphics, int width, int height) : IDisposable
{
    /// <summary>The outgoing and incoming clips' plates, drawn by the compositor.</summary>
    public ID2D1Bitmap1 A { get; } = graphics.CreateBitmap(width, height, BitmapOptions.Target);
    public ID2D1Bitmap1 B { get; } = graphics.CreateBitmap(width, height, BitmapOptions.Target);
    /// <summary>The mixed picture, drawn in the outgoing clip's place.</summary>
    public ID2D1Bitmap1 Result { get; } = graphics.CreateBitmap(width, height, BitmapOptions.Target);
    readonly ID2D1Bitmap1 dissolved = graphics.CreateBitmap(width, height, BitmapOptions.Target);

    public void Mix(TransitionKind kind, double p)
    {
        switch (kind)
        {
            case TransitionKind.CrossFade:
                Dissolve(Result, p);
                break;
            case TransitionKind.Flashback:
                Flashback(p);
                break;
        }
    }

    /// <summary>A × (1 − amount) + B × amount.</summary>
    void Dissolve(ID2D1Bitmap1 target, double amount)
    {
        var k = (float)Math.Clamp(amount, 0, 1);
        graphics.DrawInto(target, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            dc.PrimitiveBlend = PrimitiveBlend.Add;
            if (k < 1) dc.DrawBitmap(A, 1 - k, InterpolationMode.NearestNeighbor);
            if (k > 0) dc.DrawBitmap(B, k, InterpolationMode.NearestNeighbor);
            dc.PrimitiveBlend = PrimitiveBlend.SourceOver;
        });
    }

    /// <summary>See <see cref="FlashbackLook"/>: the dissolved picture averaged over copies zoomed and turned about the
    /// centre.</summary>
    void Flashback(double p)
    {
        if (FlashbackLook.Strength(p) <= 0.001)
        {
            Dissolve(Result, FlashbackLook.Mix(p));
            return;
        }
        Dissolve(dissolved, FlashbackLook.Mix(p));
        float cx = width / 2f, cy = height / 2f;
        graphics.DrawInto(Result, dc =>
        {
            dc.Clear(new Color4(0, 0, 0, 0));
            // Clamped edges fill what turning uncovers.
            using var brush = dc.CreateBitmapBrush(dissolved, new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp,
                InterpolationMode.Linear));
            for (var i = 0; i < FlashbackLook.BlurCopies; i++)
            {
                var (scale, rotation) = FlashbackLook.Copy(i, p);
                brush.Transform = Matrix3x2.CreateTranslation(-cx, -cy)
                    * Matrix3x2.CreateScale((float)scale)
                    * Matrix3x2.CreateRotation((float)(rotation * Math.PI / 180))
                    * Matrix3x2.CreateTranslation(cx, cy);
                // Source-over with opacity 1/(i+1) keeps a running average of the copies.
                brush.Opacity = 1f / (i + 1);
                dc.FillRectangle(new Vortice.RawRectF(0, 0, width, height), brush);
            }
        });
    }

    public void Dispose()
    {
        A.Dispose();
        B.Dispose();
        Result.Dispose();
        dissolved.Dispose();
    }
}
