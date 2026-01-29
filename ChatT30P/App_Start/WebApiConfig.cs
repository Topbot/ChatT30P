using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Extensions.Compression.Core.Compressors;
using System.Web.Http;
using Microsoft.AspNet.WebApi.Extensions.Compression.Server;
using Newtonsoft.Json;
using System.Web.Http.ExceptionHandling;

namespace ChatT30P
{
    public static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            // Web API configuration and services

            config.Services.Replace(typeof(IExceptionHandler), new ChatT30P.Infrastructure.ApiExceptionHandler());

            config.MapHttpAttributeRoutes();

            // Web API routes

            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{id}",
                defaults: new { id = RouteParameter.Optional }
            );

            // Note: avoid adding a generic route that includes {action} because it can
            // conflict with attribute routing and cause "Multiple actions were found"
            // errors when there are several POST methods on the same controller.
            // Attribute routing (MapHttpAttributeRoutes) is used for action-level routes.


            GlobalConfiguration.Configuration.MessageHandlers.Insert(0, new ServerCompressionHandler(new GZipCompressor(), new DeflateCompressor()));

            var appXmlType = config.Formatters.XmlFormatter.SupportedMediaTypes.FirstOrDefault(t => t.MediaType == "application/xml");
            config.Formatters.XmlFormatter.SupportedMediaTypes.Remove(appXmlType);
            config.Formatters.JsonFormatter.SerializerSettings = new JsonSerializerSettings();
        }
    }
}

