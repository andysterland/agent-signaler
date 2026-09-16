#pragma once
#include <windows.h>
#include <msi.h>
#include <msiquery.h>
#include <softpub.h>
#include <wintrust.h>
#include <wincrypt.h>
#include <bcrypt.h>
#include <shlobj.h>
#include <appmodel.h>
#include <atomic>
#include <vector>
#include <string>
#include <memory>
#include "BootstrapperFailure.h"
#include "BootstrapperPolicy.h"
#include "Prerequisites.generated.h"

namespace setup {
inline constexpr auto AzureMinimum = ParseVersion(AzureCliVersion);
inline constexpr auto TunnelQualified = ParseVersion(DevTunnelsFileVersion);
inline constexpr auto WindowsAppMinimum = ParseVersion(WindowsAppMinimumVersion);
static_assert(AzureMinimum.valid && TunnelQualified.valid && WindowsAppMinimum.valid, "Invalid pinned prerequisite version.");
inline uint64_t InstalledProductVersion(std::wstring_view text) {
    const auto parsed = ParseVersion(text);
    if (!parsed.valid) throw DetectionFailure(HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
    return parsed.value;
}
inline bool SupportedPlatform() {
    SYSTEM_INFO system{};
    GetNativeSystemInfo(&system);
    if (system.wProcessorArchitecture != PROCESSOR_ARCHITECTURE_AMD64) return false;
    using GetVersion = LONG(WINAPI*)(OSVERSIONINFOW*);
    auto query = reinterpret_cast<GetVersion>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
    OSVERSIONINFOW version{}; version.dwOSVersionInfoSize = sizeof(version);
    return query && query(&version) == 0 && version.dwMajorVersion >= 10 && version.dwBuildNumber >= 22621;
}
struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE h = INVALID_HANDLE_VALUE) : value(h) {}
    ~Handle() { if (value != INVALID_HANDLE_VALUE && value != nullptr) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    operator HANDLE() const { return value; }
};
inline std::wstring Folder(REFKNOWNFOLDERID id) {
    PWSTR path = nullptr;
    auto status = SHGetKnownFolderPath(id, 0, nullptr, &path);
    if (FAILED(status)) throw DetectionFailure(status);
    std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> owner(path, CoTaskMemFree);
    return std::wstring(owner.get());
}
inline std::wstring AzurePath() {
    return Folder(FOLDERID_ProgramFilesX64) + L"\\Microsoft SDKs\\Azure\\CLI2\\wbin\\az.exe";
}
inline std::wstring WingetPath() {
    return Folder(FOLDERID_LocalAppData) + L"\\Microsoft\\WinGet\\Links\\devtunnel.exe";
}
inline std::wstring TunnelPath() {
    return Folder(FOLDERID_LocalAppData) + L"\\Programs\\Microsoft Dev Tunnels CLI\\devtunnel.exe";
}
inline bool Exists(const std::wstring& path) {
    auto attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}
inline bool MicrosoftSigned(const std::wstring& path) {
    WINTRUST_FILE_INFO file{ sizeof(file), path.c_str() };
    WINTRUST_DATA data{};
    data.cbStruct = sizeof(data);
    data.dwUIChoice = WTD_UI_NONE;
    data.fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN;
    data.dwUnionChoice = WTD_CHOICE_FILE;
    data.pFile = &file;
    data.dwStateAction = WTD_STATEACTION_VERIFY;
    data.dwProvFlags = WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT;
    GUID policy = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    bool valid = WinVerifyTrust(nullptr, &policy, &data) == ERROR_SUCCESS;
    if (valid) {
        auto provider = WTHelperProvDataFromStateData(data.hWVTStateData);
        auto signer = provider ? WTHelperGetProvSignerFromChain(provider, 0, FALSE, 0) : nullptr;
        auto certificate = signer ? WTHelperGetProvCertFromChain(signer, 0) : nullptr;
        wchar_t organization[256]{}, name[256]{};
        valid = certificate &&
            CertGetNameStringW(certificate->pCert, CERT_NAME_ATTR_TYPE, 0, const_cast<char*>(szOID_ORGANIZATION_NAME), organization, 256) > 1 &&
            CertGetNameStringW(certificate->pCert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, nullptr, name, 256) > 1 &&
            wcscmp(organization, L"Microsoft Corporation") == 0 && wcscmp(name, L"Microsoft Corporation") == 0;
    }
    data.dwStateAction = WTD_STATEACTION_CLOSE;
    WinVerifyTrust(nullptr, &policy, &data);
    return valid;
}
inline bool X64(HANDLE file) {
    LARGE_INTEGER zero{}, offset{};
    DWORD read = 0;
    IMAGE_DOS_HEADER dos{};
    if (!SetFilePointerEx(file, zero, nullptr, FILE_BEGIN) || !ReadFile(file, &dos, sizeof(dos), &read, nullptr) ||
        read != sizeof(dos) || dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < sizeof(dos)) return false;
    offset.QuadPart = dos.e_lfanew;
    DWORD signature{}; IMAGE_FILE_HEADER header{};
    return SetFilePointerEx(file, offset, nullptr, FILE_BEGIN) &&
        ReadFile(file, &signature, sizeof(signature), &read, nullptr) && read == sizeof(signature) && signature == IMAGE_NT_SIGNATURE &&
        ReadFile(file, &header, sizeof(header), &read, nullptr) && read == sizeof(header) && header.Machine == IMAGE_FILE_MACHINE_AMD64;
}
inline uint64_t FileVersion(const std::wstring& path) {
    auto size = GetFileVersionInfoSizeW(path.c_str(), nullptr);
    if (!size) return 0;
    std::vector<BYTE> buffer(size);
    VS_FIXEDFILEINFO* version = nullptr; UINT length{};
    if (!GetFileVersionInfoW(path.c_str(), 0, size, buffer.data()) ||
        !VerQueryValueW(buffer.data(), L"\\", reinterpret_cast<void**>(&version), &length) || length < sizeof(*version)) return 0;
    return (static_cast<uint64_t>(version->dwFileVersionMS) << 32) | version->dwFileVersionLS;
}
inline bool Sha256(HANDLE file, const wchar_t* expected) {
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return false;
    bool valid = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
    LARGE_INTEGER zero{};
    valid = valid && SetFilePointerEx(file, zero, nullptr, FILE_BEGIN);
    BYTE block[65536], digest[32]; DWORD read{};
    while (valid) {
        if (!ReadFile(file, block, sizeof(block), &read, nullptr)) { valid = false; break; }
        if (!read) break;
        valid = BCryptHashData(hash, block, read, 0) >= 0;
    }
    valid = valid && BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    if (valid) {
        wchar_t hex[65]{};
        for (unsigned i = 0; i < 32; i++) swprintf_s(hex + i * 2, 3, L"%02X", digest[i]);
        valid = wcscmp(hex, expected) == 0;
    }
    if (hash) BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return valid;
}
inline std::wstring ProductInfo(const wchar_t* product, const wchar_t* property) {
    wchar_t buffer[1024]{}; DWORD count = 1024;
    auto status = MsiGetProductInfoExW(product, nullptr, MSIINSTALLCONTEXT_MACHINE, property, buffer, &count);
    // Enumeration can include a per-user product, outside the required machine context.
    if (status == ERROR_UNKNOWN_PRODUCT) return {};
    if (status != ERROR_SUCCESS) throw DetectionFailure(HRESULT_FROM_WIN32(status));
    return buffer;
}
inline Detection DetectAzure() {
    Detection result;
    uint64_t installed{};
    wchar_t product[39]{};
    for (DWORD index = 0;; index++) {
        auto status = MsiEnumRelatedProductsW(AzureCliUpgradeCode, 0, index, product);
        if (status == ERROR_NO_MORE_ITEMS) break;
        if (status != ERROR_SUCCESS) throw DetectionFailure(HRESULT_FROM_WIN32(status));
        if (ProductInfo(product, INSTALLPROPERTY_PUBLISHER) != L"Microsoft Corporation" ||
            ProductInfo(product, INSTALLPROPERTY_INSTALLEDPRODUCTNAME) != L"Microsoft Azure CLI (64-bit)") continue;
        installed = (std::max)(installed, InstalledProductVersion(ProductInfo(product, INSTALLPROPERTY_VERSIONSTRING)));
    }
    auto path = AzurePath();
    bool exists = Exists(path);
    result.state = installed || exists ? State::UpdateRequired : State::Missing;
    result.newer = installed > AzureMinimum.value;
    if (installed >= AzureMinimum.value) {
        Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr));
        if (file.value != INVALID_HANDLE_VALUE && X64(file) && MicrosoftSigned(path) && FileVersion(path) >= AzureMinimum.value)
            result.state = State::Installed;
    }
    return result;
}
inline bool CheckTunnelVersion(const std::wstring& path, const std::atomic_bool& cancelled) {
    SECURITY_ATTRIBUTES security{ sizeof(security), nullptr, TRUE };
    HANDLE outRead{}, outWrite{}, errRead{}, errWrite{};
    if (!CreatePipe(&outRead, &outWrite, &security, 0)) return false;
    Handle output(outRead), outputWriter(outWrite);
    if (!CreatePipe(&errRead, &errWrite, &security, 0)) return false;
    Handle error(errRead), errorWriter(errWrite);
    SetHandleInformation(output, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(error, HANDLE_FLAG_INHERIT, 0);
    Handle input(CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &security, OPEN_EXISTING, 0, nullptr));
    Handle job(CreateJobObjectW(nullptr, nullptr));
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job.value || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return false;
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(nullptr, 2, 0, &size);
    std::vector<BYTE> attributeBuffer(size);
    auto attributes = reinterpret_cast<PPROC_THREAD_ATTRIBUTE_LIST>(attributeBuffer.data());
    if (!InitializeProcThreadAttributeList(attributes, 2, 0, &size)) return false;
    HANDLE handles[] = { input.value, outputWriter.value, errorWriter.value };
    bool prepared = UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, sizeof(handles), nullptr, nullptr) &&
        UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, &job.value, sizeof(HANDLE), nullptr, nullptr);
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = input;
    startup.StartupInfo.hStdOutput = outputWriter;
    startup.StartupInfo.hStdError = errorWriter;
    startup.lpAttributeList = attributes;
    PROCESS_INFORMATION process{};
    auto command = L"\"" + path + L"\" --version";
    bool started = prepared && CreateProcessW(path.c_str(), command.data(), nullptr, nullptr, TRUE,
        CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.StartupInfo, &process);
    DeleteProcThreadAttributeList(attributes);
    if (!started) return false;
    Handle child(process.hProcess), thread(process.hThread);
    CloseHandle(outputWriter.value); outputWriter.value = INVALID_HANDLE_VALUE;
    CloseHandle(errorWriter.value); errorWriter.value = INVALID_HANDLE_VALUE;
    if (ResumeThread(thread) == static_cast<DWORD>(-1)) return false;
    std::string stdoutText, stderrText;
    auto deadline = GetTickCount64() + 15000;
    auto drain = [](HANDLE pipe, std::string& text) {
        DWORD available{}, read{}; char buffer[4096];
        if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) return GetLastError() == ERROR_BROKEN_PIPE;
        while (available) {
            if (!ReadFile(pipe, buffer, (std::min)(available, static_cast<DWORD>(sizeof(buffer))), &read, nullptr)) return false;
            text.append(buffer, read);
            if (text.size() > 65536) return false;
            available -= read;
        }
        return true;
    };
    while (!cancelled && GetTickCount64() < deadline) {
        if (!drain(output, stdoutText) || !drain(error, stderrText)) return false;
        if (WaitForSingleObject(child, 10) == WAIT_OBJECT_0) {
            DWORD code{};
            if (!drain(output, stdoutText) || !drain(error, stderrText) || !GetExitCodeProcess(child, &code)) return false;
            std::string qualified;
            for (wchar_t ch : std::wstring(DevTunnelsVersion)) {
                if (ch > 127) return false;
                qualified.push_back(static_cast<char>(ch));
            }
            return VersionOutputValid(stdoutText, stderrText, qualified, code);
        }
    }
    return false;
}
inline Detection DetectTunnels(const std::atomic_bool& cancelled) {
    // Match Dashboard discovery: an existing WinGet alias takes precedence, even if unsupported.
    auto path = Exists(WingetPath()) ? WingetPath() : TunnelPath();
    if (!Exists(path)) return {};
    Detection result{ State::UpdateRequired };
    Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr));
    if (file.value == INVALID_HANDLE_VALUE) return result;
    wchar_t resolved[32768]{};
    auto count = GetFinalPathNameByHandleW(file, resolved, 32768, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (!count || count >= 32768 || wcsncmp(resolved, L"\\\\?\\UNC\\", 8) == 0) return result;
    path = resolved;
    auto version = FileVersion(path);
    result.newer = version > TunnelQualified.value || FileVersion(TunnelPath()) > TunnelQualified.value;
    if (X64(file) && MicrosoftSigned(path) && version == TunnelQualified.value &&
        Sha256(file, DevTunnelsSha256) && CheckTunnelVersion(path, cancelled)) result.state = State::Installed;
    return result;
}
inline bool ProtocolRegistered() {
    HKEY key{};
    if (RegOpenKeyExW(HKEY_CLASSES_ROOT, L"ms-cloudpc", 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS) return false;
    auto registered = RegQueryValueExW(key, L"URL Protocol", nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS;
    RegCloseKey(key);
    if (!registered || RegOpenKeyExW(HKEY_CLASSES_ROOT, L"ms-cloudpc\\shell\\open\\command", 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS) return false;
    wchar_t command[32768]{}; DWORD bytes = sizeof(command), type{};
    registered = RegQueryValueExW(key, nullptr, nullptr, &type, reinterpret_cast<BYTE*>(command), &bytes) == ERROR_SUCCESS &&
        (type == REG_SZ || type == REG_EXPAND_SZ) && bytes > sizeof(wchar_t) && command[0];
    RegCloseKey(key);
    return registered;
}
inline Detection DetectWindowsApp() {
    UINT32 count = 0, length = 0;
    auto status = FindPackagesByPackageFamily(WindowsAppPackageFamily, PACKAGE_FILTER_HEAD, &count, nullptr, &length, nullptr, nullptr);
    if (status != ERROR_INSUFFICIENT_BUFFER || !count) return {};
    std::vector<PWSTR> names(count); std::vector<wchar_t> buffer(length);
    if (FindPackagesByPackageFamily(WindowsAppPackageFamily, PACKAGE_FILTER_HEAD, &count, names.data(), &length, buffer.data(), nullptr) != ERROR_SUCCESS) return {};
    Detection result{ State::UpdateRequired };
    for (auto name : names) {
        UINT32 bytes = 0;
        if (PackageIdFromFullName(name, 0, &bytes, nullptr) != ERROR_INSUFFICIENT_BUFFER) continue;
        std::vector<BYTE> identity(bytes);
        if (PackageIdFromFullName(name, 0, &bytes, identity.data()) != ERROR_SUCCESS) continue;
        auto package = reinterpret_cast<PACKAGE_ID*>(identity.data());
        if (package->version.Version >= WindowsAppMinimum.value && ProtocolRegistered()) result.state = State::Installed;
    }
    return result;
}
}
