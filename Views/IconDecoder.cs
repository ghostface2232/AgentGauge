using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Gauge.Views;

/// <summary>
/// Loads an <c>.ico</c> as a crisp, exact-size image source. A plain <see cref="BitmapImage"/>
/// decodes an icon's first directory entry (typically 16px) and upscales it — blurry — while
/// decoding the largest frame and letting the compositor shrink it shimmers, because that
/// runtime downscale is a plain bilinear undersample. Instead this picks the embedded frame
/// nearest the requested size and resamples it to the precise physical pixel size with WIC's
/// high-quality Fant filter, so the <c>Image</c> can draw it 1:1 with no further scaling and
/// stays clean at any DPI.
/// </summary>
internal static class IconDecoder
{
    /// <summary>
    /// Decodes the <c>.ico</c> at <paramref name="path"/> to a <paramref name="targetPx"/>-square
    /// source. Returns <c>null</c> on any IO/decode failure so callers can leave the current image
    /// in place. Callers own the staleness/dedupe guards (the decode is async and may be superseded).
    /// </summary>
    public static async Task<SoftwareBitmapSource?> LoadScaledAsync(string path, uint targetPx)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            // Smallest frame that still covers the target (so Fant resamples down, never
            // up), falling back to the largest frame.
            uint chosenIndex = 0, chosenWidth = 0, largestIndex = 0, largestWidth = 0;
            for (uint i = 0; i < decoder.FrameCount; i++)
            {
                var frame = await decoder.GetFrameAsync(i);
                var w = frame.PixelWidth;
                if (w > largestWidth) { largestWidth = w; largestIndex = i; }
                if (w >= targetPx && (chosenWidth == 0 || w < chosenWidth))
                {
                    chosenWidth = w;
                    chosenIndex = i;
                }
            }
            var bestIndex = chosenWidth > 0 ? chosenIndex : largestIndex;

            var best = await decoder.GetFrameAsync(bestIndex);
            var transform = new BitmapTransform
            {
                ScaledWidth = targetPx,
                ScaledHeight = targetPx,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var softwareBitmap = await best.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);
            return source;
        }
        catch
        {
            return null;
        }
    }
}
