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
        // Подправлены под новый макет. Значение x уменьшено и ширина увеличена —
        // иногда QR располагается у левого края, поэтому фиксированный большой сдвиг вправо приводит к обрезанию.
        private static readonly Rectangle TelegramQrCropRect = new Rectangle(x: 40, y: 40, width: 520, height: 520);

        // Координаты QR для WhatsApp Web (пример на скрине).
        private static readonly Rectangle WhatsAppQrCropRect = new Rectangle(x: 880, y: 345, width: 320, height: 320);

        // Координаты QR для Max (по примеру скрина).
        private static readonly Rectangle MaxQrCropRect = new Rectangle(x: 915, y: 230, width: 300, height: 300);
        private const int QrOutSize = 300;

        private static string ConnectionString => ConfigurationManager.ConnectionStrings["chatConnectionString"]?.ConnectionString;

        private static void LogToFile(string message, Exception ex = null)
        {
            try
            {
                string baseDir;
                try
                {
                    var ctx = System.Web.HttpContext.Current;
                    baseDir = ctx?.Server?.MapPath("~/App_Data/logs") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "logs");
                }
                catch
                {
                    baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "logs");
                }

                Directory.CreateDirectory(baseDir);
                var path = Path.Combine(baseDir, "qrcode.log");
                var text = ex == null ? message : (message + Environment.NewLine + ex);
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + text + Environment.NewLine + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch { }
        }

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
                LogToFile($"QrCode request for adsPowerId={adsPowerId}, path={path}");
                if (!File.Exists(path))
                {
                    LogToFile($"QrCode file not found: {path}");
                    return Request.CreateResponse(HttpStatusCode.NotFound);
                }

                var fi = new FileInfo(path);
                var lastWrite = fi.LastWriteTimeUtc;
                var age = DateTime.UtcNow - lastWrite;
                LogToFile($"QrCode file info: length={fi.Length} bytes, lastWriteUtc={lastWrite:o}, ageMinutes={age.TotalMinutes:F1}");
                if (age > MaxAge)
                {
                    LogToFile($"QrCode file too old (age {age.TotalMinutes:F1} min) - returning 404");
                    return Request.CreateResponse(HttpStatusCode.NotFound);
                }

                byte[] bytes;
                using (var src = (Bitmap)Image.FromFile(path))
                {
                    LogToFile($"QrCode image loaded: size={src.Width}x{src.Height}");
                    var platform = TryGetPlatformByAdsPowerId(adsPowerId);
                    var rect = GetQrCropRect(platform);
                    LogToFile($"Initial crop rect for platform={platform}: {rect}");
                    rect.Intersect(new Rectangle(0, 0, src.Width, src.Height));
                    LogToFile($"Intersected crop rect: {rect}");

                    // If the rect was partially outside image boundaries, try to expand it with a margin
                    // to include QR codes that are near image edges (left/top).
                    const int expandMargin = 100;
                    var expandX = Math.Max(0, rect.X - expandMargin);
                    var expandY = Math.Max(0, rect.Y - expandMargin);
                    var expandW = Math.Min(src.Width - expandX, rect.Width + expandMargin * 2);
                    var expandH = Math.Min(src.Height - expandY, rect.Height + expandMargin * 2);
                    var expanded = new Rectangle(expandX, expandY, expandW, expandH);
                    // Use expanded rect only if it increases the area and stays valid
                    if (expanded.Width > rect.Width || expanded.Height > rect.Height)
                    {
                        LogToFile($"Expanding crop rect to include margin: {expanded}");
                        rect = expanded;
                    }

                    if (rect.Width <= 0 || rect.Height <= 0)
                    {
                        // Fallback: use centered square region of the screenshot
                        var side = Math.Min(src.Width, src.Height);
                        var fallback = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
                        LogToFile($"Crop rect invalid; using fallback centered rect: {fallback}");
                        rect = fallback;
                    }

                    using (var cropped = src.Clone(rect, PixelFormat.Format24bppRgb))
                    {
                        // Quick heuristic: if cropped area is mostly white (no QR), fall back to returning the full screenshot
                        int sampleStep = Math.Max(1, Math.Min(cropped.Width, cropped.Height) / 100); // ~100x100 samples
                        int dark = 0, total = 0;
                        for (int y = 0; y < cropped.Height; y += sampleStep)
                        {
                            for (int x = 0; x < cropped.Width; x += sampleStep)
                            {
                                var px = cropped.GetPixel(x, y);
                                int sum = px.R + px.G + px.B;
                                if (sum < 700) // not very white
                                    dark++;
                                total++;
                            }
                        }
                        double darkRatio = total > 0 ? (double)dark / total : 0.0;
                        LogToFile($"Crop sample darkRatio={darkRatio:F3} (dark={dark} total={total}) for acc={adsPowerId}");

                        bool useFull = darkRatio < 0.02; // if <2% of sampled pixels are dark, likely empty

                        using (var outBmp = new Bitmap(QrOutSize, QrOutSize, PixelFormat.Format24bppRgb))
                        using (var g = Graphics.FromImage(outBmp))
                        using (var ms = new MemoryStream())
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.Clear(Color.White);
                            if (!useFull)
                            {
                                g.DrawImage(cropped, new Rectangle(0, 0, QrOutSize, QrOutSize));
                            }
                            else
                            {
                                LogToFile($"Cropped area looks empty, returning full screenshot for acc={adsPowerId}");
                                // draw scaled full screenshot instead
                                g.DrawImage(src, new Rectangle(0, 0, QrOutSize, QrOutSize));
                            }

                            outBmp.Save(ms, ImageFormat.Png);
                            bytes = ms.ToArray();
                        }
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
