using System.Windows;
using MemeMomo.Services;
using MemeMomo.UI.Notifications;

namespace MemeMomo.Editor;

internal sealed partial class MarkdownImageControl
{
    private bool _imageTransferBusy;

    private void InitializeImageTransferMenu() => EditorClipboardMenu.InsertImageTransferItems(
        ContextMenu, _adapter.Editor.TextArea, _adapter.IsReadOnly ? 0 : 2,
        () => CopyImageAsync(), () => SaveImageAsync(), () => TryGetTransferImage(out _));

    private bool TryGetTransferImage(out MarkdownImageLoadResult? image)
    {
        image = null;
        return !_imageTransferBusy && !_adapter.IsSourceMode && _session == _adapter.SessionVersion &&
            FindCurrentSpan() is not null && _loader.TryGetCompleted(ImageUri, out image) &&
            image is { IsSuccess: true, SourceBytes.IsEmpty: false };
    }

    internal async Task<bool> CopyImageAsync()
    {
        if (!TryGetTransferImage(out var image)) return false;
        _imageTransferBusy = true;
        CloseToolbarNow();
        try
        {
            await _adapter.ImageClipboardWriter(image!.Bitmap!);
            WindowNotifications.Show(_adapter.Editor, LocalizationService.Get("已复制图片"), NotificationKind.Success);
            return true;
        }
        catch (Exception error)
        {
            WindowNotifications.Show(_adapter.Editor, LocalizationService.Format("复制图片失败：{0}", error.Message), NotificationKind.Error);
            return false;
        }
        finally { _imageTransferBusy = false; }
    }

    internal async Task<bool> SaveImageAsync()
    {
        if (!TryGetTransferImage(out var image) || Window.GetWindow(_adapter.Editor) is not { } owner) return false;
        _imageTransferBusy = true;
        CloseToolbarNow();
        try
        {
            var dialog = MarkdownImageExport.CreateSaveDialog(image!.SourceBytes.Span, _span.AltText);
            if (_adapter.ImageSaveDialog(owner, dialog) != true) return false;
            if (_session != _adapter.SessionVersion || _adapter.IsSourceMode || FindCurrentSpan() is null) return false;
            await MarkdownImageExport.SaveAsync(dialog.FileName, image.SourceBytes);
            WindowNotifications.Show(_adapter.Editor, LocalizationService.Get("已保存图片"), NotificationKind.Success);
            return true;
        }
        catch (Exception error)
        {
            WindowNotifications.Show(_adapter.Editor, LocalizationService.Format("保存图片失败：{0}", error.Message), NotificationKind.Error);
            return false;
        }
        finally { _imageTransferBusy = false; }
    }
}
