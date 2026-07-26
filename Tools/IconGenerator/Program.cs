using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Manual icon creation based on the OmniSpot design
// Creates a simplified version of the SVG icon

var icoPath = @"c:\OmniSpot\SmartFileLauncher.UI\Resources\app.ico";

Console.WriteLine("Creating OmniSpot icon...");

// Create multiple sizes
int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
var images = new List<Bitmap>();

foreach (var size in sizes)
{
    Console.WriteLine($"  Creating {size}x{size}...");
    var bitmap = CreateIcon(size);
    images.Add(bitmap);
}

Console.WriteLine("Saving ICO file...");
SaveAsIcon(images, icoPath);
Console.WriteLine($"Done! Icon saved to: {icoPath}");

foreach (var img in images) img.Dispose();

static Bitmap CreateIcon(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    
    g.SmoothingMode = SmoothingMode.HighQuality;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    
    float scale = size / 256f;
    int cornerRadius = (int)(48 * scale);
    
    // Background gradient (dark blue)
    using (var bgBrush = new LinearGradientBrush(
        new Point(0, 0),
        new Point(size, size),
        Color.FromArgb(255, 16, 22, 51),  // #101633
        Color.FromArgb(255, 5, 8, 18)))   // #050812
    {
        using var path = CreateRoundedRect(0, 0, size, size, cornerRadius);
        g.FillPath(bgBrush, path);
    }
    
    // Main scan circle
    float circleSize = 160 * scale;
    float circleX = (size - circleSize) / 2;
    float circleY = (size - circleSize) / 2;
    
    using (var circleBrush = new LinearGradientBrush(
        new PointF(circleX, circleY),
        new PointF(circleX + circleSize, circleY + circleSize),
        Color.FromArgb(255, 55, 243, 255),  // #37F3FF cyan
        Color.FromArgb(255, 0, 80, 184)))   // #0050B8 blue
    {
        g.FillEllipse(circleBrush, circleX, circleY, circleSize, circleSize);
    }
    
    // File document icon (white with blue tint)
    float docWidth = 52 * scale;
    float docHeight = 68 * scale;
    float docX = (size - docWidth) / 2;
    float docY = (size - docHeight) / 2 + 4 * scale;
    float foldSize = 14 * scale;
    
    // Document body
    using (var docPath = CreateDocumentPath(docX, docY, docWidth, docHeight, foldSize))
    using (var docBrush = new LinearGradientBrush(
        new PointF(docX, docY),
        new PointF(docX + docWidth, docY + docHeight),
        Color.FromArgb(255, 240, 248, 255),  // Light blue-white
        Color.FromArgb(255, 200, 220, 255))) // Slight blue
    {
        g.FillPath(docBrush, docPath);
    }
    
    // Document lines
    float lineY1 = docY + 24 * scale;
    float lineY2 = docY + 36 * scale;
    float lineY3 = docY + 48 * scale;
    float lineX = docX + 10 * scale;
    float lineWidth1 = 32 * scale;
    float lineWidth2 = 24 * scale;
    
    using (var linePen = new Pen(Color.FromArgb(150, 100, 140, 200), Math.Max(1, 2 * scale)))
    {
        g.DrawLine(linePen, lineX, lineY1, lineX + lineWidth1, lineY1);
        g.DrawLine(linePen, lineX, lineY2, lineX + lineWidth1, lineY2);
        g.DrawLine(linePen, lineX, lineY3, lineX + lineWidth2, lineY3);
    }
    
    // Radar rings (subtle)
    float ringCenter = size / 2f;
    using (var ringPen = new Pen(Color.FromArgb(60, 55, 243, 255), Math.Max(1, 1.5f * scale)))
    {
        for (int i = 1; i <= 3; i++)
        {
            float ringSize = circleSize * (0.5f + i * 0.18f);
            g.DrawEllipse(ringPen, 
                ringCenter - ringSize / 2, 
                ringCenter - ringSize / 2, 
                ringSize, ringSize);
        }
    }
    
    // Glowing dot (bottom right)
    float dotSize = 12 * scale;
    float dotX = size * 0.72f;
    float dotY = size * 0.72f;
    
    // Glow
    using (var glowPath = new GraphicsPath())
    {
        glowPath.AddEllipse(dotX - dotSize, dotY - dotSize, dotSize * 2, dotSize * 2);
        using var glowBrush = new PathGradientBrush(glowPath)
        {
            CenterColor = Color.FromArgb(200, 55, 243, 255),
            SurroundColors = new[] { Color.FromArgb(0, 55, 243, 255) }
        };
        g.FillPath(glowBrush, glowPath);
    }
    
    // Dot
    using (var dotBrush = new SolidBrush(Color.FromArgb(255, 55, 243, 255)))
    {
        g.FillEllipse(dotBrush, dotX - dotSize / 3, dotY - dotSize / 3, dotSize * 2 / 3, dotSize * 2 / 3);
    }
    
    return bitmap;
}

static GraphicsPath CreateRoundedRect(float x, float y, float width, float height, float radius)
{
    var path = new GraphicsPath();
    if (radius <= 0)
    {
        path.AddRectangle(new RectangleF(x, y, width, height));
        return path;
    }
    
    float diameter = radius * 2;
    path.AddArc(x, y, diameter, diameter, 180, 90);
    path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
    path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
    path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}

static GraphicsPath CreateDocumentPath(float x, float y, float width, float height, float fold)
{
    var path = new GraphicsPath();
    
    // Document with folded corner
    path.AddLine(x, y, x + width - fold, y);
    path.AddLine(x + width - fold, y, x + width, y + fold);
    path.AddLine(x + width, y + fold, x + width, y + height);
    path.AddLine(x + width, y + height, x, y + height);
    path.CloseFigure();
    
    return path;
}

static void SaveAsIcon(List<Bitmap> images, string filePath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    
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
    
    Console.WriteLine($"  Wrote {images.Count} sizes to ICO");
}
