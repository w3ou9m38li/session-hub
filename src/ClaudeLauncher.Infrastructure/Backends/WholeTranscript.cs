namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// The transcripts read whole rather than line by line from an offset: a Gemini CLI session (one
/// JSON document written again whole) and a DeepSeek Harness session (a log of compressed
/// frames whose events refer back to each other). Everything that reads a transcript asks here
/// first, and pages, tails and indexes these from the entries they yield.
/// </summary>
public static class WholeTranscript
{
    public static bool Is(string? path) => GeminiSession.IsSessionFile(path) || DshSession.IsSessionFile(path);

    public static List<TranscriptEntry> ReadEntries(string path, bool keepImages, CancellationToken cancellation) =>
        DshSession.IsSessionFile(path)
            ? DshSession.ReadEntries(path, keepImages, cancellation)
            : GeminiSession.ReadEntries(path, keepImages, cancellation);

    public static List<TranscriptEntry> ReadDialogue(string path, CancellationToken cancellation) =>
        DshSession.IsSessionFile(path)
            ? DshSession.ReadDialogue(path, cancellation)
            : GeminiSession.ReadDialogue(path, cancellation);
}
