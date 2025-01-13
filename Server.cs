using System.Reflection;
using LOBBYN.ClassLibrary.Riot;
using LOBBYN.ClassLibrary.DTOs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace LOBBYN.server
{
    internal class SocketClientInfo : AccountInfo
    {
        public string EncryptedPuuid { get; set; }
    }

    internal class AuthInfo : SocketClientInfo
    {
        public int IconId { get; set; }
    }

    internal class SocketBehavior : WebSocketBehavior
    {
        private void Broadcast(string message)
        {
            foreach (KeyValuePair<string, SocketClientInfo> socket in Server.authorizedSockets)
            {
                if (socket.Key == ID) continue;
                else Server.socketServer.WebSocketServices["/LOBBYN"].Sessions.SendTo(message, socket.Key);
            }
        }

        private void SendByAccountInfo(string message, AccountInfo recipient)
        {
            var recipientSocket = Server.authorizedSockets.FirstOrDefault(socket => socket.Value.PlayerName == recipient.PlayerName && socket.Value.PlayerTagline == recipient.PlayerTagline);
            if (recipientSocket.Value != null) Server.socketServer.WebSocketServices["/LOBBYN"].Sessions.SendTo(message, recipientSocket.Key);
            else // check if recipient is registered and if yes, decide if to put it into a "mailbox"
            {
                // TODO
            }
        }

        protected override void OnOpen()
        {
            Console.WriteLine($"Connection {ID} opened.");
            Server.unauthorizedSockets.Add(ID, null);
            Task.Run(async () =>
            {
                await Task.Delay(30 * 1000); // 30 seconds
                if (!Server.authorizedSockets.ContainsKey(ID))
                {
                    Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Timed out");
                }
            });
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (Server.unauthorizedSockets.ContainsKey(ID)) // still unauthorized
            {
                if (e.Data == "Verify")
                {
                    AuthInfo authInfo;

                    if (Server.unauthorizedSockets.TryGetValue(ID, out authInfo!))
                    {
                        HttpResponseMessage summonerResponse = Server.httpClient.GetAsync($"https://{authInfo.PlayerRegion}.api.riotgames.com/lol/summoner/v4/summoners/by-puuid/{authInfo.EncryptedPuuid}").Result;
                        JObject summonerJson = JObject.Parse(summonerResponse.Content.ReadAsStringAsync().Result);
                        int profileIconId = summonerJson["profileIconId"]!.ToObject<int>();

                        if (profileIconId == authInfo.IconId)
                        {
                            Server.authorizedSockets.Add(ID, new SocketClientInfo
                            {
                                PlayerName = authInfo.PlayerName,
                                PlayerTagline = authInfo.PlayerTagline,
                                PlayerRegion = authInfo.PlayerRegion,
                                EncryptedPuuid = authInfo.EncryptedPuuid
                            });
                            Server.unauthorizedSockets.Remove(ID);
                            Send("Verified");
                            Console.WriteLine($"{ID} verified as {authInfo.PlayerName}#{authInfo.PlayerTagline} {authInfo.PlayerRegion}");
                            return;
                        }
                        else
                        {
                            Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Invalid Icon");
                            return;
                        }
                    }
                }
                else if (Server.unauthorizedSockets[ID] == null)
                {
                    AuthInfo authInfo;
                    try
                    {
                        AccountInfo accountInfo = JsonConvert.DeserializeObject<AccountInfo>(e.Data)!;
                        authInfo = new AuthInfo
                        {
                            PlayerName = accountInfo.PlayerName,
                            PlayerTagline = accountInfo.PlayerTagline,
                            PlayerRegion = accountInfo.PlayerRegion
                        };
                    }
                    catch (JsonException)
                    {
                        Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Invalid JSON");
                        return;
                    }

                    HttpResponseMessage accountResponse = Server.httpClient.GetAsync($"https://{Server.continent}.api.riotgames.com/riot/account/v1/accounts/by-riot-id/{authInfo.PlayerName}/{authInfo.PlayerTagline}").Result;

                    if (!accountResponse.IsSuccessStatusCode)
                    {
                        Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Invalid Riot ID");
                        return;
                    }

                    JObject accountJson = JObject.Parse(accountResponse.Content.ReadAsStringAsync().Result);
                    authInfo.EncryptedPuuid = accountJson["puuid"]!.ToString();

                    HttpResponseMessage summonerResponse = Server.httpClient.GetAsync($"https://{authInfo.PlayerRegion}.api.riotgames.com/lol/summoner/v4/summoners/by-puuid/{authInfo.EncryptedPuuid}").Result;

                    if (!summonerResponse.IsSuccessStatusCode)
                    {
                        Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Wrong PlayerRegion");
                        return;
                    }

                    JObject summonerJson = JObject.Parse(summonerResponse.Content.ReadAsStringAsync().Result);
                    int profileIconId = summonerJson["profileIconId"]!.ToObject<int>();

                    authInfo.IconId = profileIconId;

                    Random random = new Random();
                    while (authInfo.IconId == profileIconId)
                    {
                        authInfo.IconId = random.Next(0, 30);
                    }

                    Server.unauthorizedSockets[ID] = authInfo;
                    Send(Server.unauthorizedSockets[ID]!.IconId.ToString());
                    Console.WriteLine($"{ID} introduced themselves as {authInfo.PlayerName}#{authInfo.PlayerTagline} {authInfo.PlayerRegion}");
                    return;
                }
            }

            // close connection if not verified
            if (!Server.authorizedSockets.ContainsKey(ID))
            {
                Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Unauthorized");
                return;
            }

            // handle messages
            ClientToServerMessage receivedMessage;
            try
            {
                receivedMessage = JsonConvert.DeserializeObject<ClientToServerMessage>(e.Data)!;
            }
            catch (JsonException)
            {
                Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Invalid JSON");
                return;
            }

            ServerToClientMessage outgoingMessage = new ServerToClientMessage
            {
                SenderName = Server.authorizedSockets[ID].PlayerName,
                SenderTagling = Server.authorizedSockets[ID].PlayerTagline,
                Type = receivedMessage.MessageType,
                Data = receivedMessage.Data
            };

            string outgoingMessageString = JsonConvert.SerializeObject(outgoingMessage);

            switch (receivedMessage.RoutingType)
            {
                case MessageRoutingType.Broadcast:
                    Broadcast(outgoingMessageString);
                    break;

                case MessageRoutingType.Direct:
                    foreach (var recepient in receivedMessage.Recipients)
                    {
                        SendByAccountInfo(outgoingMessageString, recepient);
                    }
                    break;
                default:
                    Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Invalid Message Routing Type");
                    break;
            }
        }

        protected override void OnClose(CloseEventArgs e)
        {
            Console.WriteLine($"Connection {ID} closed - {e.Code} {e.Reason}");

            if (Server.unauthorizedSockets.ContainsKey(ID)) Server.unauthorizedSockets.Remove(ID);
            else if (Server.authorizedSockets.ContainsKey(ID)) Server.authorizedSockets.Remove(ID);
        }
    }
    public static class Server
    {
        internal static readonly HttpClient httpClient = new HttpClient();
        internal static Dictionary<string, AuthInfo?> unauthorizedSockets = new Dictionary<string, AuthInfo?>();
        internal static Dictionary<string, SocketClientInfo> authorizedSockets = new Dictionary<string, SocketClientInfo>();
        internal static WebSocketServer socketServer;
        internal static int port;
        internal static string continent;
        public static bool Running => socketServer.IsListening;

        static Server()
        {
            port = Convert.ToInt32(Program.Config["SERVER"]["PORT"]);
            continent = Program.Config["RIOT"]["CONTINENT"];
            socketServer = new WebSocketServer($"ws://127.0.0.1:{port}");
            socketServer.AddWebSocketService<SocketBehavior>("/LOBBYN");
            httpClient.DefaultRequestHeaders.Add("X-Riot-Token", Program.Config["RIOT"]["API_KEY"]);
        }

        public static void Start()
        {
            socketServer.Start();
            Console.WriteLine($"Server started on port {port}");
        }

        public static void Stop()
        {
            socketServer.Stop();
            Console.WriteLine("Server stopped");
        }
    }
}
