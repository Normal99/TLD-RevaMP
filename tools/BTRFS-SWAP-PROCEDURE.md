# Review procedure: 24 GiB disk-backed overflow swap

This is a proposed administrator procedure, **not executed**. Host swap, zram,
fstab, and host services were not changed. The current backing filesystem is
btrfs on `/dev/nvme0n1p2`, shared by `/` and `/home`, with about 78.8 GiB free,
single data profile, DUP metadata. Detailed device verification requires root.

Choose **24 GiB**, leaving about **54.8 GiB** of today's free space. Require at
least **64 GiB free before creation**, so at least 40 GiB remains afterwards.
Do not choose a file equal to all free space. Fragmentation, metadata allocation,
quotas and other writes can make `df` optimistic; abort on any failed check. Do
not shrink zram or run swapoff on it. The file is an emergency overflow tier,
not a latency or VR-capacity fix. Poor zram compression can cause RAM exhaustion
before the lower-priority tier is reached.

Btrfs swap requires suitable unshared, fully allocated NOCOW/uncompressed
extents. Use a dedicated nested subvolume excluded from snapshot/backup jobs;
never snapshot/reflink the swapfile. Active swap pins block groups and can affect
balance/scrub, including unrelated data on this shared filesystem. Schedule
maintenance with this tier safely disabled. These restrictions and extent
verification are described by [Btrfs Swapfile](https://btrfs.readthedocs.io/en/latest/Swapfile.html).
The [mkswapfile command](https://btrfs.readthedocs.io/en/latest/btrfs-filesystem.html#subcommand)
creates and formats a preallocated NOCOW file; no generic `truncate` or copied
swap image is needed.

## Preflight (root, read-only)

Run at a quiet maintenance window with the workload closed voluntarily. Recheck:

```sh
findmnt -T /home -o TARGET,SOURCE,FSTYPE,OPTIONS
btrfs filesystem show /home
btrfs filesystem usage -b /home
btrfs balance status /home
btrfs scrub status /home
btrfs quota rescan -s /home
swapon --show --bytes --output NAME,TYPE,SIZE,USED,PRIO
free -b
cat /proc/pressure/memory
```

Stop here unless there is exactly one backing device, a single data profile,
no active exclusive operation/scrub, sufficient data **and metadata** space,
and no restrictive qgroup limit on the intended new subvolume. A disabled quota
report is acceptable; other unknown/error results require investigation. Verify
at least 64 GiB free, MemAvailable >=10 GiB, and low some/full PSI. Confirm the
existing `/dev/zram0` priority is **100** and that no other tier has a conflicting
priority. Review storage encryption: disk swap can persist process data; the
observed direct btrfs partition does not establish encryption. Storage/data
retention suitability is an administrator gate before creation.

## Create and verify (root, after preflight)

Run the following as a root shell. The exact path must not already exist, even
as a symlink. A dedicated subvolume means parent snapshots do not recursively
include its contents; explicitly exclude it from tools that recurse subvolumes.
No change to the global btrfs compression mount option is needed.

The path is a choice, so it is a variable: set `SWAP_DIR` to the directory that will hold the subvolume
before running any block below. Every later block uses the same `swap_dir` / `swap_file` pair, so the
commands refer to exactly what the first one created.

```sh
set -eu
umask 077
swap_dir=${SWAP_DIR:?set SWAP_DIR to the chosen path}
swap_file="$swap_dir/swapfile"
if test -e "$swap_dir" || test -L "$swap_dir"; then
    echo 'STOP: proposed path already exists' >&2
    exit 1
fi
free_bytes=$(df --output=avail -B1 /home | tail -n 1)
test "$free_bytes" -ge 68719476736
btrfs subvolume create "$swap_dir"
chmod 0700 "$swap_dir"
btrfs filesystem mkswapfile --size 24G "$swap_file"
chmod 0600 "$swap_file"
test "$(stat -c %u:%g:%a "$swap_file")" = 0:0:600
test "$(stat -c %s "$swap_file")" = 25769803776
lsattr "$swap_file"  # must contain C (NOCOW)
btrfs inspect-internal map-swapfile "$swap_file"  # mandatory success
stat -c 'bytes=%s allocated_512_blocks=%b owner=%U mode=%a' "$swap_file"
btrfs filesystem usage -b /home
df -B1 /home
```

Stop without activating if verification fails, including a permissions error.
Confirm at least 40 GiB free remains (42949672960 bytes), with metadata headroom.
`map-swapfile` is the mapping/holes/device gate; block counts alone do not prove
suitability. Do not use `filefrag` offsets as a replacement. A small, inactive
32 MiB local test confirmed creation, 0600, NOCOW and full allocation. Mapping
could not be verified without root, and `sudo -n` required a password. No
activation test was attempted; all temporary artifacts were removed.

## Activate only this file and inspect (root)

Priority **10** is below zram **100**; higher numeric priority is preferred per
[swapon(8)](https://man7.org/linux/man-pages/man8/swapon.8.html). Do not use
`swapon -a`, change swappiness, or cycle existing swap.

```sh
swapon --priority 10 "$swap_file"
swapon --show --bytes --output NAME,TYPE,SIZE,USED,PRIO
cat /proc/swaps
free -b
cat /proc/pressure/memory
```

Require both zram at 100 and the **exact** file at 10; file usable size is one
page smaller than 24 GiB. On error, inspect the kernel journal and stop; do not
force activation. Do not manufacture pressure to test disk use. Observe normal
use later under a separate workload gate. Keep cgroup swap caps and admission
reserves unchanged. Check filesystem capacity as other writes continue.

For optional persistence **after review and successful activation**, back up
fstab, add exactly one line below using an editor (never blind append), verify
with `findmnt --verify --verbose`, then `systemctl daemon-reload`. This is a
future administrator step. `/home` must be mounted before activating the file.
`noauto` avoids introducing automatic boot activation in this first rollout;
explicit activation is still required each boot.

```fstab
$swap_file none swap noauto,nofail,pri=10 0 0
```

## Rollback (root, exact path only)

First remove the exact fstab line if added, and reload systemd. Keep the active
file until a quiet window permits safe removal. Read this file's USED bytes,
MemAvailable, other swap free, and PSI. As a conservative gate require
**MemAvailable >= file USED + 10 GiB**, ample remaining zram free, and low PSI;
otherwise defer. Never force swapoff under pressure and never use `swapoff -a`.

```sh
swapoff "$swap_file"
swapon --show --bytes --output NAME,TYPE,SIZE,USED,PRIO
cat /proc/swaps
```

Proceed only after swapoff returns 0 and the exact path is absent from active
swap. If swapoff fails, leave the file and subvolume intact and retry in a later
quiet window. Verify the subvolume contains only the file created above and no
snapshots/extra files. Then remove exactly that file and that subvolume:

```sh
rm -- "$swap_file"
btrfs subvolume delete "$swap_dir"
btrfs filesystem usage -b /home
```

For a failed pre-activation creation, first prove the exact file is inactive,
then use the same exact cleanup. Do not remove or truncate an active swapfile,
reflink it, recursively delete a directory, or touch zram. Memory availability
can change between checks; rollback must stop if the reserve disappears.
