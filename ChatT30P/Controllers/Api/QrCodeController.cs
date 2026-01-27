using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using Core;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ChatT30P.Controllers.Api
{
    public class QrCodeController : ApiController
    {
        private static readonly string ScreenshotDir = @"C:\.ADSPOWER_GLOBAL\RPA\screenshot";
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

        // Координаты QR на скриншоте Telegram Web QR login.
        // Если размер скриншота отличается, нужно подстроить значения.
        private static readonly Rectangle QrCropRect = new Rectangle(x: 353, y: 80, width: 310, height: 310);
        private const int QrOutSize = 300;

        [HttpGet]
        [Route("api/QrCode")]
        public HttpResponseMessage Get([FromUri] string adsPowerId)
        {
            if (!Security.IsAuthenticated)
                return Request.CreateResponse(HttpStatusCode.Unauthorized);

            if (!Security.IsPaid)
                return Request.CreateResponse((HttpStatusCode)402, "No active subscription");

            if (string.IsNullOrWhiteSpace(adsPowerId))
                return Request.CreateResponse(HttpStatusCode.BadRequest);

            try
            {
                var path = Path.Combine(ScreenshotDir, adsPowerId.Trim() + ".png");
                if (!File.Exists(path))
                    return Request.CreateResponse(HttpStatusCode.NotFound);

                var fi = new FileInfo(path);
                var age = DateTime.UtcNow - fi.CreationTimeUtc;
                if (age > MaxAge)
                    return Request.CreateResponse(HttpStatusCode.NotFound);

                byte[] bytes;
                using (var src = (Bitmap)Image.FromFile(path))
                {
                    var rect = QrCropRect;
                    rect.Intersect(new Rectangle(0, 0, src.Width, src.Height));
                    if (rect.Width <= 0 || rect.Height <= 0)
                        return Request.CreateResponse(HttpStatusCode.NotFound);

                    using (var cropped = src.Clone(rect, PixelFormat.Format24bppRgb))
                    using (var outBmp = new Bitmap(QrOutSize, QrOutSize, PixelFormat.Format24bppRgb))
                    using (var g = Graphics.FromImage(outBmp))
                    using (var ms = new MemoryStream())
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.White);
                        g.DrawImage(cropped, new Rectangle(0, 0, QrOutSize, QrOutSize));

                        outBmp.Save(ms, ImageFormat.Png);
                        bytes = ms.ToArray();
                    }
                }
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Content = new ByteArrayContent(bytes);
                resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                resp.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                    MustRevalidate = true
                };
                return resp;
            }
            catch
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError);
            }
        }
    }
}
