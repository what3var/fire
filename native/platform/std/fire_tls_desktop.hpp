// fire native platform layer, TLS part for the desktop platforms: SChannel on Windows (nothing to install), OpenSSL elsewhere (needs its development files; on Windows with FIRE_TLS_OPENSSL), mbedTLS with FIRE_TLS_MBEDTLS, nothing with FIRE_NO_TLS.
#pragma once

#if defined(FIRE_NO_TLS)
#include "fire_tls_none.hpp"
#elif defined(FIRE_TLS_MBEDTLS)
#include "fire_tls_mbedtls.hpp"
#elif defined(_WIN32) && !defined(FIRE_TLS_OPENSSL)
#include "fire_tls_schannel.hpp"   // Windows: its own TLS (SChannel) and its own certificate store; define FIRE_TLS_OPENSSL to use OpenSSL there
#elif defined(__has_include)
#if __has_include(<openssl/ssl.h>)
#include "fire_tls_openssl.hpp"
#else
#error "TLS needs the development files of OpenSSL (Debian/Ubuntu: libssl-dev, Fedora: openssl-devel, macOS: brew install openssl and add its include and lib folders to the toolchain, Windows needs none: it uses SChannel) - or define FIRE_TLS_MBEDTLS (mbedTLS) or FIRE_NO_TLS in the target configuration."
#endif
#else
#include "fire_tls_openssl.hpp"
#endif
