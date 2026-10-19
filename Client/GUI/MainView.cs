using ImGuiNET;
using SysDVR.Client.App;
using SysDVR.Client.Core;
using SysDVR.Client.GUI.Components;
using SysDVR.Client.Platform;
using System;
using System.Numerics;

namespace SysDVR.Client.GUI
{
    internal class MainView : View
    {
        readonly StringTable.HomePageTable Strings = Program.Strings.HomePage;

        readonly string Heading;
        readonly string SecondLine;

        readonly Image Logo;
        readonly Image UsbIcon;
        readonly Image WifiIcon;

        // Only shown when compiled as DEBUG
        readonly string DevelopmentBuild;

        bool HasDiskPermission;
        bool CanRequestDiskPermission;

        StreamKind StreamMode = StreamKind.Both;

        // Left edge and width of the region the mode buttons and the options live in, they
        // position themselves with absolute window coordinates so the navigation rail has to
        // be subtracted from every one of them
        float contentX;
        float contentW;

        Gui.Popup initErrorPopup = new Gui.Popup(Program.Strings.HomePage.InitializationErrorTitle);
        string? initError;

        float uiScale;
        float sidebarWidth;
        int ModeButtonWidth;
        int ModeButtonHeight;

        public MainView(ClientApp owner) : base(owner)
        {
            Popups.Add(initErrorPopup);

            // The app name is already the big title in the rail, this line is only the version
            Heading = Program.DisplayVersion;
            SecondLine = $"build id {Program.BuildID}";
            DevelopmentBuild = TextEncoding.ToPlainText("Welcome to SysDVR-dev, do not open github issues for development versions as they come with no support");

            UpdateDiskPermissionStatus();

			initError = Program.GetInitializationError();
			if (!string.IsNullOrWhiteSpace(initError))
                Popups.Open(initErrorPopup);

            Logo = Image.FromFile(Owner, Resources.Logo);
            UsbIcon = Image.FromFile(Owner, Resources.UsbIcon);
            WifiIcon = Image.FromFile(Owner, Resources.WifiIcon);
        }

		public override void EnterForeground()
		{
			base.EnterForeground();
			StreamMode = Program.Options.Streaming.Kind;
		}

		void UpdateDiskPermissionStatus()
        {
            HasDiskPermission = Resources.HasDiskAccessPermission();
            if (!HasDiskPermission)
                CanRequestDiskPermission = Resources.CanRequestDiskAccessPermission();
        }

        public override void ResolutionChanged()
        {
            uiScale = Owner.UiScale;
            // The rail stays readable on a small window but must never eat the whole screen
            sidebarWidth = Math.Clamp(310 * uiScale, 200, Owner.UiScale * 1280 * 0.3f);

            ModeButtonWidth = (int)(350 * uiScale);
            ModeButtonHeight = (int)(200 * uiScale);

            base.ResolutionChanged();
        }

        public override void Draw()
        {
            if (!Gui.BeginWindow("Main"))
                return;

            var winSize = ImGui.GetWindowSize();

            // The rail sits on the window background, a darker panel of its own with a thin
            // line marking where the content region starts
            ImGui.GetBackgroundDrawList().AddRectFilled(
                Vector2.Zero, new Vector2(sidebarWidth, winSize.Y), Theme.Packed(Theme.Sidebar));

            ImGui.GetBackgroundDrawList().AddLine(
                new Vector2(sidebarWidth, 0), new Vector2(sidebarWidth, winSize.Y),
                Theme.Packed(Theme.Separator));

            // Both regions are laid out directly in this window and not in child windows:
            // the mode buttons position themselves with absolute cursor coordinates and
            // would be offset by a child origin
            DrawNavigation();

            contentX = sidebarWidth + 30 * uiScale;
            contentW = winSize.X - contentX - 30 * uiScale;

            ImGui.SetCursorPos(new Vector2(contentX, 26 * uiScale));
            DrawContent();

            // Always draw popups last
            DrawInitErroPopup();

            Gui.EndWindow();
        }

        // Vertical cursor for the navigation rail, everything in it is stacked one item
        // below the previous one
        float navY;

        void NavAdvance(float amount) => navY += amount;

        void DrawNavigation()
        {
            float pad = 20 * uiScale;
            float inner = sidebarWidth - pad * 2;

            navY = 24 * uiScale;

            // Logo
            var logoHeight = (int)(92 * uiScale);
            var logoWidth = (int)(Logo.Width * (logoHeight / (float)Logo.Height));

            ImGui.SetCursorPos(new Vector2((sidebarWidth - logoWidth) / 2, navY));
            ImGui.Image(Logo.Texture, new Vector2(logoWidth, logoHeight));
            NavAdvance(logoHeight + 16 * uiScale);

            // App name and version, the app name is the big one so it reads as a title
            H1();
            var nameSize = ImGui.CalcTextSize(Program.AppName);
            ImGui.SetCursorPos(new Vector2((sidebarWidth - nameSize.X) / 2, navY));
            ImGui.Text(Program.AppName);
            PopFont();
            NavAdvance(nameSize.Y + 10 * uiScale);

            var versionSize = ImGui.CalcTextSize(Heading);
            ImGui.SetCursorPos(new Vector2((sidebarWidth - versionSize.X) / 2, navY));
            ImGui.TextColored(Theme.TextMuted, Heading);
            NavAdvance(versionSize.Y);

            var buildSize = ImGui.CalcTextSize(SecondLine);
            ImGui.SetCursorPos(new Vector2((sidebarWidth - buildSize.X) / 2, navY));
            ImGui.TextColored(Theme.TextMuted, SecondLine);
            NavAdvance(buildSize.Y + 22 * uiScale);

            ImGui.GetBackgroundDrawList().AddLine(
                new Vector2(pad, navY), new Vector2(sidebarWidth - pad, navY),
                Theme.Packed(Theme.AccentDark));

            NavAdvance(18 * uiScale);

            // The current page, listed so the rail reads as a list of sections
            NavItem(Strings.StreamButton, selected: true);

            if (NavItem(Strings.SettingsButton))
                Owner.PushView(new OptionsView(Owner));

            if (Program.IsWindows && NavItem(Strings.DriverInstallButton))
                Owner.PushView(new Platform.Specific.Win.WinDirverInstallView(Owner));

            if (NavItem(Strings.GuideButton))
                SystemUtil.OpenURL("https://github.com/exelix11/SysDVR/wiki");

            if (NavItem(Strings.GithubButton))
                SystemUtil.OpenURL(Program.ProjectUrl);

            // Attribution, this is a modified client and both the original author and the
            // author of the changes should stay visible
            float y = ImGui.GetWindowSize().Y - 92 * uiScale;

            var authorSize = ImGui.CalcTextSize(Strings.ByAuthor);
            ImGui.SetCursorPos(new Vector2((sidebarWidth - authorSize.X) / 2, y));
            ImGui.TextColored(Theme.Accent, Strings.ByAuthor);

            var creditSize = ImGui.CalcTextSize(Strings.BasedOn);
            ImGui.SetCursorPos(new Vector2((sidebarWidth - creditSize.X) / 2, y + authorSize.Y + 4 * uiScale));
            ImGui.TextColored(Theme.TextMuted, Strings.BasedOn);

            var repoSize = ImGui.CalcTextSize("exelix11/SysDVR");
            ImGui.SetCursorPos(new Vector2((sidebarWidth - repoSize.X) / 2, y + authorSize.Y + creditSize.Y + 8 * uiScale));
            ImGui.TextColored(Theme.TextMuted, "exelix11/SysDVR");
        }

        // Full width row in the navigation rail, the selected row takes the accent colour
        bool NavItem(string label, bool selected = false)
        {
            float pad = 20 * uiScale;
            var size = new Vector2(sidebarWidth - pad * 2, 38 * uiScale);

            ImGui.SetCursorPos(new Vector2(pad, navY));

            ImGui.PushStyleColor(ImGuiCol.Text, selected ? Theme.Accent : Theme.Text);
            bool clicked = ImGui.Selectable(label, selected, ImGuiSelectableFlags.None, size);
            ImGui.PopStyleColor();

            NavAdvance(size.Y + 5 * uiScale);

            return clicked;
        }

        void DrawContent()
        {
            bool wifi = false, usb = false;

            var w = contentW;
            var y = ImGui.GetCursorPosY() + 24 * uiScale;

            if (w < ModeButtonWidth * 2.2)
            {
                var center = contentX + w / 2 - ModeButtonWidth / 2;

                ImGui.SetCursorPos(new(center, y));
                wifi = ModeButton(WifiIcon, Strings.NetworkButton, ModeButtonWidth, ModeButtonHeight);

                y += 20 * uiScale + ModeButtonHeight;

                ImGui.SetCursorPos(new(center, y));
                usb = ModeButton(UsbIcon, Strings.USBButton, ModeButtonWidth, ModeButtonHeight);

                y += ModeButtonHeight;
            }
            else
            {
                var center = contentX + w / 2 - (ModeButtonWidth + ModeButtonWidth + 20) / 2;
                ImGui.SetCursorPos(new(center, y));
                wifi = ModeButton(WifiIcon, Strings.NetworkButton, ModeButtonWidth, ModeButtonHeight);

                ImGui.SetCursorPos(new(center + ModeButtonWidth + 20, y));
                usb = ModeButton(UsbIcon, Strings.USBButton, ModeButtonWidth, ModeButtonHeight);

                y += ModeButtonHeight;
            }

            if (usb)
                Owner.PushView(new UsbDevicesView(Owner, BuildOptions()));
            else if (wifi)
                Owner.PushView(new NetworkScanView(Owner, BuildOptions()));

            ImGui.SetCursorPos(new(contentX, y + 30 * uiScale));

            CenterContentText(Strings.ChannelLabel);

            CenterRadioRow(
                (Strings.ChannelVideo, StreamKind.Video),
                (Strings.ChannelAudio, StreamKind.Audio),
                (Strings.ChannelBoth, StreamKind.Both));

            ImGui.NewLine();

            if (!HasDiskPermission)
            {
                ImGui.TextWrapped(Strings.FileAccess);

                if (CanRequestDiskPermission && CenterContentButton(Strings.FileAccessRequestButton))
                {
                    Resources.RequestDiskAccessPermission();
                    UpdateDiskPermissionStatus();
                }

                ImGui.NewLine();
            }
        }

        // The helpers in Gui centre on the whole window, everything here has to centre in the
        // region the navigation rail leaves free instead
        void CenterContentText(string text)
        {
            var size = ImGui.CalcTextSize(text, false, contentW);
            var pos = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(contentX + (contentW - size.X) / 2, pos.Y));
            ImGui.TextWrapped(text);
        }

        bool CenterContentButton(string text)
        {
            var style = ImGui.GetStyle();
            var size = ImGui.CalcTextSize(text) + style.FramePadding * 2;
            var pos = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(contentX + (contentW - size.X) / 2, pos.Y));
            return ImGui.Button(text, size);
        }

        // Width of a single radio, the circle takes a full frame height and the label is
        // placed next to it separated by the inner spacing
        static float RadioWidth(string label) =>
            ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;

        // Radios laid out on one row, centred inside the content region. Measuring the row
        // before drawing it is the only way to centre it when the labels have different widths
        void CenterRadioRow(params (string Label, StreamKind Target)[] radios)
        {
            float row = ImGui.GetStyle().ItemSpacing.X * (radios.Length - 1);

            foreach (var (label, _) in radios)
                row += RadioWidth(label);

            ImGui.SetCursorPosX(contentX + (contentW - row) / 2);

            for (int i = 0; i < radios.Length; i++)
            {
                if (i > 0)
                    ImGui.SameLine();

                ChannelRadio(radios[i].Label, radios[i].Target);
            }
        }

        void DrawInitErroPopup()
        {
			if (initErrorPopup.Begin())
            {
                ImGui.TextWrapped(initError);

                ImGui.NewLine();

                if (ImGui.Button(GeneralStrings.PopupCloseButton))
                    initErrorPopup.RequestClose();

                ImGui.EndPopup();
            }
			else
                Gui.CenterText(DevelopmentBuild);
		}

		void ChannelRadio(string name, StreamKind target)
        {
            if (ImGui.RadioButton(name, StreamMode == target))
                StreamMode = target;
        }

        StreamingOptions BuildOptions() 
        {
            var opt = Program.Options.Streaming.Clone();
            opt.Kind = StreamMode;
            return opt;
        }

		bool ModeButton(Image image, string title, int width, int height)
        {
            float InnerPadding = 15 * uiScale;

            // This is what we're looking for:
            // TITLE TITLE
            //   <image> 

            H2();
            var titleLen = ImGui.CalcTextSize(title);
			PopFont();

            Vector2 imageFrame = new(
                width - InnerPadding * 2,
                image.ScaleHeight(width - InnerPadding * 2));

            var imageSpaceY = height - InnerPadding * 3 - titleLen.Y;

            if (imageFrame.Y > imageSpaceY)
                imageFrame = new(image.ScaleWidth((int)imageSpaceY), imageSpaceY);

            var x = ImGui.GetCursorPosX();
            var y = ImGui.GetCursorPosY();

            var scroll = new Vector2(ImGui.GetScrollX(), ImGui.GetScrollY());

            // Apply scroll only to the bounding box since it uses lower level APIs
            ImRect bodySize = new ImRect();
            bodySize.Min = new Vector2(x, y) - scroll;
            bodySize.Max = new Vector2(x + width, y + height) - scroll;

            var id = ImGui.GetID(title);

            if (!ImGui.ItemAdd(bodySize, id, IntPtr.Zero, ImGuiItemFlags.None))
                return false;

            var btn = ImGui.ButtonBehavior(bodySize, id, out var hovered, out var held, ImGuiButtonFlags.MouseButtonDefault);
            var col = ImGui.GetColorU32((held && hovered) ? ImGuiCol.ButtonActive : hovered ? ImGuiCol.ButtonHovered : ImGuiCol.Button);

            ImGui.RenderNavHighlight(bodySize, id, 0);
            ImGui.RenderFrame(bodySize.Min, bodySize.Max, col, true, 0);

            y += InnerPadding;

            // Title
            ImGui.SetCursorPos(new(x + width / 2 - titleLen.X / 2, y));
            H2();
            ImGui.Text(title);
			PopFont();

            y += InnerPadding + titleLen.Y;

            ImGui.SetCursorPos(new Vector2(x + width / 2 - imageFrame.X / 2, y));
            ImGui.Image(image.Texture, imageFrame);

            return btn;
        }
    }
}
