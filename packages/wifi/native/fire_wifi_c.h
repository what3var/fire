/* fire wifi package: WiFi part in C (the HAL of the wifi bridge): what the implementations share - the error codes of the language, the status of an
 * operation, a found network and a list of them.
 *
 * The wifi package brings, one file per platform (platformSources of wifi.fireproj),, all texts in UTF-8:
 *
 *   FIRE_WIFI_E_*                  the codes of the error slot (the same numbers as in the prelude: WiFi.WiFiException.code)
 *   FireWifiStatus { int code; char message[176]; }     code 0 = ok
 *   FireWifiNetwork { ssid, ssidLength, bssid, rssi, channel, auth }   auth: 0 open, 1 WEP, 2 WPA, 3 WPA2, 4 WPA3, 5 other
 *   int  fire_wifi_supported(void)                      0: there is no WiFi here (the list of interfaces is empty)
 *   int  fire_wifi_interfaces(FireWifiNames*, st)        the WiFi interfaces ("wifi" on the ESP32)
 *   int  fire_wifi_init(st)                              starts the WiFi driver (once; later calls do nothing)
 *   int  fire_wifi_scanBegin(st) / fire_wifi_scanStep(int* done, FireWifiNetworkList* found, st)
 *                                                        a scan takes seconds: begin starts it, step asks (without waiting) and gives the networks when it is done
 *   int  fire_wifi_connectBegin(ssid, password, st)      starts connecting the station (an empty password: an open network)
 *   int  fire_wifi_connectState(int* state, int* code, char* message, size_t size, st)
 *                                                        state 0 idle, 1 connecting, 2 connected (the address is there), 3 failed (code: the error, message: what happened)
 *   int  fire_wifi_disconnect(st)
 *   int  fire_wifi_stationInfo(FireWifiStation*, st)     FIRE_WIFI_E_NOT_CONNECTED when the station has no connection (mac is always given)
 *   int  fire_wifi_apStart(ssid, password, int channel, int maxClients, st) / fire_wifi_apStop(st) / fire_wifi_apInfo(FireWifiAp*, st)
 *                                                        the access point (an empty password: open)
 * The functions that return int give 1 when done and 0 on an error (the status says why). Nothing waits: the fire code asks again and again and sleeps in
 * between, so the program stays abortable. */
#ifndef FIRE_WIFI_C_H
#define FIRE_WIFI_C_H

#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { FIRE_WIFI_E_NONE = 0, FIRE_WIFI_E_INVALID_ARGUMENT = 1, FIRE_WIFI_E_INVALID_HANDLE = 2, FIRE_WIFI_E_NOT_FOUND = 3, FIRE_WIFI_E_BUSY = 4,
       FIRE_WIFI_E_PERMISSION = 5, FIRE_WIFI_E_UNSUPPORTED = 6, FIRE_WIFI_E_OTHER = 7, FIRE_WIFI_E_AUTH = 8, FIRE_WIFI_E_TIMEOUT = 9, FIRE_WIFI_E_NOT_CONNECTED = 10 };

typedef struct FireWifiStatus { int code; char message[176]; } FireWifiStatus;

/* Records a failure (printf format): always 0. */
static int fire_wifi_fail(FireWifiStatus* st, int code, const char* format, ...) {
    va_list args;
    st->code = code;
    va_start(args, format);
    vsnprintf(st->message, sizeof st->message, format, args);
    va_end(args);
    return 0;
}
static int fire_wifi_success(FireWifiStatus* st) { st->code = 0; st->message[0] = 0; return 1; }

/* A found network: the name (up to 32 bytes; `ssidLength` of them), the address of the access point ("02:00:...") and its numbers. */
typedef struct FireWifiNetwork {
    char ssid[33];
    int ssidLength;
    char bssid[24];
    int rssi;
    int channel;
    int auth;
} FireWifiNetwork;
typedef struct FireWifiNetworkList { FireWifiNetwork* items; size_t count, cap; } FireWifiNetworkList;

/* Adds a copy of `n`: 0 when out of memory. */
static int fire_wifi_networkListAdd(FireWifiNetworkList* l, const FireWifiNetwork* n) {
    if (l->count == l->cap) {
        size_t cap = l->cap ? l->cap * 2 : 8;
        FireWifiNetwork* grown = (FireWifiNetwork*)realloc(l->items, sizeof(FireWifiNetwork) * cap);
        if (grown == NULL) return 0;
        l->items = grown;
        l->cap = cap;
    }
    l->items[l->count++] = *n;
    return 1;
}
static void fire_wifi_networkListFree(FireWifiNetworkList* l) {
    free(l->items);
    l->items = NULL;
    l->count = l->cap = 0;
}

/* The names of the WiFi interfaces of the hardware. */
#define FIRE_WIFI_MAX_INTERFACES 8
typedef struct FireWifiNames { int count; char names[FIRE_WIFI_MAX_INTERFACES][32]; } FireWifiNames;

/* What the station is joined to ("" when nothing) and its own address. */
typedef struct FireWifiStation { char ssid[33]; char ip[48]; char mac[24]; int rssi; } FireWifiStation;
/* The access point: its address ("" when it is not running) and how many stations are joined. */
typedef struct FireWifiAp { char ip[48]; char mac[24]; int clients; } FireWifiAp;

#endif
