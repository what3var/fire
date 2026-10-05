// fire native platform layer, file system part: what the implementations of the HAL share (the error codes of the language, statuses, glob matching).
#pragma once

#include <cerrno>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <system_error>
#include <vector>

namespace fire {
namespace plat {
namespace fs {

/// The error codes of the language (IoError in the IO bridge).
enum IoErr { E_None = 0, E_Argument = 1, E_Handle = 2, E_NoFile = 3, E_NoDir = 4, E_Permission = 5, E_Denied = 6, E_Exists = 7, E_Unsupported = 8, E_Other = 9 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }

/// An error of the operating system as an error of the language.
inline Status fromErrno(int err, const std::string& path, bool parentMissing = false) {
    switch (err) {
        case ENOENT: return parentMissing ? fail(E_NoDir, "Could not find a part of the path '" + path + "'.") : fail(E_NoFile, "Could not find file '" + path + "'.");
        case ENOTDIR: return fail(E_NoDir, "Could not find a part of the path '" + path + "'.");
        case EACCES: case EPERM: case EROFS: case EISDIR: return fail(E_Permission, "Access to the path '" + path + "' is denied.");
        case EEXIST: return fail(E_Exists, "The file '" + path + "' already exists.");
        default: return fail(E_Other, "I/O error on '" + path + "': " + std::strerror(err));
    }
}
inline Status fromCode(const std::error_code& ec, const std::string& path) {
    if (!ec) return Status();
    return fromErrno(ec.value(), path);
}

/// `*` and `?` against the name (like Directory.EnumerateFiles; `*.*` and `*` match everything).
inline bool globMatch(const std::string& pattern, const std::string& name) {
    if (pattern == "*" || pattern == "*.*" || pattern.empty()) return true;
    size_t p = 0, n = 0, star = std::string::npos, mark = 0;
    while (n < name.size()) {
        if (p < pattern.size() && (pattern[p] == '?' || pattern[p] == name[n])) { p++; n++; }
        else if (p < pattern.size() && pattern[p] == '*') { star = p++; mark = n; }
        else if (star != std::string::npos) { p = star + 1; n = ++mark; }
        else return false;
    }
    while (p < pattern.size() && pattern[p] == '*') p++;
    return p == pattern.size();
}

inline std::string separator() {
#ifdef _WIN32
    return "\\";
#else
    return "/";
#endif
}


}  // namespace fs
}  // namespace plat
}  // namespace fire
