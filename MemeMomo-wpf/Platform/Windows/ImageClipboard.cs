using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;

namespace MemeMomo.Platform.Windows;

internal static class ImageClipboard
{
    internal static DataObject CreateData(BitmapSource bitmap)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        MemoryStream png = new();
        encoder.Save(png);
        png.Position = 0;
        DataObject data = new();
        data.SetImage(bitmap);
        data.SetData("PNG", png, autoConvert: false);
        return data;
    }

    internal static async Task CopyAsync(BitmapSource bitmap)
    {
        DataObject data = CreateData(bitmap);
        using var png = (MemoryStream)data.GetData("PNG", autoConvert: false);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (ExternalException) when (attempt < 2)
            {
                await Task.Delay(50);
            }
        }
    }
}
