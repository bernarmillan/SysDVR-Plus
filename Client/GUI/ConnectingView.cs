using ImGuiNET;
using SysDVR.Client.App;
using SysDVR.Client.Core;
using SysDVR.Client.Sources;
using SysDVR.Client.Targets.Player;
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace SysDVR.Client.GUI
{
    internal class ConnectingView : View
    {
        readonly StringTable.ConnectionTable Strings = Program.Strings.Connection;

        readonly DeviceInfo info;
        readonly DeviceConnector conn;
        readonly int reconnectAttempt;

        CancellationTokenSource? src = new();
        
        bool connected;
        bool isError;
        bool destroyed;
        string? errorLine;

        public ConnectingView(ClientApp owner, DeviceInfo info, StreamingOptions opt, int reconnectAttempt = 0) : base(owner)
        {
            this.info = info;
            this.reconnectAttempt = reconnectAttempt;
         
            conn = new DeviceConnector(info, src, opt);
            conn.OnMessage += Conn_OnMessage;
        }

        private void Conn_OnMessage(string obj)
        {
            errorLine ??= "";
            errorLine += obj + "\n";
            Owner.KickRendering(true);
        }

        public override void BackPressed()
        {
            // If we're connected, the view will be replaced in a few instants
            if (src is null && !isError)
                return;

            base.BackPressed();
        }

        public override async void Created()
        {
            StreamManager manager;
            try
            {
                manager = await conn.ConnectForPlayer();
            }
            catch (Exception e)
            {
                if (src!.IsCancellationRequested)
                    return;

                Console.WriteLine($"Player connection failed: {e}");
                Conn_OnMessage(e.ToString());

                // Auto reconnect: back off a bit and try again from a fresh view
                if (Program.Options.AutoReconnect && reconnectAttempt < Options.AutoReconnectMaxAttempts)
                {
                    RetryConnection();
                    return;
                }

                isError = true;
                return;
            }
            finally 
            {
                // We don't need the token anymore.
                // if the connection failed it's not needed anymore
                // otherwise it's now owned by the player
                src = null;
                conn.OnMessage -= Conn_OnMessage;
            }

            // This must execute on the main thread
            Owner.PostAction(() =>
            {
                try
                {
                    Owner.ReplaceView(new PlayerView(Owner, (PlayerManager)manager, info, reconnectAttempt));
                    connected = true;
                }
                catch (Exception e)
                {
                    Console.WriteLine("Player creation failed");
                    Conn_OnMessage(e.ToString());
                    isError = true;
                }
            });
        }

        // The console may be slow to come back or turned off, wait before trying again
        async void RetryConnection()
        {
            await Task.Delay(2000);

            Owner.PostAction(() =>
            {
                if (connected || destroyed)
                    return;

                try
                {
                    Owner.ReplaceView(new ConnectingView(Owner, info, Program.Options.Streaming, reconnectAttempt + 1));
                }
                catch (Exception ex)
                {
                    // A view change was already scheduled, for example the user quit
                    Console.WriteLine($"Retry failed to start: {ex.Message}");
                }
            });
        }

        public override void Destroy()
        {
            destroyed = true;

            if (!connected)
            {
                conn.OnMessage -= Conn_OnMessage;
                // If we are cdonnected the cancellation token is passed to the player and we don't own it anymore
                src?.Cancel();
            }
            
            base.Destroy();
        }

        public override void Draw()
        {
            if (!Gui.BeginWindow("Connecting"))
                return;

            ImGui.NewLine();

            H2();

            var title = isError ? Strings.Error : Strings.Title;
			Gui.CenterText(title);
			
            PopFont();

            Gui.CenterText(info.ToString());

            if (reconnectAttempt > 0)
            {
                ImGui.NewLine();
                Gui.CenterText(string.Format(Strings.ReconnectingAttempt, reconnectAttempt, Options.AutoReconnectMaxAttempts));
            }
            
            ImGui.NewLine();

            var btnSize = new Vector2(ImGui.GetWindowSize().X * 5 / 6, Gui.ButtonHeight());
            if (errorLine != null)
                ImGui.TextWrapped(errorLine);

            Gui.CursorFromBottom(btnSize.Y);

            var text = isError ? GeneralStrings.BackButton : GeneralStrings.CancelButton;
			if (Gui.CenterButton(text, btnSize))
                BackPressed();

            Gui.EndWindow();
        }
    }
}
