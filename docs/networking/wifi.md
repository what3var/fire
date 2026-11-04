# WiFi (`#import "wifi"`)


The radio of a board: scan for networks, join one as a **station**, be an **access point**. It needs `time`. The natives are `packages/wifi/native/fire_bridge_wifi.h` (C) over `fire_wifi_*` (`native/platform/std/fire_wifi_*.h`, the interface in `fire_wifi_c.h`): the **WiFi driver of ESP-IDF** (`esp_wifi`, `esp_event`,
`esp_netif`, `nvs_flash`) on an ESP32. Where the operating system owns the network (Windows, Linux, macOS) there is no radio for a program to control: the list of interfaces is empty, and the package reports "not supported" (`WiFi.Board.Available()` is false). Every platform has the
**simulated radio** `"sim"`. Once the station has an address the lwIP sockets of the `net` package (and `http`, `tls`) work - **the network packages do not know about WiFi**; a program joins the network first and then opens its sockets. Ethernet on the ESP32 would follow the same pattern.

```
#import "wifi"

var wifi = new WiFi.Station()                               // the first real interface, or "sim" (a name works too: new WiFi.Station("wifi"))
foreach (n in wifi.Scan()) { print(n.ssid + " " + n.rssi + " dBm, channel " + n.channel + ", secure: " + n.Secure) }     // strongest first
wifi.Connect("home", ReadPasswordFromSomewhere(), 20s)       // waits until joined (default limit 20 s); an empty password joins an open network
print(wifi.Ip + " " + wifi.Ssid + " " + wifi.Rssi)
var page = new Http.Client().Get("http://example.org/").Text()   // the net package works now
wifi.Disconnect()

var ap = new WiFi.AccessPoint()
ap.Start("fire-board", "password123")                       // Start(ssid, password = "", channel = 1, maxClients = 4); no password: an open network
print(ap.Ip + " " + ap.Clients)                             // the stations get their addresses from the board (default 192.168.4.1/24)
ap.Stop()
```

* **Nothing waits inside the natives**: a scan or a join takes seconds on a real radio, so the fire code asks again and again (`ScanStep`, `ConnectState`) with a pause of 1 to 20 ms in between. The program stays abortable (`terminate`) and the other threads keep running. Limits are
  a `TimeSpan`, a time value (`500ms`, `5s`) or a number (milliseconds); an exceeded limit is `WiFi.TimeoutException`.
* `Station.Start(ssid, password)` joins without waiting; `State` (`WiFi.State.Idle/Connecting/Connected`) and `IsConnected` ask, `Poll()` calls `onState(state)` when the state has changed - for a program that does other things meanwhile. A failed join throws the exception of the failure
  from `State`/`Poll`.
* **Errors** are `WiFi.WiFiException` (with a `code`): `NotFoundException` (3: the network is not in range), `BusyException` (4: e.g. a scan while the station is joining), `UnsupportedException` (6), `AuthException` (8: the network refused the login - wrong password),
  `TimeoutException` (9) and `NotConnectedException` (10). Bad arguments (name or password length, channel) are code 1. Passwords are 8 to 63 characters (or empty). **Credentials are never part of a generated file**: pass them at run time (read them from a file or the program's settings).
* **ESP32 notes:** the first call starts the driver (NVS, `esp_netif`, the default event loop - if the program did that itself, it is not done twice) and the station and the access point share the one radio (the access point runs on the channel of the station when both are on). A join is tried
  up to 3 more times (`FIRE_WIFI_RETRIES`) when the radio fails for other reasons than a missing network or a wrong password. A scan lists up to 48 networks (`FIRE_WIFI_MAX_SCAN`). Add the components `esp_wifi`, `esp_event`, `esp_netif` and `nvs_flash` to the project.
  (Not tried on a board yet; the file compiles against a stand-in of the driver headers. The mapping of the driver's disconnect reasons to NotFound/Auth is from its documentation.)
* **The simulated radio** has the networks that `WiFi.Sim.AddNetwork(ssid, password = "", rssi = -60, channel = 1)` puts in the air; a scan finds them, a join works with the right password (`AuthException` otherwise, `NotFoundException` for an unknown name) and the station gets
  `192.168.1.50`. `WiFi.Sim.Delays(joinQuestions, scanQuestions)` sets how many questions an answer takes (default 2 and 1: the waiting code is really exercised), `Drop()` loses the connection, `RemoveNetwork(ssid)` takes a network away, `ClientJoins()`/`ClientLeaves()` let stations
  join the simulated access point (`192.168.4.1`), `Reset()` starts over. It is not a network: the sockets of the net package keep using the network of the PC.

## On the PC: the simulated radio

```fire
#import "wifi"

WiFi.Sim.AddNetwork("home", "secret123", -48, 6)
WiFi.Sim.AddNetwork("cafe", "", -70, 11)
var wifi = new WiFi.Station("sim")
foreach (n in wifi.Scan()) { print(n.ssid + " " + n.rssi + " dBm, channel " + n.channel + ", secure: " + n.Secure) }
wifi.Connect("home", "secret123", 5s)
print(wifi.Ip + " " + wifi.Ssid)                        // 192.168.1.50 home
wifi.Disconnect()
```
