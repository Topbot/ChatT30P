using System;
using System.ServiceProcess;

namespace ChatService
{
    internal static class Program
    {
        private static void Main()
        {
#if DEBUG
            // Run as console in Debug for easier local testing.
            var svc = new RpaTaskPollerService();
            svc.DebugStart();
            Console.WriteLine("ChatService running. Press ENTER to stop...");
            Console.ReadLine();
            svc.DebugStop();
#else
            ServiceBase.Run(new ServiceBase[] { new RpaTaskPollerService() });
#endif
        }
    }
}
