using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SysDVR.Client.Core
{
    public enum UsbLogLevel
    {
        Error,
        Warning,
        Debug,
        None
    }

    public enum SDLScaleMode 
    {
        Linear,
        Nearest,
        Best
    }

    public enum SDLAudioMode 
    {
        Compatible,
        Default,
        Auto
    }

    // The console always captures at 720p, these options only control how that frame is
    // presented on screen, they can never add detail that the stream doesn't have
    public enum VideoUpscaleMode
    {
        // Keep the native 720p frame and let SDL scale it to the window (original behaviour)
        Auto,
        HD1080,
        QHD1440,
        UHD2160
    }

    public enum VideoUpscaleFilter
    {
        Linear,
        Nearest,
        Bicubic,
        Lanczos,
        // Lanczos with fewer kernel taps, softer than the standard one. Lanczos sharpens
        // edges by design which makes the stair steps of the 720p source more visible,
        // this variant trades some of that definition back for a cleaner image
        LanczosSoft
    }

    // How finished frames are handed over to the display
    public enum PresentMode
    {
        // Wait for the display refresh, no tearing, this is the default
        VSync,
        // Wait for the display only when the frame rate is stable, less latency when it can
        Adaptive,
        // Never wait, lowest possible latency but the picture may tear
        Off
    }

    public class Options
    {
        public bool UncapStreaming;
        public bool UncapGUI;

        // Presentation of the decoded frames, applied live
        public PresentMode Presentation = PresentMode.VSync;
        // Stretch the video to the entire window, ignores the aspect ratio
        public bool FillScreen;

        // Automatically start a new connection attempt when the stream dies or the
        // console can't be reached, for up to AutoReconnectMaxAttempts tries
        public bool AutoReconnect = true;
        public const int AutoReconnectMaxAttempts = 5;

        public string RecordingsPath = DefaultPlatformVideoPath();
        public string ScreenshotsPath = DefaultPlatformPicturePath();
        public bool HideSerials;

        public string? PreferredLanguage;

        public bool PlayerHotkeys = true;

        // (Windows only) Capture screenshots to clipboard by default
        public bool Windows_ScreenToClip = false;

		// Usb logging options
		[JsonIgnore]
		public UsbLogLevel UsbLogging = UsbLogLevel.Error;

		// Ffmpeg options
        public string? DecoderName;

        // SDL options
        public bool ForceSoftwareRenderer;
        public SDLScaleMode RendererScale = SDLScaleMode.Linear;
        public SDLAudioMode AudioPlayerMode = SDLAudioMode.Auto;

        // Video output options, applied on the client when presenting the stream
        // Note these are read when the player is created, restart the stream to apply them
        // Default is 1080p since that is the most common monitor resolution, change it in the settings
        public VideoUpscaleMode UpscaleMode = VideoUpscaleMode.HD1080;
        public VideoUpscaleFilter UpscaleFilter = VideoUpscaleFilter.Lanczos;
        public bool UpscaleSharpen = true;
        public float UpscaleSharpenAmount = 0.35f;

        // Edge directed anti aliasing, applied to the luma of the 720p source before it is
        // enlarged. The console already captures aliased edges and upscaling only makes them
        // more visible, this softens the stair steps without touching flat areas.
        public bool AntiAlias = true;
        public float AntiAliasAmount = 0.5f;

        // Sysmodule options
        public StreamingOptions Streaming = new();

		// Debug settings
		public DebugOptions Debug = new();
        public bool UseSIMDAcceleration = true;

        public float GuiFontScale = 1;

        public int DefaultVolume = 60;

        public bool ControllerInput = true;

        public string LastNetworkModeIP = "";

		public string ScaleHintForSDL => RendererScale switch
        {
            SDLScaleMode.Linear => "linear",
            SDLScaleMode.Nearest => "nearest",
            SDLScaleMode.Best => "best",
            _ => throw new NotImplementedException(),
        };

        // Returns the resolution the video texture should be rendered at.
        // When the upscale mode is disabled it returns false and the native stream
        // resolution must be used instead.
        public bool TryGetUpscaleSize(out int width, out int height) =>
            TryGetUpscaleSize(UpscaleMode, out width, out height);

        public static bool TryGetUpscaleSize(VideoUpscaleMode mode, out int width, out int height)
        {
            switch (mode)
            {
                case VideoUpscaleMode.HD1080:
                    width = 1920; height = 1080;
                    return true;
                case VideoUpscaleMode.QHD1440:
                    width = 2560; height = 1440;
                    return true;
                case VideoUpscaleMode.UHD2160:
                    width = 3840; height = 2160;
                    return true;
                default:
                    width = 0; height = 0;
                    return false;
            }
        }

        public string GetFilePathForVideo()
        {
            var format = $"SysDVR_{DateTime.Now:yyyy_MM_dd_HH_mm_ss}.mp4";
            return Path.Combine(RecordingsPath, format);
        }

        public string GetFilePathForScreenshot()
        {
            var format = $"SysDVR_{DateTime.Now:yyyy_MM_dd_HH_mm_ss}.png";
            return Path.Combine(ScreenshotsPath, format);
        }

        static string LinuxFallbackPath(string wantedFolderName) 
        {
            var wanted = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), wantedFolderName);
            
            if (!Directory.Exists(wanted))
            {
                wanted = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
                
                if (!Directory.Exists(wanted))
                {
                    wanted = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");

                    // WSL doesn't have the other folders by default
                    if (!Directory.Exists(wanted))
                        wanted = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
            }

            return wanted;
        }

        static string DefaultPlatformVideoPath()
        {
#if ANDROID_LIB
            return "/sdcard/Movies";  
#else
            if (Program.IsWindows)
                return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            else
                // like you realise ~/Videos is a thing on linux right
                //                                                  -Blecc
                return LinuxFallbackPath("Videos");
#endif
        }

        static string DefaultPlatformPicturePath()
        {
#if ANDROID_LIB
            return "/sdcard/Pictures";  
#else
            if (Program.IsWindows)
                return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            else
                return LinuxFallbackPath("Pictures");
#endif
        }

        public string SerializeToJson() 
        {
            return JsonSerializer.Serialize(this, OptionsJsonSerializer.Default.SysDVROptions);
		}

        public static Options FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<Options>(json, OptionsJsonSerializer.Default.SysDVROptions)!;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to deserialize options from json: " + ex);
                return new Options();
            }
        }
    }

    // When using AOT we must use source generation for json serialization since reflection is not available
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        IncludeFields = true, 
        IgnoreReadOnlyProperties = true)]
    [JsonSerializable(typeof(Options), TypeInfoPropertyName = "SysDVROptions")]
    internal partial class OptionsJsonSerializer : JsonSerializerContext 
    {
    }
}
