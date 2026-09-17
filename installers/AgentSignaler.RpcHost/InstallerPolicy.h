#pragma once
#include <algorithm>
#include <cstdint>
#include <cwctype>
#include <map>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

namespace rpcinstaller {
inline constexpr wchar_t Upgrade[] = L"{A8D1F2A9-762B-4B2C-A5A3-451463D743B2}";
inline constexpr wchar_t MetadataRoot[] = L"Software\\AgentSignaler\\Installer\\RpcHost\\";
inline constexpr wchar_t Executable[] = L"AgentSignaler.RpcHost.exe";
inline void Require(bool condition) { if (!condition) throw std::runtime_error("servicing-policy"); }
inline void AuthorizeFirewall(bool elevated, bool intendedUserMatches) { Require(elevated && intendedUserMatches); }
inline int Port(const std::wstring& value) {
    Require(!value.empty() && value.size() <= 5);
    int result = 0;
    for (auto c : value) { Require(c >= L'0' && c <= L'9'); result = result * 10 + c - L'0'; }
    Require(result >= 1024 && result <= 65535);
    return result;
}

// Parse the complete bounded settings document, not a substring/regex that could
// select a nested, duplicate, escaped, or malformed port.
class SettingsJson {
    const std::wstring& text;
    size_t offset = 0;
    std::map<std::wstring, std::wstring> ports;
    void Space() { while (offset < text.size() && (text[offset] == L' ' || text[offset] == L'\t' ||
        text[offset] == L'\r' || text[offset] == L'\n')) ++offset; }
    wchar_t Take() { Require(offset < text.size()); return text[offset++]; }
    void Expect(wchar_t c) { Space(); Require(Take() == c); }
    unsigned Hex() {
        unsigned n = 0;
        for (int i = 0; i < 4; ++i) {
            auto c = Take();
            Require((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') || (c >= L'A' && c <= L'F'));
            n = n * 16 + (c <= L'9' ? c - L'0' : (c | 32) - L'a' + 10);
        }
        return n;
    }
    std::wstring String() {
        Expect(L'"');
        std::wstring result;
        for (;;) {
            wchar_t c = Take();
            if (c == L'"') return result;
            Require(c >= 32);
            if (c == L'\\') {
                c = Take();
                switch (c) {
                case L'"': case L'\\': case L'/': break;
                case L'b': c = L'\b'; break;
                case L'f': c = L'\f'; break;
                case L'n': c = L'\n'; break;
                case L'r': c = L'\r'; break;
                case L't': c = L'\t'; break;
                case L'u': c = static_cast<wchar_t>(Hex()); break;
                default: Require(false);
                }
            }
            result += c;
            Require(result.size() <= 32768);
        }
    }
    void Value(int depth) {
        Require(depth <= 32);
        Space(); Require(offset < text.size());
        if (text[offset] == L'{') { Object(depth + 1); return; }
        if (text[offset] == L'[') {
            ++offset; Space();
            if (offset < text.size() && text[offset] == L']') { ++offset; return; }
            for (;;) {
                Value(depth + 1); Space(); auto c = Take();
                if (c == L']') return;
                Require(c == L',');
            }
        }
        if (text[offset] == L'"') { String(); return; }
        for (const auto literal : { L"true", L"false", L"null" }) {
            std::wstring word(literal);
            if (text.compare(offset, word.size(), word) == 0) { offset += word.size(); return; }
        }
        if (text[offset] == L'-') ++offset;
        Require(offset < text.size());
        if (text[offset] == L'0') ++offset;
        else {
            Require(text[offset] >= L'1' && text[offset] <= L'9');
            while (offset < text.size() && text[offset] >= L'0' && text[offset] <= L'9') ++offset;
        }
        if (offset < text.size() && text[offset] == L'.') {
            ++offset; size_t start = offset;
            while (offset < text.size() && text[offset] >= L'0' && text[offset] <= L'9') ++offset;
            Require(offset != start);
        }
        if (offset < text.size() && (text[offset] == L'e' || text[offset] == L'E')) {
            ++offset;
            if (offset < text.size() && (text[offset] == L'+' || text[offset] == L'-')) ++offset;
            size_t start = offset;
            while (offset < text.size() && text[offset] >= L'0' && text[offset] <= L'9') ++offset;
            Require(offset != start);
        }
    }
    void Object(int depth) {
        Require(depth <= 32);
        Expect(L'{'); Space();
        if (offset < text.size() && text[offset] == L'}') { ++offset; return; }
        std::set<std::wstring> keys;
        for (;;) {
            auto key = String();
            Require(keys.insert(key).second && keys.size() <= 128);
            Expect(L':'); Space();
            size_t start = offset;
            Value(depth);
            if (depth == 1 && (key == L"Port" || key == L"RpcPort"))
                ports[key] = text.substr(start, offset - start);
            Space(); auto c = Take();
            if (c == L'}') return;
            Require(c == L',');
        }
    }
public:
    explicit SettingsJson(const std::wstring& value) : text(value) {}
    std::pair<int, int> Read() {
        Require(text.size() <= 1024 * 1024);
        Object(1); Space(); Require(offset == text.size());
        return { ports.count(L"Port") ? Port(ports[L"Port"]) : 51820,
            ports.count(L"RpcPort") ? Port(ports[L"RpcPort"]) : 51821 };
    }
};

struct State {
    bool metadata = false;
    bool rule = false;
    int port = 0;
    bool operator==(const State& other) const {
        return metadata == other.metadata && rule == other.rule && port == other.port;
    }
};
inline std::pair<int, int> SelectPorts(const std::wstring& explicitPort, const State& installed,
    std::pair<int, int> settings, bool settingsValid) {
    Require(settingsValid || !explicitPort.empty());
    if (!settingsValid) settings = {51820, 51821};
    int receiver = !explicitPort.empty() ? Port(explicitPort) :
        installed.metadata ? Port(std::to_wstring(installed.port)) : Port(std::to_wstring(settings.first));
    int rpc = Port(std::to_wstring(settings.second));
    Require(receiver != rpc);
    return {receiver, rpc};
}
inline std::pair<std::wstring, std::wstring> MaintenanceRegistryRewrite(bool installed, bool explicitPort,
    bool removing, std::wstring features, std::wstring modes) {
    if (!installed || !explicitPort || removing) return {features, modes};
    Require(features.size() <= 4096 && modes.size() <= 128);
    if (features != L"ALL") {
        bool containsMain = false;
        size_t start = 0;
        for (;;) {
            auto end = features.find(L',', start);
            if (features.substr(start, end - start) == L"Main") containsMain = true;
            if (end == std::wstring::npos) break;
            start = end + 1;
        }
        if (!containsMain) features += features.empty() ? L"Main" : L",Main";
    }
    if (modes.find_first_of(L"uU") == std::wstring::npos) modes += L"u";
    return {features, modes};
}
struct Plan {
    std::wstring sid, directory, dataDirectory;
    State before;
    int port = 51820;
    int rpcPort = 51821;
    bool removing = false;
    std::wstring transaction = L"00000000-0000-4000-8000-000000000001";
    std::wstring Exe() const { return directory + L"\\" + Executable; }
    std::wstring RuleName() const { return L"Agent Signaler RpcHost " + sid; }
    std::wstring Owner() const { return std::wstring(Upgrade) + L"|" + sid; }
    std::wstring Description(int rulePort) const {
        // INetFwRule descriptions forbid '|'; protected metadata keeps its existing format.
        return std::wstring(Upgrade) + L";" + sid + L";" + Exe() + L";" +
            std::to_wstring(rulePort) + L";Private;TCP;receiver";
    }
    void Validate() const {
        Require(sid.rfind(L"S-1-", 0) == 0 && sid.size() < 185);
        for (auto c : sid) Require(c == L'S' || c == L'-' || (c >= L'0' && c <= L'9'));
        for (const auto& path : {directory, dataDirectory}) {
            Require(path.size() > 3 && path.size() < 30000 && path[1] == L':' && path[2] == L'\\');
            for (auto c : path) Require(c >= 32 && c != L'|' && c != L'"');
            Require(path.find(L"\\..") == std::wstring::npos);
        }
        Require(Port(std::to_wstring(port)) != Port(std::to_wstring(rpcPort)));
        Require(!before.rule || before.metadata);
        Require(transaction.size() == 36);
        for (size_t i = 0; i < transaction.size(); ++i) {
            auto c = transaction[i];
            Require(i == 8 || i == 13 || i == 18 || i == 23 ? c == L'-' :
                (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') || (c >= L'A' && c <= L'F'));
        }
        if (before.metadata) Port(std::to_wstring(before.port));
        else Require(before.port == 0);
    }
    State After() const { return removing ? State{} : State{true, true, port}; }
};
struct RuleSnapshot {
    std::wstring name, group, description, exe, service, localPorts, remotePorts, localAddresses,
        remoteAddresses, interfaceTypes;
    int protocol = 6, profiles = 2, direction = 1, action = 1;
    bool enabled = true, edge = false, noInterfaces = true, noSecurityRestrictions = true;
};
inline int VerifyRule(const Plan& plan, const RuleSnapshot& rule) {
    auto insensitive = [](std::wstring a, std::wstring b) {
        std::transform(a.begin(), a.end(), a.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
        std::transform(b.begin(), b.end(), b.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
        return a == b;
    };
    int port = Port(rule.localPorts);
    Require(rule.name == plan.RuleName() && rule.group == Upgrade &&
        rule.description == plan.Description(port) && insensitive(rule.exe, plan.Exe()) &&
        rule.service.empty() && (rule.remotePorts.empty() || rule.remotePorts == L"*") &&
        rule.localAddresses == L"*" && rule.remoteAddresses == L"*" &&
        insensitive(rule.interfaceTypes, L"All") && rule.protocol == 6 && rule.profiles == 2 &&
        rule.direction == 1 && rule.action == 1 && rule.enabled && !rule.edge &&
        rule.noInterfaces && rule.noSecurityRestrictions);
    return port;
}

// Both the production adapter and in-memory faults exercise this same algorithm.
// Read verifies every rule field against protected ownership metadata.
template<class Adapter> void Apply(const Plan& plan, Adapter& adapter) {
    plan.Validate();
    Require(adapter.Read(plan) == plan.before);
    adapter.AssertNotInUse(plan);
    adapter.BeginTransaction(plan);
    if (plan.before.rule) adapter.RemoveRule(plan, plan.before.port);
    if (!plan.removing) adapter.AddRule(plan, plan.port);
    adapter.WriteMetadata(plan, plan.After());
}
template<class Adapter> void Rollback(const Plan& plan, Adapter& adapter) {
    plan.Validate();
    if (!adapter.HasTransaction(plan)) return;
    // ReadForRollback accepts only the old/new exact owned identities, including
    // partial failure between rule replacement and the metadata write.
    State current = adapter.ReadForRollback(plan);
    if (current.rule && (!plan.before.rule || current.port != plan.before.port))
        adapter.RemoveRule(plan, current.port);
    if (plan.before.rule && (!current.rule || current.port != plan.before.port))
        adapter.AddRule(plan, plan.before.port);
    adapter.WriteMetadata(plan, plan.before);
    adapter.EndTransaction(plan);
}
}
