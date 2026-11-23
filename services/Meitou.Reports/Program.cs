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
MapBench(app);
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
    builder.Services.AddSingleton(NewReleases);
    builder.Services.AddSingleton<Reporters>();
    builder.Services.AddSingleton<GithubAuth>();
    builder.Services.AddSingleton<Feedback>();
    builder.Services.AddSingleton<Bench>();
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // A feedback report is up to five requests (start, three files, send); the 20 a day per person is counted on the forms.
        o.AddPolicy("feedback", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => TenMinuteWindow(60)));

        // A benchmark session is one request; the launcher submits one per tier, five at most.
        o.AddPolicy("bench", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => TenMinuteWindow(20)));
    });
}

// The releases reader with an HTTP client of its own.
static Releases NewReleases(IServiceProvider sp)
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    return new Releases(http, sp.GetRequiredService<Config>(), sp.GetRequiredService<GithubAuth>(), sp.GetRequiredService<ILogger<Releases>>());
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

    // The load order with every workshop link, for an issue too long to hold them: public like the files; no scripts, the names are the player's.
    app.MapGet($"/feedback/{{token}}/{Feedback.LoadOrderFile}", async (string token, Feedback feedback, HttpResponse response) =>
    {
        if (await feedback.LoadOrderPage(token) is not { } page) return Results.NotFound();

        response.Headers.ContentSecurityPolicy = "default-src 'none'";
        return Results.Content(page, "text/html; charset=utf-8");
    });

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

// The launcher's benchmarks: submitting a session; the sessions and the targets, which the site reads from its own origin.
static void MapBench(WebApplication app)
{
    app.MapPost("/bench", async (HttpRequest request, Bench bench) =>
    {
        var (id, error) = await bench.Submit(request);
        return id is { } created
            ? Results.Json(new BenchCreated(created), ReportsJson.Default.BenchCreated, statusCode: StatusCodes.Status201Created)
            : Results.BadRequest(new Refused(error!));
    }).RequireRateLimiting("bench");
    app.MapGet("/bench/data", async (HttpContext ctx, Bench bench) =>
    {
        AllowSite(ctx);
        ctx.Response.Headers.CacheControl = "public, max-age=60";
        return Results.Content(await bench.Data(), "application/json; charset=utf-8");
    });
    app.MapGet("/bench/targets", async (HttpContext ctx, Releases releases) =>
    {
        AllowSite(ctx);
        if (await releases.Targets() is not { } targets)
            return Results.Json(new Refused("the game's releases can't be read now"), ReportsJson.Default.Refused, statusCode: 503);

        ctx.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Ok(new BenchTargets(targets));
    });
}

// CORS for the site's pages (a plain GET, so no preflight): the allowed origin is echoed; Vary keeps caches from mixing origins.
static void AllowSite(HttpContext ctx)
{
    string[] siteOrigins = ["https://meitou.kenshi.wiki", "http://localhost:4321"];
    string origin = ctx.Request.Headers.Origin.ToString();
    if (siteOrigins.Contains(origin)) ctx.Response.Headers.AccessControlAllowOrigin = origin;
    ctx.Response.Headers.Vary = "Origin";
}

// Admin (Authorization: Bearer REPORTS_ADMIN_TOKEN): removing a feedback form and its files; replacing the legacy benchmark sessions.
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
    admin.MapPost("/admin/bench/legacy", async (HttpRequest request, Bench bench) =>
    {
        var (count, error) = await bench.ImportLegacy(request);
        return count is { } imported ? Results.Ok(new LegacyImported(imported)) : Results.BadRequest(new Refused(error!));
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
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(BenchBody))]
[JsonSerializable(typeof(BenchMachine))]
[JsonSerializable(typeof(BenchCreated))]
[JsonSerializable(typeof(BenchData))]
[JsonSerializable(typeof(BenchTargets))]
[JsonSerializable(typeof(LegacyBody))]
[JsonSerializable(typeof(LegacyImported))]
sealed partial class ReportsJson : JsonSerializerContext;
