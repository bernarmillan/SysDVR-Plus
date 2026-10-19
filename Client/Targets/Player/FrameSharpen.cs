using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SysDVR.Client.Targets.Player
{
    /// <summary>
    /// Unsharp mask applied to the luma plane of an upscaled frame to restore some of the
    /// definition lost when enlarging the 720p source. Uses a separable 1-2-1 blur so only a
    /// single scratch buffer the size of the frame is needed, the amplification factor comes
    /// from a lookup table built once on construction. Chroma planes are left untouched
    /// since the eye is far less sensitive to their detail.
    ///
    /// Both passes work one output row at a time and never read a row they write, so they
    /// split cleanly in horizontal bands without needing any padding. The vertical pass
    /// reads its neighbours from the scratch buffer, which is fully written by the horizontal
    /// pass, so the two passes can not run at the same time and are separated by the barrier
    /// Parallel.For puts at the end of every call.
    /// </summary>
    unsafe class FrameSharpen : IDisposable
    {
        readonly int width;
        readonly int height;
        readonly int workers;
        readonly object sync = new();

        // Index = (source - blur) + 255, value = amount * that difference. Keeping this in a
        // lookup table avoids a float multiply for every pixel of the frame
        short* lut = null;
        byte* buffer = null;

        // Written only by the thread running Apply, read by the worker threads
        AVFrame* currentFrame;
        int currentStride;

        /// <param name="workerCount">Number of bands the frame is split in, use one to run
        /// the passes on a single thread. Zero picks a value from the cpu core count.</param>
        public FrameSharpen(int frameWidth, int frameHeight, float amount, int workerCount = 0)
        {
            width = frameWidth;
            height = frameHeight;

            if (width <= 2 || height <= 2 || amount <= 0)
                return;

            lut = (short*)NativeMemory.Alloc(512 * sizeof(short));

            for (int d = -255; d <= 255; d++)
                lut[d + 255] = (short)Math.Clamp((int)Math.Round(amount * d), -255, 255);

            workers = workerCount > 0
                ? workerCount
                : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

            // Every band needs at least a few rows to be worth the parallelization
            workers = Math.Min(workers, Math.Max(1, height / 8));
        }

        public bool IsEnabled => lut != null;

        public void Apply(AVFrame* frame)
        {
            if (lut == null || frame->data[0] == null || frame->width != width || frame->height != height)
                return;

            if (width <= 2 || height <= 2 || frame->linesize[0] < width)
                return;

            lock (sync)
            {
                if (buffer == null)
                    buffer = (byte*)NativeMemory.Alloc((nuint)((long)width * height));

                currentFrame = frame;
                currentStride = frame->linesize[0];

                if (workers <= 1)
                {
                    HorizontalBand(0);
                    VerticalBand(0);
                }
                else
                {
                    var options = new ParallelOptions() { MaxDegreeOfParallelism = workers };

                    Parallel.For(0, workers, options, HorizontalBand);
                    Parallel.For(0, workers, options, VerticalBand);
                }

                currentFrame = null;
            }
        }

        // blurred[x] = (p[x-1] + 2*p[x] + p[x+1]) / 4
        void HorizontalBand(int index)
        {
            AVFrame* frame = currentFrame;
            byte* src = frame->data[0];
            byte* tmp = buffer;
            int stride = currentStride;
            int w = width;

            int end = BandEnd(index);

            for (int y = BandStart(index); y < end; y++)
            {
                byte* row = src + (long)y * stride;
                byte* outRow = tmp + (long)y * w;

                int left = row[0];
                int center = row[0];
                int right = row[1];

                outRow[0] = (byte)((left + 2 * center + right + 2) >> 2);

                for (int x = 1; x < w - 1; x++)
                {
                    left = center;
                    center = row[x];
                    right = row[x + 1];
                    outRow[x] = (byte)((left + 2 * center + right + 2) >> 2);
                }

                outRow[w - 1] = (byte)((center + 2 * row[w - 1] + row[w - 1] + 2) >> 2);
            }
        }

        // Applied in place, the original luma row is still intact while the blurred values
        // are read from the scratch buffer
        void VerticalBand(int index)
        {
            AVFrame* frame = currentFrame;
            byte* src = frame->data[0];
            byte* tmp = buffer;
            int stride = currentStride;
            int w = width;
            short* table = lut;

            int end = BandEnd(index);

            for (int y = BandStart(index); y < end; y++)
            {
                byte* dstRow = src + (long)y * stride;

                byte* above = tmp + (long)(y > 0 ? y - 1 : 0) * w;
                byte* middle = tmp + (long)y * w;
                byte* below = tmp + (long)(y < height - 1 ? y + 1 : 0) * w;

                for (int x = 0; x < w; x++)
                {
                    int center = dstRow[x];
                    int blur = (above[x] + 2 * middle[x] + below[x] + 2) >> 2;
                    int value = center + table[center - blur + 255];

                    dstRow[x] = value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;
                }
            }
        }

        int BandStart(int index) => index * height / workers;
        int BandEnd(int index) => (index + 1) * height / workers;

        public void Dispose()
        {
            if (buffer != null)
            {
                NativeMemory.Free(buffer);
                buffer = null;
            }

            if (lut != null)
            {
                NativeMemory.Free(lut);
                lut = null;
            }
        }
    }
}
