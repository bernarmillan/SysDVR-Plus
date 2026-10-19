using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SysDVR.Client.Targets.Player
{
    /// <summary>
    /// Edge directed anti aliasing for the luma plane of the source frame.
    ///
    /// The console renders at 720p and the game itself is already aliased by the time it is
    /// captured, so the stair steps have to be cleaned up before upscaling and not after:
    /// the stair pattern lives at the source resolution, enlarging it first would make it
    /// 2.25 times more expensive for a worse result.
    ///
    /// For every pixel the 3x3 neighbourhood is examined. The luma difference along each axis
    /// decides whether the edge is vertical or horizontal, and the pixel is then blended with
    /// the 1-2-1 blur taken along that same axis. Blending across the edge is what actually
    /// removes the stair case, blurring along it would only soften the image. Flat areas are
    /// left completely untouched because the blend fades out once the local contrast drops
    /// below a threshold that scales with the local brightness.
    ///
    /// The frame is copied to a scratch buffer first and the result is read back from there,
    /// this way a pixel never depends on what its neighbours were rewritten to and the output
    /// is identical no matter how the bands are distributed between threads.
    /// </summary>
    unsafe class FrameAntiAlias : IDisposable
    {
        // Contrast needed before a pixel is considered to be on an edge, as a percentage of
        // the brightest pixel in the neighbourhood. Scaled this way dark edges still get
        // treated while plain gradients on a bright surface are left alone.
        const int EdgeThresholdPercent = 8;

        // Minimum contrast in absolute luma values, below this everything is noise
        const int EdgeThresholdMin = 4;

        // Local contrast past which the full strength is applied, values in between ramp up
        const int FullBlendContrast = 64;

        readonly int width;
        readonly int height;
        readonly int workers;
        readonly object sync = new();

        byte* scratch = null;

        // Written only by the thread running Apply, read by the worker threads
        AVFrame* currentFrame;
        int currentStride;
        int currentStrength;

        /// <param name="workerCount">Number of bands the frame is split in, use one to run
        /// on a single thread. Zero picks a value from the cpu core count.</param>
        public FrameAntiAlias(int frameWidth, int frameHeight, int workerCount = 0)
        {
            width = frameWidth;
            height = frameHeight;

            if (width < 3 || height < 3)
                return;

            scratch = (byte*)NativeMemory.Alloc((nuint)((long)width * height));

            workers = workerCount > 0
                ? workerCount
                : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

            // Every band needs at least a few rows to be worth the parallelization
            workers = Math.Min(workers, Math.Max(1, height / 8));
        }

        public bool IsEnabled => scratch != null;

        /// <param name="strength">Blend strength from 0 to 255, 0 disables the filter entirely
        /// and 255 fully blends every detected edge.</param>
        public void Apply(AVFrame* frame, int strength)
        {
            if (scratch == null || strength <= 0)
                return;

            if (frame->data[0] == null || frame->width != width || frame->height != height)
                return;

            if (frame->linesize[0] < width)
                return;

            lock (sync)
            {
                // Snapshot the luma plane into the scratch buffer, the frame keeps the
                // untouched values in the rows and columns the filter does not write
                byte* src = frame->data[0];
                int stride = frame->linesize[0];

                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy(src + (long)y * stride, scratch + (long)y * width, width, width);

                currentFrame = frame;
                currentStride = stride;
                currentStrength = strength > 255 ? 255 : strength;

                if (workers <= 1)
                {
                    Band(0);
                }
                else
                {
                    var options = new ParallelOptions() { MaxDegreeOfParallelism = workers };
                    Parallel.For(0, workers, options, Band);
                }

                currentFrame = null;
            }
        }

        void Band(int index)
        {
            AVFrame* frame = currentFrame;
            byte* src = scratch;
            int w = width;
            int stride = currentStride;
            int strength = currentStrength;

            int start = BandStart(index);
            int end = BandEnd(index);

            // The outermost pixels have no full neighbourhood, they keep their original value
            if (start < 1)
                start = 1;
            if (end > height - 1)
                end = height - 1;

            for (int y = start; y < end; y++)
            {
                byte* above = src + (long)(y - 1) * w;
                byte* middle = src + (long)y * w;
                byte* below = src + (long)(y + 1) * w;

                byte* dstRow = frame->data[0] + (long)y * stride;

                for (int x = 1; x < w - 1; x++)
                {
                    int nw = above[x - 1], north = above[x], ne = above[x + 1];
                    int west = middle[x - 1], center = middle[x], east = middle[x + 1];
                    int sw = below[x - 1], south = below[x], se = below[x + 1];

                    int lumaMin = nw, lumaMax = nw;

                    if (north < lumaMin) lumaMin = north; else if (north > lumaMax) lumaMax = north;
                    if (ne < lumaMin) lumaMin = ne; else if (ne > lumaMax) lumaMax = ne;
                    if (west < lumaMin) lumaMin = west; else if (west > lumaMax) lumaMax = west;
                    if (east < lumaMin) lumaMin = east; else if (east > lumaMax) lumaMax = east;
                    if (sw < lumaMin) lumaMin = sw; else if (sw > lumaMax) lumaMax = sw;
                    if (south < lumaMin) lumaMin = south; else if (south > lumaMax) lumaMax = south;
                    if (se < lumaMin) lumaMin = se; else if (se > lumaMax) lumaMax = se;

                    int range = lumaMax - lumaMin;

                    int threshold = lumaMax * EdgeThresholdPercent / 255;
                    if (threshold < EdgeThresholdMin)
                        threshold = EdgeThresholdMin;

                    // Not on an edge, leave the pixel exactly as it was decoded
                    if (range < threshold)
                        continue;

                    // Sum of luma differences along each axis, weighted with the diagonals so
                    // corners are classified the same way straight edges are
                    int dirX = (ne + 2 * east + se) - (nw + 2 * west + sw);
                    int dirY = (sw + 2 * south + se) - (nw + 2 * north + ne);

                    int gx = dirX < 0 ? -dirX : dirX;
                    int gy = dirY < 0 ? -dirY : dirY;

                    int total = gx + gy;
                    if (total == 0)
                        continue;

                    int blurX = (west + 2 * center + east + 2) >> 2;
                    int blurY = (north + 2 * center + south + 2) >> 2;

                    // Weighted towards the blur taken across the edge, a diagonal edge ends up
                    // blending in both directions which is what keeps it from stepping again
                    int blur = (gx * blurX + gy * blurY + total / 2) / total;

                    int over = range - threshold;
                    int blend = over >= FullBlendContrast ? strength : over * strength / FullBlendContrast;
                    if (blend == 0)
                        continue;

                    int value = center + (blur - center) * blend / 255;

                    dstRow[x] = value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;
                }
            }
        }

        int BandStart(int index) => index * height / workers;
        int BandEnd(int index) => (index + 1) * height / workers;

        public void Dispose()
        {
            if (scratch != null)
            {
                NativeMemory.Free(scratch);
                scratch = null;
            }
        }
    }
}
