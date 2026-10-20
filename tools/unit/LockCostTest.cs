using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        private static void TestLockCost()
        {
            void Check(bool ok, string why) => IdleTrafficTest.Check(ok, "lock cost: " + why);
            var records = new List<TestBolt> { new TestBolt(1, 2), new TestBolt(2, 3), new TestBolt(0, 4) };
            int visits = 0;
            var index = new FarBoltIndex<TestBolt>(a => { visits++; return a.Parent; });
            index.Prepare(records, 1); index.Prepare(records, 1);
            Check(visits == 3 && index.TryGetValue(2, out var kids) && kids[0].Id == 3, "all flushes in a frame share one index, including nested parts");
            records.Add(new TestBolt(3, 5)); index.Prepare(records, 1);
            Check(visits == 4 && index.TryGetValue(3, out kids) && kids[0].Id == 5, "same-frame AddFar append extends index once");
            records[0] = new TestBolt(6, 2); index.Invalidate(); index.Prepare(records, 1);
            Check(!index.TryGetValue(1, out _) && index.TryGetValue(6, out _), "same-count stored attach replacement invalidates old parent");
            records.RemoveAt(1); index.Invalidate(); index.Prepare(records, 1);
            Check(!index.TryGetValue(2, out _), "stored detach removes child from index");
            records[0] = new TestBolt(7, 2); index.Prepare(records, 2);
            Check(!index.TryGetValue(6, out _) && index.TryGetValue(7, out _), "new frame sees game-side changes");
            var replacement = new List<TestBolt> { new TestBolt(8, 9) }; index.Prepare(replacement, 2);
            Check(!index.TryGetValue(7, out _) && index.TryGetValue(8, out _), "new save list never reuses previous index");

            foreach (int n in new[] { 20, 100 })
            {
                ByNet.Clear();
                // Managed Unity surfaces only: this measures traversal/GC, not native calls or game frame time.
                for (uint id = 1; id <= 300; id++) ByNet[id] = new Ent { NetId = id };
                var car = ByNet[300]; car.Items.Add(car.Root);
                var mount = new TestMount { parentTosave = car.Root };
                mount.transform.parent = new Transform { parent = new Transform { parent = car.Root.transform } };
                car.Root.Add(new[] { mount });
                var cargo = new Ent[n];
                for (int i = 0; i < n; i++)
                {
                    cargo[i] = new Ent { NetId = (uint)(400 + i), Proxy = true, PoseLocked = true, LockFrame = 300, LockItem = 0, LockMount = 0, PoseLockMount = mount };
                    cargo[i].Root.P = new TestPickup { transform = cargo[i].Root.transform, physlock = mount };
                    ByNet.Add(cargo[i].NetId, cargo[i]);
                }
                Action before = () => {
                    foreach (var e in cargo)
                    {
                        LegacyLockedFrame(e, mount);
                        // Old EnsurePoseLock allocated the mount scan/filter even for an unchanged game lock.
                        Array.FindAll(car.Root.GetComponentsInChildren<TestMount>(true), m => m.parentTosave == car.Root);
                    }
                };
                Action after = () => {
                    foreach (var e in cargo)
                    {
                        LockedFrame(e, mount, e.Root.transform, out _, out _, out _, out _);
                        TickPoseLock(e);
                    }
                };
                MeasureLockCost("locked N=" + n + " entities=" + ByNet.Count, before, after);
            }
            var all = new List<TestBolt>();
            for (uint i = 1; i <= 10000; i++) all.Add(new TestBolt(i / 4 + 1, i));
            var bolts = new FarBoltIndex<TestBolt>(a => a.Parent); int frame = 0;
            var legacy = new Dictionary<uint, List<TestBolt>>();
            MeasureLockCost("bolt records=10000 flushes=64", () => {
                for (int i = 0; i < 64; i++)
                {
                    legacy.Clear();
                    foreach (var a in all)
                    {
                        if (!legacy.TryGetValue(a.Parent, out var list)) legacy[a.Parent] = list = new List<TestBolt>();
                        list.Add(a);
                    }
                }
            }, () => {
                frame++; for (int i = 0; i < 64; i++) bolts.Prepare(all, frame);
            });
        }

        private static void LegacyLockedFrame(Ent e, TestMount mount)
        {
            for (var a = mount.transform; a != null; a = a.parent)
                foreach (var c in ByNet.Values)
                {
                    if (c == e || c.Root == null || c.Root.transform != a || !StreamsLockFrame(c)) continue;
                    if (c.Items.IndexOf(mount.parentTosave) < 0) continue;
                    var mounts = Array.FindAll(mount.parentTosave.GetComponentsInChildren<TestMount>(true), m => m.parentTosave == mount.parentTosave);
                    if (Array.IndexOf(mounts, mount) >= 0) return;
                }
        }

        private static void MeasureLockCost(string label, Action before, Action after)
        {
            (double ms, long bytes) Measure(Action action)
            {
                for (int i = 0; i < 40; i++) action();
                var sw = new Stopwatch(); long start = GC.GetAllocatedBytesForCurrentThread(); sw.Start();
                for (int i = 0; i < 200; i++) action();
                sw.Stop(); return (sw.Elapsed.TotalMilliseconds / 200, (GC.GetAllocatedBytesForCurrentThread() - start) / 200);
            }
            var old = Measure(before); var current = Measure(after);
            Console.WriteLine($"Lock benchmark {label}: before {old.ms:F4} ms/frame {old.bytes} B/frame; after {current.ms:F4} ms/frame {current.bytes} B/frame (managed surfaces)");
            IdleTrafficTest.Check(current.bytes == 0, "warm cached lock/index frame allocates zero managed bytes");
        }

        private sealed class TestBolt
        {
            internal uint Parent, Id;
            internal TestBolt(uint parent, uint id) { Parent = parent; Id = id; }
        }
    }
}
