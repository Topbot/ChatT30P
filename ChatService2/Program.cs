using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WTelegram;

namespace ChatService2
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            Helpers.Log = (level, message) => { };
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<ChatSyncWorkerService>();
                })
                .Build()
                .Run();
        }
    }
}
