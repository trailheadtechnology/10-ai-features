using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

static partial class Triage
{
    public static async Task Run()
    {
        IChatClient client = Clients.Local("llama3.2");
        // Same message, same queue, every run. Scoring needs that.
        var options = new ChatOptions { Temperature = 0 };

        var results = new List<(Inquiry Inquiry, Category Category)>();
        var queues = Enum.GetValues<Category>().Select(Route).Distinct()
            .ToDictionary(q => q, q => new List<Inquiry>());
        foreach (var inquiry in LoadInquiries())
        {
            // Only a Category can come back: every label has a queue.
            var response = await client.GetResponseAsync<TriageResult>(
                Prompt(inquiry.text), options);
            var queue = Route(response.Result.Category);
            queues[queue].Add(inquiry);   // the label picks the queue
            results.Add((inquiry, response.Result.Category));

            if (queue == "dispatch") Page(inquiry);
            Show(inquiry, response.Result.Category, queue);
        }
        ShowQueues(queues);

        // Not part of the app: the same run scored against hand labels.
        // Accuracy is the headline. Emergency recall decides whether it ships:
        // a missed emergency is a person waiting in a queue nobody watches.
        Score(results);
    }

    // The routing: one queue per label.
    static string Route(Category category) => category switch
    {
        Category.Emergency => "dispatch",
        Category.Permit => "permits-office",
        Category.Conditions => "trail-info-desk",
        Category.Complaint => "visitor-services",
        Category.LostAndFound => "lost-and-found-desk",
        Category.General => "front-desk",
        _ => "human-review",   // unsure: a ranger reads it and picks the queue
    };
}

// The model can only return one of these.
[JsonConverter(typeof(JsonStringEnumConverter<Category>))]
enum Category
{
    [JsonStringEnumMemberName("permit")] Permit,
    [JsonStringEnumMemberName("conditions")] Conditions,
    [JsonStringEnumMemberName("complaint")] Complaint,
    [JsonStringEnumMemberName("lost-and-found")] LostAndFound,
    [JsonStringEnumMemberName("emergency")] Emergency,
    [JsonStringEnumMemberName("general")] General,
    [JsonStringEnumMemberName("unsure")] Unsure,
}

record TriageResult(Category Category);

static partial class Triage
{
    // These descriptions are the taxonomy, and editing them changes behavior
    // more than any code here. Emergency wins over every other category, so the
    // ordering paragraph at the end must stay. And unsure has to stay narrow:
    // widen it and it fills with ordinary traffic, the unsorted inbox again.
    static string Prompt(string text) => $"""
        You are the triage system for the Trailhead Guides shared inbox.
        Classify the visitor message into exactly one category.

        - permit: reserving, changing, canceling, or paying for a permit, pass,
          or reservation, including billing problems and missing confirmations
          for a permit application, and asking whether an activity such as
          commercial photography needs a special use permit.
        - conditions: asking whether a trail, road, or area is open, safe, or
          passable right now: snow, water levels, washouts, wildlife activity,
          closures.
        - complaint: unhappy about a park facility, service, or staff member
          and wants it acknowledged or fixed.
        - lost-and-found: reporting a lost or found physical item.
        - emergency: a person may be hurt, missing, or in danger right now and
          needs immediate human attention.
        - general: anything else: park rules, fees, trip planning, questions
          that fit none of the above.
        - unsure: two different queues both have to act before this message can
          be resolved, so no single queue owns it. The case that qualifies: the
          sender asks about trail conditions AND asks someone to change, refund,
          or cancel a booking. Trail info cannot issue a refund, and the permits
          office does not decide whether a trail is passable, so a human reads
          this queue and splits the work. Also use unsure when the message fits
          none of the categories above.

        Decide in this order. First, if anyone might be hurt, missing, or in
        danger, answer emergency and stop; never answer unsure for those, even
        when the message also mentions permits, conditions, or a lost item.
        Second, if one queue can resolve the whole message on its own, answer
        that queue; a booking or reservation problem with nothing else attached
        is permit, not unsure. Third, only if two queues must both act, answer
        unsure. Unsure is not a catch-all for anything hard.

        Message:
        {text}
        """;

    // ---------------------------------------------------------------------------
    // Loading, printing, and scoring against the reference labels.
    // ---------------------------------------------------------------------------
    static readonly ReferenceLabels Reference = JsonSerializer.Deserialize<ReferenceLabels>(
        File.ReadAllText(Clients.Data("triage/reference-labels.json")))!;

    static IEnumerable<Inquiry> LoadInquiries() =>
        File.ReadLines(Clients.Data("triage/inquiries-slice.jsonl"))
            .Select(line => JsonSerializer.Deserialize<Inquiry>(line)!);

    static void Show(Inquiry inquiry, Category category, string queue)
    {
        var flag = category == Category.Emergency ? "!!!" : "   ";
        Console.WriteLine($"{flag} {inquiry.id,-9} {Wire(category),-15} -> {queue,-20} {Clip(inquiry.text, 48)}");
    }

    // An emergency does not wait for anyone to open a queue.
    static void Page(Inquiry inquiry) =>
        Console.WriteLine($"\u001b[31m!!! PAGE duty ranger: {inquiry.id} ({inquiry.channel}, {inquiry.received})\u001b[0m");

    static void ShowQueues(Dictionary<string, List<Inquiry>> queues)
    {
        Console.WriteLine();
        Console.WriteLine("-- queues --");
        foreach (var (queue, items) in queues.Where(q => q.Value.Count > 0).OrderByDescending(q => q.Key == "dispatch").ThenByDescending(q => q.Value.Count))
            Console.WriteLine($"  {queue,-20} {items.Count,2}  {string.Join(" ", items.Select(i => i.id))}");
    }

    static void Score(List<(Inquiry Inquiry, Category Category)> results)
    {
        var correct = results.Count(r => Wire(r.Category) == Reference.Labels[r.Inquiry.id]);
        var emergencyIds = Reference.Labels.Where(l => l.Value == "emergency").Select(l => l.Key).ToList();
        var caught = results.Count(r => r.Category == Category.Emergency && emergencyIds.Contains(r.Inquiry.id));

        Console.WriteLine();
        Console.WriteLine("-- evaluation: hand labels, not part of the app --");
        Console.WriteLine($"Accuracy vs reference labels: {correct}/{results.Count}");
        Console.WriteLine($"Emergency recall: {caught}/{emergencyIds.Count} " +
            (caught == emergencyIds.Count ? "(all caught; the metric that matters)" : "(MISSED ONE; this fails, whatever the accuracy says)"));
        foreach (var (inquiry, category) in results.Where(r => Wire(r.Category) != Reference.Labels[r.Inquiry.id]))
            Console.WriteLine($"  miss: {inquiry.id} got {Wire(category)}, reference says {Reference.Labels[inquiry.id]}");
    }

    static string Wire(Category c) => c switch
    {
        Category.LostAndFound => "lost-and-found",
        _ => c.ToString().ToLowerInvariant(),
    };

    static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    record Inquiry(string id, string channel, string received, string text);

    record ReferenceLabels(
        [property: JsonPropertyName("labels")] Dictionary<string, string> Labels);
}
