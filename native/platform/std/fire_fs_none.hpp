// fire native platform layer, file system part for a board without a file system: every file operation fails with "not supported" (the console - IO.Stdio -
// and memory streams still work). Select it with FIRE_NO_FS in the target configuration (`defines`).
#pragma once

#include "fire_fs_common.hpp"

namespace fire {
namespace plat {
namespace fs {

inline Status none() { return fail(E_Unsupported, "This platform has no file system."); }

inline bool exists(const std::string&, bool&) { return false; }
inline std::FILE* open(const std::string&, int, int, Status& status) { status = none(); return nullptr; }
inline int64_t tell(std::FILE*) { return -1; }
inline bool seek(std::FILE*, int64_t, int) { return false; }
inline bool truncate(std::FILE*, int64_t) { return false; }
inline Status fileSize(const std::string&, int64_t&) { return none(); }
inline Status fileTime(const std::string&, int64_t&) { return none(); }
inline Status removeFile(const std::string&) { return none(); }
inline Status copyFile(const std::string&, const std::string&, bool) { return none(); }
inline Status moveFile(const std::string&, const std::string&, bool) { return none(); }
inline Status makeDirs(const std::string&) { return none(); }
inline Status removeDir(const std::string&, bool) { return none(); }
inline Status list(const std::string&, const std::string&, bool, bool, std::vector<std::string>&) { return none(); }
inline std::string currentDir() { return "/"; }
inline std::string tempDir() { return "/tmp/"; }
inline std::string fullPath(const std::string& path) { return path; }

}  // namespace fs
}  // namespace plat
}  // namespace fire
