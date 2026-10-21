# Admission policy and operational gates

`tools/tld-admit run --profile agent|test|game|vr --name NAME -- COMMAND...`
selects a resource budget explicitly. Names, argv, and executable names do not
infer VR. Use `vr` for OpenXR, WiVRn/encoding, or an uncertain VR workload; `game`
is for ordinary non-VR game work. No game or VR workload was run for this change.

| Profile | MemoryHigh | MemoryMax | MemorySwapMax | Minimum MemAvailable at launch | Minimum swap free | TasksMax | CPUQuota |
|---|---:|---:|---:|---:|---:|---:|---:|
| agent | 1536 MiB | 2 GiB | 1 GiB | 9 GiB | 5 GiB | 256 | 200% |
| test | 1536 MiB | 2 GiB | 1 GiB | 9 GiB | 5 GiB | 256 | 200% |
| game | 8 GiB | 10 GiB | 2 GiB | 12 GiB | 6 GiB | 512 | 300% |
| vr | 10 GiB | 12 GiB | 4 GiB | 26 GiB | 8 GiB | 768 | 300% |

All profiles use OOMPolicy=kill / memory.oom.group=1 and KillMode=control-group.
Agent/test keep Nice=10 and CPUWeight/IOWeight=50; game/VR use Nice=0 and weights
100. The supervisor verifies exact systemd readback and kernel RAM, swap, task,
and OOM-group controls before exec. Every profile has a finite MemoryHigh.
Admission still permits only one job tree per user, including across fixture
runtime directories and during unit teardown. Lock lifetime, unit barriers,
privacy filtering, exit handling and descendant cleanup remain in place.

The old 6 GiB game hard limit had little room for a surge. The new 8 GiB high
limit starts reclaim/throttling before a 10 GiB ceiling. This is an allowance,
not a measurement of the game's requirement.

**The game's entry reserve is 12 GiB, measured.** One full session, 329 samples:
PEAK 4.72 GiB total (3.83 GiB anon, 0.86 GiB file cache), ZERO swap. 12 GiB
leaves +7.28 GiB over the measured peak and still +2.00 GiB even at the unit's
hard 10 GiB ceiling. The 18 GiB reserve it replaced was a guess, and it was
what forced the coordinator to pause thirteen workers to launch one game. The
numbers are in `smoke-live-1/MEMORY-PROFILE.md` and `smoke-live-1/memsample-2.txt`.
The 24 GiB RAM / 30.6 GiB swap
session incident must not become a per-game entitlement. A recurrence inside
this tree can terminate the job at its cap, rather than grant it all host RAM.
VR has a separate, still bounded allowance and a much larger entry reserve.

Launch headroom budgets the **entire RAM cap plus the entire swap cap**, then
leaves 6 GiB for other activity (10 GiB for VR). This treats zram as potentially
uncompressible; 64 GiB logical swap is not 64 GiB extra RAM. Swap headroom also
leaves 4 GiB beyond the job's swap cap. Exact entry equality is accepted. All
ordinary pressure gates still apply. Profile capacity rejection is immediate,
does not latch a global cooldown, and is repeated inside the supervisor just
before exec. Idle `status` reports general pressure health, not permission to
launch every profile. A VR capacity denial at the current roughly 18–19 GiB
available is intentional.

## Pressure and recovery

Read every required gauge afresh. MemAvailable and global swap come from
`/proc/meminfo`; some/full PSI from `/proc/pressure/memory` **and** the watched
session's `memory.pressure`; anon/file/shmem from session `memory.stat`; session
swap from `memory.swap.current`; OOM kills from session `memory.events` and
`/proc/vmstat`. Missing, truncated, NUL-containing, malformed, duplicated required
counters, inconsistent or out-of-range values reject admission. No fallback to
`memory.stat` swap or cached readings is allowed. Unknown readings latch a hold
and reset recovery. Zero available memory and zero configured swap are known
pressure states.

The entry gates are:

- Immediately latch at MemAvailable **below 6 GiB**, or swap free **below the
  larger of 2 GiB and 5% of total swap** (integer bytes).
- Also latch when available memory is below 10 GiB, swap free below 8 GiB,
  session swap is nonzero, and session anon is at least file minus shmem.
  This compound working-set signal uses file-minus-shmem as a cache proxy;
  neither raw anon nor raw session/global swap used alone pauses admission.
- Use the worse global/session avg10 independently for some and full PSI.
  Some **>=10%** or full **>=2%** holds admission immediately while debouncing.
  Five observed monotonic seconds with gaps no larger than three seconds latch
  the pause. Repeated CLI polls cannot replace elapsed time. A short spike
  clears without a long cooldown. Severe some **>=25%** or full **>=10%** latches
  immediately. Timer resolution is one second, as in the existing cooldown.
- Any observed increase in either OOM-kill counter latches immediately; a reset
  also invalidates recovery. A counter first seen establishes its baseline;
  historical totals cannot establish when an earlier OOM happened. Counts are
  recorded once even under a manual hold. An unchanged counter does not restart
  recovery. Watched/global OOM evidence never by itself labels a job OOM-killed.

Recovery requires **60 continuously sampled seconds** with available memory
**>=8 GiB**, swap free **>=max(4 GiB, 10% of total swap)**, some **<5%**, full
**<1%**, and no compound working-set gate. New crossings, unknown data,
hysteresis reentry, backwards time or gaps over three seconds break the interval.
The limited transient watch runs during pending PSI as well as a latched hold,
so debounce/recovery do not depend on CLI polling. Manual hold always wins and
requires explicit `resume`; automatic hold is still respected afterwards.

Thresholds are conservative operational choices, not kernel guarantees. PSI
explains stalls, not future demand; avg10 lags bursts. The meaning of some/full
is described in the [kernel PSI documentation](https://cdn.kernel.org/doc/html/latest/accounting/psi.html).

## Remaining operational gates

The broker limits descendants it actually owns. Sending IPC to an existing Steam
client or using an already running WiVRn service can perform work outside that
cgroup. Before any future VR acceptance, establish containment of the complete
pipeline and independently observe its peaks and pressure. Do not move or stop
existing processes/services to accomplish this as part of this change. GPU/driver
allocations, unrelated processes, scheduler delay, and zram metadata can still
consume the reserve. Limits can cause a job-local OOM or performance loss.

No increase beyond these caps is justified by this incident alone. Further
workload testing needs a separate controlled gate; ordinary game success does
not establish VR safety. The emergency Hyprland config remains uninstalled.
See [the disk-swap procedure](BTRFS-SWAP-PROCEDURE.md); it is additional recovery
headroom, not permission to raise limits. `tldrevamp.com` remains postponed.
