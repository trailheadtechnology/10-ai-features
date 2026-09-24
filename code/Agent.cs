using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

static partial class Agent
{
    public static async Task Run(string request, bool gate)
    {
        if (request == "") request = DefaultRequest;
        Gate = gate;

        IChatClient client = new ChatClientBuilder(Clients.Cloud("gpt-5.5"))
            // The step budget: the only bound on the loop.
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 12)
            .Build();

        // Plain C# methods. The model reads their [Description]s and decides
        // which to call, with what arguments, and when it is done.
        var options = new ChatOptions
        {
            Reasoning = new() { Effort = ReasoningEffort.Low },
            Tools =
            [
                AIFunctionFactory.Create(SearchTrails),
                AIFunctionFactory.Create(GetWeather),
                AIFunctionFactory.Create(GetTrailConditions),
                AIFunctionFactory.Create(CheckCampsites),
                AIFunctionFactory.Create(RequestPermit),
            ],
        };

        await Plan(client, options, request);
    }

    [Description("Submit a backcountry permit request. This files a real request, so use it once, at the end, after the plan is settled.")]
    static string RequestPermit(
        [Description("Park name")] string park,
        [Description("Permit zone, e.g. 'Lake McDonald / Sperry'")] string zone,
        [Description("e.g. '2026-09-14 to 2026-09-16'")] string dates,
        [Description("Number of people")] int groupSize = 2)
    {
        Narrate("request_permit", new { park, zone, dates, groupSize });

        // The one irreversible action never runs on the model's say-so.
        if (Gate && !HumanApproves($"File a permit for {zone}, {dates}?"))
            return Result("""{"status": "cancelled", "message": "The user declined. Do not retry; say no permit was filed."}""");

        return Result(Permits["submit_response"]!.ToJsonString());
    }
}

// ---------------------------------------------------------------------------
// The other four tools: ordinary C# methods over the fixture files. The
// [Description] attributes are the model's only documentation for each tool
// and parameter, so rewording them changes which tools get called. Every
// method prints itself on entry so the loop is visible while it runs.
// ---------------------------------------------------------------------------
static partial class Agent
{
    [Description("Search the trail catalog. Returns matching trails with id, name, park, distance, elevation, difficulty, and features.")]
    static string SearchTrails(
        [Description("Park name, e.g. 'Glacier National Park'. Partial names like 'Glacier' work.")] string park = "Glacier National Park",
        [Description("Optional keywords matched against each trail's features and its name, e.g. ['lake', 'waterfall'] or ['Avalanche Lake'].")] string[]? features = null,
        [Description("Optional maximum difficulty: 'easy', 'moderate', or 'hard'.")] string? maxDifficulty = null)
    {
        Narrate("search_trails", new { park, features, max_difficulty = maxDifficulty });

        var rank = (string d) => d switch { "easy" => 0, "moderate" => 1, _ => 2 };
        var maxRank = maxDifficulty is null ? 2 : rank(maxDifficulty.ToLowerInvariant());

        var found = Trails
            .Where(t => ((string)t!["park"]!).Contains(park, StringComparison.OrdinalIgnoreCase))
            .Where(t => rank((string)t!["difficulty"]!) <= maxRank)
            // Keywords match features OR the trail name. Without the name match
            // Avalanche Lake (27th Glacier trail) never makes the Take(8) cut.
            .Where(t => features is null || features.Length == 0 || features.Any(f =>
                ((string)t!["name"]!).Contains(f, StringComparison.OrdinalIgnoreCase) ||
                t!["features"]!.AsArray().Any(x => ((string)x!).Contains(f, StringComparison.OrdinalIgnoreCase))))
            .Take(8)
            .Select(t => new
            {
                id = (string)t!["id"]!,
                name = (string)t["name"]!,
                park = (string)t["park"]!,
                distance_mi = (double)t["distance_mi"]!,
                elevation_ft = (int)t["elevation_ft"]!,
                difficulty = (string)t["difficulty"]!,
                features = t["features"]!.AsArray().Select(x => (string)x!).ToArray(),
            })
            .ToArray();

        LastResultIds.Clear();
        LastResultIds.AddRange(found.Select(t => t.id));
        return Result(JsonSerializer.Serialize(found));
    }

    [Description("Get the multi-day weather forecast and advisories for a park.")]
    static string GetWeather(
        [Description("Park name, e.g. 'Glacier National Park'.")] string park = "Glacier National Park")
    {
        Narrate("get_weather", new { park });
        var entry = ForPark(Fixture("weather.json"), park);
        return Result(entry?.ToJsonString() ?? $"{{\"error\": \"No forecast available for '{park}'.\"}}");
    }

    [Description("Get the most recent hiker-submitted condition reports for a trail. Always check this before recommending a trail; reports surface closures and hazards such as washouts.")]
    static string GetTrailConditions(
        [Description("The trail id from search_trails, e.g. 'trail-0117'. A trail name also works.")] string? trailId = null)
    {
        Narrate("get_trail_conditions", new { trail_id = trailId });

        // Every failure returns a correctable message instead of throwing, so a
        // bad argument costs one turn of the loop rather than the whole process.
        if (string.IsNullOrWhiteSpace(trailId) || trailId is "null" or "string")
        {
            var candidates = LastResultIds.Count > 0 ? string.Join(", ", LastResultIds) : "call search_trails first";
            return Result($"{{\"error\": \"trailId is required. Call this tool again with one of these ids: {candidates}.\"}}");
        }

        // A model may pass the trail name where an id is expected.
        if (!trailId.StartsWith("trail-", StringComparison.OrdinalIgnoreCase))
        {
            var byName = Trails.FirstOrDefault(t =>
                ((string)t!["name"]!).Contains(trailId, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) trailId = (string)byName["id"]!;
        }

        var id = trailId;
        var reports = File.ReadLines(Clients.Data("agent/condition-reports.jsonl"))
            .Select(l => JsonNode.Parse(l)!)
            .Where(r => string.Equals((string)r["trail_id"]!, id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => (string)r["date"]!)
            .Take(4)
            .Select(r => new { date = (string)r["date"]!, report = (string)r["text"]! })
            .ToArray();

        return Result(reports.Length == 0
            ? $"{{\"error\": \"No condition reports found for '{trailId}'.\"}}"
            : JsonSerializer.Serialize(reports));
    }

    [Description("Check campground availability in a park. Returns campgrounds with open sites per date, type (frontcountry or backcountry), and notes.")]
    static string CheckCampsites(
        [Description("Park name, e.g. 'Glacier National Park'.")] string park = "Glacier National Park")
    {
        Narrate("check_campsites", new { park });
        var entry = ForPark(Fixture("campsites.json"), park);
        return Result(entry?.ToJsonString() ?? $"{{\"error\": \"No campsite data for '{park}'.\"}}");
    }
}

// ---------------------------------------------------------------------------
// The loop driver, the prompt, and the plumbing.
// ---------------------------------------------------------------------------
static partial class Agent
{
    const string DefaultRequest =
        "Plan me a 3-day backpacking trip in Glacier National Park for " +
        "September 14-16, camping at backcountry sites, that includes the " +
        "Avalanche Lake Trail (trail-0117).";

    const string SystemPrompt = """
        You are the trip-planning agent for Trailhead Guides, a hiking app.
        Today's date is September 11, 2026.

        Plan trips using your tools; never invent trails, weather, availability,
        or conditions. Every trail name, forecast, campground, and condition in
        your answer must have come back from a tool call in this conversation.

        Call the tools one at a time, in this order, and do not write any part of
        the itinerary until all of them have been called:
        1. get_weather for the park.
        2. search_trails for candidate trails that fit the request.
        3. get_trail_conditions for EVERY trail you intend to recommend, one call
           per trail, using the trail id returned by search_trails.
           If the newest reports for a trail mention a closure, a washout, a bridge
           that is out, or any other reason hikers are turning around, that trail is
           CLOSED. Do not schedule a day on a closed trail. Replace it with another
           trail from search_trails and state plainly, in the itinerary, that the
           original trail is closed and why.
        4. check_campsites for where to stay each night.
        5. request_permit once, only if a backcountry site or permit zone is involved.

        If you have not yet called search_trails and get_trail_conditions, your
        next move is a tool call, not prose.

        Then write the final itinerary: one section per day with trail, campsite,
        and how the forecast shaped the choice (put harder or more exposed hiking
        on the drier days). End with the permit status.
        """;

    static bool Gate;
    static readonly HashSet<string> Called = [];
    static readonly List<string> LastResultIds = [];
    static readonly JsonArray Trails =
        JsonNode.Parse(File.ReadAllText(Clients.Data("agent/trails.json")))!.AsArray();
    static readonly JsonNode Permits = Fixture("permits.json");

    static async Task Plan(IChatClient client, ChatOptions options, string request)
    {
        List<ChatMessage> messages = [new(ChatRole.System, SystemPrompt), new(ChatRole.User, request)];
        Console.WriteLine($"Request: {request}");
        Console.WriteLine($"Permit gate: {(Gate ? "ON, a human approves" : "OFF, the model decides")}");
        Console.WriteLine(new string('=', 60));

        var response = await StreamTurn(client, messages, options);

        // A model can stop mid-plan believing it is done. When a required tool
        // is still uncalled, name it and let the loop continue; capped at three.
        // gpt-5.5 should need none; [nudge] lines mean the model is underpowered.
        string[] required = ["get_weather", "search_trails", "get_trail_conditions", "check_campsites"];
        for (var nudge = 0; nudge < 3; nudge++)
        {
            var missing = required.Where(t => !Called.Contains(t)).ToArray();
            if (missing.Length == 0) break;

            Console.WriteLine($"[nudge] still missing: {string.Join(", ", missing)}");
            messages.AddRange(response.Messages);
            messages.Add(new ChatMessage(ChatRole.User,
                $"You have not called these tools yet: {string.Join(", ", missing)}. " +
                "Call the next one now with real arguments. Do not write the itinerary yet."));
            response = await StreamTurn(client, messages, options);
        }
    }

    // Tool calls print as they happen; the itinerary streams once they are done.
    static async Task<ChatResponse> StreamTurn(IChatClient client, List<ChatMessage> messages, ChatOptions options)
    {
        List<ChatResponseUpdate> updates = [];
        var started = false;
        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            updates.Add(update);
            if (string.IsNullOrEmpty(update.Text)) continue;
            if (!started) { Console.WriteLine(new string('=', 60)); started = true; }
            Console.Write(update.Text);
        }
        Console.WriteLine();
        return updates.ToChatResponse();
    }

    static bool HumanApproves(string question)
    {
        Console.Write($"  [gate] {question} [y/N] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Clients.Data($"agent/mock-apis/{name}")))!;

    static JsonNode? ForPark(JsonNode fixture, string park) =>
        fixture.AsObject().FirstOrDefault(kv =>
            kv.Key.Contains(park, StringComparison.OrdinalIgnoreCase) ||
            park.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
            kv.Key.Contains(park.Split(' ')[0], StringComparison.OrdinalIgnoreCase)).Value;

    static void Narrate(string tool, object args)
    {
        Called.Add(tool);
        Console.WriteLine($"[tool] {tool} {JsonSerializer.Serialize(args)}");
    }

    static string Result(string json)
    {
        Console.WriteLine($"  [result] {(json.Length > 100 ? json[..100] + "..." : json)}");
        return json;
    }
}
