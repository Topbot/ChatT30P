using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;
using System.Web.Http;
using System.Web.Http.ExceptionHandling;

namespace ChatT30P.Infrastructure
{
    public class ApiExceptionHandler : ExceptionHandler
    {
        public override void Handle(ExceptionHandlerContext context)
        {
            try
            {
                var baseDir = HttpContext.Current?.Server?.MapPath("~/App_Data/logs") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "logs");
                Directory.CreateDirectory(baseDir);
                var path = Path.Combine(baseDir, "api-unhandled-errors.log");
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + context.Exception + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }

            context.Result = new TextPlainErrorResult(
                context.Request,
                HttpStatusCode.InternalServerError,
                "An error has occurred.\n\n" + context.Exception);
        }

        private class TextPlainErrorResult : IHttpActionResult
        {
            private readonly HttpRequestMessage _request;
            private readonly HttpStatusCode _status;
            private readonly string _text;

            public TextPlainErrorResult(HttpRequestMessage request, HttpStatusCode status, string text)
            {
                _request = request;
                _status = status;
                _text = text;
            }

            public System.Threading.Tasks.Task<HttpResponseMessage> ExecuteAsync(System.Threading.CancellationToken cancellationToken)
            {
                var response = _request.CreateResponse(_status);
                response.Content = new StringContent(_text, Encoding.UTF8, "text/plain");
                return System.Threading.Tasks.Task.FromResult(response);
            }
        }
    }
}
