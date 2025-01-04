using WebSocketSharp;
using WebSocketSharp.Server;
using LOBBYN.server.DTOs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;

namespace LOBBYN.server
{
    internal class SocketClientInfo
    {
        public string PlayerName { get; set; }
        public string PlayerTagline { get; set; }
        public PlayerRegion PlayerRegion { get; set; }
        public string EncryptedPuuid { get; set; }
    }

    internal class AuthInfo : SocketClientInfo
    {
        public int IconId { get; set; }
    }

    internal class SocketBehavior : WebSocketBehavior
    {
        private const string HEADER_PLAYERNAME = "LOBBYN-PlayerName";
        private const string HEADER_PLAYERTAGLINE = "LOBBYN-PlayerTagline";
        private const string HEADER_PLAYERREGION = "LOBBYN-PlayerRegion";
        private const string HEADER_WEBSOCKET_KEY = "Sec-WebSocket-Key";
        protected override void OnOpen()
        {
            Console.WriteLine($"Connection {Context.Headers[HEADER_WEBSOCKET_KEY]} opened - {Context.Headers[HEADER_PLAYERNAME]}#{Context.Headers[HEADER_PLAYERTAGLINE]} {Context.Headers[HEADER_PLAYERREGION]}");

            if (!Context.Headers.Contains(HEADER_PLAYERNAME) || !Context.Headers.Contains(HEADER_PLAYERTAGLINE) || !Context.Headers.Contains(HEADER_PLAYERREGION))
            {
                Context.WebSocket.Close(CloseStatusCode.InvalidData, $"Make sure headers {HEADER_PLAYERNAME}, {HEADER_PLAYERTAGLINE} and {HEADER_PLAYERREGION} are included");
                return;
            }

            if (!Enum.TryParse<PlayerRegion>(Context.Headers[HEADER_PLAYERREGION]!, out _))
            {
                Context.WebSocket.Close(CloseStatusCode.InvalidData, "Invalid PlayerRegion");
                return;
            }

            string websocketKey = Context.Headers[HEADER_WEBSOCKET_KEY]!;
            AuthInfo authInfo = new AuthInfo
            {
                PlayerName = Context.Headers[HEADER_PLAYERNAME]!,
                PlayerTagline = Context.Headers[HEADER_PLAYERTAGLINE]!,
                PlayerRegion = Enum.Parse<PlayerRegion>(Context.Headers[HEADER_PLAYERREGION]!)
            };

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

            Server.authIcons.Add(websocketKey, authInfo);
            Send(Server.authIcons[websocketKey].IconId.ToString());
            Task.Run(async () =>
            {
                await Task.Delay(30*1000); // 30 seconds
                if (Server.authIcons.ContainsKey(websocketKey))
                {
                    Server.authIcons.Remove(websocketKey);
                    Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Timed out");
                }
            });
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            string websocketKey = Context.Headers[HEADER_WEBSOCKET_KEY]!;
            if (e.Data == "Verify")
            {
                AuthInfo authInfo;

                if (Server.authIcons.TryGetValue(websocketKey, out authInfo!))
                {
                    HttpResponseMessage summonerResponse = Server.httpClient.GetAsync($"https://{authInfo.PlayerRegion}.api.riotgames.com/lol/summoner/v4/summoners/by-puuid/{authInfo.EncryptedPuuid}").Result;
                    JObject summonerJson = JObject.Parse(summonerResponse.Content.ReadAsStringAsync().Result);
                    int profileIconId = summonerJson["profileIconId"]!.ToObject<int>();

                    if (profileIconId == authInfo.IconId)
                    {
                        Server.validSockets.Add(websocketKey, authInfo);
                        Server.authIcons.Remove(websocketKey);
                        Send("Verified");
                        return;
                    }
                    else
                    {
                        Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Invalid Icon");
                        return;
                    }
                }

                if (!Server.validSockets.ContainsKey(websocketKey))
                {
                    Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Unauthorized");
                    return;
                }
            }
        }

        protected override void OnClose(CloseEventArgs e)
        {
            Console.WriteLine($"Connection {Context.Headers[HEADER_WEBSOCKET_KEY]} closed - {e.Code} {e.Reason}");
        }
    }
    public static class Server
    {
        internal const string RIOT_CONTINENT = "europe";
        internal static readonly HttpClient httpClient = new HttpClient();
        internal static Dictionary<string, AuthInfo> authIcons = new Dictionary<string, AuthInfo>();
        internal static Dictionary<string, SocketClientInfo> validSockets = new Dictionary<string, SocketClientInfo>();
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
