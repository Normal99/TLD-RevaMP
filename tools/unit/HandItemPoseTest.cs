using System;
using TLDRevamp.Net;
using UnityEngine;

static class HandItemPoseTest
{
    internal static void Run(Action<bool, string> check)
    {
        var hand = new Transform { position = new Vector3(17, 3, -22), rotation = Quaternion.Euler(25, 91, -37) };
        var lp = new Vector3(.173f, -.287f, 1.36f); var lq = Quaternion.Euler(-43, 12, 175);
        HandItemPose.World(hand, lp, lq, out var world, out var facing);
        HandItemPose.Relative(hand, world, facing, out var back, out var backQ);
        check((lp - back).magnitude < .00001f && Quaternion.Angle(lq, backQ) < .04f, "hand offset in/out with tilted translated target");
        for (int frame = 0; frame < 300; frame++)
        {
            hand.position = new Vector3(frame * .2f, 1 + frame * .001f, -frame * .3f);
            hand.rotation = Quaternion.Euler(frame * .1f, frame * 3, 23);
            HandItemPose.World(hand, lp, lq, out world, out facing);
            check((world - hand.TransformPoint(lp)).magnitude < .00001f && Quaternion.Angle(facing, hand.rotation * lq) < .04f,
                "newest unchanged grip follows moving final hand without another state");
        }
        var glide = new HandItemGlide();
        glide.Seed(new Vector3d(1000000, 2, 3), Quaternion.Euler(12, 25, 7));
        object first = new object(), second = new object();
        double time = 10;
        foreach (var source in new[] { first, null, second, null, first })
        {
            var previous = glide.Shown; var previousQ = glide.Rotation;
            var destination = previous + new Vector3d(5, -1, 3); var q = Quaternion.Euler(37, -82, 45);
            glide.Apply(source, time, destination, q, out var start, out var startQ);
            check((start - previous).magnitude < 1e-8 && Quaternion.Angle(startQ, previousQ) < .04f,
                "pickup/drop/handover/model loss/reacquire begins exactly at last draw");
            var moving = destination + new Vector3d(2, 0, 0);
            glide.Apply(source, time + .06, moving, q, out var middle, out _);
            var expected = moving + (previous - destination) * .5;
            check((middle - expected).magnitude < .00001, "switch glide follows moving destination while removing half its error");
            glide.Apply(source, time + .121, moving, q, out var end, out var endQ);
            check((end - moving).magnitude < 1e-8 && Quaternion.Angle(endQ, q) < .04f && !glide.Blending(time + .121),
                "switch ends on normal state/physics or hand pose after 0.12 seconds");
            glide.Apply(source, time + .15, moving + new Vector3d(1, 0, 0), q, out var next, out _);
            check((next - moving - new Vector3d(1, 0, 0)).magnitude < 1e-8, "same source does not restart glide each frame");
            time++;
        }
        glide.Reset();
        check(!glide.HasShown && glide.Source == null && !glide.Blending(time), "local takeover clears remote drawing history");
        glide.Seed(new Vector3d(1, 2, 3), Quaternion.identity);
        glide.Apply(first, time, new Vector3d(8, 9, 10), Quaternion.Euler(0, 90, 0), out _, out _);
        glide.Apply(first, time + .03, new Vector3d(8, 9, 10), Quaternion.Euler(0, 90, 0), out var interrupted, out var interruptedQ);
        glide.Apply(second, time + .03, new Vector3d(-8, -9, -10), Quaternion.Euler(30, -90, 20), out var restart, out var restartQ);
        check((restart - interrupted).magnitude < 1e-8 && Quaternion.Angle(restartQ, interruptedQ) < .04f,
            "handover during a pickup glide starts at the interrupted draw without a jump");
    }
}
