using UnityEngine;

namespace TLDRevamp.Multiplayer
{
    /// A kind of object a mod shares: how to build it from a record, how to write its record, how edits from other
    /// players are applied. One instance per type, registered once (MpApi.RegisterType). Every method runs on Unity's
    /// main thread.
    ///
    /// The rules (docs/MODDING-MULTIPLAYER.md):
    ///  - One machine owns a shared object and runs its logic and physics. Copies on the other machines only show it.
    ///  - The record is the whole state needed to build the object anywhere. The same record must give the same
    ///    object on every machine: random values (loot, wear) are chosen once by the owner and written.
    ///  - Other players change an object by asking its owner (SharedObject.RequestEdit); the owner decides in OnEdit.
    public abstract class SharedType
    {
        /// Unique within the mod, e.g. "headunit". The network key is "modid/name".
        public abstract string Name { get; }

        /// Bump when the record layout changes. Every player must have the same version (checked at join, with the mod).
        public virtual byte Version => 1;

        /// Records larger than this are refused (on the sender and on the host).
        public virtual int MaxRecordBytes => 8192;
        /// Edits and events about this type larger than this are refused.
        public virtual int MaxEditBytes => 1024;

        /// The owner sends the object's position and rotation while it moves, and copies follow smoothly. Off for things
        /// that never move or ride something else (a part in a car's slot moves with the car).
        public virtual bool SyncPose => false;
        /// How often the owner sends the pose while it moves (at most 20).
        public virtual float PoseHz => 10f;
        /// Copies' rigidbodies are made kinematic while they are copies, so only the owner's physics moves the object.
        public virtual bool KinematicCopies => true;

        /// Other players may take ownership (SharedObject.RequestOwnership), e.g. by picking the object up.
        public virtual bool AllowTakeOver => true;

        /// State records (API 2): small state that changes often (a gauge, a light, a needle) and that only players near
        /// need. The owner calls WriteState up to StateHz times a second and sends it when the bytes changed; the host
        /// passes it only to players within StateRange of the object (0 = every player), and a player who comes within
        /// range gets the latest. Copies apply it in ReadState. The full record (Write/Read) stays for what everyone
        /// must know and for building the object.
        public virtual bool HasState => false;
        public virtual float StateHz => 5f;
        public virtual float StateRange => 500f;
        public virtual int MaxStateBytes => 256;
        public virtual void WriteState(GameObject obj, RecordWriter state) { }
        public virtual void ReadState(GameObject obj, RecordReader state) { }

        /// The far store (API 2): 0 = the object exists on every player's machine all the time. Above 0, a player's
        /// machine has it only while the player is within ViewRange metres of it (a little more before it goes, so it
        /// doesn't flicker at the edge); farther away the host keeps its record and the copy is removed (OnStored), and
        /// it is built again from the latest record when the player comes back (Create, then OnPlaced). An owner who
        /// goes beyond the range hands the object to the nearest player within it, or to the host when nobody is near;
        /// the first player who comes near an object nobody has loaded owns it.
        public virtual float ViewRange => 0f;

        /// Set when registered.
        public string Key { get; internal set; }
        public ModInfo Mod { get; internal set; }

        /// Build the object on this machine from its record. Called for copies, for players who join later, and when a
        /// player comes back to an object they had not seen yet. Position and rotation are in this machine's Unity space.
        /// Return null if the record can't be used (the object then stays missing here, and the log says why).
        public abstract GameObject Create(RecordReader record, Vector3 position, Quaternion rotation);

        /// Write the object's whole state.
        public abstract void Write(GameObject obj, RecordWriter record);

        /// Apply a newer record to an existing copy (after an edit, or when the owner called RecordChanged).
        public abstract void Read(GameObject obj, RecordReader record);

        /// Owner only: another player (or this one) asked for a change. Check it, apply it, return true. The session then
        /// sends the new record to everyone. Return false to refuse it. fromPlayer: the player id (0 = the host).
        public virtual bool OnEdit(SharedObject obj, RecordReader edit, int fromPlayer) => false;

        /// This machine became the owner (mine = true: start the logic and physics) or stopped being it (stop them).
        public virtual void OnOwnerChanged(SharedObject obj, bool mine) { }

        /// The object was removed by its owner. The default destroys the GameObject on this machine.
        public virtual void OnRemoved(SharedObject obj)
        {
            if (obj.GameObject != null) Object.Destroy(obj.GameObject);
        }

        /// A copy was built here because the player came near (ViewRange), after Create. Not called for the first copy
        /// a player gets when the object is shared or when they join.
        public virtual void OnPlaced(SharedObject obj) { }

        /// The player went beyond ViewRange: the copy here goes into the far store (the host keeps its record). The
        /// default destroys the GameObject. obj is finished after this: a new SharedObject comes when the player is
        /// near again.
        public virtual void OnStored(SharedObject obj)
        {
            if (obj.GameObject != null) Object.Destroy(obj.GameObject);
        }
    }
}
