"""Interactive local acceptance test. Shows temporary previews; sends no approval or keystrokes."""
import argparse
import os
import uuid
import ctypes as ct
from ctypes import wintypes as wt
from pathlib import Path
import subprocess
import time
import tkinter as tk

parser = argparse.ArgumentParser()
parser.add_argument("exe", type=Path)
parser.add_argument("--minimized-only", action="store_true")
parser.add_argument("--chat", type=uuid.UUID, help="Use an indexed source chat for completion previews.")
args = parser.parse_args()
exe = args.exe.resolve(strict=True)
user = ct.WinDLL("user32", use_last_error=True)
kernel = ct.WinDLL("kernel32", use_last_error=True)
def api(lib, name, result, *params):
    function = getattr(lib, name)
    function.restype, function.argtypes = result, params
    return function
handle = wt.HWND
callback = ct.WINFUNCTYPE(wt.BOOL, handle, wt.LPARAM)
foreground = api(user, "GetForegroundWindow", handle)
set_front = api(user, "SetForegroundWindow", wt.BOOL, handle)
get_rect = api(user, "GetWindowRect", wt.BOOL, handle, ct.POINTER(wt.RECT))
get_pid = api(user, "GetWindowThreadProcessId", wt.DWORD, handle, ct.POINTER(wt.DWORD))
open_process = api(kernel, "OpenProcess", wt.HANDLE, wt.DWORD, wt.BOOL, wt.DWORD)
close_handle = api(kernel, "CloseHandle", wt.BOOL, wt.HANDLE)
get_path = api(kernel, "QueryFullProcessImageNameW", wt.BOOL, wt.HANDLE, wt.DWORD, wt.LPWSTR, ct.POINTER(wt.DWORD))
enum = api(user, "EnumWindows", wt.BOOL, callback, wt.LPARAM)
visible = api(user, "IsWindowVisible", wt.BOOL, handle)
iconic = api(user, "IsIconic", wt.BOOL, handle)
show = api(user, "ShowWindow", wt.BOOL, handle, ct.c_int)
get_title = api(user, "GetWindowTextW", ct.c_int, handle, wt.LPWSTR, ct.c_int)
get_class = api(user, "GetClassNameW", ct.c_int, handle, wt.LPWSTR, ct.c_int)
get_monitor = api(user, "MonitorFromWindow", wt.HANDLE, handle, wt.DWORD)
class MonitorInfo(ct.Structure):
    _fields_ = [("size", wt.DWORD), ("monitor", wt.RECT), ("work", wt.RECT), ("flags", wt.DWORD)]
monitor_info = api(user, "GetMonitorInfoW", wt.BOOL, wt.HANDLE, ct.POINTER(MonitorInfo))
api(user, "SetProcessDpiAwarenessContext", wt.BOOL, ct.c_void_p)(ct.c_void_p(-4))

def image_path(hwnd):
    pid = wt.DWORD()
    get_pid(hwnd, ct.byref(pid))
    proc = open_process(0x1000, False, pid)
    if not proc:
        return ""
    try:
        buf, size = ct.create_unicode_buffer(2048), wt.DWORD(2048)
        return buf.value if get_path(proc, 0, buf, ct.byref(size)) else ""
    finally:
        close_handle(proc)

def windows():
    result = []
    @callback
    def collect(hwnd, _):
        if visible(hwnd):
            result.append(hwnd)
        return True
    enum(collect, 0)
    return result

def rect(hwnd):
    r = wt.RECT()
    get_rect(hwnd, ct.byref(r))
    return r

def popup():
    for hwnd in windows():
        buf = ct.create_unicode_buffer(128)
        get_title(hwnd, buf, len(buf))
        if buf.value == "浮笺提醒" and image_path(hwnd).casefold() == str(exe).casefold():
            return hwnd
    return None

def codex_main():
    matches = []
    for hwnd in windows():
        buf = ct.create_unicode_buffer(128)
        get_class(hwnd, buf, len(buf))
        path = image_path(hwnd).lower()
        if "\\openai.codex_" not in path:
            continue
        caption=ct.create_unicode_buffer(256)
        get_title(hwnd,caption,len(caption))
        if not caption.value or (caption.value not in ("ChatGPT","Codex") and buf.value!="Chrome_WidgetWin_1"): continue
        r = rect(hwnd)
        has_min=bool(api(user,"GetWindowLongW",ct.c_long,handle,ct.c_int)(hwnd,-16)&0x20000)
        matches.append((caption.value in ("ChatGPT","Codex"),has_min,(r.right-r.left)*(r.bottom-r.top), hwnd))
    return max(matches)[-1] if matches else None

def tell(*args):
    subprocess.run([str(exe), *args], timeout=4, check=True)
def wait_for(test, seconds=3):
    until = time.monotonic() + seconds
    while time.monotonic() < until:
        root.update()
        value = test()
        if value:
            return value
        time.sleep(.06)
    return None

class GuiThreadInfo(ct.Structure):
    _fields_ = [("size", wt.DWORD), ("flags", wt.DWORD),
                *[(name, handle) for name in ("active","focus","capture","menu","move","caret")],
                ("caret_rect", wt.RECT)]

query_gui = api(user, "GetGUIThreadInfo", wt.BOOL, wt.DWORD, ct.POINTER(GuiThreadInfo))
def keyboard_window(hwnd):
    info = GuiThreadInfo()
    info.size = ct.sizeof(info)
    return info.focus if query_gui(get_pid(hwnd, None), ct.byref(info)) else None

def verify_drag(card, window, keyboard):
    # Send mouse messages only to the verified preview HWND; leave the user's pointer untouched.
    send = api(user, "SendMessageW", ct.c_ssize_t, handle, wt.UINT, wt.WPARAM, wt.LPARAM)
    before = rect(card)
    dpi = api(user, "GetDpiForWindow", wt.UINT, handle)(card)
    x, y = (before.right - before.left) // 3, round(45 * dpi / 96)
    def point(x, y):
        return ((y & 0xffff) << 16) | (x & 0xffff)
    dx, dy = 97, 53
    assert popup() == card
    send(card, 0x201, 1, point(x, y))
    try:
        send(card, 0x200, 1, point(x + dx, y + dy))
    finally:
        moved = rect(card)
        send(card, 0x202, 0, point(x + dx - (moved.left-before.left), y + dy - (moved.top-before.top)))
    time.sleep(.45)
    after = rect(card)
    assert visible(card) and (after.left-before.left, after.top-before.top) == (dx, dy), "Drag did not persist"
    if api(user, "GetSystemMetrics", ct.c_int, ct.c_int)(68) // 2 > 1:
        # A new press must use its own drag threshold even after this card was moved.
        send(card, 0x201, 1, point(x, y))
        try:
            send(card, 0x200, 1, point(x + 1, y))
        finally:
            current = rect(card)
            send(card, 0x202, 0, point(x + 1 - (current.left-after.left), y))
        assert rect(card).left == after.left, "A one-pixel second press moved the card"
    assert foreground() == window and keyboard_window(window) == keyboard, "Dragging changed the reference focus"

def verify_cards(state, window):
    for kind in ("done", "question", "permission"):
        keyboard = keyboard_window(window)
        assert foreground() == window and keyboard, f"{state}: no focused input window"
        if kind == "done" and args.chat:
            tell("--preview", kind, str(args.chat))
        else:
            tell("--preview", kind)
        card = wait_for(popup)
        assert card, f"{state}/{kind}: no visible popup"
        dx, dy = centered(card, window)
        assert dx <= 2 and dy <= 2, f"{state}/{kind}: off center ({dx}, {dy})"
        until = time.monotonic() + 2.6
        while time.monotonic() < until:
            root.update()
            front = foreground()
            focused = keyboard_window(front) if front else None
            assert visible(card) and front == window and focused == keyboard, (
                f"{state}/{kind}: visible={bool(visible(card))}, expected window/input={window}/{keyboard}, "
                f"observed={front}/{focused} ({Path(image_path(front)).name if front else 'no foreground'})"
            )
            time.sleep(.03)
        verify_drag(card, window, keyboard)
        print(f"{state}/{kind}: initially centered ({dx}, {dy}), dragged (97, 53), foreground and keyboard window retained", flush=True)
        assert dismiss_preview()

def focus_reference(hwnd):
    if hwnd == api(user,"GetAncestor",handle,handle,wt.UINT)(root.winfo_id(),2):
        root.lift()
        root.focus_force()
    else:
        maximized=bool(api(user,"IsZoomed",wt.BOOL,handle)(hwnd))
        show(hwnd,6)
        wait_for(lambda: iconic(hwnd),1)
        show(hwnd,3 if maximized else 9)
    set_front(hwnd)
    if wait_for(lambda: foreground()==hwnd, .4):
        return True
    front=foreground()
    if not front:
        return False
    own=api(kernel,"GetCurrentThreadId",wt.DWORD)()
    active=get_pid(front,None)
    attach=api(user,"AttachThreadInput",wt.BOOL,wt.DWORD,wt.DWORD,wt.BOOL)
    if active==own or not attach(own,active,True):
        return False
    try:
        set_front(hwnd)
    finally:
        attach(own,active,False)
    return bool(wait_for(lambda: foreground()==hwnd, .5))

def dismiss_preview():
    tell("--dismiss-preview")
    return wait_for(lambda: not popup())

def centered(hwnd, reference):
    m = MonitorInfo()
    m.size = ct.sizeof(m)
    assert monitor_info(get_monitor(reference, 2), ct.byref(m))
    r, w = rect(hwnd), m.work
    dx = abs((r.left+r.right)-(w.left+w.right))/2
    dy = abs((r.top+r.bottom)-(w.top+w.bottom))/2
    return (dx, dy)

previous = foreground()
root = tk.Tk()
root.title("Codex 提醒 · 焦点验证")
tk.Label(root, text="正在验证提醒是否会打断当前窗口。\n测试窗口会自动关闭。", padx=22, pady=14).pack()
entry = tk.Entry(root)
entry.pack(pady=8)
entry.focus_set()
root.update()
original_minimized = False
temporarily_minimized = False
was_maximized = False
codex = None
try:
    assert popup() is None, "An actual alert is open. Complete it first."
    ref = api(user,"GetAncestor",handle,handle,wt.UINT)(root.winfo_id(),2)
    if not focus_reference(ref):
        root.attributes("-topmost",True)
        print("Waiting for the reference window to be selected.",flush=True)
        assert wait_for(lambda: foreground()==ref,90), "Could not establish the reference window foreground."
        root.attributes("-topmost",False)
    entry.focus_force()
    if not args.minimized_only:
        verify_cards("other application",ref)
    codex = codex_main()
    if not codex:
        print("Foreground and minimized checks skipped: no ChatGPT main window found")
    else:
        original_minimized=bool(iconic(codex))
        was_maximized=bool(api(user,"IsZoomed",wt.BOOL,handle)(codex))
        if original_minimized: show(codex,9)
        if not args.minimized_only:
            if args.chat:
                os.startfile("codex://threads/" + str(args.chat))
                def active_chat_window():
                    hwnd = foreground()
                    if not hwnd or iconic(hwnd):
                        return None
                    path = image_path(hwnd).lower()
                    return hwnd if "\\openai.codex_" in path and Path(path).stem in ("codex", "chatgpt") else None
                codex = wait_for(active_chat_window, 8) or codex
                assert foreground() == codex, "Source conversation did not become foreground."
                time.sleep(.7)
            else:
                assert focus_reference(codex), "Cannot focus ChatGPT for the foreground checks."
            verify_cards("ChatGPT foreground",codex)
        # Ask the window to minimize through its normal system menu command.
        post=api(user,"PostMessageW",wt.BOOL,handle,wt.UINT,wt.WPARAM,wt.LPARAM)
        post(codex,0x0112,0xF020,0)
        temporarily_minimized=True
        if not wait_for(lambda: iconic(codex),1.5): show(codex,6)
        assert wait_for(lambda: iconic(codex)), "The Codex main window did not enter the minimized state."
        assert focus_reference(ref), "Could not focus the reference input after minimizing ChatGPT."
        entry.focus_force()
        verify_cards("ChatGPT minimized",ref)
finally:
    tell("--dismiss-preview")
    if codex and original_minimized: show(codex,6)
    elif codex and temporarily_minimized: show(codex,3 if was_maximized else 9)
    root.destroy()
    if previous and visible(previous): set_front(previous)
