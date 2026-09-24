using System.Numerics.Tensors;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

static partial class Rag
{
    const string Refusal = "The provided documents don't say.";

    public static async Task Run(string question, bool grounded)
    {
        if (question == "") question = "Is the Avalanche Lake Trail open right now?";
        IChatClient llm = Clients.Cloud("gpt-4.1");
        Console.WriteLine($"Q: {question}\n");

        // Before: no documents. The model answers from memory.
        if (!grounded) { await Stream(llm, question); return; }

        // 1. Retrieve: the three chunks closest to the question.
        var top = await Retrieve(question, topK: 3);

        // 2. Ground: the chunks go in, the rules say "only these".
        var answer = await Stream(llm, $"""
            You are a park information assistant. Answer the visitor's
            question using ONLY the context below.
            - Do not use outside knowledge.
            - Cite the chunk_id of each chunk you relied on, in square
              brackets, exactly as it appears in the context.
            - If none of the context is relevant, reply exactly: "{Refusal}"
              A question about "right now" is answered from the context:
              today is September 23, 2026, and a notice in effect "until
              further notice" is still in effect.

            Context:
            {string.Join("\n\n", top.Select(c => $"chunk_id: {c.Id}\n{c.Text}"))}

            Question: {question}
            """);

        // 3. Verify: every citation must point at a chunk we retrieved.
        var bad = Citations(answer).Where(id => !top.Any(c => c.Id == id));
        Console.WriteLine(bad.Any()
            ? $"\n!! INVALID CITATIONS: {string.Join(", ", bad)}"
            : $"\n[citations valid: {string.Join(", ", Citations(answer).Distinct())}]");
    }
}

// ---------------------------------------------------------------------------
// Retrieval. Hybrid: cosine similarity over precomputed nomic-embed-text
// vectors, blended with a BM25-lite lexical score so a distinctive proper noun
// like "Sperry" counts for something. Cosine alone ranks five parks' campfire
// rules as near neighbors.
// ---------------------------------------------------------------------------
static partial class Rag
{
    record Chunk(string chunk_id, string source, string text);
    public record Hit(string Id, string Text, double Score);

    const double Alpha = 0.6; // weight on the semantic signal; 1.0 = cosine only

    static async Task<List<Hit>> Retrieve(string question, int topK)
    {
        var chunks = File.ReadLines(Clients.Data("rag/chunks.jsonl"))
            .Select(line => JsonSerializer.Deserialize<Chunk>(line)!)
            .ToList();

        // Chunk vectors were embedded ahead of time; only the question is embedded live.
        var index = JsonSerializer.Deserialize<Dictionary<string, float[]>>(
            File.ReadAllText(Clients.Data("rag/chunk-embeddings.json")))!;
        var questionVector = (await Clients.Embedder().GenerateAsync(question)).Vector.ToArray();

        // Signal 1: semantic.
        var cosine = chunks.ToDictionary(
            c => c.chunk_id,
            c => (double)TensorPrimitives.CosineSimilarity(questionVector, index[c.chunk_id]));

        // Signal 2: lexical, BM25-lite. IDF makes a term in 1 of 250 chunks
        // worth far more than one in 200 of them.
        var tokenized = chunks.ToDictionary(c => c.chunk_id, c => Tokenize(c.text));
        var avgLength = tokenized.Values.Average(t => (double)t.Count);
        var docFreq = new Dictionary<string, int>();
        foreach (var terms in tokenized.Values)
            foreach (var term in terms.Distinct())
                docFreq[term] = docFreq.GetValueOrDefault(term) + 1;

        const double K1 = 1.2, B = 0.3;
        var n = chunks.Count;
        var queryTerms = Tokenize(question).Distinct().ToList();
        var idf = queryTerms.ToDictionary(
            t => t,
            t => Math.Log(1 + (n - docFreq.GetValueOrDefault(t) + 0.5) / (docFreq.GetValueOrDefault(t) + 0.5)));

        var lexical = new Dictionary<string, double>();
        foreach (var c in chunks)
        {
            var terms = tokenized[c.chunk_id];
            var counts = terms.GroupBy(t => t).ToDictionary(g => g.Key, g => (double)g.Count());
            var score = 0.0;
            foreach (var t in queryTerms)
            {
                if (!counts.TryGetValue(t, out var tf)) continue;
                score += idf[t] * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * terms.Count / avgLength));
            }
            lexical[c.chunk_id] = score;
        }

        // Rescale both signals to 0..1 so Alpha means what it looks like it means.
        var semanticNorm = MinMax(cosine);
        var lexicalNorm = MinMax(lexical);

        var top = chunks
            .Select(c => new Hit(c.chunk_id, c.text,
                Alpha * semanticNorm[c.chunk_id] + (1 - Alpha) * lexicalNorm[c.chunk_id]))
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();

        Console.WriteLine($"[retrieved top {topK}]");
        foreach (var h in top)
            Console.WriteLine($"  {h.Score:F3}  {h.Id}");
        Console.WriteLine();
        return top;
    }

    static Dictionary<string, double> MinMax(Dictionary<string, double> raw)
    {
        var min = raw.Values.Min();
        var range = raw.Values.Max() - min;
        return raw.ToDictionary(kv => kv.Key, kv => range > 1e-9 ? (kv.Value - min) / range : 0.0);
    }

    // Lowercase, split on non-alphanumerics, drop question filler, and knock the
    // plural off so "campfires" in a document matches "campfire" in a question.
    static List<string> Tokenize(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value)
            .Where(t => t.Length > 2 && !StopWords.Contains(t))
            .Select(t => t.Length > 3 && t.EndsWith('s') && !t.EndsWith("ss") ? t[..^1] : t)
            .ToList();

    // Question filler. Without this, "can" and "have" carry as much weight as
    // "Sperry" simply because no park document says "can I have".
    static readonly HashSet<string> StopWords =
    [
        "the", "and", "for", "are", "but", "not", "you", "your", "with", "that", "this", "these",
        "those", "from", "have", "has", "had", "was", "were", "been", "being", "can", "could",
        "will", "would", "shall", "should", "may", "might", "must", "does", "did", "doing",
        "what", "when", "where", "which", "who", "whom", "why", "how", "any", "all", "some",
        "there", "here", "then", "than", "them", "they", "their", "its", "his", "her", "our",
        "get", "got", "still", "now", "right", "just", "about", "into", "onto", "over", "under",
        "out", "off", "per", "via", "one", "two", "also", "more", "most", "much", "many", "each",
        "other", "such", "only", "own", "same", "too", "very", "let", "need", "want",
    ];

    // Any bracketed token containing a colon is a citation attempt, including
    // comma-separated lists. Generous on purpose: we want the near-misses.
    static List<string> Citations(string text) =>
        Regex.Matches(text, @"\[([^\]]*:[^\]]*)\]")
            .SelectMany(m => m.Groups[1].Value.Split(','))
            .Select(c => c.Trim())
            .Where(c => c.Contains(':'))
            .ToList();

    // Print the answer as it arrives, and hand back the whole thing.
    static async Task<string> Stream(IChatClient llm, string prompt)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var update in llm.GetStreamingResponseAsync(prompt))
        {
            Console.Write(update.Text);
            text.Append(update.Text);
        }
        Console.WriteLine();
        return text.ToString();
    }
}
