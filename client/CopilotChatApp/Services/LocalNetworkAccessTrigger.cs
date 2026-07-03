using System.Net;
using System.Net.Sockets;

namespace CopilotChatApp.Services;

/// <summary>
/// On iOS/iPadOS, connecting to any device on the local network (including our own WebSocket
/// server) requires the user to grant a one-time "Allow this app to find devices on your local
/// network?" permission (NSLocalNetworkUsageDescription). There's no public API to request that
/// prompt directly or to await the user's answer - it's normally triggered lazily by whatever
/// happens to be the app's first local-network socket.
///
/// Using the actual chat WebSocket connection as that trigger is fragile: the connection attempt
/// fails outright while the prompt is up, and by the time the user notices and taps Allow, a
/// bounded retry loop (see ChatClientService.ConnectWithRetryAsync) may already have given up and
/// shown a scary error (see PBI note on session-list flakiness on first launch).
///
/// This fires a harmless UDP broadcast packet as early as possible (app startup, before any page
/// is shown) purely to surface that permission prompt ahead of time, decoupled from the real
/// connection attempt - the well-known community workaround for triggering
/// NSLocalNetworkUsageDescription proactively. No response is expected or read; this is fire-and-forget.
/// </summary>
public static class LocalNetworkAccessTrigger
{
    public static void FireAndForget()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var udp = new UdpClient();
                udp.EnableBroadcast = true;
                var payload = new byte[] { 0 };
                // Port is arbitrary and nothing needs to be listening - only the OS-level "app tried
                // to reach the local network" signal that this send triggers actually matters here.
                await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, 65530));
            }
            catch
            {
                // Best-effort only. If this fails (e.g. no network yet), the normal
                // connect-with-retry path when the user actually opens the app still runs.
            }
        });
    }
}
