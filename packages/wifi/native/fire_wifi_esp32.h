/* fire wifi package: WiFi part on the ESP32: the WiFi driver of ESP-IDF (esp_wifi, esp_event, esp_netif). The interface is called "wifi"; it has a
 * station (joins a network) and an access point (is one), either alone or both at once. The driver is started by the first call that needs it (this also sets
 * up NVS, the network interfaces and the default event loop - if the program has done that already, it is not done twice). Events of the driver only note what
 * happened in a few variables; the fire code asks (`connectState`, `scanStep`). A failed join is retried a few times (the radio is not perfect) before it is
 * reported. Once the station has an address, the lwIP sockets of the net package work - the net package does not know about WiFi. (Not tried on a board yet.)
 * The interface is the one of fire_wifi_c.h. */
#ifndef FIRE_WIFI_ESP32_H
#define FIRE_WIFI_ESP32_H

#include <esp_event.h>
#include <esp_mac.h>
#include <esp_netif.h>
#include <esp_wifi.h>
#include <nvs_flash.h>

#include "fire_wifi_c.h"

#ifndef FIRE_WIFI_RETRIES
#define FIRE_WIFI_RETRIES 3
#endif
#ifndef FIRE_WIFI_MAX_SCAN
#define FIRE_WIFI_MAX_SCAN 48
#endif

/* What the event handler notes (a handler runs in the event task: only plain variables are written). */
typedef struct FireWifiState {
    int started;
    esp_netif_t* sta;
    esp_netif_t* ap;
    wifi_mode_t mode;
    volatile int state;            /* 0 idle, 1 connecting, 2 connected, 3 failed */
    volatile int failCode;
    char failMessage[96];
    volatile int retries;
    volatile int dropExpected;     /* we disconnected on purpose */
    volatile int scanDone;
    int scanning;
} FireWifiState;
static FireWifiState g_fireWifi = {0, NULL, NULL, WIFI_MODE_STA, 0, 0, {0}, 0, 0, 0, 0};

/* The text of a field of `max` bytes that may not end with a zero, into `out` (at least max + 1 bytes): its length. */
static int fire_wifi_espText(const uint8_t* bytes, size_t max, char* out) {
    size_t n = 0;
    while (n < max && bytes[n]) n++;
    memcpy(out, bytes, n);
    out[n] = 0;
    return (int)n;
}
static void fire_wifi_espMacText(const uint8_t* m, char* out, size_t size) {
    snprintf(out, size, "%02x:%02x:%02x:%02x:%02x:%02x", m[0], m[1], m[2], m[3], m[4], m[5]);
}
static void fire_wifi_espIpText(const esp_netif_ip_info_t* ip, char* out, size_t size) {
    snprintf(out, size, "%d.%d.%d.%d", (int)(ip->ip.addr & 0xFF), (int)((ip->ip.addr >> 8) & 0xFF), (int)((ip->ip.addr >> 16) & 0xFF), (int)((ip->ip.addr >> 24) & 0xFF));
}
static int fire_wifi_espFail(esp_err_t err, const char* what, FireWifiStatus* st) {
    if (err == ESP_ERR_WIFI_NOT_STARTED || err == ESP_ERR_WIFI_NOT_INIT) return fire_wifi_fail(st, FIRE_WIFI_E_OTHER, "%s: the WiFi driver is not running.", what);
    if (err == ESP_ERR_WIFI_STATE || err == ESP_ERR_WIFI_CONN) return fire_wifi_fail(st, FIRE_WIFI_E_BUSY, "%s: the WiFi driver is busy.", what);
    if (err == ESP_ERR_NO_MEM) return fire_wifi_fail(st, FIRE_WIFI_E_OTHER, "%s: out of memory.", what);
    return fire_wifi_fail(st, FIRE_WIFI_E_OTHER, "%s: %s", what, esp_err_to_name(err));
}

static void fire_wifi_espOnEvent(void* arg, esp_event_base_t base, int32_t id, void* data) {
    FireWifiState* s = &g_fireWifi;
    (void)arg;
    if (base == WIFI_EVENT && id == WIFI_EVENT_SCAN_DONE) {
        s->scanDone = 1;
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        const wifi_event_sta_disconnected_t* d = (const wifi_event_sta_disconnected_t*)data;
        int reason, auth;
        if (s->dropExpected) { s->dropExpected = 0; s->state = 0; return; }
        if (s->state == 2) { s->state = 0; return; }   /* a connection that was there is lost */
        if (s->state != 1) return;
        reason = d ? d->reason : 0;
        auth = reason == WIFI_REASON_AUTH_FAIL || reason == WIFI_REASON_4WAY_HANDSHAKE_TIMEOUT || reason == WIFI_REASON_HANDSHAKE_TIMEOUT || reason == WIFI_REASON_MIC_FAILURE
            || reason == WIFI_REASON_AUTH_EXPIRE;
        if (reason == WIFI_REASON_NO_AP_FOUND) {
            s->failCode = FIRE_WIFI_E_NOT_FOUND;
            snprintf(s->failMessage, sizeof s->failMessage, "The network was not found.");
            s->state = 3;
        } else if (auth) {
            s->failCode = FIRE_WIFI_E_AUTH;
            snprintf(s->failMessage, sizeof s->failMessage, "The network refused the login (wrong password?), reason %d.", reason);
            s->state = 3;
        } else if (s->retries < FIRE_WIFI_RETRIES) {
            s->retries = s->retries + 1;
            esp_wifi_connect();
        } else {
            s->failCode = FIRE_WIFI_E_OTHER;
            snprintf(s->failMessage, sizeof s->failMessage, "Could not connect (reason %d).", reason);
            s->state = 3;
        }
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        s->state = 2;
    }
}

static int fire_wifi_supported(void) { return 1; }

static int fire_wifi_interfaces(FireWifiNames* names, FireWifiStatus* st) {
    names->count = 1;
    snprintf(names->names[0], sizeof names->names[0], "wifi");
    return fire_wifi_success(st);
}

static int fire_wifi_init(FireWifiStatus* st) {
    FireWifiState* s = &g_fireWifi;
    esp_err_t err;
    if (s->started) return fire_wifi_success(st);
    err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        nvs_flash_erase();
        err = nvs_flash_init();
    }
    if (err != ESP_OK) return fire_wifi_espFail(err, "nvs_flash_init", st);
    err = esp_netif_init();
    if (err != ESP_OK) return fire_wifi_espFail(err, "esp_netif_init", st);
    err = esp_event_loop_create_default();
    if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) return fire_wifi_espFail(err, "esp_event_loop_create_default", st);   /* (invalid state: the program has made it) */
    s->sta = esp_netif_create_default_wifi_sta();
    s->ap = esp_netif_create_default_wifi_ap();
    {
        wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
        err = esp_wifi_init(&cfg);
    }
    if (err != ESP_OK) return fire_wifi_espFail(err, "esp_wifi_init", st);
    esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, &fire_wifi_espOnEvent, NULL);
    esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, &fire_wifi_espOnEvent, NULL);
    err = esp_wifi_set_mode(WIFI_MODE_STA);
    if (err != ESP_OK) return fire_wifi_espFail(err, "esp_wifi_set_mode", st);
    err = esp_wifi_start();
    if (err != ESP_OK) return fire_wifi_espFail(err, "esp_wifi_start", st);
    s->mode = WIFI_MODE_STA;
    s->started = 1;
    return fire_wifi_success(st);
}

static int fire_wifi_scanBegin(FireWifiStatus* st) {
    FireWifiState* s = &g_fireWifi;
    wifi_scan_config_t cfg;
    esp_err_t err;
    if (!fire_wifi_init(st)) return 0;
    s->scanDone = 0;
    memset(&cfg, 0, sizeof cfg);
    err = esp_wifi_scan_start(&cfg, false);
    if (err != ESP_OK) return fire_wifi_espFail(err, "scan", st);
    s->scanning = 1;
    return fire_wifi_success(st);
}

static int fire_wifi_espAuthOf(wifi_auth_mode_t m) {
    switch (m) {
        case WIFI_AUTH_OPEN: return 0;
        case WIFI_AUTH_WEP: return 1;
        case WIFI_AUTH_WPA_PSK: return 2;
        case WIFI_AUTH_WPA2_PSK: case WIFI_AUTH_WPA_WPA2_PSK: return 3;
        case WIFI_AUTH_WPA3_PSK: case WIFI_AUTH_WPA2_WPA3_PSK: return 4;
        default: return 5;
    }
}

static int fire_wifi_scanStep(int* done, FireWifiNetworkList* found, FireWifiStatus* st) {
    FireWifiState* s = &g_fireWifi;
    uint16_t count = FIRE_WIFI_MAX_SCAN, i;
    wifi_ap_record_t* records;
    esp_err_t err;
    *done = 0;
    if (!s->scanning) return fire_wifi_fail(st, FIRE_WIFI_E_INVALID_ARGUMENT, "No scan is going on.");
    if (!s->scanDone) return fire_wifi_success(st);
    s->scanning = 0;
    records = (wifi_ap_record_t*)calloc(count, sizeof(wifi_ap_record_t));
    if (records == NULL) return fire_wifi_fail(st, FIRE_WIFI_E_OTHER, "scan results: out of memory.");
    err = esp_wifi_scan_get_ap_records(&count, records);
    if (err != ESP_OK) { free(records); return fire_wifi_espFail(err, "scan results", st); }
    found->count = 0;
    for (i = 0; i < count; i++) {
        FireWifiNetwork n;
        memset(&n, 0, sizeof n);
        n.ssidLength = fire_wifi_espText(records[i].ssid, sizeof records[i].ssid < 32 ? sizeof records[i].ssid : 32, n.ssid);
        fire_wifi_espMacText(records[i].bssid, n.bssid, sizeof n.bssid);
        n.rssi = records[i].rssi;
        n.channel = records[i].primary;
        n.auth = fire_wifi_espAuthOf(records[i].authmode);
        fire_wifi_networkListAdd(found, &n);
    }
    free(records);
    *done = 1;
    return fire_wifi_success(st);
}

static int fire_wifi_connectBegin(const char* ssid, const char* password, FireWifiStatus* st) {
    FireWifiState* s = &g_fireWifi;
    size_t ssidLength = strlen(ssid), passwordLength = strlen(password);
    wifi_config_t cfg;
    esp_err_t err;
    if (!fire_wifi_init(st)) return 0;
    if (ssidLength == 0 || ssidLength > 32) return fire_wifi_fail(st, FIRE_WIFI_E_INVALID_ARGUMENT, "The network name must be 1 to 32 characters.");
    if (passwordLength > 63 || (passwordLength > 0 && passwordLength < 8))
        return fire_wifi_fail(st, FIRE_WIFI_E_INVALID_ARGUMENT, "The password must be 8 to 63 characters (or empty for an open network).");
    memset(&cfg, 0, sizeof cfg);
    memcpy(cfg.sta.ssid, ssid, ssidLength);
    memcpy(cfg.sta.password, password, passwordLength);
    cfg.sta.threshold.authmode = passwordLength == 0 ? WIFI_AUTH_OPEN : WIFI_AUTH_WPA_PSK;
    cfg.sta.pmf_cfg.capable = true;
    cfg.sta.pmf_cfg.required = false;
    if (s->state == 1 || s->state == 2) { s->dropExpected = 1; esp_wifi_disconnect(); }
    err = esp_wifi_set_config(WIFI_IF_STA, &cfg);
    if (err != ESP_OK) return fire_wifi_espFail(err, "set the station", st);
    s->retries = 0;
    s->failCode = 0;
    s->state = 1;
    err = esp_wifi_connect();
    if (err != ESP_OK) { s->state = 0; return fire_wifi_espFail(err, "connect", st); }
    return fire_wifi_success(st);
}

static int fire_wifi_connectState(int* state, int* code, char* message, size_t size, FireWifiStatus* st) {
    FireWifiState* s = &g_fireWifi;
    *state = s->state;
    *code = s->failCode;
    snprintf(message, size, "%s", s->failMessage);
    return fire_wifi_success(st);
}

static int fire_wifi_disconnect(FireWifiStatus* st) {
    FireWifiState* s = &g_fireWifi;
    esp_err_t err;
    if (!s->started) return fire_wifi_success(st);
    s->dropExpected = s->state != 0;
    s->state = 0;
    err = esp_wifi_disconnect();
    if (err != ESP_OK && err != ESP_ERR_WIFI_NOT_CONNECT) return fire_wifi_espFail(err, "disconnect", st);
    return fire_wifi_success(st);
}

static int fire_wifi_stationInfo(FireWifiStation* info, FireWifiStatus* st) {
    FireWifiState* s = &g_fireWifi;
    uint8_t m[6] = {0};
    wifi_ap_record_t rec;
    esp_netif_ip_info_t ip;
    if (!fire_wifi_init(st)) return 0;
    esp_read_mac(m, ESP_MAC_WIFI_STA);
    fire_wifi_espMacText(m, info->mac, sizeof info->mac);
    info->ssid[0] = 0;
    info->ip[0] = 0;
    info->rssi = 0;
    if (esp_wifi_sta_get_ap_info(&rec) != ESP_OK) return fire_wifi_fail(st, FIRE_WIFI_E_NOT_CONNECTED, "The station is not connected.");
    fire_wifi_espText(rec.ssid, sizeof rec.ssid < 32 ? sizeof rec.ssid : 32, info->ssid);
    info->rssi = rec.rssi;
    if (s->sta && esp_netif_get_ip_info(s->sta, &ip) == ESP_OK) fire_wifi_espIpText(&ip, info->ip, sizeof info->ip);
    return fire_wifi_success(st);
}

static int fire_wifi_apStart(const char* ssid, const char* password, int channel, int maxClients, FireWifiStatus* st) {
    FireWifiState* s = &g_fireWifi;
    size_t ssidLength = strlen(ssid), passwordLength = strlen(password);
    wifi_config_t cfg;
    esp_err_t err;
    if (!fire_wifi_init(st)) return 0;
    if (ssidLength == 0 || ssidLength > 32) return fire_wifi_fail(st, FIRE_WIFI_E_INVALID_ARGUMENT, "The network name must be 1 to 32 characters.");
    if (passwordLength > 63 || (passwordLength > 0 && passwordLength < 8))
        return fire_wifi_fail(st, FIRE_WIFI_E_INVALID_ARGUMENT, "The password must be 8 to 63 characters (or empty for an open network).");
    if (channel < 1 || channel > 13) return fire_wifi_fail(st, FIRE_WIFI_E_INVALID_ARGUMENT, "The channel must be 1 to 13.");
    if (maxClients < 1 || maxClients > 10) return fire_wifi_fail(st, FIRE_WIFI_E_INVALID_ARGUMENT, "The number of clients must be 1 to 10.");
    memset(&cfg, 0, sizeof cfg);
    memcpy(cfg.ap.ssid, ssid, ssidLength);
    cfg.ap.ssid_len = (uint8_t)ssidLength;
    memcpy(cfg.ap.password, password, passwordLength);
    cfg.ap.channel = (uint8_t)channel;
    cfg.ap.max_connection = (uint8_t)maxClients;
    cfg.ap.authmode = passwordLength == 0 ? WIFI_AUTH_OPEN : WIFI_AUTH_WPA2_PSK;
    err = esp_wifi_set_mode(WIFI_MODE_APSTA);
    if (err != ESP_OK) return fire_wifi_espFail(err, "set the mode", st);
    err = esp_wifi_set_config(WIFI_IF_AP, &cfg);
    if (err != ESP_OK) return fire_wifi_espFail(err, "set the access point", st);
    s->mode = WIFI_MODE_APSTA;
    return fire_wifi_success(st);
}

static int fire_wifi_apStop(FireWifiStatus* st) {
    FireWifiState* s = &g_fireWifi;
    esp_err_t err;
    if (!s->started || s->mode != WIFI_MODE_APSTA) return fire_wifi_success(st);
    err = esp_wifi_set_mode(WIFI_MODE_STA);
    if (err != ESP_OK) return fire_wifi_espFail(err, "set the mode", st);
    s->mode = WIFI_MODE_STA;
    return fire_wifi_success(st);
}

static int fire_wifi_apInfo(FireWifiAp* info, FireWifiStatus* st) {
    FireWifiState* s = &g_fireWifi;
    uint8_t m[6] = {0};
    esp_netif_ip_info_t ip;
    wifi_sta_list_t list;
    if (!fire_wifi_init(st)) return 0;
    esp_read_mac(m, ESP_MAC_WIFI_SOFTAP);
    fire_wifi_espMacText(m, info->mac, sizeof info->mac);
    info->ip[0] = 0;
    info->clients = 0;
    if (s->ap && esp_netif_get_ip_info(s->ap, &ip) == ESP_OK) fire_wifi_espIpText(&ip, info->ip, sizeof info->ip);
    if (s->mode == WIFI_MODE_APSTA && esp_wifi_ap_get_sta_list(&list) == ESP_OK) info->clients = list.num;
    return fire_wifi_success(st);
}

#endif
