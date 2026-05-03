using System.Buffers.Binary;
using System.Text.RegularExpressions;
using JojoExtractor.Pac;

namespace JojoExtractor.Psx;

public readonly record struct KplnRenderContext(
    int FrameIndex,
    int ClutBase,
    int ClutRowBase,
    int ClutMode,
    int RenderMode,
    int Orientation,
    int? AssetSlot,
    string Source,
    int Score);

public static class KplnRenderContextFinder
{
    private const uint PlayerOverlayLoadAddress = 0x800df000;
    private const uint AnimationInterpreterAddress = 0x800268fc;
    private const int OverlayPointerScanLength = 0x1000;
    private const int ObjectWindowBytes = 0x180;
    private const int CompactWindowBytes = 0x80;

    public static IReadOnlyList<KplnRenderContext> FindContexts(
        PacFile kplnPac,
        string companionPath,
        int side = 0,
        ushort frameOpcode = KplnFramePreviewer.DefaultFrameOpcode)
    {
        if (!File.Exists(companionPath))
            throw new FileNotFoundException("Companion file not found.", companionPath);

        byte[] data = File.ReadAllBytes(companionPath);
        int frameCount = KplnFramePreviewer.GetFrameCount(kplnPac, frameOpcode);
        var contexts = new List<KplnRenderContext>();
        int[] overlayFunctionStarts = GetOverlayFunctionStarts(data).ToArray();
        if (overlayFunctionStarts.Length >= 4)
        {
            contexts.AddRange(ScanOverlayStores(data, overlayFunctionStarts, frameCount, side, Path.GetFileName(companionPath)));
            contexts.AddRange(ScanAnimationScriptCalls(data, overlayFunctionStarts, frameCount, side, Path.GetFileName(companionPath)));
        }
        else
        {
            contexts.AddRange(ScanCompactRecords(data, frameCount, side, Path.GetFileName(companionPath)));
        }
        return contexts
            .OrderBy(context => context.FrameIndex)
            .ThenByDescending(context => context.Score)
            .ThenBy(context => context.Source, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyDictionary<int, KplnRenderContext> FindBestContexts(
        PacFile kplnPac,
        string companionPath,
        int side = 0,
        ushort frameOpcode = KplnFramePreviewer.DefaultFrameOpcode)
    {
        return FindContexts(kplnPac, companionPath, side, frameOpcode)
            .GroupBy(context => context.FrameIndex)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(context => context.Score).First());
    }

    public static string? TryFindDefaultCompanionPath(string kplnPacPath, int side = 0)
    {
        string fileName = Path.GetFileName(kplnPacPath);
        Match match = Regex.Match(fileName, @"^KPLN(?<id>[0-9A-F]{2})\.PAC$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        if (side is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(side), "Side must be 0 or 1.");

        DirectoryInfo? pDir = Directory.GetParent(Path.GetFullPath(kplnPacPath));
        DirectoryInfo? discRoot = pDir?.Parent;
        if (discRoot is null)
            return null;

        string id = match.Groups["id"].Value.ToUpperInvariant();
        string plPath = Path.Combine(discRoot.FullName, "M", side == 0 ? $"PL{id}.BIN" : $"PL{id}X.BIN");
        return File.Exists(plPath) ? plPath : null;
    }

    private static IEnumerable<KplnRenderContext> ScanCompactRecords(byte[] data, int frameCount, int side, string sourceName)
    {
        for (int offset = 0; offset <= data.Length - 0x28; offset++)
        {
            if (data[offset] == 0 || data[offset + 1] == 0)
                continue;

            int frameIndex = ReadU16(data, offset + 0x0e);
            if (frameIndex < 0 || frameIndex >= frameCount)
                continue;

            int clutMode = NormalizeSignedByte(data[offset + 0x06]);
            int clutBase = data[offset + 0x07];
            int clutRowBase = ReadU16(data, offset + 0x0c);
            int assetSlot = data[offset + 0x1d];
            int orientation = ComputeOrientation(data[offset + 0x1e], data[offset + 0x1f]);

            if (assetSlot != side && assetSlot != side + 2)
                continue;
            if (!IsKplnClutRow(clutRowBase))
                continue;

            int score = 30 + (assetSlot == side ? 4 : 2) + (clutRowBase >= KplnClutPreviewer.BaseVramY ? 2 : 0);
            yield return new KplnRenderContext(
                frameIndex,
                clutBase,
                clutRowBase,
                clutMode,
                RenderMode: 0,
                orientation,
                assetSlot,
                $"{sourceName}:compact+0x{offset:X5}",
                score);
        }
    }

    private static IEnumerable<KplnRenderContext> ScanOverlayStores(byte[] data, IReadOnlyList<int> functionStarts, int frameCount, int side, string sourceName)
    {
        var hits = CollectStoreHits(data, functionStarts);
        foreach (StoreHit frameHit in hits.Where(hit => hit.Field == ContextField.Frame))
        {
            int frameIndex = frameHit.Value & 0x0fff;
            if (frameIndex < 0 || frameIndex >= frameCount)
                continue;

            int window = frameHit.IsObject ? ObjectWindowBytes : CompactWindowBytes;
            StoreHit[] nearby = hits
                .Where(hit => hit.IsObject == frameHit.IsObject &&
                              hit.BaseRegister == frameHit.BaseRegister &&
                              hit.FunctionOffset == frameHit.FunctionOffset &&
                              Math.Abs(hit.Offset - frameHit.Offset) <= window)
                .ToArray();

            int? assetSlot = NearestValue(nearby, ContextField.AssetSlot, frameHit.Offset);
            if (assetSlot is int slot && slot != side && (!frameHit.IsObject || slot != side + 2))
                continue;

            int clutBase = NearestValue(nearby, ContextField.ClutBase, frameHit.Offset) ?? 0;
            int clutRowBase = NearestValue(nearby, ContextField.ClutRowBase, frameHit.Offset) ?? (KplnFramePreviewer.DefaultClutBaseY + side);
            int clutMode = NormalizeSignedByte(NearestValue(nearby, ContextField.ClutMode, frameHit.Offset) ?? 0);
            int renderMode = frameHit.IsObject
                ? NearestValue(nearby, ContextField.RenderMode, frameHit.Offset) ?? 0
                : 0;
            int flipA = NearestValue(nearby, ContextField.FlipA, frameHit.Offset) ?? 0;
            int flipB = NearestValue(nearby, ContextField.FlipB, frameHit.Offset) ?? 0;
            int orientation = ComputeOrientation(flipA, flipB);

            if (!IsKplnClutRow(clutRowBase))
                continue;

            int score = 20 + nearby.Select(hit => hit.Field).Distinct().Count();
            if (frameHit.IsObject)
                score += 8;
            if (assetSlot == side)
                score += 6;
            if (clutRowBase >= KplnClutPreviewer.BaseVramY)
                score += 2;

            yield return new KplnRenderContext(
                frameIndex,
                clutBase,
                clutRowBase,
                clutMode,
                renderMode,
                orientation,
                assetSlot,
                $"{sourceName}:overlay+0x{frameHit.FunctionOffset:X5}/store+0x{frameHit.Offset:X5}",
                score);
        }
    }

    private static IEnumerable<KplnRenderContext> ScanAnimationScriptCalls(byte[] data, IReadOnlyList<int> functionStarts, int frameCount, int side, string sourceName)
    {
        var hits = CollectStoreHits(data, functionStarts);
        foreach (AnimationCall call in CollectAnimationCalls(data, functionStarts))
        {
            if (call.ObjectBaseRegister is not int objectBaseRegister)
                continue;

            StoreHit[] nearby = hits
                .Where(hit => hit.IsObject &&
                              hit.BaseRegister == objectBaseRegister &&
                              hit.FunctionOffset == call.FunctionOffset &&
                              Math.Abs(hit.Offset - call.Offset) <= ObjectWindowBytes)
                .ToArray();

            int? assetSlot = NearestValue(nearby, ContextField.AssetSlot, call.Offset);
            if (assetSlot is int slot && slot != side && slot != side + 2)
                continue;

            int clutBase = NearestValue(nearby, ContextField.ClutBase, call.Offset) ?? 0;
            if (NearestValue(nearby, ContextField.ClutRowBase, call.Offset) is not int clutRowBase)
                continue;

            int clutMode = NormalizeSignedByte(NearestValue(nearby, ContextField.ClutMode, call.Offset) ?? 0);
            int renderMode = NearestValue(nearby, ContextField.RenderMode, call.Offset) ?? 0;
            int flipA = NearestValue(nearby, ContextField.FlipA, call.Offset) ?? 0;
            int flipB = NearestValue(nearby, ContextField.FlipB, call.Offset) ?? 0;
            int orientation = ComputeOrientation(flipA, flipB);

            if (!IsKplnClutRow(clutRowBase))
                continue;

            int distinctFields = nearby.Select(hit => hit.Field).Distinct().Count();
            if (distinctFields < 2)
                continue;

            int score = 18 + distinctFields;
            if (assetSlot == side)
                score += 4;

            foreach (int frameIndex in ReadAnimationScriptFrames(data, call.ScriptAddress, frameCount).Distinct())
            {
                yield return new KplnRenderContext(
                    frameIndex,
                    clutBase,
                    clutRowBase,
                    clutMode,
                    renderMode,
                    orientation,
                    assetSlot,
                    $"{sourceName}:anim+0x{call.Offset:X5}/script+0x{call.ScriptAddress - PlayerOverlayLoadAddress:X5}",
                    score);
            }
        }
    }

    private static IReadOnlyList<StoreHit> CollectStoreHits(byte[] data, IReadOnlyList<int> functionStarts)
    {
        var hits = new List<StoreHit>();
        long[] registers = new long[32];
        bool[] known = new bool[32];
        int nextFunctionIndex = 0;
        int currentFunctionOffset = functionStarts[0];
        ResetRegisters(registers, known);

        for (int offset = 0; offset <= data.Length - 4; offset += 4)
        {
            if (nextFunctionIndex < functionStarts.Count && offset == functionStarts[nextFunctionIndex])
            {
                currentFunctionOffset = functionStarts[nextFunctionIndex];
                nextFunctionIndex++;
                ResetRegisters(registers, known);
            }

            uint word = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            int op = (int)(word >> 26);
            int rs = (int)((word >> 21) & 0x1f);
            int rt = (int)((word >> 16) & 0x1f);
            int rd = (int)((word >> 11) & 0x1f);
            int funct = (int)(word & 0x3f);
            int imm = (int)(word & 0xffff);
            int simm = (short)imm;

            if (op == 0)
            {
                switch (funct)
                {
                    case 0x21:
                        SetRegister(registers, known, rd, known[rs] && known[rt] ? registers[rs] + registers[rt] : null);
                        break;
                    case 0x25:
                        SetRegister(registers, known, rd, known[rs] && known[rt] ? registers[rs] | registers[rt] : null);
                        break;
                }
                continue;
            }

            switch (op)
            {
                case 0x09:
                    SetRegister(registers, known, rt, known[rs] ? registers[rs] + simm : null);
                    break;
                case 0x0d:
                    SetRegister(registers, known, rt, known[rs] ? registers[rs] | (long)imm : null);
                    break;
                case 0x0f:
                    SetRegister(registers, known, rt, (long)imm << 16);
                    break;
                case 0x20:
                case 0x21:
                case 0x23:
                case 0x24:
                case 0x25:
                    SetRegister(registers, known, rt, TryReadKnownLoad(data, registers, known, rs, simm, op));
                    break;
                case 0x28:
                case 0x29:
                case 0x2b:
                    AddStoreHit(hits, offset, currentFunctionOffset, op, rs, rt, simm, registers, known);
                    break;
            }
        }

        return hits;
    }

    private static IReadOnlyList<AnimationCall> CollectAnimationCalls(byte[] data, IReadOnlyList<int> functionStarts)
    {
        var calls = new List<AnimationCall>();
        long[] registers = new long[32];
        bool[] known = new bool[32];
        int?[] aliases = new int?[32];
        int nextFunctionIndex = 0;
        int currentFunctionOffset = functionStarts[0];
        ResetRegisters(registers, known, aliases);

        for (int offset = 0; offset <= data.Length - 4; offset += 4)
        {
            if (nextFunctionIndex < functionStarts.Count && offset == functionStarts[nextFunctionIndex])
            {
                currentFunctionOffset = functionStarts[nextFunctionIndex];
                nextFunctionIndex++;
                ResetRegisters(registers, known, aliases);
            }

            uint word = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            int op = (int)(word >> 26);
            int rs = (int)((word >> 21) & 0x1f);
            int rt = (int)((word >> 16) & 0x1f);
            int rd = (int)((word >> 11) & 0x1f);
            int shamt = (int)((word >> 6) & 0x1f);
            int funct = (int)(word & 0x3f);
            int imm = (int)(word & 0xffff);
            int simm = (short)imm;

            if (op == 0x03)
            {
                uint target = ((PlayerOverlayLoadAddress + (uint)offset + 4) & 0xf0000000) | ((word & 0x03ffffff) << 2);
                if (target == AnimationInterpreterAddress && known[5])
                    calls.Add(new AnimationCall(offset, currentFunctionOffset, (uint)registers[5], aliases[4]));
                continue;
            }

            if (op == 0)
            {
                switch (funct)
                {
                    case 0x00:
                        SetRegister(registers, known, aliases, rd, known[rt] ? registers[rt] << shamt : null, shamt == 0 ? aliases[rt] : null);
                        break;
                    case 0x21:
                        SetRegister(registers, known, aliases, rd, known[rs] && known[rt] ? registers[rs] + registers[rt] : null, rt == 0 ? aliases[rs] : null);
                        break;
                    case 0x25:
                        SetRegister(registers, known, aliases, rd, known[rs] && known[rt] ? registers[rs] | registers[rt] : null, null);
                        break;
                }
                continue;
            }

            switch (op)
            {
                case 0x09:
                    SetRegister(registers, known, aliases, rt, known[rs] ? registers[rs] + simm : null, simm == 0 ? aliases[rs] : null);
                    break;
                case 0x0d:
                    SetRegister(registers, known, aliases, rt, known[rs] ? registers[rs] | (long)imm : null, null);
                    break;
                case 0x0f:
                    SetRegister(registers, known, aliases, rt, (long)imm << 16, null);
                    break;
                case 0x20:
                case 0x21:
                case 0x23:
                case 0x24:
                case 0x25:
                    SetRegister(registers, known, aliases, rt, TryReadKnownLoad(data, registers, known, rs, simm, op), null);
                    break;
                case 0x28:
                case 0x29:
                case 0x2b:
                    break;
                default:
                    if (rt != 0)
                        SetRegister(registers, known, aliases, rt, null, null);
                    break;
            }
        }

        return calls;
    }

    private static IEnumerable<int> ReadAnimationScriptFrames(byte[] data, uint scriptAddress, int frameCount)
    {
        int offset = (int)(scriptAddress - PlayerOverlayLoadAddress);
        if (offset < 0 || offset >= data.Length)
            yield break;

        int visited = 0;
        while (offset + 4 <= data.Length && visited++ < 256)
        {
            byte command = data[offset];
            int length = command & 0x2f;
            if (length < 4)
                yield break;

            int frameIndex = ReadU16(data, offset + 2) & 0x0fff;
            if (frameIndex >= 0 && frameIndex < frameCount)
                yield return frameIndex;

            offset += length;
        }
    }

    private static void AddStoreHit(
        List<StoreHit> hits,
        int offset,
        int functionOffset,
        int op,
        int baseRegister,
        int valueRegister,
        int fieldOffset,
        long[] registers,
        bool[] known)
    {
        if (baseRegister == 29)
            return;

        if (!known[valueRegister])
            return;

        if (TryGetObjectField(fieldOffset, out ContextField objectField))
        {
            int value = NormalizeStoredValue(op, objectField, registers[valueRegister]);
            hits.Add(new StoreHit(offset, functionOffset, IsObject: true, baseRegister, objectField, value));
        }

        if (TryGetCompactField(fieldOffset, out ContextField compactField))
        {
            int value = NormalizeStoredValue(op, compactField, registers[valueRegister]);
            hits.Add(new StoreHit(offset, functionOffset, IsObject: false, baseRegister, compactField, value));
        }
    }

    private static IEnumerable<int> GetOverlayFunctionStarts(byte[] data)
    {
        uint endAddress = PlayerOverlayLoadAddress + (uint)data.Length;
        return Enumerable.Range(0, Math.Min(data.Length, OverlayPointerScanLength) / 4)
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4, 4)))
            .Where(pointer => pointer >= PlayerOverlayLoadAddress && pointer < endAddress)
            .Select(pointer => (int)(pointer - PlayerOverlayLoadAddress))
            .Distinct()
            .OrderBy(offset => offset)
            .ToArray();
    }

    private static int? NearestValue(IEnumerable<StoreHit> hits, ContextField field, int targetOffset)
    {
        StoreHit? hit = hits
            .Where(candidate => candidate.Field == field)
            .OrderBy(candidate => Math.Abs(candidate.Offset - targetOffset))
            .ThenByDescending(candidate => candidate.Offset)
            .Cast<StoreHit?>()
            .FirstOrDefault();
        return hit?.Value;
    }

    private static long? TryReadKnownLoad(byte[] data, long[] registers, bool[] known, int baseRegister, int offset, int op)
    {
        if (!known[baseRegister])
            return null;

        long address = registers[baseRegister] + offset;
        long dataOffset = address - PlayerOverlayLoadAddress;
        if (dataOffset < 0 || dataOffset >= data.Length)
            return null;

        int source = (int)dataOffset;
        return op switch
        {
            0x20 when source < data.Length => (sbyte)data[source],
            0x21 when source + 2 <= data.Length => (short)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source, 2)),
            0x23 when source + 4 <= data.Length => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(source, 4)),
            0x24 when source < data.Length => data[source],
            0x25 when source + 2 <= data.Length => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source, 2)),
            _ => null
        };
    }

    private static bool TryGetObjectField(int offset, out ContextField field)
    {
        field = offset switch
        {
            0x0b => ContextField.FlipA,
            0x10 => ContextField.Frame,
            0x12 => ContextField.FlipB,
            0x1d => ContextField.ClutMode,
            0x1e => ContextField.ClutBase,
            0x20 => ContextField.ClutRowBase,
            0xb0 => ContextField.RenderMode,
            0xb1 => ContextField.AssetSlot,
            _ => ContextField.Unknown
        };
        return field != ContextField.Unknown;
    }

    private static bool TryGetCompactField(int offset, out ContextField field)
    {
        field = offset switch
        {
            0x06 => ContextField.ClutMode,
            0x07 => ContextField.ClutBase,
            0x0c => ContextField.ClutRowBase,
            0x0e => ContextField.Frame,
            0x1d => ContextField.AssetSlot,
            0x1e => ContextField.FlipA,
            0x1f => ContextField.FlipB,
            _ => ContextField.Unknown
        };
        return field != ContextField.Unknown;
    }

    private static int NormalizeStoredValue(int op, ContextField field, long value)
    {
        int normalized = op switch
        {
            0x28 => (byte)value,
            0x29 => (ushort)value,
            _ => (int)value
        };

        if (field == ContextField.ClutMode)
            return NormalizeSignedByte(normalized);

        if (field is ContextField.Frame or ContextField.ClutBase or ContextField.ClutRowBase)
            return normalized & 0xffff;

        return normalized & 0xff;
    }

    private static int ComputeOrientation(int flipA, int flipB)
    {
        return (((flipA & 1) ^ (flipB & 1)) * 2) + ((flipB & 2) >> 1);
    }

    private static bool IsKplnClutRow(int clutRowBase)
    {
        return clutRowBase >= KplnClutPreviewer.BaseVramY &&
               clutRowBase < KplnClutPreviewer.BaseVramY + KplnClutPreviewer.PreviewRows;
    }

    private static int NormalizeSignedByte(int value)
    {
        value &= 0xff;
        return value >= 0x80 ? value - 0x100 : value;
    }

    private static int ReadU16(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    private static void ResetRegisters(long[] registers, bool[] known)
    {
        Array.Clear(registers);
        Array.Clear(known);
        known[0] = true;
    }

    private static void ResetRegisters(long[] registers, bool[] known, int?[] aliases)
    {
        ResetRegisters(registers, known);
        Array.Clear(aliases);
        for (int i = 1; i < aliases.Length; i++)
            aliases[i] = i;
    }

    private static void SetRegister(long[] registers, bool[] known, int register, long? value)
    {
        if (register == 0)
            return;

        if (value is long knownValue)
        {
            registers[register] = knownValue;
            known[register] = true;
        }
        else
        {
            registers[register] = 0;
            known[register] = false;
        }
    }

    private static void SetRegister(long[] registers, bool[] known, int?[] aliases, int register, long? value, int? alias)
    {
        SetRegister(registers, known, register, value);
        if (register != 0)
            aliases[register] = alias;
    }

    private readonly record struct StoreHit(
        int Offset,
        int FunctionOffset,
        bool IsObject,
        int BaseRegister,
        ContextField Field,
        int Value);

    private readonly record struct AnimationCall(
        int Offset,
        int FunctionOffset,
        uint ScriptAddress,
        int? ObjectBaseRegister);

    private enum ContextField
    {
        Unknown,
        Frame,
        ClutMode,
        ClutBase,
        ClutRowBase,
        RenderMode,
        AssetSlot,
        FlipA,
        FlipB
    }
}
