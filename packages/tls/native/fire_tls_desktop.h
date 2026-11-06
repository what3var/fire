/* fire tls package: TLS part in C for the desktop platforms: SChannel on Windows (nothing to install), OpenSSL elsewhere (needs its development files; on
 * Windows with FIRE_TLS_OPENSSL), mbedTLS with FIRE_TLS_MBEDTLS, nothing with FIRE_NO_TLS. */
#ifndef FIRE_TLS_DESKTOP_H
#define FIRE_TLS_DESKTOP_H

#if defined(FIRE_NO_TLS)
#define FIRE_TLS_USE_NONE 1
#elif defined(FIRE_TLS_MBEDTLS)
#define FIRE_TLS_USE_MBEDTLS 1
#elif defined(_WIN32) && !defined(FIRE_TLS_OPENSSL)
#define FIRE_TLS_USE_SCHANNEL 1   /* Windows: its own TLS (SChannel) and its own certificate store; define FIRE_TLS_OPENSSL to use OpenSSL there */
#elif defined(__has_include)
#if __has_include(<openssl/ssl.h>)
#define FIRE_TLS_USE_OPENSSL 1
#else
#error "TLS needs the development files of OpenSSL (Debian/Ubuntu: libssl-dev, Fedora: openssl-devel, macOS: brew install openssl and add its include and lib folders to the toolchain, Windows needs none: it uses SChannel) - or define FIRE_TLS_MBEDTLS (mbedTLS) or FIRE_NO_TLS in the target configuration."
#endif
#else
#define FIRE_TLS_USE_OPENSSL 1
#endif

#endif
