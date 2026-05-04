using System.Text.Json;

namespace ZyCleaver;

internal static class PaletteHintCatalog
{
    private const string StageDataFileName = "PaletteHintStageData.json";

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<PaletteHintCandidate>>> StageCandidatesByCadName =
        new(LoadStageCandidatesByCadName);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PaletteHintCandidate>> ManualCandidatesByCadName =
        new Dictionary<string, IReadOnlyList<PaletteHintCandidate>>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] =
            [
                new PaletteHintCandidate(
                    "title.pal",
                    "literal palette load",
                    "Raw code at 0x00032c22 opens title.pal from zyclunt.jam, reads 0x300 bytes, then loads title.cad.")
            ],
            ["ending"] =
            [
                new PaletteHintCandidate(
                    "ending.pal",
                    "literal palette load",
                    "Raw code at 0x00035c76 opens ending.pal from zyclunt.jam, reads 0x300 bytes, then loads ending.cad.")
            ],
            ["endroll"] =
            [
                new PaletteHintCandidate(
                    "ending.pal",
                    "shared literal palette load",
                    "The ending sequence loads ending.pal once, then loads ending.cad and endroll.cad under that same palette setup.")
            ],
            ["sclear"] =
            [
                new PaletteHintCandidate(
                    "sclear.pal",
                    "literal palette load",
                    "Raw code at 0x000374c2 opens sclear.pal from zyclunt.jam, reads 0x300 bytes, then loads sclear.cad.")
            ],
            ["sou"] =
            [
                new PaletteHintCandidate(
                    "sclear.pal",
                    "shared literal palette load",
                    "The sclear sequence loads sclear.pal once, then loads sclear.cad followed by sou.cad under that same palette setup.")
            ],
            ["st1_le"] =
            [
                new PaletteHintCandidate(
                    "st1_h.pal",
                    "startup basename table",
                    "The static basename table at 0x00010010 contains st1_le, and the startup loader at 0x00010066..0x00010074 pairs major 0 with the sub-0 palette string at stage + 0x2b, which is st1_h.pal.")
            ],
            ["st2_le"] =
            [
                new PaletteHintCandidate(
                    "st2_s.pal",
                    "startup basename table",
                    "The static basename table at 0x00010018 contains st2_le, and the startup loader at 0x00010066..0x00010074 pairs major 1 with the sub-0 palette string at stage + 0x2b, which is st2_s.pal.")
            ],
            ["st3_le"] =
            [
                new PaletteHintCandidate(
                    "st3_s.pal",
                    "startup basename table",
                    "The static basename table at 0x00010020 contains st3_le, and the startup loader at 0x00010066..0x00010074 pairs major 2 with the sub-0 palette string at stage + 0x2b, which is st3_s.pal.")
            ],
            ["st4_le"] =
            [
                new PaletteHintCandidate(
                    "st4_s.pal",
                    "startup basename table",
                    "The static basename table at 0x00010028 contains st4_le, and the startup loader at 0x00010066..0x00010074 pairs major 3 with the sub-0 palette string at stage + 0x2b, which is st4_s.pal.")
            ],
            ["st5_le"] =
            [
                new PaletteHintCandidate(
                    "st5_s.pal",
                    "startup basename table",
                    "The static basename table at 0x00010030 contains st5_le, and the startup loader at 0x00010066..0x00010074 pairs major 4 with the sub-0 palette string at stage + 0x2b, which is st5_s.pal.")
            ],
            ["st6_le"] =
            [
                new PaletteHintCandidate(
                    "st6_s.pal",
                    "startup basename table",
                    "The static basename table at 0x00010038 contains st6_le, and the startup loader at 0x00010066..0x00010074 pairs major 5 with the sub-0 palette string at stage + 0x2b, which is st6_s.pal.")
            ]
        };

    public static IReadOnlyList<PaletteHintCandidate> GetCandidates(string cadName)
    {
        List<PaletteHintCandidate>? candidates = null;

        if (StageCandidatesByCadName.Value.TryGetValue(cadName, out var stageCandidates))
        {
            candidates = [.. stageCandidates];
        }

        if (ManualCandidatesByCadName.TryGetValue(cadName, out var manualCandidates))
        {
            candidates ??= [];

            foreach (var candidate in manualCandidates)
            {
                if (!candidates.Contains(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        return candidates is null ? Array.Empty<PaletteHintCandidate>() : candidates;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<PaletteHintCandidate>> LoadStageCandidatesByCadName()
    {
        var stageDataPath = ResolveStageDataPath();

        if (stageDataPath is null)
        {
            return new Dictionary<string, IReadOnlyList<PaletteHintCandidate>>(StringComparer.OrdinalIgnoreCase);
        }

        using var stream = File.OpenRead(stageDataPath);
        var stagePaletteData = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)
            ?? new Dictionary<string, string[]>();
        var result = new Dictionary<string, IReadOnlyList<PaletteHintCandidate>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (cadName, paletteFiles) in stagePaletteData)
        {
            result[cadName] = [.. paletteFiles.Select(paletteFile => new PaletteHintCandidate(
                paletteFile,
                "stage table",
                "Recovered from the stage subtable palette field at +0x2b."))];
        }

        return result;
    }

    private static string? ResolveStageDataPath()
    {
        var projectStageDataPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", StageDataFileName));

        if (File.Exists(projectStageDataPath))
        {
            return projectStageDataPath;
        }

        var outputStageDataPath = Path.Combine(AppContext.BaseDirectory, StageDataFileName);
        return File.Exists(outputStageDataPath) ? outputStageDataPath : null;
    }
}

internal sealed record PaletteHintCandidate(string PaletteFileName, string Source, string Evidence);
