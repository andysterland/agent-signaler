#include "Bootstrapper.cpp"
#include <iostream>

int main() {
    unsigned tests = 0;
    auto check = [&](bool condition) { ++tests; if (!condition) throw std::runtime_error("Bootstrapper policy test failed"); };
    using namespace setup;
    check(!LicenseAccepted({}));
    check(LicenseAccepted({ L"ACCEPT_PREREQUISITE_LICENSES=1" }));
    for (auto bad : { L"ACCEPT_PREREQUISITE_LICENSES=0", L"ACCEPT_PREREQUISITE_LICENSES=01",
        L"ACCEPT_PREREQUISITE_LICENSES=true", L"ACCEPT_PREREQUISITE_LICENSES=1extra", L"xACCEPT_PREREQUISITE_LICENSES=1" })
        check(!LicenseAccepted({ bad }));
    check(!LicenseAccepted({ L"ACCEPT_PREREQUISITE_LICENSES=1", L"ACCEPT_PREREQUISITE_LICENSES=1" }));
    for (auto state : { State::Missing, State::UpdateRequired, State::Installed })
        for (bool newer : { false, true })
            for (bool selected : { false, true })
                for (bool uninstall : { false, true })
                    check(NeedsInstall({ state, newer }, selected, uninstall) == (state != State::Installed && !newer && selected && !uninstall));
    check(ParseVersion(L"2.90.0").value == ParseVersion(L"2.90.0.0").value);
    check(ParseVersion(L"2.91.0").value > ParseVersion(L"2.90.0").value);
    check(ParseVersion(L"1.0.2031").value > ParseVersion(L"1.0.2030.64658").value);
    for (auto bad : { L"", L"1", L"1.0", L"1.0.0.", L"1.0.0.0.0", L"1.0.-1", L"1.0.65536", L"1.0.0evil" }) {
        check(!ParseVersion(bad).valid);
    }
    check(VersionOutputValid("Tunnel CLI version: 1.0.2030+fc9273aa0f\r\n", "", "1.0.2030+fc9273aa0f", 0));
    check(!VersionOutputValid("", "Tunnel CLI version: 1.0.2030+fc9273aa0f", "1.0.2030+fc9273aa0f", 0));
    check(!VersionOutputValid("Tunnel CLI version: 1.0.2030+fc9273aa0f", "CLI version: other", "1.0.2030+fc9273aa0f", 0));
    check(!VersionOutputValid("Tunnel CLI version: 1.0.2030+fc9273aa0f", "", "1.0.2030+fc9273aa0f", 1));
    check(!VersionOutputValid(std::string(65537, 'x'), "", "1.0.2030+fc9273aa0f", 0));
    check(Result(0, false, false) == 0);
    check(Result(0, false, true) == 3010);
    check(Result(0, true, true) == 1602);
    check(Result(5102, true, true) == 5102);
    for (bool supported : { false, true })
        for (bool interactive : { false, true })
            for (bool license : { false, true })
                check(StartupResult(interactive, license, supported) == (!supported ? 87u : !interactive && !license ? 5100u : 0u));
    unsigned engineCalls = 0;
    auto fakeEngine = [](BOOTSTRAPPER_ENGINE_MESSAGE message, const LPVOID, LPVOID, LPVOID context) -> HRESULT {
        if (message == BOOTSTRAPPER_ENGINE_MESSAGE_APPLY || message == BOOTSTRAPPER_ENGINE_MESSAGE_PLAN)
            throw std::runtime_error("Tests must not apply or plan an installer");
        ++*static_cast<unsigned*>(context); return S_OK;
    };
    for (auto state : { BOOTSTRAPPER_PACKAGE_STATE_ABSENT, BOOTSTRAPPER_PACKAGE_STATE_PRESENT })
        for (bool requested : { false, true }) {
            Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
            ba.installAzure = requested;
            BA_ONPLANPACKAGEBEGIN_ARGS args{}; args.wzPackageId = L"AzureCli"; args.state = state;
            BA_ONPLANPACKAGEBEGIN_RESULTS result{};
            ba.OnMessage(BOOTSTRAPPER_APPLICATION_MESSAGE_ONPLANPACKAGEBEGIN, &args, &result);
            check(result.requestedState == (!requested ? BOOTSTRAPPER_REQUEST_STATE_NONE :
                state == BOOTSTRAPPER_PACKAGE_STATE_PRESENT ? BOOTSTRAPPER_REQUEST_STATE_REPAIR : BOOTSTRAPPER_REQUEST_STATE_PRESENT));
        }
    for (bool acceptedAzure : { false, true })
        for (bool acceptedTunnel : { false, true })
            for (bool azureInstalled : { false, true })
                for (bool tunnelInstalled : { false, true }) {
                    Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
                    ba.acceptAzure = acceptedAzure; ba.acceptTunnel = acceptedTunnel;
                    ba.detectAzure = [=] { return Detection{ azureInstalled ? State::Installed : State::Missing }; };
                    ba.detectTunnel = [=] { return Detection{ tunnelInstalled ? State::Installed : State::UpdateRequired }; };
                    BA_ONEXECUTEPACKAGEBEGIN_ARGS args{}; args.wzPackageId = L"Dashboard"; args.fExecute = TRUE;
                    BA_ONEXECUTEPACKAGEBEGIN_RESULTS result{};
                    ba.OnMessage(BOOTSTRAPPER_APPLICATION_MESSAGE_ONEXECUTEPACKAGEBEGIN, &args, &result);
                    check(!!result.fCancel == ((acceptedAzure && !azureInstalled) || (acceptedTunnel && !tunnelInstalled)));
                }
    for (auto product : { L"AzureCli", L"DevTunnels" }) {
        Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
        ba.detectAzure = ba.detectTunnel = [] { return Detection{ State::Installed, true }; };
        BA_ONEXECUTEPACKAGEBEGIN_ARGS args{}; args.wzPackageId = product; args.fExecute = TRUE;
        BA_ONEXECUTEPACKAGEBEGIN_RESULTS result{};
        ba.OnMessage(BOOTSTRAPPER_APPLICATION_MESSAGE_ONEXECUTEPACKAGEBEGIN, &args, &result);
        check(result.fCancel && ba.failure == 1638);
        ba.failure = 0; args.fExecute = FALSE; result.fCancel = FALSE;
        ba.OnMessage(BOOTSTRAPPER_APPLICATION_MESSAGE_ONEXECUTEPACKAGEBEGIN, &args, &result);
        check(!result.fCancel && ba.failure == 0);
        ba.command.action = BOOTSTRAPPER_ACTION_UNINSTALL; args.fExecute = TRUE;
        ba.OnMessage(BOOTSTRAPPER_APPLICATION_MESSAGE_ONEXECUTEPACKAGEBEGIN, &args, &result);
        check(!result.fCancel);
    }
    check(engineCalls == 0);
    for (bool azureProduct : { false, true }) {
        Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
        ba.command.display = BOOTSTRAPPER_DISPLAY_NONE;
        ba.accepted = true;
        ba.acceptAzure = azureProduct; ba.acceptTunnel = !azureProduct;
        (azureProduct ? ba.azure : ba.tunnel) = Detection{ State::UpdateRequired, true };
        ba.BeginPlan();
        check(ba.completion == 1638);
        check(!ba.installAzure && !ba.installTunnel);
        check(!ba.applying);
    }
    for (auto code : { 0u, 1602u, 1223u, 995u, 1603u }) {
        Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
        BA_ONCACHEACQUIRECOMPLETE_ARGS acquire{}; acquire.hrStatus = HRESULT_FROM_WIN32(code);
        BA_ONCACHEACQUIRECOMPLETE_RESULTS acquireResult{};
        ba.OnMessage(BOOTSTRAPPER_APPLICATION_MESSAGE_ONCACHEACQUIRECOMPLETE, &acquire, &acquireResult);
        check(ba.failure == (code == 1603 ? 5101u : 0u));
        check(ba.cancelled == (code == 1602 || code == 1223 || code == 995));
        BA_ONAPPLYCOMPLETE_ARGS done{}; done.hrStatus = acquire.hrStatus; done.restart = BOOTSTRAPPER_APPLY_RESTART_REQUIRED;
        BA_ONAPPLYCOMPLETE_RESULTS doneResult{};
        ba.OnMessage(BOOTSTRAPPER_APPLICATION_MESSAGE_ONAPPLYCOMPLETE, &done, &doneResult);
        check(ba.completion == (code == 1603 ? 5101u : code ? 1602u : 3010u));
    }
    {
        Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
        BA_ONCACHEVERIFYCOMPLETE_ARGS verify{}; verify.hrStatus = E_FAIL;
        BA_ONCACHEVERIFYCOMPLETE_RESULTS result{};
        ba.OnMessage(BOOTSTRAPPER_APPLICATION_MESSAGE_ONCACHEVERIFYCOMPLETE, &verify, &result);
        check(ba.failure == 5102);
    }
    for (auto display : { BOOTSTRAPPER_DISPLAY_NONE, BOOTSTRAPPER_DISPLAY_PASSIVE })
        for (auto action : { BOOTSTRAPPER_ACTION_INSTALL, BOOTSTRAPPER_ACTION_REPAIR, BOOTSTRAPPER_ACTION_MODIFY,
            BOOTSTRAPPER_ACTION_UNINSTALL, BOOTSTRAPPER_ACTION_LAYOUT, BOOTSTRAPPER_ACTION_CACHE })
            for (auto relation : { BOOTSTRAPPER_RELATION_NONE, BOOTSTRAPPER_RELATION_UPGRADE, BOOTSTRAPPER_RELATION_DETECT,
                BOOTSTRAPPER_RELATION_ADDON, BOOTSTRAPPER_RELATION_PATCH, BOOTSTRAPPER_RELATION_DEPENDENT_ADDON,
                BOOTSTRAPPER_RELATION_DEPENDENT_PATCH, BOOTSTRAPPER_RELATION_UPDATE, BOOTSTRAPPER_RELATION_CHAIN_PACKAGE })
                for (bool license : { false, true }) {
                    Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
                    ba.command.display = display; ba.command.action = action; ba.command.relationType = relation;
                    ba.accepted = license;
                    ba.supportedPlatform = [] { return true; };
                    ba.startUi = [&] { ba.StartDetection(); };
                    auto before = engineCalls;
                    check(Callback(BOOTSTRAPPER_APPLICATION_MESSAGE_ONSTARTUP, nullptr, nullptr, &ba) == S_OK);
                    bool supported = action != BOOTSTRAPPER_ACTION_LAYOUT && action != BOOTSTRAPPER_ACTION_CACHE;
                    bool exempt = action == BOOTSTRAPPER_ACTION_UNINSTALL && relation == BOOTSTRAPPER_RELATION_UPGRADE;
                    auto expected = !supported ? 87u : !license && !exempt ? 5100u : UINT_MAX;
                    check(ba.completion == expected);
                    check(engineCalls - before == (expected == UINT_MAX ? 1u : 0u));
                }
    {
        Bootstrapper ba;
        unsigned plans = 0;
        ba.context = &plans;
        ba.engine = [](BOOTSTRAPPER_ENGINE_MESSAGE message, const LPVOID args, LPVOID, LPVOID context) -> HRESULT {
            if (message == BOOTSTRAPPER_ENGINE_MESSAGE_APPLY) throw std::runtime_error("Unexpected apply");
            if (message == BOOTSTRAPPER_ENGINE_MESSAGE_PLAN) {
                if (static_cast<BAENGINE_PLAN_ARGS*>(args)->action != BOOTSTRAPPER_ACTION_UNINSTALL)
                    throw std::runtime_error("Consent exemption must not plan an installation");
                ++*static_cast<unsigned*>(context);
            }
            return S_OK;
        };
        ba.command.display = BOOTSTRAPPER_DISPLAY_NONE;
        ba.command.action = BOOTSTRAPPER_ACTION_UNINSTALL;
        ba.command.relationType = BOOTSTRAPPER_RELATION_UPGRADE;
        ba.detectAzure = ba.detectTunnel = ba.detectWindows = []() -> Detection { throw std::runtime_error("Uninstall must not validate prerequisites"); };
        BA_ONDETECTCOMPLETE_ARGS detect{}; detect.hrStatus = S_OK;
        check(Callback(BOOTSTRAPPER_APPLICATION_MESSAGE_ONDETECTCOMPLETE, &detect, nullptr, &ba) == S_OK);
        ba.ShowStates();
        check(plans == 1 && !ba.accepted && !ba.installAzure && !ba.installTunnel);
        for (auto product : { L"AzureCli", L"DevTunnels" }) {
            BA_ONPLANPACKAGEBEGIN_ARGS args{}; args.wzPackageId = product; args.state = BOOTSTRAPPER_PACKAGE_STATE_PRESENT;
            BA_ONPLANPACKAGEBEGIN_RESULTS result{};
            check(Callback(BOOTSTRAPPER_APPLICATION_MESSAGE_ONPLANPACKAGEBEGIN, &args, &result, &ba) == S_OK);
            check(result.requestedState == BOOTSTRAPPER_REQUEST_STATE_NONE);
        }
    }
    for (auto product : { L"AzureCli", L"DevTunnels", L"Dashboard" })
        for (bool cancelAzure : { false, true }) {
            if ((wcscmp(product, L"AzureCli") == 0 && !cancelAzure) || (wcscmp(product, L"DevTunnels") == 0 && cancelAzure)) continue;
            for (bool cancelBefore : { false, true })
                for (bool newer : { false, true }) {
                    Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
                    ba.command.action = BOOTSTRAPPER_ACTION_INSTALL;
                    ba.acceptAzure = ba.acceptTunnel = true;
                    ba.cancelled = cancelBefore;
                    unsigned detections = 0;
                    ba.detectAzure = [&] {
                        ++detections;
                        if (cancelAzure) ba.cancelled = true;
                        return Detection{ cancelAzure ? State::UpdateRequired : State::Installed, cancelAzure && newer };
                    };
                    ba.detectTunnel = [&] {
                        ++detections; ba.cancelled = true;
                        return Detection{ State::UpdateRequired, newer };
                    };
                    BA_ONEXECUTEPACKAGEBEGIN_ARGS args{}; args.wzPackageId = product; args.fExecute = TRUE;
                    BA_ONEXECUTEPACKAGEBEGIN_RESULTS result{};
                    check(Callback(BOOTSTRAPPER_APPLICATION_MESSAGE_ONEXECUTEPACKAGEBEGIN, &args, &result, &ba) == S_OK);
                    check(result.fCancel && ba.cancelled && ba.failure == 0);
                    check(!cancelBefore || detections == 0);
                    BA_ONAPPLYCOMPLETE_ARGS done{}; done.hrStatus = HRESULT_FROM_WIN32(ERROR_INSTALL_USEREXIT);
                    BA_ONAPPLYCOMPLETE_RESULTS doneResult{};
                    check(Callback(BOOTSTRAPPER_APPLICATION_MESSAGE_ONAPPLYCOMPLETE, &done, &doneResult, &ba) == S_OK);
                    check(ba.completion == 1602);
                }
        }
    {
        Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
        ba.detectAzure = [] { return Detection{ State::Installed }; };
        ba.detectTunnel = [&] { ba.cancelled = true; return Detection{ State::UpdateRequired }; };
        ba.detectWindows = []() -> Detection { throw std::runtime_error("Detection must stop after cancellation"); };
        BA_ONDETECTCOMPLETE_ARGS args{}; args.hrStatus = S_OK;
        check(Callback(BOOTSTRAPPER_APPLICATION_MESSAGE_ONDETECTCOMPLETE, &args, nullptr, &ba) == S_OK);
        check(ba.completion == 1602 && ba.failure == 0);
        ba.failure = 5102;
        BA_ONAPPLYCOMPLETE_ARGS done{}; done.hrStatus = HRESULT_FROM_WIN32(ERROR_INSTALL_USEREXIT);
        BA_ONAPPLYCOMPLETE_RESULTS result{};
        check(Callback(BOOTSTRAPPER_APPLICATION_MESSAGE_ONAPPLYCOMPLETE, &done, &result, &ba) == S_OK);
        check(ba.completion == 5102);
    }
    check(noexcept(ParseVersion(L"malformed")));
    check(ParseVersion(L"65535.65535.65535.65535").valid &&
        ParseVersion(L"65535.65535.65535.65535").value == UINT64_MAX);
    check(InstalledProductVersion(L"2.91.0") > AzureMinimum.value);
    for (auto malformed : { L"", L"not-a-version", L"2.99", L"2.99.0garbage", L"2.99.-1", L"2.99.65536",
        L"2.99.0.", L"2.99.0.0.0", L"99999999999999999999999999.0.0", L"2.\uFF19\uFF19.0" }) {
        Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
        ba.detectAzure = [=] {
            InstalledProductVersion(malformed);
            return Detection{ State::Installed };
        };
        ba.detectTunnel = []() -> Detection { throw std::logic_error("Malformed MSI metadata must stop detection"); };
        BA_ONDETECTCOMPLETE_ARGS args{}; args.hrStatus = S_OK;
        check(Callback(BOOTSTRAPPER_APPLICATION_MESSAGE_ONDETECTCOMPLETE, &args, nullptr, &ba) == HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
        check(ba.failure == 5104 && ba.completion == 5104);
        check(!ba.installAzure && !ba.installTunnel && !ba.applying);
    }
    struct VariableFailureScenario {
        const wchar_t* variable;
        HRESULT status;
        unsigned quitCode = UINT_MAX;
    };
    for (auto variable : { L"ACCEPT_PREREQUISITE_LICENSES", L"InstallAzureCli", L"InstallDevTunnels" })
        for (HRESULT status : { E_ACCESSDENIED, E_OUTOFMEMORY, E_FAIL, static_cast<HRESULT>(0x80040000u),
            HRESULT_FROM_WIN32(ERROR_CANCELLED) }) {
            VariableFailureScenario scenario{ variable, status };
            Bootstrapper ba; ba.context = &scenario;
            ba.engine = [](BOOTSTRAPPER_ENGINE_MESSAGE message, const LPVOID args, LPVOID, LPVOID context) -> HRESULT {
                auto& scenario = *static_cast<VariableFailureScenario*>(context);
                if (message == BOOTSTRAPPER_ENGINE_MESSAGE_SETVARIABLENUMERIC &&
                    wcscmp(static_cast<BAENGINE_SETVARIABLENUMERIC_ARGS*>(args)->wzVariable, scenario.variable) == 0) return scenario.status;
                if (message == BOOTSTRAPPER_ENGINE_MESSAGE_QUIT) scenario.quitCode = static_cast<BAENGINE_QUIT_ARGS*>(args)->dwExitCode;
                if (message == BOOTSTRAPPER_ENGINE_MESSAGE_PLAN || message == BOOTSTRAPPER_ENGINE_MESSAGE_APPLY)
                    throw std::logic_error("A failed Burn variable write must not reach planning/apply");
                return S_OK;
            };
            ba.accepted = true; ba.command.action = BOOTSTRAPPER_ACTION_INSTALL;
            ba.startUi = [&] { ba.BeginPlan(); };
            auto expectedStatus = status == HRESULT_FROM_WIN32(ERROR_CANCELLED) ? HRESULT_FROM_WIN32(ERROR_INSTALL_USEREXIT) : status;
            check(Callback(BOOTSTRAPPER_APPLICATION_MESSAGE_ONSTARTUP, nullptr, nullptr, &ba) == expectedStatus);
            check(ba.completion == NativeExitCode(expectedStatus) && ba.completion != 0);
            check(scenario.quitCode == ba.completion && !ba.applying);
        }
    for (bool allocation : { false, true }) {
        Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
        ba.detectAzure = [=]() -> Detection {
            if (allocation) throw std::bad_alloc();
            throw DetectionFailure(E_ACCESSDENIED);
        };
        BA_ONDETECTCOMPLETE_ARGS args{}; args.hrStatus = S_OK;
        check(Callback(BOOTSTRAPPER_APPLICATION_MESSAGE_ONDETECTCOMPLETE, &args, nullptr, &ba) == (allocation ? E_OUTOFMEMORY : E_ACCESSDENIED));
        check(ba.completion == (allocation ? static_cast<unsigned>(ERROR_OUTOFMEMORY) : 5104u));
    }
    {
        Bootstrapper ba; ba.engine = fakeEngine; ba.context = &engineCalls;
        ba.detectAzure = []() -> Detection { throw std::logic_error("Deliberate unexpected programming fault"); };
        BA_ONDETECTCOMPLETE_ARGS args{}; args.hrStatus = S_OK;
        bool propagated = false;
        try { ba.OnMessage(BOOTSTRAPPER_APPLICATION_MESSAGE_ONDETECTCOMPLETE, &args, nullptr); }
        catch (const std::logic_error&) { propagated = true; }
        check(propagated && ba.failure == 0 && ba.completion == UINT_MAX);
        check(noexcept(Callback(BOOTSTRAPPER_APPLICATION_MESSAGE_ONDETECTCOMPLETE, &args, nullptr, &ba)));
        check(noexcept(BootstrapperApplicationCreate(nullptr, nullptr)));
        check(BootstrapperApplicationCreate(nullptr, nullptr) == E_INVALIDARG);
    }
    std::cout << tests << " native bootstrapper policy tests passed (fakes only).\n";
}
