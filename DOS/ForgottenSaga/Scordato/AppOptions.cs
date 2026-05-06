sealed class AppOptions
{
	public required List<string> InputPaths { get; init; }
	public string? PalettePath { get; init; }
	public string? ForgaSceneName { get; init; }
	public bool WriteBinaryOutputs { get; init; }
	public bool WriteStripOutputs { get; init; }
	public bool WriteSequenceRelativeOutputs { get; init; }
	public required List<string> FamProbeSceneNames { get; init; }
	public bool ProbeAllFamScenes { get; init; }
}
