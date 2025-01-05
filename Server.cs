using LOBBYN.server.DTOs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace LOBBYN.server
{
    internal class AccountInfo
    {
        public string PlayerName { get; set; }
        public string PlayerTagline { get; set; }
        public PlayerRegion PlayerRegion { get; set; }
    }

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
        private const string HEADER_WEBSOCKET_KEY = "Sec-WebSocket-Key";

        protected override void OnOpen()
        {
            string websocketKey = Context.Headers[HEADER_WEBSOCKET_KEY]!;
            Console.WriteLine($"Connection {websocketKey} opened.");
            Server.unauthorizedSockets.Add(websocketKey, null);
            Task.Run(async () =>
            {
                await Task.Delay(30 * 1000); // 30 seconds
                if (!Server.unauthorizedSockets.ContainsKey(websocketKey))
                {
                    Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Timed out");
                }
            });
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            string websocketKey = Context.Headers[HEADER_WEBSOCKET_KEY]!;

            if (Server.unauthorizedSockets.ContainsKey(websocketKey)) // still unauthorized
            {
                if (e.Data == "Verify")
                {
                    AuthInfo authInfo;

                    if (Server.unauthorizedSockets.TryGetValue(websocketKey, out authInfo!))
                    {
                        HttpResponseMessage summonerResponse = Server.httpClient.GetAsync($"https://{authInfo.PlayerRegion}.api.riotgames.com/lol/summoner/v4/summoners/by-puuid/{authInfo.EncryptedPuuid}").Result;
                        JObject summonerJson = JObject.Parse(summonerResponse.Content.ReadAsStringAsync().Result);
                        int profileIconId = summonerJson["profileIconId"]!.ToObject<int>();

                        if (profileIconId == authInfo.IconId)
                        {
                            Server.authorizedSockets.Add(websocketKey, new SocketClientInfo
                            {
                                PlayerName = authInfo.PlayerName,
                                PlayerTagline = authInfo.PlayerTagline,
                                PlayerRegion = authInfo.PlayerRegion,
                                EncryptedPuuid = authInfo.EncryptedPuuid
                            });
                            Server.unauthorizedSockets.Remove(websocketKey);
                            Send("Verified");
                            Console.WriteLine($"{websocketKey} verified as {authInfo.PlayerName}#{authInfo.PlayerTagline} {authInfo.PlayerRegion}");
                            return;
                        }
                        else
                        {
                            Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Invalid Icon");
                            return;
                        }
                    }
                }
                else if (Server.unauthorizedSockets[websocketKey] == null)
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

                    HttpResponseMessage accountResponse = Server.httpClient.GetAsync($"https://{Server.RIOT_CONTINENT}.api.riotgames.com/riot/account/v1/accounts/by-riot-id/{authInfo.PlayerName}/{authInfo.PlayerTagline}").Result;

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

                    Server.unauthorizedSockets[websocketKey] = authInfo;
                    Send(Server.unauthorizedSockets[websocketKey]!.IconId.ToString());
                    Console.WriteLine($"{websocketKey} introduced themselves as {authInfo.PlayerName}#{authInfo.PlayerTagline} {authInfo.PlayerRegion}");
                    return;
                }
            }

            // close connection if not verified
            if (!Server.authorizedSockets.ContainsKey(websocketKey))
            {
                Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Unauthorized");
                return;
            }
        }

        protected override void OnClose(CloseEventArgs e)
        {
            string websocketKey = Context.Headers[HEADER_WEBSOCKET_KEY]!;
            Console.WriteLine($"Connection {websocketKey} closed - {e.Code} {e.Reason}");

            if (Server.unauthorizedSockets.ContainsKey(websocketKey)) Server.unauthorizedSockets.Remove(websocketKey);
            else if (Server.authorizedSockets.ContainsKey(websocketKey)) Server.authorizedSockets.Remove(websocketKey);
        }
    }
    public static class Server
    {
        internal const string RIOT_CONTINENT = "europe";
        internal static readonly HttpClient httpClient = new HttpClient();
        internal static Dictionary<string, AuthInfo?> unauthorizedSockets = new Dictionary<string, AuthInfo?>();
        internal static Dictionary<string, SocketClientInfo> authorizedSockets = new Dictionary<string, SocketClientInfo>();
        private static WebSocketServer socketServer = new WebSocketServer($"ws://127.0.0.1:{PORT}");
        private const int PORT = 8080;
        public static bool Running => socketServer.IsListening;

        static Server()
        {
            socketServer.AddWebSocketService<SocketBehavior>("/LOBBYN");
            httpClient.DefaultRequestHeaders.Add("X-Riot-Token", File.ReadAllText("RIOT_API_KEY"));
        }

        public static void Start()
        {
            socketServer.Start();
            Console.WriteLine($"Server started on port {PORT}");
        }

        public static void Stop()
        {
            socketServer.Stop();
            Console.WriteLine("Server stopped");
        }

        internal static JObject RiotApiRequest(string url)
        {
            HttpResponseMessage response = Server.httpClient.GetAsync(url).Result;
            return JObject.Parse(response.Content.ReadAsStringAsync().Result);
        }
    }
}
