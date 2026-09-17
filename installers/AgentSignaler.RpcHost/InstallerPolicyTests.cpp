#include "InstallerPolicy.h"
#include <iostream>
#include <tuple>
using namespace rpcinstaller;

struct Fake {
    State state;
    bool running = false, foreign = false;
    bool transaction = false;
    int failAt = -1, calls = 0;
    void Fault() { if (++calls == failAt) throw std::runtime_error("injected"); }
    State Read(const Plan&) { Require(!foreign); return state; }
    State ReadForRollback(const Plan& p) {
        Require(!foreign && (!state.rule || state.port == p.port || state.port == p.before.port));
        return state;
    }
    void AssertNotInUse(const Plan&) { Require(!running); }
    void BeginTransaction(const Plan&) { Fault(); Require(!transaction); transaction = true; }
    bool HasTransaction(const Plan&) { return transaction; }
    void EndTransaction(const Plan&) { transaction = false; }
    void RemoveRule(const Plan&, int port) { Require(state.rule && state.port == port); Fault(); state.rule = false; }
    void AddRule(const Plan&, int port) { Require(!state.rule); Fault(); state.rule = true; state.port = port; }
    void WriteMetadata(const Plan&, const State& target) {
        Fault(); state.metadata = target.metadata; if (!state.rule) state.port = target.port;
    }
};
template<class F> void Reject(F action) {
    bool rejected = false;
    try { action(); } catch (const std::runtime_error&) { rejected = true; }
    Require(rejected);
}
int main() {
    try {
        AuthorizeFirewall(true, true);
        Reject([] { AuthorizeFirewall(false, true); });
        Reject([] { AuthorizeFirewall(true, false); });
        Reject([] { AuthorizeFirewall(false, false); });
        Require(Port(L"1024") == 1024 && Port(L"65535") == 65535);
        for (const auto p : {L"", L"1023", L"65536", L"51820 ", L"+51820", L"5e4"}) Reject([&] { Port(p); });
        Require(SettingsJson(L"{\"Port\":51822,\"RpcPort\":51823,\"extension\":{\"Port\":42}}").Read() == std::make_pair(51822, 51823));
        Require(SettingsJson(L"{}").Read() == std::make_pair(51820, 51821));
        Require(SelectPorts(L"", {}, {51820, 51821}, true).first == 51820);
        Require(SelectPorts(L"", {}, {51822, 51823}, true).first == 51822);
        Require(SelectPorts(L"", {true, true, 51824}, {51822, 51823}, true).first == 51824);
        Require(SelectPorts(L"51825", {true, true, 51824}, {51822, 51823}, true).first == 51825);
        Require(SelectPorts(L"51825", {}, {}, false).first == 51825);
        Reject([&] { SelectPorts(L"", {}, {}, false); });
        Reject([&] { SelectPorts(L"", {true, true, 51824}, {}, false); });
        Reject([&] { SelectPorts(L"51823", {}, {51822, 51823}, true); });
        Require(MaintenanceRegistryRewrite(true, true, false, L"", L"") == std::make_pair(std::wstring(L"Main"), std::wstring(L"u")));
        Require(MaintenanceRegistryRewrite(true, true, false, L"ALL", L"vomus") == std::make_pair(std::wstring(L"ALL"), std::wstring(L"vomus")));
        Require(MaintenanceRegistryRewrite(true, true, false, L"Main", L"voms") == std::make_pair(std::wstring(L"Main"), std::wstring(L"vomsu")));
        Require(MaintenanceRegistryRewrite(true, true, false, L"Main", L"U").second == L"U");
        for (auto flags : {std::make_tuple(false, true, false), std::make_tuple(true, false, false),
            std::make_tuple(true, true, true)}) {
            Require(MaintenanceRegistryRewrite(std::get<0>(flags), std::get<1>(flags), std::get<2>(flags),
                L"", L"") == std::make_pair(std::wstring(), std::wstring()));
        }
        for (const auto p : {L"null", L"{", L"{\"Port\":51820,\"P\\u006frt\":51822}",
             L"{\"Port\":51820.0}", L"{\"RpcPort\":80}", L"{\"x\":{\"x\":1,\"x\":2}}",
             L"{\"x\":01}", L"{\"x\":1e}", L"{\"x\":true,}", L"{\"Port\":\"51820\"}"})
            Reject([&] { SettingsJson(p).Read(); });
        std::wstring deep(34, L'['); deep += L"0"; deep += std::wstring(34, L']');
        Reject([&] { SettingsJson(L"{\"x\":" + deep + L"}").Read(); });
        std::wstring many = L"{";
        for (int i = 0; i < 129; ++i) many += (i ? L"," : L"") + std::wstring(L"\"x") + std::to_wstring(i) + L"\":0";
        many += L"}"; Reject([&] { SettingsJson(many).Read(); });
        Plan plan{L"S-1-5-21-1-2-3-1001", L"C:\\Users\\Fixture\\AppData\\Local\\Programs\\AgentSignaler\\RpcHost",
            L"C:\\Users\\Fixture\\AppData\\Local\\AgentSignaler"};
        auto entraUser = plan; entraUser.sid = L"S-1-12-1-111-222-333-444"; entraUser.Validate();
        RuleSnapshot rule{plan.RuleName(), Upgrade, plan.Description(51820), plan.Exe(), L"", L"51820",
            L"*", L"*", L"*", L"All"};
        Require(VerifyRule(plan, rule) == 51820);
        for (auto field : { &RuleSnapshot::name, &RuleSnapshot::group, &RuleSnapshot::description,
            &RuleSnapshot::exe, &RuleSnapshot::service, &RuleSnapshot::localPorts, &RuleSnapshot::remotePorts,
            &RuleSnapshot::localAddresses, &RuleSnapshot::remoteAddresses, &RuleSnapshot::interfaceTypes }) {
            auto changed = rule; changed.*field = L"foreign"; Reject([&] { VerifyRule(plan, changed); });
        }
        for (auto field : {&RuleSnapshot::protocol, &RuleSnapshot::profiles, &RuleSnapshot::direction, &RuleSnapshot::action}) {
            auto changed = rule; changed.*field = 0; Reject([&] { VerifyRule(plan, changed); });
        }
        for (auto field : {&RuleSnapshot::enabled, &RuleSnapshot::edge, &RuleSnapshot::noInterfaces, &RuleSnapshot::noSecurityRestrictions}) {
            auto changed = rule; changed.*field = !(changed.*field); Reject([&] { VerifyRule(plan, changed); });
        }
        for (int profiles : {1, 4, 3, 6, 7}) {
            auto changed = rule; changed.profiles = profiles; Reject([&] { VerifyRule(plan, changed); });
        }
        auto rpcRule = rule; rpcRule.localPorts = L"51821";
        Reject([&] { VerifyRule(plan, rpcRule); });
        for (bool commit : {false, true}) {
            auto maintenance = plan;
            maintenance.before = {true, true, 51820}; maintenance.port = 51822;
            Fake adapter{maintenance.before};
            int mirrorPort = 51820;
            auto rewrite = MaintenanceRegistryRewrite(true, true, false, L"", L"");
            Apply(maintenance, adapter);
            // Model WriteRegistryValues and its MSI rollback record. Without
            // both REINSTALL and 'u', an already-installed component is a no-op.
            if (rewrite.first == L"Main" && rewrite.second.find(L'u') != std::wstring::npos)
                mirrorPort = maintenance.port;
            if (commit) adapter.EndTransaction(maintenance);
            else {
                mirrorPort = 51820;
                Rollback(maintenance, adapter);
            }
            Require(mirrorPort == adapter.state.port && mirrorPort == (commit ? 51822 : 51820));
            auto upgrade = plan;
            upgrade.before = adapter.state;
            // The updater passes the HKCU mirror explicitly into the next MSI.
            upgrade.port = SelectPorts(std::to_wstring(mirrorPort), adapter.state, {51820, 51821}, true).first;
            Apply(upgrade, adapter);
            Require(adapter.state.port == (commit ? 51822 : 51820));
        }
        for (const State before : { State{}, State{true, true, 51820}, State{true, false, 51820} }) {
            for (bool removing : {false, true}) {
                plan.before = before; plan.removing = removing; plan.port = 51824;
                Fake success{before}; Apply(plan, success); Require(success.state == plan.After());
                Rollback(plan, success); Require(success.state == before);
                for (int failure = 1; failure <= 4; ++failure) {
                    Fake fault{before}; fault.failAt = failure;
                    try { Apply(plan, fault); } catch (const std::runtime_error&) {}
                    fault.failAt = -1; Rollback(plan, fault); Require(fault.state == before);
                }
                Fake running{before}; running.running = true;
                Reject([&] { Apply(plan, running); }); Require(running.state == before);
                Fake foreign{before}; foreign.foreign = true;
                Reject([&] { Apply(plan, foreign); }); Require(foreign.state == before);
                Rollback(plan, foreign); Require(foreign.state == before);
                foreign.transaction = true;
                Reject([&] { Rollback(plan, foreign); }); Require(foreign.state == before);
            }
        }
        plan.rpcPort = plan.port; Reject([&] { plan.Validate(); });
        std::cout << "RpcHost installer policy: ports, strict settings, install/repair/upgrade/uninstall,\n"
            "in-use, ownership collisions, mutation faults and rollback passed (fakes; no servicing).\n";
        return 0;
    } catch (...) { std::cerr << "RpcHost installer policy test failed.\n"; return 1; }
}
