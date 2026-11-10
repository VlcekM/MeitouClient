using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Meitou.Reports;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var config = Config.Load(builder.Configuration);
AddServices(builder, config);

var app = builder.Build();
await app.Services.GetRequiredService<Store>().Migrate();
app.UseRateLimiter();

MapPublic(app);
MapFeedback(app);
MapAdmin(app, config);
app.Run();

// The database, the JSON types, the services and the rate limit.
static void AddServices(WebApplicationBuilder builder, Config config)
{
    builder.Services.AddSingleton(config);

    // The slim builder: the basic types and no reflection; TLS is opt-in there and the database is reached over it.
    builder.Services.AddSingleton(new NpgsqlSlimDataSourceBuilder(config.ConnectionString).EnableTransportSecurity().EnableArrays().Build());
    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ReportsJson.Default));
    builder.Services.AddSingleton<Store>();
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton(sp =>
    {
        return new Releases(sp.GetRequiredService<IHttpClientFactory>().CreateClient(), config, sp.GetRequiredService<ILogger<Releases>>());
    });
    builder.Services.AddSingleton<Reporters>();
    builder.Services.AddSingleton<GithubAuth>();
    builder.Services.AddSingleton<Feedback>();
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // A feedback report is up to five requests (start, three files, send); the 20 a day per person is counted on the forms.
        o.AddPolicy("feedback", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => TenMinuteWindow(60)));
    });
}

// A rate limit of permits per client in each 10 minutes.
static FixedWindowRateLimiterOptions TenMinuteWindow(int permits) => new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(10) };

// The pages anyone may read: the service's name, its health and the newest versions.
static void MapPublic(WebApplication app)
{
    app.MapGet("/", () => "Meitou feedback. https://github.com/VlcekM/MeitouClient/tree/master/services/Meitou.Reports");
    app.MapGet("/health", () => Results.Ok());

    // Public: the newest game versions (full release, and any kind), for the game's feedback form; GitHub is read at most every 10 minutes.
    app.MapGet("/latest", async (Releases releases, HttpResponse response) =>
    {
        response.Headers.CacheControl = "public, max-age=300";
        return await releases.Latest();
    });
}

// The game's feedback button: start a form, upload each attachment as the raw request body, send it (it becomes a GitHub issue).
static void MapFeedback(WebApplication app)
{
    app.MapPost("/feedback", async (HttpContext ctx, FeedbackBody body, Feedback feedback, Reporters reporters) =>
    {
        var (token, error) = await feedback.Create(body, reporters.Hash($"ip:{ClientIp(ctx)}"));
        return error is null ? Results.Ok(new FeedbackCreated(token!)) : Results.BadRequest(new Refused(error));
    }).RequireRateLimiting("feedback");
    app.MapPut("/feedback/{token}/{kind}", async (HttpRequest request, string token, string kind, Feedback feedback) =>
    {
        return await feedback.Upload(token, kind, request) is { } refused
            ? Results.Json(new Refused(refused.Error), ReportsJson.Default.Refused, statusCode: refused.Status)
            : Results.NoContent();
    }).RequireRateLimiting("feedback");
    app.MapPost("/feedback/{token}/send", async (string token, Feedback feedback) =>
    {
        var (issue, error) = await feedback.Submit(token);
        return issue is not null ? Results.Ok(issue) : Results.BadRequest(new Refused(error!));
    }).RequireRateLimiting("feedback");

    // The issue's links: public, the token is the secret. The log is stored gzipped and sent as such for the browser to unpack.
    app.MapGet("/feedback/{token}/{file}", (string token, string file, Feedback feedback, HttpResponse response) =>
    {
        string? kind = Feedback.Kinds.FirstOrDefault(k => Feedback.FileName(k) == file);
        if (kind is null || feedback.FilePath(token, kind) is not { } path) return Results.NotFound();
        response.Headers.CacheControl = "public, max-age=31536000, immutable";
        if (kind == "log") response.Headers.ContentEncoding = "gzip";
        string type = kind switch { "screenshot" or "navmesh" => "image/png", "save" => "application/zip", _ => "text/plain; charset=utf-8" };
        return Results.File(path, type, kind == "save" ? $"meitou-feedback-{token[..8]}.zip" : null);
    });
}

// Admin (Authorization: Bearer REPORTS_ADMIN_TOKEN): removing a feedback form and its files.
static void MapAdmin(WebApplication app, Config config)
{
    var admin = app.MapGroup("").AddEndpointFilter(async (ctx, next) =>
    {
        return config.AdminToken is { } token && ctx.HttpContext.Request.Headers.Authorization.ToString() == $"Bearer {token}"
            ? await next(ctx)
            : Results.Unauthorized();
    });
    admin.MapDelete("/feedback/{token}", async (Store store, Feedback feedback, string token) =>
    {
        feedback.DeleteFiles(token);
        return await store.DeleteFeedback(token) ? Results.NoContent() : Results.NotFound();
    });
}

// Behind Cloudflare and the Cloudovna edge: Cloudflare's header, else the address the edge appended last, else the socket.
static string ClientIp(HttpContext ctx)
{
    var h = ctx.Request.Headers;
    if (h["CF-Connecting-IP"].ToString() is { Length: > 0 } cf) return cf;
    if (h["X-Forwarded-For"].ToString() is { Length: > 0 } xff) return xff.Split(',')[^1].Trim();
    return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>
/// A refused request: why.
/// </summary>
sealed record Refused(string Error);

/// <summary>
/// Every type that crosses the wire as JSON: Native AOT has no reflection-based serializer, so each is generated here.
/// </summary>
[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Refused))]
[JsonSerializable(typeof(List<GithubRelease>))]
[JsonSerializable(typeof(Latest))]
[JsonSerializable(typeof(FeedbackBody))]
[JsonSerializable(typeof(FeedbackCreated))]
[JsonSerializable(typeof(FeedbackIssue))]
sealed partial class ReportsJson : JsonSerializerContext;
