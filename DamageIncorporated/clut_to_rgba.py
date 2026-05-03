import argparse
import csv
import json
from pathlib import Path


CLUT_TAG = b"clut"
PAYLOAD_SIZE = 0x606
HEADER_SIZE = 6
ENTRY_SIZE = 6
ENTRY_COUNT = 256


def parse_int(value: str) -> int:
    return int(value, 0)


def find_clut_offsets(data: bytes) -> list[int]:
    offsets: list[int] = []
    start = 0
    while True:
        index = data.find(CLUT_TAG, start)
        if index == -1:
            return offsets
        offsets.append(index)
        start = index + 1


def printable_runs(data: bytes, min_run: int = 8) -> int:
    run = 0
    runs = 0
    for value in data:
        is_printable = 0x20 <= value <= 0x7E
        if is_printable:
            run += 1
        else:
            if run >= min_run:
                runs += 1
            run = 0
    if run >= min_run:
        runs += 1
    return runs


def score_payload_candidate(payload: bytes) -> tuple[float, float, int]:
    entries = payload[HEADER_SIZE:HEADER_SIZE + ENTRY_SIZE * ENTRY_COUNT]
    if not entries:
        return (float("inf"), 1.0, 999)

    printable_ratio = sum(1 for value in entries if 0x20 <= value <= 0x7E) / len(entries)
    runs = printable_runs(entries)
    score = printable_ratio * 100.0 + runs * 25.0
    return score, printable_ratio, runs


def find_payload_rel_candidates(data: bytes, tag_offset: int) -> list[int]:
    rels: set[int] = set()
    rels.update({4, 6, 8, 10, 12, 14, 16, 20, 24, 28, 32})

    tag_scan_end = min(len(data), tag_offset + 48)
    for rel in range(4, tag_scan_end - tag_offset - 3):
        raw = data[tag_offset + rel:tag_offset + rel + 4]
        be = int.from_bytes(raw, "big")
        le = int.from_bytes(raw, "little")
        if be == PAYLOAD_SIZE or le == PAYLOAD_SIZE:
            rels.add(rel + 4)

    valid = sorted(rel for rel in rels if tag_offset + rel + PAYLOAD_SIZE <= len(data))
    return valid


def auto_select_payload_offset(data: bytes, tag_offset: int) -> tuple[int, list[dict]]:
    candidates = find_payload_rel_candidates(data, tag_offset)
    if not candidates:
        raise ValueError("Could not find any valid payload start candidate near 'clut' tag")

    scored: list[dict] = []
    for rel in candidates:
        start = tag_offset + rel
        payload = data[start:start + PAYLOAD_SIZE]
        score, printable_ratio, runs = score_payload_candidate(payload)
        scored.append(
            {
                "rel": rel,
                "start": start,
                "score": score,
                "printable_ratio": printable_ratio,
                "printable_runs": runs,
                "header_hex": payload[:HEADER_SIZE].hex(" "),
            }
        )

    scored.sort(key=lambda item: item["score"])
    best = scored[0]
    return best["start"], scored


def get_payload_from_tag(
    data: bytes,
    tag_offset: int,
    tag_payload_offset: int | None,
    auto_detect: bool,
) -> tuple[bytes, int]:
    if tag_payload_offset is not None:
        payload_offset = tag_offset + tag_payload_offset
    elif auto_detect:
        payload_offset, scored = auto_select_payload_offset(data, tag_offset)
        print("Auto payload-start candidates (best first):")
        for item in scored[:8]:
            print(
                f"  rel=0x{item['rel']:X} start=0x{item['start']:X} "
                f"score={item['score']:.2f} printable={item['printable_ratio']:.3f} "
                f"runs={item['printable_runs']} header={item['header_hex']}"
            )
    else:
        payload_offset = tag_offset + len(CLUT_TAG)

    end = payload_offset + PAYLOAD_SIZE
    if end > len(data):
        raise ValueError("Not enough bytes after 'clut' marker for 0x606 payload")
    return data[payload_offset:end], payload_offset


def get_payload_from_raw_offset(data: bytes, payload_offset: int) -> tuple[bytes, int]:
    end = payload_offset + PAYLOAD_SIZE
    if end > len(data):
        raise ValueError("Not enough bytes for 0x606 payload at requested offset")
    return data[payload_offset:end], payload_offset


def decode_channel(value16: int, mode: str) -> int:
    if mode == "high":
        return (value16 >> 8) & 0xFF
    if mode == "low":
        return value16 & 0xFF
    if mode == "scale":
        return round((value16 / 65535.0) * 255.0)
    raise ValueError(f"Unsupported channel mode: {mode}")


def decode_payload(
    payload: bytes,
    word_endian: str,
    channel_mode: str,
    alpha: int,
    force_full_256: bool,
    entry_data_offset: int,
    count_offset: int,
    count_size: int,
    count_endian: str,
    verbose: bool = True,
) -> tuple[list[dict], int, int]:
    header = payload[:HEADER_SIZE]

    if entry_data_offset < 0 or entry_data_offset >= len(payload):
        raise ValueError("--entry-data-offset is out of payload range")
    if count_size not in (1, 2):
        raise ValueError("--count-size must be 1 or 2")
    if count_offset < 0 or count_offset + count_size > len(payload):
        raise ValueError("Count field is out of payload range")

    byteorder = "little" if word_endian == "little" else "big"
    count_byteorder = "little" if count_endian == "little" else "big"

    declared_count = int.from_bytes(
        payload[count_offset:count_offset + count_size],
        count_byteorder,
    )

    available_entries = max(0, (len(payload) - entry_data_offset) // ENTRY_SIZE)
    if force_full_256:
        effective_count = min(ENTRY_COUNT, available_entries)
    else:
        effective_count = max(0, min(declared_count, ENTRY_COUNT, available_entries))

    palette: list[dict] = []
    for index in range(effective_count):
        base = entry_data_offset + index * ENTRY_SIZE
        triplet = payload[base:base + ENTRY_SIZE]
        r16 = int.from_bytes(triplet[0:2], byteorder)
        g16 = int.from_bytes(triplet[2:4], byteorder)
        b16 = int.from_bytes(triplet[4:6], byteorder)

        palette.append(
            {
                "index": index,
                "r16": r16,
                "g16": g16,
                "b16": b16,
                "r": decode_channel(r16, channel_mode),
                "g": decode_channel(g16, channel_mode),
                "b": decode_channel(b16, channel_mode),
                "a": alpha,
            }
        )

    header_words_be = [int.from_bytes(header[i:i + 2], "big") for i in (0, 2, 4)]
    header_words_le = [int.from_bytes(header[i:i + 2], "little") for i in (0, 2, 4)]

    if verbose:
        print("CLUT header raw:", header.hex(" "))
        print("CLUT header words (BE):", [f"0x{value:04X}" for value in header_words_be])
        print("CLUT header words (LE):", [f"0x{value:04X}" for value in header_words_le])

    used_bytes = entry_data_offset + effective_count * ENTRY_SIZE
    trailing_bytes = max(0, PAYLOAD_SIZE - used_bytes)

    if verbose:
        print(f"Declared palette count (count field): {declared_count} (0x{declared_count:04X})")
        print(f"Effective decoded entries: {effective_count}")
        print(
            f"Entry start offset: 0x{entry_data_offset:X}  "
            f"Count field: off=0x{count_offset:X} size={count_size} endian={count_endian}"
        )
        print(f"Used bytes: 0x{used_bytes:X}  Trailing bytes in 0x606 payload: 0x{trailing_bytes:X}")

    return palette, declared_count, effective_count


def write_json(path: Path, palette: list[dict]) -> None:
    path.write_text(json.dumps(palette, indent=2), encoding="utf-8")


def write_csv(path: Path, palette: list[dict]) -> None:
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=["index", "r", "g", "b", "a", "r16", "g16", "b16"])
        writer.writeheader()
        writer.writerows(palette)


def write_gpl(path: Path, palette: list[dict], name: str = "DamageIncorporated_CLUT") -> None:
    lines = [
        "GIMP Palette",
        f"Name: {name}",
        "Columns: 16",
        "#",
    ]
    for entry in palette:
        lines.append(f"{entry['r']:3d} {entry['g']:3d} {entry['b']:3d} Index_{entry['index']:03d}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def write_rgb_bin(path: Path, palette: list[dict]) -> None:
    data = bytearray()
    for entry in palette:
        data.extend((entry["r"], entry["g"], entry["b"]))
    path.write_bytes(bytes(data))


def main() -> None:
    parser = argparse.ArgumentParser(description="Convert Damage Incorporated CLUT palette data to RGBA.")
    parser.add_argument("input", help="Input binary file")
    group = parser.add_mutually_exclusive_group(required=False)
    group.add_argument("--tag-offset", type=parse_int, help="Offset of ASCII 'clut' tag")
    group.add_argument("--payload-offset", type=parse_int, help="Offset of raw 0x606 CLUT payload")
    parser.add_argument("--find-first-tag", action="store_true", help="Auto-locate first 'clut' tag")
    parser.add_argument(
        "--tag-payload-offset",
        type=parse_int,
        help="Offset from clut tag to the first byte of the 0x606 payload (e.g. 0x10)",
    )
    parser.add_argument(
        "--no-auto-tag-layout",
        action="store_true",
        help="Disable automatic payload-start detection and use tag+4 default (legacy behavior)",
    )
    parser.add_argument(
        "--allow-heuristic-offset",
        action="store_true",
        help="Allow heuristic payload-start selection near 'clut' (disabled by default for strict RE workflow)",
    )
    parser.add_argument("--word-endian", choices=["little", "big"], default="little", help="Endian of 16-bit channel words")
    parser.add_argument(
        "--channel-mode",
        choices=["high", "low", "scale"],
        default="high",
        help="How to derive 8-bit channel from 16-bit source",
    )
    parser.add_argument("--alpha", type=parse_int, default=255, help="Alpha value for output RGBA entries")
    parser.add_argument("--out-json", help="Write palette entries to JSON")
    parser.add_argument("--out-csv", help="Write palette entries to CSV")
    parser.add_argument("--out-gpl", help="Write palette to GIMP palette (.gpl)")
    parser.add_argument("--out-rgb-bin", help="Write RGB bytes (3 bytes/entry) to binary file")
    parser.add_argument(
        "--dump-all-tag-rgb-dir",
        help="Decode every 'clut' tag in input and write one RGB binary file per palette to this directory",
    )
    parser.add_argument("--print-first", type=int, default=16, help="Print first N RGBA entries to console")
    parser.add_argument(
        "--force-full-256",
        action="store_true",
        help="Decode all 256 entries regardless of header count word (engine-default behavior is to honor count)",
    )
    parser.add_argument(
        "--entry-data-offset",
        type=parse_int,
        default=HEADER_SIZE,
        help="Offset within 0x606 payload where first 6-byte RGB16 entry begins (default: 0x6)",
    )
    parser.add_argument(
        "--count-offset",
        type=parse_int,
        default=0,
        help="Offset within 0x606 payload of palette count field (default: 0)",
    )
    parser.add_argument(
        "--count-size",
        type=int,
        choices=[1, 2],
        default=2,
        help="Palette count field width in bytes (default: 2)",
    )
    parser.add_argument(
        "--count-endian",
        choices=["little", "big", "word"],
        default="word",
        help="Endian for count field; 'word' means use --word-endian",
    )

    args = parser.parse_args()

    data = Path(args.input).read_bytes()

    resolved_count_endian = args.word_endian if args.count_endian == "word" else args.count_endian

    if args.dump_all_tag_rgb_dir:
        if args.payload_offset is not None:
            raise ValueError("--dump-all-tag-rgb-dir cannot be combined with --payload-offset")

        offsets = find_clut_offsets(data)
        if not offsets:
            raise ValueError("No 'clut' marker found in file")

        if args.tag_payload_offset is None and not args.allow_heuristic_offset and not args.no_auto_tag_layout:
            raise ValueError(
                "Strict mode (batch): provide --tag-payload-offset. "
                "Use --allow-heuristic-offset only if you explicitly want heuristic detection."
            )

        out_dir = Path(args.dump_all_tag_rgb_dir)
        out_dir.mkdir(parents=True, exist_ok=True)

        print(f"Found {len(offsets)} clut tags")
        written = 0
        for index, tag_offset in enumerate(offsets):
            payload, payload_offset = get_payload_from_tag(
                data,
                tag_offset,
                args.tag_payload_offset,
                auto_detect=args.allow_heuristic_offset and not args.no_auto_tag_layout,
            )

            palette, declared_count, effective_count = decode_payload(
                payload,
                args.word_endian,
                args.channel_mode,
                args.alpha,
                args.force_full_256,
                args.entry_data_offset,
                args.count_offset,
                args.count_size,
                resolved_count_endian,
                verbose=False,
            )

            out_file = out_dir / (
                f"palette_{index:03d}_tag_{tag_offset:08X}_payload_{payload_offset:08X}_"
                f"entries_{effective_count:03d}.rgb"
            )
            write_rgb_bin(out_file, palette)
            print(
                f"[{index:03d}] tag=0x{tag_offset:X} payload=0x{payload_offset:X} "
                f"declared={declared_count} effective={effective_count} -> {out_file.name}"
            )
            written += 1

        print(f"Wrote {written} RGB binary palette files to {out_dir}")
        return

    if args.payload_offset is not None:
        payload, payload_offset = get_payload_from_raw_offset(data, args.payload_offset)
    else:
        if args.tag_offset is not None:
            tag_offset = args.tag_offset
        elif args.find_first_tag:
            offsets = find_clut_offsets(data)
            if not offsets:
                raise ValueError("No 'clut' marker found in file")
            tag_offset = offsets[0]
        else:
            offsets = find_clut_offsets(data)
            if len(offsets) == 1:
                tag_offset = offsets[0]
            elif len(offsets) > 1:
                raise ValueError("Multiple 'clut' markers found, use --tag-offset")
            else:
                raise ValueError("No 'clut' marker found; use --payload-offset for raw CLUT payload")

        if args.tag_payload_offset is None and not args.allow_heuristic_offset and not args.no_auto_tag_layout:
            raise ValueError(
                "Strict mode: provide --tag-payload-offset (or --payload-offset). "
                "Use --allow-heuristic-offset only if you explicitly want heuristic detection."
            )

        payload, payload_offset = get_payload_from_tag(
            data,
            tag_offset,
            args.tag_payload_offset,
            auto_detect=args.allow_heuristic_offset and not args.no_auto_tag_layout,
        )
        print(f"Using clut tag at 0x{tag_offset:X}; payload starts at 0x{payload_offset:X}")

    if not (0 <= args.alpha <= 255):
        raise ValueError("--alpha must be between 0 and 255")

    palette, declared_count, effective_count = decode_payload(
        payload,
        args.word_endian,
        args.channel_mode,
        args.alpha,
        args.force_full_256,
        args.entry_data_offset,
        args.count_offset,
        args.count_size,
        resolved_count_endian,
    )

    print(
        f"Decoded {len(palette)} entries using word_endian={args.word_endian}, "
        f"channel_mode={args.channel_mode}, declared_count={declared_count}, effective_count={effective_count}"
    )
    preview_count = max(0, min(args.print_first, len(palette)))
    for entry in palette[:preview_count]:
        print(
            f"{entry['index']:03d}: RGBA({entry['r']:3d}, {entry['g']:3d}, {entry['b']:3d}, {entry['a']:3d}) "
            f"from ({entry['r16']:04X}, {entry['g16']:04X}, {entry['b16']:04X})"
        )

    if args.out_json:
        write_json(Path(args.out_json), palette)
    if args.out_csv:
        write_csv(Path(args.out_csv), palette)
    if args.out_gpl:
        write_gpl(Path(args.out_gpl), palette)
    if args.out_rgb_bin:
        write_rgb_bin(Path(args.out_rgb_bin), palette)


if __name__ == "__main__":
    main()
