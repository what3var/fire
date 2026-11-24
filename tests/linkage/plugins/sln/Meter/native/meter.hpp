#include <string>
namespace fire {
static int mt_count = 0;
inline Value mt_twice(Value a) { mt_count++; return Int(a.i * 2); }
inline Value mt_label(Value a, OwnList* list) {
    std::string text = "n=" + std::to_string((long long)a.i);
    Str* s = allocStr((uint32_t)text.size(), list);
    for (size_t i = 0; i < text.size(); i++) strChars(s)[i] = (fire_char16)text[i];
    return StrV(s);
}
inline Value mt_calls() { return Int(mt_count); }
inline Value mt_check(Value a) { if (a.i < 0) return fireError("ArgumentException", "negative in C++"); return a; }
}
