using ImGuiNET;
using System.Numerics;

namespace SysDVR.Client.GUI
{
    /// <summary>
    /// The look of SysDVR+.
    ///
    /// Everything is a dark blue slate with a single cyan accent. The accent is used only
    /// where the user needs to find something quickly, selected items and the currently
    /// active state, so it keeps its meaning instead of becoming decoration.
    ///
    /// This must be applied right after the ImGui context is created and before the style
    /// gets backed up, the app restores the backup on every window resize and then rescales
    /// it for the new DPI, so whatever is backed up is what the user sees all session.
    /// </summary>
    internal static class Theme
    {
        // Cyan accent, the one colour that marks whatever is selected or active
        public static readonly Vector4 Accent = Rgb(34, 211, 238);
        // The same accent darkened enough to be a background under light text
        public static readonly Vector4 AccentDark = Rgb(14, 116, 144);
        // Accent tinted hover state for controls sitting on the dark background
        public static readonly Vector4 AccentHover = Rgb(29, 78, 99);

        // Reused by the views for text they draw themselves
        public static readonly Vector4 Text = Rgb(232, 238, 247);
        public static readonly Vector4 TextMuted = Rgb(124, 139, 161);

        // Background of the navigation rail, a clearly separate shade from the content
        // behind it so the two regions read as different areas at a glance
        public static readonly Vector4 Sidebar = Rgb(11, 14, 20);
        public static readonly Vector4 Separator = Rgb(41, 50, 68, 200);

        public static Vector4 Rgb(int r, int g, int b, int a = 255) =>
            new(r / 255f, g / 255f, b / 255f, a / 255f);

        // ImGui draw list APIs take colours packed as ABGR, the style takes Vector4
        public static uint Packed(Vector4 c) =>
            ((uint)(c.W * 255f) << 24) | ((uint)(c.Z * 255f) << 16) |
            ((uint)(c.Y * 255f) << 8) | (uint)(c.X * 255f);

        public static void Apply(ImGuiStylePtr style)
        {
            var colors = style.Colors;

            // Text
            colors[(int)ImGuiCol.Text] = Rgb(232, 238, 247);
            colors[(int)ImGuiCol.TextDisabled] = Rgb(124, 139, 161);

            // Backgrounds, from the furthest back to the most raised
            colors[(int)ImGuiCol.WindowBg] = Rgb(18, 22, 31, 238);
            colors[(int)ImGuiCol.ChildBg] = Rgb(21, 26, 37);
            colors[(int)ImGuiCol.PopupBg] = Rgb(23, 29, 41, 245);
            colors[(int)ImGuiCol.TitleBg] = Rgb(15, 19, 27);
            colors[(int)ImGuiCol.TitleBgActive] = Rgb(20, 27, 39);
            colors[(int)ImGuiCol.TitleBgCollapsed] = Rgb(15, 19, 27, 180);
            colors[(int)ImGuiCol.MenuBarBg] = Rgb(18, 22, 31);

            // Borders
            colors[(int)ImGuiCol.Border] = Rgb(41, 50, 68, 160);
            colors[(int)ImGuiCol.BorderShadow] = Vector4.Zero;

            // Text fields, sliders and anything else with a sunken frame
            colors[(int)ImGuiCol.FrameBg] = Rgb(27, 34, 48);
            colors[(int)ImGuiCol.FrameBgHovered] = Rgb(34, 48, 73);
            colors[(int)ImGuiCol.FrameBgActive] = Rgb(39, 64, 94);

            // Scrollbar
            colors[(int)ImGuiCol.ScrollbarBg] = Rgb(18, 22, 31, 180);
            colors[(int)ImGuiCol.ScrollbarGrab] = Rgb(44, 53, 72);
            colors[(int)ImGuiCol.ScrollbarGrabHovered] = Rgb(58, 70, 95);
            colors[(int)ImGuiCol.ScrollbarGrabActive] = AccentDark;

            // Selection marks and slider handles
            colors[(int)ImGuiCol.CheckMark] = Accent;
            colors[(int)ImGuiCol.SliderGrab] = Rgb(43, 179, 214);
            colors[(int)ImGuiCol.SliderGrabActive] = Accent;

            // Buttons, these carry most of the navigation
            colors[(int)ImGuiCol.Button] = Rgb(30, 42, 59);
            colors[(int)ImGuiCol.ButtonHovered] = AccentHover;
            colors[(int)ImGuiCol.ButtonActive] = AccentDark;

            // Selection headers, also used by the selectable list items
            colors[(int)ImGuiCol.Header] = Rgb(26, 58, 74);
            colors[(int)ImGuiCol.HeaderHovered] = Rgb(31, 90, 114);
            colors[(int)ImGuiCol.HeaderActive] = AccentDark;

            // Separators and resize grips
            colors[(int)ImGuiCol.Separator] = Rgb(38, 49, 63);
            colors[(int)ImGuiCol.SeparatorHovered] = Accent;
            colors[(int)ImGuiCol.SeparatorActive] = Accent;
            colors[(int)ImGuiCol.ResizeGrip] = Rgb(34, 211, 238, 64);
            colors[(int)ImGuiCol.ResizeGripHovered] = Rgb(34, 211, 238, 112);
            colors[(int)ImGuiCol.ResizeGripActive] = Accent;

            // Tabs
            colors[(int)ImGuiCol.Tab] = Rgb(24, 32, 44);
            colors[(int)ImGuiCol.TabHovered] = AccentHover;
            colors[(int)ImGuiCol.TabActive] = AccentDark;
            colors[(int)ImGuiCol.TabUnfocused] = Rgb(20, 26, 36);
            colors[(int)ImGuiCol.TabUnfocusedActive] = Rgb(27, 35, 48);

            // Plots and tables
            colors[(int)ImGuiCol.PlotLines] = Accent;
            colors[(int)ImGuiCol.PlotHistogram] = Accent;
            colors[(int)ImGuiCol.PlotHistogramHovered] = Rgb(150, 240, 250);
            colors[(int)ImGuiCol.TableHeaderBg] = Rgb(22, 29, 41);
            colors[(int)ImGuiCol.TableBorderStrong] = Rgb(41, 50, 68);
            colors[(int)ImGuiCol.TableBorderLight] = Rgb(34, 42, 56);
            colors[(int)ImGuiCol.TableRowBg] = Vector4.Zero;
            colors[(int)ImGuiCol.TableRowBgAlt] = Rgb(255, 255, 255, 6);

            // Miscellaneous
            colors[(int)ImGuiCol.TextSelectedBg] = Rgb(34, 211, 238, 90);
            colors[(int)ImGuiCol.DragDropTarget] = Accent;
            colors[(int)ImGuiCol.NavHighlight] = Accent;
            colors[(int)ImGuiCol.NavWindowingHighlight] = Rgb(255, 255, 255, 180);
            colors[(int)ImGuiCol.NavWindowingDimBg] = Rgb(2, 8, 16, 150);
            colors[(int)ImGuiCol.ModalWindowDimBg] = Rgb(5, 10, 20, 160);

            // Sizes, the app rescales all of these for the window size and the DPI
            style.WindowRounding = 10;
            style.ChildRounding = 8;
            style.FrameRounding = 6;
            style.PopupRounding = 8;
            style.ScrollbarRounding = 8;
            style.GrabRounding = 6;
            style.TabRounding = 6;

            style.WindowBorderSize = 1;
            style.ChildBorderSize = 1;
            style.PopupBorderSize = 1;
            style.FrameBorderSize = 0;
            style.TabBorderSize = 0;

            style.WindowPadding = new(16, 14);
            style.FramePadding = new(12, 9);
            style.ItemSpacing = new(12, 9);
            style.ItemInnerSpacing = new(8, 6);
            style.CellPadding = new(8, 6);
            style.ScrollbarSize = 15;
            style.WindowMinSize = new(80, 60);
            style.WindowTitleAlign = new(0.5f, 0.5f);
        }
    }
}
