using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Meitou.Reports;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var config = Config.Load(builder.Configuration);
AddServices(builder, config);

var app = builder.Build();
await app.Services.GetRequiredService<Store>().Migrate();
app.UseCors();
app.UseRateLimiter();

MapPublic(app);
MapReports(app, config);
MapFeedback(app);
MapAdmin(app, config);
MapDiscord(app);
app.Run();

// The database, the JSON types, the services, CORS for the configured origins and the rate limits.
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
        return new Roadmap(sp.GetRequiredService<IHttpClientFactory>().CreateClient(), config, sp.GetRequiredService<ILogger<Roadmap>>());
    });
    builder.Services.AddSingleton<Intake>();
    builder.Services.AddSingleton<Discord>();
    builder.Services.AddSingleton<GithubAuth>();
    builder.Services.AddSingleton<Feedback>();
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(config.Origins).WithMethods("GET", "POST").WithHeaders("Content-Type")));
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy("post", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => TenMinuteWindow(10)));

        // A feedback report is up to five requests (start, three files, send); the 20 a day per person is counted on the forms.
        o.AddPolicy("feedback", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => TenMinuteWindow(60)));
    });
}

// A rate limit of permits per client in each 10 minutes.
static FixedWindowRateLimiterOptions TenMinuteWindow(int permits) => new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(10) };

// The pages anyone may read: the service's name, its health, the roadmap's counts, the newest versions and the proposals.
static void MapPublic(WebApplication app)
{
    app.MapGet("/", () => "Meitou play reports. https://github.com/VlcekM/MeitouClient/tree/master/services/Meitou.Reports");
    app.MapGet("/health", () => Results.Ok());

    // Public: counts per row since the cutoff (reports older than two releases stop counting, a Broken one once a full release is newer
    // than its build), for the roadmap page.
    app.MapGet("/summary", async (Store store, Roadmap roadmap, HttpResponse response) =>
    {
        var window = await roadmap.Window();
        response.Headers.CacheControl = "public, max-age=60";
        return new Summary(window.Since, await store.Summary(window));
    });

    // Public: the newest game versions (full release, and any kind), for the game's feedback form; GitHub is read at most every 10 minutes.
    app.MapGet("/latest", async (Roadmap roadmap, HttpResponse response) =>
    {
        response.Headers.CacheControl = "public, max-age=300";
        return (await roadmap.Window()).Latest;
    });

    // Public: the status changes the reports suggest; a person approves them on the roadmap.
    app.MapGet("/proposals", async (Store store, Roadmap roadmap) =>
    {
        return Roadmap.Propose(await roadmap.Rows(), await store.Summary(await roadmap.Window()));
    });
}

// A report from the roadmap page (Turnstile when configured) or the launcher (may carry the end of log.txt).
static void MapReports(WebApplication app, Config config)
{
    app.MapPost("/reports", async (HttpContext ctx, ReportBody body, Intake intake, IHttpClientFactory http) =>
    {
        bool launcher = body.Source == "launcher";
        if (!launcher && config.TurnstileSecret is { } secret && !await Turnstile(http, secret, body.Turnstile, ClientIp(ctx)))
            return Results.BadRequest(new Refused("bot check failed"));
        var report = new Incoming(body.Row, body.Verdict, body.Build, body.Platform, body.Modded, body.Note, launcher ? body.Log : null);
        var (id, error) = await intake.Add(report, launcher ? "launcher" : "web", intake.Reporter($"ip:{ClientIp(ctx)}"), trusted: false);
        return error is null ? Results.Ok(new Recorded(id)) : Results.BadRequest(new Refused(error));
    }).RequireRateLimiting("post");
}

// The game's feedback button: start a form, upload each attachment as the raw request body, send it (it becomes a GitHub issue).
static void MapFeedback(WebApplication app)
{
    app.MapPost("/feedback", async (HttpContext ctx, FeedbackBody body, Feedback feedback, Intake intake) =>
    {
        var (token, error) = await feedback.Create(body, intake.Reporter($"ip:{ClientIp(ctx)}"));
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

// Admin (Authorization: Bearer REPORTS_ADMIN_TOKEN): the raw reports, a report's log, removing spam, registering the Discord commands.
static void MapAdmin(WebApplication app, Config config)
{
    var admin = app.MapGroup("").AddEndpointFilter(async (ctx, next) =>
    {
        return config.AdminToken is { } token && ctx.HttpContext.Request.Headers.Authorization.ToString() == $"Bearer {token}"
            ? await next(ctx)
            : Results.Unauthorized();
    });
    admin.MapGet("/reports", async (Store store, DateTimeOffset? since, string? row, int? limit) =>
    {
        return await store.List(since ?? DateTimeOffset.UtcNow.AddDays(-7), row, Math.Clamp(limit ?? 500, 1, 5000));
    });
    admin.MapGet("/reports/{id:long}/log", async (Store store, long id) =>
    {
        return await store.Log(id) is { } log ? Results.Text(Intake.Unzip(log), "text/plain; charset=utf-8") : Results.NotFound();
    });
    admin.MapDelete("/reports/{id:long}", async (Store store, long id) => await store.Delete(id) ? Results.NoContent() : Results.NotFound());
    admin.MapDelete("/feedback/{token}", async (Store store, Feedback feedback, string token) =>
    {
        feedback.DeleteFiles(token);
        return await store.DeleteFeedback(token) ? Results.NoContent() : Results.NotFound();
    });
    admin.MapPost("/discord/register", async (Discord discord) => Results.Text(await discord.Register()));
}

// Discord's interactions endpoint: each request is checked against the application's key before it is handled.
static void MapDiscord(WebApplication app)
{
    app.MapPost("/discord", async (HttpRequest request, Discord discord) =>
    {
        if (!discord.Enabled) return Results.NotFound();
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        var body = buffer.ToArray();
        if (!discord.Verify(request.Headers["X-Signature-Ed25519"], request.Headers["X-Signature-Timestamp"], body))
            return Results.Unauthorized();
        return Results.Text((await discord.Handle(JsonNode.Parse(body)!)).ToJsonString(), "application/json");
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

// Cloudflare Turnstile: the page's widget gives a token, checked here with the secret.
static async Task<bool> Turnstile(IHttpClientFactory http, string secret, string? token, string ip)
{
    if (string.IsNullOrEmpty(token)) return false;
    try
    {
        using var response = await http.CreateClient().PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["secret"] = secret, ["response"] = token, ["remoteip"] = ip }));
        var result = JsonNode.Parse(await response.Content.ReadAsStreamAsync());
        return result?["success"]?.GetValue<bool>() == true;
    }
    catch (HttpRequestException)
    {
        return false;
    }
}

/// <summary>
/// The JSON body of <c>POST /reports</c>.
/// </summary>
sealed record ReportBody(string? Row, string? Verdict, string? Build, string? Platform, bool? Modded, string? Note, string? Source, string? Turnstile,
        string? Log);

/// <summary>
/// <c>GET /summary</c>: the counts per row since the cutoff.
/// </summary>
sealed record Summary(DateTimeOffset Since, List<RowSummary> Rows);

/// <summary>
/// <c>POST /reports</c> answers: the stored report's id, or why it was refused.
/// </summary>
sealed record Recorded(long? Id);

/// <summary>
/// A refused report: why.
/// </summary>
sealed record Refused(string Error);

/// <summary>
/// Every type that crosses the wire as JSON: Native AOT has no reflection-based serializer, so each is generated here.
/// </summary>
[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ReportBody))]
[JsonSerializable(typeof(Summary))]
[JsonSerializable(typeof(List<Proposal>))]
[JsonSerializable(typeof(List<Report>))]
[JsonSerializable(typeof(Recorded))]
[JsonSerializable(typeof(Refused))]
[JsonSerializable(typeof(RoadmapFile))]
[JsonSerializable(typeof(List<GithubRelease>))]
[JsonSerializable(typeof(Latest))]
[JsonSerializable(typeof(FeedbackBody))]
[JsonSerializable(typeof(FeedbackCreated))]
[JsonSerializable(typeof(FeedbackIssue))]
sealed partial class ReportsJson : JsonSerializerContext;
