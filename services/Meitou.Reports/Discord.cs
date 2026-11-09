using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Meitou.Reports;

/// <summary>
/// Discord slash commands over the HTTP interactions endpoint (no bot connection): <c>/works</c> and <c>/broken</c>, the system
/// autocompleted from the roadmap rows. Members with one of <c>DISCORD_TRUSTED_ROLES</c> count as trusted testers.
/// </summary>
sealed class Discord(Config config, Intake intake, Roadmap roadmap, IHttpClientFactory http)
{
    public bool Enabled => config.DiscordPublicKey is not null;

    /// <summary>
    /// Discord signs every interaction with the application's Ed25519 key over timestamp + body.
    /// </summary>
    public bool Verify(string? signature, string? timestamp, byte[] body)
    {
        if (config.DiscordPublicKey is null || signature is null || timestamp is null) return false;
        try
        {
            return Ed25519.Verify(Convert.FromHexString(config.DiscordPublicKey), Convert.FromHexString(signature),
                    [.. Encoding.UTF8.GetBytes(timestamp), .. body]);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task<JsonObject> Handle(JsonNode interaction)
    {
        int type = (int)interaction["type"]!;
        if (type == 1) return new() { ["type"] = 1 };   // ping
        var data = interaction["data"]!;
        var options = (data["options"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(o => (string)o["name"]!, o => o);
        string? Opt(string name) => options.TryGetValue(name, out var o) ? o["value"]?.ToString() : null;

        if (type == 4)   // autocomplete for the system
        {
            string typed = (Opt("system") ?? "").Trim();
            var rows = await roadmap.Rows();
            var choices = new JsonArray();
            foreach (var r in rows.Values.Where(r => Matches(r, typed)).Take(25))
            {
                // The non-generic Add: the generic one is not AOT-safe.
                choices.Add((JsonNode)new JsonObject { ["name"] = Trim(r.System, 100), ["value"] = r.Id });
            }
            return new() { ["type"] = 8, ["data"] = new JsonObject { ["choices"] = choices } };
        }

        string command = (string)data["name"]!;
        var member = interaction["member"];
        string? userId = (string?)(member?["user"]?["id"] ?? interaction["user"]?["id"]);
        if (userId is null) return Reply("Could not tell who you are.");
        var roles = (member?["roles"] as JsonArray ?? []).Select(r => (string?)r).ToHashSet();
        bool trusted = config.DiscordTrustedRoles.Any(roles.Contains);
        bool? modded = options.TryGetValue("modded", out var m) ? (bool?)m["value"] : null;
        var report = new Incoming(Opt("system"), command, Opt("build"), Opt("platform"), modded, Opt(command == "broken" ? "what" : "note"), null);
        var (id, error) = await intake.Add(report, "discord", intake.Reporter($"discord:{userId}"), trusted);
        if (error is not null) return Reply($"Not recorded: {error}.");
        var row = (await roadmap.Rows())[report.Row!];
        return Reply(command == "works"
            ? $"Thanks, recorded: **{row.System}** works for you (report {id})."
            : $"Thanks, recorded: **{row.System}** is broken for you (report {id}). "
                + $"If you can, open an issue with steps and your log.txt: https://github.com/{config.Repo}/issues/new/choose");
    }

    /// <summary>
    /// Whether the autocomplete offers <paramref name="r"/> for what was <paramref name="typed"/>: its system or id holds it.
    /// </summary>
    static bool Matches(RoadmapRow r, string typed) =>
        typed.Length == 0 || r.System.Contains(typed, StringComparison.OrdinalIgnoreCase) || r.Id.Contains(typed, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An answer saying <paramref name="text"/>; flags 64: only the reporter sees it.
    /// </summary>
    static JsonObject Reply(string text) => new() { ["type"] = 4, ["data"] = new JsonObject { ["content"] = text, ["flags"] = 64 } };

    static string Trim(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    /// <summary>
    /// Registers (overwrites) the two global commands with Discord.
    /// </summary>
    public async Task<string> Register()
    {
        if (config.DiscordAppId is null || config.DiscordBotToken is null)
            return "Set DISCORD_APP_ID and DISCORD_BOT_TOKEN first.";
        JsonObject Str(string name, string description, bool required, bool autocomplete = false, int? max = null)
        {
            var o = new JsonObject { ["type"] = 3, ["name"] = name, ["description"] = description, ["required"] = required };
            if (autocomplete) o["autocomplete"] = true;
            if (max is { } n) o["max_length"] = n;
            return o;
        }
        JsonObject Platform() => new()
        {
            ["type"] = 3, ["name"] = "platform", ["description"] = "Where you play", ["required"] = false,
            ["choices"] = new JsonArray(
                new JsonObject { ["name"] = "Windows", ["value"] = "win" }, new JsonObject { ["name"] = "Linux", ["value"] = "linux" },
                new JsonObject { ["name"] = "macOS", ["value"] = "mac" }, new JsonObject { ["name"] = "Proton", ["value"] = "proton" }),
        };
        JsonObject Modded() => new() { ["type"] = 5, ["name"] = "modded", ["description"] = "Playing with mods", ["required"] = false };
        var commands = new JsonArray(
            new JsonObject
            {
                ["name"] = "works", ["description"] = "Tell the Meitou roadmap a system works for you",
                ["options"] = new JsonArray(Str("system", "The roadmap system", true, autocomplete: true), Str("build", "Meitou version, e.g. 0.15.0",
                        false, max: 64),
                    Platform(), Modded(), Str("note", "What you did", false, max: 300)),
            },
            new JsonObject
            {
                ["name"] = "broken", ["description"] = "Tell the Meitou roadmap a system is broken for you",
                ["options"] = new JsonArray(Str("system", "The roadmap system", true, autocomplete: true), Str("what", "What happened", true,
                        max: 300),
                    Str("build", "Meitou version, e.g. 0.15.0", false, max: 64), Platform(), Modded()),
            });
        using var request = new HttpRequestMessage(HttpMethod.Put, $"https://discord.com/api/v10/applications/{config.DiscordAppId}/commands")
        {
            Content = new StringContent(commands.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", config.DiscordBotToken);
        using var response = await http.CreateClient().SendAsync(request);
        return response.IsSuccessStatusCode
            ? "Commands registered."
            : $"Discord answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
    }
}
