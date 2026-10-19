using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SysDVR.Client.Core
{
    // Lightweight counters feeding the on screen stats overlay, all static and
    // updated with interlocked ops since packets arrive on the stream thread
    public static class StreamStats
    {
        static long videoBytes, audioBytes, videoPackets, audioPackets, videoDrops, audioDrops;

        public static void Reset()
        {
            Interlocked.Exchange(ref videoBytes, 0);
            Interlocked.Exchange(ref audioBytes, 0);
            Interlocked.Exchange(ref videoPackets, 0);
            Interlocked.Exchange(ref audioPackets, 0);
            Interlocked.Exchange(ref videoDrops, 0);
            Interlocked.Exchange(ref audioDrops, 0);
            CpuSampler.Reset();
        }

        public static void OnPacket(bool video, int size)
        {
            if (video)
            {
                Interlocked.Add(ref videoBytes, size);
                Interlocked.Increment(ref videoPackets);
            }
            else
            {
                Interlocked.Add(ref audioBytes, size);
                Interlocked.Increment(ref audioPackets);
            }
        }

        public static void OnDrop(bool video)
        {
            if (video)
                Interlocked.Increment(ref videoDrops);
            else
                Interlocked.Increment(ref audioDrops);
        }

        public static long VideoBytes => Interlocked.Read(ref videoBytes);
        public static long AudioBytes => Interlocked.Read(ref audioBytes);
        public static long TotalDrops => Interlocked.Read(ref videoDrops) + Interlocked.Read(ref audioDrops);

        // --- CPU usage of this process, sampled at most twice per second ---

        static class CpuSampler
        {
            static Process process = Process.GetCurrentProcess();
            static TimeSpan lastCpu;
            static long lastTicks;
            static double lastValue;
            static long lastSample;

            public static void Reset()
            {
                lastCpu = process.TotalProcessorTime;
                lastTicks = Environment.TickCount64;
                lastSample = 0;
                lastValue = 0;
            }

            public static double GetPercent()
            {
                long now = Environment.TickCount64;
                if (now - lastSample < 500)
                    return lastValue;
                if (lastSample == 0)
                {
                    Reset();
                    lastSample = now;
                    return 0;
                }

                try
                {
                    var cpu = process.TotalProcessorTime;
                    double wallMs = now - lastTicks;
                    double cpuMs = (cpu - lastCpu).TotalMilliseconds;

                    if (wallMs > 0)
                    {
                        lastValue = 100.0 * cpuMs / wallMs / Environment.ProcessorCount;
                        if (lastValue < 0) lastValue = 0;
                    }

                    lastCpu = cpu;
                    lastTicks = now;
                }
                catch { /* process may be gone */ }

                lastSample = now;
                return lastValue;
            }
        }

        // --- GPU usage, windows only via the PDH "GPU Engine" counters ---
        // Sums every engine instance owned by this process id. If anything fails
        // the overlay simply shows nothing for this value.

        static class GpuSampler
        {
            const uint PDH_FMT_DOUBLE = 0x00000200;
            const int PDH_MORE_DATA = unchecked((int)0x800007D2);

            static IntPtr query, counter;
            static bool initialized, ok;
            static double lastValue;
            static long lastSample;
            static readonly int pid = Environment.ProcessId;

            public static double GetPercent()
            {
                if (!OperatingSystem.IsWindows())
                    return -1;

                long now = Environment.TickCount64;
                if (now - lastSample < 500)
                    return ok ? lastValue : -1;
                lastSample = now;

                if (!initialized)
                {
                    initialized = true;
                    try
                    {
                        if (PdhOpenEnglishQueryW(IntPtr.Zero, 0, out query) == 0 &&
                            PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) == 0)
                        {
                            // First collection only establishes the baseline for the rates
                            PdhCollectQueryData(query);
                            ok = true;
                        }
                    }
                    catch { ok = false; }
                    if (!ok)
                        return -1;
                }

                try
                {
                    if (PdhCollectQueryData(query) != 0)
                        return lastValue;

                    uint size = 0, count = 0;
                    PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
                    if (size == 0 || count == 0)
                        return lastValue;

                    var buf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        int status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, buf);
                        if (status != 0 && status != PDH_MORE_DATA)
                            return lastValue;

                        double sum = 0;
                        nint p = buf;
                        string self = $"pid_{pid}_";
                        for (uint i = 0; i < count; i++)
                        {
                            string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p)) ?? "";
                            if (name.StartsWith(self, StringComparison.Ordinal))
                            {
                                // PDH_FMT_COUNTERVALUE_ITEM_W: ptr + { uint CStatus; pad; double DoubleValue }
                                sum += BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p + 16));
                            }
                            p += IntPtr.Size + 16;
                        }

                        lastValue = Math.Min(100, sum);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
                catch
                {
                    ok = false;
                }

                return ok ? lastValue : -1;
            }

            [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
            static extern int PdhOpenEnglishQueryW(IntPtr path, uint flags, out IntPtr query);

            [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
            static extern int PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

            [DllImport("pdh.dll")]
            static extern int PdhCollectQueryData(IntPtr query);

            [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
            static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize,
                out uint itemCount, IntPtr itemBuffer);

            [DllImport("pdh.dll")]
            static extern int PdhCloseQuery(IntPtr query);
        }

        public static double GetCpuPercent() => CpuSampler.GetPercent();
        public static double GetGpuPercent() => GpuSampler.GetPercent();
    }
}
