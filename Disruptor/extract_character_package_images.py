import argparse
import json
import struct
import shutil
import zlib
from pathlib import Path


def read_u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def read_i8(data: memoryview, offset: int) -> int:
    return struct.unpack_from("<b", data, offset)[0]


def align4(value: int) -> int:
    return (value + 3) & ~3


def psx_555_to_rgb(value: int) -> tuple[int, int, int]:
    red = (value >> 0) & 0x1F
    green = (value >> 5) & 0x1F
    blue = (value >> 10) & 0x1F
    return (
        (red << 3) | (red >> 2),
        (green << 3) | (green >> 2),
        (blue << 3) | (blue >> 2),
    )


def psx_555_to_rgba(value: int) -> tuple[int, int, int, int]:
    red, green, blue = psx_555_to_rgb(value)
    if (value & 0x7FFF) == 0:
        return red, green, blue, 0
    if value & 0x8000:
        return red, green, blue, 128
    return red, green, blue, 255


def ensure_clean_directory(path: Path) -> None:
    if path.exists():
        for child in path.iterdir():
            if child.is_dir():
                shutil.rmtree(child)
            else:
                child.unlink()
    else:
        path.mkdir(parents=True, exist_ok=True)


def descriptor_base_name(descriptor: dict) -> str:
    return f"{descriptor['table']}_{descriptor['index']:02d}"


def write_ppm_from_16bpp(raw_plane: bytes, row_bytes: int, height: int, output_path: Path) -> None:
    width = row_bytes // 2
    header = f"P6\n{width} {height}\n255\n".encode("ascii")
    rgb = bytearray()
    for offset in range(0, row_bytes * height, 2):
        value = struct.unpack_from("<H", raw_plane, offset)[0]
        rgb.extend(psx_555_to_rgb(value))
    output_path.write_bytes(header + rgb)


def png_chunk(chunk_type: bytes, payload: bytes) -> bytes:
    return (
        struct.pack(">I", len(payload))
        + chunk_type
        + payload
        + struct.pack(">I", zlib.crc32(chunk_type + payload) & 0xFFFFFFFF)
    )


def write_png_from_16bpp(raw_plane: bytes, row_bytes: int, height: int, output_path: Path) -> None:
    width = row_bytes // 2
    scanlines = bytearray()
    for row in range(height):
        scanlines.append(0)
        row_offset = row * row_bytes
        for column in range(width):
            value = struct.unpack_from("<H", raw_plane, row_offset + column * 2)[0]
            scanlines.extend(psx_555_to_rgb(value))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    png = bytearray(b"\x89PNG\r\n\x1a\n")
    png += png_chunk(b"IHDR", ihdr)
    png += png_chunk(b"IDAT", zlib.compress(bytes(scanlines), level=9))
    png += png_chunk(b"IEND", b"")
    output_path.write_bytes(png)


def write_png_from_16bpp_rgba(raw_plane: bytes, row_bytes: int, height: int, output_path: Path) -> None:
    width = row_bytes // 2
    scanlines = bytearray()
    for row in range(height):
        scanlines.append(0)
        row_offset = row * row_bytes
        for column in range(width):
            value = struct.unpack_from("<H", raw_plane, row_offset + column * 2)[0]
            scanlines.extend(psx_555_to_rgba(value))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    png = bytearray(b"\x89PNG\r\n\x1a\n")
    png += png_chunk(b"IHDR", ihdr)
    png += png_chunk(b"IDAT", zlib.compress(bytes(scanlines), level=9))
    png += png_chunk(b"IEND", b"")
    output_path.write_bytes(png)


def palette_from_16bpp_words(raw_plane: bytes, color_count: int = 256) -> list[tuple[int, int, int]]:
    palette = []
    available = min(color_count, len(raw_plane) // 2)
    for index in range(available):
        palette.append(psx_555_to_rgb(struct.unpack_from("<H", raw_plane, index * 2)[0]))
    while len(palette) < color_count:
        palette.append((0, 0, 0))
    return palette


def write_png_from_indexed8(index_plane: bytes, width: int, height: int, palette: list[tuple[int, int, int]], output_path: Path) -> None:
    scanlines = bytearray()
    for row in range(height):
        scanlines.append(0)
        row_offset = row * width
        for value in index_plane[row_offset:row_offset + width]:
            red, green, blue = palette[value]
            scanlines.extend((red, green, blue))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    png = bytearray(b"\x89PNG\r\n\x1a\n")
    png += png_chunk(b"IHDR", ihdr)
    png += png_chunk(b"IDAT", zlib.compress(bytes(scanlines), level=9))
    png += png_chunk(b"IEND", b"")
    output_path.write_bytes(png)


def palette_words_from_16bpp(raw_plane: bytes, color_count: int = 256) -> list[int]:
    palette = []
    available = min(color_count, len(raw_plane) // 2)
    for index in range(available):
        palette.append(struct.unpack_from("<H", raw_plane, index * 2)[0])
    while len(palette) < color_count:
        palette.append(0)
    return palette


def write_png_from_indexed8_rgba(index_plane: bytes, width: int, height: int, palette_words: list[int], output_path: Path) -> None:
    scanlines = bytearray()
    for row in range(height):
        scanlines.append(0)
        row_offset = row * width
        for value in index_plane[row_offset:row_offset + width]:
            scanlines.extend(psx_555_to_rgba(palette_words[value]))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    png = bytearray(b"\x89PNG\r\n\x1a\n")
    png += png_chunk(b"IHDR", ihdr)
    png += png_chunk(b"IDAT", zlib.compress(bytes(scanlines), level=9))
    png += png_chunk(b"IEND", b"")
    output_path.write_bytes(png)


def write_primary_asset_png(index_plane: bytes, width: int, height: int, palette_words: list[int], output_path: Path) -> None:
    write_png_from_indexed8_rgba(index_plane, width, height, palette_words, output_path)


def write_png_from_indexed4_rgba(index_plane: bytes, row_bytes: int, height: int, palette_words: list[int], output_path: Path) -> None:
    width = row_bytes * 2
    scanlines = bytearray()
    for row in range(height):
        scanlines.append(0)
        row_offset = row * row_bytes
        for value in index_plane[row_offset:row_offset + row_bytes]:
            scanlines.extend(psx_555_to_rgba(palette_words[value & 0x0F]))
            scanlines.extend(psx_555_to_rgba(palette_words[(value >> 4) & 0x0F]))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    png = bytearray(b"\x89PNG\r\n\x1a\n")
    png += png_chunk(b"IHDR", ihdr)
    png += png_chunk(b"IDAT", zlib.compress(bytes(scanlines), level=9))
    png += png_chunk(b"IEND", b"")
    output_path.write_bytes(png)


def get_firstblock_row0_palette_words(package_first_block: bytes) -> list[int]:
    firstblock_row0 = package_first_block[0x88:0x88 + 0x200]
    if len(firstblock_row0) >= 0x200:
        return palette_words_from_16bpp(firstblock_row0)
    return [0] * 256


def get_fixedclut_base_0488_palette_words(package_first_block: bytes) -> list[int]:
    fixed_clut_base = package_first_block[0x488:0x488 + 0x200]
    if len(fixed_clut_base) >= 0x200:
        return palette_words_from_16bpp(fixed_clut_base)
    return get_firstblock_row0_palette_words(package_first_block)


def decode_packed_image(chunk: bytes, row_bytes: int, height: int) -> dict:
    control_to_stream_a = read_u32(chunk, 0)
    stream_a_len = read_u32(chunk, 4)
    stream_b_len = read_u32(chunk, 8)

    control = memoryview(chunk)[0x0C:0x0C + control_to_stream_a]
    stream_a = memoryview(chunk)[0x0C + control_to_stream_a:0x0C + control_to_stream_a + stream_a_len]
    stream_b = memoryview(chunk)[0x0C + control_to_stream_a + stream_a_len:0x0C + control_to_stream_a + stream_a_len + stream_b_len]
    main_stream = memoryview(chunk)[0x0C + control_to_stream_a + stream_a_len + stream_b_len:]

    semantic_size = row_bytes * height
    scratch_size = align4(semantic_size)
    output = bytearray(scratch_size)

    control_pos = 0
    stream_a_pos = 0
    stream_b_pos = 2
    main_pos = 0
    stream_a_lane_pos = 3
    out_pos = 0
    event_count = 0
    skip_count = 0

    while True:
        if control_pos >= len(control):
            raise RuntimeError("control stream exhausted before terminator")

        control_1 = read_i8(control, control_pos)
        control_pos += 1

        if control_1 == -1:
            break

        if control_1 < 0:
            out_pos += control_1 & 0x7F
            skip_count += 1
            continue

        event_count += 1
        out_pos += control_1
        mode = control_1 & 3

        if mode == 1:
            if len(stream_a) < 4 or out_pos < 1 or out_pos + 3 > len(output):
                raise RuntimeError("mode 1 prefix write out of bounds")
            word = read_u32(stream_a, 0)
            output[out_pos - 1:out_pos + 3] = struct.pack("<I", (word << 8) & 0xFFFFFFFF)
            out_pos += 3
        elif mode == 2:
            if len(stream_b) < 2 or out_pos + 2 > len(output):
                raise RuntimeError("mode 2 prefix write out of bounds")
            output[out_pos:out_pos + 2] = stream_b[0:2]
            out_pos += 2
        elif mode == 3:
            if len(stream_a) < 4 or out_pos + 1 > len(output):
                raise RuntimeError("mode 3 prefix write out of bounds")
            output[out_pos] = stream_a[3]
            out_pos += 1

        if control_pos >= len(control):
            raise RuntimeError("second control byte missing")

        control_2 = control[control_pos]
        control_pos += 1

        extra_dwords = control_2 & 3
        literal_bytes = (control_2 & 0x3C) * 4
        if main_pos + literal_bytes > len(main_stream) or out_pos + literal_bytes > len(output):
            raise RuntimeError("literal block write out of bounds")

        output[out_pos:out_pos + literal_bytes] = main_stream[main_pos:main_pos + literal_bytes]
        main_pos += literal_bytes
        out_pos += literal_bytes

        for _ in range(extra_dwords):
            if main_pos + 4 > len(main_stream) or out_pos + 4 > len(output):
                raise RuntimeError("extra dword write out of bounds")
            output[out_pos:out_pos + 4] = main_stream[main_pos:main_pos + 4]
            main_pos += 4
            out_pos += 4

        variant = control_2 >> 6
        if variant == 1:
            if stream_a_lane_pos >= len(stream_a) or out_pos + 1 > len(output):
                raise RuntimeError("variant 1 write out of bounds")
            output[out_pos] = stream_a[stream_a_lane_pos]
            stream_a_lane_pos += 4
            out_pos += 4
        elif variant == 2:
            if stream_b_pos + 2 > len(stream_b) or out_pos + 2 > len(output):
                raise RuntimeError("variant 2 write out of bounds")
            output[out_pos:out_pos + 2] = stream_b[stream_b_pos:stream_b_pos + 2]
            stream_b_pos += 2
            out_pos += 4
        elif variant == 3:
            if stream_a_pos + 4 > len(stream_a) or out_pos + 3 > len(output):
                raise RuntimeError("variant 3 write out of bounds")
            word = read_u32(stream_a, stream_a_pos)
            stream_a_pos += 4
            output[out_pos:out_pos + 3] = struct.pack("<I", word)[:3]
            out_pos += 4

    return {
        "semantic_size": semantic_size,
        "scratch_size": scratch_size,
        "decoded": bytes(output[:semantic_size]),
        "stats": {
            "control_to_stream_a": control_to_stream_a,
            "stream_a_len": stream_a_len,
            "stream_b_len": stream_b_len,
            "control_used": control_pos,
            "control_total": len(control),
            "stream_a_used": stream_a_pos,
            "stream_a_lane_used": stream_a_lane_pos,
            "stream_b_used": stream_b_pos,
            "main_used": main_pos,
            "final_pos": out_pos,
            "event_count": event_count,
            "skip_count": skip_count,
        },
    }


def build_package_from_wad(wad_data: bytes, package_offset: int) -> tuple[bytes, bytes, dict]:
    first = wad_data[package_offset:package_offset + 0x11000]
    if len(first) < 0x88:
        raise RuntimeError("package header is truncated")

    header_dwords = struct.unpack_from("<34I", first, 0)
    continuation_size = header_dwords[13]
    continuation = wad_data[package_offset + 0x11000:package_offset + 0x11000 + continuation_size]
    if len(continuation) < continuation_size:
        raise RuntimeError("package continuation is truncated")

    metadata = {
        "package_offset": package_offset,
        "header_dwords": list(header_dwords),
        "continuation_size": continuation_size,
        "primary_table_offset": header_dwords[0],
        "secondary_table_offset": header_dwords[1],
        "texture_table_offset": header_dwords[2],
        "render_table_offset": header_dwords[3],
        "audio_table_offset": header_dwords[4],
        "primary_chunk_bias": header_dwords[5],
        "secondary_chunk_bias": header_dwords[6],
        "primary_count": header_dwords[9],
        "secondary_count": header_dwords[10],
        "texture_count": header_dwords[11],
        "active_primary_index": first[0x7D],
        "active_frame_timer": first[0x7E],
        "render_group_count": first[0x81],
    }
    return first, continuation, metadata


def looks_like_character_package_header(first_block: bytes) -> bool:
    if len(first_block) < 0x88:
        return False
    header_dwords = struct.unpack_from("<34I", first_block, 0)
    primary_count = header_dwords[9]
    secondary_count = header_dwords[10]
    texture_count = header_dwords[11]
    continuation_size = header_dwords[13]
    render_group_count = first_block[0x81]

    if header_dwords[0] != 0 or header_dwords[1] != 0x64 or header_dwords[2] != 0x70:
        return False
    if not (1 <= primary_count <= 0x40):
        return False
    if not (0 <= secondary_count <= 0x20):
        return False
    if not (0 <= texture_count <= 0x20):
        return False
    if not (1 <= render_group_count <= 0x20):
        return False
    if not (0 < continuation_size <= 0x40000):
        return False
    if not (header_dwords[5] < continuation_size and header_dwords[6] < continuation_size):
        return False
    if not (header_dwords[2] <= header_dwords[3] <= header_dwords[4] <= continuation_size):
        return False

    selector_table = first_block[0x78:0x7C]
    if any(value >= primary_count for value in selector_table):
        return False
    return True


def scan_character_package_candidates(wad_data: bytes, max_results: int | None = None) -> list[int]:
    pattern = struct.pack("<III", 0, 0x64, 0x70)
    offsets: list[int] = []
    search_from = 0
    while True:
        offset = wad_data.find(pattern, search_from)
        if offset < 0:
            break
        first_block = wad_data[offset:offset + 0x11000]
        if looks_like_character_package_header(first_block):
            try:
                _, continuation, metadata = build_package_from_wad(wad_data, offset)
                first_primary = parse_primary_entry(continuation, metadata, 0)
                decode_packed_image(continuation[first_primary["chunk_offset"]:], first_primary["row_bytes"], first_primary["height"])
                offsets.append(offset)
                if max_results is not None and len(offsets) >= max_results:
                    break
            except Exception:
                pass
        search_from = offset + 4
    return offsets


def parse_primary_entry(continuation: bytes, metadata: dict, index: int) -> dict:
    entry_offset = metadata["primary_table_offset"] + index * 0x14
    entry = continuation[entry_offset:entry_offset + 0x14]
    if len(entry) < 0x14:
        raise RuntimeError(f"primary entry {index} is truncated")
    relative_chunk_offset = read_u32(entry, 0)
    return {
        "table": "primary",
        "index": index,
        "entry_offset": entry_offset,
        "relative_chunk_offset": relative_chunk_offset,
        "chunk_offset": metadata["primary_chunk_bias"] + relative_chunk_offset,
        "row_bytes": entry[7],
        "height": entry[8],
        "frame_timer": entry[0x0C],
        "control_mode": entry[0x0D],
        "effect_trigger_count": entry[0x0E],
        "effect_param": entry[0x0F],
        "event_id": entry[0x10],
        "companion_index": entry[0x12],
        "companion_param": entry[0x13],
        "raw_entry": entry.hex(),
    }


def parse_secondary_entry(continuation: bytes, metadata: dict, index: int) -> dict:
    entry_offset = metadata["secondary_table_offset"] + index * 0x0C
    entry = continuation[entry_offset:entry_offset + 0x0C]
    if len(entry) < 0x0C:
        raise RuntimeError(f"secondary entry {index} is truncated")
    relative_chunk_offset = read_u32(entry, 0)
    return {
        "table": "secondary",
        "index": index,
        "entry_offset": entry_offset,
        "relative_chunk_offset": relative_chunk_offset,
        "chunk_offset": metadata["secondary_chunk_bias"] + relative_chunk_offset,
        "row_bytes": entry[7],
        "height": entry[8],
        "raw_entry": entry.hex(),
    }


def dump_plane(output_dir: Path, continuation: bytes, descriptor: dict, write_descriptor_outputs: bool) -> dict:
    chunk = continuation[descriptor["chunk_offset"]:]
    decode_result = decode_packed_image(chunk, descriptor["row_bytes"], descriptor["height"])

    result = dict(descriptor)
    result["decode_stats"] = decode_result["stats"]
    result["nonzero_bytes"] = sum(1 for value in decode_result["decoded"] if value)
    result["decoded_bytes"] = decode_result["decoded"]

    if write_descriptor_outputs:
        base_name = descriptor_base_name(descriptor)
        raw_path = output_dir / f"{base_name}.raw"
        ppm_path = output_dir / f"{base_name}.ppm"
        png_path = output_dir / f"{base_name}.png"
        png_rgba_path = output_dir / f"{base_name}_rgba.png"
        raw_path.write_bytes(decode_result["decoded"])
        write_ppm_from_16bpp(decode_result["decoded"], descriptor["row_bytes"], descriptor["height"], ppm_path)
        write_png_from_16bpp(decode_result["decoded"], descriptor["row_bytes"], descriptor["height"], png_path)
        write_png_from_16bpp_rgba(decode_result["decoded"], descriptor["row_bytes"], descriptor["height"], png_rgba_path)
        result["raw_path"] = raw_path.name
        result["ppm_path"] = ppm_path.name
        result["png_path"] = png_path.name
        result["png_rgba_path"] = png_rgba_path.name
    return result


def add_indexed_previews(output_dir: Path, package_first_block: bytes, dumped_planes: list[dict]) -> None:
    primary_planes = [plane for plane in dumped_planes if plane.get("table") == "primary" and "decoded_bytes" in plane]
    secondary_planes = [plane for plane in dumped_planes if plane.get("table") == "secondary" and "decoded_bytes" in plane]

    palette_sources: list[tuple[str, list[tuple[int, int, int]]]] = []
    palette_sources_rgba: list[tuple[str, list[int]]] = []

    clut_block = package_first_block[0x88:0x88 + 0x400]
    if len(clut_block) >= 0x200:
        palette_sources.append(("firstblock_row0", palette_from_16bpp_words(clut_block[0x000:0x200])))
        palette_sources_rgba.append(("firstblock_row0", palette_words_from_16bpp(clut_block[0x000:0x200])))
    if len(clut_block) >= 0x400:
        palette_sources.append(("firstblock_row1", palette_from_16bpp_words(clut_block[0x200:0x400])))
        palette_sources_rgba.append(("firstblock_row1", palette_words_from_16bpp(clut_block[0x200:0x400])))

    fixed_clut_base = package_first_block[0x488:0x488 + 0x200]
    if len(fixed_clut_base) >= 0x200:
        palette_sources.append(("fixedclut_base_0488", palette_from_16bpp_words(fixed_clut_base)))
        palette_sources_rgba.append(("fixedclut_base_0488", palette_words_from_16bpp(fixed_clut_base)))

    for secondary in secondary_planes:
        secondary_raw = secondary["decoded_bytes"]
        palette_sources.append((f"secondary_{secondary['index']:02d}", palette_from_16bpp_words(secondary_raw)))
        palette_sources_rgba.append((f"secondary_{secondary['index']:02d}", palette_words_from_16bpp(secondary_raw)))

    for primary in primary_planes:
        raw_primary = primary["decoded_bytes"]
        width = primary["row_bytes"]
        height = primary["height"]
        indexed_outputs = []
        for label, palette in palette_sources:
            file_name = f"{primary['table']}_{primary['index']:02d}_indexed8_{label}.png"
            output_path = output_dir / file_name
            write_png_from_indexed8(raw_primary, width, height, palette, output_path)
            indexed_outputs.append(file_name)
        primary["indexed8_preview_pngs"] = indexed_outputs

        indexed_rgba_outputs = []
        for label, palette_words in palette_sources_rgba:
            file_name = f"{primary['table']}_{primary['index']:02d}_indexed8_{label}_rgba.png"
            output_path = output_dir / file_name
            write_png_from_indexed8_rgba(raw_primary, width, height, palette_words, output_path)
            indexed_rgba_outputs.append(file_name)
        primary["indexed8_preview_rgba_pngs"] = indexed_rgba_outputs


def write_final_descriptor_pngs(output_dir: Path, package_first_block: bytes, dumped_planes: list[dict]) -> None:
    primary_palette_words = get_firstblock_row0_palette_words(package_first_block)
    secondary_palette_words = get_fixedclut_base_0488_palette_words(package_first_block)
    for plane in dumped_planes:
        if plane.get("error") or "decoded_bytes" not in plane:
            continue
        file_name = f"{descriptor_base_name(plane)}.png"
        output_path = output_dir / file_name
        if plane.get("table") == "secondary":
            write_png_from_indexed8_rgba(
                plane["decoded_bytes"],
                plane["row_bytes"],
                plane["height"],
                secondary_palette_words,
                output_path,
            )
            plane["render_mode"] = "indexed8_fixedclut_base_0488_rgba"
        else:
            write_png_from_indexed8_rgba(
                plane["decoded_bytes"],
                plane["row_bytes"],
                plane["height"],
                primary_palette_words,
                output_path,
            )
            plane["render_mode"] = "indexed8_firstblock_row0_rgba"
        plane["png_path"] = file_name


def try_dump_plane(output_dir: Path, continuation: bytes, descriptor: dict, write_descriptor_outputs: bool) -> dict:
    try:
        return dump_plane(output_dir, continuation, descriptor, write_descriptor_outputs)
    except Exception as exc:
        failed = dict(descriptor)
        failed["error"] = str(exc)
        return failed


def get_primary_asset_palette_words(package_first_block: bytes) -> list[int]:
    fixed_clut_base = package_first_block[0x488:0x488 + 0x200]
    if len(fixed_clut_base) >= 0x200:
        return palette_words_from_16bpp(fixed_clut_base)
    firstblock_row0 = package_first_block[0x88:0x88 + 0x200]
    if len(firstblock_row0) >= 0x200:
        return palette_words_from_16bpp(firstblock_row0)
    return [0] * 256


def write_canonical_asset_outputs(package_dir: Path, package_first_block: bytes, dumped_planes: list[dict], asset_manifest: dict) -> None:
    assets_dir = package_dir / "assets"
    assets_dir.mkdir(parents=True, exist_ok=True)

    primary_palette_words = get_primary_asset_palette_words(package_first_block)
    planes_by_index = {plane["index"]: plane for plane in dumped_planes if "decoded_bytes" in plane}

    for asset in asset_manifest["unique_primary_assets"]:
        canonical_index = asset["descriptor_indices"][0]
        plane = planes_by_index[canonical_index]
        base_name = asset["asset_id"]
        raw_name = f"{base_name}.raw"
        png_name = f"{base_name}.png"
        raw_path = assets_dir / raw_name
        png_path = assets_dir / png_name
        raw_path.write_bytes(plane["decoded_bytes"])
        write_primary_asset_png(plane["decoded_bytes"], plane["row_bytes"], plane["height"], primary_palette_words, png_path)
        asset["canonical_outputs"] = {
            "raw": f"assets/{raw_name}",
            "png": f"assets/{png_name}",
        }

    for asset in asset_manifest["secondary_assets"]:
        plane = planes_by_index[asset["descriptor_index"]]
        base_name = asset["asset_id"]
        raw_name = f"{base_name}.raw"
        png_name = f"{base_name}.png"
        raw_path = assets_dir / raw_name
        png_path = assets_dir / png_name
        raw_path.write_bytes(plane["decoded_bytes"])
        write_png_from_16bpp_rgba(plane["decoded_bytes"], plane["row_bytes"], plane["height"], png_path)
        asset["outputs"] = {
            "raw": f"assets/{raw_name}",
            "png": f"assets/{png_name}",
        }


def make_json_safe_dumped_planes(dumped_planes: list[dict]) -> list[dict]:
    safe_planes = []
    for plane in dumped_planes:
        safe_plane = {key: value for key, value in plane.items() if key != "decoded_bytes"}
        safe_planes.append(safe_plane)
    return safe_planes


def build_character_asset_manifest(package_first_block: bytes, dumped_planes: list[dict]) -> dict:
    selector_table = [value for value in package_first_block[0x78:0x7C]]
    selector_slots_by_index: dict[int, list[int]] = {}
    for slot, primary_index in enumerate(selector_table):
        selector_slots_by_index.setdefault(primary_index, []).append(slot)

    default_selector_slot = 2
    default_primary_index = selector_table[default_selector_slot] if default_selector_slot < len(selector_table) else None

    primary_planes = [plane for plane in dumped_planes if plane.get("table") == "primary" and "error" not in plane]
    primary_groups: dict[tuple[int, int, int], list[dict]] = {}
    for plane in primary_planes:
        key = (plane["chunk_offset"], plane["row_bytes"], plane["height"])
        primary_groups.setdefault(key, []).append(plane)

    unique_primary_assets = []
    descriptor_to_asset_id: dict[int, str] = {}
    for asset_index, key in enumerate(sorted(primary_groups.keys())):
        group = sorted(primary_groups[key], key=lambda plane: plane["index"])
        canonical = group[0]
        alias_indices = [plane["index"] for plane in group]
        selector_slots = sorted({slot for plane in group for slot in selector_slots_by_index.get(plane["index"], [])})
        frame_timers = [frame_timer for frame_timer in {plane.get("frame_timer") for plane in group} if frame_timer is not None]
        companion_indices = sorted(
            {
                int(companion_index)
                for companion_index in (plane.get("companion_index") for plane in group)
                if companion_index is not None and companion_index != 0xFF
            }
        )
        asset_id = f"primary_asset_{asset_index:02d}"
        for plane in group:
            descriptor_to_asset_id[plane["index"]] = asset_id
        unique_primary_assets.append(
            {
                "asset_id": asset_id,
                "chunk_offset": canonical["chunk_offset"],
                "row_bytes": canonical["row_bytes"],
                "height": canonical["height"],
                "descriptor_indices": alias_indices,
                "frame_timers": sorted(frame_timers),
                "companion_indices": companion_indices,
                "selector_slots": selector_slots,
                "is_default_active_asset": default_primary_index in alias_indices,
                "canonical_outputs": {},
            }
        )

    secondary_planes = [plane for plane in dumped_planes if plane.get("table") == "secondary" and "error" not in plane]
    secondary_assets = []
    for plane in secondary_planes:
        secondary_assets.append(
            {
                "asset_id": f"secondary_asset_{plane['index']:02d}",
                "descriptor_index": plane["index"],
                "chunk_offset": plane["chunk_offset"],
                "row_bytes": plane["row_bytes"],
                "height": plane["height"],
                "outputs": {},
            }
        )

    descriptor_summary = []
    for plane in sorted(primary_planes, key=lambda plane: plane["index"]):
        descriptor_summary.append(
            {
                "descriptor_index": plane["index"],
                "asset_id": descriptor_to_asset_id[plane["index"]],
                "chunk_offset": plane["chunk_offset"],
                "row_bytes": plane["row_bytes"],
                "height": plane["height"],
                "frame_timer": plane.get("frame_timer"),
                "control_mode": plane.get("control_mode"),
                "effect_trigger_count": plane.get("effect_trigger_count"),
                "effect_param": plane.get("effect_param"),
                "event_id": plane.get("event_id"),
                "companion_index": plane.get("companion_index"),
                "companion_param": plane.get("companion_param"),
                "selector_slots": selector_slots_by_index.get(plane["index"], []),
                "is_runtime_selectable": plane["index"] in selector_slots_by_index,
                "is_default_active_descriptor": plane["index"] == default_primary_index,
            }
        )

    return {
        "ghidra_cross_reference": {
            "source_function": "LoadSceneCharacterPackage",
            "selector_table_offset": "0x78",
            "selector_table_size": 4,
            "default_selector_slot_written_by_loader": default_selector_slot,
            "default_frame_timer_offset_within_primary_descriptor": "0x0C",
            "default_companion_index_offset_within_primary_descriptor": "0x12",
            "notes": [
                "Cross-referenced against LoadSceneCharacterPackage in Ghidra.",
                "The loader copies a primary selector from package[0x78 + slot] and then seeds the runtime frame timer from primaryDescriptor[0x0C].",
                "FUN_80032638 decrements package[0x7E] every update, so this byte is a timer rather than a secondary-image selector.",
                "FUN_8004363c decodes a companion image when primaryDescriptor[0x12] != 0xFF, using secondaryTable[primaryDescriptor[0x12]].",
                "Unique primary assets are grouped here by decoded chunk source and dimensions, because multiple descriptors can alias the same packed image chunk.",
            ],
        },
        "selector_table": selector_table,
        "default_primary_index": default_primary_index,
        "default_frame_timer": next((plane.get("frame_timer") for plane in primary_planes if plane["index"] == default_primary_index), None),
        "default_companion_index": next((plane.get("companion_index") for plane in primary_planes if plane["index"] == default_primary_index), None),
        "unique_primary_assets": unique_primary_assets,
        "primary_descriptor_summary": descriptor_summary,
        "secondary_assets": secondary_assets,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Dump validated character-package image planes from Disruptor WAD.IN")
    parser.add_argument("--wad", type=Path, default=Path("WAD.IN"), help="Path to WAD.IN")
    parser.add_argument("--package-offset", type=lambda value: int(value, 0), default=0x0089D800, help="File offset of a candidate character package")
    parser.add_argument("--output", type=Path, default=Path("dumped_planes"), help="Output directory")
    parser.add_argument("--primary-limit", type=int, default=None, help="Optional maximum number of primary entries to dump")
    parser.add_argument("--secondary-limit", type=int, default=None, help="Optional maximum number of secondary entries to dump")
    parser.add_argument("--debug-outputs", action="store_true", help="Also write raw/PPM/direct-color debug images and alternate palette preview variants")
    parser.add_argument("--asset-only", action="store_true", help="Only write canonical manifest assets, without descriptor-level PNG outputs")
    parser.add_argument("--no-clean-output", action="store_true", help="Do not delete existing files in the package output directory before writing")
    parser.add_argument("--write-assets", action="store_true", help="Also write canonical grouped assets into an assets subfolder")
    parser.add_argument("--scan-candidates", action="store_true", help="Scan WAD.IN for candidate character packages and print their offsets")
    parser.add_argument("--scan-limit", type=int, default=20, help="Maximum candidate offsets to report when scanning")
    args = parser.parse_args()

    wad_data = args.wad.read_bytes()

    if args.scan_candidates:
        candidate_offsets = scan_character_package_candidates(wad_data, max_results=args.scan_limit)
        print(f"candidate_count={len(candidate_offsets)}")
        for offset in candidate_offsets:
            print(f"candidate_offset=0x{offset:08X}")
        return

    package_first_block, continuation, metadata = build_package_from_wad(wad_data, args.package_offset)
    write_descriptor_debug_outputs = args.debug_outputs and not args.asset_only
    write_descriptor_final_pngs = not args.asset_only

    package_dir = args.output / f"package_{args.package_offset:08X}"
    if args.no_clean_output:
        package_dir.mkdir(parents=True, exist_ok=True)
    else:
        ensure_clean_directory(package_dir)

    dumped = []
    primary_count = metadata["primary_count"] if args.primary_limit is None else min(metadata["primary_count"], args.primary_limit)
    secondary_count = metadata["secondary_count"] if args.secondary_limit is None else min(metadata["secondary_count"], args.secondary_limit)

    for index in range(primary_count):
        dumped.append(try_dump_plane(package_dir, continuation, parse_primary_entry(continuation, metadata, index), write_descriptor_debug_outputs))
    for index in range(secondary_count):
        dumped.append(try_dump_plane(package_dir, continuation, parse_secondary_entry(continuation, metadata, index), write_descriptor_debug_outputs))

    if write_descriptor_final_pngs:
        write_final_descriptor_pngs(package_dir, package_first_block, dumped)
    if write_descriptor_debug_outputs:
        add_indexed_previews(package_dir, package_first_block, dumped)
    asset_manifest = build_character_asset_manifest(package_first_block, dumped)
    if args.asset_only or args.write_assets:
        write_canonical_asset_outputs(package_dir, package_first_block, dumped, asset_manifest)

    metadata["dumped_planes"] = make_json_safe_dumped_planes(dumped)
    metadata_path = package_dir / "metadata.json"
    metadata_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    if args.asset_only or args.write_assets:
        manifest_path = package_dir / "assets_manifest.json"
        manifest_path.write_text(json.dumps(asset_manifest, indent=2), encoding="utf-8")

    print(f"output_dir={package_dir}")
    print(f"primary_count={metadata['primary_count']} secondary_count={metadata['secondary_count']}")
    print(
        f"unique_primary_assets={len(asset_manifest['unique_primary_assets'])} "
        f"selector_table={asset_manifest['selector_table']} "
        f"default_primary_index={asset_manifest['default_primary_index']}"
    )
    for asset in asset_manifest["unique_primary_assets"]:
        print(
            f"  {asset['asset_id']} chunk=0x{asset['chunk_offset']:X} "
            f"size={asset['row_bytes']}x{asset['height']} descriptors={asset['descriptor_indices']}"
        )
    if write_descriptor_final_pngs:
        for plane in dumped:
            if "error" in plane:
                print(
                    f"{plane['table']}[{plane['index']}] row_bytes=0x{plane['row_bytes']:X} "
                    f"height=0x{plane['height']:X} error={plane['error']}"
                )
            else:
                if plane.get("png_path"):
                    print(
                        f"{plane['table']}[{plane['index']}] row_bytes=0x{plane['row_bytes']:X} "
                        f"height=0x{plane['height']:X} png={plane['png_path']}"
                    )
                else:
                    print(
                        f"{plane['table']}[{plane['index']}] row_bytes=0x{plane['row_bytes']:X} "
                        f"height=0x{plane['height']:X} render_mode={plane.get('render_mode')}"
                    )
    if write_descriptor_debug_outputs:
        for plane in dumped:
            if "error" not in plane and plane.get("indexed8_preview_pngs"):
                print("  indexed8=" + ", ".join(plane["indexed8_preview_pngs"]))
            if "error" not in plane and plane.get("indexed8_preview_rgba_pngs"):
                print("  indexed8_rgba=" + ", ".join(plane["indexed8_preview_rgba_pngs"]))


if __name__ == "__main__":
    main()
