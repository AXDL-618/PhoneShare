using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace PhoneShareReceiver;

public sealed class UploadServer
{
    private WebApplication? _app;
    private readonly Func<ReceiverSettings> _settingsProvider;
    private readonly Action<string> _log;
    private readonly Action<IReadOnlyList<string>> _filesReceived;
    private readonly Action<PairedPhoneDevice> _devicePaired;

    public bool IsRunning => _app != null;

    public UploadServer(
        Func<ReceiverSettings> settingsProvider,
        Action<string> log,
        Action<IReadOnlyList<string>> filesReceived,
        Action<PairedPhoneDevice> devicePaired)
    {
        _settingsProvider = settingsProvider;
        _log = log;
        _filesReceived = filesReceived;
        _devicePaired = devicePaired;
    }

    public async Task StartAsync()
    {
        if (_app != null) return;

        var settings = _settingsProvider();
        Directory.CreateDirectory(settings.SaveFolder);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.WebHost.UseUrls($"http://0.0.0.0:{settings.Port}");
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = settings.MaxFileSizeMb * 1024L * 1024L;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        });

        var app = builder.Build();

        app.MapGet("/", () => Results.Json(new
        {
            app = "PhoneShareReceiver",
            status = "ok",
            device = settings.DeviceName
        }));

        app.MapGet("/pairing", () => Results.Json(BuildPairingPayload(settings)));

        app.MapGet("/health", () => Results.Json(new
        {
            ok = true,
            app = "PhoneShareReceiver",
            status = "running",
            device = settings.DeviceName
        }));

        app.MapPost("/pair", async (HttpRequest request, CancellationToken ct) =>
        {
            var latest = _settingsProvider();
            if (!IsAuthorized(request, latest.Token))
            {
                _log("拒绝一次未授权配对请求。");
                return Results.Unauthorized();
            }

            PairRequest? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<PairRequest>(
                    request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    ct);
            }
            catch
            {
                payload = null;
            }

            var phoneName = string.IsNullOrWhiteSpace(payload?.PhoneName)
                ? "Android 手机"
                : payload!.PhoneName!.Trim();

            var phone = new PairedPhoneDevice
            {
                DeviceId = string.IsNullOrWhiteSpace(payload?.DeviceId)
                    ? Guid.NewGuid().ToString("N")
                    : payload!.DeviceId!.Trim(),
                PhoneName = phoneName,
                Manufacturer = payload?.Manufacturer?.Trim() ?? "",
                AndroidVersion = payload?.AndroidVersion?.Trim() ?? "",
                FirstPairedAt = DateTimeOffset.Now,
                LastPairedAt = DateTimeOffset.Now
            };

            _log($"手机已配对：{phone.DisplayName}");
            _devicePaired(phone);

            return Results.Ok(new
            {
                ok = true,
                message = "paired",
                receiver = latest.DeviceName,
                knownDevices = latest.PairedPhones.Count,
                urls = NetworkUtil.GetLocalUrls(latest.Port)
            });
        });

        app.MapPost("/upload", async (HttpRequest request, CancellationToken ct) =>
        {
            var latest = _settingsProvider();
            if (!IsAuthorized(request, latest.Token))
            {
                _log("拒绝一次未授权上传请求。 ");
                return Results.Unauthorized();
            }

            if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaTypeHeader) ||
                !string.Equals(mediaTypeHeader.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Content-Type must be multipart/form-data" });
            }

            var boundary = HeaderUtilities.RemoveQuotes(mediaTypeHeader.Boundary).Value;
            if (string.IsNullOrWhiteSpace(boundary))
            {
                return Results.BadRequest(new { error = "Missing multipart boundary" });
            }

            Directory.CreateDirectory(latest.SaveFolder);
            var reader = new MultipartReader(boundary, request.Body);
            var saved = new List<string>();
            MultipartSection? section;

            while ((section = await reader.ReadNextSectionAsync(ct)) != null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                    continue;

                if (!HasFileContentDisposition(contentDisposition))
                    continue;

                var rawFileName = GetFileName(contentDisposition);
                var safeName = SecurityUtil.SafeFileName(rawFileName);
                var targetPath = SecurityUtil.UniquePath(latest.SaveFolder, safeName);

                await using (var fs = new FileStream(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    useAsync: true))
                {
                    await section.Body.CopyToAsync(fs, ct);
                }

                saved.Add(targetPath);
                _log($"已接收：{Path.GetFileName(targetPath)}");
            }

            if (saved.Count == 0)
                return Results.BadRequest(new { error = "No files received" });

            _filesReceived(saved);
            return Results.Ok(new
            {
                ok = true,
                count = saved.Count,
                files = saved.Select(Path.GetFileName).ToArray()
            });
        });

        await app.StartAsync();
        _app = app;
        _log($"接收服务已启动：http://0.0.0.0:{settings.Port}");
    }

    public async Task StopAsync()
    {
        if (_app == null) return;
        try
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _log("接收服务已停止。 ");
        }
        finally
        {
            _app = null;
        }
    }

    public static object BuildPairingPayload(ReceiverSettings settings)
    {
        return new
        {
            type = "PhoneSharePairing",
            version = 1,
            name = settings.DeviceName,
            deviceId = settings.DeviceId,
            token = settings.Token,
            urls = NetworkUtil.GetLocalUrls(settings.Port)
        };
    }

    public static string BuildPairingJson(ReceiverSettings settings)
    {
        return JsonSerializer.Serialize(BuildPairingPayload(settings), new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static bool IsAuthorized(HttpRequest request, string token)
    {
        var auth = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var got = auth[prefix.Length..].Trim();
        return SecurityUtil.FixedTimeEquals(got, token);
    }

    private static bool HasFileContentDisposition(ContentDispositionHeaderValue contentDisposition)
    {
        return string.Equals(contentDisposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase) &&
               (!StringSegment.IsNullOrEmpty(contentDisposition.FileName) ||
                !StringSegment.IsNullOrEmpty(contentDisposition.FileNameStar));
    }

    private static string GetFileName(ContentDispositionHeaderValue contentDisposition)
    {
        var fileName = !StringSegment.IsNullOrEmpty(contentDisposition.FileNameStar)
            ? contentDisposition.FileNameStar.ToString()
            : contentDisposition.FileName.ToString();

        return fileName.Trim().Trim('"');
    }
}


public sealed class PairRequest
{
    public string? PhoneName { get; set; }
    public string? Manufacturer { get; set; }
    public string? AndroidVersion { get; set; }
    public string? DeviceId { get; set; }
}
