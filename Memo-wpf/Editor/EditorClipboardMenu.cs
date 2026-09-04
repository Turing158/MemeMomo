using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Editing;
using Memo.UI.Popup;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using TextBox = System.Windows.Controls.TextBox;

namespace Memo.Editor;

/// <summary>
/// 编辑区剪切/复制/粘贴右键菜单项的共享构建器。主编辑区（AvalonEdit 文本区）通过显式
/// CommandTarget 命中各自的命令绑定；表格单元格（TextBox）的剪切/复制在未选中文字时作
/// 用于整个单元格内容（复制写入完整单元格文本，剪切写入并清空单元格），有选区时保持标
/// 准行为只作用于选区。右键菜单打开期间不改变命令的路由目标。粘贴项只在剪贴板有内容时
/// 显示，且每次菜单打开时重新探测——菜单在编辑器或单元格创建时就已构建，剪贴板内容随后
/// 随时可能变化；剪贴板被占用等探测失败按无内容处理。
/// </summary>
internal static class EditorClipboardMenu
{
    internal const string CutHeader = "剪切";
    internal const string CopyHeader = "复制";
    internal const string PasteHeader = "粘贴";

    /// <summary>
    /// 为主编辑区创建右键菜单。剪贴板是图片/文件列表时粘贴走 <paramref name="pasteImages"/>
    /// （与 Ctrl+V 的图片拦截一致），其余情况执行普通文本粘贴。
    /// </summary>
    internal static ContextMenu CreateForTextArea(TextArea textArea, Action pasteImages)
    {
        ContextMenu menu = new()
        {
            Style = Application.Current?.TryFindResource("MarkdownTableEdgeMenuPresenterTheme") as Style
        };
        ContextMenuAnimations.SetIsEnabled(menu, true);
        AppendClipboardItems(menu, textArea, pasteImages, includeImageContent: true, trailingSeparator: false);
        return menu;
    }

    /// <summary>
    /// 把剪切/复制/粘贴插到已有菜单（如表格单元格菜单）的最顶部，后接一条分隔线。
    /// 单元格未选中文字时剪切/复制作用于整个单元格内容，而不是像标准命令那样禁用。
    /// </summary>
    internal static void PrependToCellMenu(ContextMenu menu, TextBox cell)
    {
        AppendClipboardItems(menu, cell, pasteImages: null, includeImageContent: false,
            trailingSeparator: true, cellContentFallback: true);
    }

    private static void AppendClipboardItems(
        ContextMenu menu,
        FrameworkElement target,
        Action? pasteImages,
        bool includeImageContent,
        bool trailingSeparator,
        bool cellContentFallback = false)
    {
        MenuItem cut = CreateItem(CutHeader, target);
        MenuItem copy = CreateItem(CopyHeader, target);
        if (cellContentFallback)
        {
            TextBox cell = (TextBox)target;
            cut.Click += (_, _) => ExecuteCellCut(cell);
            copy.Click += (_, _) => ExecuteCellCopy(cell);
        }
        else
        {
            cut.Command = ApplicationCommands.Cut;
            copy.Command = ApplicationCommands.Copy;
        }
        MenuItem paste = CreateItem(PasteHeader, target);
        paste.Click += (_, _) =>
        {
            if (pasteImages is not null && HasClipboardImageData())
            {
                pasteImages();
            }
            else
            {
                ApplicationCommands.Paste.Execute(null, target);
            }
        };
        menu.Items.Insert(0, cut);
        menu.Items.Insert(1, copy);
        menu.Items.Insert(2, paste);
        if (trailingSeparator)
        {
            menu.Items.Insert(3, new Separator
            {
                Style = Application.Current?.TryFindResource("MarkdownToolbarMenuSeparatorTheme") as Style
            });
        }
        menu.Opened += (_, _) => UpdatePasteVisibility(paste, includeImageContent);
        UpdatePasteVisibility(paste, includeImageContent);
    }

    /// <summary>
    /// 单元格剪切/复制：有选区时保持标准行为只作用于选区；未选中文字时作用于整个
    /// 单元格内容——复制写入完整单元格文本，剪切写入并清空单元格（经由 TextChanged
    /// 同步回 Markdown 源码）。空单元格没有内容可写，点击为空操作。
    /// </summary>
    private static void ExecuteCellCopy(TextBox cell)
    {
        if (cell.SelectionLength > 0)
        {
            cell.Copy();
        }
        else if (cell.Text.Length > 0)
        {
            TrySetClipboardText(cell.Text);
        }
    }

    private static void ExecuteCellCut(TextBox cell)
    {
        if (cell.SelectionLength > 0)
        {
            cell.Cut();
            return;
        }
        if (cell.Text.Length > 0)
        {
            TrySetClipboardText(cell.Text);
            cell.Clear();
        }
    }

    private static void TrySetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // 剪贴板被其他进程占用时写入失败，按无操作处理，与读取路径的降级一致。
        }
    }

    private static MenuItem CreateItem(string header, FrameworkElement target)
    {
        MenuItem item = new()
        {
            Header = header,
            CommandTarget = target,
            Style = Application.Current?.TryFindResource("MarkdownTableEdgeMenuItemTheme") as Style
        };
        AutomationProperties.SetName(item, header);
        return item;
    }

    private static void UpdatePasteVisibility(MenuItem paste, bool includeImageContent) =>
        paste.Visibility = HasClipboardText() || (includeImageContent && HasClipboardImageData())
            ? Visibility.Visible
            : Visibility.Collapsed;

    internal static bool HasClipboardText()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    internal static bool HasClipboardImageData()
    {
        try
        {
            return Clipboard.ContainsImage() || Clipboard.ContainsFileDropList();
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
