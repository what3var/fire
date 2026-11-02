/* fire native platform layer, WiFi part for a platform without WiFi that the program can control (Windows, macOS, Linux - there the operating system owns the
 * network -, a board without a package of its own): the list of interfaces is empty and every call fails with "not supported". The simulated interface `sim`
 * of the bridge works everywhere. The interface is described in fire_wifi_c.h. */
#ifndef FIRE_WIFI_NONE_H
#define FIRE_WIFI_NONE_H

#include "fire_wifi_c.h"

static int fire_wifi_supported(void) { return 0; }
static int fire_wifi_none(FireWifiStatus* st) {
    return fire_wifi_fail(st, FIRE_WIFI_E_UNSUPPORTED, "This platform has no WiFi that a program can control (the simulated interface \"sim\" works everywhere).");
}

static int fire_wifi_interfaces(FireWifiNames* names, FireWifiStatus* st) { names->count = 0; return fire_wifi_success(st); }
static int fire_wifi_init(FireWifiStatus* st) { return fire_wifi_none(st); }
static int fire_wifi_scanBegin(FireWifiStatus* st) { return fire_wifi_none(st); }
static int fire_wifi_scanStep(int* done, FireWifiNetworkList* found, FireWifiStatus* st) { (void)done; (void)found; return fire_wifi_none(st); }
static int fire_wifi_connectBegin(const char* ssid, const char* password, FireWifiStatus* st) { (void)ssid; (void)password; return fire_wifi_none(st); }
static int fire_wifi_connectState(int* state, int* code, char* message, size_t size, FireWifiStatus* st) {
    (void)code; (void)message; (void)size;
    *state = 0;
    return fire_wifi_none(st);
}
static int fire_wifi_disconnect(FireWifiStatus* st) { return fire_wifi_none(st); }
static int fire_wifi_stationInfo(FireWifiStation* info, FireWifiStatus* st) { (void)info; return fire_wifi_none(st); }
static int fire_wifi_apStart(const char* ssid, const char* password, int channel, int maxClients, FireWifiStatus* st) {
    (void)ssid; (void)password; (void)channel; (void)maxClients;
    return fire_wifi_none(st);
}
static int fire_wifi_apStop(FireWifiStatus* st) { return fire_wifi_none(st); }
static int fire_wifi_apInfo(FireWifiAp* info, FireWifiStatus* st) { (void)info; return fire_wifi_none(st); }

#endif
