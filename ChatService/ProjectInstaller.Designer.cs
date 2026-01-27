using System.ComponentModel;
using System.ServiceProcess;

namespace ChatService
{
    partial class ProjectInstaller
    {
        private ServiceProcessInstaller serviceProcessInstaller1;
        private ServiceInstaller serviceInstaller1;

        private void InitializeComponent()
        {
            this.serviceProcessInstaller1 = new ServiceProcessInstaller();
            this.serviceInstaller1 = new ServiceInstaller();

            this.serviceProcessInstaller1.Account = ServiceAccount.LocalSystem;

            this.serviceInstaller1.ServiceName = "ChatService";
            this.serviceInstaller1.DisplayName = "ChatService";
            this.serviceInstaller1.StartType = ServiceStartMode.Automatic;

            this.Installers.AddRange(new Installer[] {
                this.serviceProcessInstaller1,
                this.serviceInstaller1
            });
        }
    }
}
