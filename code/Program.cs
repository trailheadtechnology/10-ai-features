// Four demos from the talk, one per command:
//   dotnet run -- extract                  trip report in, validated record out
//   dotnet run -- rag --ungrounded         the question, no documents: it cannot know
//   dotnet run -- rag ["question"]         the same question, grounded and cited
//   dotnet run -- triage                   20 inquiries routed, emergency recall scored
//   dotnet run -- agent --no-gate          the agent files the permit on its own say-so
//   dotnet run -- agent ["request"]        the same agent, a human approves the permit

var flags = args.Where(a => a.StartsWith("--")).ToHashSet();
var text = string.Join(" ", args.Skip(1).Where(a => !a.StartsWith("--")));

switch (args.FirstOrDefault())
{
    case "extract": await Extract.Run(); break;
    case "rag": await Rag.Run(text, grounded: !flags.Contains("--ungrounded")); break;
    case "triage": await Triage.Run(); break;
    case "agent": await Agent.Run(text, gate: !flags.Contains("--no-gate")); break;
    default:
        Console.WriteLine("usage: dotnet run -- extract | rag [--ungrounded] | triage | agent [--no-gate]");
        break;
}
