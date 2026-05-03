# Famous Five 2 BRG Tool

This app decodes `.brg` resources from *Famous Five 2* and exports frames as PNG images.

## Current scope

- Supports BRG subtype `1`
- Supports BRG subtype `2`
- Supports BRG subtype `3`
- Uses `ImageSharp` for PNG export

The implementation is based on reverse engineering of:

- `FUN_0042add0`
- `FUN_004c7590`
- `FUN_00503820`
- `FUN_00504010`

## Usage

```powershell
dotnet run --project ".\Famous Five 2\FamousFive2.BrgTool.csproj" -- ".\path\to\folder"
dotnet run --project ".\Famous Five 2\FamousFive2.BrgTool.csproj" -- ".\path\to\folder" ".\out"
dotnet run --project ".\Famous Five 2\FamousFive2.BrgTool.csproj" -- ".\path\to\file.brg" ".\out"
dotnet run --project ".\Famous Five 2\FamousFive2.BrgTool.csproj" -- ".\path\to\file.brg" ".\out" 0
```

Folder input exports every `.brg` in that folder into per-file subdirectories.

For single-file input, the first form exports every frame. The second exports a single frame.

## Notes

- Subtype `1/2` is treated as indexed-color RLE over 4-byte tokens.
- Subtype `3` is treated as 2x2 block RLE over 4-byte tokens.
- Transparency handling is inferred from the decoder and may still need adjustment against real assets.
