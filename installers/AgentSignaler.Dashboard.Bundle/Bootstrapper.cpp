#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <msi.h>
#include <shellapi.h>
#include <commctrl.h>
#include <thread>
#include <atomic>
#include <memory>
#include <functional>
#include "BootstrapperEngine.h"
#include "BootstrapperApplication.h"
#include "PrerequisiteDetection.h"

namespace {
constexpr UINT Ready = WM_APP + 1, Finished = WM_APP + 2, Progress = WM_APP + 3, NativeError = WM_APP + 4;
constexpr int AzureCheck = 101, TunnelCheck = 102, LicenseCheck = 103, Install = 104,
    Cancel = 105, AzureLicense = 106, TunnelLicense = 107, Store = 108, Refresh = 109;
constexpr unsigned VerificationFailed = 5102, DetectionFailed = 5104;
struct Bootstrapper {
    PFN_BOOTSTRAPPER_ENGINE_PROC engine{};
    void* context{};
    BOOTSTRAPPER_COMMAND command{};
    std::wstring commandLine;
    HWND window{}, azureLabel{}, tunnelLabel{}, windowsLabel{}, progressLabel{}, progressBar{};
    std::thread ui;
    std::atomic_bool cancelled{ false }, applying{ false };
    std::atomic_uint failure{ 0 };
    std::atomic_uint completion{ UINT_MAX };
    setup::Detection azure, tunnel, windows;
    bool acceptAzure = false, acceptTunnel = false, finished = false, accepted = false;
    bool installAzure = false, installTunnel = false;
    unsigned exitCode = 0;
    std::function<setup::Detection()> detectAzure = setup::DetectAzure;
    std::function<setup::Detection()> detectTunnel = [this] { return setup::DetectTunnels(cancelled); };
    std::function<setup::Detection()> detectWindows = setup::DetectWindowsApp;
    std::function<bool()> supportedPlatform = setup::SupportedPlatform;
    std::function<void()> startUi = [this] { StartUiThread(); };

    bool Interactive() const { return command.display == BOOTSTRAPPER_DISPLAY_FULL; }
    bool Uninstall() const { return command.action == BOOTSTRAPPER_ACTION_UNINSTALL; }
    template<typename Args, typename Results>
    HRESULT Call(BOOTSTRAPPER_ENGINE_MESSAGE message, Args& args, Results& results) {
        args.cbSize = sizeof(args); results.cbSize = sizeof(results);
        return engine(message, &args, &results, context);
    }
    void Numeric(const wchar_t* name, bool value) {
        BAENGINE_SETVARIABLENUMERIC_ARGS args{ sizeof(args), name, value };
        BAENGINE_SETVARIABLENUMERIC_RESULTS results{};
        auto hr = Call(BOOTSTRAPPER_ENGINE_MESSAGE_SETVARIABLENUMERIC, args, results);
        if (FAILED(hr)) throw setup::EngineFailure(hr);
    }
    void Log(const wchar_t* product, unsigned code) {
        // Only fixed product identifiers, pinned product versions, and numeric outcomes.
        std::wstring value = product;
        value += L" version=";
        value += wcscmp(product, L"AzureCli") == 0 ? AzureCliVersion : wcscmp(product, L"DevTunnels") == 0 ? DevTunnelsVersion : DashboardVersion;
        value += L" result=" + std::to_wstring(code);
        BAENGINE_LOG_ARGS args{ sizeof(args), BOOTSTRAPPER_LOG_LEVEL_STANDARD, value.c_str() };
        BAENGINE_LOG_RESULTS results{};
        Call(BOOTSTRAPPER_ENGINE_MESSAGE_LOG, args, results);
    }
    void Fail(unsigned code) {
        unsigned expected = 0; failure.compare_exchange_strong(expected, code);
    }
    void RecordFailure(HRESULT status, unsigned mapped = 0) {
        if (SUCCEEDED(status)) return;
        auto code = setup::NativeExitCode(status);
        if (code == ERROR_INSTALL_USEREXIT || code == ERROR_CANCELLED || code == ERROR_OPERATION_ABORTED)
            cancelled = true;
        else Fail(mapped ? mapped : code);
    }
    void Complete(unsigned code) { completion = code; PostMessageW(window, Finished, code, 0); }
    HRESULT HandleFailure(const setup::NativeFailure& error) noexcept {
        if (error.kind == setup::FailureKind::Engine) RecordFailure(error.status);
        else if (!cancelled) Fail(error.outcome);
        const auto code = setup::Result(failure, cancelled, false);
        completion = code;
        cancelled = true;
        if (window) PostMessageW(window, NativeError, code, 0);
        else { exitCode = code; Quit(); }
        return code == ERROR_INSTALL_USEREXIT ? HRESULT_FROM_WIN32(ERROR_INSTALL_USEREXIT) : error.status;
    }
    void StartUiThread() {
        try { ui = std::thread([this] { Run(); }); }
        catch (const std::system_error& error) {
            if (error.code() == std::errc::resource_unavailable_try_again)
                throw setup::EngineFailure(HRESULT_FROM_WIN32(ERROR_NO_SYSTEM_RESOURCES));
            throw;
        }
    }
    void Quit() {
        BAENGINE_QUIT_ARGS args{ sizeof(args), exitCode }; BAENGINE_QUIT_RESULTS results{};
        Call(BOOTSTRAPPER_ENGINE_MESSAGE_QUIT, args, results);
    }
    void Detect() {
        BAENGINE_DETECT_ARGS args{ sizeof(args), window }; BAENGINE_DETECT_RESULTS results{};
        auto hr = Call(BOOTSTRAPPER_ENGINE_MESSAGE_DETECT, args, results);
        if (FAILED(hr)) Complete(setup::NativeExitCode(hr));
    }
    void RefreshDetection() {
        if (Uninstall() || cancelled) return;
        azure = detectAzure();
        if (cancelled) return;
        tunnel = detectTunnel();
        if (cancelled) return;
        windows = detectWindows();
    }
    void StartDetection() {
        auto result = setup::StartupResult(Interactive(), accepted,
            command.action == BOOTSTRAPPER_ACTION_INSTALL || command.action == BOOTSTRAPPER_ACTION_REPAIR ||
            command.action == BOOTSTRAPPER_ACTION_MODIFY || Uninstall(),
            Uninstall() && command.relationType == BOOTSTRAPPER_RELATION_UPGRADE);
        if (result) Complete(result);
        else if (!supportedPlatform()) Complete(1633);
        else Detect();
    }
    void BeginPlan() {
        if (cancelled) { Complete(setup::Result(failure, true, false)); return; }
        Numeric(L"ACCEPT_PREREQUISITE_LICENSES", accepted);
        installAzure = setup::NeedsInstall(azure, acceptAzure, Uninstall());
        installTunnel = setup::NeedsInstall(tunnel, acceptTunnel, Uninstall());
        if (!Uninstall() && ((acceptAzure && azure.newer && azure.state != setup::State::Installed) ||
            (acceptTunnel && tunnel.newer && tunnel.state != setup::State::Installed))) {
            if (Interactive()) {
                MessageBoxW(window, L"A newer prerequisite is installed but is not supported by this release. Setup will retain it, never downgrade it. Clear its installation checkbox to continue without changing it, or cancel Setup.",
                    L"Newer prerequisite retained", MB_OK | MB_ICONWARNING);
            } else Complete(1638);
            return;
        }
        Numeric(L"InstallAzureCli", installAzure);
        Numeric(L"InstallDevTunnels", installTunnel);
        applying = true;
        for (int id : { AzureCheck, TunnelCheck, LicenseCheck, Install, Refresh, Store, AzureLicense, TunnelLicense })
            EnableWindow(GetDlgItem(window, id), FALSE);
        SetWindowTextW(progressLabel, Uninstall() ? L"Removing Dashboard; prerequisites and connection mappings are retained." : L"Verifying and applying selected packages...");
        BAENGINE_PLAN_ARGS args{ sizeof(args), command.action }; BAENGINE_PLAN_RESULTS results{};
        auto hr = Call(BOOTSTRAPPER_ENGINE_MESSAGE_PLAN, args, results);
        if (FAILED(hr)) Complete(setup::NativeExitCode(hr));
    }
    void ShowStates() {
        std::wstring a = L"Azure CLI " + std::wstring(AzureCliVersion) + L" or later: " + setup::StateText(azure.state);
        std::wstring t = L"Dev Tunnels " + std::wstring(DevTunnelsVersion) + L": " + setup::StateText(tunnel.state);
        std::wstring w = L"Windows App " + std::wstring(WindowsAppMinimumVersion) + L" or later: " + setup::StateText(windows.state);
        SetWindowTextW(azureLabel, a.c_str()); SetWindowTextW(tunnelLabel, t.c_str()); SetWindowTextW(windowsLabel, w.c_str());
        if (!Interactive()) {
            acceptAzure = acceptTunnel = !Uninstall();
            BeginPlan(); return;
        }
        CheckDlgButton(window, AzureCheck, azure.state == setup::State::Installed ? BST_UNCHECKED : BST_CHECKED);
        CheckDlgButton(window, TunnelCheck, tunnel.state == setup::State::Installed ? BST_UNCHECKED : BST_CHECKED);
        for (int id : { AzureCheck, TunnelCheck, LicenseCheck, Install, Refresh, Store, AzureLicense, TunnelLicense })
            EnableWindow(GetDlgItem(window, id), !Uninstall() || id == Install);
        SetWindowTextW(progressLabel, Uninstall() ? L"Only Dashboard will be removed. Shared prerequisites and saved mappings remain." :
            (azure.newer && azure.state != setup::State::Installed) || (tunnel.newer && tunnel.state != setup::State::Installed) ?
            L"A newer unsupported prerequisite will be retained, never downgraded. Clear its installation checkbox to continue without changing it." :
            L"Choose prerequisites. Windows App is available from Microsoft Store; Setup does not install it.");
    }
    void OpenLink(const wchar_t* uri) {
        if (reinterpret_cast<INT_PTR>(ShellExecuteW(window, L"open", uri, nullptr, nullptr, SW_SHOWNORMAL)) <= 32)
            SetWindowTextW(progressLabel, L"Windows could not open the selected Microsoft license or Store page. Check your browser/Store installation and try again.");
    }
    HWND Control(const wchar_t* type, const wchar_t* text, DWORD style, int x, int y, int width, int height, int id = 0) {
        auto control = CreateWindowExW(0, type, text, WS_CHILD | WS_VISIBLE | style, x, y, width, height, window,
            reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), GetModuleHandleW(nullptr), nullptr);
        if (!control) {
            const auto error = GetLastError();
            throw setup::EngineFailure(error ? HRESULT_FROM_WIN32(error) : E_UNEXPECTED);
        }
        SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)), TRUE);
        return control;
    }
    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) noexcept {
        auto self = reinterpret_cast<Bootstrapper*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            self = static_cast<Bootstrapper*>(reinterpret_cast<CREATESTRUCTW*>(lParam)->lpCreateParams);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        if (!self) return DefWindowProcW(hwnd, message, wParam, lParam);
        try {
            switch (message) {
            case Ready: self->ShowStates(); return 0;
            case Progress:
                SendMessageW(self->progressBar, PBM_SETPOS, wParam, 0); return 0;
            case NativeError:
                self->exitCode = static_cast<unsigned>(wParam);
                SetWindowTextW(self->progressLabel, L"Setup stopped because an installer operation failed.");
                if (!self->applying) { self->finished = true; DestroyWindow(hwnd); }
                return 0;
            case Finished: {
                self->finished = true; self->applying = false;
                self->exitCode = static_cast<unsigned>(wParam);
                self->Log(L"Dashboard", self->exitCode);
                std::wstring status = self->exitCode == 0 ? L"Setup completed." : self->exitCode == 3010 ?
                    L"Setup completed. Restart Windows to finish." : self->exitCode == 1602 ? L"Setup cancelled." :
                    self->exitCode == 1638 ? L"A newer prerequisite or concurrent installation was detected. It was retained, not downgraded. Restart Setup and review the prerequisite selections." :
                    L"Setup failed (result " + std::to_wstring(self->exitCode) + L"). No sign-in or cloud operations were performed.";
                SetWindowTextW(self->progressLabel, status.c_str());
                SetWindowTextW(GetDlgItem(hwnd, Cancel), L"Close");
                EnableWindow(GetDlgItem(hwnd, Install), FALSE);
                if (!self->Interactive()) DestroyWindow(hwnd);
                return 0;
            }
            case WM_COMMAND:
                switch (LOWORD(wParam)) {
                case Install:
                    self->acceptAzure = !self->Uninstall() && IsDlgButtonChecked(hwnd, AzureCheck) == BST_CHECKED;
                    self->acceptTunnel = !self->Uninstall() && IsDlgButtonChecked(hwnd, TunnelCheck) == BST_CHECKED;
                    self->accepted = IsDlgButtonChecked(hwnd, LicenseCheck) == BST_CHECKED;
                    if (!self->Uninstall() && (self->acceptAzure || self->acceptTunnel) && !self->accepted) {
                        MessageBoxW(hwnd, L"Read the prerequisite licenses and check the acceptance box before continuing.", L"License acceptance required", MB_OK | MB_ICONWARNING);
                        return 0;
                    }
                    if (!self->Uninstall() && ((!self->acceptAzure && self->azure.state != setup::State::Installed) ||
                        (!self->acceptTunnel && self->tunnel.state != setup::State::Installed)) &&
                        MessageBoxW(hwnd, L"Continue without the declined prerequisite(s)? Dev Box connections or Internet sharing may be unavailable. Setup will not sign in or install declined packages.",
                            L"Continue without prerequisites", MB_OKCANCEL | MB_ICONWARNING) != IDOK) return 0;
                    self->BeginPlan(); return 0;
                case Refresh:
                    EnableWindow(GetDlgItem(hwnd, Install), FALSE);
                    EnableWindow(GetDlgItem(hwnd, Refresh), FALSE);
                    SetWindowTextW(self->progressLabel, L"Checking installed prerequisites...");
                    self->Detect(); return 0;
                case AzureLicense: self->OpenLink(AzureCliLicenseUrl); return 0;
                case TunnelLicense: self->OpenLink(DevTunnelsLicenseUrl); return 0;
                case Store: self->OpenLink(WindowsAppStoreUri); return 0;
                case Cancel: SendMessageW(hwnd, WM_CLOSE, 0, 0); return 0;
                }
                break;
            case WM_CLOSE:
                if (self->applying) {
                    self->cancelled = true;
                    SetWindowTextW(self->progressLabel, L"Cancelling. Waiting for the active package to finish safely...");
                } else {
                    if (!self->finished) self->exitCode = 1602;
                    DestroyWindow(hwnd);
                }
                return 0;
            case WM_DESTROY: PostQuitMessage(0); return 0;
            }
        } catch (const setup::NativeFailure& error) {
            self->HandleFailure(error); return 0;
        } catch (const std::bad_alloc&) {
            self->HandleFailure(setup::EngineFailure(E_OUTOFMEMORY)); return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
    void Run() noexcept {
        try { RunCore(); }
        catch (const setup::NativeFailure& error) { HandleFailure(error); FinishFailedUi(); }
        catch (const std::bad_alloc&) { HandleFailure(setup::EngineFailure(E_OUTOFMEMORY)); FinishFailedUi(); }
    }
    void FinishFailedUi() noexcept {
        if (window) {
            DestroyWindow(window);
            window = nullptr;
            exitCode = completion;
            Quit();
        }
    }
    void RunCore() {
        const auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        if (FAILED(initialized)) throw setup::EngineFailure(initialized);
        struct Apartment { ~Apartment() { CoUninitialize(); } } apartment;
        INITCOMMONCONTROLSEX controls{ sizeof(controls), ICC_PROGRESS_CLASS };
        InitCommonControlsEx(&controls);
        WNDCLASSW type{}; type.lpfnWndProc = WindowProc; type.hInstance = GetModuleHandleW(nullptr);
        type.lpszClassName = L"AgentSignalerBootstrapper"; type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        type.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        RegisterClassW(&type);
        window = CreateWindowExW(WS_EX_DLGMODALFRAME, type.lpszClassName, L"Agent Signaler Dashboard Setup",
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX, CW_USEDEFAULT, CW_USEDEFAULT, 690, 510, nullptr, nullptr, type.hInstance, this);
        if (!window) {
            const auto error = GetLastError();
            throw setup::EngineFailure(error ? HRESULT_FROM_WIN32(error) : E_UNEXPECTED);
        }
        azureLabel = Control(L"STATIC", L"Checking Azure CLI...", 0, 20, 20, 640, 25);
        Control(L"BUTTON", L"Install / update Azure CLI (shared, requires elevation)", BS_AUTOCHECKBOX | WS_TABSTOP, 20, 50, 620, 25, AzureCheck);
        Control(L"BUTTON", L"Azure CLI license", BS_PUSHBUTTON | WS_TABSTOP, 20, 80, 200, 28, AzureLicense);
        tunnelLabel = Control(L"STATIC", L"Checking Dev Tunnels...", 0, 20, 120, 640, 25);
        Control(L"BUTTON", L"Install / update Dev Tunnels CLI for this Windows user", BS_AUTOCHECKBOX | WS_TABSTOP, 20, 150, 620, 25, TunnelCheck);
        Control(L"BUTTON", L"Dev Tunnels license", BS_PUSHBUTTON | WS_TABSTOP, 20, 180, 200, 28, TunnelLicense);
        windowsLabel = Control(L"STATIC", L"Checking Windows App...", 0, 20, 220, 640, 25);
        Control(L"BUTTON", L"Open Microsoft Store", BS_PUSHBUTTON | WS_TABSTOP, 20, 250, 200, 28, Store);
        Control(L"BUTTON", L"Check again", BS_PUSHBUTTON | WS_TABSTOP, 240, 250, 150, 28, Refresh);
        Control(L"BUTTON", L"I accept the licenses for the selected prerequisites (this run only)", BS_AUTOCHECKBOX | WS_TABSTOP, 20, 290, 640, 25, LicenseCheck);
        progressLabel = Control(L"STATIC", L"Checking installed prerequisites...", 0, 20, 325, 640, 50);
        progressBar = Control(PROGRESS_CLASSW, L"", 0, 20, 380, 640, 20);
        Control(L"BUTTON", Uninstall() ? L"Uninstall Dashboard" : command.action == BOOTSTRAPPER_ACTION_REPAIR ? L"Repair" : L"Install", BS_DEFPUSHBUTTON | WS_TABSTOP, 350, 415, 170, 32, Install);
        Control(L"BUTTON", L"Cancel", BS_PUSHBUTTON | WS_TABSTOP, 535, 415, 120, 32, Cancel);
        for (int id : { AzureCheck, TunnelCheck, LicenseCheck, Install, Refresh, Store, AzureLicense, TunnelLicense })
            EnableWindow(GetDlgItem(window, id), FALSE);
        if (command.display != BOOTSTRAPPER_DISPLAY_NONE) ShowWindow(window, SW_SHOWNORMAL);
        BAENGINE_CLOSESPLASHSCREEN_ARGS closeArgs{}; BAENGINE_CLOSESPLASHSCREEN_RESULTS closeResults{};
        Call(BOOTSTRAPPER_ENGINE_MESSAGE_CLOSESPLASHSCREEN, closeArgs, closeResults);
        StartDetection();
        MSG message{};
        while (GetMessageW(&message, nullptr, 0, 0) > 0) {
            if (!IsDialogMessageW(window, &message)) { TranslateMessage(&message); DispatchMessageW(&message); }
        }
        Quit();
    }
    HRESULT OnMessage(BOOTSTRAPPER_APPLICATION_MESSAGE message, const void* args, void* results) {
        switch (message) {
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONSTARTUP:
            startUi(); break;
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONDETECTCOMPLETE: {
            auto event = static_cast<const BA_ONDETECTCOMPLETE_ARGS*>(args);
            if (FAILED(event->hrStatus)) { Complete(setup::NativeExitCode(event->hrStatus)); break; }
            RefreshDetection();
            if (cancelled) { Complete(setup::Result(failure, true, false)); break; }
            PostMessageW(window, Ready, 0, 0); break;
        }
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONPLANPACKAGEBEGIN: {
            auto event = static_cast<const BA_ONPLANPACKAGEBEGIN_ARGS*>(args);
            auto result = static_cast<BA_ONPLANPACKAGEBEGIN_RESULTS*>(results);
            if (wcscmp(event->wzPackageId, L"Dashboard") != 0) {
                auto install = wcscmp(event->wzPackageId, L"AzureCli") == 0 ? installAzure : installTunnel;
                result->requestedState = !install ? BOOTSTRAPPER_REQUEST_STATE_NONE :
                    event->state == BOOTSTRAPPER_PACKAGE_STATE_PRESENT ? BOOTSTRAPPER_REQUEST_STATE_REPAIR : BOOTSTRAPPER_REQUEST_STATE_PRESENT;
                result->requestedCacheType = install ? BOOTSTRAPPER_CACHE_TYPE_KEEP : BOOTSTRAPPER_CACHE_TYPE_REMOVE;
            }
            result->fCancel = cancelled; break;
        }
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONPLANCOMPATIBLEMSIPACKAGEBEGIN:
            if (wcscmp(static_cast<const BA_ONPLANCOMPATIBLEMSIPACKAGEBEGIN_ARGS*>(args)->wzPackageId, L"Dashboard") != 0)
                static_cast<BA_ONPLANCOMPATIBLEMSIPACKAGEBEGIN_RESULTS*>(results)->fRequestRemove = FALSE;
            break;
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONPLANCOMPLETE: {
            auto event = static_cast<const BA_ONPLANCOMPLETE_ARGS*>(args);
            if (FAILED(event->hrStatus)) { Complete(setup::NativeExitCode(event->hrStatus)); break; }
            if (cancelled) { Complete(setup::Result(failure, true, false)); break; }
            BAENGINE_APPLY_ARGS apply{ sizeof(apply), window }; BAENGINE_APPLY_RESULTS result{};
            auto hr = Call(BOOTSTRAPPER_ENGINE_MESSAGE_APPLY, apply, result);
            if (FAILED(hr)) Complete(setup::NativeExitCode(hr));
            break;
        }
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONEXECUTEPACKAGEBEGIN: {
            auto event = static_cast<const BA_ONEXECUTEPACKAGEBEGIN_ARGS*>(args);
            auto result = static_cast<BA_ONEXECUTEPACKAGEBEGIN_RESULTS*>(results);
            if (!event->fExecute) break; // Do not interfere with Burn's rollback.
            if (cancelled) { result->fCancel = TRUE; break; }
            if (!Uninstall()) {
                // Re-detect immediately before each MSI. A concurrent upgrade must never be overwritten.
                if (wcscmp(event->wzPackageId, L"AzureCli") == 0) {
                    auto now = detectAzure();
                    if (cancelled) { result->fCancel = TRUE; break; }
                    if (now.newer || now.state == setup::State::Installed) Fail(1638);
                } else if (wcscmp(event->wzPackageId, L"DevTunnels") == 0) {
                    auto now = detectTunnel();
                    if (cancelled) { result->fCancel = TRUE; break; }
                    if (now.newer || now.state == setup::State::Installed) Fail(1638);
                } else {
                    if (acceptAzure) {
                        auto now = detectAzure();
                        if (cancelled) { result->fCancel = TRUE; break; }
                        if (now.state != setup::State::Installed) Fail(DetectionFailed);
                    }
                    if (!failure && acceptTunnel) {
                        auto now = detectTunnel();
                        if (cancelled) { result->fCancel = TRUE; break; }
                        if (now.state != setup::State::Installed) Fail(DetectionFailed);
                    }
                }
            }
            result->fCancel = cancelled || failure != 0; break;
        }
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONEXECUTEPACKAGECOMPLETE: {
            auto event = static_cast<const BA_ONEXECUTEPACKAGECOMPLETE_ARGS*>(args);
            RecordFailure(event->hrStatus);
            Log(event->wzPackageId, setup::NativeExitCode(event->hrStatus));
            static_cast<BA_ONEXECUTEPACKAGECOMPLETE_RESULTS*>(results)->action = BOOTSTRAPPER_EXECUTEPACKAGECOMPLETE_ACTION_NONE;
            break;
        }
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONEXECUTEPROGRESS:
            PostMessageW(window, Progress, static_cast<const BA_ONEXECUTEPROGRESS_ARGS*>(args)->dwOverallPercentage, 0);
            static_cast<BA_ONEXECUTEPROGRESS_RESULTS*>(results)->fCancel = cancelled; break;
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONCACHEACQUIREPROGRESS:
            static_cast<BA_ONCACHEACQUIREPROGRESS_RESULTS*>(results)->fCancel = cancelled; break;
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONCACHEVERIFYPROGRESS:
            static_cast<BA_ONCACHEVERIFYPROGRESS_RESULTS*>(results)->fCancel = cancelled; break;
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONCACHEACQUIRECOMPLETE:
            RecordFailure(static_cast<const BA_ONCACHEACQUIRECOMPLETE_ARGS*>(args)->hrStatus, 5101);
            static_cast<BA_ONCACHEACQUIRECOMPLETE_RESULTS*>(results)->action = BOOTSTRAPPER_CACHEACQUIRECOMPLETE_ACTION_NONE; break;
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONCACHEVERIFYCOMPLETE:
            RecordFailure(static_cast<const BA_ONCACHEVERIFYCOMPLETE_ARGS*>(args)->hrStatus, VerificationFailed);
            static_cast<BA_ONCACHEVERIFYCOMPLETE_RESULTS*>(results)->action = BOOTSTRAPPER_CACHEVERIFYCOMPLETE_ACTION_NONE; break;
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONERROR: {
            auto event = static_cast<const BA_ONERROR_ARGS*>(args);
            if (event->errorType == BOOTSTRAPPER_ERROR_TYPE_WINDOWS_INSTALLER || event->errorType == BOOTSTRAPPER_ERROR_TYPE_EXE_PACKAGE ||
                event->errorType == BOOTSTRAPPER_ERROR_TYPE_ELEVATE)
                RecordFailure(HRESULT_FROM_WIN32(event->dwCode));
            else if (event->errorType == BOOTSTRAPPER_ERROR_TYPE_HTTP_AUTH_SERVER || event->errorType == BOOTSTRAPPER_ERROR_TYPE_HTTP_AUTH_PROXY)
                Fail(5101);
            static_cast<BA_ONERROR_RESULTS*>(results)->nResult = IDCANCEL;
            break;
        }
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONEXECUTEFILESINUSE:
            static_cast<BA_ONEXECUTEFILESINUSE_RESULTS*>(results)->nResult = IDCANCEL; break;
        case BOOTSTRAPPER_APPLICATION_MESSAGE_ONAPPLYCOMPLETE: {
            auto event = static_cast<const BA_ONAPPLYCOMPLETE_ARGS*>(args);
            RecordFailure(event->hrStatus);
            Complete(setup::Result(failure, cancelled, event->restart != BOOTSTRAPPER_APPLY_RESTART_NONE));
            static_cast<BA_ONAPPLYCOMPLETE_RESULTS*>(results)->action = BOOTSTRAPPER_APPLYCOMPLETE_ACTION_NONE;
            break;
        }
        default: break;
        }
        return S_OK;
    }
};
std::unique_ptr<Bootstrapper> application;
HRESULT WINAPI Callback(BOOTSTRAPPER_APPLICATION_MESSAGE message, const LPVOID args, LPVOID results, LPVOID context) noexcept {
    auto self = static_cast<Bootstrapper*>(context);
    try { return self->OnMessage(message, args, results); }
    catch (const setup::NativeFailure& error) { return self->HandleFailure(error); }
    catch (const std::bad_alloc&) { return self->HandleFailure(setup::EngineFailure(E_OUTOFMEMORY)); }
}
struct LocalMemoryDeleter {
    void operator()(void* memory) const noexcept { LocalFree(memory); }
};
}
extern "C" __declspec(dllexport) HRESULT WINAPI BootstrapperApplicationCreate(
    const BOOTSTRAPPER_CREATE_ARGS* args, BOOTSTRAPPER_CREATE_RESULTS* results) noexcept {
    if (!args || args->cbSize < sizeof(*args) || !args->pCommand || !args->pfnBootstrapperEngineProc ||
        !results || results->cbSize < sizeof(*results)) return E_INVALIDARG;
    try {
        auto created = std::make_unique<Bootstrapper>();
        created->engine = args->pfnBootstrapperEngineProc;
        created->context = args->pvBootstrapperEngineProcContext;
        created->command = *args->pCommand;
        created->commandLine = args->pCommand->wzCommandLine ? args->pCommand->wzCommandLine : L"";
        int count{};
        std::unique_ptr<wchar_t*, LocalMemoryDeleter> argv(CommandLineToArgvW((L"setup " + created->commandLine).c_str(), &count));
        if (!argv) {
            const auto error = GetLastError();
            return error ? HRESULT_FROM_WIN32(error) : E_UNEXPECTED;
        }
        std::vector<std::wstring> arguments;
        for (int i = 1; i < count; i++) arguments.emplace_back(argv.get()[i]);
        created->accepted = setup::LicenseAccepted(arguments);
        application = std::move(created);
        results->pfnBootstrapperApplicationProc = Callback;
        results->pvBootstrapperApplicationProcContext = application.get();
        return S_OK;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
}
extern "C" __declspec(dllexport) void WINAPI BootstrapperApplicationDestroy(
    const BOOTSTRAPPER_DESTROY_ARGS*, BOOTSTRAPPER_DESTROY_RESULTS*) noexcept {
    if (application && application->ui.joinable()) application->ui.join();
    application.reset();
}
