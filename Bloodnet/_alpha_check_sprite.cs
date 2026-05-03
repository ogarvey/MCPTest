using System;
using System.Drawing;

var path = @".\Bloodnet\_extract_test_sprite_probe\SPRITE\0109_MALEG.preview.png";
using var bitmap = new Bitmap(path);
var transparent = 0;
for (var y = 0; y < bitmap.Height; y++)
for (var x = 0; x < bitmap.Width; x++)
{
    if (bitmap.GetPixel(x, y).A == 0)
    {
        transparent++;
    }
}
Console.WriteLine($"{bitmap.Width}x{bitmap.Height} transparentPixels={transparent}");
