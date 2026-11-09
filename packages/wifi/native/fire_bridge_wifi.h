/* fire native bridge "wifi" (C): the natives behind `#import "wifi"` (docs/networking/README.md, "WiFi"): scan for networks, join one as a station, be an access point. The
 * fire side (WiFi.Station, WiFi.AccessPoint, WiFi.Board, WiFi.Sim - fire source) is the same in the VM and in a native build; this file is what its `__WiFi...`
 * functions do.
 *
 * This file is the one implementation of the wifi natives, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual
 * machine runs it in a shared library built from it (native/abi/fire_pkg_abi.h). The radio comes with the package (
 * fire_wifi_*; one file per platform, platformSources of wifi.fireproj, see fire_wifi_c.h): the WiFi driver of ESP-IDF on an ESP32. Everywhere else the operating system owns the network and the list of
 * interfaces is empty - but one interface is always there:
 *
 *   "sim"   a simulated radio with the networks that the program (or a test) puts in the air: `SimAddNetwork`. A scan finds them, a join succeeds with the right
 *           password (and fails with NotFound/Auth otherwise), and the station gets an address. Joining and scanning take a few questions to answer (like the
 *           real thing takes seconds), so the code that waits is really tried. For tests, and to develop a program on a PC that later runs on the board. (It is
 *           no network: the sockets of the net package use the network of the PC.)
 *
 * What a program does with the radio is asked in steps, nothing waits: `ScanBegin`/`ScanStep`, `ConnectBegin`/`ConnectState` - the fire code asks again and
 * again and sleeps in between, so the program stays abortable. An interface is an integer handle in a table of this bridge (index 0 is not used; handles are
 * not used again). Every function reports an error the same way: a result of -1/false/undefined and the code and message of this thread (`__WiFiLastError`,
 * `__WiFiLastErrorMessage`, the slot of the io bridge), from which the fire code throws a typed exception. */
#ifndef FIRE_BRIDGE_WIFI_H
#define FIRE_BRIDGE_WIFI_H

#include <stdarg.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "fire_wifi_c.h"

enum { FIRE_WIFIB_IDLE = 0, FIRE_WIFIB_CONNECTING = 1, FIRE_WIFIB_CONNECTED = 2, FIRE_WIFIB_FAILED = 3 };

/* Records an error of this thread (printf format): always -1. */
static int64_t fire_wifib_error(int code, const char* format, ...) {
    va_list args;
    g_ioError.code = code;
    va_start(args, format);
    vsnprintf(g_ioError.message, sizeof g_ioError.message, format, args);
    va_end(args);
    return -1;
}
static void fire_wifib_ok(void) { g_ioError.code = 0; g_ioError.message[0] = 0; }
static int64_t fire_wifib_errorStatus(const FireWifiStatus* s) { return fire_wifib_error(s->code, "%s", s->message); }

/* ---- text ----------------------------------------------------------------------------------------------------------------------------------- */
/* A text of this bridge: `n` bytes of UTF-8 (malloc, zero terminated; NULL and 0 when empty). */
typedef struct FireWifibText { char* s; size_t n; } FireWifibText;

/* A fire string as UTF-8 (malloc; the caller frees it; NULL when out of memory); a lone surrogate becomes U+FFFD. */
static int fire_wifib_utf8(Value v, FireWifibText* out) {
    const Str* s = strOf(v);
    size_t n = 0;
    uint32_t i;
    out->s = (char*)malloc((size_t)s->length * 4 + 1);
    out->n = 0;
    if (out->s == NULL) return 0;
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out->s[n++] = (char)c;
        else if (c < 0x800) { out->s[n++] = (char)(0xC0 | (c >> 6)); out->s[n++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { out->s[n++] = (char)(0xE0 | (c >> 12)); out->s[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out->s[n++] = (char)(0x80 | (c & 0x3F)); }
        else { out->s[n++] = (char)(0xF0 | (c >> 18)); out->s[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); out->s[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out->s[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    out->s[n] = 0;
    out->n = n;
    return 1;
}
static void fire_wifib_textFree(FireWifibText* t) { free(t->s); t->s = NULL; t->n = 0; }
/* `t` becomes a copy of `n` bytes at `s`: 0 when out of memory (`t` is then empty). */
static int fire_wifib_textSet(FireWifibText* t, const char* s, size_t n) {
    char* copy = (char*)malloc(n + 1);
    fire_wifib_textFree(t);
    if (copy == NULL) return 0;
    if (n > 0) memcpy(copy, s, n);
    copy[n] = 0;
    t->s = copy;
    t->n = n;
    return 1;
}
static int fire_wifib_textEquals(const FireWifibText* a, const char* s, size_t n) { return a->n == n && (n == 0 || memcmp(a->s, s, n) == 0); }
/* (a byte above 127 becomes the Latin-1 character of the same number) */
static Value fire_wifib_str8(const char* text, size_t n, OwnList* list) {
    Str* s = allocStr((uint32_t)n, list);
    size_t i;
    for (i = 0; i < n; i++) strChars(s)[i] = (fire_char16)(uint8_t)text[i];
    return StrV(s);
}
static Value fire_wifib_cstr8(const char* text, OwnList* list) { return fire_wifib_str8(text, strlen(text), list); }

/* ---- the simulated radio -------------------------------------------------------------------------------------------------------------------- */
typedef struct FireWifibSimNetwork {
    FireWifibText ssid;
    FireWifibText password;
    int rssi;
    int channel;
} FireWifibSimNetwork;
typedef struct FireWifibSim {
    FireWifibSimNetwork* networks;
    size_t networkCount, networkCap;
    int connectPolls;        /* how many questions a join takes to answer */
    int scanPolls;           /* ... and a scan */
    /* station */
    int state;
    int failCode;
    char failMessage[176];
    FireWifibText target, targetPassword;
    int pollsLeft;
    FireWifibText ssid;
    int rssi;
    /* scan */
    int scanning;
    int scanLeft;
    /* access point */
    int apRunning;
    int apChannel, apMax, apClients;
} FireWifibSim;
static FireWifibSim g_wifiSim = {NULL, 0, 0, 2, 1, FIRE_WIFIB_IDLE, 0, {0}, {NULL, 0}, {NULL, 0}, 0, {NULL, 0}, 0, 0, 0, 0, 1, 4, 0};

static FireWifibSimNetwork* fire_wifib_simFind(const char* name, size_t n) {
    size_t i;
    for (i = 0; i < g_wifiSim.networkCount; i++) if (fire_wifib_textEquals(&g_wifiSim.networks[i].ssid, name, n)) return &g_wifiSim.networks[i];
    return NULL;
}
/* Takes the simulated radio back to the start: no networks, the default delays, nothing joined, no access point. */
static void fire_wifib_simReset(void) {
    FireWifibSim* m = &g_wifiSim;
    size_t i;
    for (i = 0; i < m->networkCount; i++) { fire_wifib_textFree(&m->networks[i].ssid); fire_wifib_textFree(&m->networks[i].password); }
    free(m->networks);
    fire_wifib_textFree(&m->target);
    fire_wifib_textFree(&m->targetPassword);
    fire_wifib_textFree(&m->ssid);
    memset(m, 0, sizeof *m);
    m->connectPolls = 2;
    m->scanPolls = 1;
    m->state = FIRE_WIFIB_IDLE;
    m->apChannel = 1;
    m->apMax = 4;
}
static const char* const kWifiSimStationIp = "192.168.1.50";
static const char* const kWifiSimStationMac = "02:00:00:00:00:01";
static const char* const kWifiSimApIp = "192.168.4.1";
static const char* const kWifiSimApMac = "02:00:00:00:00:02";

/* ---- the interfaces ------------------------------------------------------------------------------------------------------------------------- */
typedef struct FireWifibIface {
    int isSim;
    FireWifiNetworkList found;    /* the networks of the last finished scan */
} FireWifibIface;
static void fire_wifib_release(FireWifibIface* i) {
    if (!i) return;
    fire_wifi_networkListFree(&i->found);
    free(i);
}

/* The open interfaces; the handle is the index (0 is not used). */
static FireWifibIface** g_wifiIfaces = NULL;
static size_t g_wifiIfaceCount = 1, g_wifiIfaceCap = 0;

/* The program has ended (the library for the VM stays loaded for the next one): drops the handles, resets the simulated radio and (on a board) lets go of the
 * connection and the access point. */
static void fire_wifi_reset(void) {
    int hardware = 0;
    size_t i;
    for (i = 1; i < g_wifiIfaceCount; i++) {
        if (g_wifiIfaces[i] && !g_wifiIfaces[i]->isSim) hardware = 1;
        fire_wifib_release(g_wifiIfaces[i]);
        g_wifiIfaces[i] = NULL;
    }
    if (hardware) {
        FireWifiStatus st;
        fire_wifi_disconnect(&st);
        fire_wifi_apStop(&st);
    }
    fire_wifib_simReset();
}

static int64_t fire_wifib_add(FireWifibIface* i) {
    if (g_wifiIfaceCount >= g_wifiIfaceCap) {
        size_t cap = g_wifiIfaceCap ? g_wifiIfaceCap * 2 : 16;
        FireWifibIface** grown = (FireWifibIface**)realloc(g_wifiIfaces, sizeof(FireWifibIface*) * cap);
        if (grown == NULL) { fire_wifib_release(i); return fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory."); }
        if (g_wifiIfaceCap == 0) grown[0] = NULL;
        g_wifiIfaces = grown;
        g_wifiIfaceCap = cap;
    }
    g_wifiIfaces[g_wifiIfaceCount] = i;
    return (int64_t)g_wifiIfaceCount++;
}
static FireWifibIface* fire_wifib_find(Value h) {
    int64_t i = h.i;
    if (i > 0 && (size_t)i < g_wifiIfaceCount && g_wifiIfaces[i] != NULL) return g_wifiIfaces[i];
    fire_wifib_error(FIRE_WIFI_E_INVALID_HANDLE, "Invalid or already closed WiFi handle.");
    return NULL;
}

/* ---- natives -------------------------------------------------------------------------------------------------------------------------------- */
static Value fire_wifi_LastError(void) { return Int(g_ioError.code); }
static Value fire_wifi_LastErrorMessage(OwnList* list) { return fire_wifib_cstr8(g_ioError.message, list); }
static Value fire_wifi_OpenCount(void) {
    int64_t n = 0;
    size_t i;
    for (i = 1; i < g_wifiIfaceCount; i++) if (g_wifiIfaces[i] != NULL) n++;
    return Int(n);
}
/* 1 if this machine has a WiFi radio that a program can control (the simulated one is there anyway), else 0. */
static Value fire_wifi_Supported(void) {
    FireWifiNames hardware;
    FireWifiStatus st;
    hardware.count = 0;
    fire_wifib_ok();
    return Int(fire_wifi_supported() && fire_wifi_interfaces(&hardware, &st) && hardware.count > 0 ? 1 : 0);
}

/* The interfaces: "sim" first, then those of the hardware. */
static Value fire_wifi_Interfaces(OwnList* list) {
    FireWifiNames hardware;
    FireWifiStatus st;
    Arr* a;
    int i;
    hardware.count = 0;
    if (!fire_wifi_interfaces(&hardware, &st)) { fire_wifib_errorStatus(&st); return Undef(); }
    fire_wifib_ok();
    a = allocArr((uint32_t)(1 + hardware.count), list);
    arrItems(a)[0] = fire_wifib_cstr8("sim", list);
    retain(arrItems(a)[0]);
    for (i = 0; i < hardware.count; i++) { arrItems(a)[1 + i] = fire_wifib_cstr8(hardware.names[i], list); retain(arrItems(a)[1 + i]); }
    return ArrV(a);
}

static Value fire_wifi_Open(Value name) {
    FireWifibText n;
    FireWifibIface* i;
    int64_t handle;
    if (!fire_wifib_utf8(name, &n)) return Int(fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory."));
    i = (FireWifibIface*)calloc(1, sizeof(FireWifibIface));
    if (i == NULL) { fire_wifib_textFree(&n); return Int(fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory.")); }
    if (fire_wifib_textEquals(&n, "sim", 3)) i->isSim = 1;
    else {
        FireWifiNames hardware;
        FireWifiStatus st;
        int known = 0, k, listed;
        hardware.count = 0;
        listed = fire_wifi_interfaces(&hardware, &st);
        for (k = 0; k < hardware.count; k++) if (fire_wifib_textEquals(&n, hardware.names[k], strlen(hardware.names[k]))) known = 1;
        if (!listed || !known) {
            int64_t r = listed ? fire_wifib_error(FIRE_WIFI_E_NOT_FOUND, "There is no WiFi interface '%s'.", n.s) : fire_wifib_errorStatus(&st);
            free(i);
            fire_wifib_textFree(&n);
            return Int(r);
        }
        if (!fire_wifi_init(&st)) { free(i); fire_wifib_textFree(&n); return Int(fire_wifib_errorStatus(&st)); }
    }
    fire_wifib_textFree(&n);
    handle = fire_wifib_add(i);
    if (handle < 0) return Int(-1);
    fire_wifib_ok();
    return Int(handle);
}

static Value fire_wifi_Close(Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= g_wifiIfaceCount || g_wifiIfaces[i] == NULL) { fire_wifib_error(FIRE_WIFI_E_INVALID_HANDLE, "Invalid or already closed WiFi handle."); return Bool(false); }
    fire_wifib_release(g_wifiIfaces[i]);
    g_wifiIfaces[i] = NULL;
    fire_wifib_ok();
    return Bool(true);
}

/* ---- scanning ------------------------------------------------------------------------------------------------------------------------------- */
/* Starts a scan; false on an error (Busy: the station is in the middle of joining). */
static Value fire_wifi_ScanBegin(Value h) {
    FireWifibIface* i = fire_wifib_find(h);
    if (!i) return Bool(false);
    if (i->isSim) {
        if (g_wifiSim.state == FIRE_WIFIB_CONNECTING) { fire_wifib_error(FIRE_WIFI_E_BUSY, "The station is joining a network."); return Bool(false); }
        g_wifiSim.scanning = 1;
        g_wifiSim.scanLeft = g_wifiSim.scanPolls;
        i->found.count = 0;
    } else {
        FireWifiStatus st;
        if (!fire_wifi_scanBegin(&st)) { fire_wifib_errorStatus(&st); return Bool(false); }
    }
    fire_wifib_ok();
    return Bool(true);
}

/* Asks without waiting: 1 the scan is done (`ScanCount` and the fields have the networks), 0 not yet, -1 on an error. */
static Value fire_wifi_ScanStep(Value h) {
    FireWifibIface* i = fire_wifib_find(h);
    FireWifiStatus st;
    int done = 0;
    if (!i) return Int(-1);
    if (i->isSim) {
        FireWifibSim* s = &g_wifiSim;
        size_t k;
        int n = 0;
        if (!s->scanning) return Int(fire_wifib_error(FIRE_WIFI_E_INVALID_ARGUMENT, "No scan is going on."));
        if (s->scanLeft > 0) { s->scanLeft--; fire_wifib_ok(); return Int(0); }
        s->scanning = 0;
        i->found.count = 0;
        for (k = 0; k < s->networkCount; k++) {
            const FireWifibSimNetwork* net = &s->networks[k];
            FireWifiNetwork f;
            memset(&f, 0, sizeof f);
            memcpy(f.ssid, net->ssid.s, net->ssid.n);   /* (a simulated network has a name of 1 to 32 bytes) */
            f.ssid[net->ssid.n] = 0;
            f.ssidLength = (int)net->ssid.n;
            snprintf(f.bssid, sizeof f.bssid, "02:00:00:00:10:%02x", (unsigned)(n++ & 0xFF));
            f.rssi = net->rssi;
            f.channel = net->channel;
            f.auth = net->password.n == 0 ? 0 : 3;
            if (!fire_wifi_networkListAdd(&i->found, &f)) return Int(fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory."));
        }
        fire_wifib_ok();
        return Int(1);
    }
    if (!fire_wifi_scanStep(&done, &i->found, &st)) return Int(fire_wifib_errorStatus(&st));
    fire_wifib_ok();
    return Int(done ? 1 : 0);
}
static Value fire_wifi_ScanCount(Value h) {
    FireWifibIface* i = fire_wifib_find(h);
    if (!i) return Int(-1);
    fire_wifib_ok();
    return Int((int64_t)i->found.count);
}
static const FireWifiNetwork* fire_wifib_scanItem(Value h, Value index) {
    FireWifibIface* i = fire_wifib_find(h);
    if (!i) return NULL;
    if (index.i < 0 || (size_t)index.i >= i->found.count) { fire_wifib_error(FIRE_WIFI_E_INVALID_ARGUMENT, "No such network in the scan."); return NULL; }
    return &i->found.items[(size_t)index.i];
}
static Value fire_wifi_ScanSsid(Value h, Value index, OwnList* list) {
    const FireWifiNetwork* n = fire_wifib_scanItem(h, index);
    if (!n) return Undef();
    fire_wifib_ok();
    return fire_wifib_str8(n->ssid, (size_t)n->ssidLength, list);
}
static Value fire_wifi_ScanBssid(Value h, Value index, OwnList* list) {
    const FireWifiNetwork* n = fire_wifib_scanItem(h, index);
    if (!n) return Undef();
    fire_wifib_ok();
    return fire_wifib_cstr8(n->bssid, list);
}
/* A number of a found network: 0 the signal strength (dBm), 1 the channel, 2 the security (0 open, 1 WEP, 2 WPA, 3 WPA2, 4 WPA3, 5 other); -1000 on an error. */
static Value fire_wifi_ScanInfo(Value h, Value index, Value which) {
    const FireWifiNetwork* n = fire_wifib_scanItem(h, index);
    if (!n) return Int(-1000);
    fire_wifib_ok();
    switch (which.i) {
        case 0: return Int(n->rssi);
        case 1: return Int(n->channel);
        case 2: return Int(n->auth);
        default: fire_wifib_error(FIRE_WIFI_E_INVALID_ARGUMENT, "No such information."); return Int(-1000);
    }
}

/* ---- the station ---------------------------------------------------------------------------------------------------------------------------- */
/* Starts joining a network (an empty password: an open network); false on an error. */
static Value fire_wifi_ConnectBegin(Value h, Value ssid, Value password) {
    FireWifibIface* i = fire_wifib_find(h);
    FireWifibText s, p;
    if (!i) return Bool(false);
    if (!fire_wifib_utf8(ssid, &s)) { fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory."); return Bool(false); }
    if (!fire_wifib_utf8(password, &p)) { fire_wifib_textFree(&s); fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory."); return Bool(false); }
    if (i->isSim) {
        FireWifibSim* m = &g_wifiSim;
        if (s.n == 0 || s.n > 32) { fire_wifib_textFree(&s); fire_wifib_textFree(&p); fire_wifib_error(FIRE_WIFI_E_INVALID_ARGUMENT, "The network name must be 1 to 32 characters."); return Bool(false); }
        m->state = FIRE_WIFIB_CONNECTING;
        fire_wifib_textFree(&m->target);
        fire_wifib_textFree(&m->targetPassword);
        m->target = s;
        m->targetPassword = p;
        m->pollsLeft = m->connectPolls;
        m->failCode = 0;
        m->failMessage[0] = 0;
        fire_wifib_textFree(&m->ssid);
    } else {
        FireWifiStatus st;
        int done = fire_wifi_connectBegin(s.s, p.s, &st);
        fire_wifib_textFree(&s);
        fire_wifib_textFree(&p);
        if (!done) { fire_wifib_errorStatus(&st); return Bool(false); }
    }
    fire_wifib_ok();
    return Bool(true);
}

/* Asks without waiting: 0 idle (not connected, not trying), 1 still joining, 2 connected, -1 failed (the error slot says why: NotFound, Auth, ...). */
static Value fire_wifi_ConnectState(Value h) {
    FireWifibIface* i = fire_wifib_find(h);
    FireWifiStatus st;
    char message[176];
    int state = 0, code = 0;
    if (!i) return Int(-1);
    if (i->isSim) {
        FireWifibSim* m = &g_wifiSim;
        if (m->state == FIRE_WIFIB_CONNECTING) {
            const FireWifibSimNetwork* net;
            if (m->pollsLeft > 0) { m->pollsLeft--; fire_wifib_ok(); return Int(FIRE_WIFIB_CONNECTING); }
            net = fire_wifib_simFind(m->target.s, m->target.n);
            if (!net) {
                m->state = FIRE_WIFIB_FAILED;
                m->failCode = FIRE_WIFI_E_NOT_FOUND;
                snprintf(m->failMessage, sizeof m->failMessage, "The network '%s' was not found.", m->target.s ? m->target.s : "");
            } else if (!fire_wifib_textEquals(&net->password, m->targetPassword.s, m->targetPassword.n)) {
                m->state = FIRE_WIFIB_FAILED;
                m->failCode = FIRE_WIFI_E_AUTH;
                snprintf(m->failMessage, sizeof m->failMessage, "The network refused the login (wrong password?).");
            } else {
                m->state = FIRE_WIFIB_CONNECTED;
                fire_wifib_textSet(&m->ssid, net->ssid.s, net->ssid.n);
                m->rssi = net->rssi;
            }
        }
        if (m->state == FIRE_WIFIB_FAILED) return Int(fire_wifib_error(m->failCode, "%s", m->failMessage));   /* (stays so until the next join or Disconnect) */
        fire_wifib_ok();
        return Int(m->state);
    }
    message[0] = 0;
    if (!fire_wifi_connectState(&state, &code, message, sizeof message, &st)) return Int(fire_wifib_errorStatus(&st));
    if (state == FIRE_WIFIB_FAILED) return Int(fire_wifib_error(code ? code : FIRE_WIFI_E_OTHER, "%s", message[0] == 0 ? "Could not connect." : message));
    fire_wifib_ok();
    return Int(state);
}

static Value fire_wifi_Disconnect(Value h) {
    FireWifibIface* i = fire_wifib_find(h);
    if (!i) return Bool(false);
    if (i->isSim) { g_wifiSim.state = FIRE_WIFIB_IDLE; fire_wifib_textFree(&g_wifiSim.ssid); g_wifiSim.failCode = 0; }
    else {
        FireWifiStatus st;
        if (!fire_wifi_disconnect(&st)) { fire_wifib_errorStatus(&st); return Bool(false); }
    }
    fire_wifib_ok();
    return Bool(true);
}

/* A text of the station: 0 the name of the network it is joined to ("" if none), 1 its IP address ("" if none), 2 its own MAC address; undefined on an error. */
static Value fire_wifi_StationText(Value h, Value which, OwnList* list) {
    FireWifibIface* i = fire_wifib_find(h);
    FireWifiStation info;
    const char* ssid;
    size_t ssidLength;
    if (!i) return Undef();
    memset(&info, 0, sizeof info);
    if (i->isSim) {
        FireWifibSim* m = &g_wifiSim;
        snprintf(info.mac, sizeof info.mac, "%s", kWifiSimStationMac);
        if (m->state == FIRE_WIFIB_CONNECTED) { ssid = m->ssid.s ? m->ssid.s : ""; ssidLength = m->ssid.n; snprintf(info.ip, sizeof info.ip, "%s", kWifiSimStationIp); }
        else { ssid = ""; ssidLength = 0; }
    } else {
        FireWifiStatus st;
        if (!fire_wifi_stationInfo(&info, &st) && st.code != FIRE_WIFI_E_NOT_CONNECTED) { fire_wifib_errorStatus(&st); return Undef(); }
        ssid = info.ssid;
        ssidLength = strlen(info.ssid);
    }
    fire_wifib_ok();
    switch (which.i) {
        case 0: return fire_wifib_str8(ssid, ssidLength, list);
        case 1: return fire_wifib_cstr8(info.ip, list);
        case 2: return fire_wifib_cstr8(info.mac, list);
        default: fire_wifib_error(FIRE_WIFI_E_INVALID_ARGUMENT, "No such information."); return Undef();
    }
}
/* The signal strength of the joined network in dBm (0 when not connected); -1000 on an error. */
static Value fire_wifi_StationRssi(Value h) {
    FireWifibIface* i = fire_wifib_find(h);
    FireWifiStation info;
    FireWifiStatus st;
    if (!i) return Int(-1000);
    if (i->isSim) { fire_wifib_ok(); return Int(g_wifiSim.state == FIRE_WIFIB_CONNECTED ? g_wifiSim.rssi : 0); }
    memset(&info, 0, sizeof info);
    if (!fire_wifi_stationInfo(&info, &st) && st.code != FIRE_WIFI_E_NOT_CONNECTED) { fire_wifib_errorStatus(&st); return Int(-1000); }
    fire_wifib_ok();
    return Int(info.rssi);
}

/* ---- the access point ----------------------------------------------------------------------------------------------------------------------- */
static Value fire_wifi_ApStart(Value h, Value ssid, Value password, Value channel, Value maxClients) {
    FireWifibIface* i = fire_wifib_find(h);
    FireWifibText s, p;
    const char* problem = NULL;
    if (!i) return Bool(false);
    if (!fire_wifib_utf8(ssid, &s)) { fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory."); return Bool(false); }
    if (!fire_wifib_utf8(password, &p)) { fire_wifib_textFree(&s); fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory."); return Bool(false); }
    if (i->isSim) {
        FireWifibSim* m = &g_wifiSim;
        if (s.n == 0 || s.n > 32) problem = "The network name must be 1 to 32 characters.";
        else if (p.n != 0 && (p.n < 8 || p.n > 63)) problem = "The password must be 8 to 63 characters (or empty for an open network).";
        else if (channel.i < 1 || channel.i > 13) problem = "The channel must be 1 to 13.";
        else if (maxClients.i < 1 || maxClients.i > 10) problem = "The number of clients must be 1 to 10.";
        fire_wifib_textFree(&s);
        fire_wifib_textFree(&p);
        if (problem) { fire_wifib_error(FIRE_WIFI_E_INVALID_ARGUMENT, "%s", problem); return Bool(false); }
        m->apRunning = 1;
        m->apChannel = (int)channel.i;
        m->apMax = (int)maxClients.i;
        m->apClients = 0;
    } else {
        FireWifiStatus st;
        int done = fire_wifi_apStart(s.s, p.s, (int)channel.i, (int)maxClients.i, &st);
        fire_wifib_textFree(&s);
        fire_wifib_textFree(&p);
        if (!done) { fire_wifib_errorStatus(&st); return Bool(false); }
    }
    fire_wifib_ok();
    return Bool(true);
}
static Value fire_wifi_ApStop(Value h) {
    FireWifibIface* i = fire_wifib_find(h);
    if (!i) return Bool(false);
    if (i->isSim) { g_wifiSim.apRunning = 0; g_wifiSim.apClients = 0; }
    else {
        FireWifiStatus st;
        if (!fire_wifi_apStop(&st)) { fire_wifib_errorStatus(&st); return Bool(false); }
    }
    fire_wifib_ok();
    return Bool(true);
}
/* 1 if the access point is running, else 0; -1 on an error. */
static Value fire_wifi_ApRunning(Value h) {
    FireWifibIface* i = fire_wifib_find(h);
    FireWifiAp info;
    FireWifiStatus st;
    if (!i) return Int(-1);
    if (i->isSim) { fire_wifib_ok(); return Int(g_wifiSim.apRunning ? 1 : 0); }
    memset(&info, 0, sizeof info);
    if (!fire_wifi_apInfo(&info, &st)) return Int(fire_wifib_errorStatus(&st));
    fire_wifib_ok();
    return Int(info.ip[0] != 0 ? 1 : 0);
}
/* A text of the access point: 0 its IP address, 1 its MAC address; undefined on an error. */
static Value fire_wifi_ApText(Value h, Value which, OwnList* list) {
    FireWifibIface* i = fire_wifib_find(h);
    FireWifiAp info;
    if (!i) return Undef();
    memset(&info, 0, sizeof info);
    if (i->isSim) { snprintf(info.ip, sizeof info.ip, "%s", kWifiSimApIp); snprintf(info.mac, sizeof info.mac, "%s", kWifiSimApMac); }
    else {
        FireWifiStatus st;
        if (!fire_wifi_apInfo(&info, &st)) { fire_wifib_errorStatus(&st); return Undef(); }
    }
    fire_wifib_ok();
    switch (which.i) {
        case 0: return fire_wifib_cstr8(info.ip, list);
        case 1: return fire_wifib_cstr8(info.mac, list);
        default: fire_wifib_error(FIRE_WIFI_E_INVALID_ARGUMENT, "No such information."); return Undef();
    }
}
/* How many stations are joined to the access point; -1 on an error. */
static Value fire_wifi_ApClients(Value h) {
    FireWifibIface* i = fire_wifib_find(h);
    FireWifiAp info;
    FireWifiStatus st;
    if (!i) return Int(-1);
    if (i->isSim) { fire_wifib_ok(); return Int(g_wifiSim.apClients); }
    memset(&info, 0, sizeof info);
    if (!fire_wifi_apInfo(&info, &st)) return Int(fire_wifib_errorStatus(&st));
    fire_wifib_ok();
    return Int(info.clients);
}

/* ---- the simulated radio from the outside --------------------------------------------------------------------------------------------------- */
/* Puts a network in the air (an empty password: an open network); one with the same name is replaced. */
static Value fire_wifi_SimAddNetwork(Value ssid, Value password, Value rssi, Value channel) {
    FireWifibSim* m = &g_wifiSim;
    FireWifibSimNetwork n, *existing;
    if (!fire_wifib_utf8(ssid, &n.ssid)) { fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory."); return Bool(false); }
    if (n.ssid.n == 0 || n.ssid.n > 32) { fire_wifib_textFree(&n.ssid); fire_wifib_error(FIRE_WIFI_E_INVALID_ARGUMENT, "The network name must be 1 to 32 characters."); return Bool(false); }
    if (!fire_wifib_utf8(password, &n.password)) { fire_wifib_textFree(&n.ssid); fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory."); return Bool(false); }
    n.rssi = (int)rssi.i;
    n.channel = (int)channel.i;
    existing = fire_wifib_simFind(n.ssid.s, n.ssid.n);
    if (existing) {
        fire_wifib_textFree(&existing->ssid);
        fire_wifib_textFree(&existing->password);
        *existing = n;
        fire_wifib_ok();
        return Bool(true);
    }
    if (m->networkCount == m->networkCap) {
        size_t cap = m->networkCap ? m->networkCap * 2 : 8;
        FireWifibSimNetwork* grown = (FireWifibSimNetwork*)realloc(m->networks, sizeof(FireWifibSimNetwork) * cap);
        if (grown == NULL) { fire_wifib_textFree(&n.ssid); fire_wifib_textFree(&n.password); fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory."); return Bool(false); }
        m->networks = grown;
        m->networkCap = cap;
    }
    m->networks[m->networkCount++] = n;
    fire_wifib_ok();
    return Bool(true);
}
static Value fire_wifi_SimRemoveNetwork(Value ssid) {
    FireWifibSim* m = &g_wifiSim;
    FireWifibText s;
    size_t i;
    if (!fire_wifib_utf8(ssid, &s)) { fire_wifib_error(FIRE_WIFI_E_OTHER, "Out of memory."); return Bool(false); }
    for (i = 0; i < m->networkCount; i++)
        if (fire_wifib_textEquals(&m->networks[i].ssid, s.s, s.n)) {
            fire_wifib_textFree(&m->networks[i].ssid);
            fire_wifib_textFree(&m->networks[i].password);
            memmove(&m->networks[i], &m->networks[i + 1], sizeof(FireWifibSimNetwork) * (m->networkCount - i - 1));
            m->networkCount--;
            break;
        }
    if (m->state == FIRE_WIFIB_CONNECTED && fire_wifib_textEquals(&m->ssid, s.s, s.n)) { m->state = FIRE_WIFIB_IDLE; fire_wifib_textFree(&m->ssid); }   /* (the network went away: the connection is lost) */
    fire_wifib_textFree(&s);
    fire_wifib_ok();
    return Bool(true);
}
/* The connection of the station is lost (the router went away). */
static Value fire_wifi_SimDrop(void) {
    if (g_wifiSim.state == FIRE_WIFIB_CONNECTED) { g_wifiSim.state = FIRE_WIFIB_IDLE; fire_wifib_textFree(&g_wifiSim.ssid); }
    fire_wifib_ok();
    return Bool(true);
}
/* How many questions a join and a scan take to answer (default 2 and 1; 0: the first question already has the answer). */
static Value fire_wifi_SimDelays(Value connectPolls, Value scanPolls) {
    if (connectPolls.i < 0 || scanPolls.i < 0 || connectPolls.i > 100000 || scanPolls.i > 100000) { fire_wifib_error(FIRE_WIFI_E_INVALID_ARGUMENT, "Invalid number of questions."); return Bool(false); }
    g_wifiSim.connectPolls = (int)connectPolls.i;
    g_wifiSim.scanPolls = (int)scanPolls.i;
    fire_wifib_ok();
    return Bool(true);
}
/* A station joins or leaves the simulated access point (up to the maximum); the new number of stations, -1 if it cannot be done. */
static Value fire_wifi_SimApClients(Value delta) {
    FireWifibSim* m = &g_wifiSim;
    int n;
    if (!m->apRunning) return Int(fire_wifib_error(FIRE_WIFI_E_NOT_CONNECTED, "The access point is not running."));
    n = m->apClients + (int)delta.i;
    if (n < 0 || n > m->apMax) return Int(fire_wifib_error(FIRE_WIFI_E_INVALID_ARGUMENT, "The number of stations must stay between 0 and the maximum."));
    m->apClients = n;
    fire_wifib_ok();
    return Int(n);
}
/* Removes all networks and takes the radio back to the start (open interfaces stay open). */
static Value fire_wifi_SimReset(void) {
    fire_wifib_simReset();
    fire_wifib_ok();
    return Bool(true);
}

#endif
