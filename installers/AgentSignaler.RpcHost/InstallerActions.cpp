#include <windows.h>
#include <msiquery.h>
#include <netfw.h>
#include <sddl.h>
#include <shlobj.h>
#include <tlhelp32.h>
#include <wrl/client.h>
#include <memory>
#include "InstallerPolicy.h"

using namespace rpcinstaller;
using Microsoft::WRL::ComPtr;
namespace {
std::wstring Encode(const Plan& p);
thread_local const wchar_t* servicingStage = L"initialize-com";
struct NativeFailure : std::runtime_error {
    HRESULT code;
    explicit NativeFailure(HRESULT value) : std::runtime_error("windows-api"), code(value) {}
};
std::wstring FailureMessage(const std::exception& error) {
    if (strcmp(error.what(), "in-use") == 0)
        return L"Agent Signaler RpcHost is in use. Explicitly stop the exact installed host in every Windows session and retry; servicing never shuts it down.";
    auto message = std::wstring(L"Agent Signaler RpcHost servicing failed at ") + servicingStage;
    if (auto native = dynamic_cast<const NativeFailure*>(&error)) {
        wchar_t code[16]{};
        swprintf_s(code, L" (0x%08lX)", static_cast<unsigned long>(native->code));
        message += code;
    }
    message += L". No firewall success is assumed. See the verbose MSI log.";
    if (wcscmp(servicingStage, L"port-configuration") == 0)
        message += L" Supply RECEIVERPORT=1024..65535 when default settings are malformed; it must differ from RpcPort.";
    return message;
}
struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE h) : value(h) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
};
struct Key {
    HKEY value = nullptr;
    ~Key() { if (value) RegCloseKey(value); }
};
struct Bstr {
    BSTR value = nullptr;
    Bstr() = default;
    explicit Bstr(const std::wstring& s) : value(SysAllocString(s.c_str())) { Require(value != nullptr); }
    ~Bstr() { SysFreeString(value); }
    std::wstring Text() const { return value ? std::wstring(value, SysStringLen(value)) : L""; }
};
void Hr(HRESULT value) { if (FAILED(value)) throw NativeFailure(value); }
std::wstring Get(MSIHANDLE install, const wchar_t* name) {
    DWORD length = 0;
    wchar_t empty = 0;
    UINT result = MsiGetPropertyW(install, name, &empty, &length);
    Require(result == ERROR_SUCCESS || result == ERROR_MORE_DATA);
    Require(length <= 65535);
    std::vector<wchar_t> buffer(static_cast<size_t>(length) + 1);
    ++length;
    Require(MsiGetPropertyW(install, name, buffer.data(), &length) == ERROR_SUCCESS);
    return std::wstring(buffer.data(), length);
}
void Set(MSIHANDLE install, const wchar_t* name, const std::wstring& value) {
    Require(MsiSetPropertyW(install, name, value.c_str()) == ERROR_SUCCESS);
}
std::wstring TrimSlash(std::wstring path) {
    while (path.size() > 3 && path.back() == L'\\') path.pop_back();
    return path;
}
bool Same(const std::wstring& a, const std::wstring& b) { return _wcsicmp(a.c_str(), b.c_str()) == 0; }
std::wstring RegistryText(HKEY key, const wchar_t* name) {
    DWORD bytes = 0, type = 0;
    Require(RegQueryValueExW(key, name, nullptr, &type, nullptr, &bytes) == ERROR_SUCCESS &&
        (type == REG_SZ || type == REG_EXPAND_SZ) && bytes >= sizeof(wchar_t) && bytes <= 65536 && bytes % 2 == 0);
    std::vector<wchar_t> buffer(bytes / sizeof(wchar_t));
    Require(RegQueryValueExW(key, name, nullptr, &type, reinterpret_cast<BYTE*>(buffer.data()), &bytes) == ERROR_SUCCESS);
    Require(buffer.back() == 0 && wcslen(buffer.data()) == buffer.size() - 1);
    return buffer.data();
}
std::wstring UserSid(HANDLE token) {
    DWORD length = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &length);
    Require(length > 0 && length < 65536);
    std::vector<BYTE> buffer(length);
    Require(GetTokenInformation(token, TokenUser, buffer.data(), length, &length) != FALSE);
    LPWSTR text = nullptr;
    Require(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &text) != FALSE);
    std::wstring sid(text); LocalFree(text); return sid;
}
std::wstring CurrentSid() {
    HANDLE raw = nullptr;
    if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &raw))
        Require(GetLastError() == ERROR_NO_TOKEN && OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw));
    Handle token(raw); return UserSid(token.value);
}
std::wstring UserLocalAppData(const std::wstring& sid) {
    Key key;
    Require(RegOpenKeyExW(HKEY_LOCAL_MACHINE,
        (L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList\\" + sid).c_str(), 0,
        KEY_READ | KEY_WOW64_64KEY, &key.value) == ERROR_SUCCESS);
    auto profile = RegistryText(key.value, L"ProfileImagePath");
    std::vector<wchar_t> expanded(32768);
    DWORD size = ExpandEnvironmentStringsW(profile.c_str(), expanded.data(), static_cast<DWORD>(expanded.size()));
    Require(size > 0 && size <= expanded.size());
    return TrimSlash(expanded.data()) + L"\\AppData\\Local";
}
void ValidatePaths(const Plan& plan) {
    plan.Validate();
    PSID sid = nullptr;
    Require(ConvertStringSidToSidW(plan.sid.c_str(), &sid));
    bool validSid = IsValidSid(sid) != FALSE;
    LocalFree(sid); Require(validSid);
    auto local = UserLocalAppData(plan.sid);
    Require(Same(plan.directory, local + L"\\Programs\\AgentSignaler\\RpcHost") &&
        Same(plan.dataDirectory, local + L"\\AgentSignaler"));
    // Do not let an elevated machine rule claim an aliased/redirected executable.
    auto path = plan.directory;
    for (;;) {
        DWORD attributes = GetFileAttributesW(path.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES) Require((attributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0);
        else Require(GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND);
        auto slash = path.find_last_of(L'\\');
        if (slash <= 2 || slash == std::wstring::npos) break;
        path.resize(slash);
    }
}
bool SameFile(const std::wstring& first, const std::wstring& second) {
    if (Same(first, second)) return true;
    Handle a(CreateFileW(first.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    Handle b(CreateFileW(second.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (a.value == INVALID_HANDLE_VALUE || b.value == INVALID_HANDLE_VALUE) return false;
    FILE_ID_INFO ia{}, ib{};
    Require(GetFileInformationByHandleEx(a.value, FileIdInfo, &ia, sizeof(ia)) &&
        GetFileInformationByHandleEx(b.value, FileIdInfo, &ib, sizeof(ib)));
    return ia.VolumeSerialNumber == ib.VolumeSerialNumber &&
        memcmp(&ia.FileId, &ib.FileId, sizeof(ia.FileId)) == 0;
}
void AssertNotInUse(const Plan& plan) {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    Require(snapshot.value != INVALID_HANDLE_VALUE);
    PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
    Require(Process32FirstW(snapshot.value, &entry));
    do {
        if (entry.th32ProcessID == 0) continue;
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID));
        if (!process.value) {
            Require(!Same(entry.szExeFile, Executable) || GetLastError() == ERROR_INVALID_PARAMETER);
            continue;
        }
        std::vector<wchar_t> path(32768); DWORD length = static_cast<DWORD>(path.size());
        if (!QueryFullProcessImageNameW(process.value, 0, path.data(), &length)) {
            Require(!Same(entry.szExeFile, Executable));
            continue;
        }
        if (SameFile(path.data(), plan.Exe()))
            throw std::runtime_error("in-use");
    } while (Process32NextW(snapshot.value, &entry));
    Require(GetLastError() == ERROR_NO_MORE_FILES);
}
std::pair<int, int> ReadSettings(const Plan& plan) {
    auto path = plan.dataDirectory + L"\\dashboard-settings.json";
    Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (file.value == INVALID_HANDLE_VALUE) {
        Require(GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND);
        return {51820, 51821};
    }
    BY_HANDLE_FILE_INFORMATION info{};
    Require(GetFileInformationByHandle(file.value, &info) && (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0);
    LARGE_INTEGER size{};
    Require(GetFileSizeEx(file.value, &size) && size.QuadPart > 0 && size.QuadPart <= 1024 * 1024);
    std::vector<char> bytes(static_cast<size_t>(size.QuadPart)); DWORD read = 0;
    Require(ReadFile(file.value, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) &&
        read == bytes.size());
    size_t start = bytes.size() >= 3 && static_cast<unsigned char>(bytes[0]) == 0xef &&
        static_cast<unsigned char>(bytes[1]) == 0xbb && static_cast<unsigned char>(bytes[2]) == 0xbf ? 3 : 0;
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes.data() + start,
        static_cast<int>(bytes.size() - start), nullptr, 0);
    Require(count > 0);
    std::wstring text(count, 0);
    Require(MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes.data() + start,
        static_cast<int>(bytes.size() - start), text.data(), count) == count);
    return SettingsJson(text).Read();
}
State ReadMetadata(const Plan& plan) {
    Key key;
    auto status = RegOpenKeyExW(HKEY_LOCAL_MACHINE, (std::wstring(MetadataRoot) + plan.sid).c_str(), 0,
        KEY_READ | KEY_WOW64_64KEY, &key.value);
    if (status == ERROR_FILE_NOT_FOUND) return {};
    Require(status == ERROR_SUCCESS);
    DWORD subkeys = 0, values = 0;
    Require(RegQueryInfoKeyW(key.value, nullptr, nullptr, nullptr, &subkeys, nullptr, nullptr, &values,
        nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS && subkeys == 0 && values == 1);
    auto text = RegistryText(key.value, L"State");
    auto prefix = plan.Owner() + L"|" + plan.Exe() + L"|";
    Require(text.compare(0, prefix.size(), prefix) == 0);
    return {true, false, Port(text.substr(prefix.size()))};
}
class Firewall {
    ComPtr<INetFwRules> rules;
    ComPtr<INetFwRule> Find(const Plan& plan) {
        // Item(name) alone hides duplicate-name rules; enumerate and fail closed.
        ComPtr<IUnknown> unknown; Hr(rules->get__NewEnum(&unknown));
        ComPtr<IEnumVARIANT> enumerator; Hr(unknown.As(&enumerator));
        ComPtr<INetFwRule> found;
        for (size_t i = 0;; ++i) {
            Require(i < 65536);
            VARIANT item; VariantInit(&item); ULONG fetched = 0;
            HRESULT result = enumerator->Next(1, &item, &fetched);
            if (result == S_FALSE) { VariantClear(&item); break; }
            Hr(result);
            ComPtr<INetFwRule> rule;
            HRESULT queried = item.vt == VT_DISPATCH ? item.pdispVal->QueryInterface(IID_PPV_ARGS(&rule)) : E_NOINTERFACE;
            VariantClear(&item); Hr(queried);
            Bstr name; Hr(rule->get_Name(&name.value));
            if (Same(name.Text(), plan.RuleName())) { Require(!found); found = rule; }
        }
        return found;
    }
    int Verify(const Plan& plan, INetFwRule* rule) {
        servicingStage = L"firewall-rule-verification";
        Bstr name, group, description, application, service, localPorts, remotePorts, localAddresses, remoteAddresses, interfaces;
        Hr(rule->get_Name(&name.value)); Hr(rule->get_Grouping(&group.value));
        Hr(rule->get_Description(&description.value)); Hr(rule->get_ApplicationName(&application.value));
        Hr(rule->get_ServiceName(&service.value)); Hr(rule->get_LocalPorts(&localPorts.value));
        Hr(rule->get_RemotePorts(&remotePorts.value)); Hr(rule->get_LocalAddresses(&localAddresses.value));
        Hr(rule->get_RemoteAddresses(&remoteAddresses.value)); Hr(rule->get_InterfaceTypes(&interfaces.value));
        long protocol = 0, profiles = 0; NET_FW_RULE_DIRECTION direction{}; NET_FW_ACTION action{};
        VARIANT_BOOL enabled = VARIANT_FALSE, edge = VARIANT_TRUE;
        Hr(rule->get_Protocol(&protocol)); Hr(rule->get_Profiles(&profiles)); Hr(rule->get_Direction(&direction));
        Hr(rule->get_Action(&action)); Hr(rule->get_Enabled(&enabled)); Hr(rule->get_EdgeTraversal(&edge));
        VARIANT list; VariantInit(&list);
        Hr(rule->get_Interfaces(&list));
        bool noInterfaces = list.vt == VT_EMPTY || list.vt == VT_NULL;
        VariantClear(&list); Require(noInterfaces);
        ComPtr<INetFwRule3> advanced; Hr(rule->QueryInterface(IID_PPV_ARGS(&advanced)));
        Bstr users, machines, localUsers, package;
        long secureFlags = 0, edgeOptions = 0;
        Hr(advanced->get_RemoteUserAuthorizedList(&users.value));
        Hr(advanced->get_RemoteMachineAuthorizedList(&machines.value));
        Hr(advanced->get_LocalUserAuthorizedList(&localUsers.value));
        Hr(advanced->get_LocalAppPackageId(&package.value));
        Hr(advanced->get_SecureFlags(&secureFlags)); Hr(advanced->get_EdgeTraversalOptions(&edgeOptions));
        return VerifyRule(plan, {name.Text(), group.Text(), description.Text(), application.Text(), service.Text(),
            localPorts.Text(), remotePorts.Text(), localAddresses.Text(), remoteAddresses.Text(), interfaces.Text(),
            static_cast<int>(protocol), static_cast<int>(profiles), static_cast<int>(direction), static_cast<int>(action),
            enabled == VARIANT_TRUE, edge != VARIANT_FALSE, noInterfaces,
            users.Text().empty() && machines.Text().empty() && localUsers.Text().empty() &&
            package.Text().empty() && secureFlags == 0 && edgeOptions == 0});
    }
public:
    Firewall() {
        servicingStage = L"firewall-connect";
        ComPtr<INetFwPolicy2> policy;
        Hr(CoCreateInstance(__uuidof(NetFwPolicy2), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&policy)));
        Hr(policy->get_Rules(&rules));
    }
    std::wstring JournalKey(const Plan& plan) {
        return L"Software\\AgentSignaler\\Installer\\RpcHostTransactions\\" + plan.sid + L"\\" + plan.transaction;
    }
    void BeginTransaction(const Plan& plan) {
        servicingStage = L"firewall-transaction-create";
        Key key; DWORD disposition = 0;
        Require(RegCreateKeyExW(HKEY_LOCAL_MACHINE, JournalKey(plan).c_str(), 0, nullptr, 0,
            KEY_WRITE | KEY_WOW64_64KEY, nullptr, &key.value, &disposition) == ERROR_SUCCESS &&
            disposition == REG_CREATED_NEW_KEY);
        auto text = Encode(plan);
        auto status = RegSetValueExW(key.value, L"Plan", 0, REG_SZ, reinterpret_cast<const BYTE*>(text.c_str()),
            static_cast<DWORD>((text.size() + 1) * sizeof(wchar_t)));
        if (status != ERROR_SUCCESS) {
            RegCloseKey(key.value); key.value = nullptr;
            Require(RegDeleteKeyExW(HKEY_LOCAL_MACHINE, JournalKey(plan).c_str(), KEY_WOW64_64KEY, 0) == ERROR_SUCCESS);
        }
        Require(status == ERROR_SUCCESS);
    }
    bool HasTransaction(const Plan& plan) {
        servicingStage = L"firewall-transaction-read";
        Key key;
        auto status = RegOpenKeyExW(HKEY_LOCAL_MACHINE, JournalKey(plan).c_str(), 0, KEY_READ | KEY_WOW64_64KEY, &key.value);
        if (status == ERROR_FILE_NOT_FOUND) return false;
        Require(status == ERROR_SUCCESS && RegistryText(key.value, L"Plan") == Encode(plan));
        DWORD children = 0, values = 0;
        Require(RegQueryInfoKeyW(key.value, nullptr, nullptr, nullptr, &children, nullptr, nullptr, &values,
            nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS && children == 0 && values == 1);
        return true;
    }
    void EndTransaction(const Plan& plan) {
        Require(HasTransaction(plan));
        servicingStage = L"firewall-transaction-delete";
        Require(RegDeleteKeyExW(HKEY_LOCAL_MACHINE, JournalKey(plan).c_str(), KEY_WOW64_64KEY, 0) == ERROR_SUCCESS);
    }
    State Read(const Plan& plan) {
        servicingStage = L"firewall-ownership-read";
        State state = ReadMetadata(plan);
        auto rule = Find(plan);
        if (rule) { Require(state.metadata && Verify(plan, rule.Get()) == state.port); state.rule = true; }
        return state;
    }
    State ReadForRollback(const Plan& plan) {
        servicingStage = L"firewall-rollback-read";
        State state = ReadMetadata(plan);
        Require(!state.metadata || state.port == plan.port || (plan.before.metadata && state.port == plan.before.port));
        auto rule = Find(plan);
        if (rule) {
            int port = Verify(plan, rule.Get());
            Require(port == plan.port || (plan.before.rule && port == plan.before.port));
            state.rule = true; state.port = port;
        }
        return state;
    }
    void AssertNotInUse(const Plan& plan) {
        servicingStage = L"installed-host-check";
        ::AssertNotInUse(plan);
    }
    void RemoveRule(const Plan& plan, int port) {
        auto rule = Find(plan); Require(rule && Verify(plan, rule.Get()) == port);
        Bstr name(plan.RuleName()); Hr(rules->Remove(name.value));
        Require(!Find(plan));
    }
    void AddRule(const Plan& plan, int port) {
        servicingStage = L"firewall-rule-create";
        Require(!Find(plan));
        ComPtr<INetFwRule> rule;
        Hr(CoCreateInstance(__uuidof(NetFwRule), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&rule)));
        Bstr name(plan.RuleName()), group(Upgrade), description(plan.Description(port)), exe(plan.Exe()),
            ports(std::to_wstring(port)), any(L"*"), interfaces(L"All");
        Hr(rule->put_Name(name.value)); Hr(rule->put_Grouping(group.value));
        Hr(rule->put_Description(description.value)); Hr(rule->put_ApplicationName(exe.value));
        Hr(rule->put_Protocol(NET_FW_IP_PROTOCOL_TCP)); Hr(rule->put_LocalPorts(ports.value));
        Hr(rule->put_RemotePorts(any.value)); Hr(rule->put_LocalAddresses(any.value));
        Hr(rule->put_RemoteAddresses(any.value)); Hr(rule->put_InterfaceTypes(interfaces.value));
        Hr(rule->put_Profiles(NET_FW_PROFILE2_PRIVATE)); Hr(rule->put_Direction(NET_FW_RULE_DIR_IN));
        Hr(rule->put_Action(NET_FW_ACTION_ALLOW)); Hr(rule->put_EdgeTraversal(VARIANT_FALSE));
        Hr(rule->put_Enabled(VARIANT_TRUE));
        servicingStage = L"firewall-rule-add";
        Hr(rules->Add(rule.Get()));
        auto actual = Find(plan); Require(actual && Verify(plan, actual.Get()) == port);
    }
    void WriteMetadata(const Plan& plan, const State& state) {
        servicingStage = L"firewall-ownership-write";
        auto path = std::wstring(MetadataRoot) + plan.sid;
        if (!state.metadata) {
            auto existing = ReadMetadata(plan);
            if (existing.metadata) Require(RegDeleteKeyExW(HKEY_LOCAL_MACHINE, path.c_str(), KEY_WOW64_64KEY, 0) == ERROR_SUCCESS);
            return;
        }
        Key key;
        DWORD disposition = 0;
        Require(RegCreateKeyExW(HKEY_LOCAL_MACHINE, path.c_str(), 0, nullptr, 0, KEY_WRITE | KEY_WOW64_64KEY,
            nullptr, &key.value, &disposition) == ERROR_SUCCESS);
        // One atomic value prevents partially updated ownership fields on failure.
        auto text = plan.Owner() + L"|" + plan.Exe() + L"|" + std::to_wstring(state.port);
        auto result = RegSetValueExW(key.value, L"State", 0, REG_SZ, reinterpret_cast<const BYTE*>(text.c_str()),
            static_cast<DWORD>((text.size() + 1) * sizeof(wchar_t)));
        if (result != ERROR_SUCCESS && disposition == REG_CREATED_NEW_KEY) {
            RegCloseKey(key.value); key.value = nullptr;
            Require(RegDeleteKeyExW(HKEY_LOCAL_MACHINE, path.c_str(), KEY_WOW64_64KEY, 0) == ERROR_SUCCESS);
        }
        Require(result == ERROR_SUCCESS);
    }
};
std::wstring Encode(const Plan& p) {
    return p.sid + L"|" + p.directory + L"|" + p.dataDirectory + L"|" + std::to_wstring(p.port) + L"|" +
        std::to_wstring(p.rpcPort) + L"|" + (p.removing ? L"1" : L"0") + L"|" +
        (p.before.metadata ? L"1" : L"0") + L"|" + (p.before.rule ? L"1" : L"0") + L"|" +
        std::to_wstring(p.before.port) + L"|" + p.transaction;
}
Plan Decode(const std::wstring& text) {
    std::vector<std::wstring> fields; size_t start = 0;
    for (;;) {
        auto end = text.find(L'|', start); fields.push_back(text.substr(start, end - start));
        if (end == std::wstring::npos) break;
        start = end + 1;
    }
    Require(fields.size() == 10);
    for (int i : {5, 6, 7}) Require(fields[i] == L"0" || fields[i] == L"1");
    Plan plan{fields[0], fields[1], fields[2], {fields[6] == L"1", fields[7] == L"1",
        fields[8] == L"0" ? 0 : Port(fields[8])}, Port(fields[3]), Port(fields[4]), fields[5] == L"1", fields[9]};
    ValidatePaths(plan); return plan;
}
template<class Action> UINT Guard(MSIHANDLE install, Action action) {
    servicingStage = L"initialize-com";
    HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    try {
        Require(SUCCEEDED(com) || com == RPC_E_CHANGED_MODE);
        action();
        if (SUCCEEDED(com)) CoUninitialize();
        return ERROR_SUCCESS;
    } catch (const std::exception& error) {
        MSIHANDLE record = MsiCreateRecord(1);
        auto message = FailureMessage(error);
        MsiRecordSetStringW(record, 0, message.c_str());
        MsiProcessMessage(install, INSTALLMESSAGE_ERROR, record); MsiCloseHandle(record);
        if (SUCCEEDED(com)) CoUninitialize();
        return ERROR_INSTALL_FAILURE;
    }
}
void Deferred(MSIHANDLE install, int mode) {
    servicingStage = L"deferred-plan-validation";
    Plan plan = Decode(Get(install, L"CustomActionData"));
    servicingStage = L"firewall-token-query";
    BOOL member = FALSE; SID_IDENTIFIER_AUTHORITY authority = SECURITY_NT_AUTHORITY; PSID administrators = nullptr;
    Require(AllocateAndInitializeSid(&authority, 2, SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS,
        0, 0, 0, 0, 0, 0, &administrators));
    BOOL checked = CheckTokenMembership(nullptr, administrators, &member);
    FreeSid(administrators);
    servicingStage = L"installer-user-query";
    bool intendedUserMatches = Get(install, L"UserSID") == plan.sid;
    servicingStage = checked && member ? L"original-user-authorization" : L"firewall-elevation";
    AuthorizeFirewall(checked && member, intendedUserMatches);
    // Serialize this product/user's machine firewall operation across sessions.
    servicingStage = L"firewall-transaction-lock";
    auto name = L"Global\\AgentSignaler.RpcHost.Msi." + plan.sid;
    Handle mutex(CreateMutexW(nullptr, FALSE, name.c_str()));
    Require(mutex.value != nullptr);
    DWORD wait = WaitForSingleObject(mutex.value, 10000);
    Require(wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED);
    try {
        Firewall firewall;
        if (mode == 1) Rollback(plan, firewall);
        else if (mode == 2) firewall.EndTransaction(plan);
        else Apply(plan, firewall);
        ReleaseMutex(mutex.value);
    } catch (...) { ReleaseMutex(mutex.value); throw; }
}
}
extern "C" __declspec(dllexport) UINT __stdcall CaptureUser(MSIHANDLE install) {
    return Guard(install, [&] {
        servicingStage = L"capture-original-user";
        Require(Get(install, L"ALLUSERS").empty());
        std::wstring sid = Get(install, L"UserSID");
        Require(!sid.empty());
        Plan plan;
        plan.sid = sid;
        auto local = UserLocalAppData(sid);
        plan.directory = local + L"\\Programs\\AgentSignaler\\RpcHost";
        plan.dataDirectory = local + L"\\AgentSignaler";
        if (Get(install, L"RPCPREPARED") != L"1") {
            Require(CurrentSid() == sid);
            PWSTR known = nullptr; Hr(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, nullptr, &known));
            bool match = Same(TrimSlash(known), local); CoTaskMemFree(known); Require(match);
            Set(install, L"RPCUSER", sid); Set(install, L"RPCDIRECTORY", plan.directory);
            Set(install, L"RPCDATA", plan.dataDirectory);
            auto explicitPort = Get(install, L"RECEIVERPORT");
            Set(install, L"RPCPORTMAINTENANCE", explicitPort.empty() ? L"0" : L"1");
            auto metadata = ReadMetadata(plan);
            std::pair<int, int> settings;
            bool settingsValid = true;
            try { settings = ReadSettings(plan); }
            catch (const std::runtime_error&) { settingsValid = false; }
            servicingStage = L"port-configuration";
            auto ports = SelectPorts(explicitPort, metadata, settings, settingsValid);
            plan.port = ports.first; plan.rpcPort = ports.second;
            plan.Validate();
            Set(install, L"RECEIVERPORT", std::to_wstring(plan.port));
            Set(install, L"RPCPORT", std::to_wstring(plan.rpcPort));
            Set(install, L"RPCPREPARED", L"1");
        }
        servicingStage = L"validate-captured-user";
        Require(Get(install, L"RPCUSER") == sid && Same(Get(install, L"RPCDIRECTORY"), plan.directory) &&
            Same(Get(install, L"RPCDATA"), plan.dataDirectory));
        plan.port = Port(Get(install, L"RECEIVERPORT")); plan.rpcPort = Port(Get(install, L"RPCPORT"));
        ValidatePaths(plan);
        auto maintenance = Get(install, L"RPCPORTMAINTENANCE");
        Require(maintenance == L"0" || maintenance == L"1");
        // Changing a formatted Registry value does not itself reinstall its
        // component. Schedule MSI's transactional HKCU rewrite before costing.
        auto reinstall = MaintenanceRegistryRewrite(!Get(install, L"Installed").empty(), maintenance == L"1",
            !Get(install, L"REMOVE").empty(), Get(install, L"REINSTALL"), Get(install, L"REINSTALLMODE"));
        Set(install, L"REINSTALL", reinstall.first);
        Set(install, L"REINSTALLMODE", reinstall.second);
        Set(install, L"INSTALLFOLDER", plan.directory + L"\\");
        Set(install, L"MSIRESTARTMANAGERCONTROL", L"Disable");
        Set(install, L"MSIDISABLERMRESTART", L"1");
    });
}
extern "C" __declspec(dllexport) UINT __stdcall PrepareServicing(MSIHANDLE install) {
    return Guard(install, [&] {
        servicingStage = L"prepare-servicing";
        Plan plan;
        plan.sid = Get(install, L"RPCUSER"); plan.directory = Get(install, L"RPCDIRECTORY");
        plan.dataDirectory = Get(install, L"RPCDATA"); plan.port = Port(Get(install, L"RECEIVERPORT"));
        plan.rpcPort = Port(Get(install, L"RPCPORT")); plan.removing = Get(install, L"REMOVE") == L"ALL";
        Require(plan.sid == Get(install, L"UserSID") &&
            Same(TrimSlash(Get(install, L"INSTALLFOLDER")), plan.directory) && Get(install, L"ALLUSERS").empty());
        ValidatePaths(plan); AssertNotInUse(plan);
        if (!Get(install, L"UPGRADINGPRODUCTCODE").empty()) return;
        Firewall firewall; plan.before = firewall.Read(plan);
        GUID transaction{}; Hr(CoCreateGuid(&transaction));
        wchar_t transactionText[39]{};
        Require(StringFromGUID2(transaction, transactionText, 39) == 39);
        plan.transaction = std::wstring(transactionText + 1, 36);
        plan.Validate();
        auto encoded = Encode(plan);
        Set(install, L"RollbackRpcHostFirewall", encoded); Set(install, L"ApplyRpcHostFirewall", encoded);
        Set(install, L"CommitRpcHostFirewall", encoded);
        Set(install, L"VerifyRpcHostFilesIdle", encoded);
    });
}
extern "C" __declspec(dllexport) UINT __stdcall ApplyFirewall(MSIHANDLE install) {
    return Guard(install, [&] { Deferred(install, 0); });
}
extern "C" __declspec(dllexport) UINT __stdcall RollbackFirewall(MSIHANDLE install) {
    return Guard(install, [&] { Deferred(install, 1); });
}
extern "C" __declspec(dllexport) UINT __stdcall CommitFirewall(MSIHANDLE install) {
    return Guard(install, [&] { Deferred(install, 2); });
}
extern "C" __declspec(dllexport) UINT __stdcall VerifyFilesIdle(MSIHANDLE install) {
    return Guard(install, [&] {
        auto plan = Decode(Get(install, L"CustomActionData"));
        Require(Get(install, L"UserSID") == plan.sid);
        AssertNotInUse(plan);
    });
}
