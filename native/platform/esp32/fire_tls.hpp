// fire native platform package "esp32": TLS for the tls bridge on the mbedTLS of ESP-IDF (add the component `mbedtls` and, for the certificate bundle, `esp-tls` / `esp_crt_bundle`
// to the project; to verify servers without giving CA certificates define in the target configuration: includes "esp_crt_bundle.h" and
// `FIRE_TLS_CA_ATTACH(conf)` as `esp_crt_bundle_attach(conf)`). (Not tried on a board yet.)
#pragma once
#include "../std/fire_tls_mbedtls.hpp"
