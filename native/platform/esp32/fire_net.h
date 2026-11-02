/* fire native platform package "esp32": the network in C for the net bridge (lwIP sockets of ESP-IDF; the connection itself - WiFi or Ethernet - is brought up by
 * the program or by the wifi package). */
#ifndef FIRE_PLATFORM_NET_H
#define FIRE_PLATFORM_NET_H
#include <lwip/sockets.h>
#include <lwip/netdb.h>
#define FIRE_NET_LWIP 1
#include "../std/fire_net_sockets.h"
#endif
