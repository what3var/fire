// fire native platform layer, WiFi part on the ESP32: the WiFi driver of ESP-IDF (esp_wifi, esp_event, esp_netif). The interface is called "wifi"; it has a station (joins a network) and an access point (is one),
// either alone or both at once. The driver is started by the first call that needs it (this also sets up NVS, the network interfaces and the default event loop - if the program has done that already, it is not done
// twice). Events of the driver only note what happened in a few variables; the fire code asks (`connectState`, `scanStep`). A failed join is retried a few times (the radio is not perfect) before it is reported.
// Once the station has an address, the lwIP sockets of the net package work - the net package does not know about WiFi. (Not tried on a board yet.) The interface is the one of fire_wifi_none.hpp.
#pragma once

#include <esp_event.h>
#include <esp_mac.h>
#include <esp_netif.h>
#include <esp_wifi.h>
#include <nvs_flash.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#ifndef FIRE_WIFI_RETRIES
#define FIRE_WIFI_RETRIES 3
#endif
#ifndef FIRE_WIFI_MAX_SCAN
#define FIRE_WIFI_MAX_SCAN 48
#endif

namespace fire {
namespace plat {
namespace wifi {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7, E_Auth = 8, E_Timeout = 9, E_NotConnected = 10 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

struct Network {
    std::string ssid;
    std::string bssid;
    int rssi = 0;
    int channel = 0;
    int auth = 0;
};

/// What the event handler notes (a handler runs in the event task: only plain variables are written).
struct State {
    bool started = false;
    esp_netif_t* sta = nullptr;
    esp_netif_t* ap = nullptr;
    wifi_mode_t mode = WIFI_MODE_STA;
    volatile int state = 0;            // 0 idle, 1 connecting, 2 connected, 3 failed
    volatile int failCode = 0;
    char failMessage[96] = {0};
    volatile int retries = 0;
    volatile bool dropExpected = false;   // we disconnected on purpose
    volatile bool scanDone = false;
    bool scanning = false;
};
inline State& st() { static State s; return s; }

inline std::string text(const uint8_t* bytes, size_t max) {
    size_t n = 0;
    while (n < max && bytes[n]) n++;
    return std::string(reinterpret_cast<const char*>(bytes), n);
}
inline std::string macText(const uint8_t* m) {
    char t[24];
    std::snprintf(t, sizeof t, "%02x:%02x:%02x:%02x:%02x:%02x", m[0], m[1], m[2], m[3], m[4], m[5]);
    return t;
}
inline std::string ipText(const esp_netif_ip_info_t& ip) {
    char t[20];
    std::snprintf(t, sizeof t, "%d.%d.%d.%d", (int)(ip.ip.addr & 0xFF), (int)((ip.ip.addr >> 8) & 0xFF), (int)((ip.ip.addr >> 16) & 0xFF), (int)((ip.ip.addr >> 24) & 0xFF));
    return t;
}
inline Status failEsp(esp_err_t err, const std::string& what) {
    if (err == ESP_ERR_WIFI_NOT_STARTED || err == ESP_ERR_WIFI_NOT_INIT) return fail(E_Other, what + ": the WiFi driver is not running.");
    if (err == ESP_ERR_WIFI_STATE || err == ESP_ERR_WIFI_CONN) return fail(E_Busy, what + ": the WiFi driver is busy.");
    if (err == ESP_ERR_NO_MEM) return fail(E_Other, what + ": out of memory.");
    return fail(E_Other, what + ": " + esp_err_to_name(err));
}

inline void onEvent(void*, esp_event_base_t base, int32_t id, void* data) {
    State& s = st();
    if (base == WIFI_EVENT && id == WIFI_EVENT_SCAN_DONE) {
        s.scanDone = true;
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        const wifi_event_sta_disconnected_t* d = static_cast<const wifi_event_sta_disconnected_t*>(data);
        if (s.dropExpected) { s.dropExpected = false; s.state = 0; return; }
        if (s.state == 2) { s.state = 0; return; }   // a connection that was there is lost
        if (s.state != 1) return;
        int reason = d ? d->reason : 0;
        bool auth = reason == WIFI_REASON_AUTH_FAIL || reason == WIFI_REASON_4WAY_HANDSHAKE_TIMEOUT || reason == WIFI_REASON_HANDSHAKE_TIMEOUT || reason == WIFI_REASON_MIC_FAILURE || reason == WIFI_REASON_AUTH_EXPIRE;
        if (reason == WIFI_REASON_NO_AP_FOUND) {
            s.failCode = E_NotFound;
            std::snprintf(s.failMessage, sizeof s.failMessage, "The network was not found.");
            s.state = 3;
        } else if (auth) {
            s.failCode = E_Auth;
            std::snprintf(s.failMessage, sizeof s.failMessage, "The network refused the login (wrong password?), reason %d.", reason);
            s.state = 3;
        } else if (s.retries < FIRE_WIFI_RETRIES) {
            s.retries = s.retries + 1;
            esp_wifi_connect();
        } else {
            s.failCode = E_Other;
            std::snprintf(s.failMessage, sizeof s.failMessage, "Could not connect (reason %d).", reason);
            s.state = 3;
        }
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        s.state = 2;
    }
}

inline bool supported() { return true; }

inline Status interfaces(std::vector<std::string>& names) {
    names.clear();
    names.push_back("wifi");
    return success();
}

inline Status init() {
    State& s = st();
    if (s.started) return success();
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        nvs_flash_erase();
        err = nvs_flash_init();
    }
    if (err != ESP_OK) return failEsp(err, "nvs_flash_init");
    err = esp_netif_init();
    if (err != ESP_OK) return failEsp(err, "esp_netif_init");
    err = esp_event_loop_create_default();
    if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) return failEsp(err, "esp_event_loop_create_default");   // (invalid state: the program has made it)
    s.sta = esp_netif_create_default_wifi_sta();
    s.ap = esp_netif_create_default_wifi_ap();
    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    err = esp_wifi_init(&cfg);
    if (err != ESP_OK) return failEsp(err, "esp_wifi_init");
    esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, &onEvent, nullptr);
    esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, &onEvent, nullptr);
    err = esp_wifi_set_mode(WIFI_MODE_STA);
    if (err != ESP_OK) return failEsp(err, "esp_wifi_set_mode");
    err = esp_wifi_start();
    if (err != ESP_OK) return failEsp(err, "esp_wifi_start");
    s.mode = WIFI_MODE_STA;
    s.started = true;
    return success();
}

inline Status scanBegin() {
    Status ready = init();
    if (!ready.ok()) return ready;
    State& s = st();
    s.scanDone = false;
    wifi_scan_config_t cfg;
    std::memset(&cfg, 0, sizeof cfg);
    esp_err_t err = esp_wifi_scan_start(&cfg, false);
    if (err != ESP_OK) return failEsp(err, "scan");
    s.scanning = true;
    return success();
}

inline int authOf(wifi_auth_mode_t m) {
    switch (m) {
        case WIFI_AUTH_OPEN: return 0;
        case WIFI_AUTH_WEP: return 1;
        case WIFI_AUTH_WPA_PSK: return 2;
        case WIFI_AUTH_WPA2_PSK: case WIFI_AUTH_WPA_WPA2_PSK: return 3;
        case WIFI_AUTH_WPA3_PSK: case WIFI_AUTH_WPA2_WPA3_PSK: return 4;
        default: return 5;
    }
}

inline Status scanStep(bool& done, std::vector<Network>& found) {
    State& s = st();
    done = false;
    if (!s.scanning) return fail(E_InvalidArgument, "No scan is going on.");
    if (!s.scanDone) return success();
    s.scanning = false;
    uint16_t count = FIRE_WIFI_MAX_SCAN;
    std::vector<wifi_ap_record_t> records(count);
    esp_err_t err = esp_wifi_scan_get_ap_records(&count, records.data());
    if (err != ESP_OK) return failEsp(err, "scan results");
    found.clear();
    for (uint16_t i = 0; i < count; i++) {
        Network n;
        n.ssid = text(records[i].ssid, sizeof records[i].ssid);
        n.bssid = macText(records[i].bssid);
        n.rssi = records[i].rssi;
        n.channel = records[i].primary;
        n.auth = authOf(records[i].authmode);
        found.push_back(n);
    }
    done = true;
    return success();
}

inline Status connectBegin(const std::string& ssid, const std::string& password) {
    Status ready = init();
    if (!ready.ok()) return ready;
    State& s = st();
    if (ssid.empty() || ssid.size() > 32) return fail(E_InvalidArgument, "The network name must be 1 to 32 characters.");
    if (password.size() > 63 || (!password.empty() && password.size() < 8)) return fail(E_InvalidArgument, "The password must be 8 to 63 characters (or empty for an open network).");
    wifi_config_t cfg;
    std::memset(&cfg, 0, sizeof cfg);
    std::memcpy(cfg.sta.ssid, ssid.data(), ssid.size());
    std::memcpy(cfg.sta.password, password.data(), password.size());
    cfg.sta.threshold.authmode = password.empty() ? WIFI_AUTH_OPEN : WIFI_AUTH_WPA_PSK;
    cfg.sta.pmf_cfg.capable = true;
    cfg.sta.pmf_cfg.required = false;
    if (s.state == 1 || s.state == 2) { s.dropExpected = true; esp_wifi_disconnect(); }
    esp_err_t err = esp_wifi_set_config(WIFI_IF_STA, &cfg);
    if (err != ESP_OK) return failEsp(err, "set the station");
    s.retries = 0;
    s.failCode = 0;
    s.state = 1;
    err = esp_wifi_connect();
    if (err != ESP_OK) { s.state = 0; return failEsp(err, "connect"); }
    return success();
}

inline Status connectState(int& state, int& code, std::string& message) {
    State& s = st();
    state = s.state;
    code = s.failCode;
    message = s.failMessage;
    return success();
}

inline Status disconnect() {
    State& s = st();
    if (!s.started) return success();
    s.dropExpected = s.state != 0;
    s.state = 0;
    esp_err_t err = esp_wifi_disconnect();
    if (err != ESP_OK && err != ESP_ERR_WIFI_NOT_CONNECT) return failEsp(err, "disconnect");
    return success();
}

inline Status stationInfo(std::string& ssid, std::string& ip, std::string& mac, int& rssi) {
    Status ready = init();
    if (!ready.ok()) return ready;
    State& s = st();
    uint8_t m[6] = {0};
    esp_read_mac(m, ESP_MAC_WIFI_STA);
    mac = macText(m);
    ssid.clear();
    ip.clear();
    rssi = 0;
    wifi_ap_record_t rec;
    if (esp_wifi_sta_get_ap_info(&rec) != ESP_OK) return fail(E_NotConnected, "The station is not connected.");
    ssid = text(rec.ssid, sizeof rec.ssid);
    rssi = rec.rssi;
    esp_netif_ip_info_t info;
    if (s.sta && esp_netif_get_ip_info(s.sta, &info) == ESP_OK) ip = ipText(info);
    return success();
}

inline Status apStart(const std::string& ssid, const std::string& password, int channel, int maxClients) {
    Status ready = init();
    if (!ready.ok()) return ready;
    State& s = st();
    if (ssid.empty() || ssid.size() > 32) return fail(E_InvalidArgument, "The network name must be 1 to 32 characters.");
    if (password.size() > 63 || (!password.empty() && password.size() < 8)) return fail(E_InvalidArgument, "The password must be 8 to 63 characters (or empty for an open network).");
    if (channel < 1 || channel > 13) return fail(E_InvalidArgument, "The channel must be 1 to 13.");
    if (maxClients < 1 || maxClients > 10) return fail(E_InvalidArgument, "The number of clients must be 1 to 10.");
    wifi_config_t cfg;
    std::memset(&cfg, 0, sizeof cfg);
    std::memcpy(cfg.ap.ssid, ssid.data(), ssid.size());
    cfg.ap.ssid_len = (uint8_t)ssid.size();
    std::memcpy(cfg.ap.password, password.data(), password.size());
    cfg.ap.channel = (uint8_t)channel;
    cfg.ap.max_connection = (uint8_t)maxClients;
    cfg.ap.authmode = password.empty() ? WIFI_AUTH_OPEN : WIFI_AUTH_WPA2_PSK;
    esp_err_t err = esp_wifi_set_mode(WIFI_MODE_APSTA);
    if (err != ESP_OK) return failEsp(err, "set the mode");
    err = esp_wifi_set_config(WIFI_IF_AP, &cfg);
    if (err != ESP_OK) return failEsp(err, "set the access point");
    s.mode = WIFI_MODE_APSTA;
    return success();
}

inline Status apStop() {
    State& s = st();
    if (!s.started || s.mode != WIFI_MODE_APSTA) return success();
    esp_err_t err = esp_wifi_set_mode(WIFI_MODE_STA);
    if (err != ESP_OK) return failEsp(err, "set the mode");
    s.mode = WIFI_MODE_STA;
    return success();
}

inline Status apInfo(std::string& ip, std::string& mac, int& clients) {
    Status ready = init();
    if (!ready.ok()) return ready;
    State& s = st();
    uint8_t m[6] = {0};
    esp_read_mac(m, ESP_MAC_WIFI_SOFTAP);
    mac = macText(m);
    ip.clear();
    clients = 0;
    esp_netif_ip_info_t info;
    if (s.ap && esp_netif_get_ip_info(s.ap, &info) == ESP_OK) ip = ipText(info);
    wifi_sta_list_t list;
    if (s.mode == WIFI_MODE_APSTA && esp_wifi_ap_get_sta_list(&list) == ESP_OK) clients = list.num;
    return success();
}

}  // namespace wifi
}  // namespace plat
}  // namespace fire
