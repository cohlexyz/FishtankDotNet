using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KfChatDotNetWsClient;

namespace KfChatDotNetBot.Services;

public static class FishtankForwarder
{
    public static async void Start(ChatBot SneedChat)
    {
        int listenPort = 8087;

        using (UdpClient listener = new UdpClient(listenPort))
        {
            IPEndPoint groupEP = new IPEndPoint(IPAddress.Loopback, listenPort);

            Console.WriteLine($"Listening for UDP data on port {listenPort}...");

            while (true)
            {
                byte[] bytes = listener.Receive(ref groupEP);
                string receivedData = Encoding.UTF8.GetString(bytes);
                try
                {


                    var msg = JsonSerializer.Deserialize<KfChatDotNetBot.Models.UDPMessage>(receivedData);

                    if (msg != null)
                    {

                        _ = msg.HandleMessage(SneedChat).ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                Console.WriteLine($"Error handling message: {t.Exception?.GetBaseException().Message}");

                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message} // {receivedData}");
                }
            }
        }
    }
}