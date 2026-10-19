using ImGuiNET;
using SysDVR.Client.App;
using SysDVR.Client.Core;
using SysDVR.Client.GUI.Components;
using SysDVR.Client.Platform;
using SysDVR.Client.Sources;
using SysDVR.Client.Targets;
using SysDVR.Client.Targets.FileOutput;
using SysDVR.Client.Targets.Player;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using static SDL2.SDL;

namespace SysDVR.Client.GUI
{
    class PendingUiNotif : IDisposable
    {
        public string Text;
        public bool ShouldRemove;

        Timer disposeTimer;

        public PendingUiNotif(string text)
        {
            Text = text;
            ShouldRemove = false;
            disposeTimer = new Timer((_) => ShouldRemove = true, null, 5000, Timeout.Infinite);
        }

        public void Dispose()
        {
            disposeTimer.Dispose();
        }
    }

    internal class PlayerCore
    {
        internal readonly AudioPlayer? Audio;
        internal readonly VideoPlayer? Video;
        internal readonly PlayerManager Manager;

        readonly FramerateCounter fps = new();

        // Last measured framerate, updated by SampleFps for the stats overlay
        public int LastFps => fps.Fps;
        public void SampleFps() => fps.Poll();

        SDL_Rect DisplayRect = new SDL_Rect();
        int RotationQuarterTurns = 0;

        public PlayerCore(PlayerManager manager)
        {
            Manager = manager;

            // SyncHelper is disabled if there is only a single stream
            // Note that it can also be disabled via a --debug flag and this is handled by the constructor
            var sync = new StreamSynchronizationHelper(manager.HasAudio && manager.HasVideo);

            if (manager.HasVideo)
            {
                Video = new(Program.Options.DecoderName);
                Video.Decoder.SyncHelper = sync;
                manager.VideoTarget.UseContext(Video.Decoder);

                InitializeLoadingTexture();

                fps.Start();
            }

            if (manager.HasAudio)
            {
                Audio = new(manager.AudioTarget);
                manager.AudioTarget.Volume = Program.Options.DefaultVolume / 100f;
            }

            manager.UseSyncManager(sync);
        }

        public void Start()
        {
            Manager.Begin();
            Audio?.Resume();
        }

        public void Destroy()
        {
            Manager.Stop().GetAwaiter().GetResult();

            // Dispose of unmanaged resources
            Audio?.Dispose();
            Video?.Dispose();

            Manager.Dispose();
        }

        public void ResolutionChanged()
        {
            var aspectRatio = (double)StreamInfo.VideoWidth / StreamInfo.VideoHeight;
            var portrait = RotationQuarterTurns is 1 or 3;
            if (portrait)
                aspectRatio = 1 / aspectRatio;

            var w = (int)Program.SdlCtx.WindowSize.X;
            var h = (int)Program.SdlCtx.WindowSize.Y;

            int fittedWidth;
            int fittedHeight;

            if (Program.Options.FillScreen)
            {
                // Ignore the aspect ratio, fill the whole window
                fittedWidth = w;
                fittedHeight = h;
            }
            else if (w >= h * aspectRatio)
            {
                fittedWidth = (int)(h * aspectRatio);
                fittedHeight = h;
            }
            else
            {
                fittedHeight = (int)(w / aspectRatio);
                fittedWidth = w;
            }

            // SDL_RenderCopyEx rotates after scaling, so when rotating by 90°/270° we
            // swap destination width/height to keep the final on-screen bounds correct.
            DisplayRect.w = portrait ? fittedHeight : fittedWidth;
            DisplayRect.h = portrait ? fittedWidth : fittedHeight;
            DisplayRect.x = w / 2 - DisplayRect.w / 2;
            DisplayRect.y = h / 2 - DisplayRect.h / 2;
        }

        public void RotateClockwise()
        {
            RotationQuarterTurns = (RotationQuarterTurns + 1) & 3;
            ResolutionChanged();
        }

        int debugFps = 0;
        public string GetDebugString()
        {
            var sb = new StringBuilder();

            fps.Poll();
            debugFps = fps.Fps;

            sb.AppendLine($"Video fps: {debugFps} DispRect {DisplayRect.x} {DisplayRect.y} {DisplayRect.w} {DisplayRect.h}");

            if (Video is not null)
                sb.AppendLine($"Video texture: {Video.TargetTextureSize.w}x{Video.TargetTextureSize.h} (stream is {StreamInfo.VideoWidth}x{StreamInfo.VideoHeight})");

            // Rough estimate of how much data is waiting to be consumed, at 30fps a queued
            // video packet is about 33ms, audio payloads are 21ms each plus whatever the
            // sysmodule is batching (also 21ms per batch unit)
            int videoPending = Manager.VideoTarget?.Pending ?? 0;
            sb.AppendLine($"Video pending packets: {videoPending} (~{videoPending * 1000 / 30} ms buffered)");

            if (Manager.AudioTarget is AudioPlayerTarget audio)
                sb.AppendLine($"Audio pending packets: {audio.Pending} (~{audio.Pending * 21} ms buffered)");

            sb.AppendLine($"Audio batching: {Program.Options.Streaming.AudioBatching} (~{Program.Options.Streaming.AudioBatching * 21} ms added by the console)");
            sb.AppendLine($"IsCompatibleAudioStream: {Manager.IsCompatibleAudioStream}");
            return sb.ToString();
        }

        public string? GetChosenDecoder()
        {
            if (Program.Options.DecoderName is not null)
            {
                if (Video.DecoderName != Program.Options.DecoderName)
                {
                    return string.Format(Program.Strings.Player.CustomDecoderError, Program.Options.DecoderName, Video.DecoderName);
                }
                else
                {
                    return string.Format(Program.Strings.Player.CustomDecoderEnabled, Program.Options.DecoderName);
                }
            }

            if (Video.UsingCustomDecoder)
            {
                return string.Format(Program.Strings.Player.CustomDecoderEnabled, Video.DecoderName);
            }

            return null;
        }

        // For imgui usage, this function draws the current frame
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool DrawAsync()
        {
            if (Video is null)
                return false;

            if (Video.DecodeFrame())
            {
                fps.OnFrame();
            }

            // Bypass imgui for this
            DrawTexture();

            // Signal we're presenting something to SDL to kick the decoding thread
            // We don't care if we didn't actually decode anything we just do it here
            // to do this on every vsync to avoid arbitrary sleeps on the other side
            Video.Decoder.OnFrameEvent.Set();

            return true;
        }

        // For legacy player usage, this locks the thread until the next frame is ready
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool DrawLocked()
        {
            if (Video is null)
                return false;

            if (!Video.DecodeFrame())
                return false;

            DrawTexture();
            Video.Decoder.OnFrameEvent.Set();

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void DrawTexture()
        {
            if (Video is null)
                return;

            if (RotationQuarterTurns == 0)
            {
                SDL_RenderCopy(Program.SdlCtx.RendererHandle, Video.TargetTexture, ref Video.TargetTextureSize, ref DisplayRect);
            }
            else
            {
                SDL_RenderCopyEx(Program.SdlCtx.RendererHandle, Video.TargetTexture, ref Video.TargetTextureSize, ref DisplayRect, RotationQuarterTurns * 90, IntPtr.Zero, SDL_RendererFlip.SDL_FLIP_NONE);
            }
        }

        private unsafe void InitializeLoadingTexture()
        {
            Program.SdlCtx.BugCheckThreadId();

            // This hardcodes YUV dats
            if (Video.TargetTextureFormat != SDL_PIXELFORMAT_IYUV)
                return;

            int w = Video.TargetTextureSize.w;
            int h = Video.TargetTextureSize.h;

            byte[] data = null;

            // The bundled loading image is a 1280x720 frame, it can only be used as-is
            // when the texture has the stream resolution
            if (w == 1280 && h == 720)
            {
                try
                {
                    data = Resources.ReadResource(Resources.LoadingImage);
                }
                catch
                {
                    // Don't care
                }
            }

            if (data == null)
            {
                // Solid white frame matching the current texture size
                int ySize = w * h;
                int cSize = (w / 2) * (h / 2);

                data = new byte[ySize + cSize * 2];
                data.AsSpan(0, ySize).Fill(0xFF);
                data.AsSpan(ySize, cSize).Fill(0x7F);
                data.AsSpan(ySize + cSize, cSize).Fill(0x80);
            }

            fixed (byte* ptr = data)
            {
                int ySize = w * h;
                int cSize = (w / 2) * (h / 2);

                SDL_UpdateYUVTexture(Video.TargetTexture, ref Video.TargetTextureSize,
                    (nint)ptr, w, (nint)(ptr + ySize), w / 2, (nint)(ptr + ySize + cSize), w / 2);
            }
        }
    }

    internal class PlayerView : View
    {
        public readonly StringTable.PlayerTable Strings = Program.Strings.Player;

        readonly bool HasAudio;
        readonly bool HasVideo;

        readonly PlayerCore player;

        bool OverlayAlwaysShowing = false;

        List<PendingUiNotif> notifications = new();

        // Texts produced by the streaming thread, moved to the list above by the UI thread
        readonly Queue<string> pendingNotifications = new();

        Gui.CenterGroup uiOptCenter;
        Gui.CenterGroup quitOptCenter;
        Gui.Popup quitConfirm = new(Program.Strings.Player.ConfirmQuitPopupTitle);
        Gui.Popup fatalError = new(Program.Strings.General.PopupErrorTitle);

        bool drawUi;
        string fatalMessage;

        // Video settings being edited in the overlay panel. Every change is pushed to the
        // running stream as soon as it happens and stored back in the options so the next
        // session starts with the same values
        bool showVideoSettings;
        // Set the frame the panel opens so it is explicitly brought to front once
        bool videoSettingsFocusPending;

        // On screen stats overlay
        bool showStats;
        long lastStatsSampleMs;
        long lastVideoBytes, lastAudioBytes;
        float shownVideoRate, shownAudioRate;

        // H.264 decoders available in the bundled ffmpeg, enumerated on demand
        List<LibavUtils.Codec>? decoderList;

        // Frametime history for the stats graph, only sampled while the overlay is on
        const int FrametimeHistorySize = 120;
        readonly float[] frametimeHistory = new float[FrametimeHistorySize];
        int frametimeIndex;
        long lastFrameTickMs;
        float lastFrameTime;

        VideoUpscaleMode settingsMode;
        VideoUpscaleFilter settingsFilter;
        bool settingsSharpen;
        float settingsSharpenAmount;
        bool settingsAntiAlias;
        float settingsAntiAliasAmount;

        public bool IsRecording => videoRecorder is not null;
        string recordingButtonText = Program.Strings.Player.StartRecording;
        Mp4Output? videoRecorder;

        readonly string volumePercentFormat;

        // Set when this session may be automatically retried after a fatal error
        readonly DeviceInfo? reconnectInfo;
        readonly int reconnectAttempt;
        int fatalReconnectScheduled;

        void MessageUi(string message)
        {
            Console.WriteLine(message);

            // This can be called from the streaming thread, the notification list is only
            // touched by the UI thread so queue the text instead of adding it directly
            lock (pendingNotifications)
                pendingNotifications.Enqueue(message);
        }

        public override void Created()
        {
            StreamStats.Reset();
            player.Start();
            base.Created();
        }

        public override void DrawDebug()
        {
            ImGui.Text(player.GetDebugString());
        }

        void ShowPlayerOptionMessage()
        {
            var dec = player.GetChosenDecoder();
            if (dec is not null)
                MessageUi(dec);
        }

        // Adaptive rendering causes a lot of stuttering, for now avoid it in the video player
        static FramerateCapOptions CurrentRenderMode() =>
            Program.Options.UncapStreaming ? FramerateCapOptions.Uncapped() :
            FramerateCapOptions.Target(36);

        public PlayerView(ClientApp owner, PlayerManager manager, DeviceInfo? reconnectInfo = null, int reconnectAttempt = 0) : base(owner)
        {
            this.reconnectInfo = reconnectInfo;
            this.reconnectAttempt = reconnectAttempt;

            RenderMode = CurrentRenderMode();

            Popups.Add(quitConfirm);
            Popups.Add(fatalError);

            HasVideo = manager.HasVideo;
            HasAudio = manager.HasAudio;

            if (!HasAudio && !HasVideo)
                throw new Exception("Can't start a player with no streams");

            manager.OnFatalError += Manager_OnFatalError;
            manager.OnErrorMessage += Manager_OnErrorMessage;

            player = new PlayerCore(manager);

            if (HasVideo)
                ShowPlayerOptionMessage();

            if (!HasVideo)
                OverlayAlwaysShowing = true;

            settingsMode = Program.Options.UpscaleMode;
            settingsFilter = Program.Options.UpscaleFilter;
            settingsSharpen = Program.Options.UpscaleSharpen;
            settingsSharpenAmount = Math.Clamp(Program.Options.UpscaleSharpenAmount, 0f, 1f);
            settingsAntiAlias = Program.Options.AntiAlias;
            settingsAntiAliasAmount = Math.Clamp(Program.Options.AntiAliasAmount, 0f, 1f);

            drawUi = OverlayAlwaysShowing;

            if (Program.Options.PlayerHotkeys && !Program.IsAndroid) // Android is less likely to have a keyboard so don't show the hint. The hotkeys still work.
                MessageUi(Strings.Shortcuts);

            // Convert C# format to ImGui format
            volumePercentFormat = Strings.VolumePercent
                .Replace("%", "%%") // escape the % sign
                .Replace("{0}", "%d"); // replace the format specifier
        }

        private void Manager_OnErrorMessage(string obj) =>
            MessageUi(obj);

        private void Manager_OnFatalError(Exception obj)
        {
            // With auto reconnect enabled a dead stream goes back to the connection
            // screen for another attempt instead of showing the error popup
            if (Program.Options.AutoReconnect && reconnectInfo is not null &&
                reconnectAttempt < Options.AutoReconnectMaxAttempts &&
                Interlocked.Exchange(ref fatalReconnectScheduled, 1) == 0)
            {
                try
                {
                    Owner.ReplaceView(new ConnectingView(Owner, reconnectInfo, Program.Options.Streaming, reconnectAttempt + 1));
                }
                catch (Exception ex)
                {
                    // A view change may already be scheduled (eg. the user just quit)
                    Console.WriteLine($"Reconnect failed to start: {ex.Message}");
                }
                return;
            }

            fatalMessage = obj.ToString();

            // Fatal errors arrive from the streaming thread, popups are ImGui state and
            // must only be touched by the UI thread
            Owner.PostAction(() => Popups.Open(fatalError));
        }

        public override void Draw()
        {
            // Cursor is hidden only during full screen and when there are no other popups
            bool shouldHideCursor = Program.SdlCtx.IsFullscreen;

            if (!Gui.BeginWindow("Player", ImGuiWindowFlags.NoBackground))
            {
                // Nothing to do here
                Program.SdlCtx.ShowCursor(true);
                return;
            }

            if (!HasVideo)
            {
                // When there is no video, show the cursor.
                shouldHideCursor = false;

                H2();
                Gui.CenterText(Strings.AudioOnlyMode);
                PopFont();
            }

            // Move any text queued by the streaming thread into the visible list
            lock (pendingNotifications)
            {
                while (pendingNotifications.Count > 0)
                    notifications.Add(new PendingUiNotif(pendingNotifications.Dequeue()));
            }

            for (int i = 0; i < notifications.Count; i++)
            {
                var notif = notifications[i];
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0, 0, 1));
                ImGui.TextWrapped(notif.Text);
                ImGui.PopStyleColor();
                if (notif.ShouldRemove)
                {
                    notifications.RemoveAt(i);
                    notif.Dispose();
                }
            }

            if (showStats)
                DrawStatsOverlay();

            if (drawUi)
            {
                shouldHideCursor = false;
                DrawOverlayMenu();
            }

            if (!OverlayAlwaysShowing && !showVideoSettings)
                DrawOverlayToggleArea();

            if (quitConfirm.Begin())
            {
                shouldHideCursor = false;
                DrawQuitModal();
            }

            if (fatalError.Begin(ImGui.GetIO().DisplaySize * 0.95f))
            {
                shouldHideCursor = false;

                ImGui.TextWrapped(fatalMessage);
                if (Gui.CenterButton(GeneralStrings.PopupCloseButton))
                    Owner.PopView();

                Gui.MakeWindowScrollable();
                ImGui.EndPopup();
            }

            ImGui.End();

            Program.SdlCtx.ShowCursor(!shouldHideCursor);
        }

        public override void OnKeyPressed(SDL_Keysym key)
        {
            if (!Program.Options.PlayerHotkeys)
                return;

            // Handle hotkeys
            if (key.sym == SDL_Keycode.SDLK_s)
                ButtonScreenshot();
            if (key.sym == SDL_Keycode.SDLK_r)
                ButtonToggleRecording();
            if (key.sym == SDL_Keycode.SDLK_t)
                player.RotateClockwise();
            if (key.sym == SDL_Keycode.SDLK_f)
                Program.SdlCtx.SetFullScreen(!Program.SdlCtx.IsFullscreen);
            if (key.sym == SDL_Keycode.SDLK_v && HasVideo)
            {
                // The panel lives inside the overlay, make sure it is visible first
                if (!drawUi)
                    drawUi = true;
                ToggleVideoSettings();
            }
            if (key.sym == SDL_Keycode.SDLK_F3 && HasVideo)
                showStats = !showStats;
            if (key.sym == SDL_Keycode.SDLK_c && HasVideo && reconnectInfo is not null)
            {
                // Restart the stream without leaving the player, the attempt counter resets
                try { Owner.ReplaceView(new ConnectingView(Owner, reconnectInfo, Program.Options.Streaming, 0)); }
                catch (Exception ex) { Console.WriteLine($"Reconnect failed to start: {ex.Message}"); }
            }
            if (key.sym == SDL_Keycode.SDLK_UP && player.Manager.AudioTarget is not null)
                player.Manager.AudioTarget.Volume += 0.1f;
            if (key.sym == SDL_Keycode.SDLK_DOWN && player.Manager.AudioTarget is not null)
                player.Manager.AudioTarget.Volume -= 0.1f;
        }

        public override void BackPressed()
        {
            if (Popups.AnyOpen)
            {
                base.BackPressed();
                return;
            }

            Popups.Open(quitConfirm);
        }

        void DrawOverlayToggleArea()
        {
            var rect = new ImRect()
            {
                Min = ImGui.GetWindowPos(),
                Max = ImGui.GetWindowPos() + ImGui.GetWindowSize(),
            };

            var id = ImGui.GetID("##TepToReveal");
            ImGui.KeepAliveID(id);

            if (ImGui.ButtonBehavior(rect, id, out _, out _, ImGuiButtonFlags.MouseButtonLeft) || ImGui.IsKeyPressed(ImGuiKey.Space))
                drawUi = !drawUi;
        }

        void DrawVolumeSlider(float x, float width)
        {
            if (player.Manager.AudioTarget is not null)
            {
                var vol = (int)(player.Manager.AudioTarget.Volume * 100);
                var volnew = vol;
                ImGui.SetCursorPosX(x);
                ImGui.PushItemWidth(width);
                ImGui.SliderInt("##VolumeSlider", ref volnew, 0, 100, volumePercentFormat);
                if (vol != volnew)
                    player.Manager.AudioTarget.Volume = volnew / 100f;
            }
        }

        void DrawOverlayMenu()
        {
            float OverlayY = ImGui.GetWindowSize().Y;

            if (Owner.IsPortrait)
            {
                OverlayY = OverlayY * 6 / 10;
                ImGui.SetCursorPosY(OverlayY + ImGui.GetStyle().WindowPadding.Y);

                var width = ImGui.GetWindowSize().X;

                var btnwidth = width * 3 / 6;
                var btnheight = (ImGui.GetWindowSize().Y - ImGui.GetCursorPosY()) / 8;
                var btnsize = new Vector2(btnwidth, btnheight);

                var center = width / 2 - btnwidth / 2;

                if (HasVideo)
                {
                    ImGui.SetCursorPosX(center);
                    if (ImGui.Button(Strings.TakeScreenshot, btnsize)) ButtonScreenshot();
                }

                if (HasVideo)
                {
                    ImGui.SetCursorPosX(center);
                    if (ImGui.Button(Strings.Rotate, btnsize)) player.RotateClockwise();
                }

                if (HasVideo)
                {
                    ImGui.SetCursorPosX(center);
                    if (ImGui.Button(Strings.VideoSettingsButton, btnsize)) ToggleVideoSettings();
                }

                ImGui.SetCursorPosX(center);
                if (ImGui.Button(recordingButtonText, btnsize)) ButtonToggleRecording();

                ImGui.SetCursorPosX(center);
                if (ImGui.Button(Strings.StopStreaming, btnsize)) ButtonQuit();

                if (Program.Options.Debug.Log)
                {
                    ImGui.SetCursorPosX(center);
                    if (ImGui.Button(Strings.DebugInfo, btnsize)) ButtonStats();
                }

                ImGui.SetCursorPosX(center);
                if (ImGui.Button(Strings.EnterFullScreen, btnsize)) ButtonFullscreen();

                ImGui.NewLine();
                DrawVolumeSlider(center, btnwidth);
            }
            else
            {
                OverlayY = OverlayY * 4 / 6;
                var spacing = ImGui.GetStyle().ItemSpacing.X * 3;

                ImGui.SetCursorPosY(OverlayY + ImGui.GetStyle().WindowPadding.Y);

                uiOptCenter.StartHere();
                if (HasVideo)
                {
                    if (ImGui.Button(Strings.TakeScreenshot)) ButtonScreenshot();
                    ImGui.SameLine();
                    if (ImGui.Button(Strings.Rotate)) player.RotateClockwise();
                    ImGui.SameLine();
                    if (ImGui.Button(Strings.VideoSettingsButton)) ToggleVideoSettings();
                    ImGui.SameLine();
                }
                if (ImGui.Button(recordingButtonText)) ButtonToggleRecording();
                ImGui.SameLine(0, spacing);
                if (ImGui.Button(Strings.StopStreaming)) ButtonQuit();
                ImGui.SameLine(0, spacing);

                if (Program.Options.Debug.Log)
                {
                    if (ImGui.Button(Strings.DebugInfo)) ButtonStats();
                    ImGui.SameLine();
                }

                if (ImGui.Button(Strings.EnterFullScreen)) ButtonFullscreen();
                uiOptCenter.EndHere();

                ImGui.NewLine();
                var w = ImGui.GetWindowSize().X;
                DrawVolumeSlider(w / 4, w / 2);
            }

            if (!OverlayAlwaysShowing)
            {
                ImGui.SetCursorPosY(ImGui.GetWindowSize().Y - ImGui.CalcTextSize("A").Y - ImGui.GetStyle().WindowPadding.Y);
                Gui.CenterText(Strings.HideOverlayLabel);
            }

            // Drawn last so it sits on top of the overlay band, it is a normal window and
            // not a popup on purpose: a modal would dim the video and you would not be able
            // to see what the changes you are making actually do
            if (showVideoSettings && HasVideo)
                DrawVideoSettings(OverlayY);

            ImGui.GetBackgroundDrawList().AddRectFilled(new(0, OverlayY), ImGui.GetWindowSize(), 0xe0000000);
        }

        static readonly VideoUpscaleMode[] ResolutionModes =
        {
            VideoUpscaleMode.Auto, VideoUpscaleMode.HD1080, VideoUpscaleMode.QHD1440, VideoUpscaleMode.UHD2160
        };

        static readonly VideoUpscaleFilter[] FilterModes =
        {
            VideoUpscaleFilter.Linear, VideoUpscaleFilter.Nearest, VideoUpscaleFilter.Bicubic,
            VideoUpscaleFilter.Lanczos, VideoUpscaleFilter.LanczosSoft
        };

        static readonly PresentMode[] PresentModes =
        {
            PresentMode.VSync, PresentMode.Adaptive, PresentMode.Off
        };

        string PresentName(PresentMode mode) => mode switch
        {
            PresentMode.VSync => Strings.PresentVsync,
            PresentMode.Adaptive => Strings.PresentAdaptive,
            _ => Strings.PresentOff,
        };

        string ResolutionName(VideoUpscaleMode mode) => mode switch
        {
            VideoUpscaleMode.HD1080 => "1080p",
            VideoUpscaleMode.QHD1440 => "1440p",
            VideoUpscaleMode.UHD2160 => "4K",
            _ => Strings.ResolutionNative,
        };

        string FilterName(VideoUpscaleFilter filter) => filter switch
        {
            VideoUpscaleFilter.Linear => Strings.FilterLinear,
            VideoUpscaleFilter.Nearest => Strings.FilterNearest,
            VideoUpscaleFilter.Bicubic => Strings.FilterBicubic,
            VideoUpscaleFilter.LanczosSoft => Strings.FilterLanczosSoft,
            _ => Strings.FilterLanczos,
        };

        void ToggleVideoSettings()
        {
            showVideoSettings = !showVideoSettings;
            if (showVideoSettings)
                videoSettingsFocusPending = true;
        }

        // bandTop is where the control overlay starts, the panel is kept above it so it never
        // covers the buttons while you are adjusting the picture
        void DrawVideoSettings(float bandTop)
        {
            float scale = Owner.UiScale;

            var size = new Vector2(
                MathF.Min(470 * scale, ImGui.GetIO().DisplaySize.X - 60 * scale),
                MathF.Min(MathF.Max(bandTop - 14 * scale, 260 * scale), 620 * scale));

            ImGui.SetNextWindowPos(new Vector2(
                ImGui.GetIO().DisplaySize.X - size.X - 25 * scale,
                12 * scale));

            ImGui.SetNextWindowSize(size);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12 * scale);

            ImGui.Begin("##VideoSettings",
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoSavedSettings);

            // Bring the panel above the player window the single frame it opens. The player
            // window can never come to front again (Gui.BeginWindow adds NoBringToFrontOnFocus)
            // so the panel stays reachable from then on. Do not focus it every frame though:
            // that fights the click focus of the menu buttons and breaks them.
            if (videoSettingsFocusPending)
            {
                videoSettingsFocusPending = false;
                ImGui.SetWindowFocus();
            }

            bool changed = false;

            ImGui.Text(Strings.VideoSettings);
            ImGui.Separator();

            // One click profiles, they also change the streaming options of the latency one
            var presetW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2) / 3;
            if (ImGui.Button(Strings.PresetQuality, new Vector2(presetW, 0)))
                ApplyPreset(0);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.PresetQualityTooltip);
            ImGui.SameLine();
            if (ImGui.Button(Strings.PresetBalanced, new Vector2(presetW, 0)))
                ApplyPreset(1);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.PresetBalancedTooltip);
            ImGui.SameLine();
            if (ImGui.Button(Strings.PresetLatency, new Vector2(presetW, 0)))
                ApplyPreset(2);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.PresetLatencyTooltip);

            ImGui.Spacing();

            ImGui.Text(Strings.ResolutionLabel);
            ImGui.SetNextItemWidth(-1);

            if (ImGui.BeginCombo("##Resolution", ResolutionName(settingsMode)))
            {
                foreach (var mode in ResolutionModes)
                {
                    if (ImGui.Selectable(ResolutionName(mode), mode == settingsMode))
                    {
                        settingsMode = mode;
                        changed = true;
                    }
                }

                ImGui.EndCombo();
            }

            if (settingsMode == VideoUpscaleMode.Auto && ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.ResolutionFastTooltip);

            // The filter and the sharpening only exist for the CPU scaler, in fast GPU
            // mode the graphics card does the enlargement and neither of them applies
            bool cpuUpscale = settingsMode != VideoUpscaleMode.Auto;

            if (cpuUpscale)
            {
                ImGui.Text(Strings.FilterLabel);
                ImGui.SetNextItemWidth(-1);

                if (ImGui.BeginCombo("##Filter", FilterName(settingsFilter)))
                {
                    foreach (var filter in FilterModes)
                    {
                        if (ImGui.Selectable(FilterName(filter), filter == settingsFilter))
                        {
                            settingsFilter = filter;
                            changed = true;
                        }
                    }

                    ImGui.EndCombo();
                }
            }

            ImGui.Text(Strings.PresentLabel);
            ImGui.SetNextItemWidth(-1);

            if (ImGui.BeginCombo("##Present", PresentName(Program.Options.Presentation)))
            {
                foreach (var mode in PresentModes)
                {
                    if (ImGui.Selectable(PresentName(mode), mode == Program.Options.Presentation))
                    {
                        Program.Options.Presentation = mode;
                        Program.SdlCtx.ApplyPresentMode(mode);
                        SaveSettings();
                    }
                }

                ImGui.EndCombo();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.PresentTooltip);

            ImGui.Spacing();

            // The checkbox and its slider share a row, one row per option keeps the whole
            // panel above the control overlay even on a short window
            if (ImGui.Checkbox(Strings.AntiAliasLabel, ref settingsAntiAlias))
                changed = true;

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.AntiAliasTooltip);

            if (settingsAntiAlias)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat("##AntiAlias", ref settingsAntiAliasAmount, 0f, 1f, "%.0f %%"))
                    changed = true;
            }

            if (cpuUpscale && ImGui.Checkbox(Strings.SharpenLabel, ref settingsSharpen))
                changed = true;

            if (cpuUpscale && ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.SharpenTooltip);

            if (cpuUpscale && settingsSharpen)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat("##Sharpen", ref settingsSharpenAmount, 0f, 1f, "%.0f %%"))
                    changed = true;
            }

            ImGui.Spacing();

            if (ImGui.Checkbox(Strings.StatsToggleLabel, ref showStats))
            { }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.StatsToggleTooltip);

            if (ImGui.Checkbox(Strings.FillScreenLabel, ref Program.Options.FillScreen))
            {
                player.ResolutionChanged();
                SaveSettings();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.FillScreenTooltip);

            if (ImGui.Checkbox(Strings.AutoReconnectLabel, ref Program.Options.AutoReconnect))
                SaveSettings();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.AutoReconnectTooltip);

            // Video decoder, only applied on a new connection so also offer to reconnect now
            ImGui.Spacing();
            ImGui.Text(Strings.DecoderLabel);
            ImGui.SetNextItemWidth(-1);

            if (decoderList is null)
            {
                decoderList = new List<LibavUtils.Codec>();
                decoderList.AddRange(LibavUtils.GetH264Decoders());
            }

            string decoderTarget = Program.Options.DecoderName ?? "";

            if (ImGui.BeginCombo("##Decoder", decoderTarget == "" ? Strings.DecoderSoftware : decoderTarget))
            {
                if (ImGui.Selectable(Strings.DecoderSoftware, decoderTarget == ""))
                {
                    Program.Options.DecoderName = null;
                    SaveSettings();
                }

                foreach (var c in decoderList)
                {
                    if (ImGui.Selectable(c.Name, decoderTarget == c.Name))
                    {
                        Program.Options.DecoderName = c.Name;
                        SaveSettings();
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(c.Description);
                }

                ImGui.EndCombo();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.DecoderTooltip);

            // A changed decoder only takes effect once the stream is restarted
            string decoderActive = player.Video?.DecoderName ?? "";
            if (decoderTarget != decoderActive && !(decoderTarget == "" && decoderActive == "h264"))
            {
                ImGui.TextDisabled(Strings.DecoderPendingHint);

                if (reconnectInfo is not null && ImGui.Button(Strings.ReconnectNow))
                {
                    try { Owner.ReplaceView(new ConnectingView(Owner, reconnectInfo, Program.Options.Streaming, 0)); }
                    catch (Exception ex) { Console.WriteLine($"Reconnect failed to start: {ex.Message}"); }
                }
            }

            ImGui.TextDisabled(Strings.VideoSettingsHint);

            ImGui.End();
            ImGui.PopStyleVar();

            if (changed)
                ApplyVideoSettings();
        }

        // 0 = quality, 1 = balanced, 2 = low latency. Only the latency profile
        // also touches the streaming options since they only affect delay
        void ApplyPreset(int preset)
        {
            settingsMode = VideoUpscaleMode.HD1080;

            if (preset == 0)
            {
                settingsFilter = VideoUpscaleFilter.Lanczos;
                settingsSharpen = true;
                settingsSharpenAmount = 0.35f;
                settingsAntiAlias = true;
                settingsAntiAliasAmount = 0.5f;
            }
            else if (preset == 1)
            {
                settingsFilter = VideoUpscaleFilter.LanczosSoft;
                settingsSharpen = true;
                settingsSharpenAmount = 0.35f;
                settingsAntiAlias = true;
                settingsAntiAliasAmount = 0.5f;
            }
            else
            {
                settingsFilter = VideoUpscaleFilter.LanczosSoft;
                settingsSharpen = false;
                settingsAntiAlias = false;

                // These two are only read when the stream starts: the audio batching is sent
                // to the console in the handshake and the frame cap is taken from the render
                // mode, so apply what can be applied live and tell the user about the rest
                Program.Options.UncapStreaming = true;
                Program.Options.Streaming.AudioBatching = 0;
                Owner.ApplyRenderMode(CurrentRenderMode());
                MessageUi(Strings.PresetLatencyNeedsReconnect);
            }

            ApplyVideoSettings();
        }

        void ApplyVideoSettings()
        {
            if (player.Video is null)
                return;

            player.Video.ApplyVideoProcessing(settingsMode, settingsFilter,
                settingsSharpen, settingsSharpenAmount,
                settingsAntiAlias, settingsAntiAliasAmount);

            // The texture size may have changed, refit the on screen bounds
            player.ResolutionChanged();

            Program.Options.UpscaleMode = settingsMode;
            Program.Options.UpscaleFilter = settingsFilter;
            Program.Options.UpscaleSharpen = settingsSharpen;
            Program.Options.UpscaleSharpenAmount = settingsSharpenAmount;
            Program.Options.AntiAlias = settingsAntiAlias;
            Program.Options.AntiAliasAmount = settingsAntiAliasAmount;

            SaveSettings();
        }

        void SaveSettings()
        {
            try
            {
                SystemUtil.StoreSettingsString(Program.Options.SerializeToJson());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save the settings: {ex.Message}");
            }
        }

        void DrawQuitModal()
        {
            ImGui.Text(Strings.ConfirmQuitLabel);
            ImGui.Separator();

            var w = ImGui.GetWindowSize().X / 4;
            quitOptCenter.StartHere();

            if (ImGui.Button(GeneralStrings.YesButton, new(w, 0)))
            {
                quitConfirm.RequestClose();
                Owner.PopView();
            }

            ImGui.SameLine();

            if (ImGui.Button(GeneralStrings.NoButton, new(w, 0)))
                quitConfirm.RequestClose();

            quitOptCenter.EndHere();

            ImGui.EndPopup();
        }

        void ScreenshotToClipboard()
        {
            if (!Program.IsWindows)
                throw new Exception("Screenshots to clipboard are only supported on windows");

            using (var cap = SDLCapture.CaptureTexture(player.Video.TargetTexture))
                Platform.Specific.Win.WinClipboard.CopyCapture(cap);

            MessageUi(Strings.ScreenshotSavedToClip);
        }

        void ScreenshotToFile()
        {
            var path = Program.Options.GetFilePathForScreenshot();
            SDLCapture.ExportTexture(player.Video.TargetTexture, path);
            MessageUi(string.Format(Strings.ScreenshotSaved, path));
        }

        void ButtonScreenshot()
        {
            try
            {
                if (Program.IsWindows)
                {
                    var clip = Program.Options.Windows_ScreenToClip;
                    // shift inverts the clipboard flag
                    if (Owner.ShiftDown)
                        clip = !clip;

                    if (clip)
                    {
                        ScreenshotToClipboard();
                        return;
                    }
                }

                ScreenshotToFile();
            }
            catch (Exception ex)
            {
                MessageUi($"{GeneralStrings.ErrorMessage} {ex}");
                Console.WriteLine(ex);
#if ANDROID_LIB
                MessageUi(Strings.AndroidPermissionError);
#endif
            }
        }

        void ButtonToggleRecording()
        {
            if (videoRecorder is null)
            {
                try
                {
                    var videoFile = Program.Options.GetFilePathForVideo();

                    Mp4VideoTarget? v = HasVideo ? new() : null;
                    Mp4AudioTarget? a = HasAudio ? new() : null;

                    videoRecorder = new Mp4Output(videoFile, v, a);
                    videoRecorder.Start();

                    player.Manager.ChainTargets(v, a);

                    recordingButtonText = Strings.StopRecording;
                    MessageUi(string.Format(Strings.RecordingStartedMessage, videoFile));
                }
                catch (Exception ex)
                {
                    MessageUi($"{GeneralStrings.ErrorMessage} {ex}");
                    videoRecorder?.Dispose();
                    videoRecorder = null;
                }
            }
            else
            {
                player.Manager.UnchainTargets(videoRecorder.VideoTarget, videoRecorder.AudioTarget);
                videoRecorder.Stop();
                videoRecorder.Dispose();
                videoRecorder = null;
                recordingButtonText = Strings.StartRecording;
                MessageUi(Strings.RecordingSuccessMessage);
            }
        }

        void DrawStatsOverlay()
        {
            float scale = Owner.UiScale;
            long now = Environment.TickCount64;

            player.SampleFps();

            // Sample the time between frames for the graph
            if (lastFrameTickMs != 0)
            {
                float dt = now - lastFrameTickMs;
                if (dt >= 0 && dt < 500)
                {
                    lastFrameTime = dt;
                    frametimeHistory[frametimeIndex] = dt;
                    frametimeIndex = (frametimeIndex + 1) % FrametimeHistorySize;
                }
            }
            lastFrameTickMs = now;

            // Bandwidth rates are refreshed once per second
            if (lastStatsSampleMs == 0)
                lastStatsSampleMs = now;
            else if (now - lastStatsSampleMs >= 1000)
            {
                float dt = (now - lastStatsSampleMs) / 1000f;
                long vb = StreamStats.VideoBytes;
                long ab = StreamStats.AudioBytes;
                shownVideoRate = (vb - lastVideoBytes) * 8f / 1000f / dt;
                shownAudioRate = (ab - lastAudioBytes) * 8f / 1000f / dt;
                lastVideoBytes = vb;
                lastAudioBytes = ab;
                lastStatsSampleMs = now;
            }

            var cpu = StreamStats.GetCpuPercent();
            var gpu = StreamStats.GetGpuPercent();

            ImGui.SetNextWindowPos(new Vector2(12 * scale, 12 * scale));
            ImGui.SetNextWindowBgAlpha(0.65f);
            bool statsOpen = ImGui.Begin("##StatsOverlay",
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize);

            if (statsOpen)
            {
                var video = player.Video;
                if (video is not null)
                    video.DecayPeak();

                string pipeline = video is null ? "-" :
                    $"{video.LastPipelineMs:F1} ms ({Strings.StatsPeak} {video.PeakPipelineMs:F1})";

                ImGui.Text($"FPS {player.LastFps}    CPU {cpu:F0}%    GPU {(gpu >= 0 ? gpu.ToString("F0") + "%" : "-")}");
                ImGui.Text($"{Strings.StatsPipeline}: {pipeline} / 33.3 ms");
                ImGui.Text($"{Strings.StatsDown}: {(shownVideoRate + shownAudioRate):F1} Mb/s  (V {shownVideoRate:F1} / A {shownAudioRate:F1})");
                ImGui.Text($"{Strings.StatsBuffered}: {player.Manager.VideoTarget?.Pending ?? 0}    {Strings.StatsDropped}: {StreamStats.TotalDrops}");
                ImGui.Text($"{Strings.StatsDecoder}: {player.Video?.DecoderName ?? "-"}");

                // Render loop frametime graph: the pale line is the 30fps budget, segments are
                // cyan while inside it, yellow starting to be late and red when missing frames
                var graphSize = new Vector2(Math.Max(140 * scale, ImGui.GetContentRegionAvail().X), 42 * scale);
                var graphPos = ImGui.GetCursorScreenPos();
                var dl = ImGui.GetWindowDrawList();

                dl.AddRectFilled(graphPos, graphPos + graphSize, 0x80000000, 3 * scale);

                const float maxMs = 66.67f; // two frame budgets, anything worse is pinned to the top
                float budgetY = graphPos.Y + graphSize.Y * (33.33f / maxMs);
                dl.AddLine(new Vector2(graphPos.X, budgetY), new Vector2(graphPos.X + graphSize.X, budgetY), 0x60FFFFFF, 1 * scale);

                Vector2? prev = null;
                for (int i = 0; i < FrametimeHistorySize; i++)
                {
                    int idx = (frametimeIndex + i) % FrametimeHistorySize;
                    float v = frametimeHistory[idx];
                    if (v <= 0)
                    {
                        prev = null;
                        continue;
                    }

                    var pt = new Vector2(
                        graphPos.X + graphSize.X * i / (FrametimeHistorySize - 1),
                        graphPos.Y + graphSize.Y * Math.Min(v, maxMs) / maxMs);

                    if (prev is not null)
                    {
                        uint col = v <= 33.33f ? 0xFF22D3EE : v <= 50f ? 0xFFFFC24D : 0xFFFF4D4D;
                        dl.AddLine(prev.Value, pt, col, 1.5f * scale);
                    }

                    prev = pt;
                }

                ImGui.Dummy(graphSize);
            }

            ImGui.End();
        }

        void ButtonStats()
        {
            Owner.ShowDebugInfo = !Owner.ShowDebugInfo;
        }

        void ButtonQuit()
        {
            BackPressed();
        }

        void ButtonFullscreen()
        {
            Program.SdlCtx.SetFullScreen(!Program.SdlCtx.IsFullscreen);
        }

        unsafe public override void RawDraw()
        {
            base.RawDraw();
            player.DrawAsync();
        }

        public override void ResolutionChanged()
        {
            player.ResolutionChanged();
        }

        public override void Destroy()
        {
            Program.SdlCtx.BugCheckThreadId();

            Program.SdlCtx.ShowCursor(true);

            if (IsRecording)
                ButtonToggleRecording();

            player.Destroy();
            base.Destroy();
        }
    }
}
