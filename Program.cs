namespace LOBBYN.server
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Server.Start();
            Console.ReadKey();
            Server.Stop();
        }
    }
}
