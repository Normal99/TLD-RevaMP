#define _GNU_SOURCE
#include "native_process.h"
#include <errno.h>
#include <dirent.h>
#include <fcntl.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

long long native_monotonic_ms(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (long long)t.tv_sec * 1000 + t.tv_nsec / 1000000;
}

/* The caller retains its unreaped child leader, so this group cannot be reused.
 * Signal only exact PIDs verified in that group, through pidfds (no PID reuse
 * race, no broad process-name matches or global process killing). */
void native_signal_owned_group(pid_t leader, int sig) {
    if (leader <= 0 || getpgid(leader) != leader) return;
    DIR *d = opendir("/proc");
    if (!d) { kill(leader, sig); return; }
    struct dirent *e;
    while ((e = readdir(d))) {
        char *end;
        long value = strtol(e->d_name, &end, 10);
        if (*end || value <= 0) continue;
        pid_t member = (pid_t)value;
        int pidfd = (int)syscall(SYS_pidfd_open, member, 0);
        if (pidfd < 0) continue;
        char path[64], stat[4096];
        snprintf(path, sizeof path, "/proc/%ld/stat", value);
        int fd = open(path, O_RDONLY | O_CLOEXEC);
        ssize_t got = fd < 0 ? -1 : read(fd, stat, sizeof stat - 1);
        if (fd >= 0) close(fd);
        if (got > 0) {
            stat[got] = 0;
            char *p = strrchr(stat, ')'), state;
            long parent, group;
            if (p && sscanf(p + 1, " %c %ld %ld", &state, &parent, &group) == 3 && group == leader)
                syscall(SYS_pidfd_send_signal, pidfd, sig, NULL, 0);
        }
        close(pidfd);
    }
    closedir(d);
}

int native_capture(const char *const argv[], char *out, size_t n, int timeout_ms) {
    if (!out || !n || timeout_ms <= 0) return -1;
    out[0] = 0;
    int fds[2];
    if (pipe2(fds, O_CLOEXEC)) return -1;
    pid_t pid = fork();
    if (pid < 0) { close(fds[0]); close(fds[1]); return -1; }
    if (!pid) {
        if (setpgid(0, 0)) _exit(127);
        close(fds[0]);
        if (dup2(fds[1], STDOUT_FILENO) < 0) _exit(127);
        close(fds[1]);
        int nullfd = open("/dev/null", O_RDWR);
        if (nullfd < 0 || dup2(nullfd, STDIN_FILENO) < 0 || dup2(nullfd, STDERR_FILENO) < 0) _exit(127);
        if (nullfd > STDERR_FILENO) close(nullfd);
        execvp(argv[0], (char *const *)argv);
        _exit(127);
    }
    /* Reserve the leader PID until cleanup; WNOWAIT prevents PID/group reuse. */
    setpgid(pid, pid);
    close(fds[1]);
    fcntl(fds[0], F_SETFL, O_NONBLOCK);
    size_t got = 0;
    int done = 0, eof = 0, bad = 0;
    long long deadline = native_monotonic_ms() + timeout_ms;
    while (native_monotonic_ms() < deadline) {
        char chunk[4096];
        ssize_t r = eof ? -1 : read(fds[0], chunk, sizeof chunk);
        if (r > 0) {
            if ((size_t)r >= n - got) { bad = 1; break; }
            memcpy(out + got, chunk, (size_t)r); got += (size_t)r;
        } else if (!r) eof = 1;
        else if (!eof && errno != EAGAIN && errno != EINTR) { bad = 1; break; }
        siginfo_t si = {0};
        if (waitid(P_PID, (id_t)pid, &si, WEXITED | WNOHANG | WNOWAIT) < 0 && errno != EINTR) { bad = 1; break; }
        if (si.si_pid == pid) done = 1;
        if (done && eof) break;
        usleep(1000);
    }
    close(fds[0]);
    /* This group was created solely for this helper. Also clean up descendants
     * that closed stdout before the leader exited; never signal an outside job. */
    native_signal_owned_group(pid, SIGKILL);
    int status = 0;
    pid_t reaped;
    do { reaped = waitpid(pid, &status, 0); } while (reaped < 0 && errno == EINTR);
    if (bad || !done || !eof || reaped != pid || !WIFEXITED(status) || WEXITSTATUS(status)) {
        out[0] = 0; return -1;
    }
    out[got] = 0;
    return 0;
}
