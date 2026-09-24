# Session Demos

One .NET 10 console app, four demos. Local models run on Ollama; RAG generation and the agent run on Azure OpenAI (Microsoft Foundry).

## Prerequisites

Ollama running locally, with these models:

```bash
ollama pull llama3.2
ollama pull nomic-embed-text
```

The Foundry key, stored outside the source tree (this code goes up on a projector):

```bash
dotnet user-secrets set Foundry:Key <KEY>
```

`AZURE_OPENAI_KEY` in the environment works too.

## Run

```bash
dotnet run -- extract            # F02: two trip reports in, validated records out (llama3.2)
dotnet run -- rag --ungrounded   # F05 before: the question with no documents (gpt-4.1)
dotnet run -- rag                # F05 after: retrieved, grounded, cited (nomic-embed-text + gpt-4.1)
dotnet run -- triage             # F07: 20 inquiries routed and scored (llama3.2)
dotnet run -- agent --no-gate    # F10 before: the agent files the permit on its own (gpt-5.5)
dotnet run -- agent              # F10 after: the permit waits for a human y/N (gpt-5.5)
```

`rag` and `agent` take your own question or request as extra words, e.g. `dotnet run -- rag "Are there EV charging stations in Glacier National Park?"`.

Warm the local models before going on stage: run `extract` and `triage` once.
