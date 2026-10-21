/* Bounded capture of an owned helper; stderr and failed output stay private. */
#ifndef NATIVE_PROCESS_H
#define NATIVE_PROCESS_H
#include <stddef.h>
#include <sys/types.h>
long long native_monotonic_ms(void);
int native_capture(const char *const argv[], char *out, size_t n, int timeout_ms);
void native_signal_owned_group(pid_t leader, int sig);
#endif
