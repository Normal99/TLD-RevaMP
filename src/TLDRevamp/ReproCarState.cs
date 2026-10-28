using System;

namespace TLDRevamp
{
    /// What the car did with the driver's input, packed into the driving rows' spare columns (f, g, h of repro format 1):
    /// f = the flags below, g = the average engine rpm, h = the car's speed (the game's own unit). Replay ignores all three.
    /// With only the input, a car that "doesn't move" cannot be told apart from a driver holding the clutch.
    internal static class ReproCarState
    {
        internal struct State
        {
            public bool EngineOn, Kinematic, Sleeping, Driving, HandbrakeOn, MotorTorque, BrakeTorque;
            public bool WheelsLocked, Frozen;               // the game's wheel lock (box colliders instead of wheels); body constraints other than none
            public int GroundedDriven, DrivenWheels, Gear;   // Gear: the car's own number, -1 reverse, 0 neutral
            public int ClutchPercent, HandbrakePercent;      // what the car applies (0 = released), not what the driver pressed
            public double Rpm, Speed;
        }

        // f, low to high: 0 engine running, 1 body kinematic, 2 body asleep, 3 car.driving, 4 handbrake latched,
        // 5-8 driven wheels on the ground, 9-12 driven wheels, 13-16 gear + 1, 17-23 clutch %, 24-30 handbrake %,
        // 31 motor torque on a driven wheel, 32 brake torque on a driven wheel, 33 wheels locked (carscript.whlocked), 34 body constraints set.
        // At most 2^35: exact in a double.
        internal static double Pack(State s)
        {
            long f = (s.EngineOn ? 1L : 0) | (s.Kinematic ? 2L : 0) | (s.Sleeping ? 4L : 0) | (s.Driving ? 8L : 0) | (s.HandbrakeOn ? 16L : 0);
            f |= (long)Clamp(s.GroundedDriven, 0, 15) << 5;
            f |= (long)Clamp(s.DrivenWheels, 0, 15) << 9;
            f |= (long)Clamp(s.Gear + 1, 0, 15) << 13;
            f |= (long)Clamp(s.ClutchPercent, 0, 100) << 17;
            f |= (long)Clamp(s.HandbrakePercent, 0, 100) << 24;
            f |= (s.MotorTorque ? 1L : 0) << 31;
            f |= (s.BrakeTorque ? 1L : 0) << 32;
            f |= (s.WheelsLocked ? 1L : 0) << 33;
            f |= (s.Frozen ? 1L : 0) << 34;
            return f;
        }

        internal static State Unpack(double packed, double rpm, double speed)
        {
            long f = (long)packed;
            return new State
            {
                EngineOn = (f & 1) != 0, Kinematic = (f & 2) != 0, Sleeping = (f & 4) != 0, Driving = (f & 8) != 0, HandbrakeOn = (f & 16) != 0,
                GroundedDriven = (int)(f >> 5 & 15), DrivenWheels = (int)(f >> 9 & 15), Gear = (int)(f >> 13 & 15) - 1,
                ClutchPercent = (int)(f >> 17 & 127), HandbrakePercent = (int)(f >> 24 & 127),
                MotorTorque = (f >> 31 & 1) != 0, BrakeTorque = (f >> 32 & 1) != 0, WheelsLocked = (f >> 33 & 1) != 0, Frozen = (f >> 34 & 1) != 0, Rpm = rpm, Speed = speed,
            };
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        internal static int Percent(float v) => Clamp((int)Math.Round(v * 100f), 0, 100);
    }
}
