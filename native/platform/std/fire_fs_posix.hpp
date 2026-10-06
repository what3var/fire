// fire native platform layer, file system part on POSIX calls (stat, opendir, mkdir, ...) - for the boards whose C library has a virtual file system
// (ESP-IDF: SPIFFS, FAT, LittleFS) and no <filesystem>. Same interface as fire_fs_std.hpp. Set FIRE_FS_ROOT (a string, e.g. "/spiffs") to make relative paths
// relative to a mounted file system: the board has no current directory worth the name.
#pragma once

#include <algorithm>
#include <dirent.h>
#include <sys/stat.h>
#include <unistd.h>
#include "fire_fs_common.hpp"

#ifndef FIRE_FS_ROOT
#define FIRE_FS_ROOT "/"
#endif

namespace fire {
namespace plat {
namespace fs {

inline bool exists(const std::string& path, bool& isDir) {
    struct stat st;
    if (::stat(path.c_str(), &st) != 0) return false;
    isDir = S_ISDIR(st.st_mode);
    return true;
}

inline std::FILE* open(const std::string& path, int mode, int access, Status& status) {
    bool isDir = false;
    bool present = exists(path, isDir);
    if (present && isDir) { status = fail(E_Permission, "Access to the path '" + path + "' is denied."); return nullptr; }
    if (mode == 2 && present) { status = fail(E_Exists, "The file '" + path + "' already exists."); return nullptr; }
    const char* how;
    switch (mode) {
        case 0: how = access == 0 ? "rb" : "r+b"; break;
        case 1: case 2: how = "w+b"; break;
        case 3:
            if (!present) { std::FILE* made = std::fopen(path.c_str(), "wb"); if (made) std::fclose(made); }
            how = access == 0 ? "rb" : "r+b";
            break;
        default: how = "ab"; break;
    }
    std::FILE* f = std::fopen(path.c_str(), how);
    if (!f) {
        int err = errno;
        size_t slash = path.find_last_of('/');
        bool parentMissing = false;
        if (slash != std::string::npos && slash > 0) { bool d; parentMissing = !exists(path.substr(0, slash), d); }
        status = fromErrno(err, path, parentMissing);
        return nullptr;
    }
    if (mode == 4) std::fseek(f, 0, SEEK_END);
    return f;
}

inline int64_t tell(std::FILE* f) { return (int64_t)ftell(f); }
inline bool seek(std::FILE* f, int64_t offset, int whence) { return fseek(f, (long)offset, whence) == 0; }
inline bool truncate(std::FILE* f, int64_t length) {
    std::fflush(f);
    return ftruncate(fileno(f), (off_t)length) == 0;
}

inline Status fileSize(const std::string& path, int64_t& size) {
    struct stat st;
    if (::stat(path.c_str(), &st) != 0) return fromErrno(errno, path);
    if (S_ISDIR(st.st_mode)) return fail(E_NoFile, "Could not find file '" + path + "'.");
    size = (int64_t)st.st_size;
    return Status();
}

inline Status fileTime(const std::string& path, int64_t& unixSeconds) {
    struct stat st;
    if (::stat(path.c_str(), &st) != 0) return fromErrno(errno, path);
    unixSeconds = (int64_t)st.st_mtime;
    return Status();
}

inline Status removeFile(const std::string& path) {
    bool isDir = false;
    if (!exists(path, isDir)) return Status();
    if (isDir) return fail(E_Permission, "Access to the path '" + path + "' is denied.");
    if (::unlink(path.c_str()) != 0) return fromErrno(errno, path);
    return Status();
}

inline Status copyFile(const std::string& from, const std::string& to, bool overwrite) {
    bool isDir = false;
    if (!exists(from, isDir) || isDir) return fail(E_NoFile, "Could not find file '" + from + "'.");
    if (!overwrite && exists(to, isDir)) return fail(E_Exists, "The file '" + to + "' already exists.");
    std::FILE* in = std::fopen(from.c_str(), "rb");
    if (!in) return fromErrno(errno, from);
    std::FILE* out = std::fopen(to.c_str(), "wb");
    if (!out) { int err = errno; std::fclose(in); return fromErrno(err, to); }
    char buffer[512];
    size_t n;
    Status result;
    while ((n = std::fread(buffer, 1, sizeof buffer, in)) > 0)
        if (std::fwrite(buffer, 1, n, out) != n) { result = fail(E_Other, "I/O error while writing '" + to + "'."); break; }
    std::fclose(in);
    std::fclose(out);
    return result;
}

inline Status moveFile(const std::string& from, const std::string& to, bool overwrite) {
    bool isDir = false;
    if (!exists(from, isDir)) return fail(E_NoFile, "Could not find file '" + from + "'.");
    if (exists(to, isDir)) {
        if (!overwrite) return fail(E_Exists, "The file '" + to + "' already exists.");
        if (!isDir) ::unlink(to.c_str());
    }
    if (::rename(from.c_str(), to.c_str()) != 0) return fromErrno(errno, from);
    return Status();
}

inline Status makeDirs(const std::string& path) {
    std::string current;
    size_t i = 0;
    while (i <= path.size()) {
        size_t next = path.find('/', i);
        if (next == std::string::npos) next = path.size();
        current = path.substr(0, next);
        i = next + 1;
        if (current.empty()) continue;
        bool isDir = false;
        if (exists(current, isDir)) { if (!isDir) return fail(E_Other, "'" + current + "' is a file."); continue; }
        if (::mkdir(current.c_str(), 0777) != 0 && errno != EEXIST) return fromErrno(errno, current);
    }
    return Status();
}

inline Status removeDir(const std::string& path, bool recursive) {
    bool isDir = false;
    if (!exists(path, isDir) || !isDir) return fail(E_NoDir, "Could not find a part of the path '" + path + "'.");
    if (recursive) {
        DIR* d = ::opendir(path.c_str());
        if (d) {
            std::vector<std::string> names;
            while (struct dirent* e = ::readdir(d)) {
                std::string name = e->d_name;
                if (name != "." && name != "..") names.push_back(name);
            }
            ::closedir(d);
            for (const std::string& name : names) {
                std::string child = path + "/" + name;
                bool childIsDir = false;
                if (!exists(child, childIsDir)) continue;
                Status s = childIsDir ? removeDir(child, true) : removeFile(child);
                if (!s.ok()) return s;
            }
        }
    }
    if (::rmdir(path.c_str()) != 0) return errno == ENOTEMPTY ? fail(E_Other, "The directory '" + path + "' is not empty.") : fromErrno(errno, path);
    return Status();
}

inline Status listInto(const std::string& path, const std::string& pattern, bool recursive, bool directories, std::vector<std::string>& out) {
    DIR* d = ::opendir(path.c_str());
    if (!d) return fromErrno(errno, path);
    std::vector<std::string> names;
    while (struct dirent* e = ::readdir(d)) {
        std::string name = e->d_name;
        if (name != "." && name != "..") names.push_back(name);
    }
    ::closedir(d);
    for (const std::string& name : names) {
        std::string child = (path.empty() || path.back() == '/') ? path + name : path + "/" + name;
        bool isDir = false;
        if (!exists(child, isDir)) continue;
        if (isDir == directories && globMatch(pattern, name)) out.push_back(child);
        if (isDir && recursive) { Status s = listInto(child, pattern, true, directories, out); if (!s.ok()) return s; }
    }
    return Status();
}

inline Status list(const std::string& path, const std::string& pattern, bool recursive, bool directories, std::vector<std::string>& out) {
    bool isDir = false;
    if (!exists(path, isDir) || !isDir) return fail(E_NoDir, "Could not find a part of the path '" + path + "'.");
    Status s = listInto(path, pattern, recursive, directories, out);
    std::sort(out.begin(), out.end());
    return s;
}

inline std::string currentDir() {
    char buffer[256];
    if (::getcwd(buffer, sizeof buffer)) return buffer;
    return FIRE_FS_ROOT;
}

inline std::string tempDir() {
    return std::string(FIRE_FS_ROOT) + (std::string(FIRE_FS_ROOT).back() == '/' ? "" : "/") + "tmp/";
}

/// An absolute path with `.` and `..` resolved and repeated separators collapsed.
inline std::string fullPath(const std::string& path) {
    std::string full = (!path.empty() && path[0] == '/') ? path : currentDir() + (currentDir().back() == '/' ? "" : "/") + path;
    std::vector<std::string> parts;
    size_t i = 0;
    while (i <= full.size()) {
        size_t next = full.find('/', i);
        if (next == std::string::npos) next = full.size();
        std::string part = full.substr(i, next - i);
        i = next + 1;
        if (part.empty() || part == ".") continue;
        if (part == "..") { if (!parts.empty()) parts.pop_back(); continue; }
        parts.push_back(part);
    }
    std::string result = "/";
    for (size_t k = 0; k < parts.size(); k++) { result += parts[k]; if (k + 1 < parts.size()) result += "/"; }
    if (!path.empty() && path.back() == '/' && result.size() > 1) result += "/";
    return result;
}

}  // namespace fs
}  // namespace plat
}  // namespace fire
