// The natives of $name$: C++ functions on Value, in namespace fire (docs/PACKAGE_NATIVES.md).
// Every `inline Value name(Value a, ...)` below becomes the fire function `__name` that the prelude wraps.
#pragma once
#include <cstdint>

namespace fire {

// $identlower$_add(a, b) -> int
inline Value $identlower$_add(Value a, Value b) {
    return Int(a.i + b.i);
}

}  // namespace fire
