from __future__ import annotations

import argparse
import struct
import sys
import zlib
from dataclasses import dataclass
from pathlib import Path


SCREEN_DESCRIPTOR_TABLE = 0x0003E474
DEFAULT_SAMPLE_SCREENS = (0, 1, 2)
LITERAL_COUNT_BASES = (6, 10, 10, 18)
LITERAL_COUNT_BITS = (1, 1, 1, 1, 2, 3, 3, 4, 4, 5, 7, 14)
CLEAR_ONE_MASKS = tuple(0xFFFF ^ (1 << bit) for bit in range(16))
POPCOUNT_8 = tuple(value.bit_count() for value in range(256))


def _build_c500_symbol_slot_table() -> tuple[int, ...]:
    slot_table: list[int] = []
    for mask in range(256):
        positions = [bit for bit in range(8) if mask & (1 << bit)]
        positions.extend([0] * (8 - len(positions)))
        slot_table.extend(positions[:8])
    return tuple(slot_table)


def _build_c500_symbol_tables() -> tuple[tuple[int, ...], tuple[int, ...]]:
    bits = [0] * 0x100
    thresholds = [0] * 0x100
    bits[0] = 8
    thresholds[0] = 7
    bits[1] = 0
    thresholds[1] = 8
    for count in range(2, 0x100):
        bit_count = count.bit_length() - 1
        bits[count] = bit_count
        if count == (1 << bit_count):
            thresholds[count] = count - 1
        else:
            thresholds[count] = (1 << (bit_count + 1)) - count - 1
    return tuple(bits), tuple(thresholds)


def _build_range_decode_tables() -> tuple[tuple[int, ...], tuple[int, ...]]:
    range_bits = [0] * 0x101
    range_thresholds = [0] * 0x101
    for count in range(1, 0x101):
        bits = count.bit_length() - 1
        range_bits[count] = bits
        if count == 1:
            range_thresholds[count] = 0
        elif count == (1 << bits):
            range_thresholds[count] = count - 1
        else:
            range_thresholds[count] = (1 << (bits + 1)) - count - 1
    return tuple(range_bits), tuple(range_thresholds)


RANGE_BITS, RANGE_THRESHOLDS = _build_range_decode_tables()
C500_SYMBOL_BITS, C500_SYMBOL_THRESHOLDS = _build_c500_symbol_tables()
C500_SYMBOL_SLOT_TABLE = _build_c500_symbol_slot_table()


@dataclass(frozen=True)
class ScreenDescriptor:
    index: int
    address: int
    room_block_ptr: int
    chunk_bank_ptr: int
    script_index: int
    overlay_records: tuple[tuple[int, int, int], ...]


@dataclass(frozen=True)
class RoomBlock:
    tile_count: int
    map_width: int
    map_height: int
    palette_words: tuple[int, ...]
    tilemap_words: tuple[int, ...]
    tile_bytes: bytes


@dataclass(frozen=True)
class ChunkTemplate:
    width: int
    height: int
    attr: int
    palette_words: tuple[int, ...]
    tilemap_words: tuple[int, ...]
    tile_count: int
    tile_bytes: bytes


def read_u16_be(data: bytes | memoryview, offset: int) -> int:
    return struct.unpack_from(">H", data, offset)[0]


def read_s16_be(data: bytes | memoryview, offset: int) -> int:
    return struct.unpack_from(">h", data, offset)[0]


def read_u32_be(data: bytes | memoryview, offset: int) -> int:
    return struct.unpack_from(">I", data, offset)[0]


def sign16(value: int) -> int:
    return value - 0x10000 if value & 0x8000 else value


class ImpBitStream:
    def __init__(self, block: memoryview, source_pos: int, seed_word: int) -> None:
        self._block = block
        self.source_pos = source_pos
        self.bit_buffer = seed_word & 0xFF
        if seed_word < 0x8000:
            self.source_pos -= 1

    def read_back_byte(self) -> int:
        if self.source_pos <= 0:
            raise ValueError("IMP stream underflow while reading backward byte")
        self.source_pos -= 1
        return self._block[self.source_pos]

    def read_bit(self) -> int:
        carry = 1 if (self.bit_buffer & 0x80) else 0
        self.bit_buffer = (self.bit_buffer << 1) & 0xFF
        if self.bit_buffer == 0:
            raw = self.read_back_byte()
            self.bit_buffer = ((raw << 1) & 0xFF) | carry
            carry = 1 if (raw & 0x80) else 0
        return carry

    def read_bits(self, count: int) -> int:
        value = 0
        for _ in range(count):
            value = (value << 1) | self.read_bit()
        return value


class C500BitReader:
    def __init__(self, data: bytes | memoryview) -> None:
        self._data = data
        self._position = 0
        self._buffer = 0
        self._bits_left = -1

    def _refill(self) -> None:
        if self._position + 2 > len(self._data):
            raise ValueError("C500 bitstream ended unexpectedly")
        self._buffer = ((self._data[self._position] << 8) | self._data[self._position + 1]) << 16
        self._position += 2
        self._bits_left = 15

    def read_bit(self) -> int:
        self._bits_left -= 1
        if self._bits_left < 0:
            self._refill()
        bit = 1 if (self._buffer & 0x80000000) else 0
        self._buffer = (self._buffer << 1) & 0xFFFFFFFF
        return bit

    def read_bits(self, count: int) -> int:
        value = 0
        for _ in range(count):
            value = (value << 1) | self.read_bit()
        return value


def decode_match_length(stream: ImpBitStream) -> tuple[int, int]:
    if stream.read_bit() == 0:
        return 1, 0
    if stream.read_bit() == 0:
        return 2, 1
    if stream.read_bit() == 0:
        return 3, 2
    if stream.read_bit() == 0:
        return 4, 3
    if stream.read_bit() == 1:
        return stream.read_back_byte() - 1, 3
    return stream.read_bits(3) + 5, 3


def decode_literal_count(stream: ImpBitStream, length_class: int) -> int:
    base = 0
    selector = length_class
    if stream.read_bit() == 1:
        if stream.read_bit() == 1:
            base = LITERAL_COUNT_BASES[length_class]
            selector = length_class + 8
        else:
            base = 2
            selector = length_class + 4
    return base + stream.read_bits(LITERAL_COUNT_BITS[selector])


def decode_copy_distance(
    stream: ImpBitStream,
    length_class: int,
    short_offsets: tuple[int, int, int, int],
    long_offsets: tuple[int, int, int, int],
    distance_bits: tuple[int, ...],
) -> int:
    base = 0
    selector = length_class
    if stream.read_bit() == 1:
        if stream.read_bit() == 1:
            base = long_offsets[length_class]
            selector = length_class + 8
        else:
            base = short_offsets[length_class]
            selector = length_class + 4
    return base + stream.read_bits(distance_bits[selector]) + 1


def c500_range_decode_small(reader: C500BitReader, count: int) -> int:
    if count <= 0:
        raise ValueError(f"invalid small range decode count: {count}")
    bits = RANGE_BITS[count]
    value = reader.read_bits(bits)
    threshold = RANGE_THRESHOLDS[count]
    if value <= threshold:
        return value
    return ((value << 1) | reader.read_bit()) - threshold - 1


def c500_range_decode(reader: C500BitReader, count: int) -> int:
    if count <= 0:
        raise ValueError(f"invalid range decode count: {count}")
    if count <= 0x100:
        return c500_range_decode_small(reader, count)

    first_group_size = ((count - 1) & 0xFF) + 1
    group_count = ((count - 1) >> 8) + 1
    group = c500_range_decode_small(reader, group_count)
    if group == 0:
        return c500_range_decode_small(reader, first_group_size)
    return first_group_size + ((group - 1) << 8) + reader.read_bits(8)


def c500_decode_symbol(reader: C500BitReader, mask: int, banned: tuple[int, ...] = ()) -> int:
    effective_mask = mask
    for symbol in banned:
        effective_mask &= CLEAR_ONE_MASKS[symbol & 0x0F]

    low_mask = effective_mask & 0xFF
    high_mask = (effective_mask >> 8) & 0xFF
    low_count = POPCOUNT_8[low_mask]
    total = low_count + POPCOUNT_8[high_mask]
    index = reader.read_bits(C500_SYMBOL_BITS[total])
    threshold = C500_SYMBOL_THRESHOLDS[total]
    if index > threshold:
        index = ((index << 1) | reader.read_bit()) - threshold - 1

    if index < low_count:
        slot = low_mask * 8 + index
        if slot >= len(C500_SYMBOL_SLOT_TABLE):
            raise ValueError(f"C500 low-symbol table overflow: mask=0x{mask:04X} index={index}")
        return C500_SYMBOL_SLOT_TABLE[slot]

    slot = high_mask * 8 + (index - low_count)
    if slot >= len(C500_SYMBOL_SLOT_TABLE):
        raise ValueError(f"C500 high-symbol table overflow: mask=0x{mask:04X} index={index}")
    return 8 + C500_SYMBOL_SLOT_TABLE[slot]


def c500_read_ctrl4(reader: C500BitReader) -> int:
    value = 0
    if reader.read_bit():
        value = ((0x0F ^ c500_range_decode_small(reader, 15)) << 4) & 0xF0
    if reader.read_bit():
        value = ((value | 0x0F) ^ c500_range_decode_small(reader, 15)) & 0xFF
    return value


def c500_expand_pair_ctrl(reader: C500BitReader, seed: int) -> int:
    value = seed & 0xFF
    for _ in range(8):
        carry = (value >> 7) & 1
        value = ((value << 1) | carry) & 0xFF
        if carry or value == 0xFE:
            continue
        if reader.read_bit():
            value = (value + 1) & 0xFF
    return value


def c500_read_ctrl3(reader: C500BitReader) -> int:
    value = 0
    if reader.read_bit():
        value = ((0x0F ^ c500_range_decode_small(reader, 15)) << 3) & 0xF8
    if reader.read_bit():
        value = ((value | 0x07) ^ c500_range_decode_small(reader, 7)) & 0xFF
    if value & 0x08:
        value ^= 0x07
    return value


def c500_next_ctrl_bit(ctrl: int) -> tuple[int, int]:
    return (ctrl >> 7) & 1, (ctrl << 1) & 0xFF


def c500_choose_from_current_or_above(
    reader: C500BitReader,
    mask: int,
    current: int,
    above: int,
    flag: int,
 ) -> tuple[int, int]:
    if flag < 0:
        if reader.read_bit() == 0:
            return above, flag
        if above == current:
            return c500_decode_symbol(reader, mask, (current,)), flag
        if reader.read_bit() == 0:
            return current, 0
        return c500_decode_symbol(reader, mask, (current, above)), flag

    if reader.read_bit() == 0:
        return current, flag
    if above == current:
        return c500_decode_symbol(reader, mask, (current,)), flag
    if reader.read_bit() == 0:
        return above, -1
    return c500_decode_symbol(reader, mask, (current, above)), flag


def c500_emit_chain_row(
    reader: C500BitReader,
    output: bytearray,
    out_pos: int,
    mask: int,
    ctrl: int,
    current: int,
) -> int:
    for byte_index in range(4):
        bit, ctrl = c500_next_ctrl_bit(ctrl)
        if bit == 0 and reader.read_bit():
            low = c500_decode_symbol(reader, mask, (current,))
        else:
            low = current

        output[out_pos] = ((current & 0x0F) << 4) | (low & 0x0F)
        out_pos += 1
        if byte_index == 3:
            break

        bit, ctrl = c500_next_ctrl_bit(ctrl)
        if bit == 0 and reader.read_bit():
            current = c500_decode_symbol(reader, mask, (low,))
        else:
            current = low

    return out_pos


def c500_emit_mode0(
    reader: C500BitReader,
    output: bytearray,
    out_pos: int,
    block_index: int,
    mask: int,
) -> int:
    if block_index <= 0:
        raise ValueError("C500 mode-0 block cannot reference a previous tile at index 0")

    ref_back = c500_range_decode(reader, block_index) + 1
    ref_pos = out_pos - ref_back * 32
    if ref_pos < 0:
        raise ValueError("C500 mode-0 block referenced output before the buffer start")

    seed = c500_read_ctrl4(reader)
    row_ctrl = c500_read_ctrl4(reader)

    for _ in range(8):
        copy_row, row_ctrl = c500_next_ctrl_bit(row_ctrl)
        if copy_row:
            row_ctrl |= 1
            output[out_pos:out_pos + 4] = output[ref_pos:ref_pos + 4]
            out_pos += 4
            ref_pos += 4
            continue

        pair_ctrl = seed if row_ctrl == 0xFE else c500_expand_pair_ctrl(reader, seed)
        for _ in range(4):
            source_byte = output[ref_pos]
            ref_pos += 1
            high = source_byte >> 4
            low = source_byte & 0x0F

            code = (pair_ctrl >> 6) & 0x03
            pair_ctrl = (pair_ctrl << 2) & 0xFF
            if code == 0:
                high = c500_decode_symbol(reader, mask, (high,))
                low = c500_decode_symbol(reader, mask, (low,))
            elif code == 1:
                high = c500_decode_symbol(reader, mask, (high,))
            elif code == 2:
                low = c500_decode_symbol(reader, mask, (low,))

            output[out_pos] = (high << 4) | low
            out_pos += 1

    return out_pos


def c500_emit_mode1(reader: C500BitReader, output: bytearray, out_pos: int, mask: int) -> int:
    row_copy_ctrl = c500_read_ctrl3(reader)
    row_ctrl_template = c500_read_ctrl3(reader)

    current = c500_decode_symbol(reader, mask)
    out_pos = c500_emit_chain_row(reader, output, out_pos, mask, row_ctrl_template, current)

    for _ in range(7):
        flag = 0
        copy_row, row_copy_ctrl = c500_next_ctrl_bit(row_copy_ctrl)
        if copy_row:
            output[out_pos:out_pos + 4] = output[out_pos - 4:out_pos]
            out_pos += 4
            continue

        ctrl = row_ctrl_template
        current = output[out_pos - 4] >> 4
        if reader.read_bit():
            current = c500_decode_symbol(reader, mask, (current,))

        for byte_index in range(4):
            bit, ctrl = c500_next_ctrl_bit(ctrl)
            if bit == 0:
                above_low = output[out_pos - 4] & 0x0F
                low, flag = c500_choose_from_current_or_above(reader, mask, current, above_low, flag)
            else:
                low = current

            output[out_pos] = ((current & 0x0F) << 4) | (low & 0x0F)
            out_pos += 1
            if byte_index == 3:
                break

            current = low
            bit, ctrl = c500_next_ctrl_bit(ctrl)
            if bit == 0:
                above_high = output[out_pos - 4] >> 4
                current, flag = c500_choose_from_current_or_above(reader, mask, current, above_high, flag)

    return out_pos


def decompress_c500_tail(data: bytes | memoryview) -> bytes:
    reader = C500BitReader(data)
    tile_count = reader.read_bits(8) | (reader.read_bits(2) << 8)
    seed_count = min(tile_count, 0x200)
    history_limit = c500_range_decode(reader, seed_count)
    history: list[int] = []

    for index in range(history_limit + 1):
        if index == 0 or reader.read_bit():
            history.append(reader.read_bits(16))
        else:
            base_index = c500_range_decode(reader, index)
            bit_index = reader.read_bits(4)
            history.append(history[-1 - base_index] ^ (1 << bit_index))

    visible = 1
    output = bytearray(tile_count * 0x20)
    out_pos = 0

    for block_index in range(tile_count):
        selection = c500_range_decode(reader, visible)
        if selection >= visible:
            raise ValueError("C500 history selection exceeded the visible mask count")

        selected_index = visible - selection - 1
        mask = history[selected_index]

        if selection == 0:
            visible += 1
            if visible > len(history):
                raise ValueError("C500 visible-mask count exceeded available history")

        mode = reader.read_bit()
        if mode == 0:
            out_pos = c500_emit_mode0(reader, output, out_pos, block_index, mask)
        else:
            out_pos = c500_emit_mode1(reader, output, out_pos, mask)

    if out_pos != len(output):
        raise ValueError(
            f"C500 output size mismatch: expected {len(output)} bytes, produced {out_pos}"
        )

    return bytes(output)


def decompress_imp_block(rom: memoryview, block_offset: int) -> bytes:
    block = rom[block_offset:]
    if bytes(block[:4]) != b"IMP!":
        raise ValueError(f"0x{block_offset:08X} does not point to an IMP block")

    output_size = read_u32_be(block, 4)
    trailer_offset = read_u32_be(block, 8)
    trailer_end = trailer_offset + 0x12 + 0x1C
    work_buffer_length = trailer_offset + 0x32
    if work_buffer_length > len(block):
        raise ValueError("IMP block trailer extends beyond available ROM data")

    # The original routine first copies the compressed block header/trailer region into a work
    # buffer, then overwrites the first 12 bytes with trailer data before the backward stream runs.
    # The source pointer can consume those patched bytes near the end of decompression, so reading
    # directly from ROM produces the wrong bitstream.
    work_buffer = bytearray(block[:work_buffer_length])
    trailer_header = work_buffer[trailer_offset:trailer_offset + 12]
    work_buffer[0:4] = trailer_header[8:12]
    work_buffer[4:8] = trailer_header[4:8]
    work_buffer[8:12] = trailer_header[0:4]
    work_view = memoryview(work_buffer)

    literal_count = read_u32_be(work_view, trailer_offset + 0x0C)
    seed_word = read_u16_be(work_view, trailer_offset + 0x10)
    short_offsets = tuple(
        read_s16_be(work_view, trailer_offset + 0x12 + index * 2) for index in range(4)
    )
    long_offsets = tuple(
        read_s16_be(work_view, trailer_offset + 0x1A + index * 2) for index in range(4)
    )
    distance_bits = tuple(work_view[trailer_offset + 0x22 + index] for index in range(12))

    stream = ImpBitStream(work_view, trailer_offset, seed_word)
    output = bytearray(output_size)
    out_pos = output_size

    while True:
        for _ in range(literal_count):
            if out_pos <= 0:
                raise ValueError("IMP literal run exceeds output buffer")
            output[out_pos - 1] = stream.read_back_byte()
            out_pos -= 1

        if out_pos <= 0:
            break

        match_length_minus_one, length_class = decode_match_length(stream)
        literal_count = decode_literal_count(stream, length_class)
        distance = decode_copy_distance(
            stream,
            length_class,
            short_offsets,
            long_offsets,
            distance_bits,
        )

        copy_source = out_pos + distance
        match_length = match_length_minus_one + 1
        if copy_source > output_size:
            raise ValueError("IMP back-reference points past decompressed output")

        for _ in range(match_length):
            if out_pos <= 0:
                raise ValueError("IMP match run exceeds output buffer")
            copy_source -= 1
            out_pos -= 1
            output[out_pos] = output[copy_source]

    if stream.source_pos != 0:
        raise ValueError(
            f"IMP decompression ended with {stream.source_pos} unread compressed bytes"
        )

    return bytes(output)


def parse_screen_descriptor(rom: bytes | memoryview, screen_index: int) -> ScreenDescriptor:
    descriptor_ptr = read_u32_be(rom, SCREEN_DESCRIPTOR_TABLE + screen_index * 4)
    if descriptor_ptr == 0:
        raise ValueError(f"screen {screen_index} has a null descriptor pointer")

    room_block_ptr = read_u32_be(rom, descriptor_ptr)
    chunk_bank_ptr = read_u32_be(rom, descriptor_ptr + 4)
    script_index = read_u16_be(rom, descriptor_ptr + 8)

    overlay_records: list[tuple[int, int, int]] = []
    cursor = descriptor_ptr + 0x0A
    while True:
        first_word = read_u16_be(rom, cursor)
        if first_word == 0xFFFF:
            break
        overlay_records.append(
            (
                first_word,
                read_u16_be(rom, cursor + 2),
                read_u16_be(rom, cursor + 4),
            )
        )
        cursor += 6

    return ScreenDescriptor(
        index=screen_index,
        address=descriptor_ptr,
        room_block_ptr=room_block_ptr,
        chunk_bank_ptr=chunk_bank_ptr,
        script_index=script_index,
        overlay_records=tuple(overlay_records),
    )


def parse_room_block(data: bytes) -> RoomBlock:
    tile_count = read_u16_be(data, 0)
    map_width = read_u16_be(data, 2)
    map_height = read_u16_be(data, 4)
    palette_words = tuple(read_u16_be(data, 6 + index * 2) for index in range(16))
    tilemap_offset = 6 + 32
    tilemap_word_count = map_width * map_height
    tilemap_words = tuple(
        read_u16_be(data, tilemap_offset + index * 2) for index in range(tilemap_word_count)
    )
    tile_bytes_offset = tilemap_offset + tilemap_word_count * 2
    tile_bytes_length = tile_count * 0x20
    tile_bytes = data[tile_bytes_offset:tile_bytes_offset + tile_bytes_length]
    if len(tile_bytes) != tile_bytes_length:
        raise ValueError("room block ended before the tile graphics payload")

    return RoomBlock(
        tile_count=tile_count,
        map_width=map_width,
        map_height=map_height,
        palette_words=palette_words,
        tilemap_words=tilemap_words,
        tile_bytes=tile_bytes,
    )


def load_tile_block(rom: bytes | memoryview, source_ptr: int) -> RoomBlock:
    block_data = decompress_imp_block(memoryview(rom), source_ptr)
    tile_count = read_u16_be(block_data, 0)
    map_width = read_u16_be(block_data, 2)
    map_height = read_u16_be(block_data, 4)
    tilemap_bytes = map_width * map_height * 2
    expected_tile_bytes = tile_count * 0x20
    tile_bytes_offset = 0x26 + tilemap_bytes

    if tile_bytes_offset + expected_tile_bytes <= len(block_data):
        return parse_room_block(block_data)

    external_tail_ptr = source_ptr + 0x32 + read_u32_be(rom, source_ptr + 8)
    external_tail = rom[external_tail_ptr:]
    tile_bytes = decompress_c500_tail(external_tail)
    if len(tile_bytes) != expected_tile_bytes:
        raise ValueError(
            f"C500 tail for 0x{source_ptr:06X} produced {len(tile_bytes)} bytes; expected {expected_tile_bytes}"
        )

    block_prefix = block_data[:tile_bytes_offset]
    return parse_room_block(block_prefix + tile_bytes)


def megadrive_color_to_rgb(color_word: int) -> tuple[int, int, int]:
    red = (color_word >> 1) & 0x7
    green = (color_word >> 5) & 0x7
    blue = (color_word >> 9) & 0x7

    def expand(channel: int) -> int:
        return (channel * 255 + 3) // 7

    return expand(red), expand(green), expand(blue)


def decode_tile(tile_bytes: bytes, tile_index: int) -> tuple[int, ...]:
    base = tile_index * 0x20
    tile = tile_bytes[base:base + 0x20]
    if len(tile) != 0x20:
        raise ValueError(f"tile {tile_index} extends past the decoded tile data")

    pixels: list[int] = []
    for row in range(8):
        row_bytes = tile[row * 4:(row + 1) * 4]
        for value in row_bytes:
            pixels.append(value >> 4)
            pixels.append(value & 0x0F)
    return tuple(pixels)


def build_chunk_templates(
    rom: bytes | memoryview,
    descriptor: ScreenDescriptor,
    room: RoomBlock,
) -> tuple[tuple[ChunkTemplate, ...], int]:
    if descriptor.chunk_bank_ptr == 0 or descriptor.chunk_bank_ptr + 8 > len(rom):
        return (), 0

    source_ptr = read_u32_be(rom, descriptor.chunk_bank_ptr)
    defs_ptr = read_u32_be(rom, descriptor.chunk_bank_ptr + 4)
    if source_ptr == 0xFFFFFFFF:
        bank_records: list[tuple[int | None, int]] = []
        overlay_base_index_from_first_bank = True
    elif (
        source_ptr + 4 <= len(rom)
        and bytes(rom[source_ptr:source_ptr + 4]) == b"IMP!"
        and 0 < defs_ptr < len(rom)
    ):
        record_ptr = descriptor.chunk_bank_ptr
        bank_records = []
        while True:
            source_ptr = read_u32_be(rom, record_ptr)
            if source_ptr == 0xFFFFFFFF:
                break
            defs_ptr = read_u32_be(rom, record_ptr + 4)
            bank_records.append((source_ptr, defs_ptr))
            record_ptr += 8
        overlay_base_index_from_first_bank = True
    else:
        bank_records = [(None, descriptor.chunk_bank_ptr)]
        overlay_base_index_from_first_bank = False

    templates: list[ChunkTemplate] = []
    overlay_base_index = 0

    for bank_index, (source_ptr, defs_ptr) in enumerate(bank_records):
        source_block = room if source_ptr is None else load_tile_block(rom, source_ptr)
        defs_cursor = defs_ptr
        while True:
            attr = read_u16_be(rom, defs_cursor)
            if attr == 0xFFFF:
                break

            source_x_or_alias = read_s16_be(rom, defs_cursor + 2)
            source_y = read_u16_be(rom, defs_cursor + 4)
            width = read_u16_be(rom, defs_cursor + 6)
            height = read_u16_be(rom, defs_cursor + 8)

            if source_x_or_alias < 0:
                referenced_index = -source_x_or_alias
                if referenced_index >= len(templates):
                    raise ValueError(
                        f"chunk template alias {referenced_index} is out of range for descriptor 0x{descriptor.address:06X}"
                    )
                referenced = templates[referenced_index]
                tilemap_words = referenced.tilemap_words
                width = referenced.width
                height = referenced.height
                palette_words = referenced.palette_words
                tile_count = referenced.tile_count
                tile_bytes = referenced.tile_bytes
            else:
                tile_entries: list[int] = []
                for row in range(height):
                    for col in range(width):
                        map_x = source_x_or_alias + col
                        map_y = source_y + row
                        if map_x >= source_block.map_width or map_y >= source_block.map_height:
                            source_label = "current room" if source_ptr is None else f"0x{source_ptr:06X}"
                            raise ValueError(
                                f"chunk template sampled outside source block {source_label}"
                            )
                        tile_entries.append(source_block.tilemap_words[map_y * source_block.map_width + map_x])
                tilemap_words = tuple(tile_entries)
                palette_words = source_block.palette_words
                tile_count = source_block.tile_count
                tile_bytes = source_block.tile_bytes

            templates.append(
                ChunkTemplate(
                    width=width,
                    height=height,
                    attr=attr,
                    palette_words=palette_words,
                    tilemap_words=tilemap_words,
                    tile_count=tile_count,
                    tile_bytes=tile_bytes,
                )
            )
            defs_cursor += 10

        if overlay_base_index_from_first_bank and bank_index == 0:
            overlay_base_index = len(templates)

    return tuple(templates), overlay_base_index


def write_rgb_png(path: Path, width: int, height: int, pixels: bytes) -> None:
    if len(pixels) != width * height * 3:
        raise ValueError("unexpected RGB buffer size for PNG output")

    def chunk(chunk_type: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + chunk_type
            + payload
            + struct.pack(">I", zlib.crc32(chunk_type + payload) & 0xFFFFFFFF)
        )

    scanlines = bytearray()
    row_bytes = width * 3
    for row in range(height):
        scanlines.append(0)
        start = row * row_bytes
        scanlines.extend(pixels[start:start + row_bytes])

    png = bytearray(b"\x89PNG\r\n\x1a\n")
    png.extend(chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)))
    png.extend(chunk(b"IDAT", zlib.compress(bytes(scanlines), level=9)))
    png.extend(chunk(b"IEND", b""))
    path.write_bytes(png)


def render_room_background(room: RoomBlock) -> tuple[bytearray, int]:
    width = room.map_width * 8
    height = room.map_height * 8
    palette = [megadrive_color_to_rgb(word) for word in room.palette_words]
    image = bytearray(width * height * 3)
    missing_tiles = 0

    for cell_y in range(room.map_height):
        for cell_x in range(room.map_width):
            entry = room.tilemap_words[cell_y * room.map_width + cell_x]
            tile_index = entry & 0x07FF
            hflip = (entry & 0x0800) != 0
            vflip = (entry & 0x1000) != 0
            if tile_index >= room.tile_count:
                missing_tiles += 1
                color = (255, 0, 255)
                for py in range(8):
                    for px in range(8):
                        pixel_index = ((cell_y * 8 + py) * width + (cell_x * 8 + px)) * 3
                        image[pixel_index:pixel_index + 3] = bytes(color)
                continue

            tile_pixels = decode_tile(room.tile_bytes, tile_index)
            for py in range(8):
                src_y = 7 - py if vflip else py
                for px in range(8):
                    src_x = 7 - px if hflip else px
                    color_index = tile_pixels[src_y * 8 + src_x]
                    rgb = palette[color_index]
                    pixel_index = ((cell_y * 8 + py) * width + (cell_x * 8 + px)) * 3
                    image[pixel_index:pixel_index + 3] = bytes(rgb)

    return image, missing_tiles


def render_chunk_template(
    image: bytearray,
    image_width: int,
    image_height: int,
    template: ChunkTemplate,
    x_pos: int,
    y_pos: int,
) -> int:
    palette = [megadrive_color_to_rgb(word) for word in template.palette_words]
    drawn_pixels = 0

    for cell_y in range(template.height):
        for cell_x in range(template.width):
            entry = template.tilemap_words[cell_y * template.width + cell_x]
            tile_index = entry & 0x07FF
            if tile_index >= template.tile_count:
                continue

            hflip = (entry & 0x0800) != 0
            vflip = (entry & 0x1000) != 0
            tile_pixels = decode_tile(template.tile_bytes, tile_index)
            dst_x = x_pos + cell_x * 8
            dst_y = y_pos + cell_y * 8

            for py in range(8):
                src_y = 7 - py if vflip else py
                target_y = dst_y + py
                if target_y < 0 or target_y >= image_height:
                    continue
                for px in range(8):
                    src_x = 7 - px if hflip else px
                    target_x = dst_x + px
                    if target_x < 0 or target_x >= image_width:
                        continue

                    color_index = tile_pixels[src_y * 8 + src_x]
                    if color_index == 0:
                        continue

                    rgb = palette[color_index]
                    pixel_index = (target_y * image_width + target_x) * 3
                    image[pixel_index:pixel_index + 3] = bytes(rgb)
                    drawn_pixels += 1

    return drawn_pixels


def composite_overlay_chunks(
    image: bytearray,
    room: RoomBlock,
    descriptor: ScreenDescriptor,
    templates: tuple[ChunkTemplate, ...],
    overlay_base_index: int,
) -> tuple[int, int]:
    rendered_overlays = 0
    rendered_pixels = 0
    image_width = room.map_width * 8
    image_height = room.map_height * 8

    for x_raw, y_raw, template_offset in descriptor.overlay_records:
        template_index = overlay_base_index + sign16(template_offset)
        if template_index < 0 or template_index >= len(templates):
            continue

        rendered_pixels += render_chunk_template(
            image,
            image_width,
            image_height,
            templates[template_index],
            x_raw - 0x80,
            y_raw - 0x80,
        )
        rendered_overlays += 1

    return rendered_overlays, rendered_pixels


def extract_screen(rom: bytes | memoryview, screen_index: int, output_dir: Path) -> Path:
    descriptor = parse_screen_descriptor(rom, screen_index)
    room = load_tile_block(rom, descriptor.room_block_ptr)
    image, missing_tiles = render_room_background(room)
    templates, overlay_base_index = build_chunk_templates(rom, descriptor, room)
    rendered_overlays, rendered_overlay_pixels = composite_overlay_chunks(
        image,
        room,
        descriptor,
        templates,
        overlay_base_index,
    )

    output_dir.mkdir(parents=True, exist_ok=True)
    output_path = output_dir / f"screen_{screen_index:03d}_room_{descriptor.room_block_ptr:06X}.png"
    write_rgb_png(output_path, room.map_width * 8, room.map_height * 8, bytes(image))

    highest_tile = max((entry & 0x07FF) for entry in room.tilemap_words) if room.tilemap_words else 0
    print(
        "screen {screen:03d}: desc=0x{desc:06X} room=0x{room_ptr:06X} "
        "size={w}x{h} tiles={tiles} max_tile={max_tile} overlays={overlays} "
        "rendered_overlays={rendered} overlay_templates={template_count} overlay_pixels={pixels} "
        "script=0x{script:04X} missing={missing} -> {path}".format(
            screen=screen_index,
            desc=descriptor.address,
            room_ptr=descriptor.room_block_ptr,
            w=room.map_width,
            h=room.map_height,
            tiles=room.tile_count,
            max_tile=highest_tile,
            overlays=len(descriptor.overlay_records),
            rendered=rendered_overlays,
            template_count=len(templates),
            pixels=rendered_overlay_pixels,
            script=descriptor.script_index,
            missing=missing_tiles,
            path=output_path,
        )
    )

    return output_path


def parse_args() -> argparse.Namespace:
    script_dir = Path(__file__).resolve().parent
    parser = argparse.ArgumentParser(
        description=(
            "Proof-of-concept extractor for Fantastic Dizzy Mega Drive room backgrounds. "
            "This follows the documented per-screen IMP block format and renders the base room tilemap."
        )
    )
    parser.add_argument(
        "--rom",
        type=Path,
        default=script_dir / "Fantastic Dizzy.bin",
        help="Path to the Fantastic Dizzy ROM image.",
    )
    parser.add_argument(
        "--screens",
        nargs="+",
        type=int,
        default=list(DEFAULT_SAMPLE_SCREENS),
        help="Screen indices to extract from ScreenDescriptorTable.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=script_dir / "out",
        help="Directory to write the rendered PNG files into.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if not args.rom.is_file():
        print(f"ROM not found: {args.rom}", file=sys.stderr)
        return 1

    rom = args.rom.read_bytes()
    try:
        for screen_index in args.screens:
            extract_screen(rom, screen_index, args.output_dir)
    except Exception as exc:
        print(f"Extraction failed: {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
