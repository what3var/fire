// fire native platform package "freertos": no TLS (a board package of your own provides plat::tls, see ../std/fire_tls_mbedtls.hpp for a version on mbedTLS and ../std/fire_tls_none.hpp).
#pragma once
#include "../std/fire_tls_none.hpp"
