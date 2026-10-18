# Synthetic telemetry

All data is invented. Session `a1b2c3d4e5f60718` has a host and two clients. Each machine has a play log and two
black boxes containing 2,101 frame samples over 35 seconds. Black-box columns use raw profiler marker names,
integer event flags, generation backlog totals and per-frame bytes. Play logs also contain structured backlog
details and the current nested `net.conns` format. File-relative `t` deliberately differs across machines.

- Around session time 100, client 1 has exactly three frames over 50 ms (at 99, 99.5 and 100 seconds). Each has
  GC, streaming and 40 terrain tiles waiting. Host and client 2 have no slow frames. Client 1 also has a different
  quality setting and less VRAM. Client 2 has 300 ms ping and 8% loss estimated from Steam quality.
- Around time 200, every machine has smooth frames. Client 2 has 450 ms ping and 12% estimated loss, compared with
  18 ms and 23 ms ping and no loss for the other machines. Per-frame and cumulative network byte counters differ.

Snapshot moment stamps are 100/100.4/101.2 and 200/200.4/201.2, exercising the two-second grouping tolerance.
Marks at 100 and 200 identify the same moments in the play logs. Report zip wrappers, duplicate inputs, old logs,
missing fields and truncation are generated in temporary directories by `tools/test_telemetry_compare.py`.
