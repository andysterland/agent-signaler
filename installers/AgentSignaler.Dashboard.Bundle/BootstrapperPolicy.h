#pragma once
#include <string>
#include <string_view>
#include <sstream>
#include <vector>
#include <cstdint>

namespace setup {
enum class State { Missing, UpdateRequired, Installed };
struct Detection {
    State state = State::Missing;
    bool newer = false;
};
inline const wchar_t* StateText(State state) {
    switch (state) {
    case State::Installed: return L"Installed";
    case State::UpdateRequired: return L"Update required";
    default: return L"Missing";
    }
}
struct ParsedVersion { bool valid; uint64_t value; };
inline constexpr ParsedVersion ParseVersion(std::wstring_view text) noexcept {
    if (text.size() < 5 || text.size() > 23) return { false, 0 };
    uint64_t result = 0;
    unsigned value = 0, count = 0, digits = 0;
    for (wchar_t character : text) {
        if (character == L'.') {
            if (!digits || ++count >= 4) return { false, 0 };
            result = (result << 16) | value;
            value = digits = 0;
        } else {
            if (character < L'0' || character > L'9') return { false, 0 };
            const auto digit = static_cast<unsigned>(character - L'0');
            if (value > (65535u - digit) / 10u) return { false, 0 };
            value = value * 10u + digit;
            ++digits;
        }
    }
    if (!digits || ++count < 3) return { false, 0 };
    result = (result << 16) | value;
    while (count++ < 4) result <<= 16;
    return { true, result };
}
inline bool NeedsInstall(Detection state, bool selected, bool uninstall) {
    return !uninstall && selected && state.state != State::Installed && !state.newer;
}
inline unsigned StartupResult(bool interactive, bool accepted, bool supportedAction, bool relatedUpgradeUninstall = false) {
    return !supportedAction ? 87 : !interactive && !accepted && !relatedUpgradeUninstall ? 5100 : 0;
}
inline bool LicenseAccepted(const std::vector<std::wstring>& args) {
    bool found = false;
    for (const auto& arg : args) {
        if (arg.rfind(L"ACCEPT_PREREQUISITE_LICENSES", 0) == 0) {
            if (found || arg != L"ACCEPT_PREREQUISITE_LICENSES=1") return false;
            found = true;
        }
    }
    return found;
}
inline bool VersionOutputValid(const std::string& output, const std::string& error, const std::string& version, unsigned exitCode) {
    if (exitCode || output.size() > 65536 || error.size() > 65536) return false;
    const auto expected = "Tunnel CLI version: " + version;
    bool found = false;
    for (unsigned stream = 0; stream < 2; ++stream) {
        std::istringstream lines(stream ? error : output);
        std::string line;
        while (std::getline(lines, line)) {
            while (!line.empty() && line.back() == '\r') line.pop_back();
            if (!stream && line == expected) found = true;
            if ((line.rfind("Tunnel CLI version:", 0) == 0 && line != expected) ||
                (line.rfind("CLI version:", 0) == 0 && line != "CLI version: " + version)) return false;
        }
    }
    return found;
}
inline unsigned Result(unsigned failure, bool cancelled, bool reboot) {
    return failure ? failure : cancelled ? 1602 : reboot ? 3010 : 0;
}
}
