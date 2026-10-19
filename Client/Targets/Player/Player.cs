using FFmpeg.AutoGen;
using SysDVR.Client.Core;
using SysDVR.Client.Sources;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static FFmpeg.AutoGen.ffmpeg;
using static SDL2.SDL;

namespace SysDVR.Client.Targets.Player
{
    unsafe class DecoderContext
    {
        public AVCodecContext* CodecCtx { get; init; }

        public AVFrame* RenderFrame;
        public AVFrame* ReceiveFrame;

        public AVFrame* Frame1 { get; init; }
        public AVFrame* Frame2 { get; init; }

        public object CodecLock { get; init; }

        public StreamSynchronizationHelper SyncHelper;
        public AutoResetEvent OnFrameEvent;
    }

    unsafe struct FormatConverterContext
    {
        public SwsContext* Converter { get; init; }
        public AVFrame* Frame { get; init; }
    }

    // Guarantees that the audio and video stream are compatible with the player
    class PlayerManager : StreamManager
    {
        internal new DecodeH264Target VideoTarget;
        internal new BaseAudioPlayerTarget AudioTarget;

        readonly public bool IsCompatibleAudioStream;

        static OutStream? MakeAudioStream(bool hasAudio) 
        {
            if (!hasAudio)
                return null;

            var useCompat = Program.Options.AudioPlayerMode switch
            {
                SDLAudioMode.Compatible => true,
                SDLAudioMode.Default => false,
                // Currently only mac os seems to need the compatible mode by default
                _ => RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            };

            if (useCompat)
                return new QueuedStreamAudioTarget();
            else
                return new AudioPlayerTarget(); 
        }

        static OutStream? MakeVideoStream(bool hasVideo)
        {
            if (!hasVideo)
                return null;

            return new DecodeH264Target();
        }

        public void UseSyncManager(StreamSynchronizationHelper manager)
        {
            if (AudioTarget is AudioPlayerTarget)
            {
                ((AudioPlayerTarget)AudioTarget).SyncHelper = manager;
            }
        }

        public PlayerManager(StreamingSource source, CancellationTokenSource cancel) :
            base(source, MakeVideoStream(source.Options.HasVideo), MakeAudioStream(source.Options.HasAudio), cancel)
        {
            VideoTarget = base.VideoTarget as DecodeH264Target;
            AudioTarget = base.AudioTarget as BaseAudioPlayerTarget;
            IsCompatibleAudioStream = AudioTarget is QueuedStreamAudioTarget;
        }
    }

    class AudioPlayer : IDisposable
    {
        readonly uint DeviceID;
        
        // Are we using the MacOS strategy ?
        readonly bool IsCompatiblePlayer;
        
        // Keep a reference to the callback to prevent GC from collecting it
        SDL_AudioCallback? CallbackDelegate;

        // Manually pin the Target object so it can be used as opaque pointer for the native code
        GCHandle TargetHandle;

        public const ushort AudioFormat = AUDIO_S16LSB;

        public AudioPlayer(BaseAudioPlayerTarget target) 
        {
            Program.SdlCtx.BugCheckThreadId();

            IsCompatiblePlayer = target is QueuedStreamAudioTarget;

            SDL_AudioSpec wantedSpec = new SDL_AudioSpec()
            {
                channels = StreamInfo.AudioChannels,
                format = AudioFormat,
                freq = StreamInfo.AudioSampleRate,
                // StreamInfo.MinAudioSamplesPerPayload * 2 was the default until sysdvr 5.4
                // however SDL will pick its preferred buffer size since we pass SDL_AUDIO_ALLOW_SAMPLES_CHANGE,
                // this is fine since we have our own buffering.
                samples = StreamInfo.MinAudioSamplesPerPayload,
            };

            if (!IsCompatiblePlayer)
            {
                TargetHandle = GCHandle.Alloc(target, GCHandleType.Normal);
                CallbackDelegate = AudioStreamTargetNative.SDLCallback;
                wantedSpec.callback = CallbackDelegate;
                wantedSpec.userdata = GCHandle.ToIntPtr(TargetHandle);
            }

            DeviceID = SDL_OpenAudioDevice(IntPtr.Zero, 0, ref wantedSpec, out var obtained, (int)SDL_AUDIO_ALLOW_SAMPLES_CHANGE);

            DeviceID.AssertNotZero(SDL_GetError);

			Program.DebugLog($"SDL_Audio: requested samples per callback={wantedSpec.samples} obtained={obtained.samples}");

            if (IsCompatiblePlayer)
            {
                ((QueuedStreamAudioTarget)(target)).DeviceID = DeviceID;
            }
        }

        public void Pause() 
        {
            SDL_PauseAudioDevice(DeviceID, 1);
        }

        public void Resume() 
        {
            SDL_PauseAudioDevice(DeviceID, 0);
        }

        public void Dispose()
        {
            Pause();
            SDL_CloseAudioDevice(DeviceID);
            
            if (TargetHandle.IsAllocated)
                TargetHandle.Free();
        }
    }

    unsafe class VideoPlayer : IDisposable
    {
        public const AVPixelFormat TargetDecodingFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;
        public readonly uint TargetTextureFormat = SDL_PIXELFORMAT_IYUV;

        public DecoderContext Decoder { get; private set; }
        FormatConverterContext Converter; // Initialized only when the decoder output format doesn't match the SDL texture format

        public string DecoderName { get; private set; }
        public bool UsingCustomDecoder { get; private set; }

        public object TextureLock;
        public IntPtr TargetTexture;
        public SDL_Rect TargetTextureSize;

        // Size of the texture we render to, this is bigger than the stream resolution when
        // the user enabled client side upscaling. The stream itself is always 720p.
        // These are mutable so the processing can be changed while the stream is running,
        // see ApplyVideoProcessing
        public int TargetWidth { get; private set; }
        public int TargetHeight { get; private set; }
        public bool IsUpscaled { get; private set; }

        // Applied to the enlarged frame, only created when upscaling is enabled
        FrameSharpen? sharpen;
        // Applied to the luma of the decoded 720p frame before it is enlarged,
        // sized after the stream and never recreated since the stream size is fixed
        FrameAntiAlias? antiAlias;

        VideoUpscaleFilter currentFilter = VideoUpscaleFilter.Lanczos;
        float currentSharpenAmount;
        float currentAntiAliasAmount;

        // The format converter is created lazily because the source pixel format is only
        // known once the first frame is decoded. When the settings change it is thrown away
        // here and rebuilt on the next frame with the new target size and filter.
        bool converterDirty;

        // Time spent in the client video pipeline for the last frame, in milliseconds:
        // decode plus antialias, scaling and sharpening. Measured so the stats overlay can
        // show the real cost instead of the render loop time
        public float LastPipelineMs { get; private set; }
        public float PeakPipelineMs { get; private set; }
        readonly System.Diagnostics.Stopwatch pipelineTimer = new();

        // One of the two decoder owned buffers, kept as the fallback render target for the
        // frames while no converter exists. Must never be freed by this class
        AVFrame* idleDecoderFrame;

        public VideoPlayer(string? preferredDecoderName)
        {
            InitVideoDecoder(preferredDecoderName);

            idleDecoderFrame = Decoder.Frame2;
            Decoder.RenderFrame = Decoder.Frame2;

            ApplyVideoProcessing(
                Program.Options.UpscaleMode,
                Program.Options.UpscaleFilter,
                Program.Options.UpscaleSharpen, Program.Options.UpscaleSharpenAmount,
                Program.Options.AntiAlias, Program.Options.AntiAliasAmount);
        }

        /// <summary>
        /// Rebuilds the whole video processing chain with new settings. Safe to call while a
        /// stream is running, the conversion, the texture and the post processing passes are
        /// all rebuilt before the next frame is presented. Must be called from the thread
        /// that owns the SDL renderer.
        /// </summary>
        public void ApplyVideoProcessing(VideoUpscaleMode mode, VideoUpscaleFilter filter,
            bool sharpenEnabled, float sharpenAmount, bool antiAliasEnabled, float antiAliasAmount)
        {
            Program.SdlCtx.BugCheckThreadId();

            int targetW, targetH;

            if (Options.TryGetUpscaleSize(mode, out targetW, out targetH))
            {
                // Use the requested upscale size
            }
            else
            {
                targetW = StreamInfo.VideoWidth;
                targetH = StreamInfo.VideoHeight;
            }

            bool sizeChanged = targetW != TargetWidth || targetH != TargetHeight;

            TargetWidth = targetW;
            TargetHeight = targetH;
            IsUpscaled = targetW != StreamInfo.VideoWidth || targetH != StreamInfo.VideoHeight;

            currentFilter = filter;

            // Sharpening restores definition lost while enlarging, it is meaningless when
            // the frame is not being enlarged at all
            currentSharpenAmount = sharpenEnabled && IsUpscaled
                ? Math.Clamp(sharpenAmount, 0f, 1f)
                : 0f;

            currentAntiAliasAmount = antiAliasEnabled
                ? Math.Clamp(antiAliasAmount, 0f, 1f)
                : 0f;

            sharpen?.Dispose();
            sharpen = null;

            if (currentSharpenAmount > 0)
                sharpen = new FrameSharpen(targetW, targetH, currentSharpenAmount);

            // The antialias always runs on the source resolution, only its strength changes
            if (antiAlias == null)
                antiAlias = new FrameAntiAlias(StreamInfo.VideoWidth, StreamInfo.VideoHeight);

            // Drop the converter so the next frame rebuilds it for the new target, it also
            // picks up the filter and the kernel parameters from the current settings
            converterDirty = true;

            if (TargetTexture == 0)
                CreateRenderTexture(targetW, targetH);
            else if (sizeChanged)
                RecreateRenderTexture(targetW, targetH);

            Console.WriteLine($"Video output: {StreamInfo.VideoWidth}x{StreamInfo.VideoHeight} stream -> {TargetWidth}x{TargetHeight} texture"
                + (IsUpscaled ? $" ({filter}, sharpen {currentSharpenAmount:0.00}, antialias {currentAntiAliasAmount:0.00})" : $" (antialias {currentAntiAliasAmount:0.00})"));
        }

        // Applies the antialias with the current strength, no op when it is disabled
        void ApplyAntiAlias(AVFrame* frame)
        {
            if (antiAlias == null || currentAntiAliasAmount <= 0)
                return;

            // The decoded frame is reference counted and the decoder keeps a copy of it in
            // its decoded picture buffer to predict the following P frames from. The filter
            // writes into the luma plane in place, so without this call it would corrupt
            // that reference and every frame predicted from it would be built on a smeared
            // source, showing up as the picture alternating between sharp and blurry.
            //
            // make_writable hands us a privately owned copy only when the buffer is shared
            // (the normal case for a freshly decoded frame) and is a no-op otherwise, so
            // the decoder's reference is left untouched and the filter still sees the frame.
            av_frame_make_writable(frame);

            antiAlias.Apply(frame, (int)(currentAntiAliasAmount * 255));
        }

        void CreateRenderTexture(int width, int height)
        {
            Program.SdlCtx.BugCheckThreadId();

            var tex = SDL_CreateTexture(Program.SdlCtx.RendererHandle, TargetTextureFormat,
                (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
                width, height).AssertNotNull(SDL_GetError);

            if (Program.Options.Debug.Log)
            {
                var pixfmt = SDL_QueryTexture(tex, out var format, out var a, out var w, out var h);
                Console.WriteLine($"SDL texture info: f = {SDL_GetPixelFormatName(format)} a = {a} w = {w} h = {h}");

                SDL_RendererInfo info;
                SDL_GetRendererInfo(Program.SdlCtx.RendererHandle, out info);
                for (int i = 0; i < info.num_texture_formats; i++) unsafe
                    {
                        Console.WriteLine($"Renderer supports pixel format {SDL_GetPixelFormatName(info.texture_formats[i])}");
                    }
            }

            TargetTextureSize = new SDL_Rect() { x = 0, y = 0, w = width, h = height };
            TargetTexture = tex;

            TextureLock ??= new object();
        }

        // Swaps the texture for one of a different size, used when the upscale resolution
        // changes while the stream is running
        void RecreateRenderTexture(int width, int height)
        {
            Program.SdlCtx.BugCheckThreadId();

            lock (TextureLock)
            {
                if (TargetTexture != 0)
                    SDL_DestroyTexture(TargetTexture);

                TargetTexture = 0;
            }

            CreateRenderTexture(width, height);
        }

        unsafe void InitVideoDecoder(string? name)
        {
            AVCodec* codec = null;

            if (name is not null)
            {
                codec = avcodec_find_decoder_by_name(name);
                
                if (codec != null)
                    UsingCustomDecoder = true;
            }

            if (codec == null)
                codec = avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);

            if (codec == null)
                throw new Exception("Couldn't find any compatible video codecs");

            Decoder = CreateDecoderContext(codec);
            DecoderName = Marshal.PtrToStringAnsi((IntPtr)codec->name);
        }

        static unsafe DecoderContext CreateDecoderContext(AVCodec* codec)
        {
            if (codec == null)
                throw new Exception("Codec can't be null");

            string codecName = Marshal.PtrToStringAnsi((IntPtr)codec->name);

            Console.WriteLine(string.Format(Program.Strings.Player.PlayerInitializationMessage, codecName));

            var codectx = avcodec_alloc_context3(codec);
            if (codectx == null)
                throw new Exception("Couldn't allocate a codec context");

            // These are set in ffplay
            codectx->codec_id = codec->id;
            codectx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            codectx->bit_rate = 0;

            // Some decoders break without this
            codectx->width = StreamInfo.VideoWidth;
            codectx->height = StreamInfo.VideoHeight;

            var (ex, sz) = LibavUtils.AllocateH264Extradata();
            codectx->extradata_size = sz;
            codectx->extradata = (byte*)ex.ToPointer();

            avcodec_open2(codectx, codec, null).AssertZero("Couldn't open the codec.");

            var pic = av_frame_alloc();
            if (pic == null)
                throw new Exception("Couldn't allocate the decoding frame");

            var pic2 = av_frame_alloc();
            if (pic2 == null)
                throw new Exception("Couldn't allocate the decoding frame");

            return new DecoderContext()
            {
                CodecCtx = codectx,
                Frame1 = pic,
                Frame2 = pic2,
                ReceiveFrame = pic,
                RenderFrame = pic2,
                CodecLock = new object(),
                OnFrameEvent = new AutoResetEvent(true)
            };
        }

        public unsafe bool DecodeFrame() 
        {
            // Only the receive + conversion path is timed, the texture upload is a driver
            // side copy that does not tell us anything about the CPU cost of the pipeline
            pipelineTimer.Restart();

            if (DecodeFrameInternal())
            {
                pipelineTimer.Stop();
                LastPipelineMs = (float)pipelineTimer.Elapsed.TotalMilliseconds;

                if (LastPipelineMs > PeakPipelineMs)
                    PeakPipelineMs = LastPipelineMs;

                // TODO: this call is needed only with opengl on linux (and not on every linux install i tested) where TextureUpdate must be called by the main thread,
                // Check if are there any performance improvements by moving this to the decoder thread on other OSes
                UpdateSDLTexture(Decoder.RenderFrame);

                return true;
            }

            pipelineTimer.Stop();
            return false;
        }

        // Decays the peak so it only reflects the recent past, called by the stats overlay
        public void DecayPeak(float factor = 0.98f)
        {
            PeakPipelineMs *= factor;
            if (PeakPipelineMs < LastPipelineMs)
                PeakPipelineMs = LastPipelineMs;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int av_ceil_rshift(int a, int b) =>
            -(-a >> b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void UpdateSDLTexture(AVFrame* pic)
        {
            if (pic->linesize[0] > 0 && pic->linesize[1] > 0 && pic->linesize[2] > 0)
            {
                SDL_UpdateYUVTexture(TargetTexture, ref TargetTextureSize,
                    (IntPtr)pic->data[0], pic->linesize[0],
                    (IntPtr)pic->data[1], pic->linesize[1],
                    (IntPtr)pic->data[2], pic->linesize[2]);
            }
#if DEBUG
            // Not sure if this is needed but ffplay source does handle this case, all my tests had positive linesize
            else if (pic->linesize[0] < 0 && pic->linesize[1] < 0 && pic->linesize[2] < 0)
            {
                Program.DebugLog("Negative Linesize");
                SDL_UpdateYUVTexture(TargetTexture, ref TargetTextureSize,
                    (IntPtr)(pic->data[0] + pic->linesize[0] * (pic->height - 1)), -pic->linesize[0],
                    (IntPtr)(pic->data[1] + pic->linesize[1] * (av_ceil_rshift(pic->height, 1) - 1)), -pic->linesize[1],
                    (IntPtr)(pic->data[2] + pic->linesize[2] * (av_ceil_rshift(pic->height, 1) - 1)), -pic->linesize[2]);
            }
#endif
            // While this doesn't seem to be handled in ffplay but the texture can be non-planar with some decoders
            else if (pic->linesize[0] > 0 && pic->linesize[1] == 0)
            {
                SDL_UpdateTexture(TargetTexture, ref TargetTextureSize, (nint)pic->data[0], pic->linesize[0]);
            }
            else Console.WriteLine($"Error: Non-positive planar linesizes are not supported, open an issue on Github. {pic->linesize[0]} {pic->linesize[1]} {pic->linesize[2]}");
        }

        bool converterFirstFrameCheck = false;
        unsafe bool DecodeFrameInternal()
        {
            int ret = 0;

            lock (Decoder.CodecLock)
                ret = avcodec_receive_frame(Decoder.CodecCtx, Decoder.ReceiveFrame);

            if (ret == AVERROR(EAGAIN))
            {
                // Try again for the next SDL frame
                return false;
            }
            else if (ret != 0)
            {
                // Should not happen
                Program.DebugLog($"avcodec_receive_frame {ret}");
                return false;
            }
            else
            {
                // A settings change dropped the converter, it has to be built again with the
                // new target size and filter before this frame can be converted
                if (converterDirty)
                {
                    converterDirty = false;
                    FreeConverter();
                }

                // On the first frame we get check if we need to use a converter
                if (!converterFirstFrameCheck && Decoder.CodecCtx->pix_fmt != AVPixelFormat.AV_PIX_FMT_NONE)
                {
					Program.DebugLog($"Decoder.CodecCtx uses pixel format {Decoder.CodecCtx->pix_fmt}");

                    converterFirstFrameCheck = true;

                    // A converter is needed when the decoded format doesn't match the texture format,
                    // it is also used to perform the optional upscale to the target resolution
                    if (Decoder.CodecCtx->pix_fmt != TargetDecodingFormat || IsUpscaled)
                    {
                        Converter = InitializeConverter(Decoder.CodecCtx);
                        // Render to the converted frame
                        Decoder.RenderFrame = Converter.Frame;
                    }
                }

                if (Converter.Converter != null)
                {
                    var source = Decoder.ReceiveFrame;
                    var target = Decoder.RenderFrame;

                    // The antialias runs on the source and not on the enlarged frame: the
                    // stair steps are a 720p artifact and cleaning them up afterwards would
                    // cost 2.25 times more for a worse result
                    ApplyAntiAlias(source);

                    sws_scale(Converter.Converter, source->data, source->linesize, 0, source->height, target->data, target->linesize);

                    sharpen?.Apply(target);
                }
                else
                {
                    // Nothing to convert, the source is rendered as-is and the antialias is
                    // the only pass that applies to it
                    ApplyAntiAlias(Decoder.ReceiveFrame);

                    // Swap the frames so we can render source
                    var toRender = Decoder.ReceiveFrame;
                    var receiveNext = Decoder.RenderFrame;

                    Decoder.ReceiveFrame = receiveNext;
                    Decoder.RenderFrame = toRender;
                }

                return true;
            }
        }

        // Throws away the format converter so the next frame builds a new one. The render
        // target goes back to a decoder owned buffer, always the one that is not being
        // received into, since the two are swapped around while there is no converter
        unsafe void FreeConverter()
        {
            converterFirstFrameCheck = false;

            if (Converter.Converter == null)
                return;

            if (Decoder.RenderFrame == Converter.Frame)
                Decoder.RenderFrame = idleDecoderFrame;

            var frame = Converter.Frame;
            av_frame_free(&frame);

            sws_freeContext(Converter.Converter);
            Converter = default;
        }

        unsafe FormatConverterContext InitializeConverter(AVCodecContext* codecctx)
        {
            AVFrame* dstframe = null;
            SwsContext* swsContext = null;

            Program.DebugLog($"Initializing converter for {codecctx->pix_fmt}");

            dstframe = av_frame_alloc();

            if (dstframe == null)
                throw new Exception("Couldn't allocate the the converted frame");

            dstframe->format = (int)TargetDecodingFormat;
            dstframe->width = TargetWidth;
            dstframe->height = TargetHeight;

            av_frame_get_buffer(dstframe, 32).AssertZero("Couldn't allocate the buffer for the converted frame");

            // When only the pixel format changes there's nothing to gain from the heavier
            // scalers, the extra quality only matters when we are changing the resolution
            int scaleFlags = IsUpscaled ? LibavUtils.SwsFlagsFor(currentFilter) : SWS_FAST_BILINEAR;

            // Only the filters with a tunable kernel get a parameter array, passing one to
            // the others would override the swscale defaults for no reason
            double* param = stackalloc double[2];
            param[0] = 3.0;
            param[1] = 3.0;

            if (LibavUtils.TryGetSwsParam(currentFilter, out var tunedParam))
                param[0] = tunedParam;
            else
                param = null;

            swsContext = sws_getContext(codecctx->width, codecctx->height, codecctx->pix_fmt,
                                        dstframe->width, dstframe->height, (AVPixelFormat)dstframe->format,
                                        scaleFlags, null, null, param);

            if (swsContext == null)
                throw new Exception("Couldn't initialize the converter");

            return new FormatConverterContext()
            {
                Converter = swsContext,
                Frame = dstframe
            };
        }

        public unsafe void Dispose()
        {
            var ptr = Decoder.Frame1;
            av_frame_free(&ptr);

            ptr = Decoder.Frame2;
            av_frame_free(&ptr);

            var ptr2 = Decoder.CodecCtx;
            avcodec_free_context(&ptr2);

            if (Converter.Converter != null)
            {
                var ptr3 = Converter.Frame;
                av_frame_free(&ptr3);

                sws_freeContext(Converter.Converter);
            }

            sharpen?.Dispose();
            sharpen = null;

            antiAlias?.Dispose();
            antiAlias = null;

            if (TargetTexture != 0)
                SDL_DestroyTexture(TargetTexture);

            Decoder.OnFrameEvent.Dispose();
        }
    }
}
