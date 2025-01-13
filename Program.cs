using System.Diagnostics;
using Salaros.Configuration;
using WebSocketSharp;
using LOBBYN.ClassLibrary.Riot;

namespace LOBBYN.server
{
    internal class Program
    {
        static public ConfigParser Config;
        static public string ConfigFilePath;

        static void Main(string[] args)
        {
            EnsureConfigFilled();
            Server.Start();
            Console.ReadKey();
            Server.Stop();
        }

        static void EnsureConfigFilled()
        {
            string appDataFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dataFolderPath = Path.Combine(appDataFolderPath, "LOBBYN", "Server");
            ConfigFilePath = Path.Combine(dataFolderPath, "config.ini");
            if (!Directory.Exists(dataFolderPath))
            {
                Directory.CreateDirectory(dataFolderPath);
                if (!File.Exists(ConfigFilePath))
                {
                    File.WriteAllText(ConfigFilePath, // default config
@"[SERVER]
PORT=8080

[RIOT]
API_KEY=YOUR_API_KEY
CONTINENT=EUROPE
");
                }
                Console.Write($"Looks like it's the first launch.\nPlease navigate to {ConfigFilePath} and fill in the config values.\nAfter doing so, press any key to validate them...");
                Process.Start("notepad.exe", ConfigFilePath);
                Console.ReadKey();
                Console.Clear();
            }
            
            Config = new ConfigParser(ConfigFilePath);

            bool valid = true;

            if (Config["SERVER"]["PORT"].IsNullOrEmpty())
            {
                valid = false;
                Console.WriteLine("[SERVER] PORT is required.");
            }
            else if (!int.TryParse(Config["SERVER"]["PORT"], out _))
            {
                valid = false;
                Console.WriteLine("[SERVER] PORT has to be a number.");
            }

            if (Config["RIOT"]["API_KEY"].Length != 42)
            {
                valid = false;
                Console.WriteLine("[RIOT] API_KEY has to be 42 characters long.");
            }

            // check if API KEY is not expired
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Add("X-Riot-Token", Config["RIOT"]["API_KEY"]);
            HttpResponseMessage response = client.GetAsync("https://euw1.api.riotgames.com/lol/status/v4/platform-data").Result;
            if (!response.IsSuccessStatusCode)
            {
                valid = false;
                Console.WriteLine("[RIOT] API_KEY is invalid.");
            }

            if (!Enum.TryParse<Continent>(Config["RIOT"]["CONTINENT"], out _))
            {
                valid = false;
                Console.Write("[RIOT] CONTINENT has to be one of the following: ");
                foreach (Continent continent in Enum.GetValues(typeof(Continent)))
                {
                    Console.Write($"{continent.ToString()} ");
                }
                Console.WriteLine();
            }

            if (!valid)
            {
                Process.Start("notepad.exe", ConfigFilePath);
                throw new ConfigParserException("Invalid config fields.");
            }
        }
    }
}
