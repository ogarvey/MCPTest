from __future__ import annotations

import argparse
import json
import struct
import zlib
from collections import Counter
from dataclasses import dataclass
from pathlib import Path


DESCRIPTOR_COUNT_CANDIDATES = (4, 3)
DESCRIPTOR_RECORD_SIZE = 0x28
RAW_PANEL_SIZE = 0x10000
RAW_PANEL_WIDTH = 0x100
RAW_PANEL_HEIGHT = 0x100
POINTER_BASE_FLOOR = 0x80000000
FULL_IMG_RAM_ADDRESS = 0x800B0000
GRAPHICS_ROOT_RAM_ADDRESS = 0x80118838
COMMAND_STREAM_POINTER_RAM_ADDRESS = 0x801187FC
COMMAND_STREAM_PREVIEW_WORDS = 32
RESOURCE_DESCRIPTOR_TABLE_RAM_ADDRESS = 0x800D4044
PRIMARY_STARTUP_RECORD_BANK_RAM_ADDRESS = 0x801AE6C8
SECONDARY_STARTUP_RECORD_BANK_RAM_ADDRESS = 0x801AFF00
SECONDARY_STARTUP_METADATA_RAM_ADDRESS = 0x801AFEE0
FIXED_STARTUP_POINTER_RAM_ADDRESS = 0x801AEA8C
FIXED_STARTUP_POINTER_TABLE_RAM_ADDRESS = 0x801AEAD0
PRIMARY_STARTUP_LIST_HEAD_RAM_ADDRESS = 0x801AFE70
SECONDARY_STARTUP_LIST_HEAD_RAM_ADDRESS = 0x801B2048
STARTUP_RECORD_SIZE = 0x22
MAX_BANK_COUNT = 4
MAX_TILE_REMAP = 0x400

Pixel = tuple[int, int, int, int]


@dataclass(frozen=True)
class DescriptorRecord:
    index: int
    file_offset: int
    ram_address: int
    data_pointer: int
    data_offset: int
    clut_x: int
    clut_y: int
    tpage_x: int
    tpage_y: int
    vram_width: int
    vram_height: int
    mode: int
    abr: int
    byte_size: int
    texel_width: int
    texel_height: int


@dataclass(frozen=True)
class PageData:
    descriptor: DescriptorRecord
    img_offset: int
    source_layout: str
    raw_bytes: bytes
    indices: list[int]
    palette_words: list[int]


@dataclass(frozen=True)
class SpriteEntry:
    index: int
    file_offset: int
    u0: int
    v0: int
    descriptor_index: int
    palette_slot: int
    flags: int


@dataclass(frozen=True)
class BankDescriptor:
    index: int
    file_offset: int
    ram_address: int
    flags: int
    size_pointer: int
    size_offset: int
    sprite_pointer: int
    sprite_offset: int
    aux_pointer: int
    aux_offset: int | None
    texture_array_pointer: int
    texture_array_offset: int
    variant_word: int
    remap_pointer: int
    remap_offset: int | None
    map_width: int
    map_height: int
    tile_width: int
    tile_height: int
    initial_scroll_x: int
    initial_scroll_y: int
    depth_mode: int
    depth_aux: int
    tile_values: list[int]


@dataclass(frozen=True)
class GraphicsRoot:
    file_offset: int
    ram_address: int
    texture_array_pointer: int
    texture_array_offset: int
    bank_array_pointer: int
    bank_array_offset: int
    active_bank_index: int
    script_tables_pointer: int
    script_tables_offset: int | None
    bank_pointers: list[int]
    banks: list[BankDescriptor]


@dataclass(frozen=True)
class BankUsage:
    used_sprite_indices: list[int]
    descriptor_counts: dict[int, int]
    palette_counts: dict[int, int]
    flag_counts: dict[int, int]
    high_bits: dict[int, int]
    missing_sprite_indices: list[int]


@dataclass(frozen=True)
class ImgPayloadSelection:
    data: bytes
    offset: int
    description: str
    trusted_graphics: bool


@dataclass(frozen=True)
class AuxiliaryAsset:
    kind: str
    img_offset: int
    source_layout: str
    raw_bytes: bytes
    row_byte_width: int | None = None
    row_count: int | None = None


@dataclass(frozen=True)
class CommandStreamAnchor:
    pointer_slot_offset: int
    pointer_value: int
    stream_offset: int
    preview_words: list[int]


def read_u16_table(data: bytes, count: int) -> list[int]:
    return [struct.unpack_from('<H', data, offset)[0] for offset in range(0, count * 2, 2)]


def read_u32_table(data: bytes, count: int) -> list[int]:
    return [struct.unpack_from('<I', data, offset)[0] for offset in range(0, count * 4, 4)]


def read_u32(data: bytes, offset: int) -> int:
    return struct.unpack_from('<I', data, offset)[0]


def read_u16(data: bytes, offset: int) -> int:
    return struct.unpack_from('<H', data, offset)[0]


def pointer_to_offset(pointer: int, relocation_base: int, data_length: int) -> int | None:
    offset = pointer - relocation_base
    if 0 <= offset < data_length:
        return offset
    return None


def format_count_map(counts: dict[int, int], *, hex_keys: bool = False) -> str:
    if not counts:
        return 'none'
    if hex_keys:
        return ', '.join(f'0x{key:X}->{value}' for key, value in counts.items())
    return ', '.join(f'{key}->{value}' for key, value in counts.items())


def mode_to_texel_width(mode: int, vram_width: int) -> int:
    if mode == 0:
        return vram_width * 4
    if mode == 1:
        return vram_width * 2
    return vram_width


def read_descriptor_record(data: bytes, descriptor_base: int, relocation_base: int, index: int) -> DescriptorRecord | None:
    values = [read_u32(data, descriptor_base + value_index * 4) for value_index in range(DESCRIPTOR_RECORD_SIZE // 4)]
    data_pointer = values[0]
    clut_x = values[1]
    clut_y = values[2]
    tpage_x = values[4]
    tpage_y = values[5]
    vram_width = values[6]
    vram_height = values[7]
    mode = values[8]
    abr = values[9]

    if mode > 2 or abr > 3:
        return None
    if clut_x > 0x400 or clut_y > 0x400 or tpage_x > 0x400 or tpage_y > 0x400:
        return None
    if vram_width == 0 or vram_height == 0 or vram_width > 0x200 or vram_height > 0x200:
        return None

    data_offset = data_pointer - relocation_base
    if data_offset < 0 or data_offset + 0x200 > len(data):
        return None

    texel_width = mode_to_texel_width(mode, vram_width)
    byte_size = vram_width * vram_height * 2

    return DescriptorRecord(
        index=index,
        file_offset=descriptor_base,
        ram_address=relocation_base + descriptor_base,
        data_pointer=data_pointer,
        data_offset=data_offset,
        clut_x=clut_x,
        clut_y=clut_y,
        tpage_x=tpage_x,
        tpage_y=tpage_y,
        vram_width=vram_width,
        vram_height=vram_height,
        mode=mode,
        abr=abr,
        byte_size=byte_size,
        texel_width=texel_width,
        texel_height=vram_height,
    )


def find_pointer_backed_descriptor_bank(data: bytes) -> tuple[int, int, int, list[DescriptorRecord]] | None:
    for descriptor_count in DESCRIPTOR_COUNT_CANDIDATES:
        minimum_size = descriptor_count * DESCRIPTOR_RECORD_SIZE + descriptor_count * 4
        for descriptor_base in range(0, len(data) - minimum_size, 4):
            pointer_array_offset = descriptor_base + descriptor_count * DESCRIPTOR_RECORD_SIZE
            pointer_words = [read_u32(data, pointer_array_offset + index * 4) for index in range(descriptor_count)]
            relocation_base = pointer_words[0] - descriptor_base

            if relocation_base < POINTER_BASE_FLOOR:
                continue
            if any(
                pointer_words[index] != relocation_base + descriptor_base + index * DESCRIPTOR_RECORD_SIZE
                for index in range(descriptor_count)
            ):
                continue

            records: list[DescriptorRecord] = []
            valid = True
            for index in range(descriptor_count):
                record = read_descriptor_record(data, descriptor_base + index * DESCRIPTOR_RECORD_SIZE, relocation_base, index)
                if record is None:
                    valid = False
                    break
                records.append(record)

            if not valid:
                continue

            return relocation_base, descriptor_base, pointer_array_offset, records

    return None


def read_bank_descriptor(
    data: bytes,
    relocation_base: int,
    bank_pointer: int,
    index: int,
) -> BankDescriptor | None:
    bank_offset = pointer_to_offset(bank_pointer, relocation_base, len(data))
    if bank_offset is None or bank_offset + 0x24 > len(data):
        return None

    flags = read_u16(data, bank_offset)
    size_pointer = read_u32(data, bank_offset + 4)
    sprite_pointer = read_u32(data, bank_offset + 8)
    aux_pointer = read_u32(data, bank_offset + 0x0C)
    texture_array_pointer = read_u32(data, bank_offset + 0x10)
    variant_word = read_u32(data, bank_offset + 0x14)
    remap_pointer = read_u32(data, bank_offset + 0x18)

    size_offset = pointer_to_offset(size_pointer, relocation_base, len(data))
    sprite_offset = pointer_to_offset(sprite_pointer, relocation_base, len(data))
    aux_offset = pointer_to_offset(aux_pointer, relocation_base, len(data)) if aux_pointer != 0 else None
    texture_array_offset = pointer_to_offset(texture_array_pointer, relocation_base, len(data))
    remap_offset = pointer_to_offset(remap_pointer, relocation_base, len(data)) if remap_pointer != 0 else None

    if size_offset is None or sprite_offset is None or texture_array_offset is None:
        return None
    if size_offset + 8 > len(data):
        return None

    map_width = data[size_offset + 4]
    map_height = data[size_offset + 5]
    tile_width = data[size_offset + 6]
    tile_height = data[size_offset + 7]
    initial_scroll_x = struct.unpack_from('<h', data, bank_offset + 0x1C)[0]
    initial_scroll_y = struct.unpack_from('<h', data, bank_offset + 0x1E)[0]
    depth_mode = struct.unpack_from('<h', data, bank_offset + 0x20)[0]
    depth_aux = struct.unpack_from('<h', data, bank_offset + 0x22)[0]
    if map_width == 0 or map_height == 0 or tile_width == 0 or tile_height == 0:
        return None

    tile_count = map_width * map_height
    tile_map_end = size_offset + 8 + tile_count * 2
    if tile_map_end > len(data):
        return None

    tile_values = [read_u16(data, size_offset + 8 + tile_index * 2) for tile_index in range(tile_count)]

    return BankDescriptor(
        index=index,
        file_offset=bank_offset,
        ram_address=bank_pointer,
        flags=flags,
        size_pointer=size_pointer,
        size_offset=size_offset,
        sprite_pointer=sprite_pointer,
        sprite_offset=sprite_offset,
        aux_pointer=aux_pointer,
        aux_offset=aux_offset,
        texture_array_pointer=texture_array_pointer,
        texture_array_offset=texture_array_offset,
        variant_word=variant_word,
        remap_pointer=remap_pointer,
        remap_offset=remap_offset,
        map_width=map_width,
        map_height=map_height,
        tile_width=tile_width,
        tile_height=tile_height,
        initial_scroll_x=initial_scroll_x,
        initial_scroll_y=initial_scroll_y,
        depth_mode=depth_mode,
        depth_aux=depth_aux,
        tile_values=tile_values,
    )


def read_graphics_root(data: bytes, relocation_base: int, texture_array_offset: int) -> GraphicsRoot | None:
    root_offset = GRAPHICS_ROOT_RAM_ADDRESS - relocation_base
    if root_offset < 0 or root_offset + 0x20 > len(data):
        return None

    texture_array_pointer = read_u32(data, root_offset)
    bank_array_pointer = read_u32(data, root_offset + 4)
    active_bank_index = read_u32(data, root_offset + 8)
    script_tables_pointer = read_u32(data, root_offset + 0x0C)

    root_texture_array_offset = pointer_to_offset(texture_array_pointer, relocation_base, len(data))
    bank_array_offset = pointer_to_offset(bank_array_pointer, relocation_base, len(data))
    script_tables_offset = pointer_to_offset(script_tables_pointer, relocation_base, len(data)) if script_tables_pointer != 0 else None

    if root_texture_array_offset is None or bank_array_offset is None or active_bank_index >= MAX_BANK_COUNT:
        return None
    if root_texture_array_offset != texture_array_offset:
        return None
    if bank_array_offset + MAX_BANK_COUNT * 4 > len(data):
        return None

    bank_pointers = [read_u32(data, bank_array_offset + index * 4) for index in range(MAX_BANK_COUNT)]
    banks: list[BankDescriptor] = []
    for index, bank_pointer in enumerate(bank_pointers):
        if bank_pointer == 0:
            continue
        bank = read_bank_descriptor(data, relocation_base, bank_pointer, index)
        if bank is not None:
            banks.append(bank)

    if not banks or all(bank.index != active_bank_index for bank in banks):
        return None

    return GraphicsRoot(
        file_offset=root_offset,
        ram_address=GRAPHICS_ROOT_RAM_ADDRESS,
        texture_array_pointer=texture_array_pointer,
        texture_array_offset=root_texture_array_offset,
        bank_array_pointer=bank_array_pointer,
        bank_array_offset=bank_array_offset,
        active_bank_index=active_bank_index,
        script_tables_pointer=script_tables_pointer,
        script_tables_offset=script_tables_offset,
        bank_pointers=bank_pointers,
        banks=banks,
    )


def get_active_bank(graphics_root: GraphicsRoot | None) -> BankDescriptor | None:
    if graphics_root is None:
        return None
    for bank in graphics_root.banks:
        if bank.index == graphics_root.active_bank_index:
            return bank
    return None


def read_command_stream_anchor(
    data: bytes,
    relocation_base: int,
    *,
    preview_words: int = COMMAND_STREAM_PREVIEW_WORDS,
) -> CommandStreamAnchor | None:
    pointer_slot_offset = pointer_to_offset(COMMAND_STREAM_POINTER_RAM_ADDRESS, relocation_base, len(data))
    if pointer_slot_offset is None or pointer_slot_offset + 4 > len(data):
        return None

    pointer_value = read_u32(data, pointer_slot_offset)
    stream_offset = pointer_to_offset(pointer_value, relocation_base, len(data))
    if stream_offset is None:
        return None

    preview: list[int] = []
    for index in range(preview_words):
        word_offset = stream_offset + index * 2
        if word_offset + 2 > len(data):
            break
        preview.append(read_u16(data, word_offset))

    return CommandStreamAnchor(
        pointer_slot_offset=pointer_slot_offset,
        pointer_value=pointer_value,
        stream_offset=stream_offset,
        preview_words=preview,
    )


def read_sprite_entry(data: bytes, sprite_offset: int, sprite_index: int) -> SpriteEntry | None:
    entry_offset = sprite_offset + sprite_index * 4
    if entry_offset + 4 > len(data):
        return None

    u0, v0, packed, flags = struct.unpack_from('<BBBB', data, entry_offset)
    return SpriteEntry(
        index=sprite_index,
        file_offset=entry_offset,
        u0=u0,
        v0=v0,
        descriptor_index=packed & 0x0F,
        palette_slot=packed >> 4,
        flags=flags,
    )


def resolve_sprite_page(
    data: bytes,
    bank: BankDescriptor,
    sprite_entry: SpriteEntry,
    pages_by_ram_address: dict[int, PageData],
) -> PageData | None:
    descriptor_pointer_offset = bank.texture_array_offset + sprite_entry.descriptor_index * 4
    if descriptor_pointer_offset + 4 > len(data):
        return None

    descriptor_pointer = read_u32(data, descriptor_pointer_offset)
    if descriptor_pointer == 0:
        return None

    return pages_by_ram_address.get(descriptor_pointer)


def read_remap_sequence_value(data: bytes, sequence_offset: int, sequence_step: int) -> int | None:
    count_offset = sequence_offset + sequence_step * 2
    if count_offset + 4 > len(data):
        return None

    if read_u16(data, count_offset) == 0:
        count_offset = sequence_offset
        if count_offset + 4 > len(data) or read_u16(data, count_offset) == 0:
            return None

    return read_u16(data, count_offset + 2)


def build_tile_remap_table(data: bytes, bank: BankDescriptor, relocation_base: int) -> list[int]:
    remap_table = list(range(MAX_TILE_REMAP))
    if bank.remap_offset is None:
        return remap_table

    cursor = bank.remap_offset
    while cursor + 12 <= len(data):
        target_index = read_u16(data, cursor)
        if target_index == 0:
            break

        sequence_pointer = read_u32(data, cursor + 4)
        sequence_offset = pointer_to_offset(sequence_pointer, relocation_base, len(data))
        sequence_step = read_u16(data, cursor + 8)
        if target_index < MAX_TILE_REMAP and sequence_offset is not None:
            sequence_value = read_remap_sequence_value(data, sequence_offset, sequence_step)
            if sequence_value is not None:
                remap_table[target_index] = sequence_value

        cursor += 12

    return remap_table


def analyze_bank_usage(data: bytes, bank: BankDescriptor, relocation_base: int) -> BankUsage:
    remap_table = build_tile_remap_table(data, bank, relocation_base)
    used_sprite_indices = sorted(
        {
            remap_table[tile_value & 0x0FFF]
            for tile_value in bank.tile_values
            if (tile_value & 0x0FFF) != 0 and remap_table[tile_value & 0x0FFF] != 0
        }
    )

    descriptor_counts: Counter[int] = Counter()
    palette_counts: Counter[int] = Counter()
    flag_counts: Counter[int] = Counter()
    high_bits: Counter[int] = Counter()
    missing_sprite_indices: list[int] = []

    for tile_value in bank.tile_values:
        if (tile_value & 0x0FFF) != 0:
            high_bits[tile_value & 0xF000] += 1

    for sprite_index in used_sprite_indices:
        sprite_entry = read_sprite_entry(data, bank.sprite_offset, sprite_index)
        if sprite_entry is None:
            missing_sprite_indices.append(sprite_index)
            continue
        descriptor_counts[sprite_entry.descriptor_index] += 1
        palette_counts[sprite_entry.palette_slot] += 1
        flag_counts[sprite_entry.flags] += 1

    return BankUsage(
        used_sprite_indices=used_sprite_indices,
        descriptor_counts=dict(sorted(descriptor_counts.items())),
        palette_counts=dict(sorted(palette_counts.items())),
        flag_counts=dict(sorted(flag_counts.items())),
        high_bits=dict(sorted(high_bits.items())),
        missing_sprite_indices=missing_sprite_indices,
    )


def read_palette_words(data: bytes, data_offset: int) -> list[int]:
    return [read_u16(data, data_offset + index * 2) for index in range(256)]


def is_vab_like_payload(img_data: bytes) -> bool:
    return len(img_data) >= 4 and (
        img_data.startswith(b'VABp')
        or img_data.startswith(b'pBAV')
        or read_u32(img_data, 0) == 0x56414270
    )


def resolve_palette_words(
    img_data: bytes,
    dat_data: bytes,
    record: DescriptorRecord,
) -> tuple[list[int], str]:
    dat_palette = read_palette_words(dat_data, record.data_offset)
    if any(dat_palette):
        return dat_palette, ''

    if is_vab_like_payload(img_data):
        full_img_offset = record.data_pointer - FULL_IMG_RAM_ADDRESS
        if 0 <= full_img_offset <= len(img_data) - 0x200:
            img_palette = read_palette_words(img_data, full_img_offset)
            if any(img_palette):
                return img_palette, f'; palette=img+0x{full_img_offset:X}'

    return dat_palette, ''


def parse_vab_container(img_data: bytes) -> dict[str, int] | None:
    """Parse a Sony PSY-Q VAB header (`pBAV`/`VABp`) and locate the audio body.

    Layout (verified against FUN_80128a34 / S3041.IMG / S3011.IMG / S3031.IMG):
      0x0000        VabHdr (0x20 bytes)             - magic, ver, ps, ts, vs at +0x12,+0x14,+0x16
      0x0020        ProgAttr[128]  (16 bytes each = 0x800)
      0x0820        VagAtr[ps][16] (32 bytes each = ps*0x200)
      table_off     VAG offset table (256 shorts; entry = waveform size in 8-byte units)
      ...           padding to VH_STAGE_SIZE
      VH_STAGE_SIZE VAG bodies (audio samples). Total = sum(table[1..vs]) << 3.

    FUN_80128a34 always copies the first VH_STAGE_SIZE (0x6000) bytes as the VH and
    treats everything after as the VAG body. So `body_start_in_file` is fixed at 0x6000.
    """
    if len(img_data) < 0x20:
        return None

    magic = read_u32(img_data, 0)
    if magic >> 8 != 0x564142:
        return None

    version = read_u32(img_data, 4)
    program_count = read_u16(img_data, 0x12)
    vag_count = img_data[0x16]
    if program_count > 0x80 or vag_count > 0xFF:
        return None

    table_off = 0x20 + 0x80 * 0x10 + program_count * 0x200
    entry_count = vag_count + 1
    if table_off + entry_count * 2 > len(img_data):
        return None

    size_shift = 3 if version > 4 else 2
    body_size = sum(
        read_u16(img_data, table_off + index * 2) << size_shift
        for index in range(entry_count)
    )

    vh_stage_size = 0x6000  # FUN_80128a34 copies exactly this many bytes as VH
    body_start_in_file = vh_stage_size
    body_end_in_file = body_start_in_file + body_size

    return {
        'version': version,
        'program_count': program_count,
        'vag_count': vag_count,
        'declared_size': read_u32(img_data, 0x0C),
        'vag_table_offset': table_off,
        'body_start_in_file': body_start_in_file,
        'body_size': body_size,
        'audio_end_in_file': body_end_in_file,
    }


# Fixed graphics-block offset for VAB-prefixed scene IMGs.
#
# Ghidra-grounded:
#   - The IMG file is loaded into RAM at base 0x800C9000 (the audio install path
#     in FUN_8016a730 calls FUN_80128aa8(&DAT_800c9000, &DAT_800cf000, 1), where
#     0x800CF000 = 0x800C9000 + 0x6000 is the start of the audio body).
#   - The same scene installer (FUN_8016a730) issues GPU upload calls with literal
#     source pointers DAT_800E8000, DAT_800F8000, DAT_800F8400, DAT_80100400. The
#     smallest of these (0x800E8000) corresponds to file offset
#         0x800E8000 - 0x800C9000 = 0x1F000.
#     i.e. the graphics block always begins at IMG+0x1F000 in VAB-prefixed IMGs,
#     regardless of where the VAB audio body actually ends. The bytes between
#     audio_end and 0x1F000 are unused padding inside the loaded buffer.
#   - The exact in-file structure beyond 0x1F000 is per-scene (each scene has a
#     bespoke installer in the EXE that hardcodes VRAM coords / sizes / CLUT slots
#     for its specific image pages). The post-0x1F000 region is therefore raw
#     graphics bytes, but the per-page layout cannot be derived from the IMG alone.
VAB_GRAPHICS_BLOCK_OFFSET = 0x1F000


def select_img_payload(img_data: bytes, records: list[DescriptorRecord]) -> ImgPayloadSelection:
    """Locate the descriptor-graphics payload inside an IMG file.

    For VAB-prefixed scene IMG files (e.g. 31/S3041.IMG, E1/SE01.IMG):
      - 0x00000..0x06000  PSY-Q VAB header staging area (VabHdr + ProgAttr + VagAtr +
                          VAG offset table; possibly also a SEQ block in some scenes).
      - 0x06000..audio_end VAG audio body, total = sum(VAG table) << 3.
      - audio_end..0x1F000 zero padding inside the staging buffer; not used.
      - 0x1F000..end       Graphics block. Uploaded to VRAM by the scene's hardcoded
                          installer in the EXE (e.g. FUN_8016a730), which knows the
                          per-page (img_offset, vram_x, vram_y, w, h) layout.
    For pure-graphics IMGs (no leading VAB), the entire file is the graphics block.
    """
    if not records:
        return ImgPayloadSelection(
            data=b'', offset=len(img_data),
            description='empty descriptor set', trusted_graphics=False,
        )

    vab = parse_vab_container(img_data)
    if vab is not None:
        audio_end = vab['audio_end_in_file']
        if VAB_GRAPHICS_BLOCK_OFFSET < len(img_data):
            payload_offset = VAB_GRAPHICS_BLOCK_OFFSET
            description = (
                f'graphics block at fixed IMG+0x{payload_offset:X} '
                f'(Ghidra-grounded: IMG loads to RAM 0x800C9000; FUN_8016a730 '
                f'uploads graphics from RAM 0x800E8000 = base+0x1F000); '
                f'VAB header at IMG+0x0, audio body 0x{vab["body_start_in_file"]:X}..'
                f'0x{audio_end:X} (body_size=0x{vab["body_size"]:X}); '
                f'graphics block size = {len(img_data) - payload_offset} bytes; '
                f'per-page layout is per-scene and lives in the EXE installer'
            )
            return ImgPayloadSelection(
                data=img_data[payload_offset:],
                offset=payload_offset,
                description=description,
                trusted_graphics=True,
            )
        # IMG ends before the fixed graphics offset: pure-audio IMG.
        return ImgPayloadSelection(
            data=b'', offset=len(img_data),
            description=(
                f'entire IMG ({len(img_data)} bytes) is a PSY-Q VAB audio file; '
                f'audio body ends at 0x{audio_end:X} and the file is shorter than '
                f'the fixed 0x{VAB_GRAPHICS_BLOCK_OFFSET:X} graphics-block offset'
            ),
            trusted_graphics=False,
        )

    expected_size = sum(record.byte_size for record in records)
    if expected_size == 0:
        return ImgPayloadSelection(data=b'', offset=len(img_data), description='zero-byte descriptor set', trusted_graphics=False)

    if len(img_data) < expected_size:
        return ImgPayloadSelection(data=b'', offset=len(img_data), description='IMG smaller than concatenated descriptor bytes', trusted_graphics=False)

    payload_offset = len(img_data) - expected_size
    description = f'sequential descriptor block tail at IMG+0x{payload_offset:X} ({expected_size} bytes total)'
    if payload_offset > 0:
        description += f'; 0x{payload_offset:X} bytes of leading auxiliary data'

    return ImgPayloadSelection(
        data=img_data[payload_offset:],
        offset=payload_offset,
        description=description,
        trusted_graphics=True,
    )


def decode_page_indices(raw_bytes: bytes, descriptor: DescriptorRecord) -> list[int]:
    expected_pixels = descriptor.texel_width * descriptor.texel_height
    if descriptor.mode == 0:
        indices: list[int] = []
        for byte in raw_bytes:
            indices.append(byte & 0x0F)
            indices.append(byte >> 4)
        return indices[:expected_pixels]
    if descriptor.mode == 1:
        return list(raw_bytes[:expected_pixels])
    return []


def build_pages(
    img_data: bytes,
    dat_data: bytes,
    records: list[DescriptorRecord],
) -> tuple[str, list[PageData], list[AuxiliaryAsset]]:
    """Decode an IMG file into per-descriptor pages.

    Ghidra grounding:
    - FUN_8011d804 only reads descriptor fields +0x04, +0x08, +0x10, +0x14, +0x20, +0x24
      (CLUT and TPage params); it never consumes any IMG-relative pointer from a
      descriptor. The IMG-to-VRAM uploader is not present in the visible call graph
      (FUN_80152a80 is a small 512-byte-block helper used only by FUN_8011c854 and
      FUN_801299a4, neither of which carries scene textures), so we make no bbox or
      packing assumption beyond what the descriptor list itself states.
    - Each descriptor's VRAM rectangle is vram_width cells x vram_height pixels at 2
      bytes/cell, giving byte_size = vram_width*vram_height*2 (already computed in
      read_descriptor_record). We slice the IMG tail as a concatenation of those byte_size
      blocks in declared descriptor order. Anything before that tail is preserved as a
      leading auxiliary payload (commonly a VAB container) instead of being discarded.
    """
    if not records:
        raise ValueError('Cannot decode IMG without descriptor records')

    selection = select_img_payload(img_data, records)

    if not selection.trusted_graphics:
        auxiliary_assets: list[AuxiliaryAsset] = [
            AuxiliaryAsset(
                kind='img_audio_vab',
                img_offset=0,
                source_layout=selection.description,
                raw_bytes=bytes(img_data),
                row_byte_width=None,
                row_count=None,
            )
        ]
        placeholder_pages: list[PageData] = []
        for record in records:
            palette_words, palette_source = resolve_palette_words(img_data, dat_data, record)
            placeholder_pages.append(
                PageData(
                    descriptor=record,
                    img_offset=0,
                    source_layout=f'no IMG bytes (audio-only IMG){palette_source}',
                    raw_bytes=b'',
                    indices=[],
                    palette_words=palette_words,
                )
            )
        layout = (
            f'no graphics decoded from IMG: {selection.description}; '
            f'descriptor metadata and CLUTs are still emitted from the DAT'
        )
        return layout, placeholder_pages, auxiliary_assets

    payload = selection.data
    expected_size = sum(record.byte_size for record in records)
    if expected_size == 0 or len(payload) < expected_size:
        raise ValueError(
            f'IMG payload (0x{len(payload):X} bytes) cannot accommodate concatenated descriptor '
            f'block (need 0x{expected_size:X})'
        )

    auxiliary_assets: list[AuxiliaryAsset] = []
    if selection.offset > 0:
        auxiliary_assets.append(
            AuxiliaryAsset(
                kind='img_leading_aux',
                img_offset=0,
                source_layout=f'leading 0x{selection.offset:X} bytes before descriptor block',
                raw_bytes=img_data[:selection.offset],
                row_byte_width=None,
                row_count=None,
            )
        )

    if len(payload) > expected_size:
        trailing_offset = selection.offset + expected_size
        trailing_bytes = bytes(payload[expected_size:])
        auxiliary_assets.append(
            AuxiliaryAsset(
                kind='img_trailing_aux',
                img_offset=trailing_offset,
                source_layout=f'trailing 0x{len(trailing_bytes):X} bytes after descriptor block',
                raw_bytes=trailing_bytes,
                row_byte_width=None,
                row_count=None,
            )
        )

    pages: list[PageData] = []
    cursor = 0
    for record in records:
        raw_bytes = bytes(payload[cursor:cursor + record.byte_size])
        page_offset_in_payload = cursor
        cursor += record.byte_size
        palette_words, palette_source = resolve_palette_words(img_data, dat_data, record)
        pages.append(
            PageData(
                descriptor=record,
                img_offset=selection.offset + page_offset_in_payload,
                source_layout=(
                    f'descriptor_block+0x{page_offset_in_payload:X} '
                    f'({record.vram_width}x{record.vram_height} cells, 0x{record.byte_size:X} bytes)'
                    f'{palette_source}'
                ),
                raw_bytes=raw_bytes,
                indices=decode_page_indices(raw_bytes, record),
                palette_words=palette_words,
            )
        )

    layout = (
        f'sequential per-descriptor blit: {len(records)} records, total 0x{expected_size:X} bytes; '
        f'{selection.description}'
    )
    return layout, pages, auxiliary_assets


def psx_color_to_rgba(color: int) -> Pixel:
    red = color & 0x1F
    green = (color >> 5) & 0x1F
    blue = (color >> 10) & 0x1F
    alpha = 0 if color == 0 else (0x80 if color & 0x8000 else 0xFF)
    return (
        (red * 255 + 15) // 31,
        (green * 255 + 15) // 31,
        (blue * 255 + 15) // 31,
        alpha,
    )


def sample_page_pixel(page: PageData, x: int, y: int, palette_slot: int, *, zero_index_transparent: bool = True) -> Pixel:
    descriptor = page.descriptor
    if not (0 <= x < descriptor.texel_width and 0 <= y < descriptor.texel_height):
        return 0, 0, 0, 0

    if descriptor.mode == 0:
        index = page.indices[y * descriptor.texel_width + x] & 0x0F
        if zero_index_transparent and index == 0:
            return 0, 0, 0, 0
        palette_index = min(255, palette_slot * 16 + index)
        return psx_color_to_rgba(page.palette_words[palette_index])

    if descriptor.mode == 1:
        index = page.indices[y * descriptor.texel_width + x]
        if zero_index_transparent and index == 0:
            return 0, 0, 0, 0
        return psx_color_to_rgba(page.palette_words[index])

    word_index = (y * descriptor.texel_width + x) * 2
    color = read_u16(page.raw_bytes, word_index)
    return psx_color_to_rgba(color)


def render_byteview_panels(img_data: bytes, output_dir: Path) -> list[str]:
    messages: list[str] = []
    if len(img_data) % RAW_PANEL_SIZE != 0:
        return messages

    panel_count = len(img_data) // RAW_PANEL_SIZE
    composite_pixels: list[Pixel] = []

    for panel_index in range(panel_count):
        panel_data = img_data[panel_index * RAW_PANEL_SIZE:(panel_index + 1) * RAW_PANEL_SIZE]
        panel_pixels = [(value, value, value, 0xFF) for value in panel_data]
        panel_path = output_dir / f'debug_raw_panel_{panel_index:02d}_256x256_gray.png'
        write_png(panel_path, RAW_PANEL_WIDTH, panel_pixels)
        messages.append(f'Wrote raw byte-view panel {panel_index}: {panel_path.name}')

    for row in range(RAW_PANEL_HEIGHT):
        for panel_index in range(panel_count):
            panel_data = img_data[panel_index * RAW_PANEL_SIZE:(panel_index + 1) * RAW_PANEL_SIZE]
            start = row * RAW_PANEL_WIDTH
            row_data = panel_data[start:start + RAW_PANEL_WIDTH]
            composite_pixels.extend((value, value, value, 0xFF) for value in row_data)

    composite_path = output_dir / f'debug_raw_atlas_{panel_count * RAW_PANEL_WIDTH}x{RAW_PANEL_HEIGHT}_gray.png'
    write_png(composite_path, panel_count * RAW_PANEL_WIDTH, composite_pixels)
    messages.append(f'Wrote raw byte-view composite: {composite_path.name}')
    return messages


def render_4bpp_palette_grid(page: PageData, output_dir: Path) -> str:
    descriptor = page.descriptor
    grid_columns = 4
    grid_rows = 4
    output_width = descriptor.texel_width * grid_columns
    output_height = descriptor.texel_height * grid_rows
    pixels = [(0, 0, 0, 0)] * (output_width * output_height)

    for palette_slot in range(16):
        grid_x = (palette_slot % grid_columns) * descriptor.texel_width
        grid_y = (palette_slot // grid_columns) * descriptor.texel_height
        for source_y in range(descriptor.texel_height):
            destination_row = (grid_y + source_y) * output_width
            for source_x in range(descriptor.texel_width):
                pixel = sample_page_pixel(page, source_x, source_y, palette_slot)
                pixels[destination_row + grid_x + source_x] = pixel

    path = output_dir / f'desc_{descriptor.index:02d}_all_slots_{output_width}x{output_height}_4bpp.png'
    write_png(path, output_width, pixels)
    return path.name


def render_page_preview(page: PageData, palette_slot: int, output_dir: Path) -> str:
    descriptor = page.descriptor
    pixels = [
        sample_page_pixel(page, x, y, palette_slot)
        for y in range(descriptor.texel_height)
        for x in range(descriptor.texel_width)
    ]
    mode_name = {0: '4bpp', 1: '8bpp', 2: '16bpp'}.get(descriptor.mode, f'mode{descriptor.mode}')
    path = output_dir / (
        f'desc_{descriptor.index:02d}_slot_{palette_slot:02d}_{descriptor.texel_width}x{descriptor.texel_height}_{mode_name}.png'
    )
    write_png(path, descriptor.texel_width, pixels)
    return path.name


def descriptor_mode_name(descriptor: DescriptorRecord) -> str:
    return {0: '4bpp', 1: '8bpp', 2: '16bpp'}.get(descriptor.mode, f'mode{descriptor.mode}')


def write_page_asset_dump(page: PageData, output_dir: Path) -> list[str]:
    descriptor = page.descriptor
    mode_name = descriptor_mode_name(descriptor)
    asset_stem = f'desc_{descriptor.index:02d}_{descriptor.texel_width}x{descriptor.texel_height}_{mode_name}'

    raw_path = output_dir / f'{asset_stem}.bin'
    raw_path.write_bytes(page.raw_bytes)

    clut_path = output_dir / f'{asset_stem}.clut.bin'
    clut_path.write_bytes(b''.join(struct.pack('<H', word) for word in page.palette_words))

    metadata = {
        'descriptorIndex': descriptor.index,
        'fileOffset': f'0x{descriptor.file_offset:X}',
        'ramAddress': f'0x{descriptor.ram_address:08X}',
        'imgOffset': f'0x{page.img_offset:X}',
        'sourceLayout': page.source_layout,
        'dataPointer': f'0x{descriptor.data_pointer:08X}',
        'dataOffset': f'0x{descriptor.data_offset:X}',
        'clut': {'x': descriptor.clut_x, 'y': descriptor.clut_y},
        'tpage': {'x': descriptor.tpage_x, 'y': descriptor.tpage_y},
        'vramSize': {'width': descriptor.vram_width, 'height': descriptor.vram_height},
        'decodedSize': {'width': descriptor.texel_width, 'height': descriptor.texel_height},
        'mode': descriptor.mode,
        'modeName': mode_name,
        'abr': descriptor.abr,
        'byteSize': descriptor.byte_size,
        'indexCount': len(page.indices),
        'paletteWordCount': len(page.palette_words),
        'paletteSlots': list(range(16)) if descriptor.mode == 0 else [0],
        'rawFile': raw_path.name,
        'clutFile': clut_path.name,
        'previewFiles': [
            f'desc_{descriptor.index:02d}_slot_{slot:02d}_{descriptor.texel_width}x{descriptor.texel_height}_{mode_name}.png'
            for slot in (range(16) if descriptor.mode == 0 else (0,))
        ],
    }
    metadata_path = output_dir / f'{asset_stem}.json'
    metadata_path.write_text(json.dumps(metadata, indent=2), encoding='ascii')

    return [
        f'Extracted descriptor bytes: assets/{raw_path.name}',
        f'Extracted CLUT words: assets/{clut_path.name}',
        f'Wrote descriptor metadata: assets/{metadata_path.name}',
    ]


def write_auxiliary_asset_dump(asset: AuxiliaryAsset, output_dir: Path) -> list[str]:
    row_byte_width = asset.row_byte_width if asset.row_byte_width is not None else len(asset.raw_bytes)
    row_count = asset.row_count if asset.row_count is not None else 1
    asset_stem = f'aux_{asset.kind}_{row_byte_width:03X}bytes_{row_count:03X}rows'

    raw_path = output_dir / f'{asset_stem}.bin'
    raw_path.write_bytes(asset.raw_bytes)

    metadata = {
        'kind': asset.kind,
        'imgOffset': f'0x{asset.img_offset:X}',
        'imgOffsetMeaning': 'row-local source offset before deinterleaving' if asset.row_count else 'payload-relative source offset',
        'sourceLayout': asset.source_layout,
        'byteSize': len(asset.raw_bytes),
        'rowByteWidth': asset.row_byte_width,
        'rowCount': asset.row_count,
        'extraction': 'deinterleaved residual bytes from each source row' if asset.row_count else 'raw auxiliary payload bytes',
        'rawFile': raw_path.name,
    }
    metadata_path = output_dir / f'{asset_stem}.json'
    metadata_path.write_text(json.dumps(metadata, indent=2), encoding='ascii')

    return [
        f'Extracted auxiliary bytes: assets/{raw_path.name}',
        f'Wrote auxiliary metadata: assets/{metadata_path.name}',
    ]


def blit_tile(
    destination: list[Pixel],
    destination_width: int,
    page: PageData,
    palette_slot: int,
    source_x: int,
    source_y: int,
    destination_x: int,
    destination_y: int,
    tile_width: int,
    tile_height: int,
    flags: int,
    blend_alpha: bool = False,
    zero_index_transparent: bool = True,
) -> None:
    flip_y = (flags & 1) != 0
    flip_x = (flags & 2) != 0

    for local_y in range(tile_height):
        sample_y = source_y + (tile_height - 1 - local_y if flip_y else local_y)
        for local_x in range(tile_width):
            sample_x = source_x + (tile_width - 1 - local_x if flip_x else local_x)
            pixel = sample_page_pixel(
                page,
                sample_x,
                sample_y,
                palette_slot,
                zero_index_transparent=zero_index_transparent,
            )
            if pixel[3] == 0:
                continue

            destination_index = (destination_y + local_y) * destination_width + destination_x + local_x
            if blend_alpha:
                destination[destination_index] = alpha_composite_pixel(destination[destination_index], pixel)
            else:
                destination[destination_index] = pixel


def alpha_composite_pixel(destination: Pixel, source: Pixel) -> Pixel:
    source_alpha = source[3]
    if source_alpha == 0:
        return destination
    if source_alpha == 0xFF or destination[3] == 0:
        return source

    destination_alpha = destination[3]
    inverse_source_alpha = 0xFF - source_alpha
    destination_weight = destination_alpha * inverse_source_alpha
    output_alpha_numerator = source_alpha * 0xFF + destination_weight
    if output_alpha_numerator == 0:
        return 0, 0, 0, 0

    output_alpha = (output_alpha_numerator + 0x7F) // 0xFF

    def blend_channel(source_channel: int, destination_channel: int) -> int:
        numerator = source_channel * source_alpha * 0xFF + destination_channel * destination_weight
        return (numerator + output_alpha_numerator // 2) // output_alpha_numerator

    return (
        blend_channel(source[0], destination[0]),
        blend_channel(source[1], destination[1]),
        blend_channel(source[2], destination[2]),
        output_alpha,
    )


def should_skip_masked_tile(
    bank: BankDescriptor,
    cell_x: int,
    cell_y: int,
    occupancy: list[list[int]],
) -> bool:
    if (bank.flags & 0x4) != 0 or cell_x == 0 or cell_y == 0:
        return False

    return (
        occupancy[cell_y][cell_x] != 0
        and occupancy[cell_y][cell_x + 1] != 0
        and occupancy[cell_y + 1][cell_x] != 0
        and occupancy[cell_y + 1][cell_x + 1] != 0
    )


def update_mask_occupancy(
    bank: BankDescriptor,
    sprite_entry: SpriteEntry,
    cell_x: int,
    cell_y: int,
    occupancy: list[list[int]],
) -> None:
    if (bank.flags & 0x4) == 0 and (sprite_entry.flags & 0x0C) == 0x08:
        occupancy[cell_y][cell_x] += 1


def draw_bank_tiles(
    destination: list[Pixel],
    destination_width: int,
    dat_data: bytes,
    bank: BankDescriptor,
    relocation_base: int,
    pages_by_ram_address: dict[int, PageData],
    *,
    blend_alpha: bool = False,
) -> None:
    remap_table = build_tile_remap_table(dat_data, bank, relocation_base)
    occupancy = [[0] * (bank.map_width + 1) for _ in range(bank.map_height + 1)]

    for tile_y in range(bank.map_height):
        for tile_x in range(bank.map_width):
            tile_value = bank.tile_values[tile_y * bank.map_width + tile_x]
            raw_sprite_index = tile_value & 0x0FFF
            if raw_sprite_index == 0:
                continue

            sprite_index = remap_table[raw_sprite_index]
            if sprite_index == 0:
                continue

            sprite_entry = read_sprite_entry(dat_data, bank.sprite_offset, sprite_index)
            if sprite_entry is None:
                continue

            page = resolve_sprite_page(dat_data, bank, sprite_entry, pages_by_ram_address)
            if page is None:
                continue

            if should_skip_masked_tile(bank, tile_x, tile_y, occupancy):
                continue

            palette_slot = sprite_entry.palette_slot if page.descriptor.mode == 0 else 0
            blit_tile(
                destination,
                destination_width,
                page,
                palette_slot,
                sprite_entry.u0,
                sprite_entry.v0,
                tile_x * bank.tile_width,
                tile_y * bank.tile_height,
                bank.tile_width,
                bank.tile_height,
                sprite_entry.flags,
                blend_alpha=blend_alpha,
            )
            update_mask_occupancy(bank, sprite_entry, tile_x, tile_y, occupancy)


def visible_tile_counts(bank: BankDescriptor) -> tuple[int, int]:
    def count_visible(axis_pixels: int, tile_pixels: int) -> int:
        base = axis_pixels // tile_pixels
        return base + 3 if axis_pixels % tile_pixels == 0 else base + 4

    return count_visible(320, bank.tile_width), count_visible(240, bank.tile_height)


def remap_tile_index(tile_index: int, remap_table: list[int]) -> int:
    if tile_index == 0:
        return 0
    if 0 <= tile_index < len(remap_table):
        return remap_table[tile_index]
    return tile_index


def render_runtime_viewport_sheet(
    dat_data: bytes,
    bank: BankDescriptor,
    relocation_base: int,
    pages_by_ram_address: dict[int, PageData],
    output_dir: Path,
) -> str:
    output_width = 320
    output_height = 240
    pixels = [(0, 0, 0, 0)] * (output_width * output_height)

    draw_runtime_viewport_tiles(
        pixels,
        output_width,
        output_height,
        dat_data,
        bank,
        relocation_base,
        pages_by_ram_address,
    )

    path = output_dir / f'runtime_bank_{bank.index:02d}_{output_width}x{output_height}.png'
    write_png(path, output_width, pixels)
    return path.name


def draw_runtime_viewport_tiles(
    destination: list[Pixel],
    destination_width: int,
    destination_height: int,
    dat_data: bytes,
    bank: BankDescriptor,
    relocation_base: int,
    pages_by_ram_address: dict[int, PageData],
) -> None:
    remap_table = build_tile_remap_table(dat_data, bank, relocation_base)
    visible_columns, visible_rows = visible_tile_counts(bank)
    occupancy = [[0] * (visible_columns + 1) for _ in range(visible_rows + 1)]

    scroll_x = bank.initial_scroll_x
    scroll_y = bank.initial_scroll_y
    scroll_tile_x, scroll_frac_x = divmod(scroll_x, bank.tile_width)
    scroll_tile_y, scroll_frac_y = divmod(scroll_y, bank.tile_height)

    for view_tile_y in range(1, visible_rows):
        for view_tile_x in range(1, visible_columns):
            map_x = scroll_tile_x + view_tile_x - 2
            map_y = scroll_tile_y + view_tile_y - 2

            if (bank.flags & 0x8) != 0:
                map_x %= bank.map_width
                map_y %= bank.map_height
            elif not (0 <= map_x < bank.map_width and 0 <= map_y < bank.map_height):
                continue

            tile_value = bank.tile_values[map_y * bank.map_width + map_x]
            raw_sprite_index = remap_tile_index(tile_value & 0x0FFF, remap_table)
            if raw_sprite_index == 0:
                continue

            sprite_entry = read_sprite_entry(dat_data, bank.sprite_offset, raw_sprite_index)
            if sprite_entry is None:
                continue

            page = resolve_sprite_page(dat_data, bank, sprite_entry, pages_by_ram_address)
            if page is None:
                continue

            if should_skip_masked_tile(bank, view_tile_x, view_tile_y, occupancy):
                continue

            palette_slot = sprite_entry.palette_slot if page.descriptor.mode == 0 else 0
            destination_x = view_tile_x * bank.tile_width - scroll_frac_x - bank.tile_width
            destination_y = view_tile_y * bank.tile_height - scroll_frac_y - bank.tile_height

            for local_y in range(bank.tile_height):
                target_y = destination_y + local_y
                if not (0 <= target_y < destination_height):
                    continue
                for local_x in range(bank.tile_width):
                    target_x = destination_x + local_x
                    if not (0 <= target_x < destination_width):
                        continue

                    sample_x = sprite_entry.u0 + (bank.tile_width - 1 - local_x if (sprite_entry.flags & 2) != 0 else local_x)
                    sample_y = sprite_entry.v0 + (bank.tile_height - 1 - local_y if (sprite_entry.flags & 1) != 0 else local_y)
                    pixel = sample_page_pixel(page, sample_x, sample_y, palette_slot)
                    if pixel[3] == 0:
                        continue
                    target_index = target_y * destination_width + target_x
                    destination[target_index] = alpha_composite_pixel(destination[target_index], pixel)

            update_mask_occupancy(bank, sprite_entry, view_tile_x, view_tile_y, occupancy)


def render_runtime_viewport_composite_sheet(
    dat_data: bytes,
    banks: list[BankDescriptor],
    relocation_base: int,
    pages_by_ram_address: dict[int, PageData],
    output_dir: Path,
    descending: bool,
) -> str:
    output_width = 320
    output_height = 240
    pixels = [(0, 0, 0, 0)] * (output_width * output_height)
    ordered_banks = sorted(banks, key=lambda candidate: candidate.index, reverse=descending)

    for bank in ordered_banks:
        draw_runtime_viewport_tiles(
            pixels,
            output_width,
            output_height,
            dat_data,
            bank,
            relocation_base,
            pages_by_ram_address,
        )

    order_name = 'desc' if descending else 'asc'
    path = output_dir / f'runtime_all_banks_{order_name}_{output_width}x{output_height}.png'
    write_scene_png_variants(path, output_width, pixels)
    return path.name


def render_active_bank_sheet(
    dat_data: bytes,
    bank: BankDescriptor,
    relocation_base: int,
    pages_by_ram_address: dict[int, PageData],
    output_dir: Path,
) -> str:
    output_width = bank.map_width * bank.tile_width
    output_height = bank.map_height * bank.tile_height
    pixels = [(0, 0, 0, 0)] * (output_width * output_height)

    draw_bank_tiles(pixels, output_width, dat_data, bank, relocation_base, pages_by_ram_address)

    path = output_dir / f'active_bank_{bank.index:02d}_{output_width}x{output_height}.png'
    write_scene_png_variants(path, output_width, pixels)
    return path.name


def render_bank_sprite_sheet(
    dat_data: bytes,
    bank: BankDescriptor,
    relocation_base: int,
    pages_by_ram_address: dict[int, PageData],
    output_dir: Path,
) -> str | None:
    usage = analyze_bank_usage(dat_data, bank, relocation_base)
    sprite_entries: list[tuple[SpriteEntry, PageData]] = []

    for sprite_index in usage.used_sprite_indices:
        sprite_entry = read_sprite_entry(dat_data, bank.sprite_offset, sprite_index)
        if sprite_entry is None:
            continue

        page = resolve_sprite_page(dat_data, bank, sprite_entry, pages_by_ram_address)
        if page is None:
            continue

        sprite_entries.append((sprite_entry, page))

    if not sprite_entries:
        return None

    columns = min(8, max(1, len(sprite_entries)))
    rows = (len(sprite_entries) + columns - 1) // columns
    output_width = columns * bank.tile_width
    output_height = rows * bank.tile_height
    pixels = [(0, 0, 0, 0)] * (output_width * output_height)

    for entry_index, (sprite_entry, page) in enumerate(sprite_entries):
        palette_slot = sprite_entry.palette_slot if page.descriptor.mode == 0 else 0
        destination_x = (entry_index % columns) * bank.tile_width
        destination_y = (entry_index // columns) * bank.tile_height
        blit_tile(
            pixels,
            output_width,
            page,
            palette_slot,
            sprite_entry.u0,
            sprite_entry.v0,
            destination_x,
            destination_y,
            bank.tile_width,
            bank.tile_height,
            sprite_entry.flags,
        )

    path = output_dir / (
        f'bank_{bank.index:02d}_used_sprites_{len(sprite_entries):03d}_{output_width}x{output_height}.png'
    )
    write_png(path, output_width, pixels)
    return path.name


def render_bank_composite_sheet(
    dat_data: bytes,
    banks: list[BankDescriptor],
    relocation_base: int,
    pages_by_ram_address: dict[int, PageData],
    output_dir: Path,
    descending: bool,
) -> str:
    ordered_banks = sorted(banks, key=lambda candidate: candidate.index, reverse=descending)
    output_width = max(bank.map_width * bank.tile_width for bank in ordered_banks)
    output_height = max(bank.map_height * bank.tile_height for bank in ordered_banks)
    pixels = [(0, 0, 0, 0)] * (output_width * output_height)

    for bank in ordered_banks:
        draw_bank_tiles(
            pixels,
            output_width,
            dat_data,
            bank,
            relocation_base,
            pages_by_ram_address,
            blend_alpha=True,
        )

    order_name = 'desc' if descending else 'asc'
    path = output_dir / f'all_banks_{order_name}_{output_width}x{output_height}.png'
    write_scene_png_variants(path, output_width, pixels)
    return path.name


def render_final_scene_blackbase_sheet(
    dat_data: bytes,
    banks: list[BankDescriptor],
    relocation_base: int,
    pages_by_ram_address: dict[int, PageData],
    output_dir: Path,
) -> str:
    ordered_banks = sorted(banks, key=lambda candidate: candidate.index)
    output_width = max(bank.map_width * bank.tile_width for bank in ordered_banks)
    output_height = max(bank.map_height * bank.tile_height for bank in ordered_banks)
    pixels = [(0, 0, 0, 0)] * (output_width * output_height)

    for bank in ordered_banks:
        draw_bank_tiles(
            pixels,
            output_width,
            dat_data,
            bank,
            relocation_base,
            pages_by_ram_address,
            blend_alpha=True,
        )

    path = output_dir / blackbase_variant_name(f'all_banks_asc_{output_width}x{output_height}.png')
    write_blackbase_scene_png(path, output_width, pixels)
    return path.name


def render_bank_composite_masked_sheet(
    dat_data: bytes,
    banks: list[BankDescriptor],
    relocation_base: int,
    pages_by_ram_address: dict[int, PageData],
    output_dir: Path,
    descending: bool,
) -> str:
    ordered_banks = sorted(banks, key=lambda candidate: candidate.index, reverse=descending)
    output_width = max(bank.map_width * bank.tile_width for bank in ordered_banks)
    output_height = max(bank.map_height * bank.tile_height for bank in ordered_banks)
    pixels = [(0, 0, 0, 0)] * (output_width * output_height)
    occupancy_width = max(bank.map_width for bank in ordered_banks) + 1
    occupancy_height = max(bank.map_height for bank in ordered_banks) + 1
    occupancy = [[0] * occupancy_width for _ in range(occupancy_height)]

    for bank in ordered_banks:
        remap_table = build_tile_remap_table(dat_data, bank, relocation_base)
        for tile_y in range(bank.map_height):
            for tile_x in range(bank.map_width):
                tile_value = bank.tile_values[tile_y * bank.map_width + tile_x]
                raw_sprite_index = tile_value & 0x0FFF
                if raw_sprite_index == 0:
                    continue

                sprite_index = remap_table[raw_sprite_index]
                if sprite_index == 0:
                    continue

                sprite_entry = read_sprite_entry(dat_data, bank.sprite_offset, sprite_index)
                if sprite_entry is None:
                    continue

                page = resolve_sprite_page(dat_data, bank, sprite_entry, pages_by_ram_address)
                if page is None:
                    continue

                if (bank.flags & 0x4) == 0 and tile_x != 0 and tile_y != 0:
                    if (
                        occupancy[tile_y][tile_x] != 0
                        and occupancy[tile_y][tile_x + 1] != 0
                        and occupancy[tile_y + 1][tile_x] != 0
                        and occupancy[tile_y + 1][tile_x + 1] != 0
                    ):
                        continue

                palette_slot = sprite_entry.palette_slot if page.descriptor.mode == 0 else 0
                blit_tile(
                    pixels,
                    output_width,
                    page,
                    palette_slot,
                    sprite_entry.u0,
                    sprite_entry.v0,
                    tile_x * bank.tile_width,
                    tile_y * bank.tile_height,
                    bank.tile_width,
                    bank.tile_height,
                    sprite_entry.flags,
                    blend_alpha=True,
                )

                if (bank.flags & 0x4) == 0 and (sprite_entry.flags & 0x0C) == 0x08:
                    occupancy[tile_y][tile_x] += 1

    order_name = 'desc' if descending else 'asc'
    path = output_dir / f'all_banks_masked_{order_name}_{output_width}x{output_height}.png'
    write_scene_png_variants(path, output_width, pixels)
    return path.name


def render_bank_composite_flag8_fill_sheet(
    dat_data: bytes,
    banks: list[BankDescriptor],
    relocation_base: int,
    pages_by_ram_address: dict[int, PageData],
    output_dir: Path,
    descending: bool,
) -> str:
    ordered_banks = sorted(banks, key=lambda candidate: candidate.index, reverse=descending)
    output_width = max(bank.map_width * bank.tile_width for bank in ordered_banks)
    output_height = max(bank.map_height * bank.tile_height for bank in ordered_banks)
    pixels = [(0, 0, 0, 0)] * (output_width * output_height)

    for bank in ordered_banks:
        remap_table = build_tile_remap_table(dat_data, bank, relocation_base)
        for tile_y in range(bank.map_height):
            for tile_x in range(bank.map_width):
                tile_value = bank.tile_values[tile_y * bank.map_width + tile_x]
                raw_sprite_index = tile_value & 0x0FFF
                if raw_sprite_index == 0:
                    continue

                sprite_index = remap_table[raw_sprite_index]
                if sprite_index == 0:
                    continue

                sprite_entry = read_sprite_entry(dat_data, bank.sprite_offset, sprite_index)
                if sprite_entry is None:
                    continue

                page = resolve_sprite_page(dat_data, bank, sprite_entry, pages_by_ram_address)
                if page is None:
                    continue

                palette_slot = sprite_entry.palette_slot if page.descriptor.mode == 0 else 0
                blit_tile(
                    pixels,
                    output_width,
                    page,
                    palette_slot,
                    sprite_entry.u0,
                    sprite_entry.v0,
                    tile_x * bank.tile_width,
                    tile_y * bank.tile_height,
                    bank.tile_width,
                    bank.tile_height,
                    sprite_entry.flags,
                    blend_alpha=True,
                    zero_index_transparent=(sprite_entry.flags & 0x8) == 0,
                )

    order_name = 'desc' if descending else 'asc'
    path = output_dir / f'all_banks_flag8fill_{order_name}_{output_width}x{output_height}.png'
    write_scene_png_variants(path, output_width, pixels)
    return path.name


def blackbase_variant_name(name: str) -> str:
    stem, suffix = name.rsplit('.', 1)
    return f'{stem}_blackbase.{suffix}'


def write_scene_png_variants(path: Path, width: int, pixels: list[Pixel]) -> None:
    write_png(path, width, pixels)
    blackbase_pixels = [alpha_composite_pixel((0, 0, 0, 0xFF), pixel) for pixel in pixels]
    write_png(path.with_name(blackbase_variant_name(path.name)), width, blackbase_pixels)


def write_blackbase_scene_png(path: Path, width: int, pixels: list[Pixel]) -> None:
    blackbase_pixels = [alpha_composite_pixel((0, 0, 0, 0xFF), pixel) for pixel in pixels]
    write_png(path, width, blackbase_pixels)


def write_png(path: Path, width: int, pixels: list[Pixel]) -> None:
    if len(pixels) % width != 0:
        raise ValueError(f'Pixel count {len(pixels)} is not divisible by width {width}')

    height = len(pixels) // width
    raw_image = bytearray()
    for row in range(height):
        raw_image.append(0)
        start = row * width
        end = start + width
        for red, green, blue, alpha in pixels[start:end]:
            raw_image.extend((red, green, blue, alpha))

    def make_chunk(chunk_type: bytes, data: bytes) -> bytes:
        return (
            struct.pack('>I', len(data))
            + chunk_type
            + data
            + struct.pack('>I', zlib.crc32(chunk_type + data) & 0xFFFFFFFF)
        )

    ihdr = struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)
    idat = zlib.compress(bytes(raw_image), level=9)

    with path.open('wb') as handle:
        handle.write(b'\x89PNG\r\n\x1a\n')
        handle.write(make_chunk(b'IHDR', ihdr))
        handle.write(make_chunk(b'IDAT', idat))
        handle.write(make_chunk(b'IEND', b''))


def prepare_output_directories(
    output_dir: Path,
    *,
    write_asset_outputs: bool,
    write_page_outputs: bool,
    write_scene_outputs: bool,
    write_row_outputs: bool,
    write_raw_outputs: bool,
    write_debug_outputs: bool,
) -> dict[str, Path]:
    directories = {
        'assets': output_dir / 'assets',
        'pages': output_dir / 'pages',
        'rows': output_dir / 'rows',
        'sprites': output_dir / 'sprites',
        'scene': output_dir / 'scene',
        'raw': output_dir / 'raw',
        'debug': output_dir / 'debug',
    }

    stale_root_patterns = [
        'desc_*.bmp',
        'desc_*.png',
        'rows_desc_*.bmp',
        'rows_desc_*.png',
        'regular_rows_*.bmp',
        'regular_rows_*.png',
        'img_part_desc_*.raw',
        'debug_raw_*.bmp',
        'debug_raw_*.png',
    ]
    for pattern in stale_root_patterns:
        for path in output_dir.glob(pattern):
            if path.is_file():
                path.unlink()

    enabled_directories: set[str] = set()
    if write_asset_outputs:
        enabled_directories.add('assets')
    if write_page_outputs:
        enabled_directories.add('pages')
    if write_row_outputs:
        enabled_directories.add('rows')
    if write_scene_outputs:
        enabled_directories.update({'sprites', 'scene'})
    if write_raw_outputs:
        enabled_directories.add('raw')
    if write_debug_outputs:
        enabled_directories.add('debug')

    for name, directory in directories.items():
        if name in enabled_directories:
            directory.mkdir(parents=True, exist_ok=True)
            for child in directory.iterdir():
                if child.is_file():
                    child.unlink()
            continue

        if not directory.exists():
            continue

        for child in directory.iterdir():
            if child.is_file():
                child.unlink()

        try:
            directory.rmdir()
        except OSError:
            pass

    return directories


def render_probe_outputs(
    img_data: bytes,
    dat_data: bytes,
    relocation_base: int,
    pages: list[PageData],
    auxiliary_assets: list[AuxiliaryAsset],
    output_dir: Path,
    active_bank: BankDescriptor | None = None,
    banks: list[BankDescriptor] | None = None,
    write_asset_outputs: bool = True,
    emit_page_outputs: bool = True,
    emit_bank_outputs: bool = True,
    write_raw_outputs: bool = False,
    write_debug_outputs: bool = False,
) -> list[str]:
    messages: list[str] = []
    write_scene_outputs = emit_bank_outputs and active_bank is not None
    directories = prepare_output_directories(
        output_dir,
        write_asset_outputs=write_asset_outputs,
        write_page_outputs=emit_page_outputs,
        write_scene_outputs=write_scene_outputs,
        write_row_outputs=False,
        write_raw_outputs=write_raw_outputs,
        write_debug_outputs=write_debug_outputs,
    )
    asset_dir = directories['assets']
    page_dir = directories['pages']
    sprite_dir = directories['sprites']
    scene_dir = directories['scene']
    raw_dir = directories['raw']
    debug_dir = directories['debug']
    pages_by_descriptor = {page.descriptor.index: page for page in pages}
    pages_by_ram_address = {page.descriptor.ram_address: page for page in pages}

    if write_raw_outputs:
        for page in pages:
            raw_path = raw_dir / f'img_part_desc_{page.descriptor.index:02d}.raw'
            raw_path.write_bytes(page.raw_bytes)
            messages.append(f'Wrote raw page {page.descriptor.index}: raw/{raw_path.name}')

    for page in pages:
        messages.extend(write_page_asset_dump(page, asset_dir))
    for asset in auxiliary_assets:
        messages.extend(write_auxiliary_asset_dump(asset, asset_dir))

    if emit_page_outputs:
        for descriptor_index, page in sorted(pages_by_descriptor.items()):
            if page.descriptor.mode == 0:
                for palette_slot in range(16):
                    preview_name = render_page_preview(page, palette_slot, page_dir)
                    messages.append(f'Wrote palette-backed page preview: pages/{preview_name}')
                continue

            preview_name = render_page_preview(page, 0, page_dir)
            messages.append(f'Wrote palette-backed page preview: pages/{preview_name}')

    if emit_bank_outputs and active_bank is not None:
        scene_banks = [bank for bank in (banks or []) if bank is not None]
        if not scene_banks:
            scene_banks = [active_bank]

        for bank in scene_banks:
            sprite_name = render_bank_sprite_sheet(
                dat_data,
                bank,
                relocation_base,
                pages_by_ram_address,
                sprite_dir,
            )
            if sprite_name is not None:
                messages.append(f'Wrote bank-used sprite sheet: sprites/{sprite_name}')

        scene_name = render_final_scene_blackbase_sheet(
            dat_data,
            scene_banks,
            relocation_base,
            pages_by_ram_address,
            scene_dir,
        )
        messages.append(f'Wrote final black-base scene sheet: scene/{scene_name}')

    if write_debug_outputs:
        debug_messages = render_byteview_panels(img_data, debug_dir)
        for message in debug_messages:
            messages.append(message.replace(': ', ': debug/'))
    return messages


def build_report(
    dat_path: Path,
    img_path: Path,
    output_dir: Path,
    *,
    write_raw_outputs: bool = False,
    write_debug_outputs: bool = False,
) -> str:
    dat_data = dat_path.read_bytes()
    img_data = img_path.read_bytes()
    descriptor_bank = find_pointer_backed_descriptor_bank(dat_data)
    img_magic = read_u32(img_data, 0) if len(img_data) >= 4 else 0
    is_vab_like = is_vab_like_payload(img_data)

    lines: list[str] = []
    lines.append(f'DAT: {dat_path.name} ({len(dat_data)} bytes)')
    lines.append(f'IMG: {img_path.name} ({len(img_data)} bytes)')
    if is_vab_like:
        lines.append('IMG type: VABp-style payload with leading audio container')
    else:
        lines.append('IMG type: non-VAB payload')
    lines.append('')

    first_u32 = read_u32_table(dat_data, 16)
    first_u16 = read_u16_table(dat_data, 32)
    lines.append('First 16 DAT u32 values:')
    lines.extend(f'  0x{index * 4:04X}: 0x{value:08X}' for index, value in enumerate(first_u32))
    lines.append('')
    lines.append('First 32 DAT u16 values:')
    lines.extend(f'  0x{index * 2:04X}: 0x{value:04X}' for index, value in enumerate(first_u16))
    lines.append('')

    lines.append('Entry byte 2 interpretation from FUN_8011d804:')
    lines.append('  low nibble -> index into the per-bank descriptor-pointer array (bank+0x10).')
    lines.append('  high nibble -> 16-pixel CLUT X addend before GetClut(...).')
    lines.append('FUN_8011d804 reads descriptor fields +0x04 (clut_x), +0x08 (clut_y), +0x10 (tpage_x),')
    lines.append('+0x14 (tpage_y), +0x20 (tp), +0x24 (abr); descriptor +0x00 is loader-side metadata only.')
    lines.append('Rendered previews are written as PNG with PSX texture alpha preserved (0x0000 transparent, bit15 semi-transparent).')
    lines.append('')

    if descriptor_bank is None:
        lines.append('No pointer-backed descriptor bank was found in the DAT, so no palette-backed renders were written.')
        return '\n'.join(lines) + '\n'

    relocation_base, descriptor_base, pointer_array_offset, records = descriptor_bank
    lines.append(f'Detected relocated DAT base: 0x{relocation_base:08X}')
    lines.append(
        f'Pointer-backed descriptor bank: {len(records)} records at DAT+0x{descriptor_base:05X}, '
        f'pointer array at DAT+0x{pointer_array_offset:05X}'
    )
    graphics_root = read_graphics_root(dat_data, relocation_base, pointer_array_offset)
    active_bank = get_active_bank(graphics_root)
    active_bank_usage = analyze_bank_usage(dat_data, active_bank, relocation_base) if active_bank is not None else None
    command_stream_anchor = read_command_stream_anchor(dat_data, relocation_base)

    if graphics_root is None:
        lines.append('No graphics root matched the recovered descriptor bank, so active-bank composition is unavailable.')
    else:
        lines.append(
            f'Graphics root: DAT+0x{graphics_root.file_offset:05X} active_bank={graphics_root.active_bank_index} '
            f'bank_array=DAT+0x{graphics_root.bank_array_offset:05X} '
            f'texture_array=DAT+0x{graphics_root.texture_array_offset:05X}'
        )
        if active_bank is not None and active_bank_usage is not None:
            remap_text = 'none' if active_bank.remap_offset is None else f'DAT+0x{active_bank.remap_offset:05X}'
            lines.append(
                f'Active bank [{active_bank.index}]: size=DAT+0x{active_bank.size_offset:05X} '
                f'sprite_table=DAT+0x{active_bank.sprite_offset:05X} '
                f'texture_slots=DAT+0x{active_bank.texture_array_offset:05X} '
                f'map={active_bank.map_width}x{active_bank.map_height} '
                f'tile={active_bank.tile_width}x{active_bank.tile_height} '
                f'scroll=({active_bank.initial_scroll_x},{active_bank.initial_scroll_y}) remap={remap_text}'
            )
            if active_bank_usage.used_sprite_indices:
                lines.append(
                    f'Active bank used sprite indices: {len(active_bank_usage.used_sprite_indices)} '
                    f'(0x{min(active_bank_usage.used_sprite_indices):X}..'
                    f'0x{max(active_bank_usage.used_sprite_indices):X})'
                )
            lines.append(f'Active bank descriptor-slot usage: {format_count_map(active_bank_usage.descriptor_counts)}')
            lines.append(f'Active bank palette usage: {format_count_map(active_bank_usage.palette_counts)}')
            lines.append(f'Active bank flag usage: {format_count_map(active_bank_usage.flag_counts, hex_keys=True)}')
            lines.append(f'Active bank tile high bits: {format_count_map(active_bank_usage.high_bits, hex_keys=True)}')
            if active_bank_usage.missing_sprite_indices:
                preview = ', '.join(f'0x{value:X}' for value in active_bank_usage.missing_sprite_indices[:8])
                lines.append(f'Missing sprite entries referenced by active bank: {preview}')
    lines.append('')

    lines.append('Scene command VM anchors:')
    if command_stream_anchor is None:
        lines.append(
            '  No valid command-stream base was recovered from the relocated pointer slot at '
            f'RAM 0x{COMMAND_STREAM_POINTER_RAM_ADDRESS:08X}.'
        )
    else:
        lines.append(
            f'  command_stream_ptr_slot=DAT+0x{command_stream_anchor.pointer_slot_offset:05X} '
            f'(RAM 0x{COMMAND_STREAM_POINTER_RAM_ADDRESS:08X}) -> '
            f'0x{command_stream_anchor.pointer_value:08X} / DAT+0x{command_stream_anchor.stream_offset:05X}'
        )
        if graphics_root is not None:
            delta = command_stream_anchor.pointer_slot_offset - graphics_root.file_offset
            lines.append(
                f'  pointer-slot relation to graphics root: root is at DAT+0x{graphics_root.file_offset:05X}, '
                f'so the command-stream slot sits {delta:+#x} bytes from the root header.'
            )
        lines.append('  First command-base words at DAT+stream_offset:')
        for base_index in range(0, len(command_stream_anchor.preview_words), 8):
            chunk = command_stream_anchor.preview_words[base_index:base_index + 8]
            preview_text = ' '.join(f'{word:04X}' for word in chunk)
            lines.append(f'    [{base_index:02d}] {preview_text}')
        lines.append(
            '  Ghidra-grounded interpretation: DAT_801B23D0 is the base of the 16-bit command-word array, '
            'but the runtime reader uses DAT_801FF310 as the current word cursor, so word 0 is not guaranteed '
            'to be the true first executed command in offline data.'
        )
        lines.append(
            '  The initial DAT_801FF310 cursor comes from a copied 0x22-byte runtime state record selected by '
            'FUN_80158C64/FUN_80158E90, and some opcodes can rewrite the cursor later, so full offline object '
            'walking is not emitted yet.'
        )
        lines.append(
            '  Current stable opcode anchors for future extraction: 0x04 = object spawn '
            '{slotIndex, graphicResourceId, coordA, coordB, variant, initialStateSelector}; '
            f'graphicResourceId resolves through EXE pointer table RAM 0x{RESOURCE_DESCRIPTOR_TABLE_RAM_ADDRESS:08X}.'
        )
    lines.append('')

    primary_startup_offset = PRIMARY_STARTUP_RECORD_BANK_RAM_ADDRESS - relocation_base
    secondary_startup_offset = SECONDARY_STARTUP_RECORD_BANK_RAM_ADDRESS - relocation_base
    secondary_metadata_offset = SECONDARY_STARTUP_METADATA_RAM_ADDRESS - relocation_base

    lines.append('Startup state selection:')
    lines.append(
        f'  primary_startup_bank=RAM 0x{PRIMARY_STARTUP_RECORD_BANK_RAM_ADDRESS:08X} '
        f'(relocated DAT+0x{primary_startup_offset:X}) stride=0x{STARTUP_RECORD_SIZE:X}'
    )
    lines.append(
        f'  secondary_startup_bank=RAM 0x{SECONDARY_STARTUP_RECORD_BANK_RAM_ADDRESS:08X} '
        f'(relocated DAT+0x{secondary_startup_offset:X}) stride=0x{STARTUP_RECORD_SIZE:X}'
    )
    lines.append(
        f'  secondary_metadata_bank=RAM 0x{SECONDARY_STARTUP_METADATA_RAM_ADDRESS:08X} '
        f'(relocated DAT+0x{secondary_metadata_offset:X}) stride=0x4C'
    )
    lines.append(
        '  Recovered startup selector rule from FUN_80158C64: scan the primary bank first in ascending slot order '
        'and choose the first record with u16(record+0x02) & 0x0001; only if no primary slot qualifies, '
        'scan the secondary bank using metadata bit 0x0010 in the paired 0x4C records.'
    )
    lines.append(
        '  The copied 0x22-byte record later seeds DAT_801FF310..., but the cold-start selection itself runs over '
        'runtime-built candidate banks rather than directly over the relocated scene DAT image.'
    )
    lines.append(
        '  Recovered builder-input split from FUN_801589D8: FUN_80158AA4 takes no meaningful caller arguments and '
        'seeds 5 fixed EXE-backed startup-source records through FUN_80158B08(pointer,index), using '
        f'RAM 0x{FIXED_STARTUP_POINTER_RAM_ADDRESS:08X} plus pointer table RAM 0x{FIXED_STARTUP_POINTER_TABLE_RAM_ADDRESS:08X}..DC. '
        'That fixed subset is reproducible offline.'
    )
    lines.append(
        '  The companion FUN_80158B40 path also takes no meaningful caller arguments, but it flattens two already-built '
        'runtime lists through FUN_80158BE8(nodePtr,runningIndex,bankFlag): '
        f'head RAM 0x{PRIMARY_STARTUP_LIST_HEAD_RAM_ADDRESS:08X} with bankFlag=0, then '
        f'head RAM 0x{SECONDARY_STARTUP_LIST_HEAD_RAM_ADDRESS:08X} with bankFlag=1. '
        'Those direct inputs are runtime-list state, not scene-DAT bytes at this layer.'
    )
    primary_in_dat = 0 <= primary_startup_offset <= len(dat_data) - STARTUP_RECORD_SIZE
    secondary_in_dat = 0 <= secondary_startup_offset <= len(dat_data) - STARTUP_RECORD_SIZE
    metadata_in_dat = 0 <= secondary_metadata_offset <= len(dat_data) - 0x4C
    if primary_in_dat or secondary_in_dat or metadata_in_dat:
        lines.append(
            '  Note: at least one startup-selection bank lands inside this relocated DAT image, so this pair may '
            'support deeper offline startup-state parsing than the currently validated scene-family samples.'
        )
    else:
        lines.append(
            '  For the currently validated scene-family samples these banks fall outside the relocated DAT image, '
            'which matches the current Ghidra model that startup selection depends on runtime-built candidate records.'
        )
    lines.append(
        '  Current extractor-safe fallback: report the rule above, but do not emit a guessed startup record or '
        'initial cursor from file data alone until the runtime-bank build path is emulated cleanly.'
    )
    lines.append('')

    payload_selection = select_img_payload(img_data, records)
    emit_page_outputs = (not is_vab_like) or payload_selection.trusted_graphics
    emit_bank_outputs = (not is_vab_like) or payload_selection.trusted_graphics

    try:
        image_layout, pages, auxiliary_assets = build_pages(img_data, dat_data, records)
    except ValueError as error:
        lines.append(f'Graphics decode skipped: {error}')
        lines.append('This DAT contains the same pointer-backed descriptor bank shape used by scene graphics,')
        lines.append('but this IMG payload does not match that bank and should be treated as a different resource type.')
        lines.append('')
        return '\n'.join(lines) + '\n'

    lines.append(f'IMG layout selected: {image_layout}')
    if payload_selection.offset != 0:
        lines.append(f'IMG graphics payload: {payload_selection.description}')
    if is_vab_like and not payload_selection.trusted_graphics:
        lines.append('IMG file is a PSY-Q VAB audio container with no trailing data. Page/scene/sprite renders')
        lines.append('are skipped; descriptor metadata and CLUT bytes are still emitted from the DAT under assets/.')
    elif is_vab_like and payload_selection.trusted_graphics:
        lines.append('IMG header is a PSY-Q VAB audio container. Ghidra-grounded layout:')
        lines.append('  - IMG file is loaded into RAM at base 0x800C9000.')
        lines.append('  - 0x00000..0x06000  VAB header staging (FUN_80128a34 copies these as the VH).')
        lines.append('  - 0x06000..audio_end  VAG audio body (FUN_80128aa8 transmits to SPU).')
        lines.append('  - 0x06000+pad..0x1F000  zero padding inside the loaded buffer; not used.')
        lines.append('  - 0x1F000..end       Graphics block, uploaded to VRAM by the per-scene installer')
        lines.append('                       in the EXE (e.g. FUN_8016a730 at 0x8016a730 uses literal source')
        lines.append('                       0x800E8000 = 0x800C9000 + 0x1F000). Per-page (img_offset, vram_x,')
        lines.append('                       vram_y, w, h) layout is hardcoded per-scene and is not derivable')
        lines.append('                       from the IMG file alone.')
    if active_bank is not None:
        lines.append(
            f'Runtime compositor tile step from active bank: {active_bank.tile_width}x{active_bank.tile_height}'
        )
    lines.append('')

    lines.append('Resolved descriptor records:')
    for record in records:
        mode_name = {0: '4bpp', 1: '8bpp', 2: '16bpp'}.get(record.mode, f'mode{record.mode}')
        lines.append(
            f'  [{record.index}] DAT+0x{record.file_offset:05X} RAM=0x{record.ram_address:08X} '
            f'palette=DAT+0x{record.data_offset:05X} tpage=({record.tpage_x:#x},{record.tpage_y:#x}) '
            f'size=0x{record.byte_size:X} '
            f'{record.texel_width}x{record.texel_height} {mode_name}'
        )
    lines.append('')

    if auxiliary_assets:
        lines.append('Additional extracted payload segments:')
        for asset in auxiliary_assets:
            lines.append(
                f'  {asset.kind}: {asset.source_layout} size=0x{len(asset.raw_bytes):X}'
            )
        lines.append('These bytes are preserved under assets/ instead of being silently discarded.')
        lines.append('')

    lines.extend(
        render_probe_outputs(
            payload_selection.data,
            dat_data,
            relocation_base,
            pages,
            auxiliary_assets,
            output_dir,
            active_bank,
            graphics_root.banks if graphics_root is not None else None,
            True,
            emit_page_outputs,
            emit_bank_outputs,
            write_raw_outputs=write_raw_outputs,
            write_debug_outputs=write_debug_outputs,
        )
    )
    return '\n'.join(lines) + '\n'


def main() -> None:
    parser = argparse.ArgumentParser(description='Probe Arc The Lad DAT/IMG pairs for relocated graphics structures.')
    parser.add_argument('dat_path', type=Path)
    parser.add_argument('img_path', type=Path)
    parser.add_argument('--output-dir', type=Path)
    parser.add_argument('--write-report', action='store_true', help='Write the detailed text report to probe_report.txt.')
    parser.add_argument('--write-raw', action='store_true', help='Write additional raw descriptor slices under raw/.')
    parser.add_argument('--write-debug', action='store_true', help='Write byte-view debug panels under debug/.')
    args = parser.parse_args()

    output_dir = args.output_dir or args.dat_path.with_suffix('')
    output_dir.mkdir(parents=True, exist_ok=True)

    report = build_report(
        args.dat_path,
        args.img_path,
        output_dir,
        write_raw_outputs=args.write_raw,
        write_debug_outputs=args.write_debug,
    )
    report_path = output_dir / 'probe_report.txt'
    if args.write_report:
        report_path.write_text(report, encoding='ascii')
        print(report)
        print(f'Report written to {report_path}')
    elif report_path.exists():
        report_path.unlink()

    print(f'Wrote probe outputs under {output_dir}')
    print('Parsed descriptor assets are written under assets/.')
    if not args.write_report:
        print('Detailed text report suppressed by default; pass --write-report to emit probe_report.txt.')
    if not args.write_raw:
        print('Additional raw slices suppressed by default; pass --write-raw to emit raw/.')
    if not args.write_debug:
        print('Byte-view debug panels suppressed by default; pass --write-debug to emit debug/.')


if __name__ == '__main__':
    main()
