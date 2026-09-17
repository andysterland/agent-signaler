#include "InstallerActions.cpp"
#include <iostream>

namespace {
class Child {
    Handle ready, release, process;
public:
    Child(const std::wstring& executable) : ready(nullptr), release(nullptr), process(nullptr) {
        GUID guid{}; Hr(CoCreateGuid(&guid));
        wchar_t id[39]{}; Require(StringFromGUID2(guid, id, 39) == 39);
        auto prefix = std::wstring(L"Local\\AgentSignaler.RpcHost.InstallerFixture.") + id;
        auto readyName = prefix + L".Ready", releaseName = prefix + L".Release";
        ready.value = CreateEventW(nullptr, TRUE, FALSE, readyName.c_str());
        release.value = CreateEventW(nullptr, TRUE, FALSE, releaseName.c_str());
        Require(ready.value && release.value);
        auto command = L"\"" + executable + L"\" --hold \"" + readyName + L"\" \"" + releaseName + L"\"";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup);
        PROCESS_INFORMATION info{};
        Require(CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
            CREATE_NO_WINDOW, nullptr, nullptr, &startup, &info));
        process.value = info.hProcess; CloseHandle(info.hThread);
        if (WaitForSingleObject(ready.value, 5000) != WAIT_OBJECT_0) {
            SetEvent(release.value); WaitForSingleObject(process.value, 15000);
            throw std::runtime_error("fixture-ready");
        }
    }
    ~Child() {
        SetEvent(release.value);
        if (WaitForSingleObject(process.value, 15000) == WAIT_TIMEOUT) {
            // Only this test-created process handle; never a product or name lookup.
            TerminateProcess(process.value, 1); WaitForSingleObject(process.value, 5000);
        }
    }
};
template<class Action> void Reject(Action action) {
    bool rejected = false;
    try { action(); } catch (const std::runtime_error&) { rejected = true; }
    Require(rejected);
}
}
int wmain(int argc, wchar_t** argv) {
    try {
        if (argc == 4 && std::wstring(argv[1]) == L"--hold") {
            Handle ready(OpenEventW(EVENT_MODIFY_STATE, FALSE, argv[2]));
            Handle release(OpenEventW(SYNCHRONIZE, FALSE, argv[3]));
            Require(ready.value && release.value);
            SetEvent(ready.value);
            return WaitForSingleObject(release.value, 15000) == WAIT_OBJECT_0 ? 0 : 1;
        }
        Require(argc == 2);
        servicingStage = L"firewall-rule-add";
        auto message = FailureMessage(std::runtime_error("private fixture content"));
        Require(message.find(L"firewall-rule-add") != std::wstring::npos &&
            message.find(L"private fixture content") == std::wstring::npos &&
            message.find(L"RECEIVERPORT") == std::wstring::npos);
        message = FailureMessage(NativeFailure(E_ACCESSDENIED));
        Require(message.find(L"0x80070005") != std::wstring::npos);
        servicingStage = L"port-configuration";
        Require(FailureMessage(std::runtime_error("servicing-policy")).find(L"RECEIVERPORT=1024..65535") != std::wstring::npos);
        Require(FailureMessage(std::runtime_error("in-use")).find(L"RpcHost is in use") != std::wstring::npos);
        std::wstring fixture = argv[1];
        Require(fixture.find(L"artifacts\\rpchost-installer\\") != std::wstring::npos);
        wchar_t self[32768]{};
        Require(GetModuleFileNameW(nullptr, self, 32768) > 0);
        auto expected = fixture + L"\\AgentSignaler.RpcHost.exe";
        auto alias = fixture + L"\\RenamedFixture.exe";
        auto foreignDirectory = fixture + L"\\foreign";
        Require(CreateDirectoryW(foreignDirectory.c_str(), nullptr));
        auto foreign = foreignDirectory + L"\\AgentSignaler.RpcHost.exe";
        Require(CopyFileW(self, expected.c_str(), TRUE));
        Require(CreateHardLinkW(alias.c_str(), expected.c_str(), nullptr));
        Require(CopyFileW(self, foreign.c_str(), TRUE));
        Require(SameFile(expected, alias) && !SameFile(expected, foreign));
        Plan plan; plan.directory = fixture;
        AssertNotInUse(plan);
        {
            Child exact(expected);
            Reject([&] { AssertNotInUse(plan); });
        }
        {
            Child renamed(alias);
            Reject([&] { AssertNotInUse(plan); });
        }
        {
            Child unrelated(foreign);
            AssertNotInUse(plan);
        }
        AssertNotInUse(plan);
        std::cout << "RpcHost installer identity boundaries passed: real isolated subprocesses,\n"
            "same-file hardlink/renamed image, foreign same-name preservation and explicit exit.\n";
        return 0;
    } catch (...) { std::cerr << "RpcHost installer identity boundary fixture failed.\n"; return 1; }
}
