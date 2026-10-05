// fire native platform layer, file system part (HAL for the IO bridge) - the portable implementation on <filesystem> and C stdio, for the hosted platforms.
//
// A platform package offers `fire_fs.hpp` (the generated file defines FIRE_PLATFORM_FS_HEADER for it) that provides, in fire::plat::fs:
//
//   struct Status { int code; std::string message; }   code: the IO error code of the language (0 ok, see IoErr), message for the exception
//   bool  exists(path, bool& isDir)                    a file or directory is there
//   std::FILE* open(path, mode, access, Status&)       FileMode 0 Open, 1 Create, 2 CreateNew, 3 OpenOrCreate, 4 Append; FileAccess 0 Read, 1 Write, 2 ReadWrite
//   int64_t tell(FILE*) / bool seek(FILE*, int64_t, whence) / bool truncate(FILE*, int64_t)    64-bit positions
//   Status fileSize / fileTime (unix seconds) / removeFile / copyFile / moveFile
//   Status makeDirs / removeDir(path, recursive) / list(path, pattern, recursive, directories, out)
//   std::string currentDir() / tempDir() / fullPath(path) / separator()
//
// A board without a file system (or with its own: SPIFFS, FAT) provides its own `fire_fs.hpp` - the bridge only uses these functions.
#pragma once

#include <algorithm>
#include <chrono>
#include <filesystem>
#include "fire_fs_common.hpp"
#ifdef _WIN32
#include <io.h>
#else
#include <unistd.h>
#endif

namespace fire {
namespace plat {
namespace fs {

namespace stdfs = std::filesystem;

inline bool exists(const std::string& path, bool& isDir) {
    std::error_code ec;
    auto st = stdfs::status(stdfs::u8path(path), ec);
    if (ec || !stdfs::exists(st)) return false;
    isDir = stdfs::is_directory(st);
    return true;
}

inline std::FILE* open(const std::string& path, int mode, int access, Status& status) {
    bool isDir = false;
    bool present = exists(path, isDir);
    if (present && isDir) { status = fail(E_Permission, "Access to the path '" + path + "' is denied."); return nullptr; }
    if (mode == 2 && present) { status = fail(E_Exists, "The file '" + path + "' already exists."); return nullptr; }
    const char* how;
    switch (mode) {
        case 0: how = access == 0 ? "rb" : "r+b"; break;                           // Open: must exist
        case 1: case 2: how = "w+b"; break;                                        // Create / CreateNew
        case 3:
            if (!present) { std::FILE* made = std::fopen(path.c_str(), "wb"); if (made) std::fclose(made); }   // OpenOrCreate
            how = access == 0 ? "rb" : "r+b";
            break;
        default: how = "ab"; break;                                                // Append
    }
    std::FILE* f = std::fopen(path.c_str(), how);
    if (!f) {
        int err = errno;
        bool parentMissing = false;
        stdfs::path parent = stdfs::u8path(path).parent_path();
        if (!parent.empty()) { bool d; parentMissing = !exists(parent.u8string(), d); }
        status = fromErrno(err, path, parentMissing);
        return nullptr;
    }
    if (mode == 4) std::fseek(f, 0, SEEK_END);
    return f;
}

inline int64_t tell(std::FILE* f) {
#ifdef _WIN32
    return _ftelli64(f);
#else
    return (int64_t)ftello(f);
#endif
}
inline bool seek(std::FILE* f, int64_t offset, int whence) {
#ifdef _WIN32
    return _fseeki64(f, offset, whence) == 0;
#else
    return fseeko(f, (off_t)offset, whence) == 0;
#endif
}
inline bool truncate(std::FILE* f, int64_t length) {
    std::fflush(f);
#ifdef _WIN32
    return _chsize_s(_fileno(f), length) == 0;
#else
    return ftruncate(fileno(f), (off_t)length) == 0;
#endif
}

inline Status fileSize(const std::string& path, int64_t& size) {
    std::error_code ec;
    auto n = stdfs::file_size(stdfs::u8path(path), ec);
    if (ec) return fromCode(ec, path);
    size = (int64_t)n;
    return Status();
}

inline Status fileTime(const std::string& path, int64_t& unixSeconds) {
    std::error_code ec;
    auto t = stdfs::last_write_time(stdfs::u8path(path), ec);
    if (ec) return fromCode(ec, path);
    auto system = std::chrono::time_point_cast<std::chrono::system_clock::duration>(t - stdfs::file_time_type::clock::now() + std::chrono::system_clock::now());
    unixSeconds = (int64_t)std::chrono::duration_cast<std::chrono::seconds>(system.time_since_epoch()).count();
    return Status();
}

/// A file that is not there is not an error.
inline Status removeFile(const std::string& path) {
    bool isDir = false;
    if (!exists(path, isDir)) return Status();
    if (isDir) return fail(E_Permission, "Access to the path '" + path + "' is denied.");
    std::error_code ec;
    stdfs::remove(stdfs::u8path(path), ec);
    return fromCode(ec, path);
}

inline Status copyFile(const std::string& from, const std::string& to, bool overwrite) {
    std::error_code ec;
    stdfs::copy_file(stdfs::u8path(from), stdfs::u8path(to), overwrite ? stdfs::copy_options::overwrite_existing : stdfs::copy_options::none, ec);
    if (ec) return fromCode(ec, ec == std::errc::no_such_file_or_directory ? from : to);
    return Status();
}

inline Status moveFile(const std::string& from, const std::string& to, bool overwrite) {
    std::error_code ec;
    bool isDir = false;
    if (!exists(from, isDir)) return fail(E_NoFile, "Could not find file '" + from + "'.");
    if (overwrite && exists(to, isDir) && !isDir) stdfs::remove(stdfs::u8path(to), ec);
    ec.clear();
    stdfs::rename(stdfs::u8path(from), stdfs::u8path(to), ec);
    if (ec) {
        std::error_code ec2;   // another file system: copy, then delete
        stdfs::copy_file(stdfs::u8path(from), stdfs::u8path(to), stdfs::copy_options::overwrite_existing, ec2);
        if (ec2) return fromCode(ec2, to);
        stdfs::remove(stdfs::u8path(from), ec2);
    }
    return Status();
}

/// Also makes the missing directories between; an existing one is not an error.
inline Status makeDirs(const std::string& path) {
    std::error_code ec;
    stdfs::create_directories(stdfs::u8path(path), ec);
    if (ec) return fromCode(ec, path);
    return Status();
}

inline Status removeDir(const std::string& path, bool recursive) {
    bool isDir = false;
    if (!exists(path, isDir) || !isDir) return fail(E_NoDir, "Could not find a part of the path '" + path + "'.");
    std::error_code ec;
    if (recursive) stdfs::remove_all(stdfs::u8path(path), ec);
    else stdfs::remove(stdfs::u8path(path), ec);
    if (ec) return ec == std::errc::directory_not_empty ? fail(E_Other, "The directory '" + path + "' is not empty.") : fromCode(ec, path);
    return Status();
}

/// The files (or, with `directories`, the directories) of a directory with their full paths, sorted by bytes.
inline Status list(const std::string& path, const std::string& pattern, bool recursive, bool directories, std::vector<std::string>& out) {
    bool isDir = false;
    if (!exists(path, isDir) || !isDir) return fail(E_NoDir, "Could not find a part of the path '" + path + "'.");
    std::error_code ec;
    auto consider = [&](const stdfs::directory_entry& entry) {
        std::error_code e;
        bool entryIsDir = entry.is_directory(e);
        if (entryIsDir != directories) return;
        if (!directories && !entry.is_regular_file(e)) return;
        if (!globMatch(pattern, entry.path().filename().u8string())) return;
        out.push_back(entry.path().u8string());
    };
    if (recursive) { for (stdfs::recursive_directory_iterator it(stdfs::u8path(path), stdfs::directory_options::skip_permission_denied, ec), end; !ec && it != end; it.increment(ec)) consider(*it); }
    else { for (stdfs::directory_iterator it(stdfs::u8path(path), stdfs::directory_options::skip_permission_denied, ec), end; !ec && it != end; it.increment(ec)) consider(*it); }
    if (ec) return fromCode(ec, path);
    std::sort(out.begin(), out.end());
    return Status();
}

inline std::string currentDir() {
    std::error_code ec;
    auto p = stdfs::current_path(ec);
    return ec ? std::string() : p.u8string();
}

/// With a trailing separator (like Path.GetTempPath).
inline std::string tempDir() {
    std::error_code ec;
    std::string p = stdfs::temp_directory_path(ec).u8string();
    if (p.empty()) return p;
    if (p.back() != '/' && p.back() != '\\') p += separator();
    return p;
}

/// Path.GetFullPath: an absolute path with `.` and `..` resolved and repeated separators collapsed (no symbolic links resolved).
inline std::string fullPath(const std::string& path) {
    std::error_code ec;
    stdfs::path p = stdfs::absolute(stdfs::u8path(path), ec);
    if (ec) return path;
    std::string text = p.lexically_normal().u8string();
    bool trailing = !path.empty() && (path.back() == '/' || path.back() == '\\');
    if (!trailing && text.size() > 1 && (text.back() == '/' || text.back() == '\\')) text.pop_back();   // lexically_normal keeps a "." directory as a trailing separator
    return text;
}

}  // namespace fs
}  // namespace plat
}  // namespace fire
