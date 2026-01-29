using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using Core;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Configuration;
using System.Data.SqlClient;
using System.Data;

namespace ChatT30P.Controllers.Api
{
    public class QrCodeController : ApiController
    {
        private static readonly string ScreenshotDir = @"C:\.ADSPOWER_GLOBAL\RPA\screenshot";
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

        // Координаты QR на скриншоте Telegram Web QR login.
        // Если размер скриншота отличается, нужно подстроить значения.
        private static readonly Rectangle TelegramQrCropRect = new Rectangle(x: 353, y: 80, width: 310, height: 310);

        // Координаты QR для WhatsApp Web (пример на скрине).
        private static readonly Rectangle WhatsAppQrCropRect = new Rectangle(x: 880, y: 345, width: 320, height: 320);

        // Координаты QR для Max (по примеру скрина).
        private static readonly Rectangle MaxQrCropRect = new Rectangle(x: 915, y: 230, width: 300, height: 300);
        private const int QrOutSize = 300;

        private static string ConnectionString => ConfigurationManager.ConnectionStrings["chatConnectionString"]?.ConnectionString;

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
                    var platform = TryGetPlatformByAdsPowerId(adsPowerId);
                    var rect = GetQrCropRect(platform);
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

        private static string TryGetPlatformByAdsPowerId(string adsPowerId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ConnectionString) || string.IsNullOrWhiteSpace(adsPowerId))
                    return null;

                using (var cn = new SqlConnection(ConnectionString))
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT TOP 1 platform FROM accounts WHERE ads_power_id = @ads_power_id";
                    cmd.Parameters.Add("@ads_power_id", SqlDbType.NVarChar, 128).Value = adsPowerId.Trim();
                    cn.Open();
                    return cmd.ExecuteScalar() as string;
                }
            }
            catch
            {
                return null;
            }
        }

        private static Rectangle GetQrCropRect(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform))
                return TelegramQrCropRect;

            if (platform.Equals("Telegram", StringComparison.OrdinalIgnoreCase))
                return TelegramQrCropRect;
            if (platform.Equals("Whatsapp", StringComparison.OrdinalIgnoreCase) || platform.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
                return WhatsAppQrCropRect;
            if (platform.Equals("Max", StringComparison.OrdinalIgnoreCase))
                return MaxQrCropRect;

            return TelegramQrCropRect;
        }
    }
}
