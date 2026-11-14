/* fire tls package: TLS part in C (the HAL of the tls bridge). The tls package brings, one file per platform (platformSources of tls.fireproj),
 * (the sockets are those of the net package - "uses": [ "net" ] - texts in UTF-8):
 *
 *   FireTlsOptions { int verify; const char *caFile, *caPem, *serverName, *certPem, *keyPem; }     ("" = not given)
 *   FireTlsSession, FireTlsContext                                    (opaque)
 *   int  fire_tls_supported(void)
 *   FireTlsSession* fire_tls_clientOpen(FireNetSock, const FireTlsOptions*, FireNetStatus*)   a client session over the socket (SNI, certificate and host name checked
 *                                                                      unless !verify); nothing is sent yet
 *   int  fire_tls_handshakeStep(FireTlsSession*, int* done, st)       does what can be done without waiting: *done when the handshake is complete (call again until then)
 *   FireTlsContext* fire_tls_serverContext(const FireTlsOptions* (certPem, keyPem), st) / void fire_tls_freeContext(FireTlsContext*)
 *   FireTlsSession* fire_tls_serverAccept(FireTlsContext*, FireNetSock, st)   a server session over the connection; the handshake runs with handshakeStep
 *   int  fire_tls_readSome(FireTlsSession*, uint8_t*, int count, int64_t timeoutMs, int* got, st)    got 0: the other side ended the connection
 *   int  fire_tls_writeSome(FireTlsSession*, const uint8_t*, int count, int64_t timeoutMs, int* sent, st)
 *   int  fire_tls_pending(FireTlsSession*)                            decrypted bytes that read without touching the socket
 *   void fire_tls_info(FireTlsSession*, char* protocol, size_t, char* cipher, size_t)
 *   void fire_tls_closeSession(FireTlsSession*)                       close_notify (best effort), then everything is freed (the socket stays open)
 * TLS over buffers instead of a socket (a protocol that carries the TLS records itself - the PRELOGIN packets of SQL Server's TDS):
 *   FireTlsSession* fire_tls_clientOpenMem(const FireTlsOptions*, st)  a client session without a socket (NULL with a status where it is not supported)
 *   int  fire_tls_memFeed(FireTlsSession*, const uint8_t*, int count, st)   the bytes that came from the other side
 *   int  fire_tls_memDrain(FireTlsSession*, uint8_t*, int max)         the bytes to send to it (at most max; 0: none waiting)
 *   int  fire_tls_memWaiting(FireTlsSession*)                          how many bytes wait to be sent
 *   int  fire_tls_memRead(FireTlsSession*, uint8_t*, int count, int* got, st)    decrypted bytes; *got -1: more bytes of the other side are needed, 0: it closed
 *   int  fire_tls_memWrite(FireTlsSession*, const uint8_t*, int count, int* sent, st)   encrypts (the records wait for memDrain)
 * The functions that return int give 1 when done and 0 on an error (the status says why); those that return a pointer give NULL on an error.
 *
 * The socket is non-blocking and belongs to the net bridge; the session only reads and writes it. Waiting is done with fire_net_waitFor, never longer than the
 * timeout. */
#ifndef FIRE_TLS_C_H
#define FIRE_TLS_C_H

#include "fire_net_c.h"

/* The codes of TLS (besides those of the network): a TLS error (handshake failed, protocol error), the certificate was not accepted. */
enum { FIRE_TLS_E_TLS = 14, FIRE_TLS_E_CERTIFICATE = 15 };

typedef struct FireTlsOptions { int verify; const char* caFile; const char* caPem; const char* serverName; const char* certPem; const char* keyPem; } FireTlsOptions;

#endif
