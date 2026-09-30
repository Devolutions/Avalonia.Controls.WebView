using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Logging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using static Avalonia.Controls.Gtk.GtkInterop;
using static Avalonia.Controls.Gtk.AvaloniaGtk;

namespace Avalonia.Controls.Gtk;

internal sealed class GtkOffscreenAvaloniaWebViewAdapter : GtkOffscreenWebViewAdapter
{
    private static readonly unsafe IntPtr s_showOptionMenuCallback =
        new((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, GdkEvent*, GdkRectangle*, IntPtr, int>)&ShowOptionMenuCallback);
    private static readonly unsafe IntPtr s_contextMenuCallback =
        new((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, GdkEvent*, IntPtr, IntPtr, int>)&ContextMenuCallback);
    private static readonly unsafe IntPtr s_optionsMenuClosedCallback =
        new((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&MenuClosedCallback);

    private GtkSignal? _showOptionMenuSignal;
    private GtkSignal? _contextMenuSignal;
    private HashSet<IDisposable> _openedMenus = new();

    private GtkOffscreenAvaloniaWebViewAdapter(GtkWebViewEnvironmentRequestedEventArgs environmentArgs) : base(environmentArgs)
    {
        _showOptionMenuSignal = new GtkSignal(WebViewHandle, "show-option-menu", s_showOptionMenuCallback, this);
        _contextMenuSignal = new GtkSignal(WebViewHandle, "context-menu", s_contextMenuCallback, this);
    }

    public Control? Parent { get; private set; }

    public static Task<WebViewAdapter.OffscreenWebViewAdapterBuilder> CreateBuilder(
        GtkWebViewEnvironmentRequestedEventArgs environmentArgs)
    {
        // A fresh adapter per attachment: the host disposes the previous one when the control is
        // detached, so handing out a cached instance leaves a dead web view after a re-attach.
        WebViewAdapter.OffscreenWebViewAdapterBuilder builder = async parent =>
        {
            using var backendScope = PrepareGdkBackendForGtkInit(environmentArgs.ForceX11GdkBackend);
            var adapter = await RunOnGlibThreadAsync(() => new GtkOffscreenAvaloniaWebViewAdapter(environmentArgs));
            adapter.Parent = parent;
            return adapter;
        };

        return Task.FromResult(builder);
    }

    protected override void DisposeSafe(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _showOptionMenuSignal, null)?.Dispose();
            Interlocked.Exchange(ref _contextMenuSignal, null)?.Dispose();

            var menus = Interlocked.Exchange(ref _openedMenus, new());
            foreach (var menu in menus)
            {
                menu.Dispose();
            }
            menus.Clear();
        }
        base.DisposeSafe(disposing);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ContextMenuCallback(IntPtr webview, IntPtr menu, GdkEvent* sourceEvent, IntPtr hitTest, IntPtr data)
    {
        if (data == IntPtr.Zero || GCHandle.FromIntPtr(data).Target is not GtkOffscreenAvaloniaWebViewAdapter adapter)
        {
            return False;
        }

        // Left to WebKit, the menu is popped against the offscreen toplevel, which has no place on screen for GTK
        // to measure from, so it opens nowhere near the pointer. Rebuilding it the way option menus are handled
        // puts it where the click was.
        var position = sourceEvent is not null
                       && sourceEvent->Type is GdkEventType.GDK_BUTTON_PRESS or GdkEventType.GDK_BUTTON_RELEASE
            ? new Point(sourceEvent->button.x, sourceEvent->button.y)
            : default;

        // What was clicked on is only described by the hit test, and only while the signal is being handled.
        var targets = new GtkContextMenuTargets(
            hitTest == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(webkit_hit_test_result_get_link_uri(hitTest)),
            hitTest == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(webkit_hit_test_result_get_image_uri(hitTest)),
            hitTest == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(webkit_hit_test_result_get_media_uri(hitTest)));

        new GtkContextMenuState(menu, position, targets, adapter).Open();
        return True;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ShowOptionMenuCallback(IntPtr webview, IntPtr menu, GdkEvent* sourceEvent, GdkRectangle* rect, IntPtr data)
    {
        if (data == IntPtr.Zero || GCHandle.FromIntPtr(data).Target is not GtkOffscreenAvaloniaWebViewAdapter adapter)
        {
            return False;
        }

        var isMouseRequest = sourceEvent is not null && sourceEvent->Type == GdkEventType.GDK_BUTTON_PRESS;
        var openMenuState = new GtkOptionsMenuState(menu, isMouseRequest, *rect, adapter);
        openMenuState.Open();

        return True;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuClosedCallback(IntPtr menu, IntPtr data)
    {
        if (data == IntPtr.Zero || GCHandle.FromIntPtr(data).Target is not GtkOptionsMenuState state)
        {
            return;
        }

        state.ClosedRequested();
    }

    private readonly record struct GtkContextMenuTargets(string? LinkUri, string? ImageUri, string? MediaUri);

    // WebKit's action ids shift between ports and versions - WebKitGTK carries an image entry the WPE headers do
    // not, which moves everything after it - so they are read from the enum the library itself registers.
    private static class ContextMenuActions
    {
        private static readonly Dictionary<string, int> s_values = Load();

        public static readonly int OpenLink = Get("OPEN_LINK");
        public static readonly int OpenLinkInNewWindow = Get("OPEN_LINK_IN_NEW_WINDOW");
        public static readonly int DownloadLinkToDisk = Get("DOWNLOAD_LINK_TO_DISK");
        public static readonly int CopyLinkToClipboard = Get("COPY_LINK_TO_CLIPBOARD");
        public static readonly int OpenImageInNewWindow = Get("OPEN_IMAGE_IN_NEW_WINDOW");
        public static readonly int DownloadImageToDisk = Get("DOWNLOAD_IMAGE_TO_DISK");
        public static readonly int CopyImageUrlToClipboard = Get("COPY_IMAGE_URL_TO_CLIPBOARD");
        public static readonly int GoBack = Get("GO_BACK");
        public static readonly int GoForward = Get("GO_FORWARD");
        public static readonly int Stop = Get("STOP");
        public static readonly int Reload = Get("RELOAD");
        public static readonly int Copy = Get("COPY");
        public static readonly int Cut = Get("CUT");
        public static readonly int Paste = Get("PASTE");
        public static readonly int Bold = Get("BOLD");
        public static readonly int Italic = Get("ITALIC");
        public static readonly int Underline = Get("UNDERLINE");
        public static readonly int Outline = Get("OUTLINE");
        public static readonly int InspectElement = Get("INSPECT_ELEMENT");
        public static readonly int OpenVideoInNewWindow = Get("OPEN_VIDEO_IN_NEW_WINDOW");
        public static readonly int OpenAudioInNewWindow = Get("OPEN_AUDIO_IN_NEW_WINDOW");
        public static readonly int CopyVideoLinkToClipboard = Get("COPY_VIDEO_LINK_TO_CLIPBOARD");
        public static readonly int CopyAudioLinkToClipboard = Get("COPY_AUDIO_LINK_TO_CLIPBOARD");
        public static readonly int DownloadVideoToDisk = Get("DOWNLOAD_VIDEO_TO_DISK");
        public static readonly int DownloadAudioToDisk = Get("DOWNLOAD_AUDIO_TO_DISK");
        public static readonly int Custom = Get("CUSTOM");

        // Anything the table did not have is -1, which no real action carries.
        private static int Get(string suffix) =>
            s_values.TryGetValue("WEBKIT_CONTEXT_MENU_ACTION_" + suffix, out var value) ? value : -1;

        private static unsafe Dictionary<string, int> Load()
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var klass = g_type_class_ref(webkit_context_menu_action_get_type());
                if (klass == IntPtr.Zero)
                {
                    return result;
                }

                try
                {
                    var enumClass = (GEnumClass*)klass;
                    var values = (GEnumValue*)enumClass->values;
                    if (values is null)
                    {
                        return result;
                    }

                    for (var i = 0u; i < enumClass->n_values; i++)
                    {
                        if (Marshal.PtrToStringUTF8(values[i].value_name) is { Length: > 0 } name)
                        {
                            result[name] = values[i].value;
                        }
                    }
                }
                finally
                {
                    g_type_class_unref(klass);
                }
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Error, "WebView")?.Log(null, "Could not read the context menu actions: {Error}", e);
            }

            return result;
        }
    }

    private class GtkContextMenuState : IDisposable
    {
        private readonly GtkOffscreenAvaloniaWebViewAdapter _adapter;
        private readonly Point _position;
        private readonly GtkContextMenuTargets _targets;
        private IntPtr _menu;
        private ContextMenu? _contextMenu;

        public GtkContextMenuState(IntPtr menu, Point position, GtkContextMenuTargets targets,
            GtkOffscreenAvaloniaWebViewAdapter adapter)
        {
            // The items, and the actions behind them, belong to the menu: it has to outlive the Avalonia one.
            g_object_ref(menu);
            _menu = menu;
            _position = position;
            _targets = targets;
            _adapter = adapter;
        }

        public void Open()
        {
            _adapter._openedMenus.Add(this);
            var items = ExtractContextMenu(_menu, _targets);

            WebViewDispatcher.InvokeAsync(() =>
            {
                var actualWebView = (Control?)_adapter.Parent?.GetVisualParent();
                if (items.Count == 0
                    || actualWebView is null
                    || TopLevel.GetTopLevel(actualWebView) is not { } topLevel)
                {
                    Dispose();
                    return;
                }

                // The click arrives in device pixels, the same scale the synthesized pointer events use.
                var scaling = topLevel.RenderScaling;
                _contextMenu = new ContextMenu
                {
                    // Aligning the menu's top left with an empty rect at the click is what a context menu does
                    // everywhere else.
                    Placement = PlacementMode.BottomEdgeAlignedLeft,
                    PlacementRect = new Rect(_position.X / scaling, _position.Y / scaling, 1, 1),
                    PlacementTarget = actualWebView,
                    DataContext = this
                };
                _contextMenu.Closed += static (el, _) =>
                {
                    if (el is ContextMenu { DataContext: GtkContextMenuState state })
                    {
                        state.Dispose();
                    }
                };

                Populate(this, _contextMenu.Items, items);
                _contextMenu.Open(actualWebView);
            });
        }

        public void Dispose()
        {
            var menu = Interlocked.Exchange(ref _menu, IntPtr.Zero);
            if (menu == IntPtr.Zero)
            {
                return;
            }

            GC.SuppressFinalize(this);
            RunOnGlibThreadAsync(() =>
            {
                g_object_unref(menu);
                _adapter._openedMenus.Remove(this);
            });
        }

        ~GtkContextMenuState()
        {
            var menu = Interlocked.Exchange(ref _menu, IntPtr.Zero);
            if (menu != IntPtr.Zero)
            {
                RunOnGlibThreadAsync(() => g_object_unref(menu));
            }
        }

        private static void Populate(GtkContextMenuState state, ItemCollection items,
            List<GtkContextMenuItemModel> models)
        {
            foreach (var model in models)
            {
                if (model.IsSeparator)
                {
                    items.Add(new Separator());
                    continue;
                }

                var menuItem = new MenuItem
                {
                    Header = model.Label,
                    IsEnabled = model.IsEnabled
                };

                if (model.Submenu is { Count: > 0 } submenu)
                {
                    Populate(state, menuItem.Items, submenu);
                }
                else
                {
                    menuItem.DataContext = (state, model);
                    menuItem.Click += static (el, _) =>
                    {
                        if (el is MenuItem
                            {
                                DataContext: ValueTuple<GtkContextMenuState, GtkContextMenuItemModel> clicked
                            })
                        {
                            clicked.Item1.Perform(clicked.Item2);
                        }
                    };
                }

                items.Add(menuItem);
            }
        }

        private static List<GtkContextMenuItemModel> ExtractContextMenu(IntPtr menuPtr, GtkContextMenuTargets targets)
        {
            var result = new List<GtkContextMenuItemModel>();
            var count = webkit_context_menu_get_n_items(menuPtr);

            for (uint i = 0; i < count; i++)
            {
                var itemPtr = webkit_context_menu_get_item_at_position(menuPtr, i);
                if (itemPtr == IntPtr.Zero)
                {
                    continue;
                }

                if (webkit_context_menu_item_is_separator(itemPtr))
                {
                    result.Add(new GtkContextMenuItemModel { IsSeparator = true });
                    continue;
                }

                var submenuPtr = webkit_context_menu_item_get_submenu(itemPtr);
                var action = webkit_context_menu_item_get_gaction(itemPtr);
                var stockAction = (int)webkit_context_menu_item_get_stock_action(itemPtr);
                var submenu = submenuPtr == IntPtr.Zero ? null : ExtractContextMenu(submenuPtr, targets);

                // Showing an entry that cannot be carried out is worse than not offering it: WebKit only runs a
                // stock action itself when it also owns the menu, and this one is ours.
                if (submenu is null && !CanPerform(stockAction, action, targets))
                {
                    continue;
                }

                result.Add(new GtkContextMenuItemModel
                {
                    Label = Marshal.PtrToStringUTF8(webkit_context_menu_item_get_title(itemPtr)) ?? string.Empty,
                    IsEnabled = action == IntPtr.Zero || g_action_get_enabled(action),
                    StockAction = stockAction,
                    Action = action,
                    ActionTarget = webkit_context_menu_item_get_gaction_target(itemPtr),
                    Submenu = submenu
                });
            }

            // Dropping items can leave separators stranded at either end, or doubled up in the middle.
            while (result.Count > 0 && result[0].IsSeparator)
            {
                result.RemoveAt(0);
            }

            while (result.Count > 0 && result[^1].IsSeparator)
            {
                result.RemoveAt(result.Count - 1);
            }

            for (var i = result.Count - 1; i > 0; i--)
            {
                if (result[i].IsSeparator && result[i - 1].IsSeparator)
                {
                    result.RemoveAt(i);
                }
            }

            return result;
        }

        private static string? TargetFor(int action, GtkContextMenuTargets targets)
        {
            if (action == ContextMenuActions.OpenLink
                || action == ContextMenuActions.OpenLinkInNewWindow
                || action == ContextMenuActions.CopyLinkToClipboard
                || action == ContextMenuActions.DownloadLinkToDisk)
            {
                return targets.LinkUri;
            }

            if (action == ContextMenuActions.OpenImageInNewWindow
                || action == ContextMenuActions.DownloadImageToDisk
                || action == ContextMenuActions.CopyImageUrlToClipboard)
            {
                return targets.ImageUri;
            }

            if (action == ContextMenuActions.OpenVideoInNewWindow
                || action == ContextMenuActions.OpenAudioInNewWindow
                || action == ContextMenuActions.CopyVideoLinkToClipboard
                || action == ContextMenuActions.CopyAudioLinkToClipboard
                || action == ContextMenuActions.DownloadVideoToDisk
                || action == ContextMenuActions.DownloadAudioToDisk)
            {
                return targets.MediaUri;
            }

            return null;
        }

        private static bool CanPerform(int action, IntPtr gAction, GtkContextMenuTargets targets)
        {
            // An item the host added carries its own action, which is not WebKit's to dispatch and still works.
            if (ContextMenuActions.Custom >= 0 && action >= ContextMenuActions.Custom)
            {
                return gAction != IntPtr.Zero;
            }

            if (action == ContextMenuActions.GoBack
                || action == ContextMenuActions.GoForward
                || action == ContextMenuActions.Stop
                || action == ContextMenuActions.Reload
                || action == ContextMenuActions.InspectElement
                || EditingCommandFor(action) is not null)
            {
                return true;
            }

            return TargetFor(action, targets) is { Length: > 0 };
        }

        private void Perform(GtkContextMenuItemModel model)
        {
            var webView = _adapter.WebViewHandle;
            if (webView == IntPtr.Zero)
            {
                return;
            }

            var action = model.StockAction;
            if (ContextMenuActions.Custom >= 0 && action >= ContextMenuActions.Custom)
            {
                RunOnGlibThreadAsync(() => g_action_activate(model.Action, model.ActionTarget));
                return;
            }

            var target = TargetFor(action, _targets);

            if (action == ContextMenuActions.GoBack)
            {
                RunOnGlibThreadAsync(() => webkit_web_view_go_back(webView));
            }
            else if (action == ContextMenuActions.GoForward)
            {
                RunOnGlibThreadAsync(() => webkit_web_view_go_forward(webView));
            }
            else if (action == ContextMenuActions.Stop)
            {
                RunOnGlibThreadAsync(() => webkit_web_view_stop_loading(webView));
            }
            else if (action == ContextMenuActions.Reload)
            {
                RunOnGlibThreadAsync(() => webkit_web_view_reload(webView));
            }
            else if (action == ContextMenuActions.InspectElement)
            {
                RunOnGlibThreadAsync(() =>
                {
                    var inspector = webkit_web_view_get_inspector(webView);
                    if (inspector != IntPtr.Zero)
                    {
                        webkit_web_inspector_show(inspector);
                    }
                });
            }
            else if (EditingCommandFor(action) is { } command)
            {
                RunOnGlibThreadAsync(() => webkit_web_view_execute_editing_command(webView, command));
            }
            else if (target is { Length: > 0 })
            {
                if (action == ContextMenuActions.OpenLink)
                {
                    RunOnGlibThreadAsync(() => webkit_web_view_load_uri(webView, target));
                }
                else if (action == ContextMenuActions.OpenLinkInNewWindow
                         || action == ContextMenuActions.OpenImageInNewWindow
                         || action == ContextMenuActions.OpenVideoInNewWindow
                         || action == ContextMenuActions.OpenAudioInNewWindow)
                {
                    if (Uri.TryCreate(target, UriKind.Absolute, out var newWindowUri))
                    {
                        _adapter.RaiseNewWindowRequested(newWindowUri);
                    }
                }
                else if (action == ContextMenuActions.CopyLinkToClipboard
                         || action == ContextMenuActions.CopyImageUrlToClipboard
                         || action == ContextMenuActions.CopyVideoLinkToClipboard
                         || action == ContextMenuActions.CopyAudioLinkToClipboard)
                {
                    CopyToClipboard(target);
                }
                else if (action == ContextMenuActions.DownloadLinkToDisk
                         || action == ContextMenuActions.DownloadImageToDisk
                         || action == ContextMenuActions.DownloadVideoToDisk
                         || action == ContextMenuActions.DownloadAudioToDisk)
                {
                    RunOnGlibThreadAsync(() =>
                        webkit_web_context_download_uri(webkit_web_view_get_context(webView), target));
                }
            }
        }

        // The names WebKit's editing command registry uses.
        private static string? EditingCommandFor(int action)
        {
            if (action == ContextMenuActions.Copy) return "Copy";
            if (action == ContextMenuActions.Cut) return "Cut";
            if (action == ContextMenuActions.Paste) return "Paste";
            if (action == ContextMenuActions.Bold) return "Bold";
            if (action == ContextMenuActions.Italic) return "Italic";
            if (action == ContextMenuActions.Underline) return "Underline";
            if (action == ContextMenuActions.Outline) return "Outline";
            return null;
        }

        private void CopyToClipboard(string text)
        {
            if (_contextMenu is { } menu && TopLevel.GetTopLevel(menu.PlacementTarget) is { Clipboard: { } clipboard })
            {
                _ = clipboard.SetTextAsync(text);
            }
        }

        private class GtkContextMenuItemModel
        {
            public int StockAction { get; init; }

            public string Label { get; init; } = string.Empty;
            public bool IsSeparator { get; init; }
            public bool IsEnabled { get; init; }
            public IntPtr Action { get; init; }
            public IntPtr ActionTarget { get; init; }
            public List<GtkContextMenuItemModel>? Submenu { get; init; }
        }
    }

    private class GtkOptionsMenuState : IDisposable
    {
        private readonly bool _isMouseRequest;
        private readonly GdkRectangle _rect;
        private readonly GtkOffscreenAvaloniaWebViewAdapter _adapter;
        private readonly GtkSignal _closeSignal;
        private IntPtr _menu;
        private ContextMenu? _contextMenu;

        public GtkOptionsMenuState(IntPtr menu, bool isMouseRequest, GdkRectangle rect,
            GtkOffscreenAvaloniaWebViewAdapter adapter)
        {
            g_object_ref(menu);
            _menu = menu;
            _isMouseRequest = isMouseRequest;
            _rect = rect;
            _adapter = adapter;
            _closeSignal = new GtkSignal(menu, "close", s_optionsMenuClosedCallback, this);
        }

        public void ClosedRequested()
        {
            WebViewDispatcher.InvokeAsync(() => { _contextMenu?.Close(); });
        }

        public void Open()
        {
            _adapter._openedMenus.Add(this);
            var nativeMenuItems = ExtractMenu(_menu);

            WebViewDispatcher.InvokeAsync(() =>
            {
                var actualWebView = (Control?)_adapter.Parent?.GetVisualParent()!;
                var pixelRect = new PixelRect(_rect.x, _rect.y, _rect.width, _rect.height);
                _contextMenu = new ContextMenu
                {
                    Placement = PlacementMode.Bottom,
                    PlacementRect = pixelRect.ToRect(TopLevel.GetTopLevel(actualWebView)!.RenderScaling),
                    VerticalOffset = 4,
                    PlacementTarget = actualWebView,
                    DataContext = this
                };
                _contextMenu.Closed += static (el, _) =>
                {
                    if (el is ContextMenu { DataContext: GtkOptionsMenuState state })
                    {
                        state.Dispose(true, true);
                    }
                };

                string? currentGroup = null;
                foreach (var item in nativeMenuItems)
                {
                    if (item.GroupLabel)
                    {
                        currentGroup = item.Label;
                        if (_contextMenu.Items.Count > 0)
                        {
                            _contextMenu.Items.Add(new Separator());
                        }
                    }
                    else
                    {
                        var menuItem = new MenuItem
                        {
                            Header = item.Label,
                            IsEnabled = item.IsEnabled,
                            IsChecked = item.IsSelected,
                            ToggleType = item.ToggleType,
                            DataContext = (this, item.Index),
                            GroupName = item.GroupChild ? currentGroup : null,
                            [ToolTip.TipProperty] = item.Tooltip
                        };

                        _contextMenu.Items.Add(menuItem);

                        menuItem.Click += static (el, _) =>
                        {
                            if (el is MenuItem
                                {
                                    IsChecked: true,
                                    DataContext: ValueTuple<GtkOptionsMenuState, uint> state
                                })
                            {
                                RunOnGlibThreadAsync(() =>
                                {
                                    if (state.Item1._menu != IntPtr.Zero)
                                    {
                                        webkit_option_menu_activate_item(state.Item1._menu, state.Item2);
                                    }
                                });
                            }
                        };
                    }
                }

                _contextMenu.Open(actualWebView);
            });
        }

        public void Dispose()
        {
            Dispose(true, false);
        }

        private void Dispose(bool disposing, bool close)
        {
            var menu = Interlocked.Exchange(ref _menu, IntPtr.Zero);
            if (menu != IntPtr.Zero)
            {
                RunOnGlibThreadAsync(() =>
                {
                    if (close)
                    {
                        webkit_option_menu_close(menu);
                    }

                    g_object_unref(menu);

                    if (disposing)
                    {
                        _closeSignal.Dispose();
                        _adapter._openedMenus.Remove(this);
                    }
                });
            }

            if (disposing)
            {
                GC.SuppressFinalize(this);
            }
        }

        ~GtkOptionsMenuState()
        {
            Dispose(false, false);
        }

        private static List<GtkMenuItemModel> ExtractMenu(IntPtr menuPtr)
        {
            var result = new List<GtkMenuItemModel>();
            var itemCount = webkit_option_menu_get_n_items(menuPtr);

            for (uint i = 0; i < itemCount; i++)
            {
                var itemPtr = webkit_option_menu_get_item(menuPtr, i);
                if (itemPtr == IntPtr.Zero)
                    continue;

                var groupLabel = webkit_option_menu_item_is_group_label(itemPtr);
                result.Add(new GtkMenuItemModel
                {
                    Index = i,
                    Label = Marshal.PtrToStringAnsi(webkit_option_menu_item_get_label(itemPtr)) ?? string.Empty,
                    Tooltip = Marshal.PtrToStringAnsi(webkit_option_menu_item_get_tooltip(itemPtr)),
                    IsEnabled = groupLabel || webkit_option_menu_item_is_enabled(itemPtr),
                    IsSelected = webkit_option_menu_item_is_selected(itemPtr),
                    ToggleType = groupLabel ? MenuItemToggleType.None : MenuItemToggleType.Radio,
                    GroupLabel = groupLabel,
                    GroupChild = webkit_option_menu_item_is_group_child(itemPtr)
                });
            }

            return result;
        }

        private class GtkMenuItemModel
        {
            public uint Index { get; init; }
            public string Label { get; init; } = string.Empty;
            public string? Tooltip { get; init; }
            public bool IsEnabled { get; init; }
            public bool IsSelected { get; init; }
            public MenuItemToggleType ToggleType { get; init; }
            public bool GroupLabel { get; init; }
            public bool GroupChild { get; init; }
        }
    }
}
