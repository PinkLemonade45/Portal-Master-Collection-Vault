using System.Collections.Generic;

namespace SkylandersCollection.Desktop;

internal sealed class FigureDiagnosticDump
{
    public int ToyIndex { get; init; }
    public string? ToyIndexHex { get; init; }
    public int ToyId { get; init; }
    public string? ToyIdHex { get; init; }
    public int VariantId { get; init; }
    public string? VariantIdHex { get; init; }
    public string? Block0 { get; set; }
    public string? Block1 { get; set; }
    public string? FigureUid { get; set; }
    public IReadOnlyList<FigureBlockDump>? Blocks { get; set; }
}
