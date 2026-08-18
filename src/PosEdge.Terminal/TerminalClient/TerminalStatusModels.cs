namespace PosEdge.Terminal.Client;

public sealed record TerminalStatusDto(
    string StatusCode,
    string UserMessage,
    bool Blocking,
    string SuggestedAction);

