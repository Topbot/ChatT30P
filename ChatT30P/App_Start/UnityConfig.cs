using ASP.App_Start;
using ChatT30P.Contracts;
using ChatT30P.Controllers.Data;
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

        unity.RegisterType<YoutubeController>();

        unity.RegisterType<IYoutubeRepository, YoutubeRepository>(new HierarchicalLifetimeManager());

        config.DependencyResolver = new IoCContainer(unity);
    }
}
