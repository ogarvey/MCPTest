import argparse
import json
import struct
from pathlib import Path

from extract_character_package_images import (
    ensure_clean_directory,
    palette_words_from_16bpp,
    png_chunk,
    write_png_from_indexed4_rgba,
    write_png_from_indexed8_rgba,
)


FIRST_STAGE_SIZE = 0x9000
ATLAS_OFFSET = 0x864
ATLAS_ROW_BYTES = 0x200
ATLAS_HEIGHT = 0x40
BASE_CLUT_OFFSET = 0x264
BASE_CLUT_SIZE = 0x200


def read_u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def build_enemy_package_from_wad(wad_data: bytes, package_offset: int) -> tuple[bytes, bytes, dict]:
    first_block = wad_data[package_offset:package_offset + FIRST_STAGE_SIZE]
    if len(first_block) != FIRST_STAGE_SIZE:
        raise ValueError(f"enemy package 0x{package_offset:08X} truncated first stage")

    header = struct.unpack_from("<6I", first_block, 0)
    continuation_size = header[3]
    continuation = wad_data[
        package_offset + FIRST_STAGE_SIZE:package_offset + FIRST_STAGE_SIZE + continuation_size
    ]
    if len(continuation) != continuation_size:
        raise ValueError(f"enemy package 0x{package_offset:08X} truncated continuation")

    metadata = {
        "package_offset": package_offset,
        "header": {
            "texture_table_offset": header[0],
            "render_group_table_offset": header[1],
            "aux_table_offset": header[2],
            "continuation_size": header[3],
            "field_10": header[4],
            "atlas_end_offset": header[5],
        },
        "texture_count": first_block[0x59],
        "render_group_count": first_block[0x5A],
        "atlas_offset": ATLAS_OFFSET,
        "atlas_row_bytes": ATLAS_ROW_BYTES,
        "atlas_height": ATLAS_HEIGHT,
        "palette_offset": BASE_CLUT_OFFSET,
    }
    return first_block, continuation, metadata


def parse_texture_descriptors(continuation: bytes, metadata: dict) -> list[dict]:
    descriptors = []
    table_offset = metadata["header"]["texture_table_offset"]
    for index in range(metadata["texture_count"]):
        entry_offset = table_offset + index * 0x10
        entry = continuation[entry_offset:entry_offset + 0x10]
        if len(entry) != 0x10:
            break

        descriptors.append(
            {
                "index": index,
                "entry_offset": entry_offset,
                "x": struct.unpack_from("<H", entry, 0)[0],
                "y": struct.unpack_from("<H", entry, 2)[0],
                "tpage_raw": struct.unpack_from("<H", entry, 4)[0],
                "clut_row_offset": entry[0x0A],
                "abr": entry[0x0B],
                "mode": entry[0x0D],
                "clut_x": entry[0x0E],
                "clut_y": entry[0x0F],
                "raw_hex": entry.hex(),
            }
        )

    return descriptors


def parse_render_groups(continuation: bytes, metadata: dict) -> list[dict]:
    groups = []
    table_offset = metadata["header"]["render_group_table_offset"]
    for index in range(metadata["render_group_count"]):
        entry_offset = table_offset + index * 0x38
        entry = continuation[entry_offset:entry_offset + 0x38]
        if len(entry) != 0x38:
            break

        record_pointer = read_u32(entry, 0)
        record_count = entry[0x27]
        group = {
            "index": index,
            "entry_offset": entry_offset,
            "record_pointer": record_pointer,
            "record_count": record_count,
            "record_start": entry[0x2C],
            "raw_hex": entry.hex(),
            "records": [],
        }

        for record_index in range(record_count):
            record_offset = record_pointer + record_index * 0x10
            record = continuation[record_offset:record_offset + 0x10]
            if len(record) != 0x10:
                break

            group["records"].append(
                {
                    "index": record_index,
                    "record_offset": record_offset,
                    "x": struct.unpack_from("<H", record, 0)[0],
                    "y": struct.unpack_from("<H", record, 2)[0],
                    "unknown_u16": struct.unpack_from("<H", record, 4)[0],
                    "abr": record[0x06],
                    "width": record[0x08],
                    "height": record[0x09],
                    "palette_selector": record[0x0A],
                    "unknown_0B": record[0x0B],
                    "unknown_0C": record[0x0C],
                    "mode": record[0x0D],
                    "clut_x": record[0x0E],
                    "clut_y": record[0x0F],
                    "raw_hex": record.hex(),
                }
            )

        groups.append(group)

    return groups


def get_enemy_fixed_palette_words(first_block: bytes) -> list[int]:
    raw_palette = first_block[BASE_CLUT_OFFSET:BASE_CLUT_OFFSET + BASE_CLUT_SIZE]
    if len(raw_palette) < BASE_CLUT_SIZE:
        return [0] * 256
    return palette_words_from_16bpp(raw_palette)


def crop_indexed8(atlas: bytes, x: int, y: int, width: int, height: int) -> bytes:
    cropped = bytearray()
    for row in range(height):
        start = (y + row) * ATLAS_ROW_BYTES + x
        cropped.extend(atlas[start:start + width])
    return bytes(cropped)


def crop_indexed4(atlas: bytes, x: int, y: int, width: int, height: int) -> tuple[bytes, int]:
    if x & 1:
        raise ValueError(f"indexed4 crop requires even x, got {x}")

    row_bytes = (width + 1) // 2
    byte_x = x // 2
    cropped = bytearray()
    for row in range(height):
        start = (y + row) * ATLAS_ROW_BYTES + byte_x
        cropped.extend(atlas[start:start + row_bytes])
    return bytes(cropped), row_bytes


def write_enemy_record_pngs(package_dir: Path, first_block: bytes, render_groups: list[dict]) -> list[dict]:
    atlas = first_block[ATLAS_OFFSET:ATLAS_OFFSET + ATLAS_ROW_BYTES * ATLAS_HEIGHT]
    palette_words = get_enemy_fixed_palette_words(first_block)
    outputs = []

    for group in render_groups:
        for record in group["records"]:
            x = record["x"]
            y = record["y"]
            width = record["width"]
            height = record["height"]
            mode = record["mode"]

            output_name = f"group_{group['index']:02d}_record_{record['index']:02d}.png"
            output_path = package_dir / output_name
            render_mode = "unknown"

            try:
                if mode == 1:
                    cropped = crop_indexed8(atlas, x, y, width, height)
                    write_png_from_indexed8_rgba(cropped, width, height, palette_words, output_path)
                    render_mode = "indexed8_fixedclut_base_0264_guess"
                else:
                    cropped, row_bytes = crop_indexed4(atlas, x, y, width, height)
                    palette16 = palette_words[:16]
                    write_png_from_indexed4_rgba(cropped, row_bytes, height, palette16, output_path)
                    render_mode = "indexed4_base_palette16_guess"
                error = None
            except Exception as exc:
                error = str(exc)

            record_output = {
                "group_index": group["index"],
                "record_index": record["index"],
                "x": x,
                "y": y,
                "width": width,
                "height": height,
                "mode": mode,
                "abr": record["abr"],
                "palette_selector": record["palette_selector"],
                "clut_x": record["clut_x"],
                "clut_y": record["clut_y"],
                "render_mode": render_mode,
            }
            if error is None:
                record_output["png_path"] = output_name
            else:
                record_output["error"] = error
            outputs.append(record_output)

    return outputs


def main() -> None:
    parser = argparse.ArgumentParser(description="Dump enemy-package render records from Disruptor WAD.IN")
    parser.add_argument("--wad", type=Path, default=Path("WAD.IN"), help="Path to WAD.IN")
    parser.add_argument(
        "--package-offset",
        type=lambda value: int(value, 0),
        default=0x00984800,
        help="File offset of a candidate enemy package",
    )
    parser.add_argument("--output", type=Path, default=Path("dumped_planes"), help="Output directory")
    parser.add_argument("--no-clean-output", action="store_true", help="Do not delete existing files in the package output directory before writing")
    args = parser.parse_args()

    wad_data = args.wad.read_bytes()
    first_block, continuation, metadata = build_enemy_package_from_wad(wad_data, args.package_offset)
    texture_descriptors = parse_texture_descriptors(continuation, metadata)
    render_groups = parse_render_groups(continuation, metadata)

    package_dir = args.output / f"enemy_package_{args.package_offset:08X}"
    if args.no_clean_output:
        package_dir.mkdir(parents=True, exist_ok=True)
    else:
        ensure_clean_directory(package_dir)

    record_outputs = write_enemy_record_pngs(package_dir, first_block, render_groups)

    metadata["texture_descriptors"] = texture_descriptors
    metadata["render_groups"] = render_groups
    metadata["record_outputs"] = record_outputs
    metadata_path = package_dir / "metadata.json"
    metadata_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")

    print(f"output_dir={package_dir}")
    print(
        f"texture_count={metadata['texture_count']} render_group_count={metadata['render_group_count']} "
        f"record_count={sum(len(group['records']) for group in render_groups)}"
    )
    for record in record_outputs:
        if "error" in record:
            print(
                f"group[{record['group_index']}] record[{record['record_index']}] "
                f"size={record['width']}x{record['height']} error={record['error']}"
            )
        else:
            print(
                f"group[{record['group_index']}] record[{record['record_index']}] "
                f"xy=({record['x']},{record['y']}) size={record['width']}x{record['height']} "
                f"mode={record['mode']} png={record['png_path']}"
            )


if __name__ == "__main__":
    main()
