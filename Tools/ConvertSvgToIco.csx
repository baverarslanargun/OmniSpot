// SVG to ICO Converter Script
// Run with: dotnet script ConvertSvgToIco.csx
// Install dotnet-script: dotnet tool install -g dotnet-script

#r "nuget: Svg, 3.4.6"
#r "nuget: System.Drawing.Common, 8.0.0"

using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using Svg;

var svgPath = @"c:\OmniSpot\omnispot.svg";
var icoPath = @"c:\OmniSpot\SmartFileLauncher.UI\Resources\app.ico";

Console.WriteLine("Loading SVG...");
var svgDocument = SvgDocument.Open(svgPath);

// Create multiple sizes for ICO
int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
var images = new List<Bitmap>();

foreach (var size in sizes)
{
    Console.WriteLine($"Creating {size}x{size} image...");
    var bitmap = new Bitmap(size, size);
    using (var g = Graphics.FromImage(bitmap))
    {
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        
        svgDocument.Draw(g, new SizeF(size, size));
    }
    images.Add(bitmap);
}

Console.WriteLine("Creating ICO file...");
SaveAsIcon(images, icoPath);
Console.WriteLine($"ICO saved to: {icoPath}");

void SaveAsIcon(List<Bitmap> images, string filePath)
{
    using var fs = new FileStream(filePath, FileMode.Create);
    using var bw = new BinaryWriter(fs);
    
    // ICO Header
    bw.Write((short)0);      // Reserved
    bw.Write((short)1);      // Type: 1 = ICO
    bw.Write((short)images.Count); // Image count
    
    var imageDataList = new List<byte[]>();
    int offset = 6 + (16 * images.Count); // Header + directory entries
    
    // Directory entries
    foreach (var img in images)
    {
        using var ms = new MemoryStream();
        img.Save(ms, ImageFormat.Png);
        var data = ms.ToArray();
        imageDataList.Add(data);
        
        bw.Write((byte)(img.Width >= 256 ? 0 : img.Width));  // Width
        bw.Write((byte)(img.Height >= 256 ? 0 : img.Height)); // Height
        bw.Write((byte)0);    // Color palette
        bw.Write((byte)0);    // Reserved
        bw.Write((short)1);   // Color planes
        bw.Write((short)32);  // Bits per pixel
        bw.Write(data.Length); // Image size
        bw.Write(offset);     // Offset
        
        offset += data.Length;
    }
    
    // Image data
    foreach (var data in imageDataList)
    {
        bw.Write(data);
    }
}

Console.WriteLine("Done!");
