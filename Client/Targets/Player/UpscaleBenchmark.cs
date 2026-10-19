using FFmpeg.AutoGen;
using SysDVR.Client.Core;
using System;
using System.Diagnostics;
using static FFmpeg.AutoGen.ffmpeg;

namespace SysDVR.Client.Targets.Player
{
    // Measures how long the client side video pipeline takes per frame, and checks that the
    // threaded sharpen pass produces exactly the same image as the single threaded one. Pick
    // the filter and the target resolution with this so they fit in the 33ms a 30fps stream
    // has per frame.
    // Run it with `SysDVR-Plus.exe --bench-upscale`, it needs no console and no player.
    static unsafe class UpscaleBenchmark
    {
        const int WarmupFrames = 5;
        const int MeasuredFrames = 30;

        // The console always captures at 720p, these are the resolutions the client can
        // present it at
        static readonly (int Width, int Height, string Name)[] Targets =
        {
            (1920, 1080, "1080p"),
            (2560, 1440, "1440p"),
            (3840, 2160, "2160p"),
        };

        static readonly VideoUpscaleFilter[] Filters =
        {
            VideoUpscaleFilter.Linear,
            VideoUpscaleFilter.Nearest,
            VideoUpscaleFilter.Bicubic,
            VideoUpscaleFilter.Lanczos,
            VideoUpscaleFilter.LanczosSoft,
        };

        // Same sharpen strength the client uses by default
        const float DefaultSharpenAmount = 0.35f;
        // Same antialias strength the client uses by default
        const float DefaultAntiAliasAmount = 0.5f;

        static AVFrame* MakeFrame(int width, int height)
        {
            var frame = av_frame_alloc();
            if (frame == null)
                throw new Exception("Couldn't allocate a benchmark frame");

            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            frame->width = width;
            frame->height = height;

            av_frame_get_buffer(frame, 32).AssertZero("Couldn't allocate the buffer for a benchmark frame");

            return frame;
        }

        // A deterministic pattern with gradients, sharp edges and noise, this way the scaler
        // can not shortcut on flat areas and the timings stay comparable between runs
        static void FillSourceFrame(AVFrame* frame)
        {
            int w = frame->width;
            int h = frame->height;
            var rnd = new Random(1234);

            for (int y = 0; y < h; y++)
            {
                byte* row = frame->data[0] + (long)y * frame->linesize[0];

                for (int x = 0; x < w; x++)
                    row[x] = (byte)((x >> 3) ^ (y >> 3) ^ (x * y >> 12));

                for (int i = 0; i < 64; i++)
                    row[rnd.Next(w)] = (byte)rnd.Next(256);
            }

            for (uint plane = 1; plane < 3; plane++)
            {
                for (int y = 0; y < h / 2; y++)
                {
                    byte* row = frame->data[plane] + (long)y * frame->linesize[plane];

                    for (int x = 0; x < w / 2; x++)
                        row[x] = (byte)(128 + (x * y & 0x3F));
                }
            }
        }

        static void CopyFrame(AVFrame* from, AVFrame* to)
        {
            for (int plane = 0; plane < 3; plane++)
            {
                uint p = (uint)plane;
                int w = plane == 0 ? from->width : from->width / 2;
                int h = plane == 0 ? from->height : from->height / 2;

                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(from->data[p] + (long)y * from->linesize[p],
                        to->data[p] + (long)y * to->linesize[p], w, w);
            }
        }

        // Checks that two frames of the same size hold the very same bytes
        static string CheckResult(AVFrame* a, AVFrame* b)
        {
            int differing = 0;
            int maxDiff = 0;

            for (int plane = 0; plane < 3; plane++)
            {
                uint p = (uint)plane;
                int w = plane == 0 ? a->width : a->width / 2;
                int h = plane == 0 ? a->height : a->height / 2;

                for (int y = 0; y < h; y++)
                {
                    byte* ra = a->data[p] + (long)y * a->linesize[p];
                    byte* rb = b->data[p] + (long)y * b->linesize[p];

                    for (int x = 0; x < w; x++)
                    {
                        int d = ra[x] - rb[x];
                        if (d != 0)
                        {
                            differing++;
                            if (d < 0) d = -d;
                            if (d > maxDiff) maxDiff = d;
                        }
                    }
                }
            }

            return differing == 0 ? "exact" : $"DIFF {differing} px (max {maxDiff})";
        }

        // Plain single threaded scaler, this is what the client uses
        sealed class FrameScaler : IDisposable
        {
            readonly SwsContext* ctx;

            public FrameScaler(int sw, int sh, AVPixelFormat sf, int tw, int th, AVPixelFormat tf, VideoUpscaleFilter filter)
            {
                // Only the filters with a tunable kernel take a parameter array, the rest
                // must be left with the swscale defaults
                double* param = stackalloc double[2];
                param[0] = 3.0;
                param[1] = 3.0;

                if (LibavUtils.TryGetSwsParam(filter, out var tuned))
                    param[0] = tuned;
                else
                    param = null;

                ctx = sws_getContext(sw, sh, sf, tw, th, tf, LibavUtils.SwsFlagsFor(filter), null, null, param);

                if (ctx == null)
                    throw new Exception("Couldn't initialize the benchmark scaler");
            }

            public void Scale(AVFrame* source, AVFrame* target) =>
                sws_scale(ctx, source->data, source->linesize, 0, source->height, target->data, target->linesize);

            public void Dispose()
            {
                if (ctx != null)
                    sws_freeContext(ctx);
            }
        }

        public static void Run()
        {
            int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            double budget = 1000.0 / 30;

            Console.WriteLine();
            Console.WriteLine($"Stream resolution : {StreamInfo.VideoWidth}x{StreamInfo.VideoHeight}, " +
                "this is the most the console can capture, everything else is upscaling");
            Console.WriteLine($"Cpu               : {Environment.ProcessorCount} logical processors");
            Console.WriteLine($"Pass threads      : {workers}");
            Console.WriteLine($"Budget for 30 fps : {budget:0.00} ms per frame");
            Console.WriteLine($"Measured          : {MeasuredFrames} frames per row, antialias and sharpen passes included");
            Console.WriteLine();

            // The antialias always runs on the 720p source so its cost does not depend on the
            // target resolution, measure and verify it on its own before the scaling table
            double antiAliasMs = VerifyAntiAlias(workers);

            var source = MakeFrame(StreamInfo.VideoWidth, StreamInfo.VideoHeight);
            FillSourceFrame(source);

            var target = default(AVFrame*);
            var oneThread = default(AVFrame*);
            var manyThreads = default(AVFrame*);

            try
            {
                Console.WriteLine($"{"Filter",-12} {"Target",-7} {"Scale",9} " +
                    $"{"Sharpen 1thr",13} {"Sharpen Nthr",13} {"Speedup",8} {"Total",9}  Sharpen");

                foreach (var (width, height, name) in Targets)
                {
                    FreeFrame(ref target); FreeFrame(ref oneThread); FreeFrame(ref manyThreads);

                    target = MakeFrame(width, height);
                    oneThread = MakeFrame(width, height);
                    manyThreads = MakeFrame(width, height);

                    foreach (var filter in Filters)
                    {
                        // Correctness first, a faster pass that renders a broken frame is
                        // worth nothing. The threaded sharpen must write the very same bytes
                        string sharpenMatch;

                        using (var check = new FrameScaler(StreamInfo.VideoWidth, StreamInfo.VideoHeight,
                            AVPixelFormat.AV_PIX_FMT_YUV420P, width, height, AVPixelFormat.AV_PIX_FMT_YUV420P, filter))
                        {
                            check.Scale(source, target);
                            CopyFrame(target, oneThread);
                            CopyFrame(target, manyThreads);

                            using var reference = new FrameSharpen(width, height, DefaultSharpenAmount, 1);
                            using var threaded = new FrameSharpen(width, height, DefaultSharpenAmount);

                            reference.Apply(oneThread);
                            threaded.Apply(manyThreads);

                            sharpenMatch = CheckResult(oneThread, manyThreads);
                        }

                        double scaleMs, sharpenOneMs, sharpenManyMs, totalMs;

                        using (var scaler = new FrameScaler(StreamInfo.VideoWidth, StreamInfo.VideoHeight,
                            AVPixelFormat.AV_PIX_FMT_YUV420P, width, height, AVPixelFormat.AV_PIX_FMT_YUV420P, filter))
                        using (var one = new FrameSharpen(width, height, DefaultSharpenAmount, 1))
                        using (var many = new FrameSharpen(width, height, DefaultSharpenAmount))
                        {
                            var warmup = Stopwatch.StartNew();
                            int warmupCount = 0;
                            do { scaler.Scale(source, target); warmupCount++; }
                            while (warmup.ElapsedMilliseconds < 200 || warmupCount < WarmupFrames);

                            var timer = Stopwatch.StartNew();
                            for (int i = 0; i < MeasuredFrames; i++)
                                scaler.Scale(source, target);
                            timer.Stop();

                            scaleMs = timer.Elapsed.TotalMilliseconds / MeasuredFrames;

                            warmup = Stopwatch.StartNew();
                            warmupCount = 0;
                            do { one.Apply(target); many.Apply(target); warmupCount++; }
                            while (warmup.ElapsedMilliseconds < 200 || warmupCount < WarmupFrames);

                            timer = Stopwatch.StartNew();
                            for (int i = 0; i < MeasuredFrames; i++)
                                one.Apply(target);
                            timer.Stop();

                            sharpenOneMs = timer.Elapsed.TotalMilliseconds / MeasuredFrames;

                            timer.Restart();
                            for (int i = 0; i < MeasuredFrames; i++)
                                many.Apply(target);
                            timer.Stop();

                            sharpenManyMs = timer.Elapsed.TotalMilliseconds / MeasuredFrames;

                            // What the player actually does every frame: antialias on the
                            // source, then scale, then sharpen the enlarged frame
                            timer.Restart();
                            for (int i = 0; i < MeasuredFrames; i++)
                            {
                                scaler.Scale(source, target);
                                many.Apply(target);
                            }
                            timer.Stop();

                            totalMs = antiAliasMs + timer.Elapsed.TotalMilliseconds / MeasuredFrames;
                        }

                        string result = totalMs <= budget
                            ? "fits 30fps"
                            : $"over by {totalMs - budget:0.0}ms";

                        Console.WriteLine($"{filter,-12} {name,-7} {scaleMs,7:0.00}ms " +
                            $"{sharpenOneMs,11:0.00}ms {sharpenManyMs,11:0.00}ms {sharpenOneMs / sharpenManyMs,7:0.0}x " +
                            $"{totalMs,7:0.00}ms  {sharpenMatch}  {result}");
                    }

                    Console.WriteLine();
                }

                Console.WriteLine("Sharpen and antialias compare the threaded pass against the single threaded one");
                Console.WriteLine("pixel by pixel, they must always read exact.");
                Console.WriteLine("Total is the time of the client video pipeline for every frame received from the");
                Console.WriteLine("console, antialias on the source plus scaling plus sharpen, it must stay below");
                Console.WriteLine("the budget or frames get dropped. Both passes work on the luma plane only.");
                Console.WriteLine();
            }
            finally
            {
                var ptr = source;
                av_frame_free(&ptr);

                FreeFrame(ref target); FreeFrame(ref oneThread); FreeFrame(ref manyThreads);
            }
        }

        // The three regions of the test pattern, kept far enough apart that the 3x3
        // neighbourhood of one never reaches another
        const int FlatX = 40, FlatY = 40, FlatW = 300, FlatH = 200;
        const int RampX = 1000, RampY = 40, RampW = 200, RampH = 200;
        const int EdgeX = 400, EdgeY = 300, EdgeW = 500, EdgeH = 300;

        // Builds a frame with three very different areas: a flat surface, a smooth gradient
        // and a stair cased vertical edge. The first two have no business being touched by
        // the antialias, the third is exactly what it exists for.
        static void FillAntiAliasTestPattern(AVFrame* frame)
        {
            int w = frame->width;
            int h = frame->height;

            for (int y = 0; y < h; y++)
            {
                byte* row = frame->data[0] + (long)y * frame->linesize[0];

                for (int x = 0; x < w; x++)
                    row[x] = 128;

                // One luma of difference between neighbouring pixels, well under any edge
                // threshold, so a plain gradient must survive untouched
                for (int x = RampX; x < RampX + RampW; x++)
                    row[x] = (byte)(128 + (x - RampX) / 2);

                // Vertical edge that steps sideways every few rows, the classic stair case
                for (int x = EdgeX; x < EdgeX + EdgeW; x++)
                {
                    int boundary = EdgeX + EdgeW / 2 + ((y / 6) % 8) - 4;
                    row[x] = x >= boundary ? (byte)224 : (byte)32;
                }
            }

            for (uint plane = 1; plane < 3; plane++)
            {
                for (int y = 0; y < h / 2; y++)
                {
                    byte* row = frame->data[plane] + (long)y * frame->linesize[plane];
                    for (int x = 0; x < w / 2; x++)
                        row[x] = 128;
                }
            }
        }

        static int CountChanged(AVFrame* a, AVFrame* b, int x0, int y0, int x1, int y1)
        {
            int changed = 0;

            for (int y = y0; y < y1; y++)
            {
                byte* ra = a->data[0] + (long)y * a->linesize[0];
                byte* rb = b->data[0] + (long)y * b->linesize[0];

                for (int x = x0; x < x1; x++)
                    if (ra[x] != rb[x])
                        changed++;
            }

            return changed;
        }

        // Returns the time the threaded antialias takes at the source resolution, the value
        // the scaling table adds to its totals
        static double VerifyAntiAlias(int workers)
        {
            var source = MakeFrame(StreamInfo.VideoWidth, StreamInfo.VideoHeight);
            var single = MakeFrame(StreamInfo.VideoWidth, StreamInfo.VideoHeight);
            var threaded = MakeFrame(StreamInfo.VideoWidth, StreamInfo.VideoHeight);

            try
            {
                FillAntiAliasTestPattern(source);

                CopyFrame(source, single);
                CopyFrame(source, threaded);

                int strength = (int)(DefaultAntiAliasAmount * 255);

                using (var one = new FrameAntiAlias(StreamInfo.VideoWidth, StreamInfo.VideoHeight, 1))
                using (var many = new FrameAntiAlias(StreamInfo.VideoWidth, StreamInfo.VideoHeight))
                {
                    one.Apply(single, strength);
                    many.Apply(threaded, strength);

                    // Inset by a couple of pixels, the very border of a region belongs to a
                    // neighbourhood that reaches into the area next to it
                    int flatChanged = CountChanged(source, single, FlatX + 2, FlatY + 2, FlatX + FlatW - 2, FlatY + FlatH - 2);
                    int rampChanged = CountChanged(source, single, RampX + 10, RampY + 2, RampX + RampW - 10, RampY + RampH - 2);
                    int edgeChanged = CountChanged(source, single, EdgeX + 2, EdgeY + 2, EdgeX + EdgeW - 2, EdgeY + EdgeH - 2);

                    string threadsMatch = CheckResult(single, threaded);

                    Console.WriteLine("Antialias verification on the 720p source");
                    Console.WriteLine($"  flat area untouched        : {flatChanged,5} px changed   {(flatChanged == 0 ? "PASS" : "FAIL")}");
                    Console.WriteLine($"  plain gradient untouched   : {rampChanged,5} px changed   {(rampChanged == 0 ? "PASS" : "FAIL")}");
                    Console.WriteLine($"  stair cased edge smoothed  : {edgeChanged,5} px changed   {(edgeChanged > 0 ? "PASS" : "FAIL")}");
                    Console.WriteLine($"  1 thread vs {workers} threads       : {threadsMatch,-6}            {(threadsMatch == "exact" ? "PASS" : "FAIL")}");
                    Console.WriteLine();

                    var warmup = Stopwatch.StartNew();
                    int warmupCount = 0;
                    do { one.Apply(threaded, strength); many.Apply(threaded, strength); warmupCount++; }
                    while (warmup.ElapsedMilliseconds < 200 || warmupCount < WarmupFrames);

                    var timer = Stopwatch.StartNew();
                    for (int i = 0; i < MeasuredFrames; i++)
                        one.Apply(threaded, strength);
                    timer.Stop();

                    double oneMs = timer.Elapsed.TotalMilliseconds / MeasuredFrames;

                    timer.Restart();
                    for (int i = 0; i < MeasuredFrames; i++)
                        many.Apply(threaded, strength);
                    timer.Stop();

                    double manyMs = timer.Elapsed.TotalMilliseconds / MeasuredFrames;

                    Console.WriteLine($"Antialias cost at {StreamInfo.VideoWidth}x{StreamInfo.VideoHeight} (before scaling): " +
                        $"{oneMs:0.00} ms on one thread, {manyMs:0.00} ms on {workers} threads, speedup {oneMs / manyMs:0.0}x");
                    Console.WriteLine();

                    return manyMs;
                }
            }
            finally
            {
                var ptr = source;
                av_frame_free(&ptr);

                FreeFrame(ref single); FreeFrame(ref threaded);
            }
        }

        static void FreeFrame(ref AVFrame* frame)
        {
            if (frame == null)
                return;

            var ptr = frame;
            av_frame_free(&ptr);
            frame = null;
        }
    }
}
