/* fire native platform package "wasm": no TLS (a board package of your own provides fire_tls.h, see ../std/fire_tls_c.h; ../std/fire_tls_mbedtls.h is a version on mbedTLS). */
#ifndef FIRE_PLATFORM_TLS_H
#define FIRE_PLATFORM_TLS_H
#include "../std/fire_tls_none.h"
#endif
