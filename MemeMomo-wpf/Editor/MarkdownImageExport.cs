using System.IO;
using MemeMomo.Services;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace MemeMomo.Editor;

internal static class MarkdownImageExport
{
    internal static SaveFileDialog CreateSaveDialog(ReadOnlySpan<byte> bytes, string? altText)
    {
        string extension = GetExtension(bytes);
        string name = string.IsNullOrWhiteSpace(altText) ? LocalizationService.Get("图片") : altText;
        name = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        name = name[..Math.Min(name.Length, 100)].Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(name)) name = LocalizationService.Get("图片");
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name += extension;
        return new SaveFileDialog
        {
            Title = LocalizationService.Get("保存图片"),
            FileName = name,
            DefaultExt = extension,
            Filter = LocalizationService.Format("图片文件 (*{0})|*{0}", extension),
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true
        };
    }

    internal static async Task SaveAsync(string destination, ReadOnlyMemory<byte> bytes)
    {
        string path = Path.GetFullPath(destination);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".image-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                await output.WriteAsync(bytes);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string GetExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return ".jpg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return ".gif";
        if (bytes.StartsWith("BM"u8)) return ".bmp";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8)) return ".webp";
        if (bytes.StartsWith(new byte[] { 73, 73, 42, 0 }) || bytes.StartsWith(new byte[] { 77, 77, 0, 42 })) return ".tif";
        if (bytes.StartsWith(new byte[] { 0, 0, 1, 0 })) return ".ico";
        return ".img";
    }
}
