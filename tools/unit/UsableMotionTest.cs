using System;
using System.Collections.Generic;
using TLDRevamp.Net;

static class UsableMotionTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static double Read(UsableMotion<double> motion, double time)
    {
        Check(motion.Sample(time, out var a, out var b, out var t), "sample exists");
        return a + (b - a) * t;
    }
    static int Main()
    {
        var m = new UsableMotion<double>();
        Check(!m.Sample(0, out _, out _, out _), "empty stream");
        m.Add(10, 0); m.Add(11, 90); m.Add(10.5, 999); m.Add(11, 999);
        Check(Read(m, 9) == 0, "hold before first sample");
        Check(Math.Abs(Read(m, 10.5) - 45) < 1e-5, "interpolate actual timestamp span");
        Check(m.Latest == 11 && Read(m, 12) == 90, "ignore duplicate/old packets and settle exactly");
        Check(Read(m, 100) == 90, "stationary endpoint needs no packets");

        foreach (double latency in new[] { .005, .18, .6 })
        {
            m = new UsableMotion<double>();
            var arrivals = new List<(double At, double Stamp)>();
            for (int i = 0; i <= 180; i++)
            {
                if (i % 11 == 5) continue;
                double stamp = i / 60.0;
                arrivals.Add((stamp + latency + (i % 5) * .004, stamp));
            }
            arrivals.Sort((a, b) => a.At.CompareTo(b.At));
            int next = 0, smooth = 0; double before = 0;
            for (int frame = 0; frame < 480; frame++)
            {
                double now = frame / 120.0;
                while (next < arrivals.Count && arrivals[next].At <= now)
                {
                    var p = arrivals[next++]; m.Add(p.Stamp, p.Stamp * 30);
                }
                if (now < latency + .3 || now > latency + 2.8) continue;
                double render = now - latency - .1, value = Read(m, render);
                Check(Math.Abs(value - render * 30) < 1e-4, "jitter/loss stays on the sender's linear motion");
                if (value > before) smooth++;
                before = value;
            }
            Check(smooth > 250, "render progresses between network packets even at high latency");
            Check(Read(m, 10) == 90, "last packet reaches exact end after buffering");
        }

        m = new UsableMotion<double>();
        for (int i = 0; i < 1000; i++) m.Add(i / 60.0, i);
        Check(Math.Abs(Read(m, .5 / 60) - .5) < 1e-5, "queued motion is retained until rendered");
        Check(Read(m, 100) == 999, "long queue drains to latest");
        Console.WriteLine("ALL PASS (" + checks + " usable motion checks)");
        return 0;
    }
}
