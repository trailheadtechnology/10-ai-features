using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;

static partial class Extract
{
    public static async Task Run()
    {
        IChatClient client = Clients.Local("llama3.2");

        foreach (var file in new[] { "tr-0007.md", "tr-0011.md" })
        {
            var report = await Load(file);

            // Prose in, typed object out. The record is the schema.
            var response = await client.GetResponseAsync<TripFacts>($"""
                Extract the trail facts from this trip report.
                Use null for any field the report does not state,
                and empty arrays when nothing applies. Do not guess.

                {report}
                """);
            TripFacts raw = response.Result;
            Show("what the model gave us", raw);

            // The schema guarantees it parses, not that it is true.
            var verdicts = Validate(raw, report);
            ShowVerdicts(verdicts);
            Show("what we would store", Clean(raw, verdicts));
        }
    }
}

// The schema does the prompting. Every scalar is nullable and every
// description says when to use null; a non-nullable field forces the
// model to invent a value for it.
record TripFacts(
    [property: Description("The name of the trail hiked. null if the report never names the trail.")]
    string? TrailName,
    [property: Description("The park the trail is in. null if the report never names the park.")]
    string? Park,
    [property: Description("The date of the hike in YYYY-MM-DD format. null if the report does not give an exact date. Never guess or infer a date.")]
    string? DateHiked,
    [property: Description("Round-trip distance in miles, as stated in the report. null, never 0, if the report gives no distance. Never estimate.")]
    double? DistanceMi,
    [property: Description("Elevation gain in feet, as stated in the report. null, never 0, if the report gives no elevation figure. Never estimate.")]
    double? ElevationGainFt,
    [property: Description("Animals the author actually saw on this hike. Empty array if none are mentioned.")]
    string[] Wildlife,
    [property: Description("Short phrases describing trail conditions the report mentions (mud, snow, water crossings, dry tread). Empty array if none.")]
    string[] Conditions,
    [property: Description("Hazards or closures the report mentions. Empty array if none.")]
    string[] Hazards);

// ---------------------------------------------------------------------------
// The validator. Ordinary code, no model involved. Each rule covers output
// llama3.2 has actually returned for these two reports.
// ---------------------------------------------------------------------------
static partial class Extract
{
    static List<Verdict> Validate(TripFacts f, string sourceText) =>
    [
        // A name the source text never contains does not get to be a fact.
        Grounded("trail_name", f.TrailName, sourceText),
        Grounded("park", f.Park, sourceText),
        ValidDate("date_hiked", f.DateHiked),

        // 0 is the dangerous near-miss: it is a value, so a pipeline stores it
        // and nothing downstream ever questions it. The honest answer is null.
        InRange("distance_mi", f.DistanceMi, max: 100, unit: "mi"),
        InRange("elevation_gain_ft", f.ElevationGainFt, max: 20000, unit: "ft"),
    ];

    static Verdict NonEmpty(string field, string? value)
    {
        if (value is null) return Verdict.Pass(field, null);
        return string.IsNullOrWhiteSpace(value)
            ? Verdict.Fail(field, $"\"{value}\"", "empty or whitespace-only string; should be null")
            : Verdict.Pass(field, value);
    }

    // Cheap grounding check: a name whose distinctive words never appear in the
    // report is a name the model supplied. The boilerplate list is what lets
    // "Glacier National Park" ground on a report that only says "Glacier".
    static Verdict Grounded(string field, string? value, string sourceText)
    {
        string[] boilerplate =
            ["national", "park", "state", "trail", "trailhead", "loop", "canyon", "falls", "the"];

        var basic = NonEmpty(field, value);
        if (!basic.Passed || value is null) return basic;

        var words = value.Split([' ', '-', ',', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && !boilerplate.Contains(w.ToLowerInvariant()))
            .ToArray();

        // Nothing distinctive to check: fall back to the whole string.
        var missing = words.Length == 0
            ? (sourceText.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase) ? [] : new[] { value.Trim() })
            : words.Where(w => !sourceText.Contains(w, StringComparison.OrdinalIgnoreCase)).ToArray();

        return missing.Length == 0
            ? Verdict.Pass(field, value)
            : Verdict.Fail(field, value,
                $"not grounded in the source report (no mention of {string.Join(", ", missing.Select(m => $"\"{m}\""))})");
    }

    static Verdict ValidDate(string field, string? value)
    {
        var basic = NonEmpty(field, value);
        if (!basic.Passed || value is null) return basic;

        // Explicit formats only. "last month (exact date not specified)" is
        // prose, and prose in a date column is a bug waiting for a report.
        string[] formats =
        [
            "yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "M/d/yyyy",
            "MMMM d, yyyy", "MMM d, yyyy", "d MMMM yyyy", "MMMM d yyyy",
        ];
        var parsed = DateOnly.TryParseExact(
            value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);

        if (!parsed)
            return Verdict.Fail(field, value, "does not parse as a date; no parser can store this");
        if (date.Year is < 1900 or > 2100)
            return Verdict.Fail(field, value, $"parses, but the year {date.Year} is not plausible");

        // Parseable but off-format gets normalized on the way to storage.
        return Verdict.Pass(field, value) with { Normalized = date.ToString("yyyy-MM-dd") };
    }

    static Verdict InRange(string field, double? value, double max, string unit)
    {
        if (value is null) return Verdict.Pass(field, null);

        var shown = $"{value.Value:0.#} {unit}";
        if (value.Value == 0)
            return Verdict.Fail(field, shown, "0 is not a measurement; the report gave no figure, so this should be null");
        if (value.Value < 0)
            return Verdict.Fail(field, shown, "negative value is impossible");
        return value.Value > max
            ? Verdict.Fail(field, shown, $"implausible: over {max:0} {unit} for a single day hike")
            : Verdict.Pass(field, shown);
    }

    // Rejected fields become null. Storing nothing beats storing a plausible
    // lie: null is a gap a human can fill, 0 is a number nobody will question.
    static TripFacts Clean(TripFacts f, List<Verdict> verdicts)
    {
        Verdict V(string field) => verdicts.First(v => v.Field == field);
        bool Ok(string field) => V(field).Passed;

        return f with
        {
            TrailName = Ok("trail_name") ? f.TrailName?.Trim() : null,
            Park = Ok("park") ? f.Park?.Trim() : null,
            DateHiked = Ok("date_hiked") ? V("date_hiked").Normalized ?? f.DateHiked?.Trim() : null,
            DistanceMi = Ok("distance_mi") ? f.DistanceMi : null,
            ElevationGainFt = Ok("elevation_gain_ft") ? f.ElevationGainFt : null,
            Wildlife = CleanList(f.Wildlife),
            Conditions = CleanList(f.Conditions),
            Hazards = CleanList(f.Hazards),
        };
    }

    static string[] CleanList(string[]? items) =>
        (items ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();

    // ---------------------------------------------------------------------------
    // Loading and printing.
    // ---------------------------------------------------------------------------
    static async Task<string> Load(string file)
    {
        Console.WriteLine($"== {file} ==  (asking llama3.2...)");
        var markdown = await File.ReadAllTextAsync(Clients.Data($"extract/{file}"));
        var parts = markdown.Split("---", 3, StringSplitOptions.None);
        return parts.Length == 3 ? parts[2].Trim() : markdown.Trim();
    }

    static void Show(string heading, TripFacts f)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {heading} --");
        Console.WriteLine($"  trail:      {f.TrailName ?? "null"}");
        Console.WriteLine($"  park:       {f.Park ?? "null"}");
        Console.WriteLine($"  date:       {f.DateHiked ?? "null"}");
        Console.WriteLine($"  distance:   {f.DistanceMi?.ToString("0.#") ?? "null"} mi");
        Console.WriteLine($"  elev gain:  {f.ElevationGainFt?.ToString("0") ?? "null"} ft");
        Console.WriteLine($"  wildlife:   [{string.Join(", ", f.Wildlife ?? [])}]");
        Console.WriteLine($"  conditions: [{string.Join(", ", f.Conditions ?? [])}]");
        Console.WriteLine($"  hazards:    [{string.Join(", ", f.Hazards ?? [])}]");
        if (heading.StartsWith("what we would store")) Console.WriteLine();
    }

    static void ShowVerdicts(List<Verdict> verdicts)
    {
        Console.WriteLine();
        Console.WriteLine("-- what the validator says --");
        foreach (var v in verdicts)
        {
            var mark = v.Passed ? "PASS  " : "REJECT";
            Console.WriteLine($"  {mark}  {v.Field,-18} {v.Value ?? "null"}");
            if (!v.Passed)
                Console.WriteLine($"          reason: {v.Reason}");
            else if (v.Normalized is not null && v.Normalized != v.Value)
                Console.WriteLine($"          normalized to: {v.Normalized}");
        }
    }
}

record Verdict(string Field, string? Value, bool Passed, string? Reason)
{
    // Set when a field passed only after being rewritten, e.g. a date the model
    // wrote as "July 4, 2026" that we store as "2026-07-04".
    public string? Normalized { get; init; }

    public static Verdict Pass(string field, string? value) => new(field, value, true, null);
    public static Verdict Fail(string field, string? value, string reason) => new(field, value, false, reason);
}
