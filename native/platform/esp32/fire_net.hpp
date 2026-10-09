// fire native platform package "esp32": the network for the net bridge (lwIP sockets of ESP-IDF; the connection itself - WiFi or Ethernet - is brought up by the program or by
// the wifi package). (Not tried on a board yet.)
#pragma once
#include <lwip/sockets.h>
#include <lwip/netdb.h>
#define FIRE_NET_LWIP 1
#include "../std/fire_net_sockets.hpp"
