using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;

// Every model in the talk comes through here, and every demo talks to the same
// IChatClient interface. Local or cloud is which method you call, nothing more.
static class Clients
{
    // Ollama on this laptop: free, private, small.
    public static IChatClient Local(string model = "llama3.2") =>
        new OllamaApiClient(new Uri("http://localhost:11434"), model);

    // Azure OpenAI on Microsoft Foundry: same interface, different constructor.
    public static IChatClient Cloud(string deployment) =>
        new AzureOpenAIClient(new Uri(Endpoint), new ApiKeyCredential(Key))
            .GetChatClient(deployment)
            .AsIChatClient();

    // Embeddings stay local in every demo.
    public static IEmbeddingGenerator<string, Embedding<float>> Embedder() =>
        new OllamaApiClient(new Uri("http://localhost:11434"), "nomic-embed-text");

    const string Endpoint = "https://trailhead-ai-workshop.openai.azure.com";

    // The key never lives in source, because this code goes up on a projector.
    // `dotnet user-secrets set Foundry:Key <key>` first, AZURE_OPENAI_KEY second.
    static string Key
    {
        get
        {
            var config = new ConfigurationBuilder()
                .AddUserSecrets(typeof(Clients).Assembly, optional: true)
                .AddEnvironmentVariables()
                .Build();
            var key = config["Foundry:Key"] ?? config["AZURE_OPENAI_KEY"];
            return string.IsNullOrWhiteSpace(key)
                ? throw new InvalidOperationException(
                    "No Foundry key. Run: dotnet user-secrets set Foundry:Key <key>")
                : key;
        }
    }

    // Data is copied next to the binary at build time (see Demos.csproj).
    public static string Data(string path) =>
        Path.Combine(AppContext.BaseDirectory, "data", path);
}
