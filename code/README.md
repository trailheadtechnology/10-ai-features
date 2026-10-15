# Session Demos

One .NET 10 console app, four demos. Local models run on Ollama; RAG generation and the agent run on Azure OpenAI (Microsoft Foundry).

## Prerequisites

Ollama running locally, with these models:

```bash
ollama pull llama3.2
ollama pull nomic-embed-text
```

The Foundry key, for `rag` and `agent`. It is stored in .NET user-secrets under your home folder, never in this folder, so git cannot pick it up (this code goes up on a projector):

```bash
./key.sh set       # paste the key at the hidden prompt
./key.sh status    # is it set? (never prints it)
./key.sh remove    # after the talk
```

`AZURE_OPENAI_KEY` in the environment works too.

## Run

```bash
dotnet run -- extract            # F02: 20 trip reports to JSON, rejects in red (llama3.2, ~30 s)
dotnet run -- rag --ungrounded   # F05 before: the question with no documents (gpt-4.1)
dotnet run -- rag                # F05 after: retrieved, grounded, cited (nomic-embed-text + gpt-4.1)
dotnet run -- triage             # F07: 20 inquiries routed and scored (llama3.2)
dotnet run -- agent --no-gate    # F10 before: the agent files the permit on its own (gpt-5.5)
dotnet run -- agent              # F10 after: the permit waits for a human y/N (gpt-5.5)
```

`rag` and `agent` take your own question or request as extra words, e.g. `dotnet run -- rag "Are there EV charging stations in Glacier National Park?"`.

Before going on stage: `./pretalk.sh` checks Ollama, the models, the key, the Azure endpoint, and the build, then loads the local models and keeps them loaded (`./pretalk.sh unload` afterward).

Backup for the agent demo: `script -p backup/agent.rec` (or `backup/agent-no-gate.rec`) replays a real run in the terminal at real speed. No network needed.
