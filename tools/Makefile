# The native terminal overview of the TLD free-worker fleet.
#
#   make -C tools            build tools/overview
#   make -C tools test       build and run the self-tests
#   make -C tools smoke      build, self-test and the end-to-end smoke test
#   make -C tools clean
#
# One executable, C11, the C standard library only: no third-party dependency,
# no package to install, no daemon.

CC      ?= gcc
CFLAGS  ?= -std=c11 -O2 -Wall -Wextra -Wshadow -Wno-unused-parameter
LDFLAGS ?=

BIN      := overview
OBJS     := overview.o overview_core.o overview_collect.o overview_headset.o native_process.o
ADMIT_OBJS := tld-admit.o tld_admit_core.o overview_core.o native_process.o
HEADERS  := overview_core.h overview_collect.h overview_data.h overview_headset.h native_process.h

all: $(BIN) tld-admit

$(BIN): $(OBJS)
	$(CC) $(CFLAGS) -o $@ $(OBJS) $(LDFLAGS)

overview.o: overview.c $(HEADERS)
	$(CC) $(CFLAGS) -c $< -o $@

overview_core.o: overview_core.c overview_core.h
	$(CC) $(CFLAGS) -c $< -o $@

overview_collect.o: overview_collect.c $(HEADERS)
	$(CC) $(CFLAGS) -c $< -o $@

overview_headset.o: overview_headset.c $(HEADERS)
	$(CC) $(CFLAGS) -c $< -o $@

tld-admit: $(ADMIT_OBJS)
	$(CC) $(CFLAGS) -o $@ $(ADMIT_OBJS) $(LDFLAGS)

tld-admit.o: tld-admit.c tld_admit_core.h overview_core.h native_process.h
	$(CC) $(CFLAGS) -c $< -o $@

native_process.o: native_process.c native_process.h
	$(CC) $(CFLAGS) -c $< -o $@

tld_admit_core.o: tld_admit_core.c tld_admit_core.h overview_core.h
	$(CC) $(CFLAGS) -c $< -o $@

HEADSET_TEST := test_overview_headset
HEADSET_TIMING_TEST := test_overview_headset_timing

$(HEADSET_TEST): overview_headset_test.c overview_headset.o overview_core.o
	$(CC) $(CFLAGS) -o $@ overview_headset_test.c overview_headset.o overview_core.o $(LDFLAGS)

# The read-deadline cases open a loopback socket, so they are a separate binary:
# the self-test above stays hermetic and never opens one.
$(HEADSET_TIMING_TEST): overview_headset_timing_test.c overview_headset.o overview_core.o
	$(CC) $(CFLAGS) -o $@ overview_headset_timing_test.c overview_headset.o overview_core.o $(LDFLAGS) -lpthread

test_admit: tld-admit test_tld_admit
	./test_tld_admit
	python3 test_tld_admit.py
	python3 test_tld_admit_mutations.py

test_tld_admit: test_tld_admit.c tld_admit_core.o overview_core.o
	$(CC) $(CFLAGS) -o $@ test_tld_admit.c tld_admit_core.o overview_core.o $(LDFLAGS)

# Overview checks are read-only; test_admit separately exercises live units.
test: $(BIN) $(HEADSET_TEST) $(HEADSET_TIMING_TEST)
	./$(BIN) --self-test
	./$(HEADSET_TEST)
	./$(HEADSET_TIMING_TEST)

smoke: $(BIN)
	sh overview_smoke.sh

clean:
	rm -f $(BIN) $(OBJS) tld-admit $(ADMIT_OBJS) test_tld_admit $(HEADSET_TEST) $(HEADSET_TIMING_TEST)

.PHONY: all test test_admit smoke clean
