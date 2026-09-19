// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <wrl.h>
#include <WebView2.h>
#include <filesystem>
#include <memory>
#include <mutex>
#include <sstream>
#include <thread>
#include <type_traits>
#include <vector>
#include "PolicyTests.h"

using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;

namespace
{
constexpr auto Title = L"GitHub Team App";
constexpr auto AppId = L"GitHubTeamApp.Desktop";
constexpr UINT BackendOutput = WM_APP + 1;
constexpr UINT QuitCommand = 1001;
constexpr UINT ReloadCommand = 1002;
constexpr UINT_PTR LifetimeTimer = 1;

struct Handle
{
    HANDLE value = nullptr;
    Handle() = default;
    explicit Handle(HANDLE handle) : value(handle) {}
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    ~Handle() { reset(); }
    void reset(HANDLE next = nullptr)
    {
        if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value);
        value = next;
    }
    explicit operator bool() const { return value && value != INVALID_HANDLE_VALUE; }
};

struct ComString
{
    LPWSTR value = nullptr;
    ~ComString() { CoTaskMemFree(value); }
};

std::wstring ErrorText(HRESULT error)
{
    std::wostringstream text;
    text << L" (0x" << std::hex << static_cast<unsigned long>(error) << L")";
    return text.str();
}

std::wstring Utf8(std::string_view value)
{
    if (value.empty()) return {};
    const auto size = static_cast<int>(value.size());
    const auto length = MultiByteToWideChar(CP_UTF8, 0, value.data(), size, nullptr, 0);
    std::wstring result(static_cast<std::size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), size, result.data(), length);
    return result;
}

std::filesystem::path ExecutableDirectory()
{
    std::vector<wchar_t> path(32768);
    const auto count = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (!count || count >= path.size()) throw std::runtime_error("Could not resolve the application directory.");
    return std::filesystem::path(std::wstring(path.data(), count)).parent_path();
}

class Application
{
public:
    HWND window = nullptr;
    int exitCode = 0;
    std::wstring origin;
    std::wstring dataDirectory;
    Handle parent;

    ~Application()
    {
        // The job also owns descendants (gh, etc.); no process survives a shell crash.
        job.reset();
        if (backend) WaitForSingleObject(backend.value, 5000);
        if (reader.joinable()) reader.join();
        if (controller) controller->Close();
    }

    void Fail(const std::wstring& message, HRESULT error = S_OK)
    {
        if (closing || failing) return;
        failing = true;
        exitCode = 1;
        const auto detail = message + (FAILED(error) ? ErrorText(error) : L"");
        MessageBoxW(window, detail.c_str(), Title, MB_OK | MB_ICONERROR);
        Close();
    }

    void Start()
    {
        ComString version;
        const auto available = GetAvailableCoreWebView2BrowserVersionString(nullptr, &version.value);
        if (FAILED(available))
        {
            Fail(L"Microsoft Edge WebView2 Runtime is required.\n"
                 L"Install the Evergreen Runtime from https://developer.microsoft.com/microsoft-edge/webview2/ "
                 L"and reopen the app, or run github-team.exe --browser.\nNothing has been installed automatically.", available);
            return;
        }
        if (!SetTimer(window, LifetimeTimer, 200, nullptr))
        {
            Fail(L"Could not start the app lifecycle monitor.", HRESULT_FROM_WIN32(GetLastError()));
            return;
        }
        if (origin.empty()) StartBackend();
        else InitializeWebView();
    }

    void StartBackend()
    {
        const auto executable = ExecutableDirectory() / L"github-team.exe";
        if (!std::filesystem::exists(executable))
        {
            Fail(L"The backend github-team.exe is missing. Keep it next to GitHub Team App.exe, "
                 L"or use dotnet run from the source repository.");
            return;
        }
        SECURITY_ATTRIBUTES security{ sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE };
        Handle outputWrite, inputRead;
        if (!CreatePipe(&outputRead.value, &outputWrite.value, &security, 0) ||
            !CreatePipe(&inputRead.value, &inputWrite.value, &security, 0) ||
            !SetHandleInformation(outputRead.value, HANDLE_FLAG_INHERIT, 0) ||
            !SetHandleInformation(inputWrite.value, HANDLE_FLAG_INHERIT, 0))
        {
            Fail(L"Could not create the backend pipes.", HRESULT_FROM_WIN32(GetLastError()));
            return;
        }
        job.reset(CreateJobObjectW(nullptr, nullptr));
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!job || !SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits)))
        {
            Fail(L"Could not create the backend lifetime job.", HRESULT_FROM_WIN32(GetLastError()));
            return;
        }

        SIZE_T bytes = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
        std::vector<unsigned char> attributes(bytes);
        auto list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
        if (!InitializeProcThreadAttributeList(list, 1, 0, &bytes))
        {
            Fail(L"Could not initialize backend process attributes.", HRESULT_FROM_WIN32(GetLastError()));
            return;
        }
        const auto deleteAttributes = [](LPPROC_THREAD_ATTRIBUTE_LIST value) { DeleteProcThreadAttributeList(value); };
        std::unique_ptr<std::remove_pointer_t<LPPROC_THREAD_ATTRIBUTE_LIST>, decltype(deleteAttributes)>
            attributeLifetime(list, deleteAttributes);
        HANDLE inherited[] = { inputRead.value, outputWrite.value };
        if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, sizeof(inherited), nullptr, nullptr))
        {
            Fail(L"Could not restrict backend handle inheritance.", HRESULT_FROM_WIN32(GetLastError()));
            return;
        }
        STARTUPINFOEXW start{};
        start.StartupInfo.cb = sizeof(start);
        start.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        start.StartupInfo.hStdInput = inputRead.value;
        // One merged pipe continuously drains both streams, including during shutdown.
        start.StartupInfo.hStdOutput = outputWrite.value;
        start.StartupInfo.hStdError = outputWrite.value;
        start.lpAttributeList = list;
        auto command = desktop::QuoteArgument(executable.wstring()) + L" --desktop-managed";
        if (!dataDirectory.empty()) command += L" --data-dir " + desktop::QuoteArgument(dataDirectory);
        PROCESS_INFORMATION process{};
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
                            CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT,
                            nullptr, executable.parent_path().c_str(), &start.StartupInfo, &process))
        {
            Fail(L"Could not start the local backend.", HRESULT_FROM_WIN32(GetLastError()));
            return;
        }
        backend.reset(process.hProcess);
        Handle primaryThread(process.hThread);
        if (!AssignProcessToJobObject(job.value, backend.value))
        {
            const auto error = GetLastError();
            TerminateProcess(backend.value, 1);
            Fail(L"Could not assign the backend to its lifetime job.", HRESULT_FROM_WIN32(error));
            return;
        }
        if (ResumeThread(primaryThread.value) == static_cast<DWORD>(-1))
        {
            Fail(L"Could not resume the backend.", HRESULT_FROM_WIN32(GetLastError()));
            return;
        }
        outputWrite.reset();
        inputRead.reset();
        startedAt = GetTickCount64();
        reader = std::thread([this] { ReadOutput(); });
    }

    void ReadOutput()
    {
        char buffer[4096];
        DWORD count = 0;
        std::string pending;
        while (ReadFile(outputRead.value, buffer, sizeof(buffer), &count, nullptr) && count)
        {
            pending.append(buffer, count);
            {
                std::lock_guard guard(outputMutex);
                diagnostics.append(buffer, count);
                if (diagnostics.size() > 8192) diagnostics.erase(0, diagnostics.size() - 8192);
            }
            std::size_t newline;
            while ((newline = pending.find('\n')) != std::string::npos)
            {
                auto line = pending.substr(0, newline);
                pending.erase(0, newline + 1);
                if (!line.empty() && line.back() == '\r') line.pop_back();
                constexpr std::string_view prefix = "GitHub Team App: ";
                if (line.starts_with(prefix))
                {
                    const auto parsed = desktop::LoopbackOrigin(Utf8(line.substr(prefix.size())));
                    if (parsed)
                    {
                        {
                            std::lock_guard guard(outputMutex);
                            readyOrigin = *parsed;
                        }
                        PostMessageW(window, BackendOutput, 0, 0);
                    }
                }
            }
            if (pending.size() > 8192) pending.erase(0, pending.size() - 8192);
        }
    }

    void BackendReady()
    {
        if (closing || !origin.empty()) return;
        {
            std::lock_guard guard(outputMutex);
            origin = readyOrigin;
        }
        if (!origin.empty()) InitializeWebView();
    }

    void Tick()
    {
        if (failing && !closing) return;
        if (closing)
        {
            if (!backend || WaitForSingleObject(backend.value, 0) == WAIT_OBJECT_0 ||
                GetTickCount64() - closingAt >= 5000)
            {
                job.reset();
                DestroyWindow(window);
            }
            return;
        }
        if (parent && WaitForSingleObject(parent.value, 0) == WAIT_OBJECT_0)
        {
            Close();
            return;
        }
        if (backend && WaitForSingleObject(backend.value, 0) == WAIT_OBJECT_0)
        {
            DWORD code = 1;
            GetExitCodeProcess(backend.value, &code);
            std::string log;
            { std::lock_guard guard(outputMutex); log = diagnostics; }
            Fail(L"The local backend stopped (exit " + std::to_wstring(code) + L").\n" + Utf8(log));
        }
        else if (backend && origin.empty() && GetTickCount64() - startedAt >= 30000)
        {
            Fail(L"The local backend did not start within 30 seconds.");
        }
    }

    void Close()
    {
        if (closing) return;
        closing = true;
        closingAt = GetTickCount64();
        if (controller) controller->Close();
        controller.Reset();
        webView.Reset();
        if (backend && WaitForSingleObject(backend.value, 0) != WAIT_OBJECT_0)
        {
            constexpr char shutdown[] = "shutdown\n";
            DWORD written = 0;
            if (inputWrite && !WriteFile(inputWrite.value, shutdown, sizeof(shutdown) - 1, &written, nullptr))
            {
                OutputDebugStringW(L"GitHub Team App: graceful shutdown pipe failed; lifetime job will stop the backend.\n");
            }
            inputWrite.reset();
            ShowWindow(window, SW_HIDE);
            // The timer bounds graceful shutdown; the job is the crash/timeout fallback.
            if (SetTimer(window, LifetimeTimer, 100, nullptr)) return;
            job.reset();
        }
        DestroyWindow(window);
    }

    void Resize()
    {
        if (!controller) return;
        RECT bounds{};
        GetClientRect(window, &bounds);
        const auto result = controller->put_Bounds(bounds);
        if (FAILED(result)) Fail(L"Could not resize the webview.", result);
    }

    void Focus()
    {
        if (controller) controller->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
    }

    void Reload()
    {
        if (webView)
        {
            const auto result = webView->Reload();
            if (FAILED(result)) Fail(L"Could not reload the dashboard.", result);
        }
    }

private:
    Handle backend, job, outputRead, inputWrite;
    std::thread reader;
    std::mutex outputMutex;
    std::string diagnostics;
    std::wstring readyOrigin;
    ULONGLONG startedAt = 0, closingAt = 0;
    bool closing = false;
    bool failing = false;
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2> webView;

    void OpenExternal(const std::wstring& url)
    {
        if (!desktop::ExternalUrl(url)) return;
        SHELLEXECUTEINFOW info{ sizeof(info) };
        info.fMask = SEE_MASK_FLAG_NO_UI;
        info.hwnd = window;
        info.lpVerb = L"open";
        info.lpFile = url.c_str();
        info.nShow = SW_SHOWNORMAL;
        if (!ShellExecuteExW(&info))
        {
            MessageBoxW(window, L"Windows could not open this link. Check your default browser or GitHub Copilot App installation.",
                        Title, MB_OK | MB_ICONWARNING);
        }
    }

    void InitializeWebView()
    {
        ComString localAppData;
        const auto folderResult = SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_CREATE, nullptr, &localAppData.value);
        if (FAILED(folderResult)) { Fail(L"Could not locate the local webview profile.", folderResult); return; }
        const auto profile = std::filesystem::path(localAppData.value) / L"GitHub" / L"TeamApp" / L"WebView2";
        const auto result = CreateCoreWebView2EnvironmentWithOptions(nullptr, profile.c_str(), nullptr,
            Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
                [this](HRESULT error, ICoreWebView2Environment* environment) -> HRESULT
                {
                    if (closing) return S_OK;
                    if (FAILED(error) || !environment) { Fail(L"Could not initialize WebView2.", FAILED(error) ? error : E_FAIL); return S_OK; }
                    const auto created = environment->CreateCoreWebView2Controller(window,
                        Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
                            [this](HRESULT error, ICoreWebView2Controller* value) -> HRESULT
                            {
                                if (closing) return S_OK;
                                if (FAILED(error) || !value) { Fail(L"Could not create the desktop webview.", FAILED(error) ? error : E_FAIL); return S_OK; }
                                controller = value;
                                const auto result = controller->get_CoreWebView2(&webView);
                                if (FAILED(result)) { Fail(L"Could not access the desktop webview.", result); return S_OK; }
                                ConfigureWebView();
                                return S_OK;
                            }).Get());
                    if (FAILED(created)) Fail(L"Could not start the desktop webview.", created);
                    return S_OK;
                }).Get());
        if (FAILED(result)) Fail(L"Could not create the WebView2 environment.", result);
    }

    void ConfigureWebView()
    {
        ComPtr<ICoreWebView2Settings> settings;
        auto result = webView->get_Settings(&settings);
        if (FAILED(result)) { Fail(L"Could not read webview settings.", result); return; }
        result = settings->put_IsStatusBarEnabled(FALSE);
        if (SUCCEEDED(result)) result = settings->put_AreDevToolsEnabled(FALSE);
        if (SUCCEEDED(result)) result = settings->put_IsBuiltInErrorPageEnabled(FALSE);
        if (FAILED(result)) { Fail(L"Could not configure webview settings.", result); return; }

        EventRegistrationToken token{};
        result = webView->add_NavigationStarting(Callback<ICoreWebView2NavigationStartingEventHandler>(
            [this](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT
            {
                ComString uri;
                const auto read = args->get_Uri(&uri.value);
                if (FAILED(read) || !uri.value) { args->put_Cancel(TRUE); return FAILED(read) ? read : E_FAIL; }
                if (!desktop::SameOrigin(uri.value, origin))
                {
                    args->put_Cancel(TRUE);
                    OpenExternal(uri.value);
                }
                return S_OK;
            }).Get(), &token);
        if (SUCCEEDED(result)) result = webView->add_NewWindowRequested(Callback<ICoreWebView2NewWindowRequestedEventHandler>(
            [this](ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs* args) -> HRESULT
            {
                args->put_Handled(TRUE);
                ComString uri;
                const auto read = args->get_Uri(&uri.value);
                if (FAILED(read) || !uri.value) return FAILED(read) ? read : E_FAIL;
                if (desktop::SameOrigin(uri.value, origin))
                {
                    const auto navigation = webView->Navigate(uri.value);
                    if (FAILED(navigation)) Fail(L"Could not navigate to the dashboard.", navigation);
                }
                else OpenExternal(uri.value);
                return S_OK;
            }).Get(), &token);
        if (SUCCEEDED(result)) result = webView->add_NavigationCompleted(Callback<ICoreWebView2NavigationCompletedEventHandler>(
            [this](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT
            {
                if (closing) return S_OK;
                BOOL success = FALSE;
                args->get_IsSuccess(&success);
                if (!success)
                {
                    COREWEBVIEW2_WEB_ERROR_STATUS status{};
                    args->get_WebErrorStatus(&status);
                    if (status != COREWEBVIEW2_WEB_ERROR_STATUS_OPERATION_CANCELED)
                        Fail(L"Could not load the local dashboard. WebView2 error " + std::to_wstring(status) + L".");
                }
                return S_OK;
            }).Get(), &token);
        if (SUCCEEDED(result)) result = webView->add_ProcessFailed(Callback<ICoreWebView2ProcessFailedEventHandler>(
            [this](ICoreWebView2*, ICoreWebView2ProcessFailedEventArgs*) -> HRESULT
            {
                Fail(L"The embedded browser process stopped. Reopen GitHub Team App to retry.");
                return S_OK;
            }).Get(), &token);
        if (SUCCEEDED(result)) result = webView->add_PermissionRequested(Callback<ICoreWebView2PermissionRequestedEventHandler>(
            [](ICoreWebView2*, ICoreWebView2PermissionRequestedEventArgs* args) -> HRESULT
            {
                return args->put_State(COREWEBVIEW2_PERMISSION_STATE_DENY);
            }).Get(), &token);
        if (FAILED(result)) { Fail(L"Could not register desktop navigation handlers.", result); return; }
        Resize();
        if (closing) return;
        result = controller->put_IsVisible(TRUE);
        if (SUCCEEDED(result)) result = webView->Navigate((origin + L"/").c_str());
        if (FAILED(result)) { Fail(L"Could not open the local dashboard.", result); return; }
        Focus();
    }
};

LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    auto app = reinterpret_cast<Application*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        app = static_cast<Application*>(reinterpret_cast<CREATESTRUCTW*>(lParam)->lpCreateParams);
        app->window = window;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(app));
    }
    if (!app) return DefWindowProcW(window, message, wParam, lParam);
    switch (message)
    {
    case WM_SIZE: app->Resize(); return 0;
    case WM_SETFOCUS: app->Focus(); return 0;
    case WM_CLOSE: app->Close(); return 0;
    case WM_DESTROY: KillTimer(window, LifetimeTimer); PostQuitMessage(app->exitCode); return 0;
    case WM_TIMER: app->Tick(); return 0;
    case BackendOutput: app->BackendReady(); return 0;
    case WM_COMMAND:
        if (LOWORD(wParam) == QuitCommand) { app->Close(); return 0; }
        if (LOWORD(wParam) == ReloadCommand) { app->Reload(); return 0; }
        break;
    case WM_DPICHANGED:
    {
        const auto rect = reinterpret_cast<RECT*>(lParam);
        SetWindowPos(window, nullptr, rect->left, rect->top, rect->right - rect->left,
                     rect->bottom - rect->top, SWP_NOZORDER | SWP_NOACTIVATE);
        return 0;
    }
    case WM_GETMINMAXINFO:
    {
        auto size = reinterpret_cast<MINMAXINFO*>(lParam);
        const auto dpi = GetDpiForWindow(window);
        size->ptMinTrackSize = { MulDiv(640, dpi, 96), MulDiv(480, dpi, 96) };
        return 0;
    }
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

int Run(HINSTANCE instance, int show)
{
    int count = 0;
    auto rawArguments = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!rawArguments) throw std::runtime_error("Could not read the command line.");
    std::vector<std::wstring> arguments(rawArguments + 1, rawArguments + count);
    LocalFree(rawArguments);
    if (arguments == std::vector<std::wstring>{ L"--self-test" })
    {
        RunPolicyTests();
        for (const auto& value : { L"", L"C:\\path with spaces\\", L"a\\\"b", L"hi & start other", L"Unicode-\u03b1" })
        {
            const auto command = L"test.exe " + desktop::QuoteArgument(value);
            int parsedCount = 0;
            auto parsed = CommandLineToArgvW(command.c_str(), &parsedCount);
            const bool valid = parsed && parsedCount == 2 && std::wstring_view(parsed[1]) == value;
            if (parsed) LocalFree(parsed);
            if (!valid) throw std::runtime_error("Windows command-line argument roundtrip failed.");
        }
        return 0;
    }

    Application app;
    if (arguments.size() == 4 && arguments[0] == L"--url" && arguments[2] == L"--parent-pid")
    {
        auto origin = desktop::LoopbackOrigin(arguments[1]);
        const auto pid = desktop::ProcessId(arguments[3]);
        if (!origin || !pid) throw std::runtime_error("Invalid desktop address or parent process ID.");
        app.origin = *origin;
        app.parent.reset(OpenProcess(SYNCHRONIZE, FALSE, *pid));
        if (!app.parent) throw std::runtime_error("The parent backend process is not available.");
    }
    else if (arguments.size() == 2 && arguments[0] == L"--data-dir")
    {
        app.dataDirectory = arguments[1];
        if (app.dataDirectory.empty()) throw std::runtime_error("The data directory must not be empty.");
    }
    else if (!arguments.empty())
    {
        throw std::runtime_error("Invalid desktop arguments. Launch normally, or use dotnet run.");
    }
    const auto identity = SetCurrentProcessExplicitAppUserModelID(AppId);
    if (FAILED(identity)) throw std::runtime_error("Could not set the taskbar application identity.");
    WNDCLASSEXW windowClass{ sizeof(windowClass) };
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = instance;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    windowClass.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(101));
    windowClass.hIconSm = windowClass.hIcon;
    windowClass.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    windowClass.lpszClassName = L"GitHubTeamAppWindow";
    if (!windowClass.hIcon || !RegisterClassExW(&windowClass)) throw std::runtime_error("Could not register the desktop window.");
    const auto window = CreateWindowExW(WS_EX_APPWINDOW, windowClass.lpszClassName, Title, WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 1280, 860, nullptr, nullptr, instance, &app);
    if (!window) throw std::runtime_error("Could not create the desktop window.");
    auto menu = CreateMenu();
    auto file = CreatePopupMenu();
    auto view = CreatePopupMenu();
    if (!menu || !file || !view) throw std::runtime_error("Could not create the application menus.");
    AppendMenuW(file, MF_STRING, QuitCommand, L"E&xit\tAlt+F4");
    AppendMenuW(view, MF_STRING, ReloadCommand, L"&Reload\tCtrl+R");
    AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(file), L"&File");
    AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(view), L"&View");
    SetMenu(window, menu);
    ShowWindow(window, show);
    UpdateWindow(window);
    app.Start();
    MSG message{};
    BOOL result;
    while ((result = GetMessageW(&message, nullptr, 0, 0)) > 0)
    {
        if (message.message == WM_KEYDOWN && message.wParam == 'R' && (GetKeyState(VK_CONTROL) & 0x8000))
        {
            app.Reload();
            continue;
        }
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    if (result == -1) throw std::runtime_error("The desktop message loop failed.");
    return app.exitCode;
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show)
{
    int argumentCount = 0;
    auto arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
    const bool selfTest = arguments && argumentCount == 2 && std::wstring_view(arguments[1]) == L"--self-test";
    if (arguments) LocalFree(arguments);
    const auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized))
    {
        if (!selfTest) MessageBoxW(nullptr, L"Could not initialize Windows COM.", Title, MB_OK | MB_ICONERROR);
        return 1;
    }
    int result = 1;
    try { result = Run(instance, show); }
    catch (const std::exception& error)
    {
        OutputDebugStringW(Utf8(error.what()).c_str());
        if (!selfTest) MessageBoxW(nullptr, Utf8(error.what()).c_str(), Title, MB_OK | MB_ICONERROR);
    }
    CoUninitialize();
    return result;
}
