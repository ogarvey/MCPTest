using System;
using System.Collections.Generic;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MoonChild;

public static class MoonChildMapRenderer
{
    private const int TileSize = 32;

    public static Image<Rgba32> CreateMoonChildMap(
        string mapFile,
        int widthInTiles,
        int heightInTiles,
        string tilesetPath)
    {
        if (widthInTiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(widthInTiles));
        if (heightInTiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(heightInTiles));

        return CreateMoonChildMapFromPixelMetadata(
            mapFile,
            checked(widthInTiles * TileSize),
            checked(heightInTiles * TileSize),
            tilesetPath);
    }

    public static Image<Rgba32> CreateMoonChildMapFromPixelMetadata(
        string mapFile,
        int mapWidthPixels,
        int mapHeightPixels,
        string tilesetPath)
    {
        if (mapWidthPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(mapWidthPixels));
        if (mapHeightPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(mapHeightPixels));
        if (!File.Exists(mapFile))
            throw new FileNotFoundException("Map file not found.", mapFile);
        if (!File.Exists(tilesetPath))
            throw new FileNotFoundException("Tileset image not found.", tilesetPath);

        ushort[] tileIndices = ReadTileIndices(mapFile);
        List<RowLayout> rows = BuildRowLayout(mapWidthPixels, mapHeightPixels, tileIndices.Length);

        using var tilesetImage = Image.Load<Rgba32>(tilesetPath);
        ValidateTileset(tilesetImage, tilesetPath);

        int tilesPerTilesetRow = tilesetImage.Width / TileSize;
        int tilesetTileCount = tilesPerTilesetRow * (tilesetImage.Height / TileSize);
        int outputTileWidth = GetMaximumRowWidth(rows);
        int outputTileHeight = rows.Count;

        var canvas = new Image<Rgba32>(outputTileWidth * TileSize, outputTileHeight * TileSize, Color.Transparent);

        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            RowLayout row = rows[rowIndex];

            for (int column = 0; column < row.TileCount; column++)
            {
                ushort tileIndex = tileIndices[row.StartIndex + column];
                if (tileIndex >= tilesetTileCount)
                    continue;

                BlitTile(
                    tilesetImage,
                    tileIndex,
                    tilesPerTilesetRow,
                    canvas,
                    column * TileSize,
                    rowIndex * TileSize);
            }
        }

        if (canvas.Width == mapWidthPixels && canvas.Height == mapHeightPixels)
            return canvas;

        Image<Rgba32> cropped = canvas.Clone(ctx => ctx.Crop(new Rectangle(0, 0, mapWidthPixels, mapHeightPixels)));
        canvas.Dispose();
        return cropped;
    }

    private static ushort[] ReadTileIndices(string mapFile)
    {
        using var stream = File.OpenRead(mapFile);
        if ((stream.Length & 1) != 0)
            throw new InvalidDataException("MoonChild map files must contain an even number of bytes.");

        int wordCount = checked((int)(stream.Length / sizeof(ushort)));
        ushort[] tileIndices = new ushort[wordCount];

        using var reader = new BinaryReader(stream);
        for (int i = 0; i < tileIndices.Length; i++)
            tileIndices[i] = reader.ReadUInt16();

        return tileIndices;
    }

    private static List<RowLayout> BuildRowLayout(int mapWidthPixels, int mapHeightPixels, int wordCount)
    {
        int maxRows = (mapHeightPixels + TileSize - 1) / TileSize;
        var rowStarts = new List<int>(maxRows + 2) { 0 };

        for (int row = 1; row <= maxRows; row++)
        {
            int startIndex = (checked(mapWidthPixels * row)) >> 5;
            if (startIndex >= wordCount)
            {
                rowStarts.Add(wordCount);
                break;
            }

            rowStarts.Add(startIndex);
        }

        if (rowStarts[rowStarts.Count - 1] != wordCount)
            rowStarts.Add(wordCount);

        var rows = new List<RowLayout>(rowStarts.Count - 1);
        int consumed = 0;

        for (int i = 0; i < rowStarts.Count - 1; i++)
        {
            int startIndex = rowStarts[i];
            int endIndex = rowStarts[i + 1];
            int tileCount = endIndex - startIndex;
            if (tileCount < 0)
                throw new InvalidDataException("Computed an invalid MoonChild row layout.");

            if (tileCount == 0)
                continue;

            rows.Add(new RowLayout(startIndex, tileCount));
            consumed += tileCount;
        }

        if (consumed != wordCount)
            throw new InvalidDataException($"MoonChild row layout mismatch. Expected {wordCount} tile indices, consumed {consumed}.");

        return rows;
    }

    private static int GetMaximumRowWidth(IReadOnlyList<RowLayout> rows)
    {
        int maxWidth = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].TileCount > maxWidth)
                maxWidth = rows[i].TileCount;
        }

        return maxWidth;
    }

    private static void ValidateTileset(Image<Rgba32> tilesetImage, string tilesetPath)
    {
        if ((tilesetImage.Width % TileSize) != 0 || (tilesetImage.Height % TileSize) != 0)
        {
            throw new InvalidDataException(
                $"Tileset '{tilesetPath}' must have dimensions that are multiples of {TileSize}.");
        }
    }

    private static void BlitTile(
        Image<Rgba32> tilesetImage,
        int tileIndex,
        int tilesPerTilesetRow,
        Image<Rgba32> destination,
        int destinationX,
        int destinationY)
    {
        int sourceTileX = (tileIndex % tilesPerTilesetRow) * TileSize;
        int sourceTileY = (tileIndex / tilesPerTilesetRow) * TileSize;

        for (int y = 0; y < TileSize; y++)
        {
            int srcY = sourceTileY + y;
            int dstY = destinationY + y;

            for (int x = 0; x < TileSize; x++)
            {
                destination[destinationX + x, dstY] = tilesetImage[sourceTileX + x, srcY];
            }
        }
    }

    private readonly record struct RowLayout(int StartIndex, int TileCount);
}
