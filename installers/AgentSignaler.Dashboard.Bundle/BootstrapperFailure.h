#pragma once
#include <windows.h>
#include <exception>

namespace setup {
inline unsigned NativeExitCode(HRESULT status) noexcept {
    if (SUCCEEDED(status)) return 0;
    return HRESULT_FACILITY(status) == FACILITY_WIN32 ? HRESULT_CODE(status) : static_cast<unsigned>(status);
}
enum class FailureKind { Engine, Detection };
class NativeFailure : public std::exception {
public:
    const HRESULT status;
    const unsigned outcome;
    const FailureKind kind;
    const char* what() const noexcept override { return "Native installer operation failed."; }
protected:
    NativeFailure(HRESULT hr, unsigned code, FailureKind category) noexcept
        : status(hr), outcome(code), kind(category) {
        if (SUCCEEDED(hr)) std::terminate();
    }
};
class EngineFailure final : public NativeFailure {
public:
    explicit EngineFailure(HRESULT hr) noexcept
        : NativeFailure(hr, NativeExitCode(hr), FailureKind::Engine) {}
};
class DetectionFailure final : public NativeFailure {
public:
    explicit DetectionFailure(HRESULT hr) noexcept
        : NativeFailure(hr, 5104, FailureKind::Detection) {}
};
}
