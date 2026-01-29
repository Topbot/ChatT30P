using ASP.App_Start;
using ChatT30P.Controllers.Api;
using Unity;
using Unity.Lifetime;

/// <summary>
/// Summary description for UnityConfig
/// </summary>
public class UnityConfig
{
    public UnityConfig() { }

    public static void Register(System.Web.Http.HttpConfiguration config)
    {
        var unity = new UnityContainer();

        unity.RegisterType<ChatAccountsController>();

        config.DependencyResolver = new IoCContainer(unity);
    }
}
