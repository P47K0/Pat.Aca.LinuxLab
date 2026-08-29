namespace Pat.Aca.LinuxLab.Api.Models;

/// <summary>
/// A single simulator step, reported by the shims in simulator/bin (via
/// simulator/lib.sh's lab::progress) and relayed to the browser's live
/// checklist. See the BRD's §06.
/// </summary>
public sealed record ProgressEvent(string Step, string Status, string Message);
