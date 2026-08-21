// PasteJump's gesture, in plain Win32 C++ - no .NET, no CLR, no P/Invoke.
//
// A spike, not a product: it captures text clips, walks them with the real gesture (hold Ctrl, tap V,
// release to paste) and shows an overlay while browsing. No settings, no database, no images, no pinning,
// no history window. Text only, in memory, gone when it closes.
//
// It exists for two reasons:
//
//   1. To test the gesture in every application without the rest of PasteJump in the way.
//   2. To answer, permanently, whether the managed runtime has anything to do with the keyboard blackout
//      that endpoint security can impose on one application. It does not: run at medium integrity on
//      2026-08-21 this native build was blind in exactly the same application the .NET one was, with a
//      control passing either side of every attempt. See README.md.
//
// Build:  tests\PasteJump.NativeGestureSpike\build.cmd   ->  artifacts\native-spike\pjnative.exe
// Run:    pjnative.exe            resident, use the gesture yourself
//         pjnative.exe --sweep    drives itself through every window and reports

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <string>
#include <vector>

namespace
{
    // Stamped into our own injected input so the hook can ignore it. Matching on this rather than on
    // LLKHF_INJECTED is not a detail: that flag is set by every process calling SendInput, so filtering on it
    // kills the gesture under Remote Desktop, in VM guests and for anyone on a macro or on-screen keyboard.
    constexpr ULONG_PTR kSignature = 0x504A4E56;  // 'PJNV'

    constexpr UINT WM_PJ_STEP = WM_APP + 1;
    constexpr UINT WM_PJ_COMMIT = WM_APP + 2;
    constexpr UINT WM_PJ_CANCEL = WM_APP + 3;

    constexpr size_t kMaxClips = 25;
    constexpr int kSettleDelayMs = 25;

    HHOOK g_hook = nullptr;
    HWND g_overlay = nullptr;
    HWND g_messages = nullptr;

    std::vector<std::wstring> g_clips;
    size_t g_cursor = 0;
    bool g_active = false;

    // What we last put on the clipboard in order to paste, so the change it provokes is not captured back as
    // a new clip. PasteJump hashes the payloads for this; comparing the text is enough for a text-only spike.
    std::wstring g_selfWrite;

    bool g_sweeping = false;
    int g_keysSeenThisTarget = 0;

    // Restricts the sweep to one process, so it can be pointed at a safe window rather than typing into
    // everything somebody has open. Empty means every window.
    std::wstring g_only;

    void Log(const wchar_t* format, ...)
    {
        va_list args;
        va_start(args, format);
        vwprintf(format, args);
        va_end(args);
        wprintf(L"\n");
        fflush(stdout);
    }

    std::wstring OneLine(const std::wstring& text, size_t limit)
    {
        std::wstring flat;
        flat.reserve(text.size());

        for (wchar_t c : text)
        {
            flat.push_back(c == L'\r' || c == L'\n' || c == L'\t' ? L' ' : c);
        }

        return flat.size() > limit ? flat.substr(0, limit) + L"..." : flat;
    }

    // ---------------------------------------------------------------- clipboard

    std::wstring ReadClipboardText()
    {
        // Bounded retry: the clipboard is a machine-wide lock any process can be holding, so this must be
        // able to fail rather than spin. PasteJump ramps out to about 620 ms; the shape matters more than
        // the numbers here.
        for (int attempt = 0; attempt < 8; ++attempt)
        {
            if (OpenClipboard(nullptr))
            {
                std::wstring text;
                HANDLE handle = GetClipboardData(CF_UNICODETEXT);

                if (handle != nullptr)
                {
                    const wchar_t* locked = static_cast<const wchar_t*>(GlobalLock(handle));

                    if (locked != nullptr)
                    {
                        text = locked;
                        GlobalUnlock(handle);
                    }
                }

                CloseClipboard();
                return text;
            }

            Sleep(attempt * 15);
        }

        return std::wstring();
    }

    bool WriteClipboardText(const std::wstring& text)
    {
        for (int attempt = 0; attempt < 8; ++attempt)
        {
            if (OpenClipboard(nullptr))
            {
                EmptyClipboard();

                const size_t bytes = (text.size() + 1) * sizeof(wchar_t);
                HGLOBAL block = GlobalAlloc(GMEM_MOVEABLE, bytes);
                bool ok = false;

                if (block != nullptr)
                {
                    void* target = GlobalLock(block);

                    if (target != nullptr)
                    {
                        memcpy(target, text.c_str(), bytes);
                        GlobalUnlock(block);
                        ok = SetClipboardData(CF_UNICODETEXT, block) != nullptr;
                    }

                    if (!ok)
                    {
                        GlobalFree(block);
                    }
                }

                CloseClipboard();
                return ok;
            }

            Sleep(attempt * 15);
        }

        return false;
    }

    void CaptureClipboard()
    {
        std::wstring text = ReadClipboardText();

        if (text.empty())
        {
            return;
        }

        if (text == g_selfWrite)
        {
            Log(L"  capture skipped: this is our own write, put there in order to paste");
            g_selfWrite.clear();
            return;
        }

        if (!g_clips.empty() && g_clips.front() == text)
        {
            Log(L"  capture skipped: same as the newest clip");
            return;
        }

        g_clips.insert(g_clips.begin(), text);

        if (g_clips.size() > kMaxClips)
        {
            g_clips.pop_back();
        }

        // A new copy resets the browse position, or every gesture would reopen on a stale clip.
        g_cursor = 0;

        Log(L"  captured clip 1 of %zu: \"%s\"", g_clips.size(), OneLine(text, 48).c_str());
    }

    // ---------------------------------------------------------------- overlay

    void PositionOverlay()
    {
        const int width = 460;
        const int height = 92;

        // Beside the caret when the focused application exposes one, otherwise the centre of the foreground
        // window - which cannot land on the wrong monitor. Most applications expose no Win32 caret at all,
        // so the fallback is the common case rather than the rare one.
        POINT anchor = {};
        bool haveAnchor = false;

        GUITHREADINFO gui = { sizeof(GUITHREADINFO) };

        if (GetGUIThreadInfo(0, &gui) && gui.hwndCaret != nullptr)
        {
            POINT caret = { gui.rcCaret.left, gui.rcCaret.bottom };

            if (ClientToScreen(gui.hwndCaret, &caret))
            {
                anchor = { caret.x + 4, caret.y + 20 };
                haveAnchor = true;
            }
        }

        if (!haveAnchor)
        {
            HWND foreground = GetForegroundWindow();
            RECT rect = {};

            if (foreground != nullptr && !IsIconic(foreground) && GetWindowRect(foreground, &rect))
            {
                anchor = { (rect.left + rect.right) / 2 - width / 2, (rect.top + rect.bottom) / 2 - height / 2 };
                haveAnchor = true;
            }
        }

        if (!haveAnchor)
        {
            anchor = { (GetSystemMetrics(SM_CXSCREEN) - width) / 2, (GetSystemMetrics(SM_CYSCREEN) - height) / 2 };
        }

        SetWindowPos(g_overlay, HWND_TOPMOST, anchor.x, anchor.y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    void ShowOverlay()
    {
        PositionOverlay();
        InvalidateRect(g_overlay, nullptr, TRUE);
        UpdateWindow(g_overlay);
    }

    void HideOverlay()
    {
        ShowWindow(g_overlay, SW_HIDE);
    }

    LRESULT CALLBACK OverlayProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message != WM_PAINT)
        {
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        PAINTSTRUCT ps;
        HDC dc = BeginPaint(hwnd, &ps);

        RECT rect;
        GetClientRect(hwnd, &rect);

        HBRUSH background = CreateSolidBrush(RGB(18, 27, 33));
        FillRect(dc, &rect, background);
        DeleteObject(background);

        HFONT font = CreateFontW(-13, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Consolas");
        HGDIOBJ previous = SelectObject(dc, font);

        SetBkMode(dc, TRANSPARENT);

        std::wstring headline = L"no clips";
        std::wstring preview;

        if (!g_clips.empty() && g_cursor < g_clips.size())
        {
            headline = L"clip " + std::to_wstring(g_cursor + 1) + L" of " + std::to_wstring(g_clips.size())
                + L"   (native spike)";
            preview = OneLine(g_clips[g_cursor], 60);
        }

        RECT line = { 12, 8, rect.right - 12, 30 };
        SetTextColor(dc, RGB(127, 194, 206));
        DrawTextW(dc, headline.c_str(), -1, &line, DT_LEFT | DT_SINGLELINE);

        RECT body = { 12, 34, rect.right - 12, rect.bottom - 8 };
        SetTextColor(dc, RGB(228, 237, 242));
        DrawTextW(dc, preview.c_str(), -1, &body, DT_LEFT | DT_WORDBREAK | DT_END_ELLIPSIS);

        SelectObject(dc, previous);
        DeleteObject(font);
        EndPaint(hwnd, &ps);
        return 0;
    }

    // ---------------------------------------------------------------- gesture

    void SendKey(WORD vk, bool up, ULONG_PTR extra)
    {
        INPUT input = {};
        input.type = INPUT_KEYBOARD;
        input.ki.wVk = vk;

        // A real scan code, because wScan == 0 is invisible to anything reading scan codes rather than
        // virtual keys: RDP and Citrix clients, VM guests, and various Qt and Java applications. This is the
        // "works in Notepad, not in that application" shape.
        input.ki.wScan = static_cast<WORD>(MapVirtualKeyW(vk, MAPVK_VK_TO_VSC));
        input.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
        input.ki.dwExtraInfo = extra;
        SendInput(1, &input, sizeof(INPUT));
    }

    void Commit()
    {
        HideOverlay();

        if (g_clips.empty() || g_cursor >= g_clips.size())
        {
            return;
        }

        const std::wstring clip = g_clips[g_cursor];

        // Never send the keystroke unless the write succeeded: a paste after a failed write pastes whatever
        // was there before, silently, and looks exactly like choosing the wrong clip.
        g_selfWrite = clip;

        if (!WriteClipboardText(clip))
        {
            Log(L"  clipboard busy - nothing pasted");
            g_selfWrite.clear();
            return;
        }

        Sleep(kSettleDelayMs);

        SendKey(VK_CONTROL, false, kSignature);
        SendKey('V', false, kSignature);
        SendKey('V', true, kSignature);
        SendKey(VK_CONTROL, true, kSignature);

        Log(L"  pasted clip %zu: \"%s\"", g_cursor + 1, OneLine(clip, 48).c_str());

        // Browsing starts from the newest clip next time, which is what makes a plain Ctrl+V predictable.
        g_cursor = 0;
    }

    LRESULT CALLBACK HookProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code != HC_ACTION)
        {
            return CallNextHookEx(nullptr, code, wParam, lParam);
        }

        const KBDLLHOOKSTRUCT* key = reinterpret_cast<const KBDLLHOOKSTRUCT*>(lParam);

        // Our own paste coming back round. Without this, sending Ctrl+V re-enters the gesture for ever.
        if (key->dwExtraInfo == kSignature)
        {
            return CallNextHookEx(nullptr, code, wParam, lParam);
        }

        ++g_keysSeenThisTarget;

        const bool down = (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN);
        const bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        const bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        const bool win = ((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) & 0x8000) != 0;
        const bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

        // Ctrl plus the trigger and nothing else. Alt is excluded because AltGr IS Ctrl+Alt on many layouts,
        // Win belongs to the shell, and Ctrl+Shift+V is how every terminal pastes.
        if (down && key->vkCode == 'V' && ctrl && !alt && !win && !shift)
        {
            if (g_clips.empty())
            {
                // Never swallow Ctrl+V with nothing to offer: that would break pasting system-wide.
                return CallNextHookEx(nullptr, code, wParam, lParam);
            }

            PostMessageW(g_messages, WM_PJ_STEP, 0, 0);
            return 1;
        }

        if (down && key->vkCode == VK_ESCAPE && g_active)
        {
            PostMessageW(g_messages, WM_PJ_CANCEL, 0, 0);
            return 1;
        }

        const bool isCtrl = key->vkCode == VK_CONTROL || key->vkCode == VK_LCONTROL || key->vkCode == VK_RCONTROL;

        if (!down && isCtrl && g_active)
        {
            // Posted, never done here: this callback blocks all keyboard input machine-wide until it returns,
            // and the commit writes the clipboard and sleeps before injecting.
            PostMessageW(g_messages, WM_PJ_COMMIT, 0, 0);
        }

        return CallNextHookEx(nullptr, code, wParam, lParam);
    }

    LRESULT CALLBACK MessageProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case WM_CLIPBOARDUPDATE:
                CaptureClipboard();
                return 0;

            case WM_PJ_STEP:
                if (!g_active)
                {
                    g_active = true;
                }
                else
                {
                    g_cursor = (g_cursor + 1) % g_clips.size();
                }

                ShowOverlay();
                return 0;

            case WM_PJ_COMMIT:
                g_active = false;
                Commit();
                return 0;

            case WM_PJ_CANCEL:
                g_active = false;
                HideOverlay();
                Log(L"  cancelled");
                return 0;

            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    // ---------------------------------------------------------------- sweep

    struct Target
    {
        std::wstring label;
        HWND window;
    };

    std::vector<Target> g_found;

    std::wstring ProcessNameOf(HWND hwnd)
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);

        if (process == nullptr)
        {
            return L"(unknown)";
        }

        wchar_t path[MAX_PATH] = {};
        DWORD size = MAX_PATH;
        std::wstring name = L"(unknown)";

        if (QueryFullProcessImageNameW(process, 0, path, &size))
        {
            std::wstring full(path);
            const size_t slash = full.find_last_of(L'\\');
            name = slash == std::wstring::npos ? full : full.substr(slash + 1);
            const size_t dot = name.find_last_of(L'.');

            if (dot != std::wstring::npos)
            {
                name = name.substr(0, dot);
            }
        }

        CloseHandle(process);
        return name;
    }

    BOOL CALLBACK Collect(HWND hwnd, LPARAM)
    {
        if (!IsWindowVisible(hwnd) || GetWindowTextLengthW(hwnd) == 0 || hwnd == g_overlay)
        {
            return TRUE;
        }

        const std::wstring name = ProcessNameOf(hwnd);

        if (!g_only.empty() && _wcsicmp(name.c_str(), g_only.c_str()) != 0)
        {
            return TRUE;
        }

        wchar_t title[160] = {};
        GetWindowTextW(hwnd, title, 120);
        g_found.push_back({ name + L": " + title, hwnd });
        return TRUE;
    }

    void Pump(int milliseconds)
    {
        const ULONGLONG until = GetTickCount64() + static_cast<ULONGLONG>(milliseconds);

        while (GetTickCount64() < until)
        {
            MSG msg;

            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }

            Sleep(5);
        }
    }

    bool Focus(HWND window)
    {
        const HWND foreground = GetForegroundWindow();
        const DWORD foregroundThread = GetWindowThreadProcessId(foreground, nullptr);
        const DWORD self = GetCurrentThreadId();

        AttachThreadInput(self, foregroundThread, TRUE);
        SetForegroundWindow(window);
        AttachThreadInput(self, foregroundThread, FALSE);
        Pump(200);

        DWORD wanted = 0;
        DWORD actual = 0;
        GetWindowThreadProcessId(window, &wanted);
        GetWindowThreadProcessId(GetForegroundWindow(), &actual);
        return wanted == actual;
    }

    void Sweep()
    {
        g_sweeping = true;
        g_clips.clear();
        g_clips.push_back(L"native spike test clip");

        EnumWindows(Collect, 0);

        Log(L"%-52s %-9s %-9s %-8s", L"target", L"focused", L"hook saw", L"overlay");
        Log(L"----------------------------------------------------------------------------------");

        for (const Target& target : g_found)
        {
            if (!Focus(target.window))
            {
                continue;
            }

            g_keysSeenThisTarget = 0;
            g_active = false;
            HideOverlay();
            Pump(120);

            SendKey(VK_CONTROL, false, 0);
            Pump(60);
            SendKey('V', false, 0);
            Pump(60);
            SendKey('V', true, 0);
            Pump(220);

            const bool opened = IsWindowVisible(g_overlay);

            // Escape rather than releasing Ctrl into a commit: nothing should be pasted into somebody's
            // windows by a sweep.
            SendKey(VK_ESCAPE, false, 0);
            SendKey(VK_ESCAPE, true, 0);
            Pump(80);
            SendKey(VK_CONTROL, true, 0);
            Pump(150);

            Log(L"%-52s %-9s %-9d %-8s",
                target.label.substr(0, 51).c_str(),
                L"yes",
                g_keysSeenThisTarget,
                opened ? L"OPENED" : L"no");
        }

        g_sweeping = false;
    }

    const wchar_t* Integrity()
    {
        HANDLE token = nullptr;

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        {
            return L"unknown";
        }

        DWORD size = 0;
        GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &size);
        std::vector<BYTE> buffer(size);
        const wchar_t* result = L"unknown";

        if (GetTokenInformation(token, TokenIntegrityLevel, buffer.data(), size, &size))
        {
            const TOKEN_MANDATORY_LABEL* label = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(buffer.data());
            const DWORD rid = *GetSidSubAuthority(label->Label.Sid,
                static_cast<DWORD>(*GetSidSubAuthorityCount(label->Label.Sid) - 1));

            result = rid >= SECURITY_MANDATORY_SYSTEM_RID ? L"SYSTEM"
                : rid >= SECURITY_MANDATORY_HIGH_RID ? L"HIGH (elevated)"
                : rid >= SECURITY_MANDATORY_MEDIUM_RID ? L"MEDIUM"
                : L"LOW";
        }

        CloseHandle(token);
        return result;
    }

    bool PasteJumpIsRunning()
    {
        // Two clipboard managers both swallowing Ctrl+V is not a test, it is a fight: whichever hook runs
        // first consumes the chord and the other never sees it. Refused rather than warned about, because the
        // resulting confusion looks exactly like the fault this spike is used to investigate.
        DWORD processes[2048] = {};
        DWORD needed = 0;

        if (!EnumProcesses(processes, sizeof(processes), &needed))
        {
            return false;
        }

        for (DWORD i = 0; i < needed / sizeof(DWORD); ++i)
        {
            HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processes[i]);

            if (process == nullptr)
            {
                continue;
            }

            wchar_t path[MAX_PATH] = {};
            DWORD size = MAX_PATH;
            bool found = false;

            if (QueryFullProcessImageNameW(process, 0, path, &size))
            {
                std::wstring full(path);
                found = full.size() >= 13 && _wcsicmp(full.c_str() + full.size() - 13, L"PasteJump.exe") == 0;
            }

            CloseHandle(process);

            if (found)
            {
                return true;
            }
        }

        return false;
    }
}

int wmain(int argc, wchar_t** argv)
{
    bool sweep = false;
    bool force = false;

    for (int i = 1; i < argc; ++i)
    {
        if (_wcsicmp(argv[i], L"--sweep") == 0) sweep = true;
        if (_wcsicmp(argv[i], L"--force") == 0) force = true;

        if (_wcsicmp(argv[i], L"--only") == 0 && i + 1 < argc)
        {
            g_only = argv[++i];
        }
    }

    Log(L"PasteJump native gesture spike - plain Win32, no .NET in this process");
    Log(L"integrity: %s", Integrity());

    if (PasteJumpIsRunning() && !force)
    {
        Log(L"");
        Log(L"PasteJump itself is running, and two managers both swallowing Ctrl+V would fight over it -");
        Log(L"whichever hook is called first consumes the chord and the other never sees it. Exit PasteJump");
        Log(L"from its tray icon first, or pass --force if you know what you are doing.");

        // Waits, because this is the one exit path somebody reaches by double-clicking the executable: a
        // console application that prints and returns closes its window instantly, so the explanation would
        // never be read and the spike would look like it did nothing at all.
        Log(L"");
        Log(L"Press Enter to close.");
        (void)getwchar();
        return 1;
    }

    WNDCLASSEXW overlayClass = {};
    overlayClass.cbSize = sizeof(overlayClass);
    overlayClass.lpfnWndProc = OverlayProc;
    overlayClass.hInstance = GetModuleHandleW(nullptr);
    overlayClass.lpszClassName = L"PjNativeOverlay";
    RegisterClassExW(&overlayClass);

    WNDCLASSEXW messageClass = {};
    messageClass.cbSize = sizeof(messageClass);
    messageClass.lpfnWndProc = MessageProc;
    messageClass.hInstance = GetModuleHandleW(nullptr);
    messageClass.lpszClassName = L"PjNativeMessages";
    RegisterClassExW(&messageClass);

    // Never activates, click-through, no taskbar button, always on top - the same extended styles the real
    // overlay applies in code. Focus theft would send the user's paste into this window.
    g_overlay = CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW,
        L"PjNativeOverlay", L"PasteJump native overlay", WS_POPUP,
        0, 0, 460, 92, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);

    g_messages = CreateWindowExW(0, L"PjNativeMessages", L"PasteJump native messages", 0,
        0, 0, 0, 0, HWND_MESSAGE, nullptr, GetModuleHandleW(nullptr), nullptr);

    if (g_overlay == nullptr || g_messages == nullptr)
    {
        Log(L"window creation failed: %lu", GetLastError());
        return 2;
    }

    if (!AddClipboardFormatListener(g_messages))
    {
        Log(L"AddClipboardFormatListener failed: %lu", GetLastError());
    }

    g_hook = SetWindowsHookExW(WH_KEYBOARD_LL, HookProc, GetModuleHandleW(nullptr), 0);

    if (g_hook == nullptr)
    {
        Log(L"SetWindowsHookEx failed: %lu", GetLastError());
        return 2;
    }

    // Whatever is on the clipboard now is clip one, so the gesture works from the first keystroke.
    CaptureClipboard();

    if (sweep)
    {
        Sweep();
    }
    else
    {
        Log(L"");
        Log(L"Ready. Copy a few things, then hold Ctrl and tap V to walk the clips; release to paste.");
        Log(L"Esc cancels. Close this console window to quit.");
        Log(L"");

        MSG msg;

        while (GetMessageW(&msg, nullptr, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    RemoveClipboardFormatListener(g_messages);
    UnhookWindowsHookEx(g_hook);
    return 0;
}
